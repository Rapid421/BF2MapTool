using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // `navmesh geometry <level> --region x0,z0,x1,z1 [--terrain <streaming tree>] --out soup.obj`: the level's walkable
    // world as one triangle soup, in world coordinates (Y up) -- the input a navmesh builder voxelises (Recast: the
    // `navmesh build` tool in BfiiMapTools). Every placement the level (and each layer and sub-world it references)
    // makes -- static model group members and loose blueprint placements, as `level list` reads them -- is resolved
    // to its meshes (ObjectBlueprint.Object.Mesh; prefab blueprints recursively through Objects[].Blueprint with their
    // BlueprintTransform), each mesh's finest LOD within --max-tris is transformed into the world, and the terrain's
    // heightfield is added as a grid (holes left out). Render meshes stand in for their collision, and only meshes the
    // game collides with (a physics component with bodies) are kept: light cones, decals and cloth close doorways.
    // `navmesh build` runs geometry -> navgen -> the level's pathfinding stream in one step.
    public class NavmeshCommand : ICommand
    {
        public string Name => "navmesh";


        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args == null || args.Length == 0) { Console.Error.WriteLine(Usage); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "geometry": return Geometry(args.Skip(1).ToArray(), logger, formatter);
                case "build": return Build(args.Skip(1).ToArray(), logger, formatter);
                default: Console.Error.WriteLine(Usage); return 1;
            }
        }

        // ---- navmesh build: geometry -> navgen (Recast + NavPower compile) -> the level's pathfinding stream -----------
        //
        // The level's own navmesh from the level as it stands: `geometry` over its placements and terrain, the external
        // navgen tool (net8, DotRecast; BfiiMapTools src/Bfii.Navmesh/tools/Bfii.Navgen) builds and compiles it against a
        // donor blob (agent radius/height/step and image header come from the donor), and the result is delivered
        // through a PathfindingStreamEntityData of the level: a PathfindingBlobAsset of the level's own (cloned from the
        // stream's current blob asset) carrying a new chunk, Blob.BlobId/BlobSize/ChunkSizes set, the stream's
        // PathfindingBlobs pointed at it and Autoload on, asset and chunk added to the bundle of the stream's partition.
        private int Build(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "navmesh build";
            string level = null, regionArg = null, terrain = null, donor = null, stream = null, navgen = null, keep = null, blobFile = null;
            var geoArgs = new List<string>();
            var genArgs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--region": regionArg = next; i++; break;
                    case "--terrain": terrain = next; i++; break;
                    case "--donor": donor = next; i++; break;
                    case "--stream": stream = next; i++; break;
                    case "--navgen": navgen = next; i++; break;
                    case "--keep": keep = next; i++; break;
                    case "--blob": blobFile = next; i++; break;
                    case "--terrain-step": case "--min-size": case "--max-tris": case "--skip": case "--y-range":
                    case "--terrain-tolerance": case "--terrain-max-level":
                        geoArgs.Add(args[i]); geoArgs.Add(next); i++; break;
                    case "--render-only": case "--collision": geoArgs.Add(args[i]); break;
                    case "--double-sided": genArgs.Add("--double-sided"); genArgs.Add("1"); break;
                    case "--cs": case "--ch": case "--slope": case "--radius": case "--height": case "--climb":
                    case "--region-min": case "--region-merge": case "--edge-max": case "--edge-error": case "--min-component": case "--seeds":
                    case "--tile-size": case "--threads": case "--tile-origin":
                        genArgs.Add(args[i]); genArgs.Add(next); i++; break;
                    default:
                        if (!args[i].StartsWith("--") && level == null) { level = args[i]; break; }
                        Console.Error.WriteLine(label + ": unknown option " + args[i]); return 1;
                }
            }
            if (level == null || (regionArg == null && blobFile == null)) { Console.Error.WriteLine(Usage); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": no project loaded (use --project or --save-as)."); return 1; }
            if (blobFile != null && !File.Exists(blobFile)) { Console.Error.WriteLine(label + ": no such navmesh file: " + blobFile); return 1; }
            navgen = navgen ?? Environment.GetEnvironmentVariable("FROSTYCLI_NAVGEN") ?? BundledNavgen();
            if (blobFile == null && (string.IsNullOrEmpty(navgen) || !File.Exists(navgen)))
            { Console.Error.WriteLine(label + ": navgen.dll not found next to FrostyCLI.exe (navgen/navgen.dll, built with the solution from tools/navgen); pass --navgen <path> or set FROSTYCLI_NAVGEN"); return 1; }
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(level);
            if (levelEntry == null) { Console.Error.WriteLine(label + ": level not found: " + level); return 1; }
            level = levelEntry.Name;
            string dir = level.Substring(0, level.LastIndexOf('/') + 1), leaf = level.Substring(level.LastIndexOf('/') + 1);

            // the level's pathfinding streams
            var streams = new List<Tuple<string, object, string>>();   // (partition, entity, current blob asset)
            var spawns = new List<double[]>();                           // the level's spawn points, for the reach report
            bool hasSystem = false;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx().Where(x => x.Name.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (e.Type != "LayerData" && e.Type != "SubWorldData" && e.Type != "LevelData") continue;
                EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                foreach (object o in a.Objects)
                {
                    string t = o.GetType().Name;
                    if (t == "PathfindingSystemEntityData") hasSystem = true;
                    if (t == "AlternateSpawnEntityData" && Trans(Get(o, "Transform")) is double[] sp) spawns.Add(sp);
                    if (t != "PathfindingStreamEntityData") continue;
                    streams.Add(Tuple.Create(e.Name, o, ExternalName(Get(o, "PathfindingBlobs"))));
                }
            }
            if (!hasSystem) logger.LogWarning(label + ": no PathfindingSystemEntityData under " + dir + " -- the navmesh may never be read");
            Tuple<string, object, string> target;
            if (stream != null)
                target = streams.FirstOrDefault(s => string.Equals(LevelCommand.GetEntityInstanceGuid(s.Item2)?.ToString(), stream, StringComparison.OrdinalIgnoreCase));
            else if (streams.Count == 1) target = streams[0];
            else
            {
                Console.Error.WriteLine(label + ": " + (streams.Count == 0 ? "the level has no PathfindingStreamEntityData" : streams.Count + " pathfinding streams; pick one with --stream <instance guid>:"));
                foreach (var s in streams) Console.Error.WriteLine("  " + LevelCommand.GetEntityInstanceGuid(s.Item2) + "  in " + s.Item1 + " -> " + (s.Item3 ?? "(none)"));
                return 1;
            }
            if (target == null) { Console.Error.WriteLine(label + ": no pathfinding stream " + stream + " under " + dir); return 1; }
            string streamGuid = LevelCommand.GetEntityInstanceGuid(target.Item2).ToString();

            string work = keep ?? Path.Combine(Path.GetTempPath(), "frostycli-navmesh-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(work);
            string soup = Path.Combine(work, leaf + "_geometry.obj"), donorFile = Path.Combine(work, leaf + "_donor.bin");
            string outFile = Path.Combine(work, leaf + "_navmesh.bin"), polys = Path.Combine(work, leaf + "_navmesh.polys.obj");

            int rc;
            string donorAsset = null, spawnReport = null;
            byte[] nav;
            if (blobFile != null)
            {
                // a navmesh image built elsewhere (an earlier build's --keep dir, another tool), delivered as it is
                nav = File.ReadAllBytes(blobFile);
                logger.Log(label + ": delivering " + blobFile + " (" + nav.Length + " bytes) as built -- geometry and navgen skipped");
            }
            else
            {
                // 1. geometry
                var g = new List<string> { level, "--region", regionArg, "--out", soup };
                if (terrain != null) { g.Add("--terrain"); g.Add(terrain); }
                g.AddRange(geoArgs);
                rc = Geometry(g.ToArray(), logger, formatter);
                if (rc != 0) return rc;

                // 2. donor: a blob asset's chunk, or a file
                if (donor != null && File.Exists(donor)) File.Copy(donor, donorFile, true);
                else
                {
                    donorAsset = donor ?? target.Item3;
                    if (donorAsset == null) { Console.Error.WriteLine(label + ": the stream has no blob asset to take a donor from; pass --donor"); return 1; }
                    byte[] blob = BlobBytes(donorAsset);
                    if (blob == null) { Console.Error.WriteLine(label + ": cannot read the donor blob of " + donorAsset); return 1; }
                    File.WriteAllBytes(donorFile, blob);
                }

                // 3. navgen
                var sb = new System.Text.StringBuilder();
                sb.AppendFormat("\"{0}\" build \"{1}\" \"{2}\" \"{3}\" --region {4} --polys \"{5}\"", navgen, soup, donorFile, outFile, regionArg, polys);
                foreach (string a in genArgs) sb.Append(' ').Append(a.IndexOf(' ') >= 0 || a.IndexOf(';') >= 0 ? "\"" + a + "\"" : a);
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", sb.ToString())
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                string navOut, navErr; int navCode;
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var errTask = p.StandardError.ReadToEndAsync();
                    navOut = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    navErr = errTask.Result; navCode = p.ExitCode;
                }
                foreach (string line in navOut.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0)) logger.Log("navgen: " + line);
                if (navCode != 0 || !File.Exists(outFile)) { Console.Error.WriteLine(label + ": navgen failed (" + navCode + "): " + navErr.Trim()); return 1; }
                nav = File.ReadAllBytes(outFile);

                // can a bot get anywhere from where it spawns? spawns off the navmesh, or on a piece of it joined to
                // nothing else (a ledge, a roof without its stairs), stand still all round
                double[] rg = regionArg.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                var inRegion = spawns.Where(p => p[0] >= Math.Min(rg[0], rg[2]) && p[0] <= Math.Max(rg[0], rg[2])
                                              && p[2] >= Math.Min(rg[1], rg[3]) && p[2] <= Math.Max(rg[1], rg[3])).ToList();
                // a navmesh stretched over a kilometre of height (Cloud City's planet 900 m under the city came along)
                // left every bot standing at its spawn, although DICE's own navmesh through the same delivery plays;
                // Death Star II's 485 m of floors play. Keep the play band with --y-range.
                if (File.Exists(polys))
                {
                    NavmeshSpawnReach.Mesh pm = NavmeshSpawnReach.ReadObj(polys);
                    if (pm.V.Count > 0)
                    {
                        double ylo = pm.V.Min(v => v[1]), yhi = pm.V.Max(v => v[1]);
                        if (yhi - ylo > MaxHeightSpan)
                            logger.LogWarning(string.Format(CultureInfo.InvariantCulture,
                                "{0}: the navmesh spans {1:0} m of height (y {2:0}..{3:0}); one over ~1 km (Cloud City, y -698..361) left every bot standing still -- keep the play band with --y-range",
                                label, yhi - ylo, ylo, yhi));
                    }
                }
                if (inRegion.Count > 0 && File.Exists(polys))
                {
                    NavmeshSpawnReach.Report reach = NavmeshSpawnReach.Classify(NavmeshSpawnReach.ReadObj(polys), inRegion);
                    spawnReport = NavmeshSpawnReach.Describe(reach);
                    if (reach.Count(NavmeshSpawnReach.Where.Main) < reach.Spots.Count) logger.LogWarning(label + ": " + spawnReport);
                    else logger.Log(label + ": " + spawnReport);
                }
            }

            // 4. deliver: the level's own blob asset, a new chunk, the stream pointed at it
            // one blob asset per stream (a map may stream several: Crait has two gated on Skirmish)
            string blobAsset = (dir + leaf + "_pathfindingblobs_" + streamGuid.Substring(0, 8)).ToLowerInvariant();
            Guid chunk = Guid.Empty;
            if (App.AssetManager.GetEbxEntry(blobAsset) == null)
            {
                string template = target.Item3 ?? donorAsset;
                if (template == null) { Console.Error.WriteLine(label + ": no blob asset to clone for " + blobAsset); return 1; }
                rc = EbxWriteCommand.ExecuteClone(new[] { template, "--as", blobAsset }, logger, formatter);
                if (rc != 0) return rc;
            }
            else
            {
                // a rebuild of this level's own navmesh (apply scripts run again on a project that has one): rewrite the
                // chunk this command added before instead of adding a second image beside it
                object prev = Get(Get(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(blobAsset)).RootObject, "Blob"), "BlobId");
                ChunkAssetEntry pc = prev is Guid pg ? App.AssetManager.GetChunkEntry(pg) : null;
                if (pc != null && pc.IsAdded && App.AssetManager.ModifyChunk(pc.Id, nav))
                {
                    chunk = pc.Id;
                    logger.Log(label + ": rewrote the level's own navmesh chunk " + chunk + " in place");
                }
            }
            if (chunk == Guid.Empty) chunk = App.AssetManager.AddChunk(nav, null);
            foreach (var kv in new[] { new[] { "Blob.BlobId", chunk.ToString() }, new[] { "Blob.BlobSize", nav.Length.ToString(CultureInfo.InvariantCulture) },
                                       new[] { "Blob.ChunkSizes[0]", nav.Length.ToString(CultureInfo.InvariantCulture) } })
            {
                rc = WriteOpBus.Execute("ebx-set", new[] { blobAsset, "--prop", kv[0], "--value", kv[1] }, logger, formatter);
                if (rc != 0) return rc;
            }
            rc = EbxWriteCommand.ExecuteSetRef(new[] { target.Item1, "--object", streamGuid, "--prop", "PathfindingBlobs", "--target", blobAsset }, logger, formatter);
            if (rc != 0) return rc;
            rc = WriteOpBus.Execute("ebx-set", new[] { target.Item1, "--object", streamGuid, "--prop", "Autoload", "--value", "true" }, logger, formatter);
            if (rc != 0) return rc;
            EbxAssetEntry pe = App.AssetManager.GetEbxEntry(target.Item1);
            string bundle = pe.AddedBundles.Concat(pe.Bundles).Select(b => App.AssetManager.GetBundleEntry(b)?.Name).FirstOrDefault(n => n != null);
            if (bundle == null) { Console.Error.WriteLine(label + ": " + target.Item1 + " is in no bundle"); return 1; }
            rc = BundleCommand.ExecuteAdd(new[] { blobAsset, "--to", bundle }, logger, formatter);
            if (rc == 0) rc = BundleCommand.ExecuteAdd(new[] { chunk.ToString(), "--to", bundle }, logger, formatter);
            if (rc != 0) return rc;

            formatter.WriteResult("OK", label, new Dictionary<string, string>
            {
                { "stream", streamGuid + " in " + target.Item1 + " (Autoload on)" },
                { "blob asset", blobAsset },
                { "chunk", chunk + " (" + nav.Length + " bytes)" },
                { "bundle", bundle },
                { "donor", donorAsset ?? donor },
                { "spawns", spawnReport ?? "not checked (--blob, or no spawn point in the region)" },
                { "files", work },
            });
            return 0;
        }

        private static byte[] BlobBytes(string blobAsset)
        {
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(blobAsset);
            if (e == null) return null;
            object blob = Get(App.AssetManager.GetEbx(e).RootObject, "Blob");
            object id = Get(blob, "BlobId");
            if (!(id is Guid g)) return null;
            ChunkAssetEntry c = App.AssetManager.GetChunkEntry(g);
            if (c == null) return null;
            using (Stream s = App.AssetManager.GetChunk(c))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        private const string Usage =
            "Usage: frostycli navmesh geometry <level> --region x0,z0,x1,z1 [--terrain <streaming tree res|file>] [--terrain-step m]\n" +
            "         [--min-size m] [--max-tris n] [--skip substring,...] [--y-range y0,y1] [--render-only] [--collision]\n" +
            "         [--terrain-tolerance m [--terrain-max-level n]  (adaptive terrain: merge grid cells within m of flat)] --out soup.obj\n" +
            "  Only meshes the game collides with (a physics component with bodies) are written; --render-only adds\n" +
            "  the rest (light cones, decals, foliage cards) for diagnostics. --collision writes the level's COLLISION\n" +
            "  instead of render meshes: every server physics body on a layer characters collide with (static group\n" +
            "  resources incl. character-only slots, placed blueprints' physics, OBB/sphere blockers), decoded from the\n" +
            "  Havok resources -- use it where render and collision disagree (ported groups, invisible blockers).\n" +
            "       frostycli navmesh build <level> --region x0,z0,x1,z1 [--terrain <tree>] [--donor <blob asset|file>]\n" +
            "         [--stream <guid>] [--navgen <navgen.dll>] [--keep <dir>] [geometry options] [--cs m] [--ch m] [--climb m]\n" +
            "         [--double-sided  (turn near-horizontal downward faces up: slabs whose top is not in the render mesh)]\n" +
            "         [--blob navmesh.bin  (deliver a navmesh image built elsewhere; no geometry, no navgen, no region)]\n" +
            "         [--radius m] [--height m] [--slope deg] [--min-component m2] [--seeds x,y,z;...]\n" +
            "         [--tile-size cells [--threads n]  (tiled Recast + seam stitching, for maps too large for one heightfield)]\n" +
            "  geometry -> navgen (Recast, compiled against the donor's agent) -> the level's pathfinding stream (own blob\n" +
            "  asset, new chunk, sizes, PathfindingBlobs + Autoload, bundled). Needs a loaded project.";

        private sealed class MeshPart { public string Mesh; public float[] M; public bool Collides; }        // M: 4x3 row-major (right, up, forward, trans)
        private sealed class MeshTris { public float[] P; public int[] T; public float[] Min, Max; }

        private readonly Dictionary<string, List<MeshPart>> _blueprints = new Dictionary<string, List<MeshPart>>(StringComparer.OrdinalIgnoreCase);
        private int _movedLeftOut;   // objects left out because their blueprint's logic moves them (door leaves)
        private readonly Dictionary<string, MeshTris> _meshes = new Dictionary<string, MeshTris>(StringComparer.OrdinalIgnoreCase);

        private int Geometry(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "navmesh geometry";
            string level = null, regionArg = null, terrain = null, outFile = null, skipArg = null, yArg = null;
            double terrainStep = 1.0, minSize = 1.0, terrainTolerance = 0; int maxTris = 4000, terrainMaxLevel = 6; bool renderOnly = false, collision = false;
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (args[i])
                {
                    case "--region": regionArg = next; i++; break;
                    case "--terrain": terrain = next; i++; break;
                    case "--terrain-step": terrainStep = double.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--terrain-tolerance": terrainTolerance = double.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--terrain-max-level": terrainMaxLevel = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--min-size": minSize = double.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--max-tris": maxTris = int.Parse(next, CultureInfo.InvariantCulture); i++; break;
                    case "--skip": skipArg = next; i++; break;
                    case "--y-range": yArg = next; i++; break;
                    case "--render-only": renderOnly = true; break;
                    case "--collision": collision = true; break;
                    case "--out": outFile = next; i++; break;
                    default:
                        if (!args[i].StartsWith("--") && level == null) { level = args[i]; break; }
                        Console.Error.WriteLine(label + ": unknown option " + args[i]); return 1;
                }
            }
            if (level == null || regionArg == null || outFile == null) { Console.Error.WriteLine(Usage); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine(label + ": AssetManager not initialized."); return 1; }
            double[] r = regionArg.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (r.Length != 4 || r[2] <= r[0] || r[3] <= r[1]) { Console.Error.WriteLine(label + ": --region wants x0,z0,x1,z1"); return 1; }
            string[] skip = (skipArg ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            // --y-range y0,y1: only geometry in this height band -- a backdrop far below or above the play space (Cloud
            // City's planet 900 m under the city) otherwise becomes navmesh, and the image's bounds with it
            double y0 = double.MinValue, y1 = double.MaxValue;
            if (yArg != null)
            {
                double[] y = yArg.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                if (y.Length != 2 || y[1] <= y[0]) { Console.Error.WriteLine(label + ": --y-range wants y0,y1"); return 1; }
                y0 = y[0]; y1 = y[1];
            }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(level);
            if (entry == null) { Console.Error.WriteLine(label + ": level not found: " + level); return 1; }
            // each mesh at its finest LOD within the triangle budget (Recast voxelises at decimetres)
            RadiosityUvPort.MaxTriangles = maxTris;
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            var rows = new List<LevelCommand.EntityRow>();
            string warning;
            LevelCommand.TryCollectBfiiRows(asset.RootObject, rows, null, int.MaxValue, out warning);

            var verts = new List<float>();
            var tris = new List<int>();
            var groups = new List<KeyValuePair<int, string>>();     // (first triangle, mesh) -- written as OBJ groups
            int placed = 0, skippedFar = 0, noMesh = 0, meshFailed = 0, noCollision = 0, badVerts = 0;
            var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const double margin = 5;
            NavmeshCollision col = null;
            if (collision)
            {
                col = new NavmeshCollision();
                col.Skip = skip;
                col.CollectLevel(level);
                skippedFar = CopyCollision(col, r, margin, y0, y1, verts, tris, groups);
                rows.Clear();                                        // the render placements are not read
            }
            foreach (var row in rows)
            {
                if (row.Trans == null || row.Right == null || row.Up == null || row.Forward == null) continue;
                if (skip.Any(s => row.Blueprint != null && row.Blueprint.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                List<MeshPart> parts = PartsOf(row.Blueprint, 0);
                if (parts.Count == 0) { noMesh++; continue; }
                float[] world = new float[] { row.Right[0], row.Right[1], row.Right[2], row.Up[0], row.Up[1], row.Up[2],
                                              row.Forward[0], row.Forward[1], row.Forward[2], row.Trans[0], row.Trans[1], row.Trans[2] };
                bool any = false;
                foreach (var part in parts)
                {
                    if (skip.Any(s => part.Mesh.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    if (!part.Collides && !renderOnly) { noCollision++; dropped.Add(part.Mesh); continue; }
                    MeshTris mt = TrianglesOf(part.Mesh);
                    if (mt == null) { meshFailed++; continue; }
                    float[] m = Mul(part.M, world);
                    // world bounds of the mesh box; skip tiny clutter and anything outside the region
                    double bx0 = double.MaxValue, bz0 = double.MaxValue, bx1 = double.MinValue, bz1 = double.MinValue, ext = 0;
                    double by0 = double.MaxValue, by1 = double.MinValue;
                    for (int c = 0; c < 8; c++)
                    {
                        float[] p = { (c & 1) != 0 ? mt.Max[0] : mt.Min[0], (c & 2) != 0 ? mt.Max[1] : mt.Min[1], (c & 4) != 0 ? mt.Max[2] : mt.Min[2] };
                        var w = Apply(m, p);
                        bx0 = Math.Min(bx0, w[0]); bx1 = Math.Max(bx1, w[0]); bz0 = Math.Min(bz0, w[2]); bz1 = Math.Max(bz1, w[2]);
                        by0 = Math.Min(by0, w[1]); by1 = Math.Max(by1, w[1]);
                    }
                    ext = Math.Max(bx1 - bx0, bz1 - bz0);
                    if (bx1 < r[0] - margin || bx0 > r[2] + margin || bz1 < r[1] - margin || bz0 > r[3] + margin) { skippedFar++; continue; }
                    if (by1 < y0 || by0 > y1) { skippedFar++; continue; }
                    if (ext < minSize) continue;
                    if (double.IsNaN(ext) || double.IsInfinity(ext)) { badVerts++; continue; }
                    int b = verts.Count / 3;
                    var world3 = new float[mt.P.Length];
                    bool finite = true;
                    for (int k = 0; k < mt.P.Length && finite; k += 3)
                    {
                        var w = Apply(m, new[] { mt.P[k], mt.P[k + 1], mt.P[k + 2] });
                        for (int c = 0; c < 3; c++) { world3[k + c] = (float)w[c]; finite &= !float.IsNaN(world3[k + c]) && !float.IsInfinity(world3[k + c]); }
                    }
                    if (!finite) { badVerts++; continue; }       // a few backdrop meshes carry NaN positions
                    groups.Add(new KeyValuePair<int, string>(tris.Count / 3, part.Mesh));
                    verts.AddRange(world3);
                    for (int k = 0; k + 2 < mt.T.Length; k += 3)
                    {
                        if (yArg != null)
                        {
                            float ya = world3[mt.T[k] * 3 + 1], yb = world3[mt.T[k + 1] * 3 + 1], yc = world3[mt.T[k + 2] * 3 + 1];
                            if (Math.Max(ya, Math.Max(yb, yc)) < y0 || Math.Min(ya, Math.Min(yb, yc)) > y1) continue;
                        }
                        tris.Add(b + mt.T[k]); tris.Add(b + mt.T[k + 1]); tris.Add(b + mt.T[k + 2]);
                    }
                    any = true;
                }
                if (any) placed++;
            }
            int propTris = tris.Count / 3;

            // terrain grid over the region (holes left out)
            int terrainTris = 0;
            if (terrain != null)
            {
                ResAssetEntry te; string source;
                byte[] payload = TerrainCommand.LoadPayload(label, terrain, out te, out source);
                if (payload == null) return 1;
                var parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(payload);
                if (!parsed.Success) { Console.Error.WriteLine(label + ": terrain did not parse: " + parsed.Failure); return 1; }
                var hs = new TerrainHeightSource(parsed.Resource.Heightfield);
                if (terrainTolerance > 0)
                {
                    // a whole map at 0.5 m is tens of millions of triangles as a grid: an adaptive quadtree keeps the
                    // grid's detail where the ground is not flat (trenches, rocks, hole rims) and merges the rest
                    groups.Add(new KeyValuePair<int, string>(tris.Count / 3, "terrain"));
                    TerrainNavMesher.Stats ts = TerrainNavMesher.Build(hs.TrySampleMetres, hs.IsHole, r[0], r[1], r[2], r[3], terrainStep, terrainTolerance, terrainMaxLevel, verts, tris);
                    terrainTris = ts.Triangles;
                    logger.Log(string.Format(CultureInfo.InvariantCulture, "{0}: terrain {1} grid points at {2} m -> {3} cells, {4} triangles within {5} m ({6} hole cells left out, {7} rim points without ground given their neighbours' height)",
                        label, ts.GridPoints, terrainStep, ts.Cells, ts.Triangles, terrainTolerance, ts.HoleCells, ts.FilledPoints));
                }
                int nx = terrainTolerance > 0 ? -1 : (int)Math.Ceiling((r[2] - r[0]) / terrainStep), nz = terrainTolerance > 0 ? -1 : (int)Math.Ceiling((r[3] - r[1]) / terrainStep);
                int b = verts.Count / 3;
                if (terrainTolerance <= 0) groups.Add(new KeyValuePair<int, string>(tris.Count / 3, "terrain"));
                for (int j = 0; j <= nz; j++)
                    for (int i = 0; i <= nx; i++)
                    {
                        double x = r[0] + i * terrainStep, z = r[1] + j * terrainStep;
                        verts.Add((float)x); verts.Add((float)hs.SampleMetres(x, z)); verts.Add((float)z);
                    }
                for (int j = 0; j < nz; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        double cx = r[0] + (i + 0.5) * terrainStep, cz = r[1] + (j + 0.5) * terrainStep;
                        if (hs.IsHole(cx, cz)) continue;
                        int a = b + j * (nx + 1) + i, c = a + 1, d = a + nx + 1, e = d + 1;
                        tris.Add(a); tris.Add(d); tris.Add(c);
                        tris.Add(c); tris.Add(d); tris.Add(e);
                        terrainTris += 2;
                    }
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var w = new StreamWriter(outFile))
            {
                w.WriteLine("# navmesh geometry of " + level + ", region " + regionArg);
                for (int k = 0; k < verts.Count; k += 3)
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "v {0:0.####} {1:0.####} {2:0.####}", verts[k], verts[k + 1], verts[k + 2]));
                int g = 0;
                for (int k = 0; k < tris.Count; k += 3)
                {
                    while (g < groups.Count && groups[g].Key == k / 3) { w.WriteLine("g " + groups[g].Value); g++; }
                    w.WriteLine("f " + (tris[k] + 1) + " " + (tris[k + 1] + 1) + " " + (tris[k + 2] + 1));
                }
            }
            if (col != null)
            {
                formatter.WriteResult("OK", label, new Dictionary<string, string>
                {
                    { "source", "collision (server physics bodies on character layers, collision volumes)" },
                    { "bodies", col.GroupBodies + " group bod(ies), " + col.EntityBodies + " entity bod(ies) from " + col.Placements + " placed blueprint(s); " + col.Volumes + " collision volume(s)" },
                    { "shapes", col.Census.ToString() },
                    { "skipped", col.SkippedBodies.Count == 0 ? "none" : string.Join("; ", col.SkippedBodies.Select(kv => kv.Key + " x" + kv.Value)) },
                    { "moving", col.MovedLeftOut + " object(s) in prefabs whose logic moves them (door leaves) left out" },
                    { "by name", col.SkippedByName + " blueprint placement(s) left out by --skip" },
                    { "triangles", propTris + " collision triangle(s) in the region (" + skippedFar + " outside it or the height band), " + terrainTris + " terrain" },
                    { "file", outFile },
                });
                foreach (string w in col.Warnings.Distinct().Take(40)) logger.LogWarning(label + ": " + w);
                if (col.Census.Unsupported > 0 || col.Census.Malformed > 0)
                    logger.LogWarning(label + ": " + col.Census.Unsupported + " unsupported and " + col.Census.Malformed + " malformed shape(s) left out: " + string.Join(", ", col.Census.UnsupportedClasses));
                return 0;
            }
            formatter.WriteResult("OK", label, new Dictionary<string, string>
            {
                { "placements", rows.Count + " read, " + placed + " in the region with meshes, " + noMesh + " without a mesh, " + skippedFar + " mesh parts outside" },
                { "moving", _movedLeftOut + " object(s) in prefabs whose logic moves them (door leaves) left out" },
                { "meshes", _meshes.Count + " distinct, " + meshFailed + " unreadable, " + badVerts + " parts with non-finite vertices" },
                { "no collision", renderOnly ? "kept (--render-only)" : noCollision + " parts of " + dropped.Count + " render-only meshes left out" },
                { "triangles", propTris + " from placements, " + terrainTris + " terrain" },
                { "file", outFile },
            });
            if (warning != null) logger.LogWarning(label + ": " + warning);
            return 0;
        }

        // Copies the collision soup's triangles that reach into the region (x/z, with a margin) and the height band into
        // the output lists, keeping only the vertices they use: the soup holds whole groups (kilometres of them), and
        // stray vertices would stretch the bounds navgen voxelises. Returns the number of triangles left out.
        internal static int CopyCollision(NavmeshCollision col, double[] r, double margin, double y0, double y1,
                                          List<float> verts, List<int> tris, List<KeyValuePair<int, string>> groups)
        {
            var remap = new Dictionary<int, int>();
            int outside = 0, label = 0;
            List<float> V = col.Verts; List<int> T = col.Tris;
            for (int t = 0; t + 2 < T.Count; t += 3)
            {
                while (label < col.Labels.Count && col.Labels[label].Key <= t / 3)
                {
                    groups.Add(new KeyValuePair<int, string>(tris.Count / 3, col.Labels[label].Value));
                    label++;
                }
                double xmin = double.MaxValue, xmax = double.MinValue, zmin = double.MaxValue, zmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
                for (int k = 0; k < 3; k++)
                {
                    int v = T[t + k] * 3;
                    xmin = Math.Min(xmin, V[v]); xmax = Math.Max(xmax, V[v]);
                    ymin = Math.Min(ymin, V[v + 1]); ymax = Math.Max(ymax, V[v + 1]);
                    zmin = Math.Min(zmin, V[v + 2]); zmax = Math.Max(zmax, V[v + 2]);
                }
                if (xmax < r[0] - margin || xmin > r[2] + margin || zmax < r[1] - margin || zmin > r[3] + margin || ymax < y0 || ymin > y1
                    || double.IsNaN(xmin + ymin + zmin + xmax + ymax + zmax)) { outside++; continue; }
                for (int k = 0; k < 3; k++)
                {
                    int v = T[t + k], n;
                    if (!remap.TryGetValue(v, out n))
                    {
                        n = verts.Count / 3;
                        remap[v] = n;
                        verts.Add(V[v * 3]); verts.Add(V[v * 3 + 1]); verts.Add(V[v * 3 + 2]);
                    }
                    tris.Add(n);
                }
            }
            // a label whose triangles all fell outside is dropped (the next label starts at the same triangle)
            for (int g = groups.Count - 1; g > 0; g--)
                if (groups[g].Key == groups[g - 1].Key) groups.RemoveAt(g - 1);
            return outside;
        }

        // ---- blueprints to meshes -------------------------------------------------------------------------

        private List<MeshPart> PartsOf(string blueprint, int depth)
        {
            if (blueprint == null) return new List<MeshPart>();
            List<MeshPart> parts;
            if (_blueprints.TryGetValue(blueprint, out parts)) return parts;
            parts = new List<MeshPart>();
            _blueprints[blueprint] = parts;
            if (depth > 4) return parts;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(blueprint);
            if (e == null) return parts;
            object root;
            try { root = App.AssetManager.GetEbx(e).RootObject; } catch { return parts; }
            string mesh = MeshName(root);
            if (mesh != null) { parts.Add(new MeshPart { Mesh = mesh, M = Identity(), Collides = HasCollision(root) }); return parts; }
            object obj = Get(root, "Object");
            obj = Deref(obj);
            if (obj != null)
            {
                mesh = MeshName(obj);
                if (mesh != null) { parts.Add(new MeshPart { Mesh = mesh, M = Identity(), Collides = HasCollision(obj) }); return parts; }
            }
            IList objects = Get(root, "Objects") as IList;
            HashSet<object> moved = MovedObjects(root);
            if (objects != null)
                foreach (object raw in objects)
                {
                    object o = Deref(raw);
                    if (o == null) continue;
                    if (moved.Contains(o)) { _movedLeftOut++; continue; }
                    float[] local = Transform(Get(o, "BlueprintTransform")) ?? Transform(Get(o, "Transform")) ?? Identity();
                    string sub = MeshName(o);
                    if (sub != null) { parts.Add(new MeshPart { Mesh = sub, M = local, Collides = HasCollision(o) }); continue; }
                    string child = ExternalName(Get(o, "Blueprint"));
                    if (child == null) continue;
                    foreach (var p in PartsOf(child, depth + 1))
                        parts.Add(new MeshPart { Mesh = p.Mesh, M = Mul(p.M, local), Collides = p.Collides });
                }
            return parts;
        }

        private static readonly uint TransformHash = unchecked((uint)FrostySdk.Utils.HashString("Transform"));

        // Objects a blueprint's own logic moves: a PropertyConnection writes their Transform (a door's leaves --
        // PF_AutomaticDoor_Core slides them open). They are not static ground: a closed leaf in the geometry walls off
        // its doorway, and DICE's navmeshes run through closed doors (Kamino: 2 of 19 bots stranded behind the leaves).
        internal static HashSet<object> MovedObjects(object blueprintRoot)
        {
            var moved = new HashSet<object>();
            if (!(Get(blueprintRoot, "PropertyConnections") is IList pcs)) return moved;
            foreach (object pc in pcs)
            {
                uint field;
                try { field = unchecked((uint)Convert.ToInt64(Get(pc, "TargetFieldId"))); } catch { continue; }
                if (field != TransformHash) continue;
                object t = Deref(Get(pc, "Target"));
                if (t != null) moved.Add(t);
            }
            return moved;
        }

        // A static model collides when one of its components carries physics bodies (StaticModelPhysicsComponentData ->
        // RigidBodyData -> HavokAsset). Render-only meshes (light cones, decals, cloth cards) have part components only.
        internal static bool HasCollision(object o)
        {
            if (Deref(Get(o, "PhysicsData")) != null) return true;
            if (!(Get(o, "Components") is IList comps)) return false;
            foreach (object raw in comps)
            {
                object c = Deref(raw);
                if (c == null) continue;
                if (Get(c, "PhysicsBodies") is IList bodies && bodies.Count > 0) return true;
                if (Deref(Get(c, "PhysicsData")) != null) return true;
            }
            return false;
        }

        private static string MeshName(object o)
        {
            if (o == null) return null;
            string n = ExternalName(Get(o, "Mesh"));
            if (n == null) return null;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(n);
            return e != null && e.Type != null && e.Type.IndexOf("Mesh", StringComparison.OrdinalIgnoreCase) >= 0 ? n : null;
        }

        // the tallest navmesh seen playing (Death Star II: 485 m); Cloud City's 1,059 m froze every bot
        internal const double MaxHeightSpan = 600;

        // tools\navgen\Bfii.Navgen builds into bin\x64\<config>\navgen\ beside FrostyCLI.exe
        internal static string BundledNavgen()
        {
            string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "navgen", "navgen.dll");
            return File.Exists(p) ? p : null;
        }

        private static double[] Trans(object lt)
        {
            try { dynamic t = lt; return new double[] { t.trans.x, t.trans.y, t.trans.z }; } catch { return null; }
        }

        private static object Get(object o, string prop)
        {
            if (o == null) return null;
            var pi = o.GetType().GetProperty(prop);
            return pi == null ? null : pi.GetValue(o);
        }

        private static object Deref(object pointer)
        {
            if (pointer is PointerRef pr) return pr.Type == PointerRefType.Internal ? pr.Internal : null;
            return pointer;
        }

        private static string ExternalName(object pointer)
        {
            if (!(pointer is PointerRef pr) || pr.Type != PointerRefType.External) return null;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
            return e != null ? e.Name : null;
        }

        private static float[] Transform(object lt)
        {
            if (lt == null) return null;
            try
            {
                dynamic t = lt;
                return new float[] { t.right.x, t.right.y, t.right.z, t.up.x, t.up.y, t.up.z, t.forward.x, t.forward.y, t.forward.z, t.trans.x, t.trans.y, t.trans.z };
            }
            catch { return null; }
        }

        // ---- meshes to triangles ----------------------------------------------------------------------------

        private MeshTris TrianglesOf(string mesh)
        {
            MeshTris mt;
            if (_meshes.TryGetValue(mesh, out mt)) return mt;
            var verts = new List<RadiosityUvPort.Vertex>();
            var sections = new List<RadiosityUvPort.Section>();
            bool hasRad; string note;
            string err;
            try { err = RadiosityUvPort.ReadWithTriangles(mesh, verts, sections, out hasRad, out note); }
            catch (Exception ex) { err = ex.Message; }
            if (err != null || verts.Count == 0) { _meshes[mesh] = null; return null; }
            mt = new MeshTris { P = new float[verts.Count * 3], Min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue }, Max = new float[] { float.MinValue, float.MinValue, float.MinValue } };
            for (int i = 0; i < verts.Count; i++)
            {
                mt.P[i * 3] = verts[i].X; mt.P[i * 3 + 1] = verts[i].Y; mt.P[i * 3 + 2] = verts[i].Z;
                for (int c = 0; c < 3; c++) { mt.Min[c] = Math.Min(mt.Min[c], mt.P[i * 3 + c]); mt.Max[c] = Math.Max(mt.Max[c], mt.P[i * 3 + c]); }
            }
            mt.T = sections.SelectMany(s => s.Triangles).ToArray();
            _meshes[mesh] = mt;
            return mt;
        }

        // ---- 4x3 transforms (rows: right, up, forward, trans; p' = p.x right + p.y up + p.z forward + trans) -----

        private static float[] Identity() => new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 };

        internal static double[] Apply(float[] m, float[] p) => new double[]
        {
            p[0] * m[0] + p[1] * m[3] + p[2] * m[6] + m[9],
            p[0] * m[1] + p[1] * m[4] + p[2] * m[7] + m[10],
            p[0] * m[2] + p[1] * m[5] + p[2] * m[8] + m[11],
        };

        // a then b: world = b(a(p))
        internal static float[] Mul(float[] a, float[] b)
        {
            var o = new float[12];
            for (int row = 0; row < 3; row++)
                for (int c = 0; c < 3; c++)
                    o[row * 3 + c] = a[row * 3] * b[c] + a[row * 3 + 1] * b[3 + c] + a[row * 3 + 2] * b[6 + c];
            var t = Apply(b, new[] { a[9], a[10], a[11] });
            o[9] = (float)t[0]; o[10] = (float)t[1]; o[11] = (float)t[2];
            return o;
        }
    }
}
