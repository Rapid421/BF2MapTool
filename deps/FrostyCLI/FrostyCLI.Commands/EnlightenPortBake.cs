using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Texture;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten port-bake`: give a BFII level the Enlighten bake of the SWBF 2015 level it was ported from - 2015's own
    /// lightmap charts, terrain tiles, probe sets and probe SH, not a relight. Both games ship the same two containers
    /// (memory enlighten-cross-game-port); what changes on the way:
    /// <list type="bullet">
    /// <item>mesh-instance keys: 2015 hashes {2015 mesh guid, 10 ints}, BFII {mesh guid, 11 ints}. Every static placement
    /// of the level is keyed twice - as 2015 would (its 2015 mesh guid from --mesh-map) to find its 2015 chart, and as BFII
    /// will (its own mesh guid) for the record written;</item>
    /// <item>a mesh whose lightmap uvs are an axis-aligned affine map of 2015's (DICE re-packed the uv islands of the meshes
    /// it kept) gets the chart's uv transform rewritten for its own uvs (--mesh-map "u"/"v"); a mesh whose uvs are not
    /// (or whose geometry differs) must be given 2015's uvs first (mesh radiosity-apply-lods, a level-owned clone) or is
    /// mapped "chart": false and lit by the probes;</item>
    /// <item>probe sets: positions move from +0xD8 to +0xD0 (BFII static layout); 2015's set order is kept, so 2015's
    /// default probe (set 0, probe 0 = its DistantLightprobePosition) stays the default;</item>
    /// <item>units: irradiance and probe SH x --scale (2000: DICE's own 2015 -> BFII factor for this VE); sky visibility
    /// and directions are unitless;</item>
    /// <item>atlases: irradiance R9G9B9E5 (2015's own format, no block compression), direction 2015's single RGBA8 map
    /// written to all three of BFII's R/G/B direction textures (2015 stores one radiance-weighted direction for all
    /// channels; BFII's per-channel SH L1 shading with three equal directions is that), sky visibility 2015's BC4 value
    /// in .rgb with .a (BFII's baked AO, which 2015 does not have) 255.</item>
    /// </list>
    /// The atlas is 2015's, texel for texel, at the origin (so a terrain patch no record covers samples 2015's texel 0,0 as
    /// in 2015); charts the level keeps from the host bake (--keep-host) are copied into a strip below it. Every placement
    /// in an --edit partition gets the RadiosityTypeOverride that reproduces 2015: None where it has a record, else 2015's
    /// type (a Static mesh without a record draws black indirect, SPEC.md 5, so those become Dynamic).
    /// </summary>
    public static class EnlightenPortBake
    {
        sealed class MeshMap
        {
            public Guid Guid15;
            public double A = 1, B = 0, C = 1, D = 0;   // our lightmap uv = (A u15 + B, C v15 + D)
            public bool Chart = true;
            public string Type15;                       // the 2015 mesh's EnlightenType, e.g. "EnlightenType_Static"
        }

        sealed class HostChart
        {
            public uint Key;
            public float Su, Sv, Ou, Ov;                // in the host atlas
            public int X0, Y0, X1, Y1;                  // texel rect copied (with margin)
            public int Nx, Ny;                          // its place in the output atlas
        }

        const string OvNone = "RadiosityTypeOverride_None";

        public static int Execute(string[] args, ExportLogger logger)
        {
            string level = null, endPath = null, flsPath = null, irrPath = null, dirPath = null, skyPath = null, mapPath = null;
            string onlyBakes = null, cloneInto = null, outDir = null, recordsAtPath = null;
            double matchTol = 0.05;
            double scale = 2000, belowY = -1000;
            int depth = 6;
            bool import = true;
            var edit = new List<string>();
            var keepHost = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--end" && i + 1 < args.Length) endPath = args[++i];
                else if (a == "--fls" && i + 1 < args.Length) flsPath = args[++i];
                else if (a == "--irradiance" && i + 1 < args.Length) irrPath = args[++i];
                else if (a == "--direction" && i + 1 < args.Length) dirPath = args[++i];
                else if (a == "--sky" && i + 1 < args.Length) skyPath = args[++i];
                else if (a == "--mesh-map" && i + 1 < args.Length) mapPath = args[++i];
                else if (a == "--scale" && i + 1 < args.Length) scale = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--bakes" && i + 1 < args.Length) onlyBakes = args[++i];
                else if (a == "--clone-into" && i + 1 < args.Length) cloneInto = args[++i].TrimEnd('/');
                else if (a == "--edit" && i + 1 < args.Length) edit.Add(args[++i]);
                else if (a == "--keep-host" && i + 1 < args.Length) keepHost.Add(args[++i]);
                else if (a == "--records-at" && i + 1 < args.Length) recordsAtPath = args[++i];
                else if (a == "--match-tolerance" && i + 1 < args.Length) matchTol = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--host-probes-below" && i + 1 < args.Length) belowY = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--depth" && i + 1 < args.Length) depth = int.Parse(args[++i]);
                else if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (a == "--no-import") import = false;
                else if (!a.StartsWith("--")) level = a;
                else { Console.Error.WriteLine("enlighten port-bake: unknown option " + a); return 1; }
            }
            if (level == null || endPath == null || flsPath == null || irrPath == null || dirPath == null || skyPath == null || mapPath == null)
            {
                Console.Error.WriteLine("Usage: frostycli enlighten port-bake <level> --end <2015 END .framed.bin> --fls <2015 FLS .framed.bin> " +
                    "--irradiance <2015 irradiance .dds> --direction <2015 direction .dds> --sky <2015 sky visibility .dds> --mesh-map <json> " +
                    "--clone-into <asset prefix> [--bakes FL_a,FL_b] [--scale 2000] [--edit <partition substring>]... " +
                    "[--keep-host <partition substring>]... [--host-probes-below -1000] [--depth 6] [--out dir] [--no-import]");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("enlighten port-bake: AssetManager not initialized."); return 1; }
            if (import && cloneInto == null) { Console.Error.WriteLine("enlighten port-bake: --clone-into is required to write (the host level's shipped bake stays untouched)"); return 1; }
            outDir = outDir ?? Path.Combine(Path.GetTempPath(), "frostycli-portbake-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(outDir);

            // ---- the 2015 bake
            byte[] meta;
            EnlightenDatabase src = Swbf1Enlighten.ParseEnd(Swbf1Enlighten.Unframe(File.ReadAllBytes(endPath), out meta));
            uint fw, fh;
            byte[] probes15 = Swbf1Enlighten.ReadProbes(Swbf1Enlighten.Unframe(File.ReadAllBytes(flsPath), out meta), out fw, out fh);
            long setProbes = src.ProbeSets.Sum(ps => (long)ps.Count);
            if (probes15.Length / 144 != setProbes) { Console.Error.WriteLine("enlighten port-bake: the FLS has " + probes15.Length / 144 + " probes, the END's sets " + setProbes); return 1; }
            int W0 = (int)src.Width, H0 = (int)src.Height;
            if (fw != src.Width || fh != src.Height) { Console.Error.WriteLine("enlighten port-bake: FLS atlas " + fw + "x" + fh + " != END atlas " + W0 + "x" + H0); return 1; }
            float[] irr15; byte[] dir15, sky15;
            string terr = ReadSourceAtlases(irrPath, dirPath, skyPath, W0, H0, scale, out irr15, out dir15, out sky15);
            if (terr != null) { Console.Error.WriteLine("enlighten port-bake: " + terr); return 1; }
            var rec15 = new Dictionary<uint, int>();
            for (int i = 0; i < src.MeshInstances.Count; i++) rec15[src.MeshInstances[i].Hash] = i;
            foreach (var mi in src.MeshInstances)
                foreach (byte[] uv in mi.UvTransforms)
                    if (BitConverter.ToSingle(uv, 4) != 0 || BitConverter.ToSingle(uv, 8) != 0)
                    { Console.Error.WriteLine("enlighten port-bake: 2015 record " + mi.Hash.ToString("x8") + " has a rotated uv transform; not modelled"); return 1; }
            Console.Out.WriteLine(string.Format("2015 bake: atlas {0}x{1}, {2} charts, {3} terrain tiles, {4} probe sets, {5} probes (x{6})",
                W0, H0, src.MeshInstances.Count, src.Terrain.Count, src.ProbeSets.Count, setProbes, scale.ToString(CultureInfo.InvariantCulture)));

            Dictionary<string, MeshMap> map;
            string merr = LoadMeshMap(mapPath, out map);
            if (merr != null) { Console.Error.WriteLine("enlighten port-bake: " + merr); return 1; }

            // ---- the level's bakes
            List<EnlightenRelight.Bake> bakes = EnlightenRelight.FindBakes(level, logger);
            if (onlyBakes != null)
            {
                var keep = new HashSet<string>(onlyBakes.Split(','), StringComparer.OrdinalIgnoreCase);
                bakes = bakes.Where(b => keep.Contains(Path.GetFileName(b.Static))).ToList();
            }
            if (bakes.Count == 0) { Console.Error.WriteLine("enlighten port-bake: no Enlighten bakes found under " + level); return 1; }
            foreach (var b in bakes)
            {
                if (!EnlightenRelight.LoadBake(b)) return 1;
                if (b.Fls == null) { Console.Error.WriteLine("enlighten port-bake: " + b.Static + " has no modelled static database"); return 1; }
            }
            if (import)
                foreach (var b in bakes)
                    if (!EnlightenRelight.CloneBakeInto(b, cloneInto, logger)) return 1;

            // ---- the level's static placements
            var placements = new List<EnlightenKeys.Placement>();
            var partitions = new List<string>();
            EnlightenKeys.Walk(level, depth, (name, dump) => { partitions.Add(name); placements.AddRange(EnlightenKeys.Collect(name, dump)); });
            placements = placements.GroupBy(p => p.Source + "|" + p.MeshPath + "|" + p.Key + "|" + p.GroupGuid + "|" + p.Member + "|" + p.Instance + "|" + p.ObjectGuid)
                                   .Select(g => g.First()).ToList();
            Console.Out.WriteLine(string.Format("level: {0} partition(s), {1} static mesh placement(s)", partitions.Count, placements.Count));

            // ---- 2015 placements with their records (--records-at): our transforms may be rounded copies of 2015's
            // (the census wrote rotations to 4 decimals), so a key recomputed from them can miss; the same mesh within
            // --match-tolerance of a 2015 placement that has a record takes that record
            var at15 = new Dictionary<Guid, List<Tuple<double[][], int>>>();
            if (recordsAtPath != null)
            {
                foreach (JObject r in JArray.Parse(File.ReadAllText(recordsAtPath)))
                {
                    Guid g = new Guid((string)r["guid15"]);
                    Func<string, double[]> v3 = k => ((JArray)r[k]).Select(x => (double)x).ToArray();
                    int ri;
                    uint key = (uint)r["key15"];
                    if (!rec15.TryGetValue(key, out ri)) continue;
                    List<Tuple<double[][], int>> l;
                    if (!at15.TryGetValue(g, out l)) at15[g] = l = new List<Tuple<double[][], int>>();
                    l.Add(Tuple.Create(new[] { v3("right"), v3("up"), v3("forward"), v3("trans") }, ri));
                }
                Console.Out.WriteLine(string.Format("2015 placements with records: {0} ({1} meshes)", at15.Values.Sum(l => l.Count), at15.Count));
            }
            Func<MeshMap, EnlightenKeys.Placement, int> nearRecord = (mm, p) =>
            {
                List<Tuple<double[][], int>> l;
                if (!at15.TryGetValue(mm.Guid15, out l)) return -1;
                int best = -1; double bestD = double.MaxValue;
                foreach (var c in l)
                {
                    double dt = Math.Sqrt(Sq(c.Item1[3][0] - p.Trans[0]) + Sq(c.Item1[3][1] - p.Trans[1]) + Sq(c.Item1[3][2] - p.Trans[2]));
                    if (dt > matchTol) continue;
                    double dr = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        double[] ours = k == 0 ? p.Right : k == 1 ? p.Up : p.Forward;
                        for (int j = 0; j < 3; j++) dr = Math.Max(dr, Math.Abs(c.Item1[k][j] - ours[j]));
                    }
                    if (dr > 0.01) continue;
                    if (dt < bestD) { bestD = dt; best = c.Item2; }
                }
                return best;
            };

            // ---- 2015 charts for our placements
            var chart15 = new Dictionary<uint, Tuple<int, MeshMap>>();     // our key -> 2015 record, uv map
            int exactHits = 0, nearHits = 0;
            var lit = new HashSet<EnlightenKeys.Placement>();
            var hitRecords = new HashSet<int>();
            int conflicts = 0;
            var perMesh = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);    // placements, charted, 2015 hits with chart false
            foreach (var p in placements)
            {
                MeshMap mm;
                if (!map.TryGetValue(p.MeshPath.ToLowerInvariant(), out mm)) continue;
                int[] pm;
                if (!perMesh.TryGetValue(p.MeshPath, out pm)) perMesh[p.MeshPath] = pm = new int[3];
                pm[0]++;
                uint k15 = LightmapKey.Compute(mm.Guid15, p.Right, p.Up, p.Forward, p.Trans, 10);
                int ri;
                bool exact = rec15.TryGetValue(k15, out ri);
                if (!exact) ri = nearRecord(mm, p);
                if (ri < 0) continue;
                if (!mm.Chart) { pm[2]++; continue; }
                if (exact) exactHits++; else nearHits++;
                Tuple<int, MeshMap> prev;
                if (chart15.TryGetValue(p.Key, out prev) && prev.Item1 != ri) { conflicts++; continue; }
                chart15[p.Key] = Tuple.Create(ri, mm);
                lit.Add(p);
                hitRecords.Add(ri);
                pm[1]++;
            }
            Console.Out.WriteLine(string.Format("charts: {0} placement(s) take one of {1} 2015 chart(s) ({2} of the 2015 bake's {3} records used; {4} by key, {5} by position){6}",
                lit.Count, chart15.Count, hitRecords.Count, src.MeshInstances.Count, exactHits, nearHits, conflicts > 0 ? "; " + conflicts + " key conflict(s) skipped" : ""));
            foreach (var kv in perMesh.OrderByDescending(k => k.Value[1]))
                if (kv.Value[1] > 0 || kv.Value[2] > 0)
                    Console.Out.WriteLine(string.Format("   {0,4} of {1,4} charted{2}  {3}", kv.Value[1], kv.Value[0],
                        kv.Value[2] > 0 ? string.Format(" ({0} have a 2015 chart but the mesh map says no)", kv.Value[2]) : "", kv.Key));

            // ---- per bake: layout, databases, atlases
            var report = new JObject { ["charts2015"] = chart15.Count, ["placementsCharted"] = lit.Count, ["recordsUsed"] = hitRecords.Count, ["conflicts"] = conflicts };
            var bakeReports = new JArray();
            var hostLit = new HashSet<EnlightenKeys.Placement>();
            int failures = 0;
            foreach (var b in bakes)
            {
                var br = new JObject { ["static"] = b.Static, ["dynamic"] = b.Dynamic };
                // host charts the level keeps (placements in --keep-host partitions with a record in this host bake)
                var hostRecords = new Dictionary<uint, EnlightenDatabase.MeshInstance>();
                foreach (var mi in b.Db.MeshInstances) hostRecords[mi.Hash] = mi;
                var hostCharts = new Dictionary<uint, HostChart>();
                foreach (var p in placements)
                {
                    if (lit.Contains(p) || !keepHost.Any(s => p.Partition.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    EnlightenDatabase.MeshInstance hm;
                    if (!hostRecords.TryGetValue(p.Key, out hm) || hm.UvTransforms.Count == 0 || chart15.ContainsKey(p.Key)) continue;
                    hostLit.Add(p);
                    if (!hostCharts.ContainsKey(p.Key))
                        hostCharts[p.Key] = new HostChart { Key = p.Key, Su = hm.ScaleU(), Sv = hm.ScaleV(), Ou = hm.OffsetU(), Ov = hm.OffsetV() };
                }
                int W, H;
                LayoutHostCharts(hostCharts.Values.ToList(), (int)b.Db.Width, (int)b.Db.Height, W0, H0, out W, out H);
                double kx = (double)W0 / W, ky = (double)H0 / H;

                // END: the host's header, 2015's records re-keyed, the host charts, 2015's terrain and probe sets
                var end = new EnlightenDatabase { Header = (byte[])b.Db.Header.Clone(), CookieFlags = b.Db.CookieFlags };
                end.Width = (uint)W; end.Height = (uint)H;
                foreach (var kv in chart15)
                {
                    byte[] uv = (byte[])src.MeshInstances[kv.Value.Item1].UvTransforms[0].Clone();
                    MeshMap mm = kv.Value.Item2;
                    double su = BitConverter.ToSingle(uv, 0) * kx, sv = BitConverter.ToSingle(uv, 12) * ky;
                    double ou = BitConverter.ToSingle(uv, 16) * kx, ov = BitConverter.ToSingle(uv, 20) * ky;
                    // our uv = A u15 + B  ->  atlas = u15 su + ou = uv (su / A) + (ou - B su / A)
                    PutF(uv, 0, su / mm.A); PutF(uv, 16, ou - mm.B * su / mm.A);
                    PutF(uv, 12, sv / mm.C); PutF(uv, 20, ov - mm.D * sv / mm.C);
                    var mi = new EnlightenDatabase.MeshInstance { Hash = kv.Key };
                    mi.UvTransforms.Add(uv);
                    end.MeshInstances.Add(mi);
                }
                foreach (HostChart hc in hostCharts.Values)
                {
                    int hw = (int)b.Db.Width, hh = (int)b.Db.Height;
                    var uv = new byte[32];
                    PutF(uv, 0, hc.Su * (double)hw / W); PutF(uv, 12, hc.Sv * (double)hh / H);
                    PutF(uv, 16, (hc.Ou * (double)hw - hc.X0 + hc.Nx) / W); PutF(uv, 20, (hc.Ov * (double)hh - hc.Y0 + hc.Ny) / H);
                    var mi = new EnlightenDatabase.MeshInstance { Hash = hc.Key };
                    mi.UvTransforms.Add(uv);
                    end.MeshInstances.Add(mi);
                }
                end.MeshInstances.Sort((x, y) => x.Hash.CompareTo(y.Hash));
                foreach (var t in src.Terrain)
                {
                    var tl = new EnlightenDatabase.TerrainLightmap { Raw = (byte[])t.Raw.Clone() };
                    tl.ScaleU = (float)(t.ScaleU * kx); tl.OffsetU = (float)(t.OffsetU * kx);
                    tl.ScaleV = (float)(t.ScaleV * ky); tl.OffsetV = (float)(t.OffsetV * ky);
                    end.Terrain.Add(tl);
                }
                // probe sets: 2015's in 2015's order, then the host's sets that sit wholly under the map (spawn screen,
                // end-of-round room) with their SH from the host's static database
                var fls = new StaticEnlightenDatabase { Header = (byte[])b.Fls.Header.Clone(), CookieWord0 = b.Fls.CookieWord0 };
                var probeBytes = new MemoryStream();
                uint offset = 0;
                foreach (var ps in src.ProbeSets)
                {
                    var o = new EnlightenDatabase.ProbeSet { Raw = (byte[])ps.Raw.Clone(), Positions = (byte[])ps.Positions.Clone() };
                    int first = (int)ps.GlobalOffset;
                    o.GlobalOffset = offset;
                    for (int k = 0; k < ps.Count; k++) probeBytes.Write(ScaleProbe(probes15, first + k, scale), 0, 144);
                    offset += ps.Count;
                    end.ProbeSets.Add(o);
                }
                int keptSets = 0, keptProbes = 0;
                foreach (var ps in b.Db.ProbeSets)
                {
                    bool under = ps.Count > 0;
                    for (int k = 0; k < ps.Count && under; k++) { float x, y, z; ps.Position(k, out x, out y, out z); under = y < belowY; }
                    if (!under) continue;
                    var o = new EnlightenDatabase.ProbeSet { Raw = (byte[])ps.Raw.Clone(), Positions = (byte[])ps.Positions.Clone() };
                    o.GlobalOffset = offset;
                    probeBytes.Write(b.Fls.Probes, (int)ps.GlobalOffset * 144, (int)ps.Count * 144);
                    offset += ps.Count;
                    end.ProbeSets.Add(o);
                    keptSets++; keptProbes += (int)ps.Count;
                }
                fls.Probes = probeBytes.ToArray();
                fls.Width = (uint)W; fls.Height = (uint)H;

                // atlases: 2015's at the origin, the host charts in the strip below
                float[] irr = new float[(long)W * H * 3];
                byte[] dirR = new byte[W * H * 4], dirG = new byte[W * H * 4], dirB = new byte[W * H * 4], sky = new byte[W * H * 4];
                for (int y = 0; y < H0; y++)
                {
                    Array.Copy(irr15, (long)y * W0 * 3, irr, (long)y * W * 3, W0 * 3);
                    Array.Copy(dir15, y * W0 * 4, dirR, y * W * 4, W0 * 4);
                    Array.Copy(dir15, y * W0 * 4, dirG, y * W * 4, W0 * 4);
                    Array.Copy(dir15, y * W0 * 4, dirB, y * W * 4, W0 * 4);
                    Array.Copy(sky15, y * W0 * 4, sky, y * W * 4, W0 * 4);
                }
                if (hostCharts.Count > 0)
                {
                    string cerr = CopyHostCharts(b, hostCharts.Values.ToList(), outDir, W, irr, dirR, dirG, dirB, sky);
                    if (cerr != null) { Console.Error.WriteLine("enlighten port-bake: " + cerr); return 1; }
                }

                // write
                string stem = Path.Combine(outDir, Path.GetFileName(b.Static));
                byte[] endMeta, endData = end.Write(out endMeta);
                byte[] flsMeta, flsData = fls.Write(out flsMeta);
                File.WriteAllBytes(stem + ".end.framed.bin", EnlightenRelight.Frame(endMeta, endData));
                File.WriteAllBytes(stem + ".fls.framed.bin", EnlightenRelight.Frame(flsMeta, flsData));
                // self-check: what was written parses back to the same model
                var back = EnlightenDatabase.Parse(endData, endMeta);
                if (back.MeshInstances.Count != end.MeshInstances.Count || back.Terrain.Count != end.Terrain.Count || back.ProbeSets.Count != end.ProbeSets.Count)
                { Console.Error.WriteLine("enlighten port-bake: the written END does not parse back"); return 1; }
                var irrTexels = new byte[W * H * 4];
                for (int t = 0; t < W * H; t++)
                    BitConverter.GetBytes(AtlasIo.EncodeR9G9B9E5(irr[t * 3], irr[t * 3 + 1], irr[t * 3 + 2])).CopyTo(irrTexels, t * 4);
                var files = new Dictionary<string, byte[]>
                {
                    { "StaticIrradianceTexture", AtlasIo.WriteDds32(W, H, 67, irrTexels) },
                    { "StaticDirectionTexture", AtlasIo.WriteDds32(W, H, 28, dirR) },
                    { "StaticDirectionTextureG", AtlasIo.WriteDds32(W, H, 28, dirG) },
                    { "StaticDirectionTextureB", AtlasIo.WriteDds32(W, H, 28, dirB) },
                    { "StaticSkyVisibilityTexture", AtlasIo.WriteDds32(W, H, 28, sky) },
                };
                foreach (var f in files) File.WriteAllBytes(stem + "." + f.Key + ".dds", f.Value);
                Console.Out.WriteLine(string.Format("bake {0}: atlas {1}x{2}; {3} records ({4} 2015, {5} kept from the host), {6} terrain tiles, {7} probe sets / {8} probes ({9} sets / {10} probes kept from the host under y {11})",
                    Path.GetFileName(b.Static), W, H, end.MeshInstances.Count, chart15.Count, hostCharts.Count, end.Terrain.Count, end.ProbeSets.Count,
                    fls.ProbeCount, keptSets, keptProbes, belowY.ToString(CultureInfo.InvariantCulture)));
                br["atlas"] = new JArray(W, H); br["records"] = end.MeshInstances.Count; br["hostCharts"] = hostCharts.Count;
                br["probeSets"] = end.ProbeSets.Count; br["probes"] = fls.ProbeCount; br["hostProbeSets"] = keptSets;
                bakeReports.Add(br);
                if (!import) continue;
                try
                {
                    App.AssetManager.ModifyRes(b.DbRes.Name, endData, endMeta);
                    App.AssetManager.ModifyRes(b.FlsRes.Name, flsData, flsMeta);
                }
                catch (Exception ex) { Console.Error.WriteLine("enlighten port-bake: writing the databases of " + b.Static + ": " + ex.Message); return 1; }
                foreach (var f in files)
                {
                    string ebx = b.Textures[f.Key];
                    string ierr = HeadlessTextureImporter.Import(App.AssetManager.GetEbxEntry(ebx), stem + "." + f.Key + ".dds", "DDS", false);
                    if (ierr != null) { Console.Error.WriteLine("enlighten port-bake: import " + ebx + ": " + ierr); failures++; }
                    else Console.Out.WriteLine("  imported " + ebx);
                }
                // 2015's default probe position (set 0, probe 0) on the level's own EnlightenDataAsset
                float dx, dy, dz; src.ProbeSets[0].Position(0, out dx, out dy, out dz);
                string perr = SetDistantProbe(b.Dynamic, dx, dy, dz, logger);
                if (perr != null) { Console.Error.WriteLine("enlighten port-bake: " + perr); failures++; }
            }
            report["bakes"] = bakeReports;

            // ---- the RadiosityTypeOverride of every placement in the --edit partitions
            if (edit.Count > 0)
            {
                var want = new Dictionary<EnlightenKeys.Placement, string>();
                var notStatic = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in placements)
                {
                    if (!edit.Any(s => p.Partition.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (p.Source == "prefab") continue;
                    string cur = string.IsNullOrEmpty(p.Override) ? OvNone : p.Override;
                    string ours = MeshType(p.MeshPath);
                    string target;
                    if (lit.Contains(p) || hostLit.Contains(p))
                    {
                        target = OvNone;
                        if (ours != "EnlightenType_Static") { int n; notStatic.TryGetValue(p.MeshPath, out n); notStatic[p.MeshPath] = n + 1; }
                    }
                    else if (cur != OvNone) target = cur;
                    else
                    {
                        MeshMap mm;
                        string t15 = map.TryGetValue(p.MeshPath.ToLowerInvariant(), out mm) && mm.Type15 != null ? mm.Type15 : ours;
                        if (t15 == "EnlightenType_Static" || t15 == null) target = "RadiosityTypeOverride_Dynamic";   // no record: probes, not black
                        else if (t15 != ours) target = "RadiosityTypeOverride_" + t15.Substring("EnlightenType_".Length);
                        else target = OvNone;
                    }
                    if (target != cur) want[p] = target;
                }
                int changed;
                string eerr = ApplyOverrides(want, partitions, import, logger, out changed);
                if (eerr != null) { Console.Error.WriteLine("enlighten port-bake: " + eerr); return 1; }
                Console.Out.WriteLine(string.Format("overrides: {0} placement(s) {1} ({2})", want.Count, import ? "changed" : "would change",
                    string.Join(", ", want.GroupBy(kv => kv.Value).Select(g => g.Count() + " -> " + g.Key.Replace("RadiosityTypeOverride_", "")))));
                foreach (var kv in notStatic)
                    Console.Out.WriteLine(string.Format("warning: {0} charted placement(s) of {1}, whose EnlightenType is {2}: only a Static mesh samples its chart",
                        kv.Value, kv.Key, MeshType(kv.Key)));
                report["overridesChanged"] = want.Count;
                report["chartedButNotStatic"] = JObject.FromObject(notStatic);
            }
            File.WriteAllText(Path.Combine(outDir, "port-bake.json"), report.ToString());
            Console.Out.WriteLine("enlighten port-bake: " + (failures == 0 ? "done" : failures + " failure(s)") + "; files in " + outDir);
            return failures == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- inputs

        static string LoadMeshMap(string path, out Dictionary<string, MeshMap> map)
        {
            map = new Dictionary<string, MeshMap>(StringComparer.OrdinalIgnoreCase);
            JObject j;
            try { j = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { return "mesh map " + path + ": " + ex.Message; }
            foreach (var kv in j)
            {
                var o = kv.Value as JObject;
                if (o == null || o["guid15"] == null) return "mesh map entry " + kv.Key + " has no guid15";
                var m = new MeshMap { Guid15 = new Guid((string)o["guid15"]), Chart = o["chart"] == null || (bool)o["chart"], Type15 = (string)o["type15"] };
                if (o["u"] is JArray u) { m.A = (double)u[0]; m.B = (double)u[1]; }
                if (o["v"] is JArray v) { m.C = (double)v[0]; m.D = (double)v[1]; }
                if (Math.Abs(m.A) < 1e-9 || Math.Abs(m.C) < 1e-9) return "mesh map entry " + kv.Key + " has a zero uv scale";
                map[kv.Key.ToLowerInvariant()] = m;
            }
            return null;
        }

        /// <summary>The 2015 atlases as BFII texels: irradiance float RGB x scale, direction RGBA8 (alpha 255), sky visibility
        /// in RGB (alpha 255).</summary>
        static string ReadSourceAtlases(string irrPath, string dirPath, string skyPath, int w, int h, double scale,
                                        out float[] irr, out byte[] dir, out byte[] sky)
        {
            irr = null; dir = null; sky = null;
            int dw, dh; uint dxgi;
            byte[] b = File.ReadAllBytes(irrPath);
            int off = AtlasIo.DdsDataOffset(b, out dw, out dh, out dxgi);
            if (dxgi != 67 || dw != w || dh != h) return irrPath + ": expected an R9G9B9E5 (67) " + w + "x" + h + " DDS, got " + dxgi + " " + dw + "x" + dh;
            irr = new float[(long)w * h * 3];
            for (int t = 0; t < w * h; t++)
            {
                float r, g, bl;
                AtlasIo.DecodeR9G9B9E5(BitConverter.ToUInt32(b, off + t * 4), out r, out g, out bl);
                irr[t * 3] = (float)(r * scale); irr[t * 3 + 1] = (float)(g * scale); irr[t * 3 + 2] = (float)(bl * scale);
            }
            b = File.ReadAllBytes(dirPath);
            off = AtlasIo.DdsDataOffset(b, out dw, out dh, out dxgi);
            if (dxgi != 28 || dw != w || dh != h) return dirPath + ": expected an R8G8B8A8 (28) " + w + "x" + h + " DDS, got " + dxgi + " " + dw + "x" + dh;
            dir = new byte[w * h * 4];
            Array.Copy(b, off, dir, 0, dir.Length);
            for (int t = 0; t < w * h; t++) dir[t * 4 + 3] = 255;
            b = File.ReadAllBytes(skyPath);
            off = AtlasIo.DdsDataOffset(b, out dw, out dh, out dxgi);
            if (dxgi != 80 || dw != w || dh != h) return skyPath + ": expected a BC4 (80) " + w + "x" + h + " DDS, got " + dxgi + " " + dw + "x" + dh;
            byte[] s = AtlasIo.DecodeBc4(b, off, w, h);
            sky = new byte[w * h * 4];
            // BFII's static sky-visibility texture carries the sky visibility in RGB and a cosine-weighted ambient
            // occlusion in A (DICE's Hoth bake: R=G=B, A a separate term correlated 0.69 with it; relight writes its own AO
            // there). 2015's bake has one occlusion term only -- this sky visibility -- so it fills both; an A of 255 told
            // the renderer that nothing is occluded (trench bottoms, contact areas and crevices lost their darkening).
            for (int t = 0; t < w * h; t++) { sky[t * 4] = sky[t * 4 + 1] = sky[t * 4 + 2] = s[t]; sky[t * 4 + 3] = s[t]; }
            return null;
        }

        static byte[] ScaleProbe(byte[] probes, int index, double scale)
        {
            var r = new byte[144];
            Array.Copy(probes, index * 144, r, 0, 144);
            for (int k = 0; k < 9; k++)
                for (int c = 0; c < 3; c++)     // RGB SH; .w (sky visibility SH) is unitless
                {
                    int o = k * 16 + c * 4;
                    PutF(r, o, BitConverter.ToSingle(r, o) * scale);
                }
            return r;
        }

        // ---------------------------------------------------------------- host charts

        /// <summary>Texel rects (2-texel margin, so bilinear taps at a chart's edge read what they read in the host
        /// atlas) packed in rows under the 2015 atlas; 4-aligned places, output size a multiple of 4.</summary>
        static void LayoutHostCharts(List<HostChart> charts, int hw, int hh, int w0, int h0, out int W, out int H)
        {
            W = w0; H = h0;
            if (charts.Count == 0) return;
            const int m = 2;
            foreach (HostChart c in charts)
            {
                double xa = c.Ou * hw, xb = (c.Ou + c.Su) * hw, ya = c.Ov * hh, yb = (c.Ov + c.Sv) * hh;
                c.X0 = Math.Max(0, (int)Math.Floor(Math.Min(xa, xb) - 1e-4) - m); c.X1 = Math.Min(hw, (int)Math.Ceiling(Math.Max(xa, xb) + 1e-4) + m);
                c.Y0 = Math.Max(0, (int)Math.Floor(Math.Min(ya, yb) - 1e-4) - m); c.Y1 = Math.Min(hh, (int)Math.Ceiling(Math.Max(ya, yb) + 1e-4) + m);
            }
            W = Math.Max(w0, (charts.Max(c => c.X1 - c.X0) + 3) & ~3);
            int x = 0, y = h0, rowH = 0;
            foreach (HostChart c in charts.OrderByDescending(c => c.Y1 - c.Y0))
            {
                int cw = (c.X1 - c.X0 + 3) & ~3, ch = (c.Y1 - c.Y0 + 3) & ~3;
                if (x + cw > W) { x = 0; y += rowH; rowH = 0; }
                c.Nx = x; c.Ny = y;
                x += cw; rowH = Math.Max(rowH, ch);
            }
            H = (y + rowH + 3) & ~3;
        }

        static string CopyHostCharts(EnlightenRelight.Bake b, List<HostChart> charts, string outDir, int W,
                                     float[] irr, byte[] dirR, byte[] dirG, byte[] dirB, byte[] sky)
        {
            try
            {
                int w, h;
                float[] hIrr = AtlasIo.ReadHdr(EnlightenRelight.ExportTexture(b.Textures["StaticIrradianceTexture"], "hdr", outDir), out w, out h);
                byte[] hR = AtlasIo.ReadRgba(EnlightenRelight.ExportTexture(b.Textures["StaticDirectionTexture"], "png", outDir), out w, out h);
                byte[] hG = AtlasIo.ReadRgba(EnlightenRelight.ExportTexture(b.Textures["StaticDirectionTextureG"], "png", outDir), out w, out h);
                byte[] hB = AtlasIo.ReadRgba(EnlightenRelight.ExportTexture(b.Textures["StaticDirectionTextureB"], "png", outDir), out w, out h);
                byte[] hS = AtlasIo.ReadRgba(EnlightenRelight.ExportTexture(b.Textures["StaticSkyVisibilityTexture"], "png", outDir), out w, out h);
                foreach (HostChart c in charts)
                    for (int y = c.Y0; y < c.Y1; y++)
                        for (int x = c.X0; x < c.X1; x++)
                        {
                            int s = y * w + x, d = (c.Ny + y - c.Y0) * W + c.Nx + x - c.X0;
                            for (int k = 0; k < 3; k++) irr[(long)d * 3 + k] = hIrr[(long)s * 3 + k];
                            for (int k = 0; k < 4; k++) { dirR[d * 4 + k] = hR[s * 4 + k]; dirG[d * 4 + k] = hG[s * 4 + k]; dirB[d * 4 + k] = hB[s * 4 + k]; sky[d * 4 + k] = hS[s * 4 + k]; }
                        }
                return null;
            }
            catch (Exception ex) { return "reading the host atlases of " + b.Static + ": " + ex.Message; }
        }

        // ---------------------------------------------------------------- EBX

        static readonly Dictionary<string, string> MeshTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string MeshType(string mesh)
        {
            string t;
            if (MeshTypes.TryGetValue(mesh, out t)) return t;
            t = null;
            try
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(mesh);
                object root = e == null ? null : App.AssetManager.GetEbx(e).RootObject;
                t = root?.GetType().GetProperty("EnlightenType")?.GetValue(root)?.ToString();
            }
            catch { }
            MeshTypes[mesh] = t;
            return t;
        }

        static string SetDistantProbe(string dynamicAsset, float x, float y, float z, ExportLogger logger)
        {
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(dynamicAsset);
            if (e == null) return "no " + dynamicAsset;
            EbxAsset asset = App.AssetManager.GetEbx(e);
            try
            {
                PropertyPathWriter.SetValue(asset.RootObject, "DistantLightprobePosition.x", x.ToString("R", CultureInfo.InvariantCulture));
                PropertyPathWriter.SetValue(asset.RootObject, "DistantLightprobePosition.y", y.ToString("R", CultureInfo.InvariantCulture));
                PropertyPathWriter.SetValue(asset.RootObject, "DistantLightprobePosition.z", z.ToString("R", CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { return "DistantLightprobePosition on " + dynamicAsset + ": " + ex.Message; }
            return EbxWriteCommand.CallModifyEbx(dynamicAsset, asset, logger) ? null : "could not write " + dynamicAsset;
        }

        /// <summary>Set each placement's override: a group instance's InstanceRadiosityTypeOverride entry (the list is
        /// padded to InstanceCount with None first), an object reference's RadiosityTypeOverride. The placing object is
        /// found by exported instance guid (anywhere in the level), or -- not exported -- by InternalId in the asset the
        /// placement was collected from.</summary>
        static string ApplyOverrides(Dictionary<EnlightenKeys.Placement, string> want, List<string> partitions, bool write, ExportLogger logger, out int changed)
        {
            changed = 0;
            if (!write || want.Count == 0) return null;
            Func<EnlightenKeys.Placement, string> owner = p => p.Source == "group"
                ? (p.GroupGuid != Guid.Empty ? "g:" + p.GroupGuid : "i:" + p.Partition.ToLowerInvariant() + "|" + p.GroupId)
                : (p.ObjectGuid != Guid.Empty ? "g:" + p.ObjectGuid : "i:" + p.Partition.ToLowerInvariant() + "|" + p.ObjectId);
            var byOwner = want.Where(kv => kv.Key.Source == "group" || kv.Key.Source == "object")
                              .GroupBy(kv => owner(kv.Key)).ToDictionary(g => g.Key, g => g.ToList());
            var pending = new HashSet<string>(byOwner.Keys);
            var order = want.Keys.Select(p => p.Partition).Concat(partitions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string name in order)
            {
                if (pending.Count == 0) break;
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(name);
                if (entry == null) continue;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch (Exception ex) { return "load " + name + ": " + ex.Message; }
                int before = changed;
                foreach (object obj in asset.Objects)
                {
                    Guid g = Guid.Empty; int id = -1;
                    try
                    {
                        dynamic ig = ((dynamic)obj).GetInstanceGuid();
                        g = ig.ExportedGuid; id = (int)ig.InternalId;
                    }
                    catch { continue; }
                    string key = g != Guid.Empty ? "g:" + g : "i:" + name.ToLowerInvariant() + "|" + id;
                    List<KeyValuePair<EnlightenKeys.Placement, string>> items;
                    if (!pending.Contains(key) || !byOwner.TryGetValue(key, out items)) continue;
                    if (items[0].Key.Source == "group")
                    {
                        IList members = LevelCommand.ReadList(obj, "MemberDatas");
                        if (members == null) return "group " + key + " in " + name + " has no MemberDatas";
                        foreach (var kv in items)
                        {
                            object member = LevelCommand.ResolveInternal(members[kv.Key.Member]) ?? members[kv.Key.Member];
                            IList rad = LevelCommand.ReadList(member, "InstanceRadiosityTypeOverride");
                            if (rad == null) return "group " + key + " member " + kv.Key.Member + " has no InstanceRadiosityTypeOverride";
                            Type et = PropertyPathWriter.ListElementType(rad);
                            int count = Convert.ToInt32(LevelCommand.ReadMember(member, "InstanceCount"), CultureInfo.InvariantCulture);
                            while (rad.Count < count) rad.Add(Enum.Parse(et, OvNone));
                            rad[kv.Key.Instance] = Enum.Parse(et, kv.Value);
                            if (member.GetType().IsValueType) members[kv.Key.Member] = member;
                            changed++;
                        }
                    }
                    else
                    {
                        try { PropertyPathWriter.SetValue(obj, "RadiosityTypeOverride", items[0].Value); }
                        catch (Exception ex) { return "RadiosityTypeOverride on " + key + " in " + name + ": " + ex.Message; }
                        changed++;
                    }
                    pending.Remove(key);
                }
                if (changed > before && !EbxWriteCommand.CallModifyEbx(name, asset, logger)) return "could not write " + name;
            }
            if (pending.Count > 0) return pending.Count + " placing object(s) not found in the level's assets (first " + pending.First() + ")";
            return null;
        }

        static void PutF(byte[] b, int o, double v) { Array.Copy(BitConverter.GetBytes((float)v), 0, b, o, 4); }
        static double Sq(double x) => x * x;
    }
}
