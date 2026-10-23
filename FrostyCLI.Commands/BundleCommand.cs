using System;
using System.Collections.Generic;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // Bundle membership management (a Frosty GUI capability via BundleEditorPlugin). Lets a headless
    // workflow list bundles, see which bundles an asset is in, and add an asset to a bundle so the
    // built mod ships it in that bundle.
    public class BundleCommand : ICommand
    {
        public string Name => "bundle";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli bundle <ls|new|add|remove|contents|mirror|complete> ..."); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "ls":     return ExecuteLs(Sub(args), logger, formatter);
                case "new":    return ExecuteNew(Sub(args), logger, formatter);
                case "manifest": return ExecuteManifest(Sub(args), logger, formatter);
                case "add":    return ExecuteAdd(Sub(args), logger, formatter);
                case "remove": return ExecuteRemove(Sub(args), logger, formatter);
                case "contents": return ExecuteContents(Sub(args), logger, formatter);
                case "residency": return ExecuteResidency(Sub(args), logger, formatter);
                case "mirror": return ExecuteMirror(Sub(args), logger, formatter);
                case "depots": return ExecuteDepots(Sub(args), logger, formatter);
                case "complete": return ExecuteComplete(Sub(args), logger, formatter);
                case "prune-meshes": return ExecutePruneMeshes(Sub(args), logger, formatter);
                case "hoist-list": return ExecuteHoistList(Sub(args), logger, formatter);
                default:
                    Console.Error.WriteLine("bundle: unknown subcommand '" + args[0] + "'. Use ls, new, add, remove, or complete.");
                    return 1;
            }
        }

        private static string[] Sub(string[] args)
        {
            var r = new string[args.Length - 1];
            Array.Copy(args, 1, r, 0, r.Length);
            return r;
        }

        // Resolves an asset path to its AssetEntry, trying ebx, then res, then chunk (GUID).
        //
        // `kind` ("ebx", "res" or "chunk") looks in that table only. Without it the EBX wins, and Frosty's EBX
        // lookup ignores case, so a res that shares its owner's name -- every texture's does
        // (FX/Textures/X vs fx/textures/x) -- could never be named: `bundle add <res>` found the EBX, reported
        // "already: true", and the res never reached the bundle. Five imported 2015 particle atlases shipped
        // that way (EBX and pixel chunk bundled, res in no bundle) and the level hung at load until the stall
        // watchdog fired on its "Resources" timer (game+0x5495740).
        internal static AssetEntry ResolveAsset(string name, string kind = null)
        {
            AssetEntry e;
            Guid g;
            bool isGuid = Guid.TryParse(name, out g);
            if (kind == null || kind == "ebx")
            {
                e = App.AssetManager.GetEbxEntry(name);
                if (e == null && isGuid) e = App.AssetManager.GetEbxEntry(g);   // an ebx partition (file) guid
                if (e != null || kind != null) return e;
            }
            if (kind == null || kind == "res")
            {
                e = App.AssetManager.GetResEntry(name);
                if (e != null || kind != null) return e;
            }
            if (isGuid && (kind == null || kind == "chunk"))
                return App.AssetManager.GetChunkEntry(g);
            return null;
        }

        // Parses `--kind ebx|res|chunk`; returns false (after printing why) on an unknown kind.
        internal static bool TryParseKind(string value, string command, out string kind)
        {
            kind = value?.ToLowerInvariant();
            if (kind == null || kind == "ebx" || kind == "res" || kind == "chunk") return true;
            Console.Error.WriteLine(command + ": --kind must be ebx, res or chunk (got '" + value + "')");
            return false;
        }

        internal static BundleEntry FindBundle(string bundleName)
        {
            foreach (BundleEntry b in App.AssetManager.EnumerateBundles())
                if (string.Equals(b.Name, bundleName, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        // `bundle contents <bundle> [--out file.tsv]` lists every ebx / res / chunk that is a member of a bundle
        // (shipped or project-added), one per line: kind, name, added.
        public static int ExecuteContents(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string name = null, outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (!args[i].StartsWith("--")) name = args[i];
            }
            if (name == null) { Console.Error.WriteLine("bundle contents: missing <bundle>"); return 1; }
            BundleEntry be = FindBundle(name);
            if (be == null) { Console.Error.WriteLine("bundle contents: bundle not found: " + name); return 1; }
            int bid = App.AssetManager.GetBundleId(be);
            var lines = new List<string>();
            int ne = 0, nr = 0, nc = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                if (e.Bundles.Contains(bid) || e.AddedBundles.Contains(bid)) { lines.Add("ebx\t" + e.Name + "\t" + e.AddedBundles.Contains(bid)); ne++; }
            foreach (ResAssetEntry r in App.AssetManager.EnumerateRes())
                if (r.Bundles.Contains(bid) || r.AddedBundles.Contains(bid)) { lines.Add("res\t" + r.Name + "\t" + r.AddedBundles.Contains(bid)); nr++; }
            foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks())
                if (c.Bundles.Contains(bid) || c.AddedBundles.Contains(bid)) { lines.Add("chunk\t" + c.Id + "\t" + c.AddedBundles.Contains(bid) + "\th32=" + c.H32 + "\tfirstMip=" + c.FirstMip + "\tlogical=" + c.LogicalOffset + "/" + c.LogicalSize); nc++; }
            if (outPath != null) System.IO.File.WriteAllLines(outPath, lines);
            else foreach (string l in lines) Console.WriteLine(l);
            formatter.WriteResult("OK", "bundle contents", new Dictionary<string, string>
            { { "bundle", be.Name }, { "ebx", ne.ToString() }, { "res", nr.ToString() }, { "chunks", nc.ToString() } });
            return 0;
        }

        // `bundle hoist-list <root bundle> --prefix <added bundle name prefix> --reachable-from <asset>... --out <file>`
        //
        // A partition resolves its imports in its own bundle's domain and that bundle's parents. A prefab that lives in the
        // level ROOT bundle and references a child prefab only a SUB-LEVEL bundle holds (PF_EnterVehicle_Interact: in the
        // mode sub-world's bundle) gets a null child: no crash, the child is just never instantiated. This lists every
        // asset reachable (asset-index dependencies) from the given roots that sits in an added bundle under the prefix
        // but not in the root bundle; add them to the root bundle with `bundle add --with-resources`.
        public static int ExecuteHoistList(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string root = null, prefix = null, outPath = null; var roots = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--prefix" && i + 1 < args.Length) prefix = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "--reachable-from" && i + 1 < args.Length) roots.Add(args[++i]);
                else if (!args[i].StartsWith("--")) root = args[i];
            }
            if (root == null || prefix == null || roots.Count == 0)
            { Console.Error.WriteLine("bundle hoist-list: usage <root bundle> --prefix <added bundle prefix> --reachable-from <asset>... [--out f]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle hoist-list: no project loaded."); return 1; }
            var am = App.AssetManager;
            BundleEntry rb = FindBundle(root);
            if (rb == null) { Console.Error.WriteLine("bundle hoist-list: bundle not found: " + root); return 1; }
            int rid = am.GetBundleId(rb);
            var under = new HashSet<int>();
            foreach (BundleEntry b in am.EnumerateBundles())
                if (b.Added && b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) under.Add(am.GetBundleId(b));
            var reach = new HashSet<Guid>(); var stack = new Stack<EbxAssetEntry>();
            foreach (string r in roots)
            {
                EbxAssetEntry e = am.GetEbxEntry(r);
                if (e == null) { Console.Error.WriteLine("bundle hoist-list: not found: " + r); return 1; }
                if (reach.Add(e.Guid)) stack.Push(e);
            }
            var lines = new List<string>(); int inRoot = 0, elsewhere = 0, nowhere = 0;
            while (stack.Count > 0)
            {
                EbxAssetEntry e = stack.Pop();
                bool isInRoot = e.AddedBundles.Contains(rid) || e.Bundles.Contains(rid);
                bool inChild = e.AddedBundles.Any(b => b != rid && under.Contains(b));
                if (isInRoot) inRoot++;
                else if (inChild) lines.Add(e.Type + "\t" + e.Name);
                else if (e.Bundles.Count > 0 || e.AddedBundles.Count > 0) elsewhere++;
                else nowhere++;
                foreach (Guid d in e.EnumerateDependencies())
                {
                    if (!reach.Add(d)) continue;
                    EbxAssetEntry de = am.GetEbxEntry(d);
                    if (de != null) stack.Push(de);
                }
            }
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            if (outPath != null) System.IO.File.WriteAllLines(outPath, lines); else foreach (string l in lines) Console.WriteLine(l);
            formatter.WriteResult("OK", "bundle hoist-list", new Dictionary<string, string>
            {
                { "root", rb.Name }, { "reachable", reach.Count.ToString() }, { "inRoot", inRoot.ToString() },
                { "onlyInSubLevelBundles", lines.Count.ToString() }, { "onlyElsewhere", elsewhere.ToString() }, { "inNoBundle", nowhere.ToString() }
            });
            return 0;
        }

        // `bundle prune-meshes <bundle name prefix> [--apply] [--out f] [--keep <path substring>]...`
        //
        // BFII's MeshStreamingManager holds at most 2,048 unique MeshAssets at once (level + shared + runtime soldiers and
        // vehicles); every mesh partition in a loaded bundle counts, used or not. This finds the mesh assets in the ADDED
        // bundles under the prefix that nothing the level keeps can reach: a mesh is removable when every partition that
        // imports it (transitively, inside those bundles) is itself removable, and a partition of a level-structure type
        // (level / sub-world / layer data, MeshVariationDatabase, network registry, level description ...) never is. So a
        // placed or registered blueprint, or a mesh with a MeshVariationDb entry, keeps its mesh. With --apply the mesh,
        // its importers, their companion RES (<name>, <name>_mesh/blocks, <name>_physics_win32 ...) and the mesh's linked
        // chunks leave those bundles. Dependencies come from the asset index (no partition is loaded).
        public static int ExecutePruneMeshes(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string prefix = null, outPath = null; bool apply = false; var keep = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--apply") apply = true;
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "--keep" && i + 1 < args.Length) keep.Add(args[++i].ToLowerInvariant());
                else if (!args[i].StartsWith("--")) prefix = args[i];
            }
            if (prefix == null) { Console.Error.WriteLine("bundle prune-meshes: usage <bundle name prefix> [--apply] [--out f] [--keep <substring>]..."); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle prune-meshes: no project loaded."); return 1; }
            var am = App.AssetManager;
            var bids = new HashSet<int>();
            foreach (BundleEntry b in am.EnumerateBundles())
                if (b.Added && b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) bids.Add(am.GetBundleId(b));
            if (bids.Count == 0) { Console.Error.WriteLine("bundle prune-meshes: no added bundle under " + prefix); return 1; }
            List<EbxAssetEntry> inSet = am.EnumerateEbx().Where(e => e.AddedBundles.Any(bids.Contains)).ToList();
            var byGuid = new Dictionary<Guid, EbxAssetEntry>();
            foreach (EbxAssetEntry e in inSet) byGuid[e.Guid] = e;
            var importers = new Dictionary<Guid, List<EbxAssetEntry>>();
            foreach (EbxAssetEntry e in inSet)
                foreach (Guid d in e.EnumerateDependencies())
                {
                    if (!byGuid.ContainsKey(d) || d == e.Guid) continue;
                    List<EbxAssetEntry> l;
                    if (!importers.TryGetValue(d, out l)) importers[d] = l = new List<EbxAssetEntry>();
                    l.Add(e);
                }
            Func<EbxAssetEntry, bool> structural = e =>
            {
                string t = e.Type ?? "";
                return t == "MeshVariationDatabase" || t == "NetworkRegistryAsset" || t == "LevelData" || t == "SubWorldData" || t == "LayerData"
                    || t == "LevelDescriptionAsset" || t == "MaterialGridData" || t.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("Enlighten", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0
                    || e.IsAdded || keep.Any(k => e.Name.ToLowerInvariant().Contains(k));
            };
            var remove = new HashSet<Guid>(); int meshes = 0, kept = 0, prunedMeshes = 0;
            var withVariation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ResAssetEntry re in am.EnumerateRes())
            {
                string n = re.Name;
                if (!n.EndsWith("/shaderblocks_variation/blocks", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = n.Split('/');
                if (parts.Length < 3) continue;
                string seg = parts[parts.Length - 3];
                int us = seg.LastIndexOf('_');
                if (us > 0) withVariation.Add(seg.Substring(0, us));
            }
            var lines = new List<string>();
            foreach (EbxAssetEntry m in inSet.Where(e => (e.Type ?? "").IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                meshes++;
                var chain = new HashSet<Guid> { m.Guid }; var stack = new Stack<EbxAssetEntry>(); stack.Push(m);
                bool ok = !structural(m); string why = null;
                string shortName = m.Name.Substring(m.Name.LastIndexOf('/') + 1);
                if (ok && withVariation.Contains(shortName)) { ok = false; why = "has an ObjectVariation shader-block depot"; }
                while (ok && stack.Count > 0)
                {
                    EbxAssetEntry cur = stack.Pop();
                    List<EbxAssetEntry> l;
                    if (!importers.TryGetValue(cur.Guid, out l)) continue;
                    foreach (EbxAssetEntry imp in l)
                    {
                        if (structural(imp)) { ok = false; why = imp.Type + " " + imp.Name; break; }
                        if (chain.Add(imp.Guid)) stack.Push(imp);
                    }
                }
                if (!ok) { kept++; lines.Add("keep\t" + m.Name + "\t" + why); continue; }
                prunedMeshes++;
                foreach (Guid g in chain) remove.Add(g);
                lines.Add("prune\t" + m.Name + "\t" + string.Join(" | ", chain.Where(g => g != m.Guid).Select(g => byGuid[g].Type + " " + byGuid[g].Name).Take(6)));
            }
            int ebxOut = 0, resOut = 0, chunkOut = 0, resReverted = 0;
            if (apply && !DryRun.Active)
            {
                var resByName = am.EnumerateRes().Where(x => x.AddedBundles.Any(bids.Contains)).ToList();
                var resIndex = new Dictionary<string, List<ResAssetEntry>>();
                foreach (Guid g in remove)
                {
                    EbxAssetEntry e = byGuid[g];
                    List<ulong> rids; List<Guid> cids;
                    try { GatherSubResources(e, out rids, out cids); } catch { rids = new List<ulong>(); cids = new List<Guid>(); }
                    foreach (int b in bids) if (e.AddedBundles.Remove(b)) ebxOut++;
                    string pre = e.Name.ToLowerInvariant();
                    foreach (ResAssetEntry re in resByName)
                    {
                        string n = re.Name.ToLowerInvariant();
                        if (!n.StartsWith(pre, StringComparison.Ordinal) || (n.Length > pre.Length && "_/.".IndexOf(n[pre.Length]) < 0)) continue;
                        // a longer name that is another asset's own name (protontorpedo -> protontorpedo_prequel_mesh) stays
                        string rest = n.Substring(pre.Length);
                        if (rest.Length > 0 && rest != "_mesh/blocks" && rest != "/blocks" && rest != "_occludermesh" && !rest.StartsWith("_physics_win32") && !rest.StartsWith("_mesh/")) continue;
                        if (re.IsAdded) continue;
                        // the physics res belongs to the <name>_Physics_Win32 HavokAsset ebx: it only leaves with that ebx
                        if (rest.StartsWith("_physics_win32"))
                        {
                            EbxAssetEntry pe = am.GetEbxEntry(re.Name);
                            if (pe != null && !remove.Contains(pe.Guid) && pe.AddedBundles.Any(bids.Contains)) continue;
                        }
                        foreach (int b in bids) if (re.AddedBundles.Remove(b)) resOut++;
                        if (re.IsModified && re.AddedBundles.Count == 0) { am.RevertAsset(re); resReverted++; }
                    }
                    foreach (ulong rid in rids)
                    {
                        ResAssetEntry re = am.GetResEntry(rid);
                        if (re == null || re.IsAdded) continue;
                        EbxAssetEntry own = am.GetEbxEntry(re.Name);
                        if (own != null && own.Guid != e.Guid && !remove.Contains(own.Guid) && own.AddedBundles.Any(bids.Contains)) continue;
                        foreach (int b in bids) if (re.AddedBundles.Remove(b)) resOut++;
                        if (re.IsModified && re.AddedBundles.Count == 0) { am.RevertAsset(re); resReverted++; }
                    }
                    if ((e.Type ?? "").IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) >= 0)
                        foreach (Guid c in cids) { ChunkAssetEntry ce = am.GetChunkEntry(c); if (ce != null) foreach (int b in bids) if (ce.AddedBundles.Remove(b)) chunkOut++; }
                }
            }
            if (outPath != null) System.IO.File.WriteAllLines(outPath, lines);
            formatter.WriteResult("OK", "bundle prune-meshes", new Dictionary<string, string>
            {
                { "bundles", bids.Count.ToString() }, { "ebxInBundles", inSet.Count.ToString() }, { "meshes", meshes.ToString() },
                { "kept", kept.ToString() }, { "prunable", prunedMeshes.ToString() }, { "partitionsInChains", remove.Count.ToString() },
                { "applied", apply ? "true" : "false" }, { "ebxMemberships", ebxOut.ToString() }, { "resMemberships", resOut.ToString() }, { "resReverted", resReverted.ToString() }, { "chunkMemberships", chunkOut.ToString() }
            });
            return 0;
        }

        // `bundle residency <bundle>... [--mounted <superbundle substring>]... [--out f]`: every EBX, res and chunk the project ADDED
        // to the bundles without modifying it carries no data of its own -- the game reads it from wherever it originally
        // ships. Lists each by the superbundles of its original bundles, and flags those whose original superbundles are all
        // outside --mounted (a mesh LOD chunk that only an SP superbundle holds crashed the level at game+0x6E6ED6A four
        // seconds after world-up: the missing stream's SP blast-door bisect). Exit 2 when anything is flagged.
        public static int ExecuteResidency(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var names = new List<string>(); var mounted = new List<string>(); string outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mounted" && i + 1 < args.Length) mounted.Add(args[++i].ToLowerInvariant());
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (!args[i].StartsWith("--")) names.Add(args[i]);
            }
            if (names.Count == 0) { Console.Error.WriteLine("bundle residency: missing <bundle>"); return 1; }
            var bids = new HashSet<int>();
            foreach (string n in names)
            {
                BundleEntry be = FindBundle(n);
                if (be == null) { Console.Error.WriteLine("bundle residency: bundle not found: " + n); return 1; }
                bids.Add(App.AssetManager.GetBundleId(be));
            }
            var sbOfBundle = new Dictionary<int, string>();
            Func<int, string> sbName = b =>
            {
                string v;
                if (sbOfBundle.TryGetValue(b, out v)) return v;
                v = "?";
                try
                {
                    // the superbundle's name when the catalog carries one; else the bundle's own path, trimmed to its level /
                    // shared-bundle root (win32/levels/mp/hoth_01/hoth_01/...) -- BFII's cache names no superbundles
                    BundleEntry be = App.AssetManager.GetBundleEntry(b);
                    var sb = be != null ? App.AssetManager.GetSuperBundle(be.SuperBundleId) : null;
                    if (sb != null && !string.IsNullOrEmpty(sb.Name) && sb.Name != "<none>") v = sb.Name;
                    else if (be != null)
                    {
                        string[] parts = be.Name.ToLowerInvariant().Split('/');
                        v = string.Join("/", parts.Take(Math.Min(parts.Length, 5)));
                    }
                }
                catch { }
                sbOfBundle[b] = v;
                return v;
            };
            var lines = new List<string>();
            var bySb = new Dictionary<string, int>();
            int flagged = 0, added = 0;
            Action<string, string, AssetEntry> check = (kind, name, e) =>
            {
                bool inAdded = false;
                foreach (int b in bids) if (e.AddedBundles.Contains(b)) inAdded = true;
                bool hasData = e.ModifiedEntry != null && (e.ModifiedEntry.Data != null || e.ModifiedEntry.DataObject != null);
                if (!inAdded || hasData || e.IsAdded) return;
                added++;
                var sbs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (int b in e.Bundles) sbs.Add(sbName(b));
                bool ok = mounted.Count == 0;
                foreach (string sb in sbs) foreach (string m in mounted) if (sb.ToLowerInvariant().Contains(m)) ok = true;
                string key = string.Join(" | ", sbs);
                int c; bySb.TryGetValue(key, out c); bySb[key] = c + 1;
                if (!ok) { flagged++; lines.Add("OUTSIDE	" + kind + "	" + name + "	" + key); }
                else lines.Add("ok	" + kind + "	" + name + "	" + key);
            };
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx()) check("ebx", e.Name, e);
            foreach (ResAssetEntry r in App.AssetManager.EnumerateRes()) check("res", r.Name, r);
            foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks()) check("chunk", c.Id.ToString(), c);
            if (outPath != null) System.IO.File.WriteAllLines(outPath, lines);
            foreach (var kv in bySb) Console.WriteLine(kv.Value.ToString().PadLeft(6) + "  " + kv.Key);
            foreach (string l in lines) if (l.StartsWith("OUTSIDE")) Console.WriteLine(l);
            formatter.WriteResult(flagged == 0 ? "OK" : "WARN", "bundle residency", new Dictionary<string, string>
            { { "bundles", names.Count.ToString() }, { "added_unmodified", added.ToString() }, { "outside_mounted", flagged.ToString() } });
            return flagged == 0 ? 0 : 2;
        }

        // `bundle mirror <src-bundle> --to <dst-bundle> [--skip-prefix <p>]...` adds every member of src to dst
        // (membership only - no data is copied). Used to make a new level root bundle a superset of a shipped
        // root, because a shipped root carries assets the pipeline hoisted out of its child sub-levels.
        // Chunks keep their catalog H32/FirstMip; the mod writer copies those into the new bundle's entry.
        public static int ExecuteMirror(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string src = null, dst = null, clonedFrom = null, clonedTo = null, onlyType = null; var skip = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--to" && i + 1 < args.Length) dst = args[++i];
                else if (args[i] == "--skip-cloned" && i + 1 < args.Length)
                {
                    // <from-prefix>=<to-prefix>: skip an EBX when its counterpart under <to-prefix> exists (it was
                    // scaffolded; the clone ships in its own bundle and the original must not ride along).
                    string[] kv = args[++i].Split('=');
                    if (kv.Length == 2) { clonedFrom = kv[0]; clonedTo = kv[1]; }
                }
                else if (args[i] == "--skip-prefix" && i + 1 < args.Length) skip.Add(args[++i]);
                // --only-type <substring>: mirror only EBX whose type contains it (e.g. TextureAsset) and only their own
                // res/chunks - leaf assets of a host bundle without its meshes/blueprints
                else if (args[i] == "--only-type" && i + 1 < args.Length) onlyType = args[++i];
                else if (!args[i].StartsWith("--")) src = args[i];
            }
            if (src == null || dst == null) { Console.Error.WriteLine("bundle mirror: usage <src-bundle> --to <dst-bundle>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle mirror: no project loaded."); return 1; }
            BundleEntry sb = FindBundle(src), db = FindBundle(dst);
            if (sb == null || db == null) { Console.Error.WriteLine("bundle mirror: bundle not found"); return 1; }
            int sid = App.AssetManager.GetBundleId(sb), did = App.AssetManager.GetBundleId(db);
            Func<string, bool> skipped = n => { foreach (string p in skip) if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true; return false; };
            Func<EbxAssetEntry, bool> skipEbx = e => skipped(e.Name)
                || (clonedFrom != null && e.Name.StartsWith(clonedFrom, StringComparison.OrdinalIgnoreCase)
                    && App.AssetManager.GetEbxEntry(clonedTo + e.Name.Substring(clonedFrom.Length)) != null);
            // the payloads of a skipped EBX stay out too (a skipped mesh's MeshSet res + LOD chunks, a skipped texture's
            // res + pixel chunk), and a skip prefix applies to res names as well: mirroring a host's Content minus its
            // architecture must not carry the city's geometry along as orphaned res/chunks
            var skipResIds = new HashSet<ulong>(); var skipChunkIds = new HashSet<Guid>();
            if (skip.Count > 0)
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (!(e.Bundles.Contains(sid) || e.AddedBundles.Contains(sid)) || !skipped(e.Name)) continue;
                    string t = e.Type ?? "";
                    if (t.IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) < 0 && t.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { List<ulong> rids; List<Guid> cids; GatherSubResources(e, out rids, out cids); skipResIds.UnionWith(rids); skipChunkIds.UnionWith(cids); }
                    catch (Exception ex) { logger.LogWarning("bundle mirror: could not read sub-resources of skipped {0}: {1}", e.Name, ex.Message); }
                }
            Func<ResAssetEntry, bool> skipRes = skip.Count == 0 ? (Func<ResAssetEntry, bool>)null : re => skipped(re.Name) || skipResIds.Contains(re.ResRid);
            Func<ChunkAssetEntry, bool> skipChunk = skip.Count == 0 ? (Func<ChunkAssetEntry, bool>)null : c => skipChunkIds.Contains(c.Id);
            if (onlyType != null)
            {
                var keepRes = new HashSet<ulong>(); var keepChunk = new HashSet<Guid>();
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (!(e.Bundles.Contains(sid) || e.AddedBundles.Contains(sid)) || skipEbx(e)) continue;
                    if ((e.Type ?? "").IndexOf(onlyType, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { List<ulong> rids; List<Guid> cids; GatherSubResources(e, out rids, out cids); keepRes.UnionWith(rids); keepChunk.UnionWith(cids); }
                    catch (Exception ex) { logger.LogWarning("bundle mirror: could not read sub-resources of {0}: {1}", e.Name, ex.Message); }
                }
                Func<EbxAssetEntry, bool> baseSkip = skipEbx;
                skipEbx = e => baseSkip(e) || (e.Type ?? "").IndexOf(onlyType, StringComparison.OrdinalIgnoreCase) < 0;
                skipRes = re => !keepRes.Contains(re.ResRid);
                skipChunk = c => !keepChunk.Contains(c.Id);
            }
            MirrorResult m = Mirror(sid, did, skipEbx, logger, skipRes, skipChunk);
            int ne = m.Ebx, nr = m.Res, nc = m.Chunks, ns = m.Skipped, nl = m.Linked, h32zero = m.H32Zero;
            formatter.WriteResult("OK", "bundle mirror", new Dictionary<string, string>
            { { "from", sb.Name }, { "to", db.Name }, { "ebx", ne.ToString() }, { "res", nr.ToString() },
              { "chunks", nc.ToString() }, { "skipped", ns.ToString() }, { "linkedAssets", nl.ToString() },
              { "chunksWithH32Zero", h32zero.ToString() } });
            return 0;
        }



        internal sealed class MirrorResult { public int Ebx, Res, Chunks, Skipped, Linked, H32Zero; }

        // Adds every member of bundle sid to bundle did (membership only). Mirrored meshes and textures are
        // linked to their res so their chunks get H32 / FirstMip: a shipped chunk carries those only in its own
        // bundle's metadata, the catalog entry reads H32 0, and a texture chunk shipped in a new bundle with
        // H32 0 fails at D3D creation (E_INVALIDARG raised from game+0x161C7BD).
        internal static MirrorResult Mirror(int sid, int did, Func<EbxAssetEntry, bool> skipEbx, ExportLogger logger, Func<ResAssetEntry, bool> skipRes = null, Func<ChunkAssetEntry, bool> skipChunk = null)
        {
            var r = new MirrorResult();
            var linked = new List<EbxAssetEntry>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                if (e.Bundles.Contains(sid) || e.AddedBundles.Contains(sid))
                {
                    if (skipEbx != null && skipEbx(e)) { r.Skipped++; continue; }
                    if (e.AddToBundle(did)) r.Ebx++;
                    linked.Add(e);
                }
            foreach (EbxAssetEntry e in linked)
            {
                string t = e.Type ?? "";
                if (t.IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) < 0 && t.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    List<ulong> rids; List<Guid> cids;
                    GatherSubResources(e, out rids, out cids);
                    LinkSubResources(e, rids, cids);
                    r.Linked++;
                }
                catch (Exception ex) { logger.LogWarning("bundle mirror: could not link {0}: {1}", e.Name, ex.Message); }
            }
            foreach (ResAssetEntry re in App.AssetManager.EnumerateRes())
                if (re.Bundles.Contains(sid) || re.AddedBundles.Contains(sid))
                {
                    if (skipRes != null && skipRes(re)) { r.Skipped++; continue; }
                    if (re.AddToBundle(did)) r.Res++;
                }
            foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks())
                if (c.Bundles.Contains(sid) || c.AddedBundles.Contains(sid))
                {
                    if (skipChunk != null && skipChunk(c)) { r.Skipped++; continue; }
                    if (c.AddToBundle(did)) r.Chunks++;
                }
            foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks())
                if (c.AddedBundles.Contains(did) && c.H32 == 0) r.H32Zero++;
            return r;
        }

        public static int ExecuteLs(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string asset = null; int limit = 500;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
                else if (!args[i].StartsWith("--")) asset = args[i];
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("bundle ls: AssetManager not initialized."); return 1; }

            if (asset == null)
            {
                // List all bundles.
                int n = 0;
                foreach (BundleEntry b in App.AssetManager.EnumerateBundles())
                {
                    if (limit >= 0 && n >= limit) break;
                    // SuperBundleId matters when creating a bundle (`bundle new`): a level bundle in the
                    // wrong superbundle resolves to nothing at stream time, with no build-time error.
                    // The superbundle NAME is what matters on BFII: FrostyModWriter keys an added
                    // bundle by Fnv1a(superbundle name) and FrostyModExecutor reads that key back as
                    // Fnv1a("<none>"). A mismatch means the bundle silently never reaches the manifest.
                    string sbName = "?";
                    try { var sb = App.AssetManager.GetSuperBundle(b.SuperBundleId); if (sb != null) sbName = sb.Name; }
                    catch { }
                    formatter.WriteResult("OK", "bundle", new Dictionary<string, string>
                    { { "name", b.Name }, { "type", b.Type.ToString() },
                      { "superBundleId", b.SuperBundleId.ToString() }, { "superBundle", sbName },
                      { "added", b.Added ? "true" : "false" } });
                    n++;
                }
                return 0;
            }

            AssetEntry entry = ResolveAsset(asset);
            if (entry == null) { Console.Error.WriteLine("bundle ls: asset not found: " + asset); return 1; }

            var ids = new List<int>();
            ids.AddRange(entry.Bundles);
            foreach (int id in entry.AddedBundles) if (!ids.Contains(id)) ids.Add(id);
            foreach (int id in ids)
            {
                BundleEntry b = App.AssetManager.GetBundleEntry(id);
                bool added = entry.AddedBundles.Contains(id);
                formatter.WriteResult("OK", "bundle", new Dictionary<string, string>
                { { "name", b != null ? b.Name : ("#" + id) }, { "added", added ? "true" : "false" } });
            }
            return 0;
        }

        // Reports whether a bundle is present in the game's manifest. BFII resolves bundles at
        // runtime through the manifest (a hash -> file-range table rebuilt by FrostyModExecutor and
        // written into a fresh cas, with layout.toc's `manifest` entry repointed at it), not by name.
        // A bundle that exists in the project but not in the manifest is invisible to the game, which
        // is exactly the failure mode when standing up a new level: the mod builds and applies clean
        // and the level simply cannot be loaded.
        public static int ExecuteManifest(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string name = null;
            for (int i = 0; i < args.Length; i++) if (!args[i].StartsWith("--")) name = args[i];
            if (name == null) { Console.Error.WriteLine("bundle manifest: missing <bundle-name>"); return 1; }
            if (App.FileSystem == null) { Console.Error.WriteLine("bundle manifest: FileSystem not initialized."); return 1; }

            var bi = App.FileSystem.GetManifestBundle(name);
            var d = new Dictionary<string, string> { { "bundle", name }, { "inManifest", bi != null ? "true" : "false" } };
            if (bi != null)
            {
                d["hash"] = bi.hash.ToString();
                try { d["fileCount"] = bi.files != null ? bi.files.Count.ToString() : "0"; } catch { }
            }
            BundleEntry be = FindBundle(name);
            d["inProject"] = be != null ? "true" : "false";
            if (be != null) d["added"] = be.Added ? "true" : "false";
            formatter.WriteResult("OK", "bundle manifest", d);
            return 0;
        }

        // Creates a bundle (a Frosty GUI capability via BundleEditorPlugin's "Add Bundle"). Without
        // this the CLI can only put assets into bundles the game already ships, which blocks standing
        // up a new level: BFII resolves a level from its path, and the matching root bundle
        // `win32/levels/mp/<name>/<name>` has to exist or streaming opens nothing. Frosty persists
        // added bundles in the .fbproject as {name, superbundle, type} and replays them through
        // AssetManager.AddBundle on load (FrostyProject.cs:659, :1092), so a bundle made here survives
        // a save/load round-trip and ships as a `type 4 Bundle` resource in the built .fbmod.
        //
        // --like copies type and superbundle from an existing bundle, which is the safe default for a
        // level clone: a wrong SuperBundleId fails silently at stream time rather than at build time.
        public static int ExecuteNew(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string name = null, typeName = null, sbName = null, like = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--type" && i + 1 < args.Length) typeName = args[++i];
                else if (args[i] == "--superbundle" && i + 1 < args.Length) sbName = args[++i];
                else if (args[i] == "--like" && i + 1 < args.Length) like = args[++i];
                else if (!args[i].StartsWith("--")) name = args[i];
            }
            if (name == null) { Console.Error.WriteLine("bundle new: missing <name> (e.g. win32/levels/mp/sullust_01/sullust_01)"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle new: no project loaded. Run 'project new' or 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("bundle new: AssetManager not initialized."); return 1; }

            BundleEntry existing = FindBundle(name);
            if (existing != null)
            {
                // Idempotent repair: a bundle created before this set Blueprint would otherwise
                // stay null forever, since re-running the command short-circuits here.
                string repaired = "unchanged";
                if (existing.Added && existing.Blueprint == null)
                {
                    string bn = existing.Name;
                    int sl = bn.IndexOf('/');
                    if (sl > 0 && bn.Substring(0, sl).IndexOf("win32", StringComparison.OrdinalIgnoreCase) >= 0)
                        bn = bn.Substring(sl + 1);
                    try
                    {
                        EbxAssetEntry bp = App.AssetManager.GetEbxEntry(bn);
                        if (bp != null) { existing.Blueprint = bp; repaired = "blueprint set to " + bp.Name; }
                        else repaired = "no asset named '" + bn + "'";
                    }
                    catch (Exception ex) { repaired = "failed: " + ex.Message; }
                }
                formatter.WriteResult("OK", "bundle new", new Dictionary<string, string>
                { { "bundle", existing.Name }, { "type", existing.Type.ToString() },
                  { "blueprint", existing.Blueprint != null ? existing.Blueprint.Name : "none" },
                  { "repair", repaired },
                  { "created", "false" }, { "note", "bundle already exists" } });
                return 0;
            }

            // Template bundle: --like supplies both type and superbundle unless overridden explicitly.
            BundleEntry template = null;
            if (like != null)
            {
                template = FindBundle(like);
                if (template == null) { Console.Error.WriteLine("bundle new: --like bundle not found: " + like); return 1; }
            }

            BundleType type;
            if (typeName != null)
            {
                switch (typeName.ToLowerInvariant())
                {
                    case "sublevel":  type = BundleType.SubLevel; break;
                    case "blueprint": type = BundleType.BlueprintBundle; break;
                    case "shared":    type = BundleType.SharedBundle; break;
                    default:
                        Console.Error.WriteLine("bundle new: unknown --type '" + typeName + "'. Use sublevel, blueprint, or shared.");
                        return 1;
                }
            }
            else if (template != null) type = template.Type;
            else type = BundleType.SubLevel;

            // SuperBundleId is an index, not a hash: resolve by name, or inherit from --like. Default 0
            // matches DuplicationPlugin's AddBundle("win32/" + name, BlueprintBundle, 0).
            int sbIndex;
            if (sbName != null)
            {
                sbIndex = App.AssetManager.GetSuperBundleId(sbName);
                if (sbIndex < 0) { Console.Error.WriteLine("bundle new: superbundle not found: " + sbName); return 1; }
            }
            else if (template != null) sbIndex = template.SuperBundleId;
            else sbIndex = 0;

            if (DryRun.Active)
            {
                DryRun.Report(logger, "create bundle {0} (type {1}, superbundle {2})", name, type, sbIndex);
                return 0;
            }

            BundleEntry bentry;
            try { bentry = App.AssetManager.AddBundle(name, type, sbIndex); }
            catch (Exception ex) { Console.Error.WriteLine("bundle new: AddBundle failed: " + ex.Message); return 1; }
            if (bentry == null) { Console.Error.WriteLine("bundle new: AddBundle returned null for " + name); return 1; }

            // An added bundle also needs its Blueprint: the EBX asset whose path matches the
            // bundle name with the platform prefix stripped. BundleManager does exactly this before
            // it completes anything ("make sure any added bundles have the correct .Blueprint
            // assigned"), which is a strong hint the runtime side needs it too -- a sub-level bundle
            // with a null Blueprint has nothing naming what it actually contains.
            string blueprintName = name;
            int slash = blueprintName.IndexOf('/');
            if (slash > 0 && blueprintName.Substring(0, slash).IndexOf("win32", StringComparison.OrdinalIgnoreCase) >= 0)
                blueprintName = blueprintName.Substring(slash + 1);
            string blueprintNote = "none";
            try
            {
                EbxAssetEntry bp = App.AssetManager.GetEbxEntry(blueprintName);
                if (bp != null) { bentry.Blueprint = bp; blueprintNote = bp.Name; }
                else blueprintNote = "no asset named '" + blueprintName + "'";
            }
            catch (Exception ex) { blueprintNote = "failed: " + ex.Message; }

            formatter.WriteResult("OK", "bundle new", new Dictionary<string, string>
            { { "bundle", bentry.Name }, { "type", bentry.Type.ToString() },
              { "superBundleId", bentry.SuperBundleId.ToString() },
              { "blueprint", blueprintNote },
              { "added", bentry.Added ? "true" : "false" }, { "created", "true" } });
            return 0;
        }

        // bundle depots: every mod-added mesh's shader block depot (<mesh>_mesh/blocks) joins every bundle its mesh is in.
        // The repair for projects built before `bundle add --with-resources` carried the depot: a mesh whose depot the level
        // does not load draws nothing (126 meshes of HOTH_L2 -- the ion cannon, the rocks on the hills, the trench floor boards,
        // the hangar props -- flagged by `level validate`'s mesh-depot-not-bundled). A mesh with no depot res is reported.
        public static int ExecuteDepots(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle depots: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("bundle depots: AssetManager not initialized."); return 1; }
            // --match <s> (repeatable): only meshes whose path contains one of them; --skip <s> (repeatable): leave those out.
            // For bisecting which newly drawing meshes a problem comes from.
            var match = new List<string>(); var skip = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--match" && i + 1 < args.Length) match.Add(args[++i]);
                else if (args[i] == "--skip" && i + 1 < args.Length) skip.Add(args[++i]);
            }
            int meshes = 0, joined = 0, depotsTouched = 0, unbundledMeshes = 0, texturesAdded = 0;
            var noDepot = new List<string>();
            var texNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true).ToList())
            {
                if (!e.IsAdded || !IsMeshType(e.Type)) continue;
                if (match.Count > 0 && !match.Exists(m => e.Name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (skip.Exists(m => e.Name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                meshes++;
                ResAssetEntry depot = App.AssetManager.GetResEntry(DepotResName(e.Name));
                if (depot == null) { noDepot.Add(e.Name); continue; }
                var bundles = e.EnumerateBundles().ToList();
                if (bundles.Count == 0) { unbundledMeshes++; continue; }
                int before = joined;
                if (DryRun.Active) { foreach (int b in bundles) if (!depot.IsInBundle(b)) joined++; }
                else foreach (int b in bundles) if (depot.AddToBundle(b)) joined++;
                if (joined > before) { depotsTouched++; logger.Log("bundle depots: {0} -> {1} bundle(s)", depot.Name, joined - before); }
                // ...and every texture that depot binds (by root instance guid, in no EBX dependency list): a loaded depot
                // naming an unbundled texture crashes the load at game+0x1522B97 (HOTH_L2F: T_XWingCockpitWindowDirt_02_C,
                // a donor section's leftover bound by a geometry port)
                int scanned;
                foreach (EbxAssetEntry tex in LevelCommand.DepotTextures(e, out scanned))
                    foreach (int b in bundles)
                    {
                        if (tex.IsInBundle(b)) continue;
                        if (!DryRun.Active) AddWithResources(tex, b);
                        texturesAdded++;
                        if (texNames.Add(tex.Name)) logger.Log("bundle depots: texture {0} (bound by {1})", tex.Name, depot.Name);
                    }
            }
            // ...and every SHIPPED mesh's depot this mod pulls into a bundle (`bundle add <shipped mesh> --with-resources` takes
            // the depot along since 3ec80fb, so the mesh finally draws): its textures must come too. HOTH_N3 crashed on load
            // (game+0x1522B97, T_Dots_01_D) through the shipped light-shaft cone's depot the FX closure bundled.
            int shippedDepots = 0;
            foreach (ResAssetEntry dep in App.AssetManager.EnumerateRes((uint)FrostySdk.Managers.ResourceType.ShaderBlockDepot).ToList())
            {
                if (dep.AddedBundles.Count == 0) continue;
                int cut = dep.Name.LastIndexOf("_mesh/", StringComparison.OrdinalIgnoreCase);
                if (cut <= 0) continue;
                EbxAssetEntry owner = App.AssetManager.GetEbxEntry(dep.Name.Substring(0, cut));
                if (owner == null || owner.IsAdded) continue;   // new meshes: handled above
                if (match.Count > 0 && !match.Exists(m => owner.Name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (skip.Exists(m => owner.Name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                shippedDepots++;
                int scanned;
                foreach (EbxAssetEntry tex in LevelCommand.DepotTextures(owner, out scanned))
                    foreach (int b in dep.AddedBundles.ToList())
                    {
                        if (tex.IsInBundle(b)) continue;
                        if (!DryRun.Active) AddWithResources(tex, b);
                        texturesAdded++;
                        if (texNames.Add(tex.Name)) logger.Log("bundle depots: texture {0} (bound by shipped {1})", tex.Name, dep.Name);
                    }
            }
            foreach (string n in noDepot) logger.LogWarning("bundle depots: mesh {0} has no shader block depot res", n);
            formatter.WriteResult("OK", "bundle depots", new Dictionary<string, string>
            {
                { "added_meshes", meshes.ToString() }, { "depots_joined", depotsTouched.ToString() }, { "memberships_added", joined.ToString() },
                { "meshes_in_no_bundle", unbundledMeshes.ToString() }, { "meshes_without_depot", noDepot.Count.ToString() },
                { "depot_textures_bundled", texNames.Count.ToString() }, { "texture_memberships_added", texturesAdded.ToString() },
                { "shipped_depots_checked", shippedDepots.ToString() }
            });
            return 0;
        }

        // An EBX into a bundle with its linked res and chunks, as `bundle add --with-resources` does (no output).
        internal static void AddWithResources(EbxAssetEntry entry, int bid)
        {
            List<ulong> resRids; List<Guid> chunkIds;
            GatherSubResources(entry, out resRids, out chunkIds);
            entry.AddToBundle(bid);
            LinkSubResources(entry, resRids, chunkIds);
            foreach (ulong rid in resRids) { ResAssetEntry re = App.AssetManager.GetResEntry(rid); if (re != null) re.AddToBundle(bid); }
            foreach (Guid cid in chunkIds) { ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(cid); if (ce != null) ce.AddToBundle(bid); }
        }

        public static int ExecuteAdd(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string asset = null, bundle = null, kindArg = null;
            bool withResources = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--to" && i + 1 < args.Length) bundle = args[++i];
                else if (args[i] == "--kind" && i + 1 < args.Length) kindArg = args[++i];
                else if (args[i] == "--with-resources") withResources = true;
                else if (!args[i].StartsWith("--")) asset = args[i];
            }
            if (asset == null)  { Console.Error.WriteLine("bundle add: missing <asset>"); return 1; }
            if (bundle == null) { Console.Error.WriteLine("bundle add: missing --to <bundle>"); return 1; }
            if (!TryParseKind(kindArg, "bundle add", out string kind)) return 1;
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle add: no project loaded. Run 'project new' or 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("bundle add: AssetManager not initialized."); return 1; }

            AssetEntry entry = ResolveAsset(asset, kind);
            if (entry == null) { Console.Error.WriteLine("bundle add: " + (kind ?? "asset") + " not found: " + asset); return 1; }
            BundleEntry bentry = FindBundle(bundle);
            if (bentry == null) { Console.Error.WriteLine("bundle add: bundle not found: " + bundle); return 1; }

            int bid = App.AssetManager.GetBundleId(bentry);
            List<ulong> resRids = new List<ulong>(); List<Guid> chunkIds = new List<Guid>();
            if (withResources && entry is EbxAssetEntry resourceOwner)
            {
                try { GatherSubResources(resourceOwner, out resRids, out chunkIds); }
                catch (Exception ex) { Console.Error.WriteLine("bundle add: resource preflight failed: " + ex.Message); return 1; }
            }
            if (DryRun.Active) { DryRun.Report(logger, "add {0} to bundle {1}{2}", asset, bentry.Name, withResources ? " (with linked res+chunks)" : ""); return 0; }
            bool added;
            try { added = entry.AddToBundle(bid); }
            catch (Exception ex) { Console.Error.WriteLine("bundle add: AddToBundle failed: " + ex.Message); return 1; }

            // --with-resources: also pull the asset's linked res + chunks into the same bundle, so a
            // (new) mesh/texture ships everything it needs in that bundle. Porting BundleEditorPlugin's
            // per-type sub-resource linking: mesh -> MeshSet res + LOD geometry chunks; texture -> Texture
            // res + pixel chunk; other single-`Resource` assets (atlas/svg/ies) -> their res.
            int resLinked = 0, chunkLinked = 0, chunkPresent = 0, chunkRefused = 0;
            if (withResources && entry is EbxAssetEntry ebxEntry)
            {
                LinkSubResources(ebxEntry, resRids, chunkIds);
                foreach (ulong rid in resRids)
                {
                    ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                    if (re != null && re.AddToBundle(bid)) resLinked++;
                }
                // A mesh's shader block depot (<mesh>_mesh/blocks) is no ResourceRef of the EBX -- the engine finds it by name --
                // so the link walk above never reached it. A mesh whose depot is in no bundle the level loads draws NOTHING, with
                // no crash and no warning: 84 of the Hoth 2015 port's 85 geometry ports (ion cannon, rock walls, floor boards,
                // crates) shipped that way through `port import --bundle`, and so did every `mesh clone-radiosity` clone.
                ResAssetEntry depot = IsMeshType(ebxEntry.Type) ? App.AssetManager.GetResEntry(DepotResName(ebxEntry.Name)) : null;
                if (depot != null && depot.AddToBundle(bid)) resLinked++;
                foreach (Guid cid in chunkIds)
                {
                    ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(cid);
                    if (ce == null) continue;
                    if (ce.AddToBundle(bid)) { chunkLinked++; continue; }
                    // FrostySdk's ChunkAssetEntry.AddToBundle refuses any chunk that is in no bundle
                    // and was not added by a mod (`Bundles.Count == 0 && !IsAdded`) -- which is every
                    // vanilla superbundle-resident chunk, and that is how BFII stores sound data.
                    // Counting that refusal as a plain zero made it indistinguishable from "already
                    // there": `bundle add <shipped SoundWaveAsset> --with-resources` reported OK with
                    // chunkLinked 0, put the EBX in the bundle, and left its audio payload out, with
                    // no build-time error and silence in game.
                    if (ce.IsInBundle(bid)) chunkPresent++; else chunkRefused++;
                }
            }
            if (chunkRefused > 0)
                logger.LogWarning(
                    "bundle add: {0} of {1} chunk(s) were NOT linked into {2} -- they are vanilla "
                    + "superbundle-resident chunks, which FrostySdk refuses to add to a bundle. The EBX "
                    + "is bundled but its payload is not, so the game loads nothing. Import the asset as "
                    + "a new one instead of bundling the shipped copy (for sound: audio export + audio import).",
                    chunkRefused, chunkIds.Count, bentry.Name);

            formatter.WriteResult("OK", "bundle add", new Dictionary<string, string>
            { { "asset", asset }, { "bundle", bentry.Name }, { "already", added ? "false" : "true" },
              { "resLinked", resLinked.ToString() }, { "chunkLinked", chunkLinked.ToString() },
              { "chunkAlreadyPresent", chunkPresent.ToString() }, { "chunkRefused", chunkRefused.ToString() } });
            return 0;
        }

        // Links an asset's res and chunks to it the way Frosty's Bundle Editor does when it adds an
        // asset to a bundle. The link is what fills the chunk's H32 (the hash of its owning res name)
        // and, for textures, FirstMip; the mod writer copies both into the bundle's chunk entry, and
        // the game cannot load a mesh or texture chunk whose entry carries H32 0 (verified against a
        // shipped map mod: same chunks, only these two fields differed, and the level crashed on load).
        /// <summary>The res that holds a mesh's shader block depot: the mesh's name, lower-cased, + "_mesh/blocks"
        /// (`objects/props/x/x_mesh` -> `objects/props/x/x_mesh_mesh/blocks`).</summary>
        internal static string DepotResName(string meshName)
        {
            return string.IsNullOrEmpty(meshName) ? null : meshName.ToLowerInvariant() + "_mesh/blocks";
        }

        internal static bool IsMeshType(string type)
        {
            return !string.IsNullOrEmpty(type) && type.IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static void LinkSubResources(EbxAssetEntry entry, List<ulong> resRids, List<Guid> chunkIds)
        {
            if (entry == null) return;
            string type = entry.Type ?? "";
            // An atlas chunk is read whole, never streamed by mip: BFII's own atlas chunks carry FirstMip -1 in
            // their bundle entries (fx/textures/snow/t_snow_01_d in sharedbundleweapons_common), and a new chunk
            // made by `chunk import --new` starts at 0.
            bool isAtlas = type == "AtlasTextureAsset";
            bool isTexture = !isAtlas && type.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0;
            int firstMip = isTexture ? FrostyCLI.Adapters.Texture.TextureCloner.GetFirstMip(entry) : -1;
            ResAssetEntry primary = null;
            foreach (ulong rid in resRids)
            {
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                if (re == null) continue;
                if (primary == null) primary = re;
                try { entry.LinkAsset(re); } catch { }
            }
            foreach (Guid cid in chunkIds)
            {
                ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(cid);
                if (ce == null) continue;
                if (isTexture) ce.FirstMip = firstMip;
                if (isAtlas)
                {
                    ce.FirstMip = -1;
                    if (ce.ModifiedEntry != null) ce.ModifiedEntry.FirstMip = -1;
                }
                try { if (primary != null) primary.LinkAsset(ce); else entry.LinkAsset(ce); } catch { }
            }
        }

        // The shader block depot res of a mesh asset ("<mesh path lower>_mesh/blocks", ShaderBlockDepot), 0 when it has none.
        internal static string MeshDepotName(string meshAssetName) => (meshAssetName ?? "").ToLowerInvariant() + "_mesh/blocks";

        internal static ulong MeshDepotRid(EbxAssetEntry entry)
        {
            if (entry == null || App.AssetManager == null) return 0;
            ResAssetEntry re = App.AssetManager.GetResEntry(MeshDepotName(entry.Name));
            return re != null ? re.ResRid : 0;
        }

        // The pixel chunk of an atlas resource: bytes 16..32 (after flags/width/height/unknown and the two border
        // floats), in both 2015's 32-byte version 2 and BFII's version 3 (which appends 15 mip sizes).
        internal static Guid AtlasChunkId(byte[] res)
        {
            if (res == null || res.Length < 32) return Guid.Empty;
            byte[] g = new byte[16];
            Array.Copy(res, 16, g, 0, 16);
            return new Guid(g);
        }

        private static byte[] ReadResBytes(ResAssetEntry entry)
        {
            object raw = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) })
                ?.Invoke(App.AssetManager, new object[] { entry });
            if (raw is byte[] b) return b;
            if (raw is System.IO.Stream s)
                using (var ms = new System.IO.MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
            return null;
        }

        // Gathers an EBX asset's linked sub-resources for --with-resources. Tries the mesh + texture
        // per-type resolvers (which know how to reach geometry/pixel chunks), then a generic single-
        // `Resource` fallback (res only) for the other resource-backed types (atlas/svg/ies).
        internal static void GatherSubResources(EbxAssetEntry entry, out List<ulong> resRids, out List<Guid> chunkIds)
        {
            resRids = new List<ulong>(); chunkIds = new List<Guid>();
            ulong rid; List<Guid> chunks;
            // Only run the typed resolvers on their own asset types. Probing a non-mesh (a HavokAsset,
            // say) with the mesh resolver parses its res as a MeshSet, and after that the project's
            // saved EBX came back unreadable -- observed while bundling a member's physics asset.
            string type = entry.Type ?? "";
            bool isMesh = type.IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) >= 0;
            if (type == "AtlasTextureAsset")
            {
                // An atlas resource (0x957C32B1) is flags/width/height/unknown u16, border w/h floats, then the guid
                // of the chunk holding every mip's texels (Frosty AtlasTexturePlugin). The texture resolver below
                // parses a Texture header and fails on it, so an atlas used to bring its res and not its pixels.
                ulong ar = (ulong)((dynamic)App.AssetManager.GetEbx(entry).RootObject).Resource;
                ResAssetEntry are = ar != 0 ? App.AssetManager.GetResEntry(ar) : null;
                if (are == null) return;
                resRids.Add(ar);
                Guid cid = AtlasChunkId(ReadResBytes(are));
                if (cid != Guid.Empty)
                {
                    if (App.AssetManager.GetChunkEntry(cid) == null)
                        throw new InvalidOperationException("Atlas texture references a missing chunk: " + cid);
                    chunkIds.Add(cid);
                }
                return;
            }
            if (type == "SoundWaveAsset")
            {
                dynamic wave = App.AssetManager.GetEbx(entry).RootObject;
                foreach (dynamic chunk in wave.Chunks)
                {
                    Guid id = chunk.ChunkId;
                    if (App.AssetManager.GetChunkEntry(id) == null)
                        throw new InvalidOperationException("Sound wave references a missing chunk: " + id);
                    if (!chunkIds.Contains(id)) chunkIds.Add(id);
                }
                return;
            }
            bool isTexture = type.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0;
            if ((isMesh && FrostyCLI.Adapters.Mesh.MeshCloner.TryGetLinkedResources(entry, out rid, out chunks))
                || (isTexture && FrostyCLI.Adapters.Texture.TextureCloner.TryGetLinkedResources(entry, out rid, out chunks)))
            {
                resRids.Add(rid);
                chunkIds.AddRange(chunks);
                // A mesh draws through its shader block depot ("<mesh>_mesh/blocks"): it binds each section to its
                // shader and textures. Left out, the mesh res and geometry shipped in the level bundle and the depot
                // in none, and the mesh was SILENTLY INVISIBLE -- every 2015 rigid port in the Hoth build (ion cannon,
                // floor boards, rock walls, 120 depots in HOTH_RG2), while the ports whose depot happened to be
                // bundled drew (Atgar towers, DF9). `level add` always carried it; `bundle add --with-resources` did not.
                if (isMesh)
                {
                    ulong blocks = MeshDepotRid(entry);
                    if (blocks != 0 && !resRids.Contains(blocks)) resRids.Add(blocks);
                }
                // A mesh's OccluderMeshResource is a res of its own: left out of the bundle, the reference resolves to
                // -1 at load and the first entity using the mesh faults (game+0x6E6ED6A, read from 0xFFFFFFFFFFFFFFFF --
                // BFII's SP HangarMediumBlastdoor_Left_01 mesh placed in an MP level, MISS_D1/D3).
                if (isMesh) AddOtherResourceRefs(entry, resRids);
                return;
            }
            // Generic single-`Resource` asset (no typed chunk resolution). `Resource` is a ResourceRef
            // -> ulong via its implicit operator (DLR honors it; Convert.ToUInt64 does not).
            try
            {
                dynamic root = App.AssetManager.GetEbx(entry).RootObject;
                ulong r = (ulong)root.Resource;
                if (r != 0) resRids.Add(r);
            }
            catch { /* not a resource-backed asset */ }
            // ...and any OTHER ResourceRef field, whatever it is called. MeshEmitterAsset names its
            // one `MeshEmitterResource`, so the block above missed it entirely and the resource never
            // reached the level bundle -- at load the unresolved ResourceRef came through as -1 and the
            // game faulted dereferencing it (ACCESS_VIOLATION at game+0x95C49C, read from
            // 0xFFFFFFFFFFFFFFFF, right after `and rax, ~3` on the RID/pointer discriminator).
            AddOtherResourceRefs(entry, resRids);
        }

        // Every ResourceRef field of the asset's root other than `Resource` (MeshEmitterResource, a mesh's
        // OccluderMeshResource ...), added once each.
        internal static void AddOtherResourceRefs(EbxAssetEntry entry, List<ulong> resRids)
        {
            try
            {
                object root = App.AssetManager.GetEbx(entry).RootObject;
                foreach (System.Reflection.PropertyInfo pi in root.GetType().GetProperties(
                             System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    if (pi.PropertyType.Name != "ResourceRef" || pi.Name == "Resource") continue;
                    object rr = pi.GetValue(root);
                    if (rr == null) continue;
                    ulong resourceRid = 0;
                    var rp = rr.GetType().GetProperty("ResRid") ?? rr.GetType().GetProperty("ResourceId");
                    if (rp != null && rp.GetValue(rr) != null) resourceRid = Convert.ToUInt64(rp.GetValue(rr));
                    if (resourceRid == 0)
                        foreach (var fi in rr.GetType().GetFields(
                                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                     System.Reflection.BindingFlags.NonPublic))
                            if (fi.FieldType == typeof(ulong)) { resourceRid = (ulong)fi.GetValue(rr); break; }
                    if (resourceRid != 0 && !resRids.Contains(resourceRid)) resRids.Add(resourceRid);
                }
            }
            catch { }
        }

        // Runs FrostyForge's BundleManager plugin over the loaded project -- the "Complete Bundles"
        // pass that rebuilds bundle membership, the sub-level bundle parent chains, the network
        // registries and the mesh-variation database.
        //
        // Nothing else in this CLI can substitute for it. Adding an asset to a level and creating
        // its bundle produces a mod that builds, lints and loads while the engine never streams the
        // bundle, because whether a SubLevel bundle streams is decided by a parent chain that lives
        // outside the EBX. Running this is the last step of authoring level content headlessly.
        //
        // The pass is destructive by design: it begins by clearing every existing bundle edit and
        // reverting the registry/MVDB assets, then recomputes them. Run it on a saved project.
        public static int ExecuteComplete(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string pluginsDir = null, cacheDir = null;
            var levels = new List<string>();
            var options = new List<KeyValuePair<string, object>>();
            bool blockNetworkRegistries = false;
            bool save = true;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--frosty-plugins" && i + 1 < args.Length) pluginsDir = args[++i];
                else if (args[i] == "--cache-dir" && i + 1 < args.Length) cacheDir = args[++i];
                else if (args[i] == "--level" && i + 1 < args.Length) levels.AddRange(args[++i].Split(','));
                else if (args[i] == "--cosmetic") blockNetworkRegistries = true;
                else if (args[i] == "--no-save") save = false;
                else if (args[i] == "--set" && i + 1 < args.Length)
                {
                    string kv = args[++i];
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) { Console.Error.WriteLine("bundle complete: --set expects KEY=VALUE, got '" + kv + "'"); return 1; }
                    string key = kv.Substring(0, eq), val = kv.Substring(eq + 1);
                    bool b; int n;
                    object parsed = bool.TryParse(val, out b) ? (object)b
                                  : int.TryParse(val, out n) ? (object)n : val;
                    options.Add(new KeyValuePair<string, object>(key, parsed));
                }
                else if (args[i].StartsWith("--"))
                { Console.Error.WriteLine("bundle complete: unknown option '" + args[i] + "'"); return 1; }
            }

            if (App.AssetManager == null) { Console.Error.WriteLine("bundle complete: AssetManager not initialized."); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle complete: no project loaded (pass --project)."); return 1; }

            if (pluginsDir == null) pluginsDir = FindEditorPluginsDir();
            if (pluginsDir == null)
            {
                Console.Error.WriteLine("bundle complete: could not locate a Frosty editor Plugins directory containing "
                    + "BundleManager.dll. Pass --frosty-plugins <dir>, set \"handler-plugins\" in frostycli.json, "
                    + "or set FROSTYCLI_FROSTY_PLUGINS.");
                return 1;
            }

            List<int> levelBundles = null;
            if (levels.Count > 0)
            {
                List<string> missing;
                levelBundles = BundleManagerHost.ResolveLevelBundles(levels, out missing);
                if (missing.Count > 0)
                {
                    Console.Error.WriteLine("bundle complete: level bundle not found: " + string.Join(", ", missing.ToArray()));
                    return 1;
                }
            }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "run BundleManager over {0} using {1}",
                    levelBundles == null ? "all allowed bundles" : levels.Count + " level bundle(s)", pluginsDir);
                return 0;
            }

            BundleManagerHost.Result result;
            try
            {
                System.Reflection.Assembly asm = BundleManagerHost.LoadPlugin(pluginsDir, logger);
                if (options.Count > 0) BundleManagerHost.SetOptions(options);
                // The plugin's caches live beside the editor, under <editor>/Caches, and it reaches
                // them through a path relative to the working directory.
                if (cacheDir == null) cacheDir = System.IO.Path.GetDirectoryName(
                    pluginsDir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                result = BundleManagerHost.Run(asm, levelBundles, blockNetworkRegistries, cacheDir, logger);
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                Exception inner = tie.InnerException ?? tie;
                Console.Error.WriteLine("bundle complete: BundleManager failed: " + inner.Message);
                Console.Error.WriteLine(inner.StackTrace);
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("bundle complete: " + ex.Message);
                return 1;
            }

            if (!result.CacheLoaded)
            {
                // Headlessly the plugin's "generate a cache?" prompts are declined rather than
                // silently starting a ~20-minute build, and running the pass without the cache
                // would clear every bundle edit and recompute nothing.
                Console.Error.WriteLine("bundle complete: BundleManager cache not available. Open this game in "
                    + "Frosty Editor once with no project loaded and run Tools > Bundle Manager to build "
                    + "the bundlemanager cache and the root-instance cache, then re-run.");
                return 1;
            }

            // Every other write command leaves persistence to the launcher's --save / --save-as.
            // This one also saved the loaded project itself, so `--save-as other.fbproject` wrote
            // TWICE: the BundleManager pass landed on the project the user opened -- overwriting
            // work they had asked to keep -- before the copy was written. Defer whenever the
            // launcher is going to save, and say which file was touched either way.
            bool launcherSaves = Session.SaveRequested || Session.SaveAsPath != null;
            string savedTo = null;
            if (save && !launcherSaves && Session.Project.FilePath != null)
            {
                try { Session.Project.Save(Session.Project.FilePath, logger); savedTo = Session.Project.FilePath; }
                catch (Exception ex) { Console.Error.WriteLine("bundle complete: pass succeeded but saving failed: " + ex.Message); return 1; }
                logger.LogWarning("bundle complete: saved the loaded project in place ({0}). Pass --save-as "
                    + "<file.fbproject> to write the result elsewhere, or --no-save to discard it.", savedTo);
            }
            else if (save && launcherSaves)
            {
                savedTo = Session.SaveAsPath ?? Session.Project.FilePath;
            }

            formatter.WriteResult("OK", "bundle complete", new Dictionary<string, string>
            {
                { "scope", levelBundles == null ? "all" : string.Join(",", levels.ToArray()) },
                { "network-registries", blockNetworkRegistries ? "blocked" : "rebuilt" },
                { "whitelisted-bundles", result.AllowedBundles.ToString() },
                { "log", result.LogPath ?? "" },
                { "saved", savedTo ?? "false" },
            });
            return 0;
        }

        // The plugin lives in an installed Frosty editor, not beside the CLI. Look where the user
        // said, then beside the CLI.
        private static string FindEditorPluginsDir()
        {
            var candidates = new List<string>();
            string env = Environment.GetEnvironmentVariable("FROSTYCLI_FROSTY_PLUGINS");
            if (!string.IsNullOrEmpty(env)) candidates.Add(env);
            if (!string.IsNullOrEmpty(Session.EditorPluginsDir)) candidates.Add(Session.EditorPluginsDir);
            candidates.Add(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins"));
            foreach (string c in candidates)
                if (!string.IsNullOrEmpty(c) && System.IO.File.Exists(System.IO.Path.Combine(c, "BundleManager.dll")))
                    return c;
            return null;
        }

        // Removes a PROJECT-ADDED bundle membership (the inverse of `bundle add`). Base-game bundle
        // membership isn't removed here -- only memberships this project added.
        public static int ExecuteRemove(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string asset = null, bundle = null, kindArg = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from" && i + 1 < args.Length) bundle = args[++i];
                else if (args[i] == "--kind" && i + 1 < args.Length) kindArg = args[++i];
                else if (!args[i].StartsWith("--")) asset = args[i];
            }
            if (asset == null)  { Console.Error.WriteLine("bundle remove: missing <asset>"); return 1; }
            if (bundle == null) { Console.Error.WriteLine("bundle remove: missing --from <bundle>"); return 1; }
            if (!TryParseKind(kindArg, "bundle remove", out string kind)) return 1;
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("bundle remove: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("bundle remove: AssetManager not initialized."); return 1; }

            AssetEntry entry = ResolveAsset(asset, kind);
            if (entry == null) { Console.Error.WriteLine("bundle remove: " + (kind ?? "asset") + " not found: " + asset); return 1; }
            BundleEntry bentry = FindBundle(bundle);
            if (bentry == null) { Console.Error.WriteLine("bundle remove: bundle not found: " + bundle); return 1; }

            int bid = App.AssetManager.GetBundleId(bentry);
            if (DryRun.Active) { DryRun.Report(logger, "remove {0} from bundle {1}", asset, bentry.Name); return 0; }
            bool removed = entry.AddedBundles.Remove(bid);
            formatter.WriteResult("OK", "bundle remove", new Dictionary<string, string>
            { { "asset", asset }, { "bundle", bentry.Name }, { "removed", removed ? "true" : "false" } });
            return 0;
        }
    }
}
