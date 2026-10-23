using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyLevelTools
{
    // A separately loadable copy of a shipped Battlefront II level under a name that did not ship. Nothing shipped is
    // replaced: the copy has its own LevelData, sub-world tree, bundles, network registries, MVDBs, per-level physics
    // RES and registration, and the original keeps loading untouched with the copy installed.
    //
    // Host-independent: plain FrostySdk + Frosty.Core.App, shared verbatim by FrostyCLI (`level dupe`) and the
    // LevelDuplicatorPlugin for Frosty Editor 1.0.6.3. The two host-specific services are passed in as delegates.
    //
    // Every rule here was established by in-game bisection (I:\New Levels\FINDINGS.md, 2026-09-21):
    //  1. A level name resolves through LevelDescriptionAsset.LevelName, and a description is registered only if
    //     LevelListReport.BuiltLevels holds its FILE (partition) guid - its instance guid faults like an unknown name.
    //  2. LevelDescription.LevelGuid is the LevelData's instance guid. Clones keep every instance guid, so external
    //     references (file guid, instance guid) resolve once the file guid is remapped.
    //  3. A shipped bundle carries assets hoisted out of its children, so each new bundle mirrors its shipped twin's
    //     whole membership (an unresolved import faults at game+0x1522B97).
    //  4. Chunks shipped in a new bundle need H32 / FirstMip or D3D texture creation fails (E_INVALIDARG raised
    //     from game+0x161C7BD). They come from BundleManager's cache by BundleManager's own rule.
    //  5. Whole partitions are cloned: rebuilding one from its root drops exported objects that only other partitions
    //     reference (-> null Blueprint.Objects element, game+0xC62103).
    //  6. SchematicsAsset is not cloned: its InstanceType names a class compiled into the executable, and a renamed
    //     copy resolves to no class (game+0x167FE84).
    //  7. RVM shader databases and ANT state are found through bundle membership, not by the level's name.
    public sealed class LevelDuplicator
    {
        public const string DefaultTypes =
            "LevelData,SubWorldData,LayerData,WorldPartData,LevelDescriptionAsset,MeshVariationDatabase,NetworkRegistryAsset";

        public sealed class Options
        {
            public string Source;                 // shipped LevelData, e.g. Levels/MP/Tatooine_01/Tatooine_01
            public string Destination;            // new level, e.g. Levels/MP/MyMap_01/MyMap_01
            public string Types = DefaultTypes;   // EBX types cloned as level structure
            public bool Mirror = true;            // mirror shipped bundle membership into the new bundles
            public bool CopyLevelRes = true;      // give the copy its own per-level RES (placement physics ...)
            // chunk id -> (FirstMip, H32) for shipped chunks; null result = cache unavailable
            public Func<IDictionary<Guid, KeyValuePair<int, uint>>> ChunkMetaProvider;
            // optional: link a mirrored mesh/texture to its res and chunks (fills H32/FirstMip the way Frosty's
            // bundle editor does); the cache covers whatever this leaves at 0
            public Action<EbxAssetEntry> LinkSubResources;
            public Action<string> Log = s => { };
            public Action<string, double> Progress = (s, p) => { };
            // Optional filter: (asset name or bundle name without "win32/", ebx type or "bundle") -> clone it.
            // Lets a caller take part of a host level (used by LevelCreator).
            public Func<string, string, bool> Include;
            // Graft: clone part of another level into an existing new level's directory - no registration, no
            // menu entry, the destination root is not created (used by LevelCreator for a game mode sub-world).
            public bool Graft;
            // Menu entry (optional): when MenuName is set, the copy gets its own map entry in the frontend's map lists,
            // under a new planet when PlanetName is set (otherwise under the host's planet). Strings need a writable
            // localized-string database (Frosty Editor's FsLocalizationPlugin): SetString(hash, text), GetString(hash).
            public string MenuName;
            public string PlanetName;
            public string MenuDescription;
            public Action<uint, string> SetString;
            public Func<uint, string> GetString;
            // the game modes the level plays: the entry joins only those modes' lists and Custom Arcade pickers
            // (null = every list that holds the host's entry)
            public string[] MenuModes;
        }

        public sealed class Result
        {
            public string Level, Description, LevelGuid, DescriptionFileGuid;
            public int StructureAssets, StringsRetargeted, ReferencesRemapped, LevelResCopied, Bundles;
            public int MirroredEbx, MirroredRes, MirroredChunks, ChunkMetaSet, ChunkMetaMissing, ChunksWithH32Zero;
            public List<string> ClonedAssets = new List<string>();
            public List<string> NewBundles = new List<string>();
            public HashSet<int> NewBundleIds = new HashSet<int>();
            public Dictionary<string, string> CloneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Warnings = new List<string>();
            public string MenuEntry, MenuPlanet;
            public List<string> MenuListsUpdated = new List<string>();
        }

        private readonly Options o;
        private readonly AssetManager am = App.AssetManager;
        private string srcDir, srcLeaf, dstDir, dstLeaf;

        public LevelDuplicator(Options options) { o = options ?? throw new ArgumentNullException(nameof(options)); }

        // ---- naming ------------------------------------------------------------------------------------------------

        private void SplitNames()
        {
            if (string.IsNullOrWhiteSpace(o.Source) || string.IsNullOrWhiteSpace(o.Destination))
                throw new InvalidOperationException("source and destination level names are required");
            string src = o.Source.Trim().Trim('/'), dst = o.Destination.Trim().Trim('/');
            int ss = src.LastIndexOf('/'), ds = dst.LastIndexOf('/');
            if (ss < 0 || ds < 0) throw new InvalidOperationException("level names must be <dir>/<leaf>, e.g. Levels/MP/MyMap_01/MyMap_01");
            if (dst.IndexOfAny(new[] { ' ', '\\', ':' }) >= 0) throw new InvalidOperationException("the new level name must not contain spaces, backslashes or colons");
            srcDir = src.Substring(0, ss + 1); srcLeaf = src.Substring(ss + 1);
            dstDir = dst.Substring(0, ds + 1); dstLeaf = dst.Substring(ds + 1);
            o.Source = src; o.Destination = dst;
            if (string.Equals(srcDir, dstDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("the new level needs its own directory (" + dstDir + " is the host's)");
        }

        // A path under the host's directory -> the copy's directory, renaming the host leaf where it leads a segment
        // (Tatooine_01 -> MyMap_01, Tatooine_012 -> MyMap_012, Tatooine_01_Extrusions -> MyMap_01_Extrusions).
        public string MapPath(string p)
        {
            if (p == null || !p.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase)) return p;
            string rest = p.Substring(srcDir.Length);
            if (rest.StartsWith(srcLeaf, StringComparison.OrdinalIgnoreCase)) rest = dstLeaf + rest.Substring(srcLeaf.Length);
            return dstDir + rest;
        }

        private string MapString(string v)
        {
            int at = v.IndexOf(srcDir, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : v.Substring(0, at) + MapPath(v.Substring(at));
        }

        private string MapBundle(string b)
        {
            const string w = "win32/";
            return b.StartsWith(w, StringComparison.OrdinalIgnoreCase) ? w + MapPath(b.Substring(w.Length)) : b;
        }

        private HashSet<string> TypeSet() => new HashSet<string>(
            (o.Types ?? DefaultTypes).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);

        // ---- plan (read-only) --------------------------------------------------------------------------------------

        public List<KeyValuePair<string, string>> Plan()
        {
            SplitNames();
            EbxAssetEntry root = am.GetEbxEntry(o.Source);
            if (root == null || root.Type != "LevelData") throw new InvalidOperationException("not a LevelData: " + o.Source);
            var types = TypeSet();
            var plan = new List<KeyValuePair<string, string>>();
            foreach (EbxAssetEntry e in am.EnumerateEbx())
                if (e.Name.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase) && types.Contains(e.Type ?? "")
                    && (o.Include == null || o.Include(e.Name, e.Type)))
                    plan.Add(new KeyValuePair<string, string>(e.Name, MapPath(e.Name)));
            return plan;
        }

        // ---- run ---------------------------------------------------------------------------------------------------

        public Result Run()
        {
            var r = new Result();
            List<KeyValuePair<string, string>> plan = Plan();
            if (!o.Graft && am.GetEbxEntry(o.Destination) != null) throw new InvalidOperationException("already exists: " + o.Destination);
            foreach (var kv in plan)
                if (am.GetEbxEntry(kv.Value) != null) throw new InvalidOperationException("destination asset already exists: " + kv.Value);
            r.Level = o.Destination;

            // 1. clone whole partitions, retarget strings, register
            o.Progress("Cloning level structure", 5);
            var srcGuid = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var clones = new List<KeyValuePair<string, EbxAsset>>();
            // Load every source first, then clone (the validated order: interleaving loads and binary clones made
            // the reader fail on Tatooine's TeamDeathmatch_Setup).
            var sources = new Dictionary<string, EbxAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in plan) sources[kv.Key] = am.GetEbx(am.GetEbxEntry(kv.Key));
            foreach (var kv in plan)
            {
                EbxAssetEntry se = am.GetEbxEntry(kv.Key);
                srcGuid[kv.Key] = se.Guid;
                EbxAsset cloned;
                try { cloned = CloneAsset(sources[kv.Key]); }
                catch (Exception ex) { throw new InvalidOperationException("could not clone " + kv.Key + " (" + se.Type + "): " + ex.Message, ex); }
                clones.Add(new KeyValuePair<string, EbxAsset>(kv.Key, cloned));
            }
            var cloneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in clones)
            {
                string dest = MapPath(c.Key);
                var seen = new HashSet<object>(RefEq.Instance);
                foreach (object obj in c.Value.Objects) r.StringsRetargeted += MapStrings(obj, MapString, seen);
                c.Value.SetFileGuid(Guid.NewGuid());
                am.AddEbx(dest, c.Value, new int[0]);
                cloneOf[c.Key] = dest;
                r.ClonedAssets.Add(dest);
            }
            r.StructureAssets = cloneOf.Count;
            foreach (var kv in cloneOf) r.CloneOf[kv.Key] = kv.Value;

            // remap cross-partition references to the guids the project actually assigned
            o.Progress("Remapping references", 20);
            var fileRemap = new Dictionary<Guid, Guid>();
            foreach (var kv in cloneOf) fileRemap[srcGuid[kv.Key]] = am.GetEbxEntry(kv.Value).Guid;
            foreach (string d in cloneOf.Values)
            {
                EbxAsset a = am.GetEbx(am.GetEbxEntry(d));
                int before = CountExternalsInto(a, fileRemap);
                var seen = new HashSet<object>(RefEq.Instance);
                foreach (object obj in a.Objects) RemapRefs(obj, fileRemap, seen);
                int after = CountExternalsInto(a, fileRemap);
                if (before != after) { r.ReferencesRemapped += before - after; am.ModifyEbx(d, a); }
            }
            int stale = 0;
            foreach (string d in cloneOf.Values) stale += CountExternalsInto(am.GetEbx(am.GetEbxEntry(d)), fileRemap);
            if (stale > 0) throw new InvalidOperationException(stale + " reference(s) still point at host partitions that were cloned");
            o.Log(string.Format("cloned {0} structure asset(s), retargeted {1} string(s), remapped {2} reference(s)",
                r.StructureAssets, r.StringsRetargeted, r.ReferencesRemapped));

            // 1b. the copy's own per-level RES
            var resCopy = new Dictionary<ulong, ResAssetEntry>();
            var hostResCopied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (o.CopyLevelRes)
            {
                o.Progress("Copying level resources", 30);
                foreach (string d in cloneOf.Values)
                {
                    EbxAsset a = am.GetEbx(am.GetEbxEntry(d));
                    var rids = new HashSet<ulong>();
                    var seen = new HashSet<object>(RefEq.Instance);
                    foreach (object obj in a.Objects) CollectResourceRefs(obj, rids, seen);
                    foreach (ulong rid in rids)
                    {
                        if (rid == 0 || resCopy.ContainsKey(rid)) continue;
                        ResAssetEntry re = am.GetResEntry(rid);
                        if (re == null || !re.Name.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase)) continue;
                        string newName = MapPath(re.Name).ToLowerInvariant();
                        ResAssetEntry copy = am.GetResEntry(newName);
                        if (copy == null)
                        {
                            byte[] data;
                            using (Stream st = am.GetRes(re)) using (var ms = new MemoryStream()) { st.CopyTo(ms); data = ms.ToArray(); }
                            copy = am.AddRes(newName, (ResourceType)re.ResType, re.ResMeta, data, new int[0]);
                        }
                        resCopy[rid] = copy;
                        hostResCopied.Add(re.Name);
                    }
                }
                if (resCopy.Count > 0)
                    foreach (string d in cloneOf.Values)
                    {
                        EbxAsset a = am.GetEbx(am.GetEbxEntry(d));
                        var seen = new HashSet<object>(RefEq.Instance);
                        int n = 0;
                        foreach (object obj in a.Objects) n += RepointResourceRefs(obj, resCopy, seen);
                        if (n > 0) am.ModifyEbx(d, a);
                    }
                r.LevelResCopied = resCopy.Count;
                o.Log("copied " + resCopy.Count + " level RES under the new path");
            }

            // 2. register the level
            o.Progress("Registering the level", 40);
            if (!o.Graft) Register(r, cloneOf);
            else r.Level = o.Destination;


            // 3. one new bundle per shipped bundle of the host
            o.Progress("Creating bundles", 45);
            var bundleMap = new List<KeyValuePair<int, int>>();
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleEntry b in am.EnumerateBundles().ToList())
            {
                string inner = b.Name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? b.Name.Substring(6) : b.Name;
                // a mod-made host (level create output) has only added bundles under its directory: copy those too
                if (!inner.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (o.Include != null && !o.Include(inner, "bundle")) continue;
                string nn = MapBundle(b.Name);
                BundleEntry nb = am.EnumerateBundles().FirstOrDefault(x => string.Equals(x.Name, nn, StringComparison.OrdinalIgnoreCase))
                                 ?? am.AddBundle(nn, b.Type, b.SuperBundleId);
                EbxAssetEntry bp = am.GetEbxEntry(nn.Substring(6));
                if (bp != null) nb.Blueprint = bp;
                int did = am.GetBundleId(nb);
                bundleMap.Add(new KeyValuePair<int, int>(am.GetBundleId(b), did));
                byName[b.Name] = did;
                r.NewBundles.Add(nb.Name);
            }
            r.Bundles = bundleMap.Count;
            var newBundleIds = new HashSet<int>(bundleMap.Select(kv => kv.Value));
            Func<int, int> target = bid => { int t; return byName.TryGetValue(am.GetBundleEntry(bid).Name, out t) ? t : bid; };
            foreach (var kv in cloneOf)
            {
                EbxAssetEntry se = am.GetEbxEntry(kv.Key), de = am.GetEbxEntry(kv.Value);
                // AddedBundles too: a mod-made host's partitions live only in added bundles (else the copy is unbundled
                // and Frosty Mod Manager's WriteManifest throws index-out-of-range)
                foreach (int bid in se.Bundles.Concat(se.AddedBundles).ToList()) de.AddToBundle(target(bid));
            }
            foreach (var kv in resCopy)
            {
                ResAssetEntry hr = am.GetResEntry(kv.Key);
                foreach (int bid in hr.Bundles.Concat(hr.AddedBundles).ToList()) kv.Value.AddToBundle(target(bid));
            }
            o.Log("created " + r.Bundles + " bundle(s)");

            // 4. mirror shipped membership, then chunk H32 / FirstMip
            if (o.Mirror)
            {
                var newIds = new HashSet<int>(bundleMap.Select(kv => kv.Value));
                var srcIds = new Dictionary<int, int>();
                foreach (var kv in bundleMap) srcIds[kv.Key] = kv.Value;
                double step = 0;
                // Skip what was cloned, and the host's own network registries (they import host partitions that
                // are not loaded with the copy; the copy carries cloned ones).
                var linked = new HashSet<EbxAssetEntry>();
                var types = TypeSet();
                foreach (EbxAssetEntry e in am.EnumerateEbx())
                {
                    if (++step % 5000 == 0) o.Progress("Mirroring bundle membership", 50 + 30 * step / 160000.0);
                    if (cloneOf.ContainsKey(e.Name)) continue;
                    if (e.Type == "NetworkRegistryAsset" && e.Name.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase)) continue;
                    // a partial copy leaves the host's other structure out (and out of the new bundles)
                    if (o.Include != null && e.Name.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase) && types.Contains(e.Type ?? "")) continue;
                    bool hit = false;
                    foreach (int bid in e.Bundles.Concat(e.AddedBundles).ToList())
                    {
                        int did;
                        if (srcIds.TryGetValue(bid, out did) && e.AddToBundle(did)) { r.MirroredEbx++; hit = true; }
                    }
                    if (hit) linked.Add(e);
                }
                foreach (ResAssetEntry re in am.EnumerateRes())
                {
                    if (hostResCopied.Contains(re.Name)) continue;
                    foreach (int bid in re.Bundles.Concat(re.AddedBundles).ToList())
                    {
                        int did;
                        if (srcIds.TryGetValue(bid, out did) && re.AddToBundle(did)) r.MirroredRes++;
                    }
                }
                foreach (ChunkAssetEntry c in am.EnumerateChunks())
                    foreach (int bid in c.Bundles.Concat(c.AddedBundles).ToList())
                    {
                        int did;
                        if (srcIds.TryGetValue(bid, out did) && c.AddToBundle(did)) r.MirroredChunks++;
                    }
                o.Log(string.Format("mirrored {0} ebx, {1} res, {2} chunk membership(s)", r.MirroredEbx, r.MirroredRes, r.MirroredChunks));

                if (o.LinkSubResources != null)
                {
                    o.Progress("Linking meshes and textures", 82);
                    foreach (EbxAssetEntry e in linked)
                    {
                        string t = e.Type ?? "";
                        if (t.IndexOf("MeshAsset", StringComparison.OrdinalIgnoreCase) < 0 && t.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try { o.LinkSubResources(e); } catch (Exception ex) { r.Warnings.Add("link " + e.Name + ": " + ex.Message); }
                    }
                }

                o.Progress("Chunk metadata from the BundleManager cache", 88);
                IDictionary<Guid, KeyValuePair<int, uint>> meta = null;
                try { meta = o.ChunkMetaProvider != null ? o.ChunkMetaProvider() : null; }
                catch (Exception ex) { r.Warnings.Add("BundleManager cache: " + ex.Message); }
                foreach (ChunkAssetEntry c in am.EnumerateChunks())
                {
                    if (c.H32 != 0 || !c.AddedBundles.Any(newIds.Contains)) continue;
                    KeyValuePair<int, uint> m;
                    if (meta != null && meta.TryGetValue(c.Id, out m))
                    {
                        c.H32 = (int)m.Value;
                        if (c.FirstMip < 0 && m.Key >= 0) c.FirstMip = m.Key;
                        r.ChunkMetaSet++;
                    }
                    else r.ChunkMetaMissing++;
                }
                foreach (ChunkAssetEntry c in am.EnumerateChunks())
                    if (c.H32 == 0 && c.AddedBundles.Any(newIds.Contains)) r.ChunksWithH32Zero++;
                if (meta == null) r.Warnings.Add("BundleManager cache unavailable: chunk H32/FirstMip could not be filled");
                if (r.ChunksWithH32Zero > 0)
                    r.Warnings.Add(r.ChunksWithH32Zero + " chunk(s) still have H32 0 - the level may fail to load (D3D E_INVALIDARG)");
                o.Log(string.Format("chunk metadata: {0} set from cache, {1} not in cache, {2} left at H32 0",
                    r.ChunkMetaSet, r.ChunkMetaMissing, r.ChunksWithH32Zero));
            }
            r.NewBundleIds = newBundleIds;
            if (!o.Graft && !string.IsNullOrWhiteSpace(o.MenuName))
            {
                o.Progress("Adding the map to the menus", 95);
                AddMenuEntry(r);
            }
            o.Progress("Done", 100);
            return r;
        }

        private void Register(Result r, Dictionary<string, string> cloneOf)
        {
            string descPath = cloneOf.Values.FirstOrDefault(v => am.GetEbxEntry(v).Type == "LevelDescriptionAsset");
            if (descPath == null) throw new InvalidOperationException("the host has no LevelDescriptionAsset under " + srcDir);
            string srcDescPath = cloneOf.First(kv => kv.Value == descPath).Key;
            EbxAssetEntry descEntry = am.GetEbxEntry(descPath);
            EbxAsset descAsset = am.GetEbx(descEntry);
            dynamic desc = descAsset.RootObject;
            dynamic srcDesc = am.GetEbx(am.GetEbxEntry(srcDescPath)).RootObject;
            desc.LevelName = o.Destination;
            // SuperBundles names what the engine mounts; keep the host's (the string pass rewrote them)
            for (int i = 0; i < desc.SuperBundles.Count; i++) desc.SuperBundles[i] = srcDesc.SuperBundles[i];
            dynamic dstRoot = am.GetEbx(am.GetEbxEntry(o.Destination)).RootObject;
            Guid levelGuid = (Guid)dstRoot.GetInstanceGuid().ExportedGuid;
            desc.LevelGuid = levelGuid;
            am.ModifyEbx(descPath, descAsset);

            EbxAssetEntry llr = am.GetEbxEntry("LevelListReport");
            EbxAsset llrAsset = am.GetEbx(llr);
            dynamic llrRoot = llrAsset.RootObject;
            bool present = false;
            foreach (Guid g in llrRoot.BuiltLevels) if (g == descEntry.Guid) present = true;
            if (!present) { llrRoot.BuiltLevels.Add(descEntry.Guid); am.ModifyEbx("LevelListReport", llrAsset); }
            r.Description = descPath; r.DescriptionFileGuid = descEntry.Guid.ToString(); r.LevelGuid = levelGuid.ToString();
            o.Log("registered " + descPath + " (file guid " + descEntry.Guid + ") in LevelListReport");
        }

        // ---- frontend map entry -----------------------------------------------------------------------------------

        // The frontend lists maps through LevelInformationAsset (UI/Data/GameModes/Levels/*): Key, Planet
        // (PlanetInformationAsset), LevelName / LevelDescription strings and LevelId - the level name it launches.
        // Mode pickers (GameModeInformationAsset.Levels, UIOptionLevelEnumMetaData.ItemLevelReferences in
        // UI/Metadata/OptionsEnumUIMetaData) list those assets. The copy gets its own entry (and planet) and is appended
        // to every list that holds the host's entry.
        // Only the frontend map entry, for a level that already exists (LevelCreator): Source names the level whose
        // LevelInformationAsset is the template (and whose lists the entry joins), Destination the new level.
        public Result AddMenuEntryOnly()
        {
            SplitNames();
            var r = new Result { Level = o.Destination };
            if (!string.IsNullOrWhiteSpace(o.MenuName)) AddMenuEntry(r);
            return r;
        }

        private void AddMenuEntry(Result r)
        {
            EbxAssetEntry hostInfo = null;
            foreach (EbxAssetEntry e in am.EnumerateEbx("LevelInformationAsset"))
            {
                dynamic root = am.GetEbx(e).RootObject;
                string id = root.LevelId;
                if (string.Equals(id, o.Source, StringComparison.OrdinalIgnoreCase)) { hostInfo = e; break; }
            }
            if (hostInfo == null) { r.Warnings.Add("no LevelInformationAsset launches " + o.Source + "; no menu entry made"); return; }

            string tag = dstLeaf;
            Func<string, uint> newHash = key =>
            {
                uint h = (uint)Utils.HashString("LevelDuplicator/" + o.Destination + "/" + key, true);
                while (o.GetString != null && !string.IsNullOrEmpty(SafeGet(h))) h++;
                return h;
            };
            Action<uint, string> setString = (h, text) =>
            {
                if (o.SetString != null) o.SetString(h, text);
                else r.Warnings.Add("no string database: '" + text + "' will show as a missing string");
            };

            // planet
            dynamic hostInfoRoot = am.GetEbx(hostInfo).RootObject;
            FrostySdk.Ebx.PointerRef hostPlanetRef = hostInfoRoot.Planet;
            FrostySdk.Ebx.PointerRef planetRef = hostPlanetRef;
            if (!string.IsNullOrWhiteSpace(o.PlanetName) && hostPlanetRef.Type == PointerRefType.External)
            {
                EbxAssetEntry hostPlanet = am.GetEbxEntry(hostPlanetRef.External.FileGuid);
                string planetPath = "UI/Data/GameModes/Planets/" + tag;
                EbxAssetEntry existing = am.GetEbxEntry(planetPath);
                if (existing == null)
                {
                    EbxAsset planet;
                    try { planet = CloneAsset(am.GetEbx(hostPlanet)); }
                    catch (Exception ex) { throw new InvalidOperationException("could not copy " + hostPlanet.Name + ": " + ex.Message, ex); }
                    dynamic proot = planet.RootObject;
                    uint ph = newHash("planet");
                    setString(ph, o.PlanetName);
                    ((dynamic)Deref(proot.PlanetName)).StringHash = (int)ph;
                    proot.Name = planetPath;
                    planet.SetFileGuid(Guid.NewGuid());
                    existing = am.AddEbx(planetPath, planet, new int[0]);
                    foreach (int bid in hostPlanet.Bundles) existing.AddToBundle(bid);
                }
                dynamic pr = am.GetEbx(existing).RootObject;
                planetRef = new FrostySdk.Ebx.PointerRef(new EbxImportReference
                {
                    FileGuid = existing.Guid,
                    ClassGuid = (Guid)pr.GetInstanceGuid().ExportedGuid
                });
                r.MenuPlanet = planetPath;
            }

            // map entry
            string infoPath = "UI/Data/GameModes/Levels/" + tag;
            EbxAssetEntry info = am.GetEbxEntry(infoPath);
            if (info == null)
            {
                EbxAsset clone;
                try { clone = CloneAsset(am.GetEbx(hostInfo)); }
                catch (Exception ex) { throw new InvalidOperationException("could not copy " + hostInfo.Name + ": " + ex.Message, ex); }
                dynamic root = clone.RootObject;
                root.Key = tag;
                root.LevelId = o.Destination;
                root.Name = infoPath;
                root.Planet = planetRef;
                uint nh = newHash("name");
                setString(nh, o.MenuName);
                ((dynamic)Deref(root.LevelName)).StringHash = (int)nh;
                ((dynamic)Deref(root.LevelAurebesh)).String = o.MenuName.ToUpperInvariant();
                uint dh = newHash("description");
                setString(dh, string.IsNullOrWhiteSpace(o.MenuDescription) ? o.MenuName : o.MenuDescription);
                ((dynamic)Deref(root.LevelDescription)).StringHash = (int)dh;
                // per-mode description overrides would show the host's text; the copy uses its own for every mode
                root.LevelDescriptionOverrides.Clear();
                clone.SetFileGuid(Guid.NewGuid());
                info = am.AddEbx(infoPath, clone, new int[0]);
                foreach (int bid in hostInfo.Bundles) info.AddToBundle(bid);
            }
            dynamic infoRoot = am.GetEbx(info).RootObject;
            var newRef = new FrostySdk.Ebx.PointerRef(new EbxImportReference
            {
                FileGuid = info.Guid,
                ClassGuid = (Guid)infoRoot.GetInstanceGuid().ExportedGuid
            });
            r.MenuEntry = infoPath;

            // every list that holds the host's entry gets the copy's, right after it
            var modes = o.MenuModes == null ? null : new HashSet<string>(o.MenuModes, StringComparer.OrdinalIgnoreCase);
            var candidates = am.EnumerateEbx("GameModeInformationAsset")
                .Where(e => modes == null || modes.Contains(Convert.ToString((object)((dynamic)am.GetEbx(e).RootObject).GameModeId) ?? "")).ToList();
            EbxAssetEntry options = am.GetEbxEntry("UI/Metadata/OptionsEnumUIMetaData");
            if (options != null) candidates.Add(options);
            // The frontend pickers (OptionsEnumUIMetaData) keep the levels and their labels in two parallel lists:
            // UIOptionLevelEnumMetaData.ItemLevelReferences (BlastLevelReferencesToPlay) and
            // UIOptionEnumMetaData.ItemDescriptions (BlastLevelToPlay). A level inserted without its label shifted
            // every label after it by one (measured: the new entry showed the next map's name).
            string label = (string.IsNullOrWhiteSpace(o.PlanetName) ? o.MenuName : o.PlanetName + " - " + o.MenuName).ToUpperInvariant();
            uint labelHash = 0;
            foreach (EbxAssetEntry e in candidates)
            {
                EbxAsset a = am.GetEbx(e);
                Func<object, bool> picker = x => modes == null || x.GetType().Name != "UIOptionLevelEnumMetaData"
                                                 || modes.Contains(PickerMode((GetProp(x, "TelemetryId") ?? "").ToString()) ?? "");
                var pairs = e == options ? PickerLabelPairs(a, hostInfo.Guid, info.Guid, picker) : new List<KeyValuePair<object, int>>();
                int n = 0;
                var seen = new HashSet<object>(RefEq.Instance);
                foreach (object obj in a.Objects.Where(picker)) n += AppendAfter(obj, hostInfo.Guid, info.Guid, newRef, seen);
                if (pairs.Count > 0)
                {
                    if (labelHash == 0) { labelHash = newHash("label"); setString(labelHash, label); }
                    foreach (var kv in pairs) InsertPickerLabel(a, kv.Key, kv.Value + 1, labelHash);
                    o.Log("picker labels: '" + label + "' added to " + pairs.Count + " label list(s)");
                }
                if (n > 0) { am.ModifyEbx(e.Name, a); r.MenuListsUpdated.Add(e.Name + " (" + n + ")"); }
            }
            // The settings screens look the picked level up by Key for its planet (the planet shown beside the picker) and
            // the factions it offers: PlanetLevelAssetLookupEntityData.LevelLookupData = [{LevelKey, PlanetIdData,
            // FactionIdCollection}] (SkirmishSettingsScreen: 23 rows, one per shipped level; InstantActionSettingsScreen
            // has two tables). A key with no row gets neither, so the copy gets the host's row under its own key.
            string hostKey = Convert.ToString((object)hostInfoRoot.Key);
            foreach (string screen in LookupScreens)
            {
                EbxAssetEntry se = am.GetEbxEntry(screen);
                if (se == null) continue;
                bool ia = screen.IndexOf("InstantAction", StringComparison.OrdinalIgnoreCase) >= 0;
                if (modes != null && !modes.Contains(ia ? "Mode8" : "SkirmishBlast") && !(!ia && (modes.Contains("SkirmishOnslaught") || modes.Contains("SkirmishDuel")))) continue;
                EbxAsset sa = am.GetEbx(se);
                int rows = 0;
                foreach (object lk in sa.Objects.Where(x => x.GetType().Name == "PlanetLevelAssetLookupEntityData"))
                    rows += AddLookupRow(lk, hostKey, tag);
                if (rows > 0) { am.ModifyEbx(se.Name, sa); r.MenuListsUpdated.Add(se.Name + " (" + rows + " lookup row)"); }
            }
            o.Log("menu entry " + infoPath + (r.MenuPlanet != null ? " on planet " + r.MenuPlanet : "") +
                  ", added to " + r.MenuListsUpdated.Count + " list asset(s)");
        }

        public static readonly string[] LookupScreens =
        {
            "UI/Frontend/Screens/Play/Skirmish/SkirmishSettingsScreen",
            "UI/Frontend/Screens/Play/InstantAction/InstantActionSettingsScreen",
        };

        // copies the row for <hostKey> (planet, factions) under <newKey>; 0 when the host has no row or the key has one
        public static int AddLookupRow(object lookup, string hostKey, string newKey)
        {
            if (!(GetProp(lookup, "LevelLookupData") is IList rows)) return 0;
            object host = null;
            foreach (object row in rows)
            {
                string k = (GetProp(row, "LevelKey") ?? "").ToString();
                if (string.Equals(k, newKey, StringComparison.OrdinalIgnoreCase)) return 0;
                if (host == null && string.Equals(k, hostKey, StringComparison.OrdinalIgnoreCase)) host = row;
            }
            if (host == null) return 0;
            object copy = Activator.CreateInstance(host.GetType());
            foreach (PropertyInfo p in host.GetType().GetProperties().Where(p => p.CanRead && p.CanWrite))
                p.SetValue(copy, p.GetValue(host));
            PropertyInfo kp = copy.GetType().GetProperty("LevelKey");
            kp.SetValue(copy, kp.PropertyType == typeof(string) ? (object)newKey : Activator.CreateInstance(kp.PropertyType, newKey));
            rows.Add(copy);
            return 1;
        }

        // FsLocalizationPlugin answers an unused id with "Invalid StringId: <hex>" rather than an empty string
        private string SafeGet(uint h)
        {
            try
            {
                string v = o.GetString(h);
                return v != null && v.StartsWith("Invalid StringId", StringComparison.OrdinalIgnoreCase) ? null : v;
            }
            catch { return null; }
        }

        // (label list, index of the host's entry) for every picker that lists the host but not yet the copy; the label
        // list is the UIOptionEnumMetaData with the same item count whose TelemetryId shares the longest prefix
        // (BlastLevelReferencesToPlay / BlastLevelToPlay, OnSlaughtReferencesLevelToPlay / OnslaughtLevelToPlay ...).
        // the game mode a Custom Arcade level picker serves, from its TelemetryId (UI/Metadata/OptionsEnumUIMetaData:
        // BlastLevelReferencesToPlay, OnSlaughtReferencesLevelToPlay, DuelReferencesLevelToPlay, InstantActionLevelReference,
        // ModeELevelRef, ModeFLevelRef, StarfighterReferencesLevelTopPlay)
        public static string PickerMode(string telemetryId)
        {
            string t = telemetryId ?? "";
            if (t.StartsWith("Blast", StringComparison.OrdinalIgnoreCase)) return "SkirmishBlast";
            if (t.StartsWith("Onslaught", StringComparison.OrdinalIgnoreCase)) return "SkirmishOnslaught";
            if (t.StartsWith("Duel", StringComparison.OrdinalIgnoreCase)) return "SkirmishDuel";
            if (t.StartsWith("InstantAction", StringComparison.OrdinalIgnoreCase)) return "Mode8";
            if (t.StartsWith("ModeE", StringComparison.OrdinalIgnoreCase)) return "ModeE";
            if (t.StartsWith("ModeF", StringComparison.OrdinalIgnoreCase)) return "ModeF";
            if (t.StartsWith("Starfighter", StringComparison.OrdinalIgnoreCase)) return "SkirmishSpaceBlast";
            return null;
        }

        private static List<KeyValuePair<object, int>> PickerLabelPairs(EbxAsset a, Guid hostFile, Guid newFile, Func<object, bool> picker = null)
        {
            var result = new List<KeyValuePair<object, int>>();
            var objs = a.Objects.ToList();
            var labels = objs.Where(x => x.GetType().Name == "UIOptionEnumMetaData" && GetProp(x, "ItemDescriptions") is IList).ToList();
            var used = new HashSet<object>(RefEq.Instance);
            foreach (object item in objs.Where(x => x.GetType().Name == "UIOptionLevelEnumMetaData" && (picker == null || picker(x))))
            {
                if (!(GetProp(item, "ItemLevelReferences") is IList refs)) continue;
                int host = -1; bool has = false;
                for (int i = 0; i < refs.Count; i++)
                    if (refs[i] is FrostySdk.Ebx.PointerRef pr && pr.Type == PointerRefType.External)
                    {
                        if (pr.External.FileGuid == hostFile && host < 0) host = i;
                        if (pr.External.FileGuid == newFile) has = true;
                    }
                if (host < 0 || has) continue;
                string tid = (GetProp(item, "TelemetryId") ?? "").ToString();
                object best = labels.Where(l => !used.Contains(l) && ((IList)GetProp(l, "ItemDescriptions")).Count == refs.Count)
                                    .OrderByDescending(l => CommonPrefix(tid, (GetProp(l, "TelemetryId") ?? "").ToString()))
                                    .FirstOrDefault();
                if (best == null) continue;
                used.Add(best);
                result.Add(new KeyValuePair<object, int>(best, host));
            }
            return result;
        }

        private static int CommonPrefix(string x, string y)
        {
            int n = 0;
            while (n < x.Length && n < y.Length && char.ToLowerInvariant(x[n]) == char.ToLowerInvariant(y[n])) n++;
            return n;
        }

        private static object GetProp(object obj, string name) => obj?.GetType().GetProperty(name)?.GetValue(obj);

        // A new EnumItemDescription { ItemDescription -> new internal LocalizedStringId { StringHash } } at index.
        private static void InsertPickerLabel(EbxAsset a, object labelList, int index, uint hash)
        {
            IList descs = (IList)GetProp(labelList, "ItemDescriptions");
            object template = descs[Math.Min(Math.Max(index - 1, 0), descs.Count - 1)];
            object elem = Activator.CreateInstance(template.GetType());
            PropertyInfo idp = template.GetType().GetProperty("ItemDescription");
            object tmplId = Deref(idp.GetValue(template));
            object sid = Activator.CreateInstance(tmplId.GetType());
            PropertyInfo hp = sid.GetType().GetProperty("StringHash");
            hp.SetValue(sid, Convert.ChangeType(unchecked((int)hash), hp.PropertyType));
            if (Convert.ToInt64(hp.GetValue(sid)) == 0) throw new InvalidOperationException("picker label: StringHash did not take (hash " + hash + ", property " + hp.PropertyType.Name + ", read back " + hp.GetValue(sid) + ", type " + sid.GetType().FullName + ")");
            // like every shipped label: a non-exported object with its own internal id (an exported one with a fresh
            // guid and a colliding internal id made the game crash as soon as its window came up)
            int next = a.Objects.Select(x => (int)((dynamic)x).GetInstanceGuid().InternalId).DefaultIfEmpty(0).Max() + 1;
            ((dynamic)sid).SetInstanceGuid(new FrostySdk.Ebx.AssetClassGuid(next));
            a.AddObject(sid, false);
            FrostyCLI.Core.InProcessObjects.Mark(sid);
            idp.SetValue(elem, new FrostySdk.Ebx.PointerRef(sid));
            descs.Insert(Math.Min(index, descs.Count), elem);
        }

        // LevelName / PlanetName / LevelAurebesh are PointerRefs to internal LocalizedStringId / LocalizedString objects.
        private static object Deref(object v)
        {
            if (v is FrostySdk.Ebx.PointerRef pr)
            {
                if (pr.Type != PointerRefType.Internal || pr.Internal == null) throw new InvalidOperationException("expected an internal string object");
                return pr.Internal;
            }
            return v;
        }

        // In every list of PointerRefs that contains a reference to hostFile, insert newRef after it (once).
        private static int AppendAfter(object obj, Guid hostFile, Guid newFile, FrostySdk.Ebx.PointerRef newRef, HashSet<object> seen)
        {
            if (obj == null) return 0;
            Type t = obj.GetType();
            if (IsLeaf(t) || t.Name == "PointerRef" || t.Name == "CString" || t.Name == "ResourceRef") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            if (obj is IList list)
            {
                int at = -1; bool has = false;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] is FrostySdk.Ebx.PointerRef pr && pr.Type == PointerRefType.External)
                    {
                        if (pr.External.FileGuid == hostFile) at = i;
                        if (pr.External.FileGuid == newFile) has = true;
                    }
                if (at >= 0 && !has) { list.Insert(at + 1, newRef); return 1; }
                foreach (object el in list) n += AppendAfter(el, hostFile, newFile, newRef, seen);
                return n;
            }
            foreach (PropertyInfo p in Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                n += AppendAfter(v, hostFile, newFile, newRef, seen);
            }
            return n;
        }

        // ---- clone / remap helpers ---------------------------------------------------------------------------------

        // Full-partition clone through the binary writer/reader: every object, every instance guid.
        public static EbxAsset CloneAsset(EbxAsset asset)
        {
            // Exactly the call forms validated in game (default reader arguments).
            using (EbxBaseWriter writer = EbxBaseWriter.CreateProjectWriter(new MemoryStream(), EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(asset);
                byte[] buf = writer.ToByteArray();
                using (EbxReader reader = EbxReader.CreateProjectReader(new MemoryStream(buf)))
                    return reader.ReadAsset<EbxAsset>();
            }
        }

        public sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }

        internal static bool IsLeaf(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid);

        internal static IEnumerable<PropertyInfo> Props(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

        // Rewrites string / CString fields through map (null = unchanged). Does not follow PointerRefs; callers walk
        // every object of the partition.
        public static int MapStrings(object obj, Func<string, string> map, HashSet<object> seen)
        {
            if (obj == null) return 0;
            Type t = obj.GetType();
            if (IsLeaf(t) || t.Name == "PointerRef" || t.Name == "CString" || t.Name == "ResourceRef") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            if (obj is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object el = list[i];
                    if (el == null) continue;
                    object repl;
                    if (TryMapScalar(el, map, out repl)) { list[i] = repl; n++; continue; }
                    int k = MapStrings(el, map, seen);
                    if (k > 0 && el.GetType().IsValueType) list[i] = el;
                    n += k;
                }
                return n;
            }
            foreach (PropertyInfo p in Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v == null) continue;
                object repl;
                if (TryMapScalar(v, map, out repl)) { if (p.CanWrite) { p.SetValue(obj, repl); n++; } continue; }
                int k = MapStrings(v, map, seen);
                if (k > 0 && v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
                n += k;
            }
            return n;
        }

        private static bool TryMapScalar(object v, Func<string, string> map, out object repl)
        {
            repl = null;
            Type t = v.GetType();
            if (t != typeof(string) && t.Name != "CString") return false;
            string s = v.ToString();
            if (s == null) return false;
            string r = map(s);
            if (r == null || r == s) return false;
            if (t == typeof(string)) { repl = r; return true; }
            MethodInfo imp = t.GetMethod("op_Implicit", new[] { typeof(string) });
            if (imp != null) { repl = imp.Invoke(null, new object[] { r }); return true; }
            ConstructorInfo ctor = t.GetConstructor(new[] { typeof(string) });
            if (ctor != null) { repl = ctor.Invoke(new object[] { r }); return true; }
            return false;
        }

        // Remaps external PointerRefs by file guid. PointerRef.External has no setter, so a new PointerRef is built.
        public static void RemapRefs(object obj, Dictionary<Guid, Guid> remap, HashSet<object> seen)
        {
            if (obj == null) return;
            Type t = obj.GetType();
            if (IsLeaf(t) || t.Name == "CString" || t.Name == "ResourceRef") return;
            if (!t.IsValueType && !seen.Add(obj)) return;
            if (obj is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object el = list[i];
                    if (el == null) continue;
                    object repl;
                    if (TryRemapPointer(el, remap, out repl)) { if (repl != null) list[i] = repl; continue; }
                    RemapRefs(el, remap, seen);
                    if (el.GetType().IsValueType) list[i] = el;
                }
                return;
            }
            foreach (PropertyInfo p in Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v == null) continue;
                object repl;
                if (TryRemapPointer(v, remap, out repl)) { if (repl != null && p.CanWrite) p.SetValue(obj, repl); continue; }
                RemapRefs(v, remap, seen);
                if (v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
            }
        }

        // true = it is a PointerRef (handled); repl = replacement when it was remapped
        private static bool TryRemapPointer(object v, Dictionary<Guid, Guid> remap, out object repl)
        {
            repl = null;
            if (!(v is FrostySdk.Ebx.PointerRef)) return false;
            var pr = (FrostySdk.Ebx.PointerRef)v;
            if (pr.Type != PointerRefType.External) return true;
            EbxImportReference ext = pr.External;
            Guid mapped;
            if (!remap.TryGetValue(ext.FileGuid, out mapped)) return true;
            ext.FileGuid = mapped;
            repl = new FrostySdk.Ebx.PointerRef(ext);
            return true;
        }

        private static int CountExternalsInto(EbxAsset a, Dictionary<Guid, Guid> remap)
        {
            int n = 0;
            var seen = new HashSet<object>(RefEq.Instance);
            foreach (object obj in a.Objects) n += CountIn(obj, remap, seen);
            return n;
        }

        private static int CountIn(object obj, Dictionary<Guid, Guid> remap, HashSet<object> seen)
        {
            if (obj == null) return 0;
            if (obj is FrostySdk.Ebx.PointerRef pr)
                return pr.Type == PointerRefType.External && remap.ContainsKey(pr.External.FileGuid) ? 1 : 0;
            Type t = obj.GetType();
            if (IsLeaf(t) || t.Name == "CString" || t.Name == "ResourceRef") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            if (obj is IList list) { foreach (object el in list) n += CountIn(el, remap, seen); return n; }
            foreach (PropertyInfo p in Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                n += CountIn(v, remap, seen);
            }
            return n;
        }

        private static void CollectResourceRefs(object obj, HashSet<ulong> rids, HashSet<object> seen)
        {
            if (obj == null) return;
            if (obj is FrostySdk.Ebx.ResourceRef rr) { rids.Add((ulong)rr); return; }
            Type t = obj.GetType();
            if (IsLeaf(t) || t.Name == "PointerRef" || t.Name == "CString") return;
            if (!t.IsValueType && !seen.Add(obj)) return;
            if (obj is IList list) { foreach (object el in list) CollectResourceRefs(el, rids, seen); return; }
            foreach (PropertyInfo p in Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                CollectResourceRefs(v, rids, seen);
            }
        }

        // for ProjectDelta: RES moved into another project get new RIDs
        public static int RepointResources(object obj, Dictionary<ulong, ResAssetEntry> map, HashSet<object> seen) => RepointResourceRefs(obj, map, seen);

        private static int RepointResourceRefs(object obj, Dictionary<ulong, ResAssetEntry> map, HashSet<object> seen)
        {
            if (obj == null) return 0;
            Type t = obj.GetType();
            if (IsLeaf(t) || t.Name == "PointerRef" || t.Name == "CString" || t.Name == "ResourceRef") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            if (obj is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object el = list[i];
                    if (el == null) continue;
                    ResAssetEntry to;
                    if (el is FrostySdk.Ebx.ResourceRef rr && map.TryGetValue((ulong)rr, out to))
                    { list[i] = new FrostySdk.Ebx.ResourceRef(to.ResRid); n++; continue; }
                    int k = RepointResourceRefs(el, map, seen);
                    if (k > 0 && el.GetType().IsValueType) list[i] = el;
                    n += k;
                }
                return n;
            }
            foreach (PropertyInfo p in Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v == null) continue;
                ResAssetEntry to;
                if (v is FrostySdk.Ebx.ResourceRef rr && map.TryGetValue((ulong)rr, out to))
                { if (p.CanWrite) { p.SetValue(obj, new FrostySdk.Ebx.ResourceRef(to.ResRid)); n++; } continue; }
                int k = RepointResourceRefs(v, map, seen);
                if (k > 0 && v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
                n += k;
            }
            return n;
        }

        // Chunk H32 / FirstMip from the BundleManager cache for every chunk in the given bundles that still has H32 0.
        // Returns the number left at H32 0.
        public static int FillChunkMeta(IDictionary<Guid, KeyValuePair<int, uint>> meta, HashSet<int> bundleIds, out int set)
        {
            set = 0; int zero = 0;
            AssetManager am = App.AssetManager;
            foreach (ChunkAssetEntry c in am.EnumerateChunks())
            {
                if (c.H32 != 0 || !c.AddedBundles.Any(bundleIds.Contains)) continue;
                KeyValuePair<int, uint> m;
                if (meta != null && meta.TryGetValue(c.Id, out m))
                {
                    c.H32 = (int)m.Value;
                    if (c.FirstMip < 0 && m.Key >= 0) c.FirstMip = m.Key;
                    set++;
                }
                else zero++;
            }
            // Chunks the project ADDED (imported textures and meshes) are in no cache: BundleManager's own rule gives
            // H32 = HashString(owning ebx name, lower). A texture chunk shipped with H32 0 loads while its top mip is
            // resident, but the streamer's lower-mip request for a distant object faults (game+0x159E617, Cato
            // bisect P1/P4: the same pieces load near the camera and crash 1 km away; shipped textures load anywhere).
            if (zero > 0)
            {
                var owner = new Dictionary<Guid, EbxAssetEntry>();
                foreach (EbxAssetEntry e in am.EnumerateEbx("", true))
                {
                    foreach (AssetEntry r in e.LinkedAssets)
                    {
                        if (r is ChunkAssetEntry dc) { if (!owner.ContainsKey(dc.Id)) owner[dc.Id] = e; continue; }
                        foreach (AssetEntry ch in r.LinkedAssets)
                            if (ch is ChunkAssetEntry cc && !owner.ContainsKey(cc.Id)) owner[cc.Id] = e;
                    }
                }
                zero = 0;
                foreach (ChunkAssetEntry c in am.EnumerateChunks())
                {
                    if (c.H32 != 0 || !c.AddedBundles.Any(bundleIds.Contains)) continue;
                    EbxAssetEntry o;
                    if (!owner.TryGetValue(c.Id, out o)) { zero++; continue; }
                    c.H32 = FrostySdk.Utils.HashString(o.Name, true);
                    if (c.ModifiedEntry != null) c.ModifiedEntry.H32 = c.H32;
                    // a texture chunk's first streamed mip comes from its own texture header
                    foreach (AssetEntry r in o.LinkedAssets)
                    {
                        if (!(r is ResAssetEntry re) || re.ResType != (uint)ResourceType.Texture) continue;
                        try
                        {
                            var tex = am.GetResAs<FrostySdk.Resources.Texture>(re);
                            if (tex != null && tex.ChunkId == c.Id)
                            {
                                c.FirstMip = tex.FirstMip;
                                if (c.ModifiedEntry != null) c.ModifiedEntry.FirstMip = tex.FirstMip;
                            }
                        }
                        catch { }
                    }
                    set++;
                }
            }
            return zero;
        }

        // ---- BundleManager chunk cache, for a host where the BundleManager plugin is already loaded (the editor) ----

        // Reads BundleManager.BmCache.UnmodifiedAssetData and applies BundleManager's own rule:
        // H32 = HashString(h32Name ?? owningEbx.Name, lower). Loads the cache first if the plugin has not.
        public static IDictionary<Guid, KeyValuePair<int, uint>> ReadBundleManagerCache(Assembly bundleManager, object taskWindow)
        {
            Type bmCache = bundleManager.GetType("BundleManager.BmCache");
            if (bmCache == null) return null;
            var data = bmCache.GetField("UnmodifiedAssetData", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
            if (data == null || data.Count == 0)
            {
                MethodInfo load = bmCache.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "LoadCache" && m.GetParameters().Length == 1);
                if (load == null || !(bool)load.Invoke(null, new[] { taskWindow })) return null;
                data = bmCache.GetField("UnmodifiedAssetData", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
                if (data == null) return null;
            }
            var result = new Dictionary<Guid, KeyValuePair<int, uint>>();
            foreach (DictionaryEntry kv in data)
            {
                var ebx = (EbxAssetEntry)kv.Key;
                object ad = kv.Value;
                var chunks = ad.GetType().GetField("Chunks")?.GetValue(ad) as IEnumerable;
                if (chunks == null) continue;
                foreach (object tuple in chunks)
                {
                    Type tt = tuple.GetType();
                    var ce = tt.GetField("Item1").GetValue(tuple) as ChunkAssetEntry;
                    if (ce == null || result.ContainsKey(ce.Id)) continue;
                    int firstMip = (int)tt.GetField("Item2").GetValue(tuple);
                    string h32Name = tt.GetField("Item3").GetValue(tuple) as string;
                    result[ce.Id] = new KeyValuePair<int, uint>(firstMip, (uint)Utils.HashString(h32Name ?? ebx.Name, true));
                }
            }
            return result;
        }
    }
}
