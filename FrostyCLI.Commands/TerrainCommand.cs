using System.Text;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bfii.TerrainBuild;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // `terrain` -- inspect and rewrite terrain heightfields.
    //
    // A level's terrain is three resources (VisualTerrain, TerrainStreamingTree,
    // TerrainLayerCombinations); this command works on the streaming tree, which is where the
    // elevations live. Every MP terrain in both SWBF 2015 and BFII carries its terrain fully inline
    // with zero chunk GUIDs, so there is no chunk closure to chase.
    //
    // The editing model is "overwrite a donor's elevations", never "convert a foreign terrain" --
    // see the header of FrostyCLI.Core\TerrainResource.cs for the measurements behind that.
    public partial class TerrainCommand : ICommand
    {
        public string Name => "terrain";

        private const string ResSuffix = ".streamingtree_win32";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args == null || args.Length == 0)
            {
                Console.Error.WriteLine("Usage: frostycli terrain <info|validate|create|export-layers|clear-decals|retexture|export-heights|import-heights|retarget|holes|mask-at> ...");
                return 1;
            }
            switch (args[0].ToLowerInvariant())
            {
                case "info":           return ExecuteInfo(Sub(args), logger, formatter);
                case "validate":       return ExecuteValidate(Sub(args), logger, formatter);
                case "export-heights": return ExecuteExportHeights(Sub(args), logger, formatter);
                case "import-heights": return ExecuteImportHeights(Sub(args), logger, formatter);
                case "retarget":       return ExecuteRetarget(Sub(args), logger, formatter);
                case "holes":          return ExecuteHoles(Sub(args), logger, formatter);
                case "mask-at":        return ExecuteMaskAt(Sub(args), logger, formatter);
                case "create":         return ExecuteCreate(Sub(args), logger, formatter);
                case "export-layers":  return ExecuteExportLayers(Sub(args), logger, formatter);
                case "clear-decals":   return ExecuteClearDecals(Sub(args), logger, formatter);
                case "retexture":      return ExecuteRetexture(Sub(args), logger, formatter);
                default:
                    Console.Error.WriteLine("terrain: unknown subcommand '" + args[0] +
                        "'. Use info, validate, create, export-layers, clear-decals, retexture, export-heights, import-heights, retarget, holes or mask-at.");
                    return 1;
            }
        }

        private static string[] Sub(string[] args)
        {
            var r = new string[args.Length - 1];
            Array.Copy(args, 1, r, 0, r.Length);
            return r;
        }

        // ---------------------------------------------------------------------------------------
        // Resolving a terrain
        // ---------------------------------------------------------------------------------------

        // A terrain argument is any of: a path to a payload on disk (what `res export` writes, and
        // the only way to reach the other game's terrain, since one run loads one game); a res name;
        // or the terrain's EBX path, which the res is named after with .streamingtree_win32 appended.
        internal static byte[] LoadPayload(string label, string spec, out ResAssetEntry entry, out string source) => Load(label, spec, out entry, out source);

        private static byte[] Load(string label, string spec, out ResAssetEntry entry, out string source)
        {
            entry = null;
            source = null;
            if (string.IsNullOrEmpty(spec)) { Console.Error.WriteLine(label + ": missing <terrain>"); return null; }

            if (File.Exists(spec))
            {
                source = "file " + spec;
                try { return File.ReadAllBytes(spec); }
                catch (Exception ex) { Console.Error.WriteLine(label + ": could not read " + spec + ": " + ex.Message); return null; }
            }

            if (App.AssetManager == null)
            {
                Console.Error.WriteLine(label + ": AssetManager not initialized (and '" + spec + "' is not a file on disk).");
                return null;
            }

            entry = ResolveRes(spec);
            if (entry == null)
            {
                Console.Error.WriteLine(label + ": no terrain resource for '" + spec + "'. Give a res name, the " +
                    "terrain EBX path, or a payload file. `res ls \"\" --limit 900000` lists every resource.");
                return null;
            }
            if (!CheckResType(label, entry)) { entry = null; return null; }
            source = "res " + entry.Name;
            byte[] bytes = ReadRes(entry);
            if (bytes == null) Console.Error.WriteLine(label + ": could not read " + entry.Name);
            return bytes;
        }

        // The streaming tree is looked for first, ahead of an exact-name match: a terrain EBX path
        // is also the name of its VisualTerrain resource, so an exact match on "<terrain>" silently
        // hands back the wrong resource (a few KB of struct stream instead of megabytes of tree).
        private static ResAssetEntry ResolveRes(string spec)
        {
            if (!spec.EndsWith(ResSuffix, StringComparison.OrdinalIgnoreCase))
            {
                ResAssetEntry tree = App.AssetManager.GetResEntry(spec.ToLowerInvariant() + ResSuffix);
                if (tree != null) return tree;
            }
            ResAssetEntry entry = App.AssetManager.GetResEntry(spec);
            if (entry != null) return entry;
            return App.AssetManager.GetResEntry(spec.ToLowerInvariant());
        }

        // ResType of TerrainStreamingTree (0x22FE8AC8), measured on every shipped terrain in both
        // games. Its two neighbours -- VisualTerrain (480480774) and TerrainLayerCombinations
        // (2722002395) -- are entirely different payloads, so naming one of those is caught here
        // rather than surfacing later as a malformed record.
        private const uint StreamingTreeResType = 587107016;

        private static bool CheckResType(string label, ResAssetEntry entry)
        {
            if (entry == null || entry.ResType == StreamingTreeResType) return true;
            string hint = entry.ResType == 480480774 ? " -- that is the VisualTerrain resource; the elevations live in the sibling ending .streamingtree_win32"
                        : entry.ResType == 2722002395 ? " -- that is the TerrainLayerCombinations resource; the elevations live in the sibling ending .streamingtree_win32"
                        : "";
            Console.Error.WriteLine(label + ": " + entry.Name + " is res type " + entry.ResType +
                ", not a TerrainStreamingTree (" + StreamingTreeResType + ")" + hint);
            return false;
        }

        private static byte[] ReadRes(ResAssetEntry entry)
        {
            try
            {
                System.Reflection.MethodInfo m = App.AssetManager.GetType()
                    .GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
                object raw = m != null ? m.Invoke(App.AssetManager, new object[] { entry }) : null;
                if (raw is byte[]) return (byte[])raw;
                if (raw is Stream)
                {
                    using (var st = (Stream)raw)
                    using (var ms = new MemoryStream()) { st.CopyTo(ms); return ms.ToArray(); }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("terrain: res read failed: " +
                    (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
            }
            return null;
        }

        // Writes a rewritten payload either to a file (--out, no project needed) or back into the
        // loaded project's resource.
        private static int Write(string label, byte[] payload, string outFile, ResAssetEntry entry,
                                 ExportLogger logger, IDictionary<string, string> fields, OutputFormatter formatter, string op)
        {
            if (outFile != null)
            {
                if (DryRun.Active) { DryRun.Report(logger, "write {0} bytes to {1}", payload.Length, outFile); return 0; }
                string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outFile, payload);
                fields["file"] = outFile;
                formatter.WriteResult("OK", op, fields);
                return 0;
            }

            if (entry == null)
            {
                Console.Error.WriteLine(label + ": the terrain was read from a file, so there is nothing to write back. Pass --out <file>.");
                return 1;
            }
            if (!Session.Project.IsLoaded)
            {
                Console.Error.WriteLine(label + ": no project loaded. Run 'project new'/'project load' first, or pass --out <file>.");
                return 1;
            }
            if (DryRun.Active) { DryRun.Report(logger, "write {0} bytes into res {1}", payload.Length, entry.Name); return 0; }

            byte[] meta = entry.ModifiedEntry != null && entry.ModifiedEntry.ResMeta != null ? entry.ModifiedEntry.ResMeta : entry.ResMeta;
            try { ResWriter.Modify(entry.Name, payload, meta); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": res write failed: " + ex.Message); return 1; }

            fields["res"] = entry.Name;
            formatter.WriteResult("OK", op, fields);
            return 0;
        }

        // ---------------------------------------------------------------------------------------
        // holes
        // ---------------------------------------------------------------------------------------

        // terrain holes <terrain> [--control N]: where the heightfield's hole markers (sample 0) sit, whether their
        // nodes carry containsHoles, and which mask layers cover them. Sample 0 only makes Havok see a hole; the
        // renderer cuts holes from the mask of the non-physical terrain layer (TerrainTextureGenerator
        // holesLayerIndex), so the layer covering the holes far more often than ordinary ground is the hole layer.
        private static int ExecuteHoles(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string spec = null;
            int controlCount = 20000;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--control" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out controlCount);
                else if (!args[i].StartsWith("--")) spec = args[i];
            }
            ResAssetEntry entry; string source;
            byte[] payload = Load("terrain holes", spec, out entry, out source);
            if (payload == null) return 1;

            TerrainRecord record;
            byte[] hfBytes = TerrainPayload.Isolate(payload, TerrainPayload.TagHeightfield, out record);
            Bfii.TerrainFormat.ParseResult hfParse = hfBytes != null ? Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(hfBytes) : null;
            Bfii.TerrainFormat.HeightfieldTree hf = hfParse != null && hfParse.Resource != null ? hfParse.Resource.Heightfield : null;
            if (hf == null) { Console.Error.WriteLine("terrain holes: heightfield did not parse: " + (hfParse != null ? hfParse.Failure : "no record")); return 1; }

            int side = (int)hf.NodeSamplesPerSide, border = (int)hf.NodeBorderWidth, interior = side - 2 * border - 1;
            var holesByLevel = new SortedDictionary<int, int>();
            var holes = new List<double[]>();
            var ground = new List<double[]>();
            var rng = new Random(12345);
            long groundSeen = 0;
            int flagged = 0, withZeros = 0, flaggedWithZeros = 0;
            foreach (Bfii.TerrainFormat.HeightfieldNode n in hf.EnumerateNodes())
            {
                if (n.ContainsHoles) flagged++;
                if (n.Payload == null) continue;
                // Only the finest data at each spot: a node whose children carry data is covered by them.
                bool leaf = true;
                foreach (Bfii.TerrainFormat.HeightfieldNode c in n.Children) if (c.Payload != null) { leaf = false; break; }
                var s = new List<ushort>(TerrainPayload.ToU16(n.Payload.AtlasData));
                Bfii.TerrainFormat.AxisAlignedBox b = n.BoundingBox;
                double x0 = b.MinX.Value, z0 = b.MinZ.Value;
                double dx = (b.MaxX.Value - x0) / interior, dz = (b.MaxZ.Value - z0) / interior;
                int zeros = 0;
                for (int j = border; j < side - border; j++)
                    for (int i = border; i < side - border; i++)
                    {
                        double x = x0 + (i - border) * dx, z = z0 + (j - border) * dz;
                        if (s[j * side + i] == 0) { zeros++; if (leaf) holes.Add(new[] { x, z }); }
                        else if (leaf)
                        {
                            groundSeen++;
                            if (ground.Count < controlCount) ground.Add(new[] { x, z });
                            else { long k = (long)(rng.NextDouble() * groundSeen); if (k < controlCount) ground[(int)k] = new[] { x, z }; }
                        }
                    }
                if (zeros > 0)
                {
                    withZeros++;
                    if (n.ContainsHoles) flaggedWithZeros++;
                    int c0; holesByLevel.TryGetValue(n.Id.Level, out c0); holesByLevel[n.Id.Level] = c0 + zeros;
                }
            }

            Console.Out.WriteLine(source);
            var levelText = new StringBuilder();
            foreach (KeyValuePair<int, int> kv in holesByLevel) levelText.Append(levelText.Length > 0 ? ", " : "").Append("L").Append(kv.Key).Append(' ').Append(kv.Value);
            Console.Out.WriteLine("  hole markers (sample 0) by level: " + (levelText.Length > 0 ? levelText.ToString() : "none"));
            Console.Out.WriteLine("  finest-level hole samples: " + holes.Count + "; nodes flagged containsHoles: " + flagged +
                "; nodes holding zeros: " + withZeros + " (" + flaggedWithZeros + " of them flagged)");

            // Clusters of finest-level holes, 16 m cells joined 8-connected.
            var cells = new Dictionary<long, List<double[]>>();
            foreach (double[] p in holes)
            {
                long key = HoleCellKey((int)Math.Floor(p[0] / 16.0), (int)Math.Floor(p[1] / 16.0));
                List<double[]> l; if (!cells.TryGetValue(key, out l)) { l = new List<double[]>(); cells[key] = l; } l.Add(p);
            }
            var seen = new HashSet<long>();
            var clusters = new List<double[]>();   // minX, minZ, maxX, maxZ, count
            foreach (long start in cells.Keys)
            {
                if (seen.Contains(start)) continue;
                var box = new[] { double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, 0 };
                var stack = new Stack<long>(); stack.Push(start); seen.Add(start);
                while (stack.Count > 0)
                {
                    long k = stack.Pop();
                    foreach (double[] p in cells[k])
                    {
                        box[0] = Math.Min(box[0], p[0]); box[1] = Math.Min(box[1], p[1]);
                        box[2] = Math.Max(box[2], p[0]); box[3] = Math.Max(box[3], p[1]); box[4]++;
                    }
                    int cx = (int)((k >> 21) - 1000000), cz = (int)((k & 0x1FFFFF) - 1000000);
                    for (int ox = -1; ox <= 1; ox++)
                        for (int oz = -1; oz <= 1; oz++)
                        {
                            long nk = HoleCellKey(cx + ox, cz + oz);
                            if (cells.ContainsKey(nk) && seen.Add(nk)) stack.Push(nk);
                        }
                }
                clusters.Add(box);
            }
            clusters.Sort(delegate (double[] a2, double[] b2) { return b2[4].CompareTo(a2[4]); });
            Console.Out.WriteLine("  hole clusters: " + clusters.Count);
            for (int i = 0; i < clusters.Count && i < 15; i++)
                Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "    {0,6} samples  x {1:0} .. {2:0}  z {3:0} .. {4:0}",
                    clusters[i][4], clusters[i][0], clusters[i][2], clusters[i][1], clusters[i][3]));

            byte[] maskBytes = TerrainPayload.Isolate(payload, TerrainPayload.TagMask, out record);
            Bfii.TerrainFormat.ParseResult mp = maskBytes != null ? Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(maskBytes) : null;
            Bfii.TerrainFormat.MaskTree mask = mp != null && mp.Resource != null ? mp.Resource.Mask : null;
            if (mask == null) { Console.Out.WriteLine("  mask: did not parse (" + (mp != null ? mp.Failure : "no record") + ")"); return 0; }
            int tsp = (int)mask.TileSamplesPerSide;
            var grid = new Dictionary<long, List<object[]>>();
            var big = new List<object[]>();
            int subtiles = 0;
            foreach (Bfii.TerrainFormat.MaskNode mn in mask.EnumerateNodes())
            {
                int tileIndex = 0;
                foreach (Bfii.TerrainFormat.MaskSubtile st in mn.Subtiles)
                {
                    subtiles++;
                    byte[] tile = null;
                    if ((st.Flags & 2) == 0) { if (tileIndex < mn.Tiles.Count) tile = mn.Tiles[tileIndex]; tileIndex++; }
                    var item = new object[] { st, tile };
                    int cx0 = (int)Math.Floor(st.WorldCoverage.MinX.Value / 64.0), cx1 = (int)Math.Floor(st.WorldCoverage.MaxX.Value / 64.0);
                    int cz0 = (int)Math.Floor(st.WorldCoverage.MinY.Value / 64.0), cz1 = (int)Math.Floor(st.WorldCoverage.MaxY.Value / 64.0);
                    if ((long)(cx1 - cx0 + 1) * (cz1 - cz0 + 1) > 4096) { big.Add(item); continue; }
                    for (int cx = cx0; cx <= cx1; cx++)
                        for (int cz = cz0; cz <= cz1; cz++)
                        {
                            long key = HoleCellKey(cx, cz);
                            List<object[]> l; if (!grid.TryGetValue(key, out l)) { l = new List<object[]>(); grid[key] = l; } l.Add(item);
                        }
                }
            }
            Console.Out.WriteLine("  mask: " + subtiles + " subtiles, " + mask.MaxLayerCount + " layer index(es), tile " + tsp + " samples/side, stride level " + mask.FittedStrideLevel);

            var holeCover = new double[256]; var holeWeight = new double[256];
            var groundCover = new double[256]; var groundWeight = new double[256];
            int holeProbes = SumLayerCover(holes, 20000, grid, big, tsp, holeCover, holeWeight);
            int groundProbes = SumLayerCover(ground, controlCount, grid, big, tsp, groundCover, groundWeight);
            var layers = new List<int>();
            for (int l = 0; l < 256; l++) if (holeCover[l] > 0 || groundCover[l] > 0) layers.Add(l);
            layers.Sort(delegate (int a3, int b3)
            {
                double da = (holeProbes > 0 ? holeCover[a3] / holeProbes : 0) - (groundProbes > 0 ? groundCover[a3] / groundProbes : 0);
                double db = (holeProbes > 0 ? holeCover[b3] / holeProbes : 0) - (groundProbes > 0 ? groundCover[b3] / groundProbes : 0);
                return db.CompareTo(da);
            });
            Console.Out.WriteLine("  mask layers at " + holeProbes + " hole samples vs " + groundProbes + " ground samples (coverage %, mean weight where covered):");
            foreach (int l in layers)
                Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "    layer {0,3}: holes {1,6:0.0}% (w {2,5:0})   ground {3,6:0.0}% (w {4,5:0})", l,
                    holeProbes > 0 ? 100.0 * holeCover[l] / holeProbes : 0, holeCover[l] > 0 ? holeWeight[l] / holeCover[l] : 0,
                    groundProbes > 0 ? 100.0 * groundCover[l] / groundProbes : 0, groundCover[l] > 0 ? groundWeight[l] / groundCover[l] : 0));
            return 0;
        }

        // terrain mask-at <terrain> --at x,z[;x,z...]: the mask nodes covering each point and their subtiles, and whether the
        // isolated mask record re-serialises byte for byte (an in-place hole-layer paint relies on that).
        private static int ExecuteMaskAt(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string spec = null, at = null, dump = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--at" && i + 1 < args.Length) at = args[++i];
                else if (args[i] == "--dump" && i + 1 < args.Length) dump = args[++i];
                else if (!args[i].StartsWith("--")) spec = args[i];
            }
            if (at == null && dump == null) { Console.Error.WriteLine("terrain mask-at: missing --at x,z[;x,z...] (or --dump <file>)"); return 1; }
            if (at == null) at = "";
            ResAssetEntry entry; string source;
            byte[] payload = Load("terrain mask-at", spec, out entry, out source);
            if (payload == null) return 1;

            TerrainRecord record;
            byte[] maskBytes = TerrainPayload.Isolate(payload, TerrainPayload.TagMask, out record);
            Bfii.TerrainFormat.ParseResult mp = maskBytes != null ? Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(maskBytes) : null;
            Bfii.TerrainFormat.MaskTree mask = mp != null && mp.Resource != null ? mp.Resource.Mask : null;
            if (mask == null) { Console.Error.WriteLine("terrain mask-at: mask did not parse: " + (mp != null ? mp.Failure : "no record")); return 1; }

            Console.Out.WriteLine(source);
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  mask header: tile {0}/side, {1} tile(s)/node side, atlas {2}x{3} ({4}x{5} tiles = {6}), blur 2^{7}, coverage {8:0}..{9:0} x {10:0}..{11:0}",
                mask.TileSamplesPerSide, mask.TilesPerNodeSide, mask.ResourceAtlasSampleCountX, mask.ResourceAtlasSampleCountY,
                mask.AtlasTileCountX, mask.AtlasTileCountY, (long)mask.AtlasTileCountX * mask.AtlasTileCountY, mask.BlurrinessLog2,
                mask.TreeCoverage.MinX.Value, mask.TreeCoverage.MaxX.Value, mask.TreeCoverage.MinY.Value, mask.TreeCoverage.MaxY.Value));
            Console.Out.WriteLine("  nodes " + mask.NodeCount + ", subtiles " + mask.SubtileCount + ", persistent non-trivial " +
                mask.PersistentNonTrivialSubtileCount + ", coverage levels " + mask.CoverageGridLevelCount + ", stride level " + mask.FittedStrideLevel + " (" + mask.Stride + " B)");
            try
            {
                byte[] again = Bfii.TerrainFormat.TerrainStreamingTreeSerializer.Serialize(mp.Resource, Bfii.TerrainFormat.SerializeOptions.Identity);
                int diff = -1;
                int n = Math.Min(again.Length, maskBytes.Length);
                for (int i = 0; i < n; i++) if (again[i] != maskBytes[i]) { diff = i; break; }
                Console.Out.WriteLine("  round-trip: " + (again.Length == maskBytes.Length && diff < 0 ? "byte-identical" :
                    "DIFFERS (" + again.Length + " vs " + maskBytes.Length + " bytes, first difference at " + (diff < 0 ? n : diff) + ")"));
            }
            catch (Exception ex) { Console.Out.WriteLine("  round-trip: serializer threw " + ex.Message); }

            int tsp = (int)mask.TileSamplesPerSide;
            if (dump != null)
            {
                var all = new JArray();
                foreach (Bfii.TerrainFormat.MaskNode mn in mask.EnumerateNodes())
                {
                    int ti = 0;
                    foreach (Bfii.TerrainFormat.MaskSubtile st in mn.Subtiles)
                    {
                        byte[] tile = null;
                        if ((st.Flags & 2) == 0) { if (ti < mn.Tiles.Count) tile = mn.Tiles[ti]; ti++; }
                        var c = st.WorldCoverage;
                        all.Add(new JObject
                        {
                            ["level"] = mn.Id.Level,
                            ["node"] = new JArray(mn.NodeCoverage.MinX.Value, mn.NodeCoverage.MinY.Value, mn.NodeCoverage.MaxX.Value, mn.NodeCoverage.MaxY.Value),
                            ["layer"] = st.TerrainLayerIndex,
                            ["box"] = new JArray(c.MinX.Value, c.MinY.Value, c.MaxX.Value, c.MaxY.Value),
                            ["leaf"] = st.IsLeafRaw,
                            ["full"] = st.MaskDataFullRaw,
                            ["coverage"] = BitConverter.ToString(st.HighResolutionCoverageGridStack).Replace("-", ""),
                            ["tile"] = tile != null ? BitConverter.ToString(tile).Replace("-", "") : null
                        });
                    }
                }
                var doc = new JObject { ["tileSamplesPerSide"] = tsp, ["strideLevel"] = mask.FittedStrideLevel, ["subtiles"] = all };
                File.WriteAllText(dump, doc.ToString(Formatting.None));
                Console.Out.WriteLine("  dumped " + all.Count + " subtile(s) to " + dump);
            }
            foreach (string pt in at.Split(';'))
            {
                string[] xz = pt.Split(',');
                if (xz.Length != 2) continue;
                double x = double.Parse(xz[0], CultureInfo.InvariantCulture), z = double.Parse(xz[1], CultureInfo.InvariantCulture);
                Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "  point {0:0.#},{1:0.#}", x, z));
                foreach (Bfii.TerrainFormat.MaskNode mn in mask.EnumerateNodes())
                {
                    var nc = mn.NodeCoverage;
                    if (x < nc.MinX.Value || x > nc.MaxX.Value || z < nc.MinY.Value || z > nc.MaxY.Value) continue;
                    Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "    L{0} node {1:0}..{2:0} x {3:0}..{4:0}  pruned {5} persistent {6} subtiles {7} (non-trivial {8}, recursive {9}) tiles {10} tile bytes {11} children {12}",
                        mn.Id.Level, nc.MinX.Value, nc.MaxX.Value, nc.MinY.Value, nc.MaxY.Value, mn.PrunedRaw, mn.PersistentRaw, mn.SubtileCount,
                        mn.NonTrivialSubtileCount, mn.NonTrivialPersistentSubtileCountRecursive, mn.Tiles.Count, mn.TileDataBytes, mn.HasChildrenRaw));
                    int tileIndex = 0;
                    foreach (Bfii.TerrainFormat.MaskSubtile st in mn.Subtiles)
                    {
                        byte[] tile = null;
                        if ((st.Flags & 2) == 0) { if (tileIndex < mn.Tiles.Count) tile = mn.Tiles[tileIndex]; tileIndex++; }
                        var c = st.WorldCoverage;
                        bool inside = x >= c.MinX.Value && x <= c.MaxX.Value && z >= c.MinY.Value && z <= c.MaxY.Value;
                        string w = "-";
                        long sum = 0;
                        if (tile != null)
                        {
                            foreach (byte b in tile) sum += b;
                            if (inside && tsp > 1)
                            {
                                int xi = (int)Math.Round((x - c.MinX.Value) / Math.Max(1e-6, c.MaxX.Value - c.MinX.Value) * (tsp - 1));
                                int zi = (int)Math.Round((z - c.MinY.Value) / Math.Max(1e-6, c.MaxY.Value - c.MinY.Value) * (tsp - 1));
                                w = tile[Math.Min(tsp - 1, Math.Max(0, zi)) * tsp + Math.Min(tsp - 1, Math.Max(0, xi))].ToString(CultureInfo.InvariantCulture);
                            }
                        }
                        int covBits = 0;
                        foreach (byte b in st.HighResolutionCoverageGridStack) for (int k = 0; k < 8; k++) if ((b & (1 << k)) != 0) covBits++;
                        Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "      layer {0,2} cov {1:0.#}..{2:0.#} x {3:0.#}..{4:0.#} {5} leaf {6} full {7} tile {8} weight@pt {9} tileMean {10:0.0} covBits {11}/{12}",
                            st.TerrainLayerIndex, c.MinX.Value, c.MaxX.Value, c.MinY.Value, c.MaxY.Value, inside ? "IN " : "out", st.IsLeafRaw, st.MaskDataFullRaw,
                            tile != null ? "yes" : "no", w, tile != null ? (double)sum / tile.Length : 0, covBits, st.HighResolutionCoverageGridStack.Length * 8));
                    }
                }
            }
            return 0;
        }

        private static long HoleCellKey(int cx, int cz) { return ((long)(cx + 1000000) << 21) | (uint)(cz + 1000000); }

        // For up to `limit` points: per layer, how many points some subtile of that layer covers, and the summed max tile
        // weight there (a subtile without a tile counts as 255).
        private static int SumLayerCover(List<double[]> points, int limit, Dictionary<long, List<object[]>> grid, List<object[]> big,
                                         int tsp, double[] cover, double[] weight)
        {
            int step = Math.Max(1, points.Count / Math.Max(1, limit)), probes = 0;
            var c = new double[256]; var w = new double[256];
            for (int pi = 0; pi < points.Count; pi += step)
            {
                double x = points[pi][0], z = points[pi][1];
                Array.Clear(c, 0, 256); Array.Clear(w, 0, 256);
                List<object[]> list;
                if (grid.TryGetValue(HoleCellKey((int)Math.Floor(x / 64.0), (int)Math.Floor(z / 64.0)), out list))
                    foreach (object[] it in list) ProbeSubtile(it, x, z, tsp, c, w);
                foreach (object[] it in big) ProbeSubtile(it, x, z, tsp, c, w);
                for (int l = 0; l < 256; l++) { cover[l] += c[l]; weight[l] += w[l]; }
                probes++;
            }
            return probes;
        }

        private static void ProbeSubtile(object[] it, double x, double z, int tsp, double[] cover, double[] weight)
        {
            var st = (Bfii.TerrainFormat.MaskSubtile)it[0];
            var tile = (byte[])it[1];
            double minX = st.WorldCoverage.MinX.Value, maxX = st.WorldCoverage.MaxX.Value;
            double minZ = st.WorldCoverage.MinY.Value, maxZ = st.WorldCoverage.MaxY.Value;
            if (x < minX || x > maxX || z < minZ || z > maxZ) return;
            int l = st.TerrainLayerIndex;
            cover[l] = 1;
            double v = 255;
            if (tile != null && tsp > 1 && tile.Length >= tsp * tsp)
            {
                int xi = (int)Math.Round((x - minX) / Math.Max(1e-6, maxX - minX) * (tsp - 1));
                int zi = (int)Math.Round((z - minZ) / Math.Max(1e-6, maxZ - minZ) * (tsp - 1));
                v = tile[Math.Min(tsp - 1, Math.Max(0, zi)) * tsp + Math.Min(tsp - 1, Math.Max(0, xi))];
            }
            if (v > weight[l]) weight[l] = v;
        }

        // ---------------------------------------------------------------------------------------
        // info
        // ---------------------------------------------------------------------------------------

        private static int ExecuteInfo(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string spec = null, against = null, format = "table";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--against" && i + 1 < args.Length) against = args[++i];
                else if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--json") format = "json";
                else if (!args[i].StartsWith("--")) spec = args[i];
            }

            ResAssetEntry entry; string source;
            byte[] payload = Load("terrain info", spec, out entry, out source);
            if (payload == null) return 1;

            TerrainSurvey survey;
            try { survey = TerrainSurvey.Measure(payload); }
            catch (Exception ex) { Console.Error.WriteLine("terrain info: " + ex.Message); return 1; }

            TerrainSurvey other = null;
            if (against != null)
            {
                ResAssetEntry otherEntry; string otherSource;
                byte[] otherPayload = Load("terrain info --against", against, out otherEntry, out otherSource);
                if (otherPayload == null) return 1;
                try { other = TerrainSurvey.Measure(otherPayload); }
                catch (Exception ex) { Console.Error.WriteLine("terrain info: --against: " + ex.Message); return 1; }
            }

            if (format == "json") { Console.Out.WriteLine(ToJson(survey, source, other, against).ToString(Formatting.Indented)); return 0; }

            PrintSurvey(survey, source);
            if (other != null)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine("  against " + against + ":");
                List<string> notes = survey.CompareTo(other, "this", "that");
                if (notes.Count == 0) Console.Out.WriteLine("    no differences that affect a port");
                foreach (string n in notes) Console.Out.WriteLine("    - " + n);
            }
            return 0;
        }

        private static void PrintSurvey(TerrainSurvey s, string source)
        {
            Console.Out.WriteLine(source + "  (" + s.PayloadBytes.ToString("N0") + " bytes)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("  header");
            Console.Out.WriteLine("    unblurredSamplesPerNodeSidePot   " + s.UnblurredSamplesPerNodeSidePot);
            Console.Out.WriteLine("    trackTextureDetailFalloff        " + s.TrackTextureDetailFalloff);
            Console.Out.WriteLine("    invisible/occludedDetailFactor   " + s.InvisibleDetailReductionFactor + " / " + s.OccludedDetailReductionFactor);
            Console.Out.WriteLine("    field0x0D (resourceBlurriness)   " + s.ResourceBlurriness);
            Console.Out.WriteLine("    field0x11 (skipLevels)           " + s.SkipLevels +
                (s.SkipLevels == 1 ? "   [SWBF 2015 value]" : s.SkipLevels == 2 ? "   [BFII value]" : ""));
            Console.Out.WriteLine("    streamingNodeCount               " + s.StreamingNodeCount);
            Console.Out.WriteLine("    freeStreamingEnable              " + s.FreeStreamingEnable);

            Console.Out.WriteLine();
            Console.Out.WriteLine("  records");
            foreach (TerrainSurvey.RecordInfo r in s.Records)
                Console.Out.WriteLine("    tag " + r.Tag + "  " + r.Name.PadRight(12) + r.Length.ToString("N0").PadLeft(14) + " B   " +
                    (r.Parsed ? "parsed" : "UNREADABLE" + (r.Failure != null ? ": " + Trim(r.Failure) : "")));
            if (!s.FullParseOk)
                Console.Out.WriteLine("    (a whole-payload parse fails: " + Trim(s.FullParseFailure) + " -- each tree above was read on its own)");

            if (!s.HasHeightfield) { Console.Out.WriteLine(); Console.Out.WriteLine("  no heightfield record"); return; }

            Console.Out.WriteLine();
            Console.Out.WriteLine("  heightfield");
            Console.Out.WriteLine("    tile                             " + s.NodeSamplesPerSide + " samples/side, border " +
                s.NodeBorderWidth + ", interior pot " + s.InteriorPot);
            Console.Out.WriteLine("    worldSizeY                       " + s.WorldSizeY + " m");
            Console.Out.WriteLine("    world XZ                         " + s.WorldMinX.ToString("0.#") + " .. " + s.WorldMaxX.ToString("0.#") +
                "  x  " + s.WorldMinZ.ToString("0.#") + " .. " + s.WorldMaxZ.ToString("0.#") +
                "   (" + (s.WorldMaxX - s.WorldMinX).ToString("0.#") + " m across)");
            Console.Out.WriteLine("    height band                      " + s.MinHeightMetres.ToString("0.##") + " .. " +
                s.MaxHeightMetres.ToString("0.##") + " m");
            Console.Out.WriteLine("    nodes                            " + s.HeightfieldNodeCount + " total, " +
                s.PersistentNodeCount + " persistent, " + s.SkippedNodeCount + " skipped, " + s.NodesWithData + " with data");
            Console.Out.WriteLine("    minmax / occluder depth          " + s.MinMaxStackDepth + " / " + s.OccluderGridStackDepth);
            Console.Out.WriteLine("    node data size                   " + s.NodeDataSize.ToString("N0") + " B");
            if (s.TotalSamples > 0)
                Console.Out.WriteLine("    hole markers (sample 0)          " + s.ZeroSamples.ToString("N0") + " of " +
                    s.TotalSamples.ToString("N0") + " (" + (100.0 * s.ZeroSamples / s.TotalSamples).ToString("0.00") + "%)");

            Console.Out.WriteLine();
            Console.Out.WriteLine("    atlas " + s.AtlasSampleCountX + "x" + s.AtlasSampleCountY + " = " + s.AtlasColumns + "x" +
                s.AtlasRows + " = " + s.AtlasSlots + " slot(s) for " + s.PersistentNodeCount + " persistent node(s)  " +
                (s.AtlasFits ? "OK" : "TOO SMALL"));

            Console.Out.WriteLine();
            Console.Out.WriteLine("  nodes by level");
            foreach (TerrainSurvey.LevelCensus l in s.Levels)
                Console.Out.WriteLine("    L" + l.Level + "  " + l.Nodes.ToString().PadLeft(5) + " node(s), " +
                    l.WithData.ToString().PadLeft(5) + " with data");
        }

        private static string Trim(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 160 ? s.Substring(0, 157) + "..." : s;
        }

        private static JObject ToJson(TerrainSurvey s, string source, TerrainSurvey other, string againstName)
        {
            var records = new JArray();
            foreach (TerrainSurvey.RecordInfo r in s.Records)
                records.Add(new JObject { ["tag"] = r.Tag, ["name"] = r.Name, ["bytes"] = r.Length, ["parsed"] = r.Parsed, ["failure"] = r.Failure });
            var levels = new JArray();
            foreach (TerrainSurvey.LevelCensus l in s.Levels)
                levels.Add(new JObject { ["level"] = l.Level, ["nodes"] = l.Nodes, ["withData"] = l.WithData });

            var o = new JObject
            {
                ["source"] = source,
                ["bytes"] = s.PayloadBytes,
                ["fullParse"] = s.FullParseOk,
                ["fullParseFailure"] = s.FullParseFailure,
                ["header"] = new JObject
                {
                    ["unblurredSamplesPerNodeSidePot"] = s.UnblurredSamplesPerNodeSidePot,
                    ["trackTextureDetailFalloff"] = s.TrackTextureDetailFalloff,
                    ["invisibleDetailReductionFactor"] = s.InvisibleDetailReductionFactor,
                    ["occludedDetailReductionFactor"] = s.OccludedDetailReductionFactor,
                    ["field0x0D_resourceBlurriness"] = s.ResourceBlurriness,
                    ["field0x11_skipLevels"] = s.SkipLevels,
                    ["streamingNodeCount"] = s.StreamingNodeCount,
                    ["freeStreamingEnable"] = s.FreeStreamingEnable
                },
                ["records"] = records
            };

            if (s.HasHeightfield)
            {
                o["heightfield"] = new JObject
                {
                    ["nodeSamplesPerSide"] = s.NodeSamplesPerSide,
                    ["nodeBorderWidth"] = s.NodeBorderWidth,
                    ["interiorPot"] = s.InteriorPot,
                    ["worldSizeY"] = s.WorldSizeY,
                    ["worldMinX"] = s.WorldMinX,
                    ["worldMaxX"] = s.WorldMaxX,
                    ["worldMinZ"] = s.WorldMinZ,
                    ["worldMaxZ"] = s.WorldMaxZ,
                    ["minHeightMetres"] = s.MinHeightMetres,
                    ["maxHeightMetres"] = s.MaxHeightMetres,
                    ["nodeCount"] = s.HeightfieldNodeCount,
                    ["persistentNodeCount"] = s.PersistentNodeCount,
                    ["skippedNodeCount"] = s.SkippedNodeCount,
                    ["nodesWithData"] = s.NodesWithData,
                    ["minMaxStackDepth"] = s.MinMaxStackDepth,
                    ["occluderGridStackDepth"] = s.OccluderGridStackDepth,
                    ["nodeDataSize"] = s.NodeDataSize,
                    ["zeroSamples"] = s.ZeroSamples,
                    ["totalSamples"] = s.TotalSamples,
                    ["atlasSampleCountX"] = s.AtlasSampleCountX,
                    ["atlasSampleCountY"] = s.AtlasSampleCountY,
                    ["atlasSlots"] = s.AtlasSlots,
                    ["atlasFits"] = s.AtlasFits
                };
                o["levels"] = levels;
            }

            if (other != null)
            {
                var notes = new JArray();
                foreach (string n in s.CompareTo(other, "this", "that")) notes.Add(n);
                o["against"] = new JObject { ["terrain"] = againstName, ["differences"] = notes };
            }
            return o;
        }

        // ---------------------------------------------------------------------------------------
        // validate
        // ---------------------------------------------------------------------------------------

        private static int ExecuteValidate(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string spec = null; bool json = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--json") json = true;
                else if (args[i] == "--format" && i + 1 < args.Length) json = args[++i].ToLowerInvariant() == "json";
                else if (!args[i].StartsWith("--")) spec = args[i];
            }

            ResAssetEntry entry; string source;
            byte[] payload = Load("terrain validate", spec, out entry, out source);
            if (payload == null) return 1;

            var problems = new List<string>();
            var notes = new List<string>();
            TerrainSurvey survey = null;
            try
            {
                survey = TerrainSurvey.Measure(payload);
                problems.AddRange(survey.Problems());
                notes.AddRange(survey.Notes());
            }
            catch (Exception ex) { problems.Add("measure failed: " + ex.Message); }

            // Round-trip: parse the heightfield and re-serialise with no edits. Anything but a
            // byte-identical record means the writer would corrupt this terrain, so no rewrite
            // should be attempted against it.
            string roundTrip = "not attempted";
            if (survey != null && survey.HasHeightfield)
            {
                try
                {
                    TerrainRecord record;
                    byte[] isolated = TerrainPayload.Isolate(payload, TerrainPayload.TagHeightfield, out record);
                    Bfii.TerrainFormat.ParseResult parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(isolated);
                    if (parsed.Resource == null) { roundTrip = "FAILED (parse)"; problems.Add("heightfield did not parse: " + parsed.Failure); }
                    else
                    {
                        byte[] again = Bfii.TerrainFormat.TerrainStreamingTreeSerializer.Serialize(
                            parsed.Resource, Bfii.TerrainFormat.SerializeOptions.Identity);
                        int term2;
                        TerrainRecord rebuilt = TerrainPayload.Find(TerrainPayload.Records(again, out term2), TerrainPayload.TagHeightfield);
                        bool identical = rebuilt != null && rebuilt.Length == record.Length &&
                                         Same(payload, record.Start, again, rebuilt.Start, record.End - record.Start);
                        roundTrip = identical ? "byte-identical" : "DIFFERS";
                        if (!identical) problems.Add("heightfield does not round-trip byte-identically; this terrain must not be rewritten");
                    }
                }
                catch (Exception ex) { roundTrip = "FAILED"; problems.Add("round-trip failed: " + ex.Message); }
            }

            if (json)
            {
                var problemArray = new JArray();
                foreach (string p in problems) problemArray.Add(p);
                var noteArray = new JArray();
                foreach (string n in notes) noteArray.Add(n);
                Console.Out.WriteLine(new JObject
                {
                    ["source"] = source,
                    ["ok"] = problems.Count == 0,
                    ["roundTrip"] = roundTrip,
                    ["problems"] = problemArray,
                    ["notes"] = noteArray
                }.ToString(Formatting.Indented));
                return problems.Count == 0 ? 0 : 1;
            }

            Console.Out.WriteLine(source);
            Console.Out.WriteLine("  heightfield round-trip: " + roundTrip);
            if (survey != null && survey.HasHeightfield)
                Console.Out.WriteLine("  atlas capacity: " + survey.AtlasSlots + " slot(s) for " +
                    survey.PersistentNodeCount + " persistent node(s) -- " + (survey.AtlasFits ? "OK" : "TOO SMALL"));
            foreach (string n in notes) Console.Out.WriteLine("  note: " + n);
            if (problems.Count == 0) { Console.Out.WriteLine("  OK -- no problems found"); return 0; }
            Console.Out.WriteLine("  " + problems.Count + " problem(s):");
            foreach (string p in problems) Console.Out.WriteLine("    - " + p);
            return 1;
        }

        private static bool Same(byte[] a, int aOff, byte[] b, int bOff, int length)
        {
            if (aOff + length > a.Length || bOff + length > b.Length) return false;
            for (int i = 0; i < length; i++) if (a[aOff + i] != b[bOff + i]) return false;
            return true;
        }

        // ---------------------------------------------------------------------------------------
        // export-heights / import-heights
        // ---------------------------------------------------------------------------------------

        // Writes the terrain's elevations as a square 16-bit raw heightmap plus a JSON sidecar
        // carrying the world rectangle and height range, so `import-heights` can put it back without
        // being told anything, and any heightmap editor can open the raw.
        private static int ExecuteExportHeights(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string spec = null, outFile = null, regionArg = null; int size = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--region" && i + 1 < args.Length) regionArg = args[++i];
                else if (args[i] == "--size" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                else if (!args[i].StartsWith("--")) spec = args[i];
            }
            if (outFile == null) { Console.Error.WriteLine("terrain export-heights: missing --out <file.r16>"); return 1; }
            if (size != 0 && (size < 2 || size > 16385)) { Console.Error.WriteLine("terrain export-heights: --size must be 2..16385"); return 1; }

            ResAssetEntry entry; string source;
            byte[] payload = Load("terrain export-heights", spec, out entry, out source);
            if (payload == null) return 1;

            TerrainRecord record;
            byte[] isolated;
            try { isolated = TerrainPayload.Isolate(payload, TerrainPayload.TagHeightfield, out record); }
            catch (Exception ex) { Console.Error.WriteLine("terrain export-heights: " + ex.Message); return 1; }
            if (isolated == null) { Console.Error.WriteLine("terrain export-heights: no heightfield record"); return 1; }

            Bfii.TerrainFormat.ParseResult parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(isolated);
            if (parsed.Resource == null || parsed.Resource.Heightfield == null)
            { Console.Error.WriteLine("terrain export-heights: heightfield did not parse: " + parsed.Failure); return 1; }

            Bfii.TerrainFormat.HeightfieldTree tree = parsed.Resource.Heightfield;
            if (tree.Root == null) { Console.Error.WriteLine("terrain export-heights: heightfield has no root node"); return 1; }

            // Native resolution: the deepest level carrying samples tiles the world with interior^2
            // sample grids, so the whole terrain is (interior << deepestLevel) + 1 samples across.
            // Exporting below that loses detail and exporting above it invents none, so it is both
            // the default and the number to report.
            int deepest = 0;
            foreach (Bfii.TerrainFormat.HeightfieldNode n in tree.EnumerateNodes())
                if (n.Payload != null && n.Id.Level > deepest) deepest = n.Id.Level;
            int interior = (int)tree.NodeSamplesPerSide - 2 * (int)tree.NodeBorderWidth - 1;
            long native = interior > 0 ? ((long)interior << deepest) + 1 : 0;
            var sampler = new TerrainHeightSource(tree);
            Bfii.TerrainFormat.AxisAlignedBox box = tree.Root.BoundingBox;
            double x0 = box.MinX.Value, x1 = box.MaxX.Value, z0 = box.MinZ.Value, z1 = box.MaxZ.Value;
            if (regionArg != null)
            {
                // a part of the terrain (for `terrain create --base-host`): snapped outward to the native sample grid,
                // exported at native spacing unless --size says otherwise
                string[] r = regionArg.Split(',');
                double rx0, rz0, rx1, rz1;
                if (r.Length != 4 || !double.TryParse(r[0], NumberStyles.Float, CultureInfo.InvariantCulture, out rx0)
                    || !double.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out rz0)
                    || !double.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rx1)
                    || !double.TryParse(r[3], NumberStyles.Float, CultureInfo.InvariantCulture, out rz1) || rx1 <= rx0 || rz1 <= rz0)
                { Console.Error.WriteLine("terrain export-heights: --region wants minX,minZ,maxX,maxZ in metres"); return 1; }
                double step = native > 1 ? (x1 - x0) / (native - 1) : (x1 - x0) / 1024;
                rx0 = Math.Max(x0, x0 + Math.Floor((rx0 - x0) / step) * step); rz0 = Math.Max(z0, z0 + Math.Floor((rz0 - z0) / step) * step);
                rx1 = Math.Min(x1, x0 + Math.Ceiling((rx1 - x0) / step) * step); rz1 = Math.Min(z1, z0 + Math.Ceiling((rz1 - z0) / step) * step);
                if (rx1 - rx0 != rz1 - rz0) { double e = Math.Max(rx1 - rx0, rz1 - rz0); rx1 = Math.Min(x1, rx0 + e); rz1 = Math.Min(z1, rz0 + e); }
                if (size == 0) size = (int)Math.Round((rx1 - rx0) / step) + 1;
                x0 = rx0; x1 = rx1; z0 = rz0; z1 = rz1;
            }
            if (size == 0) size = (int)Math.Min(native > 1 ? native : 1024, 2048);
            double lo = sampler.MinMetres, hi = sampler.MaxMetres;
            double span = Math.Max(1e-6, hi - lo);

            var grid = new ushort[size * size];
            for (int j = 0; j < size; j++)
            {
                double z = z0 + (z1 - z0) * j / (double)(size - 1);
                for (int i = 0; i < size; i++)
                {
                    double x = x0 + (x1 - x0) * i / (double)(size - 1);
                    double t = (sampler.SampleMetres(x, z) - lo) / span;
                    if (t < 0) t = 0; else if (t > 1) t = 1;
                    grid[j * size + i] = (ushort)Math.Round(t * 65535.0);
                }
            }

            if (DryRun.Active) { DryRun.Report(logger, "write a {0}x{1} heightmap to {2}", size, size, outFile); return 0; }
            string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outFile, TerrainPayload.FromU16(grid));

            string sidecar = outFile + ".json";
            File.WriteAllText(sidecar, new JObject
            {
                ["source"] = source,
                ["size"] = size,
                ["format"] = "u16le",
                ["worldMinX"] = x0, ["worldMaxX"] = x1,
                ["worldMinZ"] = z0, ["worldMaxZ"] = z1,
                ["minMetres"] = lo, ["maxMetres"] = hi,
                ["rowOrder"] = "min-z-first"   // `terrain create` reads this back
            }.ToString(Formatting.Indented));

            formatter.WriteResult("OK", "terrain export-heights", new Dictionary<string, string>
            {
                { "file", outFile },
                { "size", size + "x" + size + (native > 0
                    ? (size < native ? "  (native " + native + "x" + native + " -- detail below that is lost on the way back in)"
                                     : "  (native resolution)")
                    : "") },
                { "band", lo.ToString("0.##") + ".." + hi.ToString("0.##") + " m" },
                { "sidecar", sidecar }
            });
            return 0;
        }

        private static int ExecuteImportHeights(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string spec = null, from = null, outFile = null, band = null;
            bool keepDerived = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--band" && i + 1 < args.Length) band = args[++i];
                else if (args[i] == "--keep-derived") keepDerived = true;
                else if (!args[i].StartsWith("--")) spec = args[i];
            }
            if (from == null) { Console.Error.WriteLine("terrain import-heights: missing --from <file.r16>"); return 1; }
            if (!File.Exists(from)) { Console.Error.WriteLine("terrain import-heights: file not found: " + from); return 1; }

            ResAssetEntry entry; string source;
            byte[] payload = Load("terrain import-heights", spec, out entry, out source);
            if (payload == null) return 1;

            // The sidecar `terrain export-heights` wrote carries the world rectangle and height
            // range. Without it the grid is assumed to cover the target's own extent and band.
            JObject meta = null;
            if (File.Exists(from + ".json"))
                try { meta = JObject.Parse(File.ReadAllText(from + ".json")); }
                catch (Exception ex) { Console.Error.WriteLine("terrain import-heights: ignoring unreadable sidecar: " + ex.Message); }

            ushort[] grid = TerrainPayload.ToU16(File.ReadAllBytes(from));
            int size = meta != null && meta["size"] != null ? (int)meta["size"] : (int)Math.Round(Math.Sqrt(grid.Length));
            if ((long)size * size != grid.Length)
            {
                Console.Error.WriteLine("terrain import-heights: " + from + " holds " + grid.Length +
                    " u16 sample(s), which is not a square grid. Give it a .json sidecar with the size, or use a square heightmap.");
                return 1;
            }

            TerrainSurvey target;
            try { target = TerrainSurvey.Measure(payload); }
            catch (Exception ex) { Console.Error.WriteLine("terrain import-heights: " + ex.Message); return 1; }
            if (!target.HasHeightfield) { Console.Error.WriteLine("terrain import-heights: target has no heightfield record"); return 1; }

            double minX = meta != null && meta["worldMinX"] != null ? (double)meta["worldMinX"] : target.WorldMinX;
            double maxX = meta != null && meta["worldMaxX"] != null ? (double)meta["worldMaxX"] : target.WorldMaxX;
            double minZ = meta != null && meta["worldMinZ"] != null ? (double)meta["worldMinZ"] : target.WorldMinZ;
            double maxZ = meta != null && meta["worldMaxZ"] != null ? (double)meta["worldMaxZ"] : target.WorldMaxZ;
            double lo = meta != null && meta["minMetres"] != null ? (double)meta["minMetres"] : target.MinHeightMetres;
            double hi = meta != null && meta["maxMetres"] != null ? (double)meta["maxMetres"] : target.MaxHeightMetres;

            var source2 = new HeightmapSource(grid, size, minX, minZ, maxX, maxZ, lo, hi);
            return Rewrite("terrain import-heights", payload, source2, band, false, keepDerived,
                           target, outFile, entry, logger, formatter, source);
        }

        // ---------------------------------------------------------------------------------------
        // retarget
        // ---------------------------------------------------------------------------------------

        private static double[] s_offset;
        private static bool s_fillHoles;   // --fill-holes: write source holes as ground (old behaviour)
        private static string s_maskFrom;  // --mask-from: graft the host's own mask/colour/material/destruction records
        private static int s_holeLayer = -1;       // --hole-layer: paint holes into this mask layer
        private static double s_holeDilate = 1.0;  // --hole-dilate: grow painted holes by this many metres
        // --masks-from-source: rebuild the layer masks from the SOURCE tree's masks (layer i -> layer i), then drop layer
        // mixes the target's layer combinations cannot draw (TerrainMaskTransplant)
        private static byte[] s_maskSource;
        private static string s_combinations;      // --combinations <res or file>: the target's TerrainLayerCombinations
        private static int s_maskLevel = -1, s_maskTolerance = 2, s_maskBudget = 8090;

        private static int ExecuteRetarget(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string spec = null, sourceSpec = null, outFile = null, band = null, flat = null, offsetArg = null;
            bool keepDerived = false; s_fillHoles = false; s_maskFrom = null; s_holeLayer = -1; s_holeDilate = 1.0;
            bool masksFromSource = false; s_maskSource = null; s_combinations = null; s_maskLevel = -1; s_maskTolerance = 2; s_maskBudget = 8090;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--masks-from-source") masksFromSource = true;
                else if (args[i] == "--combinations" && i + 1 < args.Length) s_combinations = args[++i];
                else if (args[i] == "--mask-level" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out s_maskLevel);
                else if (args[i] == "--mask-tolerance" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out s_maskTolerance);
                else if (args[i] == "--mask-budget" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out s_maskBudget);
                else if (args[i] == "--source" && i + 1 < args.Length) sourceSpec = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--band" && i + 1 < args.Length) band = args[++i];
                else if (args[i] == "--flat" && i + 1 < args.Length) flat = args[++i];
                else if (args[i] == "--offset" && i + 1 < args.Length) offsetArg = args[++i];
                else if (args[i] == "--keep-derived") keepDerived = true;
                else if (args[i] == "--fill-holes") s_fillHoles = true;
                else if (args[i] == "--mask-from" && i + 1 < args.Length) s_maskFrom = args[++i];
                else if (args[i] == "--hole-layer" && i + 1 < args.Length) int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out s_holeLayer);
                else if (args[i] == "--hole-dilate" && i + 1 < args.Length) double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out s_holeDilate);
                else if (!args[i].StartsWith("--")) spec = args[i];
            }
            if (sourceSpec == null && flat == null)
            { Console.Error.WriteLine("terrain retarget: missing --source <terrain-or-payload-file> (or --flat <metres>)"); return 1; }
            s_offset = null;
            if (offsetArg != null)
            {
                string[] p = offsetArg.Split(',');
                double ox, oy, oz;
                if (p.Length != 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ox)
                    || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out oy)
                    || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out oz))
                { Console.Error.WriteLine("terrain retarget: --offset wants x,y,z in metres, got '" + offsetArg + "'"); return 1; }
                s_offset = new[] { ox, oy, oz };
            }

            ResAssetEntry entry; string donorSource;
            byte[] payload = Load("terrain retarget", spec, out entry, out donorSource);
            if (payload == null) return 1;

            TerrainSurvey target;
            try { target = TerrainSurvey.Measure(payload); }
            catch (Exception ex) { Console.Error.WriteLine("terrain retarget: " + ex.Message); return 1; }
            if (!target.HasHeightfield) { Console.Error.WriteLine("terrain retarget: donor has no heightfield record"); return 1; }

            ITerrainHeightSource heights;
            bool flatSource = false;
            if (flat != null)
            {
                double metres;
                if (!double.TryParse(flat, NumberStyles.Float, CultureInfo.InvariantCulture, out metres))
                { Console.Error.WriteLine("terrain retarget: --flat wants metres, got '" + flat + "'"); return 1; }
                heights = new FlatHeightSource(metres);
                flatSource = true;
            }
            else
            {
                ResAssetEntry sourceEntry; string sourceName;
                byte[] sourcePayload = Load("terrain retarget --source", sourceSpec, out sourceEntry, out sourceName);
                if (sourcePayload == null) return 1;

                TerrainRecord ignored;
                byte[] isolated;
                try { isolated = TerrainPayload.Isolate(sourcePayload, TerrainPayload.TagHeightfield, out ignored); }
                catch (Exception ex) { Console.Error.WriteLine("terrain retarget: source: " + ex.Message); return 1; }
                if (isolated == null) { Console.Error.WriteLine("terrain retarget: source has no heightfield record"); return 1; }

                Bfii.TerrainFormat.ParseResult parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(isolated);
                if (parsed.Resource == null || parsed.Resource.Heightfield == null)
                { Console.Error.WriteLine("terrain retarget: source heightfield did not parse: " + parsed.Failure); return 1; }

                var sampler = new TerrainHeightSource(parsed.Resource.Heightfield);
                heights = sampler;
                logger.Log("terrain retarget: source {0}: {1}", sourceName, sampler.Describe());
                if (masksFromSource) s_maskSource = sourcePayload;
            }
            if (masksFromSource && s_maskSource == null)
            { Console.Error.WriteLine("terrain retarget: --masks-from-source needs a --source terrain (not --flat)"); return 1; }
            if (masksFromSource && s_maskFrom != null)
            { Console.Error.WriteLine("terrain retarget: --masks-from-source and --mask-from both replace the mask; give one"); return 1; }
            if (masksFromSource && s_combinations == null && spec != null &&
                spec.EndsWith(".streamingtree_win32", StringComparison.OrdinalIgnoreCase))
                s_combinations = spec.Substring(0, spec.Length - ".streamingtree_win32".Length) + ".layercombinations_win32";

            return Rewrite("terrain retarget", payload, heights, band, flatSource, keepDerived,
                           target, outFile, entry, logger, formatter, donorSource);
        }

        // ---------------------------------------------------------------------------------------
        // Shared rewrite
        // ---------------------------------------------------------------------------------------

        private static int Rewrite(string label, byte[] payload, ITerrainHeightSource source, string band,
                                   bool flatSource, bool keepDerived, TerrainSurvey target,
                                   string outFile, ResAssetEntry entry, ExportLogger logger,
                                   OutputFormatter formatter, string donorSource)
        {
            var options = new TerrainRewriteOptions { Source = source, KeepDerived = keepDerived, FillHoles = s_fillHoles };
            s_fillHoles = false;
            byte[] transplantSource = s_maskSource; string combinationsSpec = s_combinations;
            int maskLevel = s_maskLevel, maskTolerance = s_maskTolerance, maskBudget = s_maskBudget;
            s_maskSource = null; s_combinations = null; s_maskLevel = -1; s_maskTolerance = 2; s_maskBudget = 8090;
            options.HoleLayer = s_holeLayer;
            options.HoleDilate = s_holeDilate;
            int protectLayer = s_holeLayer;
            if (transplantSource != null) options.HoleLayer = -1;   // the rebuilt mask brings the source's own hole layer
            string maskFrom = s_maskFrom;
            s_maskFrom = null; s_holeLayer = -1; s_holeDilate = 1.0;
            if (maskFrom != null)
            {
                ResAssetEntry maskEntry; string maskSource;
                options.MaskDonor = Load(label + " --mask-from", maskFrom, out maskEntry, out maskSource);
                if (options.MaskDonor == null) return 1;
            }
            if (s_offset != null) { options.OffsetX = s_offset[0]; options.OffsetY = s_offset[1]; options.OffsetZ = s_offset[2]; s_offset = null; }

            if (band != null && band != "none")
            {
                double lo, hi;
                if (string.Equals(band, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    lo = target.MinHeightMetres; hi = target.MaxHeightMetres;
                    if (hi - lo < 1.0)
                    {
                        Console.Error.WriteLine(label + ": --band auto wanted the donor's own height band, but it spans only " +
                            (hi - lo).ToString("0.###") + " m (" + lo.ToString("0.##") + ".." + hi.ToString("0.##") +
                            "). Give an explicit --band <lo>:<hi> around the host's playable ground instead.");
                        return 1;
                    }
                }
                else if (!ParseBand(band, out lo, out hi))
                {
                    Console.Error.WriteLine(label + ": --band wants <lo>:<hi> in metres, 'auto', or 'none'; got '" + band + "'");
                    return 1;
                }
                options.BandMin = lo;
                options.BandMax = hi;
            }

            TerrainRewriteResult result;
            try { result = TerrainHeightWriter.Rewrite(payload, options); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + ex.Message); return 1; }

            string transplantNote = null;
            if (transplantSource != null)
            {
                try
                {
                    List<int[]> combinations = null;
                    if (combinationsSpec != null)
                    {
                        ResAssetEntry comboEntry; string comboSource;
                        byte[] comboBytes = Load(label + " --combinations", combinationsSpec, out comboEntry, out comboSource);
                        if (comboBytes == null) return 1;
                        combinations = ReadCombinations(comboBytes);
                    }
                    result.Payload = TransplantMasks(result.Payload, transplantSource, combinations, maskLevel, maskTolerance, maskBudget,
                                                     options.OffsetX, options.OffsetZ, protectLayer, out transplantNote);
                    logger.Log("{0}: masks from source: {1}", label, transplantNote);
                }
                catch (Exception ex) { Console.Error.WriteLine(label + ": --masks-from-source: " + ex.Message); return 1; }
            }

            // The single most expensive mistake available here is a landform that renders correctly
            // hundreds of metres from where the players are, which is indistinguishable in game from
            // nothing rendering at all. Say so before the write, not after the playtest.
            if (!flatSource && !options.BandMin.HasValue &&
                (result.OutMinMetres > target.MaxHeightMetres + 5.0 || result.OutMaxMetres < target.MinHeightMetres - 5.0))
            {
                logger.Log("{0}: WARNING -- the rewritten terrain spans {1:0.#}..{2:0.#} m while the donor's own ground is " +
                           "{3:0.#}..{4:0.#} m. It will render, out of reach of the level's spawns and props. " +
                           "Re-run with --band {3:0.#}:{4:0.#} (or your own band) to put it where the players are.",
                           label, result.OutMinMetres, result.OutMaxMetres, target.MinHeightMetres, target.MaxHeightMetres);
            }

            var fields = new Dictionary<string, string>
            {
                { "donor", donorSource },
                { "source", source.Describe() },
                { "nodes", result.NodesRewritten.ToString() },
                { "samples", result.SamplesChanged.ToString("N0") + " of " + result.SamplesTotal.ToString("N0") + " changed" },
                { "band", result.OutMinMetres.ToString("0.##") + ".." + result.OutMaxMetres.ToString("0.##") + " m" },
                { "minmax", result.MinMaxLayout },
                { "occluder", result.OccluderLayout },
                { "occluder_grids_floored", result.OccluderGridsFloored.ToString() },
                { "bboxes", result.BoundingBoxesRecomputed.ToString() },
                { "tag0", result.RecordLength.ToString("N0") + " B (unchanged)" },
                { "holes", result.HoleNodes + " node(s) hold hole markers; containsHoles changed on " + result.HoleFlagsChanged },
                { "mask", transplantNote != null ? "rebuilt from the source's masks: " + transplantNote
                          : options.MaskDonor != null ? "mask/colour/material/destruction grafted from " + maskFrom : "donor's own" },
                { "hole layer", result.MaskReport ?? (options.HoleLayer >= 0 ? "not painted" : "off") }
            };
            return Write(label, result.Payload, outFile, entry, logger, fields, formatter, label);
        }

        // --masks-from-source: the target's mask tree rebuilt from the source tree's masks over the target's own frame and
        // mask layout, then filtered to the target's layer combinations; the heightfield, colour, material and
        // destruction trees keep their bytes and the streaming tree is rebuilt around the new mask.
        internal static byte[] TransplantMasks(byte[] targetPayload, byte[] sourcePayload, List<int[]> combinations,
                                               int maskLevel, int tolerance, int budget, double offsetX, double offsetZ,
                                               int protectLayer, out string note)
        {
            var tp = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(targetPayload);
            if (!tp.Success || tp.Resource == null || tp.Resource.Heightfield == null || tp.Resource.Mask == null)
                throw new InvalidOperationException("target does not parse with a heightfield and a mask: " + tp.Failure);
            var sp = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(sourcePayload);
            if (!sp.Success || sp.Resource == null || sp.Resource.Mask == null)
                throw new InvalidOperationException("source does not parse with a mask: " + sp.Failure);
            var target = tp.Resource;
            var src = new TerrainMaskTransplant.SourceMaskLayers(sp.Resource.Mask, offsetX, offsetZ);
            var frame = TreeHeightSource.FrameOf(target.Heightfield);
            var tm = target.Mask;
            int T = (int)tm.TileSamplesPerSide, pot = 1; while (pot * 2 <= T) pot *= 2;
            int perSide = (int)Math.Max(1, tm.TilesPerNodeSide);
            double sourceFinest = src.TexelAt(src.DeepestLevel);
            Bfii.TerrainFormat.MaskTree mask;
            string built;
            bool exact = offsetX == 0 && offsetZ == 0 && maskLevel < 0 && TerrainMaskTransplant.CanConvertStructure(sp.Resource.Mask, tm);
            if (exact)
            {
                // one-to-one: every source subtile keeps its coverage and texels (2015 -> BFII), no resampling
                TerrainMaskTransplant.ConvertReport crep;
                mask = TerrainMaskTransplant.ConvertStructure(sp.Resource.Mask, tm, out crep);
                built = string.Format(CultureInfo.InvariantCulture,
                    "converted subtile for subtile to L{0}, {1} nodes, {2} subtiles ({3} tiled, {4} full), {5:N0} border samples read " +
                    "from neighbours, {6} coarse root subtile(s) dropped, atlas {7}",
                    crep.MaxLevel, crep.Nodes, crep.Subtiles, crep.Tiled, crep.Full, crep.BorderSamples, crep.SourceRootSubtilesDropped, crep.Atlas);
            }
            else
            {
                if (maskLevel < 0)
                {
                    maskLevel = 0;
                    while (maskLevel < 16 && frame.Extent / (1 << maskLevel) / perSide / pot > sourceFinest * 1.01) maskLevel++;
                }
                var opts = TerrainMaskTransplant.OptionsLike(tm, maskLevel, tolerance, budget);
                MaskBuildReport brep;
                mask = MaskBuilder.Build(src, frame, opts, out brep);
                built = string.Format(CultureInfo.InvariantCulture,
                    "resampled: built to L{0} (asked L{1}), {2} nodes, {3} subtiles ({4} tiled, {5} full), atlas {6}",
                    brep.MaxLevelUsed, maskLevel, brep.Nodes, brep.Subtiles, brep.TiledSubtiles, brep.FullSubtiles, brep.Atlas);
            }
            TerrainMaskTransplant.FilterReport frep = null;
            if (combinations != null)
                frep = TerrainMaskTransplant.FilterCombinations(mask, combinations, protectLayer >= 0 ? new[] { protectLayer } : null);
            TerrainAssembler.ReplaceTree(target, mask);
            byte[] bytes = TerrainAssembler.Serialize(target);
            var check = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(bytes);
            if (!check.Success) throw new InvalidOperationException("the rebuilt terrain does not re-parse: " + check.Failure);
            note = string.Format(CultureInfo.InvariantCulture,
                "{0} source layer(s), source finest texel {1:0.###} m (L{2}); {3}; {4}",
                src.LayerCount, sourceFinest, src.DeepestLevel, built,
                frep == null ? "no layer-combination check (give --combinations)"
                    : frep.PatchesFixed + frep.PatchesUnresolved == 0 ? "all " + frep.PatchesChecked + " patches drawable by the target's " + combinations.Count + " combinations"
                    : (frep.PatchesFixed + frep.PatchesUnresolved) + " of " + frep.PatchesChecked + " patches had undrawable layer sets (" +
                      frep.PatchesUnresolved + " still undrawable" + (protectLayer >= 0 ? ", hole layer " + protectLayer + " protected" : "") + "), " +
                      frep.SubtilesDropped + " subtiles dropped: " + string.Join("; ", frep.FixedSets.Select(kv => kv.Key + " x" + kv.Value)));
            return bytes;
        }

        internal static List<int[]> ReadCombinations(byte[] bytes)
        {
            try { return TerrainMaskTransplant.ReadLayerCombinations(bytes); }
            catch (ArgumentException)
            {
                // a framed export carries the 16-byte res meta first
                if (bytes.Length <= 16) throw;
                byte[] bare = new byte[bytes.Length - 16];
                Array.Copy(bytes, 16, bare, 0, bare.Length);
                return TerrainMaskTransplant.ReadLayerCombinations(bare);
            }
        }

        private static bool ParseBand(string text, out double lo, out double hi)
        {
            lo = hi = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split(':');
            if (parts.Length != 2) return false;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lo)) return false;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out hi)) return false;
            return hi > lo;
        }
    }
}
