using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyLevelTools;

namespace FrostyCLI.Commands
{
    // `level create <new-level>` - a new Battlefront II level assembled from parts: a shipped level's terrain and shell
    // (default: the S9_3 COOP_NT_FOSD test range), one game mode sub-world grafted from another shipped level (default:
    // Heroes vs Villains from Jakku_01), a chosen visual environment (default: Naboo cloudy) and generated spawns.
    // The work is done by FrostyLevelTools.LevelCreator. Rules and evidence: I:\New Levels\FINDINGS.md.
    public static class LevelCreate
    {
        // x0, x1, z0, z1 of every AlternateSpawnEntityData in the level's partitions (null when none)
        private static double[] SpawnBounds(AssetManager am, string level)
        {
            string dir = level.Substring(0, level.LastIndexOf('/') + 1);
            double[] b = null;
            foreach (EbxAssetEntry e in am.EnumerateEbx().Where(x => x.Name.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                FrostySdk.IO.EbxAsset a;
                try { a = am.GetEbx(e); } catch { continue; }
                foreach (object o in a.Objects.Where(o => o.GetType().Name == "AlternateSpawnEntityData"))
                {
                    try
                    {
                        dynamic t = o.GetType().GetProperty("Transform").GetValue(o);
                        double x = t.trans.x, z = t.trans.z;
                        if (b == null) b = new[] { x, x, z, z };
                        b[0] = Math.Min(b[0], x); b[1] = Math.Max(b[1], x); b[2] = Math.Min(b[2], z); b[3] = Math.Max(b[3], z);
                    }
                    catch { }
                }
            }
            return b;
        }

        // `level skirmish-template <map> --out <dir> [--margin m]`: what you need to build a Skirmish map for bots in a
        // terrain editor -- the map's real ground round its Skirmish play space (terrain export-heights: heights.r16 +
        // sidecar), and template.json with the play space, every spawn (team, x, y, z), the median spawn height, the
        // Skirmish navmesh stream and the terrain to build against. The Blender add-on imports the folder (File > Import >
        // BFII Skirmish Template); export it back and `level create --skirmish-from <map> --heights ...` builds the level.
        public static int ExecuteTemplate(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string map = null, dir = null; double margin = 150;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) dir = args[++i];
                else if (args[i] == "--margin" && i + 1 < args.Length) margin = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) map = args[i];
            }
            if (map == null || dir == null) { Console.Error.WriteLine("usage: level skirmish-template <map, e.g. Levels/MP/Endor_01/Endor_01> --out <dir> [--margin m]"); return 1; }
            SkirmishShell s;
            try { s = SkirmishShell.Plan(App.AssetManager, map, n => logger.Log("level skirmish-template: " + n)); }
            catch (Exception ex) { Console.Error.WriteLine("level skirmish-template: " + ex.Message); return 1; }
            if (s.Play == null) { Console.Error.WriteLine("level skirmish-template: no Skirmish play space found on " + map); return 1; }
            Directory.CreateDirectory(dir);
            double[] region = { s.Play[0] - margin, s.Play[2] - margin, s.Play[1] + margin, s.Play[3] + margin };
            var inv = CultureInfo.InvariantCulture;
            var spawns = new Newtonsoft.Json.Linq.JArray(s.SpawnPoints.Select(kv => new Newtonsoft.Json.Linq.JObject
            {
                ["team"] = kv.Key, ["x"] = Math.Round(kv.Value[0], 2), ["y"] = Math.Round(kv.Value[1], 2), ["z"] = Math.Round(kv.Value[2], 2)
            }));
            var t = new Newtonsoft.Json.Linq.JObject
            {
                ["map"] = s.Host,
                ["terrainHost"] = s.TerrainData == null ? null : s.TerrainData.ToLowerInvariant() + ".streamingtree_win32",
                ["play"] = new Newtonsoft.Json.Linq.JArray(s.Play.Select(v => Math.Round(v, 1))),
                ["region"] = new Newtonsoft.Json.Linq.JArray(region.Select(v => Math.Round(v, 1))),
                ["spawnY"] = Math.Round(s.SpawnY, 2),
                ["streams"] = new Newtonsoft.Json.Linq.JArray(s.Streams.Select(g => g.ToString())),
                ["spawns"] = spawns,
                ["next"] = "level create <new level> --skirmish-from " + s.Host + " --heights <exported heights.r32> [--layers layers.json] --navgen <navgen.dll>",
            };
            File.WriteAllText(Path.Combine(dir, "template.json"), t.ToString(Newtonsoft.Json.Formatting.Indented));
            int rc = 0;
            if (s.TerrainData != null)
                rc = new TerrainCommand().Execute(new[] { "export-heights", s.TerrainData.ToLowerInvariant() + ".streamingtree_win32",
                    "--region", string.Join(",", region.Select(v => v.ToString("0.#", inv))), "--out", Path.Combine(dir, "heights.r16") }, logger, formatter);
            else logger.LogWarning("level skirmish-template: " + map + " has no terrain; template.json only");
            formatter.WriteResult(rc == 0 ? "OK" : "WARN", "level skirmish-template", new Dictionary<string, string>
            {
                { "map", s.Host }, { "dir", dir }, { "spawns", s.SpawnPoints.Count.ToString(inv) },
                { "play", string.Format(inv, "x {0:0}..{1:0}, z {2:0}..{3:0}", s.Play[0], s.Play[1], s.Play[2], s.Play[3]) },
                { "spawnY", s.SpawnY.ToString("0.0", inv) },
            });
            return rc;
        }

        // `level closure <level> [--frosty-plugins <dir>] [--no-link]`: re-run level create's dependency closure on an
        // existing new level after content was added to it (ported meshes and their blueprint templates bring imports no
        // bundle the level loads carries; one such import faults the load at game+0x1522B97).
        public static int ExecuteClosure(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, pluginsDir = null; bool link = true, audit = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--frosty-plugins" && i + 1 < args.Length) pluginsDir = args[++i];
                else if (args[i] == "--no-link") link = false;
                else if (args[i] == "--audit") audit = true;
                else if (!args[i].StartsWith("--")) level = args[i];
                else { Console.Error.WriteLine("level closure: unknown option " + args[i]); return 1; }
            }
            if (level == null) { Console.Error.WriteLine("usage: level closure <new level, e.g. AddedContent/MyMod/Levels/MyLevel_01/MyLevel_01> [--frosty-plugins <dir>] [--no-link] [--audit]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level closure: no project loaded (use --project)."); return 1; }
            if (pluginsDir == null) pluginsDir = @"F:\Frosty\Frosty Editor\Plugins";
            string cacheHost = Path.GetDirectoryName(pluginsDir.TrimEnd('\\', '/'));
            var o = new LevelCreator.Options { Log = s => logger.Log("level closure: " + s), Audit = audit };
            o.ChunkMetaProvider = () =>
            {
                Assembly asm = BundleManagerHost.LoadPlugin(pluginsDir, logger);
                return BundleManagerHost.LoadChunkMeta(asm, cacheHost, logger);
            };
            if (link)
                o.LinkSubResources = e =>
                {
                    string t = e.Type ?? "";
                    if (t.IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) < 0 && t.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) return;
                    List<ulong> rids; List<Guid> cids;
                    BundleCommand.GatherSubResources(e, out rids, out cids);
                    BundleCommand.LinkSubResources(e, rids, cids);
                };
            if (DryRun.Active) { DryRun.Report(logger, "closure of {0}", level); return 0; }
            LevelCreator.Result r;
            try { r = new LevelCreator(o).Close(level); }
            catch (Exception ex) { Console.Error.WriteLine("level closure: " + (ex.InnerException ?? ex).Message); return 1; }
            foreach (string w in r.Warnings) logger.LogWarning("level closure: " + w);
            formatter.WriteResult(r.ChunksWithH32Zero == 0 ? "OK" : "WARN", "level closure", new Dictionary<string, string>
            {
                { "level", r.Level }, { "closureAdded", r.ClosureAdded.ToString() }, { "closureRes", r.ClosureRes.ToString() },
                { "chunkMetaFromCache", r.ChunkMetaSet.ToString() }, { "chunksWithH32Zero", r.ChunksWithH32Zero.ToString() },
                { "auditMissing", audit ? r.AuditMissing.ToString() : "(not run; --audit)" }, { "auditRootLate", audit ? r.AuditRootLate.ToString() : "(not run; --audit)" }, { "auditStageLate", audit ? r.AuditStageLate.ToString() : "(not run; --audit)" },
            });
            return 0;
        }

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var o = new LevelCreator.Options();
            string pluginsDir = null, navgen = null, terrainFile = null, heights = null, layers = null;
            bool link = true;
            SkirmishShell skirmish = null;
            // buildings (ObjectBlueprints) from --prefab: placed after the level is built, the way `level add` places them
            // (a proper internal id, fields through the property writer, the sub-world's own network registry) -- the
            // same entities built inside level create left every bot inert (TK7-TK12, 2026-09-24); level add's worked (TP2)
            var buildings = new List<string>();
            // the play-area boundary for the new layout (`level combat-area --area`): the level otherwise keeps its mode
            // host's arena polygon, at the host's coordinates
            List<double[]> combatArea = null; double? combatFloor = null, combatHeight = null;
            // modes besides Arcade (--modes Blast,HeroesVersusVillains,...): read first, --skirmish-from plans with them
            string[] modes = null;
            // the level's loading screen (`level loading-screen`): "host" = the terrain host's, or an image / .webm of its own;
            // and its Kyber declaration (`kyber declare`), named after --menu-name, with the level's modes
            string loadingScreen = null, kyberName = null; bool kyber = false;
            int mi = Array.IndexOf(args, "--modes");
            if (mi >= 0 && mi + 1 < args.Length) modes = args[mi + 1].Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                Func<string> next = () => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
                if (a == "--terrain-from") o.TerrainHost = next();
                // a Skirmish level with bots on a shipped map's shell; options after it override what it sets
                else if (a == "--skirmish-from")
                {
                    try { skirmish = SkirmishShell.Plan(App.AssetManager, next(), s => logger.Log("level create: skirmish: " + s), args.Contains("--skirmish-content"), modes); }
                    catch (InvalidOperationException ex) { Console.Error.WriteLine("level create: " + ex.Message); return 1; }
                    skirmish.Apply(o);
                }
                // --navgen [navgen.dll]: build the navmesh too; without a path the navgen built next to FrostyCLI.exe
                else if (a == "--navgen") navgen = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "";
                else if (a == "--modes") { next(); if (skirmish == null) Console.Error.WriteLine("level create: --modes is read with --skirmish-from"); }
                else if (a == "--skirmish-content") { }   // read by --skirmish-from: keep the map's content sub-worlds, emptied
                // with --skirmish-from: a heightmap (and layer maps) built into the map's own terrain on the way
                else if (a == "--heights") heights = next();
                else if (a == "--layers") layers = next();
                else if (a == "--terrain-res")
                {
                    // `terrain create --host <the terrain host's terrain> --out <file>`: the level gets its own terrain
                    string f = next();
                    if (!File.Exists(f)) throw new ArgumentException("--terrain-res: file not found: " + f);
                    terrainFile = f;
                    o.TerrainPayload = File.ReadAllBytes(f);
                    var parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(o.TerrainPayload);
                    if (!parsed.Success) throw new ArgumentException("--terrain-res: " + f + " is not a terrain streaming tree: " + parsed.Failure);
                }
                else if (a == "--terrain-keep") o.TerrainKeep = next().Split(',');
                else if (a == "--mode-from") o.ModeHost = next();
                else if (a == "--mode") o.Mode = next();
                else if (a == "--mode-subworld") o.ModeSubWorld = next();
                else if (a == "--mode-keep") o.ModeKeep = next().Split(',');
                else if (a == "--ve") o.VisualEnvironment = next();
                else if (a == "--spawn-y") o.SpawnY = double.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--arena-offset") o.ArenaOffset = next().Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                else if (a == "--generated-spawns") o.ArenaMode = false;
                else if (a == "--spawn") o.SpawnCenter = next().Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                else if (a == "--spawn-radius") o.SpawnRadius = double.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--frosty-plugins") pluginsDir = next();
                else if (a == "--no-link") link = false;
                else if (a == "--audit") o.Audit = true;
                else if (a == "--keep-spawns") o.KeepSpawns = true;
                else if (a == "--mirror-pathfinding") o.MirrorPathfinding = true;
                else if (a == "--permute-spawns") { o.KeepSpawns = true; o.PermuteSpawns = true; }
                else if (a == "--mode-offset") o.ModeOffset = next().Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                else if (a == "--spawn-offset") { o.KeepSpawns = true; o.SpawnOffset = next().Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray(); }
                else if (a == "--mode-mirror") { string v = next(); o.ModeMirrorExtra = v == "none" ? new string[0] : v.Split(','); }
                else if (a == "--menu-name") o.MenuName = next();
                else if (a == "--planet-name") o.PlanetName = next();
                else if (a == "--menu-description") o.MenuDescription = next();
                else if (a == "--combat-area")
                {
                    try { combatArea = CombatArea.ParsePolygon(next()); }
                    catch (Exception ex) { Console.Error.WriteLine("level create: --combat-area: " + ex.Message); return 1; }
                }
                else if (a == "--combat-area-floor") combatFloor = double.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--combat-area-height") combatHeight = double.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--loading-screen") loadingScreen = next();
                else if (a == "--kyber") { kyber = true; if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) kyberName = next(); }
                else if (a == "--vehicle") o.Vehicles.Add(next());
                // any spatial prefab (a building, a wall...), placed like a vehicle spawner; its content ships via the closure
                else if (a == "--prefab")
                {
                    string spec = next();
                    int at = spec.LastIndexOf('@');
                    var be = at > 0 ? App.AssetManager.GetEbxEntry(spec.Substring(0, at)) : null;
                    if (be != null && be.Type == "ObjectBlueprint") buildings.Add(spec); else o.Vehicles.Add(spec);
                }
                else if (a == "--vehicle-layer") o.VehicleLayer = next();
                else if (a == "--mirror-bundle") o.MirrorBundles.AddRange(next().Split(',').Select(v => v.Trim()).Where(v => v.Length > 0));
                else if (a == "--mode-start-event") o.ModeStartEvent = next();
                else if (a == "--keep-mode-lists") o.KeepModeLists = true;
                else if (a == "--also-modes") o.AlsoModes = next().Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToArray();
                else if (a == "--bots") o.BotsPerTeam = int.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--bots-team1") o.BotsTeam1 = int.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--bots-team2") o.BotsTeam2 = int.Parse(next(), CultureInfo.InvariantCulture);
                else if (a == "--audit-find") { o.Audit = true; o.AuditFind = next(); }
                else if (a.StartsWith("--")) { Console.Error.WriteLine("level create: unknown option " + a); return 1; }
                else o.Destination = a;
            }
            if (heights != null)
            {
                if (skirmish == null || skirmish.TerrainData == null)
                { Console.Error.WriteLine("level create: --heights needs --skirmish-from a map with a terrain (or build it with terrain create and pass --terrain-res)"); return 1; }
                terrainFile = Path.Combine(Path.GetTempPath(), "frostycli-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".terrain");
                var t = new List<string> { "create", "--heights", heights, "--host", skirmish.TerrainData.ToLowerInvariant() + ".streamingtree_win32", "--out", terrainFile };
                if (layers != null) { t.Add("--layers"); t.Add(layers); }
                int trc = new TerrainCommand().Execute(t.ToArray(), logger, formatter);
                if (trc != 0 || !File.Exists(terrainFile)) { Console.Error.WriteLine("level create: terrain create failed"); return 1; }
                o.TerrainPayload = File.ReadAllBytes(terrainFile);
            }
            if (o.Destination == null)
            {
                Console.Error.WriteLine("usage: level create <new-level e.g. AddedContent/MyMod/Levels/MyLevel_01/MyLevel_01> [--terrain-from <level>] [--terrain-res <terrain create output>] [--mode-from <level>] [--mode <name>] [--ve <VisualEnvironmentBlueprint>] [--spawn x,y,z] [--spawn-radius m]");
                return 1;
            }
            if (o.SpawnCenter.Length != 3) { Console.Error.WriteLine("level create: --spawn takes x,y,z"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level create: no project loaded (use --project or --save-as)."); return 1; }
            if (pluginsDir == null) pluginsDir = @"F:\Frosty\Frosty Editor\Plugins";
            string cacheHost = Path.GetDirectoryName(pluginsDir.TrimEnd('\\', '/'));
            o.Log = s => logger.Log("level create: " + s);
            o.ChunkMetaProvider = () =>
            {
                Assembly asm = BundleManagerHost.LoadPlugin(pluginsDir, logger);
                return BundleManagerHost.LoadChunkMeta(asm, cacheHost, logger);
            };
            if (link)
                o.LinkSubResources = e =>
                {
                    string t = e.Type ?? "";
                    if (t.IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) < 0 && t.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) return;
                    List<ulong> rids; List<Guid> cids;
                    BundleCommand.GatherSubResources(e, out rids, out cids);
                    BundleCommand.LinkSubResources(e, rids, cids);
                };
            ILocalizedStringDatabase sdb = LocalizedStringDatabase.Current;
            if (LocalizationHost.IsReal(sdb)) { o.SetString = (h, t) => sdb.SetString(h, t); o.GetString = h => sdb.GetString(h); }
            else if (o.MenuName != null) logger.LogWarning("level create: no string database - the menu entry will show missing strings");
            if (DryRun.Active) { DryRun.Report(logger, "create {0}", o.Destination); return 0; }

            LevelCreator.Result r;
            try { r = new LevelCreator(o).Run(); }
            catch (Exception ex) { Console.Error.WriteLine("level create: " + (ex.InnerException ?? ex).Message); if (Environment.GetEnvironmentVariable("FROSTYCLI_TRACE") == "1") Console.Error.WriteLine(ex.ToString()); return 1; }
            foreach (string w in r.Warnings) logger.LogWarning("level create: " + w);
            formatter.WriteResult(r.ChunksWithH32Zero == 0 ? "OK" : "WARN", "level create", new Dictionary<string, string>
            {
                { "level", r.Level }, { "description", r.Description }, { "modeSubWorld", r.ModeSubWorld },
                { "structureAssets", r.Cloned.ToString() }, { "bundles", r.Bundles.ToString() }, { "pruned", r.Pruned.ToString() },
                { "refsDropped", r.RefsDropped.ToString() }, { "spawns", r.Spawns.ToString() }, { "closureAdded", r.ClosureAdded.ToString() }, { "auditMissing", r.AuditMissing.ToString() },
                { "chunkMetaFromCache", r.ChunkMetaSet.ToString() }, { "chunksWithH32Zero", r.ChunksWithH32Zero.ToString() },
                { "menuEntry", r.MenuEntry ?? "" }, { "menuPlanet", r.MenuPlanet ?? "" }, { "menuLists", r.MenuListsUpdated.Count.ToString() },
                { "terrain", r.Terrain ?? "(the host's, shared)" },
                { "load", "hookspawn " + r.Level + " " + o.Mode },
            });
            if (combatArea != null)
            {
                int crc = LevelCombatArea.Apply(App.AssetManager, r.Level, combatArea, combatFloor, combatHeight, null,
                                                s => logger.Log("level create: combat area: " + s), out int rewritten, out _);
                if (crc != 0) { Console.Error.WriteLine("level create: --combat-area: the new level has no combat-area link to rewrite"); return crc; }
                logger.Log("level create: combat area: " + rewritten + " shape(s) fitted to the new layout");
            }
            if (loadingScreen != null)
            {
                byte[] video = null;
                try
                {
                    if (loadingScreen.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) video = File.ReadAllBytes(loadingScreen);
                    else if (!loadingScreen.Equals("host", StringComparison.OrdinalIgnoreCase)) video = LevelLoadingScreen.EncodeStill(loadingScreen, 10, "vp8", logger);
                    LevelLoadingScreen.Apply(App.AssetManager, r.Level, o.TerrainHost, video, s => logger.Log("level create: loading screen: " + s));
                }
                catch (Exception ex) { Console.Error.WriteLine("level create: --loading-screen: " + ex.Message); return 1; }
            }
            if (kyber)
            {
                var decl = KyberCommand.Read();
                string img = null;
                if (loadingScreen != null && !loadingScreen.Equals("host", StringComparison.OrdinalIgnoreCase)
                    && !loadingScreen.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                    img = Convert.ToBase64String(KyberCommand.Jpeg(loadingScreen));
                KyberDeclaration.UpsertMap(decl, kyberName ?? o.MenuName, r.Level, new[] { o.Mode }.Concat(o.AlsoModes), img);
                KyberCommand.Write(decl);
                logger.Log("level create: Kyber: " + (kyberName ?? o.MenuName ?? r.Level) + " declared for " + string.Join(", ", new[] { o.Mode }.Concat(o.AlsoModes)));
            }
            if (buildings.Count > 0)
            {
                string layerPath = r.Level.Substring(0, r.Level.LastIndexOf('/') + 1) + o.VehicleLayer;
                if (skirmish == null || skirmish.ContentSubWorlds.Count == 0)
                    logger.LogWarning("level create: buildings go in " + layerPath + "; without --skirmish-content their meshes and collision are not loaded with the level");
                foreach (string spec in buildings)
                {
                    int at = spec.LastIndexOf('@');
                    string[] v = spec.Substring(at + 1).Split(',');
                    var add = new List<string> { layerPath, "--blueprint", spec.Substring(0, at), "--entity-class", "ObjectReferenceObjectData",
                                                 "--pos", string.Join(",", v.Take(3)) };
                    if (v.Length > 3) { add.Add("--rot"); add.Add(v[3] + ",0,0"); }
                    int arc = LevelCommand.ExecuteAdd(add.ToArray(), logger, formatter);
                    if (arc != 0) { Console.Error.WriteLine("level create: could not place " + spec); return arc; }
                }
            }
            if (skirmish == null) return 0;

            // the navmesh: generated from the new level round the Skirmish play space, through the Skirmish stream
            if (skirmish.Streams.Count == 0 || skirmish.Play == null)
            { logger.LogWarning("level create: no Skirmish stream or play space found -- run navmesh build by hand"); return 0; }
            // round the play space: the new combat area when one was given (the whole area players can reach; AI ignores
            // the combat area, so the navmesh ends at it), else the host's Skirmish play space plus 30 m
            double margin = combatArea != null ? 2 : 30;
            double[] play = combatArea != null ? CombatArea.Bounds(combatArea) : skirmish.Play;
            string region = string.Join(",", new[] { play[0] - margin, play[2] - margin, play[1] + margin, play[3] + margin }
                .Select(v => Math.Round(v).ToString(CultureInfo.InvariantCulture)));
            var nav = new List<string> { "build", r.Level, "--region", region, "--stream", skirmish.Streams[0].ToString(),
                                         "--terrain-step", "0.5", "--cs", "0.2", "--ch", "0.1", "--climb", "0.5" };
            if (terrainFile != null) { nav.Add("--terrain"); nav.Add(terrainFile); }
            if (navgen == null)
            {
                logger.Log("level create: next, the navmesh: navmesh " + string.Join(" ", nav) + " --navgen <navgen.dll>");
                return 0;
            }
            if (navgen.Length > 0) { nav.Add("--navgen"); nav.Add(navgen); }
            if (skirmish.Streams.Count > 1) logger.LogWarning("level create: " + skirmish.Streams.Count + " Skirmish streams; the navmesh goes through " + skirmish.Streams[0]);
            int nrc = new NavmeshCommand().Execute(nav.ToArray(), logger, formatter);
            if (nrc != 0) return nrc;
            // the extra modes' own streams (co-op / Instant Action missions read Tatooine's 5c0e8973): the same ground, built
            // into each of them
            // round every spawn of the level too: the co-op / Instant Action missions play far past the arena
            double[] all = SpawnBounds(App.AssetManager, r.Level);
            string extraRegion = all == null ? region : string.Join(",", new[]
            {
                Math.Min(play[0] - margin, all[0] - 30), Math.Min(play[2] - margin, all[2] - 30),
                Math.Max(play[1] + margin, all[1] + 30), Math.Max(play[3] + margin, all[3] + 30),
            }.Select(v => Math.Round(v).ToString(CultureInfo.InvariantCulture)));
            foreach (var kv in skirmish.ExtraStreams)
            {
                var extraNav = new List<string>(nav);
                extraNav[extraNav.IndexOf("--stream") + 1] = kv.Key.ToString();
                extraNav[extraNav.IndexOf("--region") + 1] = extraRegion;
                logger.Log("level create: navmesh for " + kv.Value + " through stream " + kv.Key);
                nrc = new NavmeshCommand().Execute(extraNav.ToArray(), logger, formatter);
                if (nrc != 0) return nrc;
            }
            return 0;
        }
    }
}
