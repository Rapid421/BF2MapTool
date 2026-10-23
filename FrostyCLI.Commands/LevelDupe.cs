using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostySdk.IO;
using FrostyCLI.Core;
using FrostyLevelTools;

namespace FrostyCLI.Commands
{
    // `level dupe <shipped-level> --as <new-level>` - a separately loadable copy of a shipped Battlefront II level
    // under a name that did not ship. The work is done by FrostyLevelTools.LevelDuplicator (FrostyCLI.Core), the
    // same engine the LevelDuplicatorPlugin runs inside Frosty Editor; this command supplies the CLI's two
    // host services: the headless BundleManager cache reader and the mesh/texture sub-resource linker.
    // Rules and evidence: I:\New Levels\FINDINGS.md.
    public static class LevelDupe
    {
        internal const string DefaultTypes = LevelDuplicator.DefaultTypes;

        // Include filter (as LevelCreator's): the kept partitions and sub-world bundles of the host, plus the MVDBs and
        // network registries shipped in a kept bundle
        private static Func<string, string, bool> KeepFilter(string host, string[] keep)
        {
            AssetManager am = App.AssetManager;
            string h = host.Trim().Trim('/');
            string dir = h.Substring(0, h.LastIndexOf('/') + 1);
            var names = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            Func<string, bool> keptBundle = b =>
            {
                string inner = b.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? b.Substring(6) : b;
                return inner.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && names.Contains(inner.Substring(dir.Length));
            };
            return (name, type) =>
            {
                if (type == "bundle") return keptBundle(name);
                if (type == "LevelDescriptionAsset") return false;
                if (type == "MeshVariationDatabase" || type == "NetworkRegistryAsset")
                {
                    EbxAssetEntry e = am.GetEbxEntry(name);
                    foreach (int b in e?.Bundles ?? new List<int>()) if (keptBundle(am.GetBundleEntry(b).Name)) return true;
                    return false;
                }
                return name.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && names.Contains(name.Substring(dir.Length));
            };
        }

        private static IEnumerable<string> Closure(string host, string[] roots, string[] exclude)
        {
            AssetManager am = App.AssetManager;
            string h = host.Trim().Trim('/');
            string dir = h.Substring(0, h.LastIndexOf('/') + 1);
            var ex = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = new Queue<string>(roots);
            while (q.Count > 0)
            {
                string n = q.Dequeue();
                if (ex.Contains(n) || !seen.Add(n)) continue;
                EbxAssetEntry e = am.GetEbxEntry(dir + n);
                if (e == null) continue;
                EbxAsset a = am.GetEbx(e);
                foreach (Guid g in a.Dependencies)
                {
                    EbxAssetEntry d = am.GetEbxEntry(g);
                    if (d != null && d.Name.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) q.Enqueue(d.Name.Substring(dir.Length));
                }
                foreach (object o in a.Objects)
                {
                    if (o.GetType().Name != "SubWorldReferenceObjectData") continue;
                    string b = o.GetType().GetProperty("BundleName")?.GetValue(o)?.ToString() ?? "";
                    if (b.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) q.Enqueue(b.Substring(dir.Length));
                }
            }
            return seen;
        }

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string src = null, dst = null, typeList = DefaultTypes, pluginsDir = null, menuName = null, planetName = null;
            bool mirror = true, copyRes = true, link = true, graft = false, planOnly = false;
            string[] keep = null, closure = null, exclude = new string[0];
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) dst = args[++i];
                else if (args[i] == "--types" && i + 1 < args.Length) typeList = args[++i];
                else if (args[i] == "--frosty-plugins" && i + 1 < args.Length) pluginsDir = args[++i];
                else if (args[i] == "--no-mirror") mirror = false;
                else if (args[i] == "--no-level-res") copyRes = false;
                else if (args[i] == "--no-link") link = false;
                // --graft: clone part of <shipped-level> into the directory of --as (an existing level, or a sub-directory
                // of it so the host's partition names cannot clash with the level's own); no registration, no description
                else if (args[i] == "--graft") graft = true;
                // --keep a,b,c: only these partitions / sub-world bundles (names relative to the host's directory)
                else if (args[i] == "--keep" && i + 1 < args.Length) keep = args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                else if (args[i] == "--plan") planOnly = true;
                // --keep-closure a,b: the kept set = these partitions + every host partition they reach (layer / prefab
                // imports and sub-world BundleNames), not entering --exclude x,y (names relative to the host's directory)
                else if (args[i] == "--keep-closure" && i + 1 < args.Length) closure = args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                else if (args[i] == "--exclude" && i + 1 < args.Length) exclude = args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                else if (args[i] == "--menu-name" && i + 1 < args.Length) menuName = args[++i];
                else if (args[i] == "--planet-name" && i + 1 < args.Length) planetName = args[++i];   // cache-only chunk metadata, as the editor plugin runs
                else if (args[i].StartsWith("--")) { Console.Error.WriteLine("level dupe: unknown option " + args[i]); return 1; }
                else src = args[i];
            }
            if (src == null || dst == null)
            {
                Console.Error.WriteLine("usage: level dupe <shipped-level e.g. Levels/MP/Tatooine_01/Tatooine_01> --as <new-level e.g. Levels/MP/MyMap_01/MyMap_01>");
                return 1;
            }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level dupe: no project loaded (use --project or --save-as)."); return 1; }
            if (pluginsDir == null) pluginsDir = @"F:\Frosty\Frosty Editor\Plugins";
            string cacheHost = Path.GetDirectoryName(pluginsDir.TrimEnd('\\', '/'));

            if (closure != null)
            {
                var k = new List<string>(Closure(src, closure, exclude));
                if (keep != null) k.AddRange(keep);
                keep = k.ToArray();
                logger.Log("level dupe: keep closure " + keep.Length + ": " + string.Join(",", keep));
            }
            var options = new LevelDuplicator.Options
            {
                Source = src, Destination = dst, Types = typeList, Mirror = mirror, CopyLevelRes = copyRes,
                Log = s => logger.Log("level dupe: " + s),
                MenuName = menuName, PlanetName = planetName, Graft = graft,
                Include = keep == null ? null : KeepFilter(src, keep),
                ChunkMetaProvider = () =>
                {
                    Assembly asm = BundleManagerHost.LoadPlugin(pluginsDir, logger);
                    return BundleManagerHost.LoadChunkMeta(asm, cacheHost, logger);
                },
                LinkSubResources = !link ? (Action<EbxAssetEntry>)null : e =>
                {
                    List<ulong> rids; List<Guid> cids;
                    BundleCommand.GatherSubResources(e, out rids, out cids);
                    BundleCommand.LinkSubResources(e, rids, cids);
                },
            };
            ILocalizedStringDatabase sdb = LocalizedStringDatabase.Current;
            if (LocalizationHost.IsReal(sdb)) { options.SetString = (h, t) => sdb.SetString(h, t); options.GetString = h => sdb.GetString(h); }
            var dup = new LevelDuplicator(options);

            if (DryRun.Active || planOnly)
            {
                List<KeyValuePair<string, string>> plan;
                try { plan = dup.Plan(); } catch (Exception ex) { Console.Error.WriteLine("level dupe: " + ex.Message); return 1; }
                DryRun.Report(logger, "dupe {0} -> {1}: {2} structure asset(s)", options.Source, options.Destination, plan.Count);
                foreach (var kv in plan) logger.Log("           + " + kv.Value);
                return 0;
            }

            LevelDuplicator.Result r;
            try { r = dup.Run(); }
            catch (Exception ex) { Console.Error.WriteLine("level dupe: " + (ex.InnerException ?? ex).Message); if (Environment.GetEnvironmentVariable("FROSTYCLI_TRACE") == "1") Console.Error.WriteLine(ex.ToString()); return 1; }
            foreach (string w in r.Warnings) logger.LogWarning("level dupe: " + w);

            formatter.WriteResult(r.ChunksWithH32Zero == 0 ? "OK" : "WARN", "level dupe", new Dictionary<string, string>
            {
                { "from", options.Source }, { "level", r.Level }, { "structureAssets", r.StructureAssets.ToString() },
                { "stringsRetargeted", r.StringsRetargeted.ToString() }, { "referencesRemapped", r.ReferencesRemapped.ToString() },
                { "levelResCopied", r.LevelResCopied.ToString() }, { "bundles", r.Bundles.ToString() },
                { "description", r.Description }, { "descriptionFileGuid", r.DescriptionFileGuid },
                { "levelGuid", r.LevelGuid }, { "mirroredEbx", r.MirroredEbx.ToString() },
                { "mirroredRes", r.MirroredRes.ToString() }, { "mirroredChunks", r.MirroredChunks.ToString() },
                { "chunkMetaFromCache", r.ChunkMetaSet.ToString() }, { "chunksWithH32Zero", r.ChunksWithH32Zero.ToString() },
                { "load", "hookspawn " + r.Level + " <a mode from the description's GameMode list>" },
            });
            return 0;
        }
    }
}
