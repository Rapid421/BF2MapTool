using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyLevelTools
{
    // A new Battlefront II level assembled from parts: the terrain and level shell of one shipped level, one game mode
    // sub-world of another, a chosen visual environment and generated spawns. The result is a level of its own under
    // its own name (e.g. AddedContent/TestModProject/Levels/NewLevel_01/NewLevel_01) - its own LevelData, sub-worlds,
    // bundles and registration; nothing shipped is replaced.
    //
    // Built on LevelDuplicator (a filtered copy of the terrain host, then a graft of the mode host's mode sub-world),
    // followed by pruning what the new level does not carry, a dependency closure over the loaded bundles, and the
    // chunk metadata pass. Rules and evidence: I:\New Levels\FINDINGS.md.
    public sealed class LevelCreator
    {
        public sealed class Options
        {
            public string Destination;                                   // AddedContent/<Mod>/Levels/<Level>/<Level>
            public string TerrainHost = "S9_3/COOP_NT_FOSD/COOP_NT_FOSD";  // level whose terrain + shell is used
            // host partitions (and sub-world bundles) the new level keeps, by name relative to the host directory
            public string[] TerrainKeep = { "Default", "Default_Schematic", "GameModes", "GameModes_Flow", "GameModes_Shared",
                                            "GameMode_Automation", "GameMode_Automation_Logic" };
            public string ModeHost = "Levels/MP/Jakku_01/Jakku_01";       // level whose game mode sub-world is grafted
            public string Mode = "HeroesVersusVillains";                  // game mode name (inclusion option)
            public string[] ModeKeep = { "HeroesVersusVillains", "HeroesVersusVillains_Logic", "HeroesVersusVillains_Gameplay",
                                         "HeroesVersusVillains_Automation", "HeroesVersusVillains_Shapes",
                                         "HvsVIntro", "HvsV_Intro", "HvsV_Intro_Schematic" };
            public string ModeSubWorld = "HeroesVersusVillains";          // the grafted sub-world (and its bundle)
            public string VisualEnvironment = "Levels/Lighting/Naboo/Cloudy/VE_Naboo_Cloudy_03";
            public double[] SpawnCenter = { -900, 70, 800 };
            public double SpawnRadius = 30;
            public bool KeepSpawns;                // leave the mode host's spawn transforms as they are
            public bool MoveLocators;              // also move the mode logic's LocatorEntityData to the spawn centre
            public double[] SpawnOffset;           // with KeepSpawns: translate the host's spawns by (dx, dy, dz)
            public double[] ModeOffset;            // place the whole mode sub-world: its reference's BlueprintTransform.trans
            public bool PermuteSpawns;
            public bool MirrorPathfinding;         // diagnostic: also mirror the mode host's pathfinding blobs
            // Arena mode (the default): keep the mode host's spawn layout together with its combat area and spawn-zone
            // shapes (HvV rejects spawns outside them - measured 2026-09-22), move the whole arena by ArenaOffset (x, z)
            // and stand every spawn at SpawnY (the Test Range ground is y ~ 64).
            public double? SpawnY = 67;
            public double[] ArenaOffset = { 0, 0 };
            public bool ArenaMode = true;
            public bool MirrorVeBundle = true;     // mirror the chosen VE's shipped (sub-level) bundle into the root bundle
            public bool MirrorVeLevelRoot = true;  // ...and the root bundle of the level that ships it
            // Frontend entry (optional): a LevelInformationAsset cloned from the mode host's, joined to every list that
            // holds the host's entry. Strings need a string database (SetString / GetString).
            public string MenuName, PlanetName, MenuDescription;
            public Action<uint, string> SetString;
            public Func<uint, string> GetString;
            // Further game modes the grafted mode sub-world also answers to (the frontend's Arcade launches
            // SkirmishBlast / SkirmishOnslaught / SkirmishDuel; HvV itself is online-only)
            public string[] AlsoModes = new string[0];
            // An event of the mode sub-world's interface fired when it has streamed in. Tatooine's TeamDeathmatch starts
            // its Skirmish child only on StartGameModeSkirmish, which Tatooine's own GameModes_Flow fires after
            // OnStreamedIn; the shell's flow knows nothing of it, so without this the level loads, the mode never
            // starts and no AI joins (measured 2026-09-24, SK1). Wired OnStreamedIn -> <event> on the reference.
            public string ModeStartEvent;
            // leave the terrain host's game-mode inclusion lists as they are (the host's own flow already handles the mode)
            public bool KeepModeLists;
            // kept host partitions (relative names) whose static model groups go: a Skirmish shell's GameModes carries
            // the geometry of the map's other modes
            public string[] ClearStaticGroups = new string[0];
            // kept host partitions (relative names) emptied of every placement, layer, world part, sub-world reference and
            // static model group: their bundle still loads (and with it the map's buildings, for --prefab)
            public string[] EmptyPartitions = new string[0];
            // kept partitions whose ShadowExtrusionDataEntityData goes: sun-direction extrusions baked with the host's
            // buildings standing, which keep casting their shadows after the buildings are gone (Tatooine's lighting variants)
            public string[] DropShadowExtrusions = new string[0];
            // Vehicle spawners placed in the shell's always-loaded Default layer: "<spawner prefab>@x,y,z[,yaw deg]"
            // (e.g. a VehicleSpawner prefab whose VehicleSpawnReferenceObjectData spawns a starfighter)
            public List<string> Vehicles = new List<string>();
            // layer (relative to the new level's directory) that gets the vehicle spawners; its sub-level's network
            // registry registers them. A spawner in the root's Default layer faulted (game+0xCC3F48) when its vehicle
            // arrived only with the mode's bundle - put it in the mode layer that carries the vehicle.
            public string VehicleLayer = "Default";
            // Shipped bundles whose membership is mirrored into the new root bundle (their own level's structure
            // excluded) - e.g. a space map's mode bundle, which carries the starfighters with what is hoisted around them
            public List<string> MirrorBundles = new List<string>();          // false: generated layout (LayOutSpawns) - rejected by HvV unless shapes match             // diagnostic: rotate the host's spawn transforms among spawns of the same team
            public Func<IDictionary<Guid, KeyValuePair<int, uint>>> ChunkMetaProvider;
            public Action<EbxAssetEntry> LinkSubResources;
            public Action<string> Log = s => { };
            public bool Audit;                     // instance-level import audit of every ebx in the new bundles
            // extra mode-host bundles (relative to the host directory, e.g. "Jakku_01", "GameModes") whose shipped
            // membership is mirrored into the grafted mode sub-world's bundle (host structure partitions excluded)
            // default (null): the mode host's root bundle and its GameModes bundle - the mode sub-world leans on
            // what its level hoisted there in ways a reference walk does not reach (without them the grafted Jakku
            // HvV faulted at game+0x1522B97 as it streamed in; with them it loads - 2026-09-22)
            public string[] ModeMirrorExtra;
            // auto-player bots per team (AutoPlayerManagerEntityData.FillGameplayBotsTeam1/2 in the shell's game mode
            // logic); online modes such as HvV otherwise wait in pre-round for "players needed". -1 leaves it alone.
            public int BotsPerTeam = 4;
            // per-team override (-2 = BotsPerTeam). HvV starts its round at exactly 4 v 4 with the local player:
            // 3 + 4 starts it (measured), 4 + 4 or 5 + 5 stays at "players needed: 1"
            public int BotsTeam1 = 3, BotsTeam2 = 4;
            public string AuditFind;               // optional guid to look for among all instance guids
            // A terrain of the level's own (`terrain create` output): the host's TerrainData is copied to
            // <level dir>/Terrain/Terrain with this streaming tree, and the new level's terrain entities point at the
            // copy. The payload must be built against the host's terrain (its layers and material declarations);
            // null keeps the host's terrain, shared by reference.
            public byte[] TerrainPayload;
        }

        public sealed class Result
        {
            public string Level, Description, ModeSubWorld, MenuEntry, MenuPlanet, Terrain;
            public List<string> MenuListsUpdated = new List<string>();
            public int Cloned, Bundles, Pruned, RefsDropped, Spawns, ClosureAdded, ClosureRes, ChunkMetaSet, ChunksWithH32Zero, AuditMissing, AuditRootLate, AuditStageLate;
            public List<string> Warnings = new List<string>();
        }

        private readonly Options o;
        private readonly AssetManager am = App.AssetManager;
        private string dstDir;
        private readonly Dictionary<string, string> clones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> newBundles = new HashSet<int>();

        public LevelCreator(Options options) { o = options ?? throw new ArgumentNullException(nameof(options)); }

        private static string Dir(string level) { string s = level.Trim().Trim('/'); return s.Substring(0, s.LastIndexOf('/') + 1); }
        private static string Leaf(string level) { string s = level.Trim().Trim('/'); return s.Substring(s.LastIndexOf('/') + 1); }

        // Include filter for LevelDuplicator: the kept partitions and sub-world bundles of a host, plus the host's
        // description (terrain host only) and the MVDBs / network registries shipped in a kept bundle.
        private Func<string, string, bool> Keep(string host, string[] keep, bool description)
        {
            string dir = Dir(host);
            var names = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            if (description) names.Add(Leaf(host));
            Func<string, bool> keptBundle = b =>
            {
                string inner = b.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? b.Substring(6) : b;
                return inner.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && names.Contains(inner.Substring(dir.Length));
            };
            return (name, type) =>
            {
                if (type == "bundle") return keptBundle(name);
                if (type == "LevelDescriptionAsset") return description;
                if (type == "MeshVariationDatabase" || type == "NetworkRegistryAsset")
                {
                    EbxAssetEntry e = am.GetEbxEntry(name);
                    // AddedBundles too: a mod-made host's MVDBs / registries are only in added bundles
                    return e != null && e.Bundles.Concat(e.AddedBundles).Any(b => keptBundle(am.GetBundleEntry(b).Name));
                }
                return names.Contains(name.Substring(dir.Length));
            };
        }

        public Result Run()
        {
            var r = new Result();
            if (string.IsNullOrWhiteSpace(o.Destination) || o.Destination.Trim('/').IndexOf('/') < 0)
                throw new InvalidOperationException("the new level name must be <dir>/<leaf>, e.g. AddedContent/MyMod/Levels/MyLevel_01/MyLevel_01");
            o.Destination = o.Destination.Trim().Trim('/');
            dstDir = Dir(o.Destination);
            r.Level = o.Destination;
            IDictionary<Guid, KeyValuePair<int, uint>> meta = null;
            bool metaLoaded = false;
            Func<IDictionary<Guid, KeyValuePair<int, uint>>> cachedMeta = () =>
            {
                if (!metaLoaded) { metaLoaded = true; try { meta = o.ChunkMetaProvider?.Invoke(); } catch (Exception ex) { r.Warnings.Add("BundleManager cache: " + ex.Message); } }
                return meta;
            };

            // 1. the level shell and terrain: a partial copy of the terrain host
            o.Log("terrain and level shell from " + o.TerrainHost);
            var shell = new LevelDuplicator(new LevelDuplicator.Options
            {
                Source = o.TerrainHost, Destination = o.Destination, Include = Keep(o.TerrainHost, o.TerrainKeep, true),
                ChunkMetaProvider = cachedMeta, LinkSubResources = o.LinkSubResources, Log = s => o.Log("  " + s),
            }).Run();
            Absorb(shell, r);
            r.Description = shell.Description;

            // 1b. the level's own terrain: a copy of the host's TerrainData carrying the new streaming tree
            if (o.TerrainPayload != null)
            {
                var mine = am.EnumerateEbx().Where(e => e.IsAdded && e.Name.StartsWith(dstDir, StringComparison.OrdinalIgnoreCase)).ToList();
                EbxAssetEntry hostTerrain = TerrainCloner.FindTerrain(mine)
                    ?? throw new InvalidOperationException("the terrain host's kept partitions hold no TerrainEntityData to give the new terrain to");
                string name = dstDir + "Terrain/Terrain";
                var copy = TerrainCloner.Clone(hostTerrain.Name, name, o.TerrainPayload);
                int n = TerrainCloner.Repoint(mine, hostTerrain, copy, s => o.Log("  " + s));
                if (n == 0) throw new InvalidOperationException("no terrain entity of the new level points at " + hostTerrain.Name);
                o.Log("terrain: " + hostTerrain.Name + " copied to " + name + " (" + string.Join(", ", copy.Resources) + ")");
                r.Terrain = name;
            }

            // 2. the game mode: graft the mode host's mode sub-world into the new level's directory
            o.Log("game mode " + o.Mode + " from " + o.ModeHost);
            var mode = new LevelDuplicator(new LevelDuplicator.Options
            {
                Source = o.ModeHost, Destination = o.Destination, Include = Keep(o.ModeHost, o.ModeKeep, false), Graft = true,
                ChunkMetaProvider = cachedMeta, LinkSubResources = o.LinkSubResources, Log = s => o.Log("  " + s),
            }).Run();
            Absorb(mode, r);
            string modePath = dstDir + o.ModeSubWorld;
            if (am.GetEbxEntry(modePath) == null) throw new InvalidOperationException("the graft produced no " + modePath);
            r.ModeSubWorld = modePath;

            if (o.ModeMirrorExtra == null) o.ModeMirrorExtra = new[] { Leaf(o.ModeHost), "GameModes" };
            if (o.ModeMirrorExtra.Length > 0) MirrorExtra(modePath, r);

            // 3. prune what the new level does not carry
            string terrainDir = Dir(o.TerrainHost), modeDir = Dir(o.ModeHost);
            Func<EbxAssetEntry, bool> foreign = e => e != null && !e.IsAdded &&
                (e.Name.StartsWith(terrainDir, StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith(modeDir, StringComparison.OrdinalIgnoreCase)) &&
                IsStructure(e.Type);
            var removed = new HashSet<Guid>();   // instance guids of pruned exported objects, by file
            var removedByFile = new Dictionary<Guid, HashSet<Guid>>();

            // root: layers of the host that were not kept, and the host's visual environments -> one new VE
            bool keepVe = string.IsNullOrEmpty(o.VisualEnvironment) || o.VisualEnvironment == "keep";
            EbxAssetEntry ve = keepVe ? null : am.GetEbxEntry(o.VisualEnvironment);
            if (!keepVe && ve == null) throw new InvalidOperationException("visual environment not found: " + o.VisualEnvironment);
            Edit(o.Destination, removedByFile, r, (asset, root, drop) =>
            {
                IList objs = (IList)Get(root, "Objects");
                object veRef = null;
                foreach (object obj in Resolve(asset, objs))
                {
                    string t = obj.GetType().Name;
                    if (t == "VisualEnvironmentReferenceObjectData" && !keepVe)
                    {
                        // the engine-wide base environments (Levels/Lighting/Global/VE_Gamewide_01, VE_HighEnd_01) stay:
                        // replacing them left the world black; only the host's own environment becomes the new one
                        EbxAssetEntry bpe = BlueprintEntry(obj);
                        if (bpe != null && bpe.Name.StartsWith("Levels/Lighting/Global/", StringComparison.OrdinalIgnoreCase)) continue;
                        if (veRef == null) { veRef = obj; continue; }
                        drop(obj); continue;
                    }
                    if (foreign(BlueprintEntry(obj))) drop(obj);
                }
                if (keepVe) return;
                if (veRef == null)
                {
                    // the host keeps its outdoor environment in a sub-world the new level leaves out (Jakku: a lighting
                    // sub-world): a reference of the root's own, copied from one the host uses anywhere
                    veRef = NewVeReference(asset, objs, terrainDir);
                    if (veRef == null) throw new InvalidOperationException("the terrain host has no visual environment reference to reuse");
                }
                dynamic veRoot = am.GetEbx(ve).RootObject;
                Set(veRef, "Blueprint", new FrostySdk.Ebx.PointerRef(new EbxImportReference
                { FileGuid = ve.Guid, ClassGuid = (Guid)veRoot.GetInstanceGuid().ExportedGuid }));
                o.Log("visual environment " + o.VisualEnvironment);
            });
            // the environment's own lighting bundle (Naboo: win32/Levels/MP/Naboo_01/Cloudy, a sub-level) carries what
            // the VE needs beyond its references (with only the reference closure the world rendered pure black);
            // mirror its membership into the new root bundle, the source level's structure excluded
            if (!keepVe && o.MirrorVeBundle) MirrorVeHome(ve, r);

            // GameModes: sub-world references to bundles that do not exist are dropped; the first reference that
            // carries inclusion settings becomes the new mode's (its criterion is the shared GameModeInclusionSettings)
            string gameModes = dstDir + "GameModes";
            Edit(gameModes, removedByFile, r, (asset, root, drop) =>
            {
                // a host that ships the mode itself (terrain and mode host the same level, e.g. Tatooine for Skirmish):
                // its GameModes already references the grafted sub-world (renamed into the new directory) with its own
                // inclusion settings and flow -- that reference stays as it is
                bool placed = Resolve(asset, (IList)Get(root, "Objects")).Any(x => x.GetType().Name == "SubWorldReferenceObjectData"
                    && string.Equals(Get(x, "BundleName")?.ToString(), modePath, StringComparison.OrdinalIgnoreCase));
                if (placed) o.Log("GameModes: the host's own reference to " + modePath + " kept (its flow starts the mode)");
                foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                {
                    if (obj.GetType().Name != "SubWorldReferenceObjectData") { if (foreign(BlueprintEntry(obj))) drop(obj); continue; }
                    string bundle = Get(obj, "BundleName")?.ToString() ?? "";
                    if (BundleExists(bundle)) continue;
                    // a removed auto-loaded sub-world's IsLoaded feeds gates (FOSD: AND(GameModeStreamIn, ..., FOSD.IsLoaded)
                    // -> OnTrue -> Mode9.StreamIn); with the sub-world gone the input would stay false and the mode
                    // sub-world would never stream in, so it becomes a constant true
                    if (Get(obj, "AutoLoad") is bool al && al) RecordLoadedFeeds(root, obj);
                    object inc = Get(obj, "InclusionSettings");
                    IList settings = inc == null ? null : Get(Deref(inc), "Settings") as IList;
                    if (!placed && settings != null && settings.Count > 0)
                    {
                        placed = true;
                        SetString(obj, "BundleName", modePath);
                        Set(obj, "AutoLoad", false);
                        if (o.ModeOffset != null)
                        {
                            PropertyInfo tp = obj.GetType().GetProperty("BlueprintTransform");
                            object lt = tp.GetValue(obj);
                            PropertyInfo vp = lt.GetType().GetProperty("trans");
                            object v = Activator.CreateInstance(vp.PropertyType);
                            vp.PropertyType.GetProperty("x").SetValue(v, (float)o.ModeOffset[0]);
                            vp.PropertyType.GetProperty("y").SetValue(v, (float)o.ModeOffset[1]);
                            vp.PropertyType.GetProperty("z").SetValue(v, (float)o.ModeOffset[2]);
                            vp.SetValue(lt, v);
                            tp.SetValue(obj, lt);
                            o.Log("GameModes: " + o.Mode + " sub-world placed at offset " + string.Join(",", o.ModeOffset));
                        }
                        foreach (object s in settings)
                        {
                            IList opts = (IList)Get(Deref(s), "EnabledOptions");
                            opts.Clear();
                            opts.Add(Str(opts, o.Mode));
                            foreach (string m in o.AlsoModes) opts.Add(Str(opts, m));
                        }
                        o.Log("GameModes: " + o.Mode + " -> " + modePath);
                        if (!string.IsNullOrEmpty(o.ModeStartEvent))
                        {
                            AddEventConnection(root, obj, "OnStreamedIn", obj, o.ModeStartEvent, 3);   // 3 = Server, as Tatooine
                            o.Log("GameModes: " + Leaf(modePath) + ".OnStreamedIn -> " + o.ModeStartEvent);
                        }
                        continue;
                    }
                    drop(obj);
                }
                if (!placed) throw new InvalidOperationException("the terrain host's GameModes has no mode sub-world reference to reuse");
            });

            ApplyLoadedFeeds(r);

            // every other new level / sub-world partition: references to sub-worlds the new level does not carry (a host's
            // city Content, Lobby, cinematic outros ...) go -- a SubWorldReferenceObjectData names its sub-world by
            // BundleName alone, so the blueprint test above never sees them (`level validate`: subworld-missing)
            foreach (string part in clones.Values.Where(v => !string.Equals(v, gameModes, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                EbxAssetEntry pe = am.GetEbxEntry(part);
                if (pe == null || (pe.Type != "LevelData" && pe.Type != "SubWorldData")) continue;
                EbxAsset pa = am.GetEbx(pe);
                if (!Resolve(pa, (IList)Get(pa.RootObject, "Objects")).Any(x => x.GetType().Name == "SubWorldReferenceObjectData"
                        && !BundleExists(Get(x, "BundleName")?.ToString() ?? ""))) continue;
                Edit(part, removedByFile, r, (asset, root, drop) =>
                {
                    foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                    {
                        if (obj.GetType().Name != "SubWorldReferenceObjectData") continue;
                        string bn = Get(obj, "BundleName")?.ToString() ?? "";
                        if (BundleExists(bn)) continue;
                        if (Get(obj, "AutoLoad") is bool al && al) RecordLoadedFeeds(root, obj);
                        o.Log(part + ": drop SubWorldReferenceObjectData (no bundle " + bn + ")");
                        drop(obj);
                    }
                });
            }
            ApplyLoadedFeeds(r);

            // every other new partition, too: layer / world part references to host partitions the level does not carry.
            // A kept sub-world that references a left-out layer (Tatooine's Lighting_Sunny -> Sunny_VFX) would otherwise
            // have the dependency closure add the host's layer to its bundle, and the layer loads after all (TK19/TK20:
            // the old city's lamps and baked shadow caches came back that way).
            foreach (string part in clones.Values.Where(v => !string.Equals(v, o.Destination, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                EbxAssetEntry pe = am.GetEbxEntry(part);
                if (pe == null || !IsStructure(pe.Type)) continue;
                EbxAsset pa = am.GetEbx(pe);
                if (!Resolve(pa, (IList)Get(pa.RootObject, "Objects")).Any(x => (x.GetType().Name == "LayerReferenceObjectData"
                        || x.GetType().Name == "WorldPartReferenceObjectData") && foreign(BlueprintEntry(x)))) continue;
                Edit(part, removedByFile, r, (asset, root, drop) =>
                {
                    foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                    {
                        string t = obj.GetType().Name;
                        if (t != "LayerReferenceObjectData" && t != "WorldPartReferenceObjectData") continue;
                        EbxAssetEntry bpe = BlueprintEntry(obj);
                        if (!foreign(bpe)) continue;
                        o.Log(part + ": drop " + t + " (host partition " + bpe.Name + " not kept)");
                        drop(obj);
                    }
                });
            }

            foreach (string part in o.EmptyPartitions)
            {
                string path = dstDir + part;
                if (am.GetEbxEntry(path) == null) { r.Warnings.Add("empty: no " + path); continue; }
                Edit(path, removedByFile, r, (asset, root, drop) =>
                {
                    int n = 0;
                    foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                    {
                        string t = obj.GetType().Name;
                        if (t.EndsWith("ReferenceObjectData") || t == "StaticModelGroupEntityData" || t == "ShadowExtrusionDataEntityData") { drop(obj); n++; }
                    }
                    o.Log(path + ": emptied (" + n + " object(s)); its bundle still loads");
                });
            }
            foreach (string part in o.ClearStaticGroups)
            {
                string path = part == "<root>" ? o.Destination : dstDir + part;      // <root>: the new level's own root
                if (am.GetEbxEntry(path) == null) { r.Warnings.Add("clear static groups: no " + path); continue; }
                Edit(path, removedByFile, r, (asset, root, drop) =>
                {
                    int n = 0;
                    foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                        if (obj.GetType().Name == "StaticModelGroupEntityData") { drop(obj); n++; }
                    if (n > 0) o.Log(path + ": " + n + " static model group(s) removed");
                });
            }
            foreach (string part in o.DropShadowExtrusions)
            {
                string path = part == "<root>" ? o.Destination : dstDir + part;
                if (am.GetEbxEntry(path) == null) { r.Warnings.Add("drop shadow extrusions: no " + path); continue; }
                Edit(path, removedByFile, r, (asset, root, drop) =>
                {
                    int n = 0;
                    foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                        if (obj.GetType().Name == "ShadowExtrusionDataEntityData") { drop(obj); n++; }
                    if (n > 0) o.Log(path + ": " + n + " shadow extrusion(s) removed (baked with the host's buildings)");
                });
            }

            // mode sub-world (and the sub-worlds nested in it, e.g. Tatooine TeamDeathmatch -> TeamDeathmatch_Skirmish):
            // layers and sub-worlds of the mode host that were not kept, and the host's shadow extrusions
            var modeWorlds = new List<string> { modePath };
            modeWorlds.AddRange(ModeClones().Where(v => !string.Equals(v, modePath, StringComparison.OrdinalIgnoreCase)
                                                         && am.GetEbxEntry(v)?.Type == "SubWorldData"));
            foreach (string sw in modeWorlds.Distinct(StringComparer.OrdinalIgnoreCase))
                Edit(sw, removedByFile, r, (asset, root, drop) =>
                {
                    foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                    {
                        EbxAssetEntry bpe = BlueprintEntry(obj);
                        string why = obj.GetType().Name == "ShadowExtrusionDataEntityData" ? "shadow extrusion"
                                   : foreign(bpe) ? "host partition " + bpe.Name : null;
                        // a nested sub-world that was not kept (Tatooine TeamDeathmatch_Online beside _Skirmish): its
                        // reference was renamed into the new directory, where neither the sub-world nor its bundle exists
                        if (why == null && obj.GetType().Name == "SubWorldReferenceObjectData")
                        {
                            string bn = Get(obj, "BundleName")?.ToString() ?? "";
                            // a sub-world reference names its sub-world by BundleName alone (no Blueprint)
                            if (!BundleExists(bn)) why = "no bundle " + bn;
                        }
                        if (why != null) { o.Log(sw + ": drop " + obj.GetType().Name + " (" + why + ")"); drop(obj); }
                    }
                });

            // mode logic: the host's collision blockers go; spawns and locators are laid out on the new terrain
            foreach (string layer in ModeClones().ToList())
            {
                EbxAssetEntry le = am.GetEbxEntry(layer);
                if (le == null || le.Type != "LayerData") continue;
                Edit(layer, removedByFile, r, (asset, root, drop) =>
                {
                    var spawns = new List<object>();
                    foreach (object obj in Resolve(asset, (IList)Get(root, "Objects")).ToList())
                    {
                        string t = obj.GetType().Name;
                        if (t == "OBBCollisionEntityData") drop(obj);
                    }
                    // spawns are exported partition objects outside the layer's object list (the mode sub-world links them)
                    foreach (object obj in asset.Objects)
                    {
                        string t = obj.GetType().Name;
                        if (t == "AlternateSpawnEntityData") spawns.Add(obj);
                        else if (t == "LocatorEntityData" && o.MoveLocators) Place(obj, o.SpawnCenter[0], o.SpawnCenter[1] + 2, o.SpawnCenter[2], 0);
                    }
                    if (spawns.Count > 0 && !o.KeepSpawns && o.ArenaMode)
                    {
                        foreach (object sp in spawns) MoveArena(sp, o.ArenaOffset, o.SpawnY);
                        r.Spawns += spawns.Count;
                        o.Log(layer + ": " + spawns.Count + " spawn(s) moved with the arena (offset " + o.ArenaOffset[0] + "," + o.ArenaOffset[1] + (o.SpawnY.HasValue ? ", y " + o.SpawnY : "") + ")");
                        // on a terrain of the level's own, every spawn then stands on it: one median height put the co-op
                        // missions' spawns (spread far past the arena) metres under the new hills (MP3, 2026-09-25)
                        int snapped = 0; double lo = double.MaxValue, hi = double.MinValue;
                        foreach (object sp in spawns)
                        {
                            double? g = Ground(sp);
                            if (!g.HasValue) continue;
                            SetY(sp, g.Value + 0.5);
                            snapped++; lo = Math.Min(lo, g.Value); hi = Math.Max(hi, g.Value);
                        }
                        if (snapped > 0) o.Log(layer + ": " + snapped + " spawn(s) stood on the new terrain (ground " + lo.ToString("0.0") + ".." + hi.ToString("0.0") + " m)");
                    }
                    if (o.ArenaMode && !o.KeepSpawns)
                    {
                        int shapes = 0;
                        foreach (object obj in asset.Objects)
                        {
                            string t = obj.GetType().Name;
                            if (t == "VolumeVectorShapeData" || t == "SpawnLocationFinderShapeData") { ShiftPoints(obj, o.ArenaOffset); shapes++; }
                            else if (t == "LocatorEntityData") MoveArena(obj, o.ArenaOffset, null);
                        }
                        if (shapes > 0) o.Log(layer + ": " + shapes + " combat-area / spawn-zone shape(s) moved with the arena");
                    }
                    if (spawns.Count > 0 && o.KeepSpawns && o.PermuteSpawns)
                        foreach (var grp in spawns.GroupBy(sp => Get(sp, "Team")?.ToString()))
                        {
                            var list = grp.ToList();
                            var ts = list.Select(sp => obj2t(sp)).ToList();
                            for (int i = 0; i < list.Count; i++) { PropertyInfo tp = list[i].GetType().GetProperty("Transform"); tp.SetValue(list[i], ts[(i + 1) % list.Count]); }
                        }
                    if (spawns.Count > 0 && o.KeepSpawns && o.SpawnOffset != null)
                        foreach (object sp in spawns) Offset(sp, o.SpawnOffset);
                    if (spawns.Count > 0 && !o.KeepSpawns && !o.ArenaMode) { LayOutSpawns(spawns); r.Spawns += spawns.Count; o.Log(layer + ": " + spawns.Count + " spawn(s) laid out"); }
                });
            }

            // references to pruned exported objects from every other new partition (network registries, schematics)
            foreach (string d in clones.Values)
            {
                EbxAssetEntry e = am.GetEbxEntry(d);
                if (e == null) continue;
                EbxAsset a = am.GetEbx(e);
                int n = DropRefs(a, e.Guid, removedByFile);
                if (n > 0) { am.ModifyEbx(d, a); r.RefsDropped += n; }
            }

            // 4. description: the new level offers the new mode only
            if (r.Description != null)
            {
                EbxAsset da = am.GetEbx(am.GetEbxEntry(r.Description));
                int lists = 0;
                var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
                foreach (object obj in da.Objects) lists += ReplaceModeLists(obj, seen);
                if (lists == 0) r.Warnings.Add("no game mode list found in " + r.Description);
                am.ModifyEbx(r.Description, da);
                o.Log("description: " + lists + " mode list(s) set to " + o.Mode);
            }

            // 4b. the shell's own game mode logic tests the current mode (FOSD GameModes_Flow:
            // InclusionSettingEntityData{Setting=GameMode, Settings=[Mode9, ModeDefend, ModeE, ModeF]} gates the mode
            // sub-world's StreamIn); every such list in the kept host partitions names the new mode instead -- unless the
            // host's lists already name it (KeepModeLists: a Tatooine shell's flow knows SkirmishBlast)
            foreach (var kv in clones.Where(c => !o.KeepModeLists && c.Key.StartsWith(terrainDir, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                EbxAssetEntry ce = am.GetEbxEntry(kv.Value);
                if (ce == null || ce.Type == "LevelDescriptionAsset" || ce.Type == "NetworkRegistryAsset" || ce.Type == "MeshVariationDatabase") continue;
                EbxAsset ca = am.GetEbx(ce);
                var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
                int n = 0;
                foreach (object obj in ca.Objects) n += ReplaceModeLists(obj, seen);
                if (n > 0) { am.ModifyEbx(kv.Value, ca); o.Log(kv.Value + ": " + n + " game mode list(s) set to " + o.Mode); }
            }

            // 4c. bots: the shell's auto-player manager fills both teams
            int managers = 0;
            if (o.BotsPerTeam >= 0)
                foreach (var kv in clones.Where(c => c.Key.StartsWith(terrainDir, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    EbxAssetEntry ce = am.GetEbxEntry(kv.Value);
                    if (ce == null || ce.Type != "LayerData") continue;
                    EbxAsset ca = am.GetEbx(ce);
                    int n = 0;
                    foreach (object obj in ca.Objects)
                    {
                        if (obj.GetType().Name != "AutoPlayerManagerEntityData") continue;
                        foreach (string f in new[] { "FillGameplayBotsTeam1", "FillGameplayBotsTeam2" })
                        {
                            PropertyInfo pi = obj.GetType().GetProperty(f);
                            int v = f.EndsWith("1") ? (o.BotsTeam1 != -2 ? o.BotsTeam1 : o.BotsPerTeam) : (o.BotsTeam2 != -2 ? o.BotsTeam2 : o.BotsPerTeam);
                            if (pi != null) { pi.SetValue(obj, Convert.ChangeType(v, pi.PropertyType)); n++; }
                        }
                    }
                    if (n > 0) { managers++; am.ModifyEbx(kv.Value, ca); o.Log(kv.Value + ": auto-player bots " + (o.BotsTeam1 != -2 ? o.BotsTeam1 : o.BotsPerTeam) + " + " + (o.BotsTeam2 != -2 ? o.BotsTeam2 : o.BotsPerTeam)); }
                }
            // A shell with no auto-player manager (Tatooine: none in any partition) gets one per online PvP mode layer when bots
            // are asked for -- those modes (Blast, HvV ...) otherwise wait in pre-round for players -- and only there: a
            // manager in an always-loaded layer would add auto-players to Arcade on top of its own AI. As the Test Range shell
            // ships one (COOP_NT_FOSD GameModes_Flow 78d5c95b): a bare AutoPlayerManagerEntityData, PlayerCount -1,
            // OrphanTimeSeconds 180, Flags = guid low 24 bits | 0x04 << 24.
            int bt1 = o.BotsTeam1 != -2 ? o.BotsTeam1 : o.BotsPerTeam, bt2 = o.BotsTeam2 != -2 ? o.BotsTeam2 : o.BotsPerTeam;
            if (managers == 0 && (bt1 > 0 || bt2 > 0))
                foreach (string layer in clones.Values.Where(n => OnlineModeLayer.IsMatch(n.Substring(n.LastIndexOf('/') + 1))).Distinct().ToList())
                {
                    // the hero modes start only at their own team size, the local player included: HvV 4 v 4 (3 + 4 bots,
                    // measured V5 2026-09-22; 4 + 4 stays at "players needed"), Hero Showdown 2 v 2
                    string leaf = layer.Substring(layer.LastIndexOf('/') + 1);
                    int[] n = leaf.Equals("HeroArena_Logic", StringComparison.OrdinalIgnoreCase) ? new[] { 3, 4 }
                            : leaf.Equals("Mode6_Logic", StringComparison.OrdinalIgnoreCase) ? new[] { 1, 2 } : new[] { bt1, bt2 };
                    AddAutoPlayerManager(layer, n[0], n[1], r);
                }

            // 4c2. extra shipped bundles mirrored into the root bundle
            foreach (string mb in o.MirrorBundles) MirrorIntoRoot(mb, r);

            // 4d. vehicle spawners on the ground (before the closure, which brings their vehicles' content along)
            if (o.Vehicles.Count > 0) AddVehicleSpawners(r);

            // 5. dependency closure: every import of every new partition must be in a bundle loaded with the level
            if (r.Description != null) MergeDescriptionBundles(r.Description, modeDir, r);
            HashSet<int> loaded = LoadedBundlesFor(r.Description);
            o.Log("loaded with the level: " + loaded.Count + " bundle(s) (new, global shared, description)");
            DependencyClosure(r, loaded);

            if (o.Audit) Audit(loaded, r);

            if (!string.IsNullOrWhiteSpace(o.MenuName))
            {
                var menu = new LevelDuplicator(new LevelDuplicator.Options
                {
                    Source = o.ModeHost, Destination = o.Destination, MenuName = o.MenuName, PlanetName = o.PlanetName,
                    MenuDescription = o.MenuDescription, SetString = o.SetString, GetString = o.GetString, Log = s2 => o.Log("  " + s2),
                    MenuModes = new[] { o.Mode }.Concat(o.AlsoModes).ToArray(),
                }).AddMenuEntryOnly();
                r.MenuEntry = menu.MenuEntry; r.MenuPlanet = menu.MenuPlanet; r.MenuListsUpdated = menu.MenuListsUpdated;
                r.Warnings.AddRange(menu.Warnings);
            }

            // 6. chunk metadata for everything now shipped in the new bundles
            int set;
            r.ChunksWithH32Zero = LevelDuplicator.FillChunkMeta(cachedMeta(), newBundles, out set);
            r.ChunkMetaSet = set;
            if (r.ChunksWithH32Zero > 0) r.Warnings.Add(r.ChunksWithH32Zero + " chunk(s) still have H32 0 - the level may fail to load (D3D E_INVALIDARG)");
            o.Log("chunk metadata: " + set + " set, " + r.ChunksWithH32Zero + " left at H32 0");
            return r;
        }


        // Every import of every partition in the new bundles must be in a bundle loaded with the level; missing ones
        // (and their companion RES / linked chunks) are added to the bundle of the partition that imports them.
        private void DependencyClosure(Result r, HashSet<int> loaded)
        {
            var queue = new Queue<KeyValuePair<EbxAssetEntry, int>>();
            var visited = new HashSet<Guid>();
            // every ebx in the new bundles - the clones and the mirrored host content: a mode host's sub-world bundle
            // leans on assets hoisted into its own level's root bundle (Jakku_01's footprint channels, out-of-combat
            // death screen ...), which the new level does not load
            foreach (EbxAssetEntry e in am.EnumerateEbx().Where(x => x.AddedBundles.Any(newBundles.Contains)).ToList())
            {
                int home = e.AddedBundles.First(newBundles.Contains);
                queue.Enqueue(new KeyValuePair<EbxAssetEntry, int>(e, home));
                visited.Add(e.Guid);
            }
            // an asset's compiled companions ship beside it as RES named after it (ShaderGraph -> <name>_graph/blocks,
            // mesh -> <name>_mesh/blocks, <name>_physics_win32 ...); without them the Naboo VE's flare shaders faulted
            // at game+0x104A06F half a second after the level came up
            var resByName = am.EnumerateRes().Select(x => x.Name.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Func<EbxAssetEntry, int, int> addCompanions = (dep, bundle) =>
            {
                string pre = dep.Name.ToLowerInvariant();
                int i = resByName.BinarySearch(pre, StringComparer.Ordinal);
                if (i < 0) i = ~i;
                int n = 0;
                for (; i < resByName.Count && resByName[i].StartsWith(pre, StringComparison.Ordinal); i++)
                {
                    string name = resByName[i];
                    if (name.Length > pre.Length && "_/.".IndexOf(name[pre.Length]) < 0) continue;
                    ResAssetEntry re = am.GetResEntry(name);
                    if (re == null || re.Bundles.Concat(re.AddedBundles).Any(loaded.Contains)) continue;
                    if (re.AddToBundle(bundle)) { n++; o.Log("  closure:   + res " + name); }
                }
                return n;
            };
            foreach (var kv in queue.ToList())
                if (kv.Key.Type == "SchematicsAsset") EnqueueSchematicsClass(kv.Key, kv.Value, loaded, visited, queue, r);
            var blueprintBundleRoots = new HashSet<Guid>(am.EnumerateBundles()
                .Where(bb => bb.Type == BundleType.BlueprintBundle && bb.Blueprint != null && !bb.Added)
                .Select(bb => bb.Blueprint.Guid));
            var onDemand = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.Count > 0)
            {
                var kv = queue.Dequeue();
                EbxAsset a;
                try { a = am.GetEbx(kv.Key); } catch (Exception ex) { r.Warnings.Add("closure: " + kv.Key.Name + ": " + ex.Message); continue; }
                foreach (Guid fg in Imports(a))
                {
                    if (!visited.Add(fg)) continue;
                    EbxAssetEntry dep = am.GetEbxEntry(fg);
                    if (dep == null) { r.Warnings.Add("closure: " + kv.Key.Name + " imports a missing partition " + fg); continue; }
                    if (dep.Bundles.Concat(dep.AddedBundles).Any(loaded.Contains)) continue;
                    // the root of a blueprint bundle (vehicles, kits: Vehicle_Air_TieFighter ->
                    // BlueprintBundle_SP_Kit_Vehicle_TieFighter) is streamed by the game when something spawns it;
                    // copying it and its content into a level bundle crashed the load (game+0x15AC131 / 0x1522B97)
                    if (blueprintBundleRoots.Contains(dep.Guid)) { onDemand.Add(dep.Name); continue; }
                    dep.AddToBundle(kv.Value);
                    r.ClosureAdded++;
                    o.Log("  closure: + " + dep.Name + " (" + dep.Type + ") -> " + am.GetBundleEntry(kv.Value).Name);
                    r.ClosureRes += addCompanions(dep, kv.Value);
                    if (o.LinkSubResources != null) { try { o.LinkSubResources(dep); } catch (Exception ex) { r.Warnings.Add("link " + dep.Name + ": " + ex.Message); } }
                    // linking alone does not give the texture / mesh data membership once the ebx is already in the
                    // bundle: add every linked RES and chunk explicitly (texture data missing from the bundle raised
                    // D3D E_INVALIDARG 0x80070057 during load)
                    r.ClosureRes += AddLinked(dep, kv.Value, loaded, new HashSet<AssetEntry>());
                    queue.Enqueue(new KeyValuePair<EbxAssetEntry, int>(dep, kv.Value));
                    // a SchematicsAsset names its class by string (InstanceType = "<prefab>_class"), which no reference
                    // walk sees; without the ClassInfoAsset the game faults at game+0x167FE84
                    if (dep.Type == "SchematicsAsset") EnqueueSchematicsClass(dep, kv.Value, loaded, visited, queue, r);
                }
            }
            foreach (string n in onDemand) o.Log("dependency closure: " + n + " streams on demand (blueprint bundle root)");
            o.Log("dependency closure: " + r.ClosureAdded + " partition(s) and " + r.ClosureRes + " companion RES added to the new bundles");
        }

        // `level closure <level>`: the dependency closure of an EXISTING new level, for content added after
        // `level create` (ported meshes, prefabs, blueprint templates): a template's leftover import that no bundle the
        // level loads carries faults the load at game+0x1522B97 (Utapau, 2026-10-03: a no-physics blueprint template's
        // lava texture). The level's own bundles are the added bundles under its directory.
        public Result Close(string level)
        {
            var r = new Result();
            level = (level ?? "").Trim().Trim('/');
            if (level.IndexOf('/') < 0) throw new InvalidOperationException("level path must be <dir>/<leaf>");
            r.Level = level;
            string desc = level + "/description";
            r.Description = am.GetEbxEntry(desc) != null ? desc : null;
            if (r.Description == null) r.Warnings.Add("no " + desc + ": only the global shared bundles count as loaded besides the level's own");
            string prefix = "win32/" + Dir(level);
            foreach (BundleEntry b in am.EnumerateBundles())
                if (b.Added && b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) newBundles.Add(am.GetBundleId(b));
            if (newBundles.Count == 0) throw new InvalidOperationException("no added bundle under " + prefix);
            o.Log("closure of " + level + ": " + newBundles.Count + " level bundle(s) under " + prefix);
            HashSet<int> loaded = LoadedBundlesFor(r.Description);
            o.Log("loaded with the level: " + loaded.Count + " bundle(s) (level, global shared, description)");
            DependencyClosure(r, loaded);
            if (o.Audit) Audit(loaded, r);
            IDictionary<Guid, KeyValuePair<int, uint>> meta = null;
            try { meta = o.ChunkMetaProvider?.Invoke(); } catch (Exception ex) { r.Warnings.Add("BundleManager cache: " + ex.Message); }
            int set;
            r.ChunksWithH32Zero = LevelDuplicator.FillChunkMeta(meta, newBundles, out set);
            r.ChunkMetaSet = set;
            if (r.ChunksWithH32Zero > 0) r.Warnings.Add(r.ChunksWithH32Zero + " chunk(s) still have H32 0 - the level may fail to load (D3D E_INVALIDARG)");
            o.Log("chunk metadata: " + set + " set, " + r.ChunksWithH32Zero + " left at H32 0");
            return r;
        }

        private int AddLinked(AssetEntry a, int bundle, HashSet<int> loaded, HashSet<AssetEntry> seen)
        {
            int n = 0;
            foreach (AssetEntry l in a.LinkedAssets.ToList())
            {
                if (!seen.Add(l)) continue;
                if (!l.Bundles.Concat(l.AddedBundles).Any(loaded.Contains) && l.AddToBundle(bundle))
                {
                    n++;
                    o.Log("  closure:   + linked " + (l is ChunkAssetEntry c ? "chunk " + c.Id : l.Name));
                }
                n += AddLinked(l, bundle, loaded, seen);
            }
            return n;
        }

        private static readonly int IsLoadedHash = FrostySdk.Utils.HashString("IsLoaded");
        private readonly List<KeyValuePair<object, int>> loadedFeeds = new List<KeyValuePair<object, int>>();   // (target ref, target field hash)

        private void RecordLoadedFeeds(object root, object subWorldRef)
        {
            if (!(Get(root, "PropertyConnections") is IList pcs)) return;
            foreach (object pc in pcs)
            {
                if (!(Get(pc, "Source") is FrostySdk.Ebx.PointerRef src) || src.Type != PointerRefType.Internal || !ReferenceEquals(src.Internal, subWorldRef)) continue;
                if (Convert.ToInt32(Get(pc, "SourceFieldId")) != IsLoadedHash) continue;
                loadedFeeds.Add(new KeyValuePair<object, int>(Get(pc, "Target"), Convert.ToInt32(Get(pc, "TargetFieldId"))));
            }
        }

        private void ApplyLoadedFeeds(Result r)
        {
            foreach (var kv in loadedFeeds)
            {
                var target = (FrostySdk.Ebx.PointerRef)kv.Key;
                EbxAssetEntry te = null; EbxAsset ta = null; object obj = null;
                if (target.Type == PointerRefType.External)
                {
                    te = am.GetEbxEntry(target.External.FileGuid);
                    if (te == null) { r.Warnings.Add("IsLoaded feed: target partition " + target.External.FileGuid + " not found"); continue; }
                    ta = am.GetEbx(te);
                    obj = ta.Objects.FirstOrDefault(x => (Guid)((dynamic)x).GetInstanceGuid().ExportedGuid == target.External.ClassGuid);
                }
                else if (target.Type == PointerRefType.Internal) { obj = target.Internal; }
                if (obj == null) { r.Warnings.Add("IsLoaded feed: target object not found"); continue; }
                // logic gates with dynamic ports (AndEntityData: InputCount, ports In1..InN): drop the port when it is the last one
                PropertyInfo count = obj.GetType().GetProperty("InputCount");
                if (count != null && obj.GetType().Name == "AndEntityData")
                {
                    int n = Convert.ToInt32(count.GetValue(obj));
                    int port = Enumerable.Range(1, 16).FirstOrDefault(i => FrostySdk.Utils.HashString("In" + i) == kv.Value);
                    if (port == 0 || port != n) { r.Warnings.Add("IsLoaded feed: AndEntityData port hash 0x" + kv.Value.ToString("X8") + " is not its last input (" + n + "); gate left as is"); continue; }
                    count.SetValue(obj, Convert.ChangeType(n - 1, count.PropertyType));
                    if (te != null) am.ModifyEbx(te.Name, ta);
                    o.Log("  " + (te != null ? te.Name : "GameModes") + ": AndEntityData.InputCount " + n + " -> " + (n - 1) + " (In" + port + " was fed by a removed sub-world's IsLoaded)");
                    continue;
                }
                PropertyInfo field = obj.GetType().GetProperties().FirstOrDefault(p => p.PropertyType == typeof(bool) && p.CanWrite && FrostySdk.Utils.HashString(p.Name) == kv.Value);
                if (field == null) { r.Warnings.Add("IsLoaded feed: " + obj.GetType().Name + " has no bool field with hash 0x" + kv.Value.ToString("X8")); continue; }
                field.SetValue(obj, true);
                if (te != null) am.ModifyEbx(te.Name, ta);
                o.Log("  " + (te != null ? te.Name : "GameModes") + ": " + obj.GetType().Name + "." + field.Name + " = true (was fed by a removed sub-world's IsLoaded)");
            }
            loadedFeeds.Clear();
        }

        // Every import of every ebx in the new bundles must name a partition in a loaded bundle and an exported object
        // that exists in it (an import that misses resolves to nothing: game+0x1522B97 read 0x20).
        private void Audit(HashSet<int> loaded, Result r)
        {
            var members = am.EnumerateEbx().Where(e => e.AddedBundles.Any(newBundles.Contains)).ToList();
            o.Log("audit: " + members.Count + " ebx in the new bundles");
            var exported = new Dictionary<Guid, HashSet<Guid>>();
            Func<EbxAssetEntry, HashSet<Guid>> exportsOf = e =>
            {
                HashSet<Guid> set;
                if (exported.TryGetValue(e.Guid, out set)) return set;
                set = new HashSet<Guid>();
                try
                {
                    foreach (object obj in am.GetEbx(e).Objects)
                    {
                        dynamic ig = ((dynamic)obj).GetInstanceGuid();
                        if ((bool)ig.IsExported) set.Add((Guid)ig.ExportedGuid);
                    }
                }
                catch (Exception ex) { r.Warnings.Add("audit: cannot read " + e.Name + ": " + ex.Message); }
                exported[e.Guid] = set;
                return set;
            };
            Guid find; bool finding = Guid.TryParse(o.AuditFind ?? "", out find);
            var viaShared = new Dictionary<string, int>();
            var descBundles = new HashSet<int>(DescriptionBundles(r.Description));
            // the level's root bundle loads BEFORE its sub-world bundles (Kyber log: "Loading bundle <root> into
            // compartment 3", then the sub-levels): a root member's import must resolve in the shared / description
            // bundles (the root among them), not in a level sub-bundle that streams in later (Mygeeto M37: Kamino
            // Content members mirrored into the root crashed the root load at game+0x1522B97)
            var rootStage = new HashSet<int>(GlobalSharedBundles()); rootStage.UnionWith(descBundles);
            var rootLate = new List<string>();
            foreach (EbxAssetEntry e in members)
            {
                EbxAsset a;
                try { a = am.GetEbx(e); } catch (Exception ex) { r.Warnings.Add("audit: cannot read " + e.Name + ": " + ex.Message); continue; }
                if (finding)
                    foreach (object obj in a.Objects)
                        if ((Guid)((dynamic)obj).GetInstanceGuid().ExportedGuid == find) o.Log("audit: " + find + " is an instance in " + e.Name + " (" + obj.GetType().Name + ")");
                var refs = new List<EbxImportReference>();
                var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
                foreach (object obj in a.Objects) CollectImportRefs(obj, refs, seen);
                foreach (EbxImportReference ir in refs.Distinct())
                {
                    if (ir.FileGuid == e.Guid) continue;
                    if (finding && (ir.ClassGuid == find || ir.FileGuid == find)) o.Log("audit: " + find + " is imported by " + e.Name + " (file " + ir.FileGuid + ")");
                    EbxAssetEntry t = am.GetEbxEntry(ir.FileGuid);
                    string why = null;
                    if (t == null) why = "no such partition";
                    else if (!t.Bundles.Concat(t.AddedBundles).Any(loaded.Contains)) why = t.Name + " is in no loaded bundle";
                    else if (!exportsOf(t).Contains(ir.ClassGuid)) why = t.Name + " has no exported instance " + ir.ClassGuid;
                    if (why == null && e.AddedBundles.Concat(e.Bundles).Any(descBundles.Contains)
                        && !t.Bundles.Concat(t.AddedBundles).Any(rootStage.Contains))
                        rootLate.Add(e.Name + " -> " + t.Name + " (only in " + string.Join(", ", t.Bundles.Concat(t.AddedBundles).Where(loaded.Contains).Select(b => am.GetBundleEntry(b).Name)) + ")");
                    if (why == null)
                    {
                        // satisfied only through a shared bundle the level does not name: count per bundle
                        if (!t.Bundles.Concat(t.AddedBundles).Any(b => newBundles.Contains(b) || descBundles.Contains(b)))
                        {
                            string sb = am.GetBundleEntry(t.Bundles.Concat(t.AddedBundles).First(loaded.Contains)).Name;
                            int c; viaShared.TryGetValue(sb, out c); viaShared[sb] = c + 1;
                        }
                        continue;
                    }
                    r.AuditMissing++;
                    o.Log("audit: MISSING " + e.Name + " -> " + ir.FileGuid + "/" + ir.ClassGuid + ": " + why);
                }
            }
            o.Log("audit: " + r.AuditMissing + " unresolved import(s)");
            foreach (string l in rootLate.Distinct()) o.Log("audit: ROOT-LATE " + l);
            o.Log("audit: " + rootLate.Distinct().Count() + " root-bundle import(s) resolved only by a later-loading level bundle");
            r.AuditRootLate = rootLate.Distinct().Count();
            foreach (var kv in viaShared.OrderByDescending(x => x.Value)) o.Log("audit: " + kv.Value + " import(s) resolved only through shared bundle " + kv.Key);
            foreach (int b in descBundles) o.Log("audit: description bundle " + am.GetBundleEntry(b).Name);
            AuditStages(members, rootStage, r);
        }

        // The same rule one level down: a streamed sub-world bundle loads after its parent chain (root -> GameModes ->
        // Dreadnaught -> Dreadnought_Hangar ...), so its members' imports must resolve in the root stage, its ancestors or
        // itself - not in a sibling / descendant that streams later (Mygeeto SHIP6: Kyber crashed at game+0x15264E4,
        // a null read in the MVDB material walk, as Dreadnought_Hangar loaded). Parents come from the
        // SubWorldReferenceObjectData.BundleName fields inside each level bundle's partitions.
        private void AuditStages(List<EbxAssetEntry> members, HashSet<int> rootStage, Result r)
        {
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (int b in newBundles) byName[am.GetBundleEntry(b).Name] = b;
            var parent = new Dictionary<int, int>();
            foreach (EbxAssetEntry e in members)
            {
                EbxAsset a;
                try { a = am.GetEbx(e); } catch { continue; }
                foreach (object obj in a.Objects)
                {
                    if (obj.GetType().Name != "SubWorldReferenceObjectData") continue;
                    string bn = (string)((dynamic)obj).BundleName.ToString();
                    int child;
                    if (string.IsNullOrEmpty(bn) || !byName.TryGetValue("win32/" + bn, out child)) continue;
                    int home = e.AddedBundles.Concat(e.Bundles).Where(newBundles.Contains).DefaultIfEmpty(-1).First();
                    if (home >= 0 && home != child && !parent.ContainsKey(child)) parent[child] = home;
                }
            }
            Func<int, HashSet<int>> stage = b =>
            {
                var s = new HashSet<int>(rootStage);
                for (int x = b, guard = 0; guard < 64; guard++) { s.Add(x); int p; if (!parent.TryGetValue(x, out p)) break; x = p; }
                return s;
            };
            var stages = new Dictionary<int, HashSet<int>>();
            var late = new List<string>();
            foreach (EbxAssetEntry e in members)
            {
                var homes = e.AddedBundles.Concat(e.Bundles).Where(b => newBundles.Contains(b) && !rootStage.Contains(b)).Distinct().ToList();
                if (homes.Count == 0) continue;
                EbxAsset a;
                try { a = am.GetEbx(e); } catch { continue; }
                var refs = new List<EbxImportReference>();
                var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
                foreach (object obj in a.Objects) CollectImportRefs(obj, refs, seen);
                foreach (EbxImportReference ir in refs.Distinct())
                {
                    if (ir.FileGuid == e.Guid) continue;
                    EbxAssetEntry t = am.GetEbxEntry(ir.FileGuid);
                    if (t == null) continue;
                    var tb = t.Bundles.Concat(t.AddedBundles).ToList();
                    // only a level bundle can be late; anything else is the main audit's business
                    if (!tb.Any(newBundles.Contains)) continue;
                    foreach (int h in homes)
                    {
                        HashSet<int> s;
                        if (!stages.TryGetValue(h, out s)) stages[h] = s = stage(h);
                        if (tb.Any(s.Contains)) continue;
                        late.Add(am.GetBundleEntry(h).Name + ": " + e.Name + " -> " + t.Name + " (only in "
                                 + string.Join(", ", tb.Where(newBundles.Contains).Select(b => am.GetBundleEntry(b).Name)) + ")");
                    }
                }
            }
            foreach (var kv in parent) o.Log("audit: stage " + am.GetBundleEntry(kv.Key).Name + " <- " + am.GetBundleEntry(kv.Value).Name);
            foreach (string l in late.Distinct()) o.Log("audit: STAGE-LATE " + l);
            o.Log("audit: " + late.Distinct().Count() + " sub-world import(s) resolved only by a bundle outside the importer's load chain");
            r.AuditStageLate = late.Distinct().Count();
        }

        private static void CollectImportRefs(object obj, List<EbxImportReference> refs, HashSet<object> seen)
        {
            if (obj == null) return;
            if (obj is FrostySdk.Ebx.PointerRef pr) { if (pr.Type == PointerRefType.External) refs.Add(pr.External); return; }
            Type t = obj.GetType();
            if (LevelDuplicator.IsLeaf(t) || t.Name == "CString" || t.Name == "ResourceRef") return;
            if (!t.IsValueType && !seen.Add(obj)) return;
            if (obj is IList list) { foreach (object el in list) CollectImportRefs(el, refs, seen); return; }
            foreach (PropertyInfo p in LevelDuplicator.Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                CollectImportRefs(v, refs, seen);
            }
        }

        private void MirrorVeHome(EbxAssetEntry ve, Result r)
        {
            BundleEntry root = am.EnumerateBundles().FirstOrDefault(b => string.Equals(b.Name, "win32/" + o.Destination, StringComparison.OrdinalIgnoreCase));
            if (root == null) { r.Warnings.Add("VE mirror: no root bundle"); return; }
            int tid = am.GetBundleId(root);
            var sids = new List<int>();
            foreach (int sid in ve.Bundles)
            {
                BundleEntry sb = am.GetBundleEntry(sid);
                if (sb.Type != BundleType.SubLevel) continue;
                sids.Add(sid);
                // ...and the root bundle of the level that ships it (the sub-level leans on what its level hoisted -
                // mirroring only Naboo_01/Cloudy faulted at game+0x1522B97 during load)
                string inner0 = sb.Name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? sb.Name.Substring(6) : sb.Name;
                string dir0 = Dir(inner0);
                BundleEntry levelRoot = am.EnumerateBundles().FirstOrDefault(b => !b.Added && b.Type == BundleType.SubLevel &&
                    string.Equals(b.Name, "win32/" + dir0 + Leaf(dir0.TrimEnd('/')), StringComparison.OrdinalIgnoreCase));
                if (o.MirrorVeLevelRoot && levelRoot != null && !sids.Contains(am.GetBundleId(levelRoot))) sids.Add(am.GetBundleId(levelRoot));
            }
            foreach (int sid in sids)
            {
                BundleEntry src = am.GetBundleEntry(sid);
                string inner = src.Name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? src.Name.Substring(6) : src.Name;
                string srcDir = Dir(inner);
                int ne = 0, nr = 0, nc = 0;
                foreach (EbxAssetEntry e in am.EnumerateEbx().ToList())
                {
                    if (!e.Bundles.Contains(sid)) continue;
                    if (e.Name.StartsWith(srcDir, StringComparison.OrdinalIgnoreCase) && (IsStructure(e.Type) || e.Type == "LevelDescriptionAsset" || e.Type == "NetworkRegistryAsset" || e.Type == "MeshVariationDatabase")) continue;
                    if (e.Name.IndexOf("pathfindingblobs", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (e.AddToBundle(tid)) ne++;
                }
                foreach (ResAssetEntry re in am.EnumerateRes()) if (re.Bundles.Contains(sid) && re.AddToBundle(tid)) nr++;
                foreach (ChunkAssetEntry c in am.EnumerateChunks()) if (c.Bundles.Contains(sid) && c.AddToBundle(tid)) nc++;
                o.Log("VE bundle: " + src.Name + " -> " + root.Name + ": " + ne + " ebx, " + nr + " res, " + nc + " chunks");
            }
        }

        private void MirrorExtra(string modePath, Result r)
        {
            string modeDir = Dir(o.ModeHost);
            BundleEntry target = am.EnumerateBundles().FirstOrDefault(b => string.Equals(b.Name, "win32/" + modePath, StringComparison.OrdinalIgnoreCase));
            if (target == null) { r.Warnings.Add("mirror-extra: no bundle win32/" + modePath); return; }
            int tid = am.GetBundleId(target);
            foreach (string rel in o.ModeMirrorExtra)
            {
                BundleEntry src = am.EnumerateBundles().FirstOrDefault(b => string.Equals(b.Name, "win32/" + modeDir + rel, StringComparison.OrdinalIgnoreCase));
                if (src == null) { r.Warnings.Add("mirror-extra: no bundle win32/" + modeDir + rel); continue; }
                int sid = am.GetBundleId(src);
                int ne = 0, nr = 0, nc = 0, skippedNav = 0;
                foreach (EbxAssetEntry e in am.EnumerateEbx().ToList())
                {
                    if (!e.Bundles.Contains(sid)) continue;
                    // the host level's navmesh: loaded by bundle membership, it makes the spawn system accept only
                    // spawn points near the HOST's walkable ground (Jakku y 430-480), not the new terrain
                    if (!o.MirrorPathfinding && e.Name.IndexOf("pathfindingblobs", StringComparison.OrdinalIgnoreCase) >= 0) { skippedNav++; continue; }
                    if (e.Name.StartsWith(modeDir, StringComparison.OrdinalIgnoreCase) && (IsStructure(e.Type) || e.Type == "LevelDescriptionAsset" || e.Type == "NetworkRegistryAsset" || e.Type == "MeshVariationDatabase")) continue;
                    if (e.AddToBundle(tid)) ne++;
                }
                foreach (ResAssetEntry re in am.EnumerateRes()) if (re.Bundles.Contains(sid) && re.AddToBundle(tid)) nr++;
                foreach (ChunkAssetEntry c in am.EnumerateChunks()) if (c.Bundles.Contains(sid) && c.AddToBundle(tid)) nc++;
                o.Log("mirror-extra: " + src.Name + " -> " + target.Name + ": " + ne + " ebx, " + nr + " res, " + nc + " chunks" + (skippedNav > 0 ? ", " + skippedNav + " pathfinding blob(s) left out" : ""));
            }
        }

        private void EnqueueSchematicsClass(EbxAssetEntry schem, int bundle, HashSet<int> loaded, HashSet<Guid> visited,
                                            Queue<KeyValuePair<EbxAssetEntry, int>> queue, Result r)
        {
            string cls = null;
            try
            {
                object root = am.GetEbx(schem).RootObject;
                object it = root.GetType().GetProperty("InstanceType")?.GetValue(root);
                cls = it?.ToString();
                // TypeRef.ToString() is "TypeRef '<name>'"
                if (cls != null)
                {
                    int q1 = cls.IndexOf('\''), q2 = cls.LastIndexOf('\'');
                    if (q1 >= 0 && q2 > q1) cls = cls.Substring(q1 + 1, q2 - q1 - 1);
                }
            }
            catch (Exception ex) { r.Warnings.Add("closure: cannot read InstanceType of " + schem.Name + ": " + ex.Message); return; }
            if (string.IsNullOrEmpty(cls)) return;
            EbxAssetEntry ce = am.GetEbxEntry(cls);
            bool trace = Environment.GetEnvironmentVariable("FROSTYCLI_TRACE") == "1";
            if (ce == null) { if (trace) o.Log("  schematics class: " + schem.Name + " -> '" + cls + "' not found"); return; }
            bool isLoaded = ce.Bundles.Concat(ce.AddedBundles).Any(loaded.Contains);
            if (trace) o.Log("  schematics class: " + schem.Name + " -> " + ce.Name + (isLoaded ? " (loaded)" : "") + (visited.Contains(ce.Guid) ? " (visited)" : ""));
            if (isLoaded) return;
            visited.Add(ce.Guid);
            ce.AddToBundle(bundle);
            r.ClosureAdded++;
            o.Log("  closure: + " + ce.Name + " (" + ce.Type + ", class named by " + schem.Name + ")");
            queue.Enqueue(new KeyValuePair<EbxAssetEntry, int>(ce, bundle));
        }

        private void MirrorIntoRoot(string bundleName, Result r)
        {
            string full = bundleName.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? bundleName : "win32/" + bundleName;
            BundleEntry src = am.EnumerateBundles().FirstOrDefault(b => !b.Added && string.Equals(b.Name, full, StringComparison.OrdinalIgnoreCase));
            BundleEntry root = am.EnumerateBundles().FirstOrDefault(b => string.Equals(b.Name, "win32/" + o.Destination, StringComparison.OrdinalIgnoreCase));
            if (src == null || root == null) { r.Warnings.Add("mirror-bundle: " + (src == null ? "no shipped bundle " + full : "no root bundle")); return; }
            int sid = am.GetBundleId(src), tid = am.GetBundleId(root);
            string inner = src.Name.Substring(6);
            // the source level's structure (and its registries/MVDBs) stays out: the new level has its own
            string levelDir = inner.Substring(0, inner.LastIndexOf('/') + 1);
            int ne = 0, nr = 0, nc = 0, skippedLogic = 0;
            foreach (EbxAssetEntry e in am.EnumerateEbx().ToList())
            {
                if (!e.Bundles.Contains(sid)) continue;
                if (e.Name.StartsWith(levelDir, StringComparison.OrdinalIgnoreCase) && (IsStructure(e.Type) || e.Type == "LevelDescriptionAsset" || e.Type == "NetworkRegistryAsset" || e.Type == "MeshVariationDatabase")) continue;
                if (e.Name.IndexOf("pathfindingblobs", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (e.AddToBundle(tid)) ne++;
            }
            foreach (ResAssetEntry re in am.EnumerateRes()) if (re.Bundles.Contains(sid) && re.AddToBundle(tid)) nr++;
            foreach (ChunkAssetEntry c in am.EnumerateChunks()) if (c.Bundles.Contains(sid) && c.AddToBundle(tid)) nc++;
            if (skippedLogic > 0) o.Log("mirror-bundle: " + skippedLogic + " class/schematics asset(s) of " + levelDir + " left out");
            o.Log("mirror-bundle: " + src.Name + " -> " + root.Name + ": " + ne + " ebx, " + nr + " res, " + nc + " chunks");
        }

        private void AddVehicleSpawners(Result r)
        {
            string layerPath = dstDir + o.VehicleLayer;
            EbxAssetEntry le = am.GetEbxEntry(layerPath);
            if (le == null) { r.Warnings.Add("vehicles: no " + layerPath); return; }
            EbxAsset layer = am.GetEbx(le);
            IList objects = (IList)Get(layer.RootObject, "Objects");
            object template = Resolve(layer, objects).FirstOrDefault(x => x.GetType().Name == "SpatialPrefabReferenceObjectData");
            // no spatial prefab to copy (Tatooine's Default is a WorldPart of terrain and effects): ObjectBlueprints can
            // still be placed, as ObjectReferenceObjectData built with the defaults `level add` uses
            Type objectRefType = FrostySdk.TypeLibrary.GetType("ObjectReferenceObjectData");
            if (template == null && (objectRefType == null || o.Vehicles.Any(v => am.GetEbxEntry(v.Substring(0, Math.Max(0, v.LastIndexOf('@'))))?.Type != "ObjectBlueprint")))
            { r.Warnings.Add("vehicles: " + layerPath + " has no SpatialPrefabReferenceObjectData to copy"); return; }
            // the registry shipped in the layer's own (new) bundle
            int layerBundle = le.AddedBundles.FirstOrDefault(newBundles.Contains);
            // the placing sub-world's own registry first (its entities replicate through it), then one in its bundle
            EbxAssetEntry regEntry = am.GetEbxEntry(layerPath + "_networkregistry_Win32")
                                     ?? am.EnumerateEbx("NetworkRegistryAsset").FirstOrDefault(x => x.IsAdded && x.AddedBundles.Contains(layerBundle))
                                     ?? am.GetEbxEntry(o.Destination + "_networkregistry_Win32");
            if (!layerPath.Equals(o.Destination, StringComparison.OrdinalIgnoreCase) && am.GetEbxEntry(layerPath + "_networkregistry_Win32") == null)
                r.Warnings.Add("vehicles: " + layerPath + " has no network registry of its own; networked placements there may not replicate");
            string regPath = regEntry?.Name ?? "(none)";
            o.Log("vehicles: layer " + layerPath + ", registry " + regPath);
            EbxAsset reg = regEntry != null ? am.GetEbx(regEntry) : null;
            IList regObjects = reg != null ? Get(reg.RootObject, "Objects") as IList : null;
            var registered = new HashSet<string>();
            if (regObjects != null)
                foreach (object x in regObjects)
                    if (x is FrostySdk.Ebx.PointerRef pr && pr.Type == PointerRefType.External) registered.Add(pr.External.FileGuid + "/" + pr.External.ClassGuid);
            Action<EbxAssetEntry> register = e =>
            {
                if (regObjects == null || e == null) return;
                Guid root = (Guid)((dynamic)am.GetEbx(e).RootObject).GetInstanceGuid().ExportedGuid;
                if (!registered.Add(e.Guid + "/" + root)) return;
                regObjects.Add(new FrostySdk.Ebx.PointerRef(new EbxImportReference { FileGuid = e.Guid, ClassGuid = root }));
                reg.AddDependency(e.Guid);
            };
            foreach (string spec in o.Vehicles)
            {
                int at = spec.LastIndexOf('@');
                if (at <= 0) { r.Warnings.Add("vehicles: bad spec '" + spec + "' (want <prefab>@x,y,z[,yaw])"); continue; }
                string bpName = spec.Substring(0, at);
                double[] v = spec.Substring(at + 1).Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                EbxAssetEntry bp = am.GetEbxEntry(bpName);
                if (bp == null || v.Length < 3) { r.Warnings.Add("vehicles: '" + bpName + "' not found or no position"); continue; }
                EbxAsset bpAsset = am.GetEbx(bp);
                Guid bpRoot = (Guid)((dynamic)bpAsset.RootObject).GetInstanceGuid().ExportedGuid;

                // a spatial prefab is placed as a copy of the layer's SpatialPrefabReferenceObjectData; an ObjectBlueprint
                // (a building, a rock: a static model with its own collision) as an ObjectReferenceObjectData, the way
                // Tatooine's props layers place theirs -- the fields they share (ReferenceObjectData's) come from the template
                bool isObject = bp.Type == "ObjectBlueprint";
                Type refType = isObject ? (objectRefType ?? template.GetType()) : template.GetType();
                object obj = Activator.CreateInstance(refType);
                if (template == null)
                {
                    // shipped ObjectReferenceObjectData: lightmap scale 1 with size, no local player, dynamic radiosity
                    TrySet(obj, "Flags", 19995309u); TrySet(obj, "LightmapResolutionScale", 1u); TrySet(obj, "LightmapScaleWithSize", true);
                    TrySetEnum(obj, "LocalPlayerId", "LocalPlayerId_Invalid"); TrySetEnum(obj, "RadiosityTypeOverride", "RadiosityTypeOverride_Dynamic");
                }
                else foreach (PropertyInfo p in template.GetType().GetProperties())
                {
                    if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                    PropertyInfo q = refType.GetProperty(p.Name);
                    if (q == null || !q.CanWrite || q.PropertyType != p.PropertyType) continue;
                    object val = p.GetValue(template);
                    if (val is IList src)
                    {
                        var copy = (IList)Activator.CreateInstance(val.GetType());
                        foreach (object el in src) copy.Add(el);
                        val = copy;
                    }
                    q.SetValue(obj, val);
                }
                Set(obj, "Blueprint", new FrostySdk.Ebx.PointerRef(new EbxImportReference { FileGuid = bp.Guid, ClassGuid = bpRoot }));
                PlaceTransform(obj, "BlueprintTransform", v[0], v[1], v[2], (v.Length > 3 ? v[3] : 0) * Math.PI / 180.0);
                ((dynamic)obj).SetInstanceGuid(new FrostySdk.Ebx.AssetClassGuid(Guid.NewGuid(), -1));
                layer.AddObject(obj, false);
                FrostyCLI.Core.InProcessObjects.Mark(obj);
                objects.Add(new FrostySdk.Ebx.PointerRef(obj));

                register(bp);   // a static model with health / physics is a networked entity: registered like `level add` does
                if (isObject) { o.Log("props: " + bpName + " placed at " + string.Join(",", v.Take(3)) + " in " + layerPath + " (registered in " + regPath + ")"); continue; }
                // the vehicles the spawner spawns replicate too
                var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
                var refs = new List<EbxImportReference>();
                foreach (object x in bpAsset.Objects) CollectImportRefs(x, refs, seen);
                foreach (Guid fg in refs.Select(x => x.FileGuid).Distinct())
                {
                    EbxAssetEntry dep = am.GetEbxEntry(fg);
                    if (dep != null && dep.Type == "VehicleBlueprint") { register(dep); o.Log("vehicles: " + bpName + " spawns " + dep.Name); }
                }
                o.Log("vehicles: " + bpName + " placed at " + string.Join(",", v.Take(3)) + " in " + layerPath);
            }
            am.ModifyEbx(layerPath, layer);
            if (reg != null) am.ModifyEbx(regPath, reg); else r.Warnings.Add("vehicles: no " + regPath + " - spawners not registered");
        }

        private static void TrySet(object obj, string prop, object value)
        {
            PropertyInfo p = obj.GetType().GetProperty(prop);
            if (p == null || !p.CanWrite) return;
            try { p.SetValue(obj, Convert.ChangeType(value, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType)); } catch { }
        }

        private static void TrySetEnum(object obj, string prop, string name)
        {
            PropertyInfo p = obj.GetType().GetProperty(prop);
            if (p == null || !p.CanWrite || !p.PropertyType.IsEnum || !Enum.IsDefined(p.PropertyType, name)) return;
            p.SetValue(obj, Enum.Parse(p.PropertyType, name));
        }

        // like Place(), for a transform property of any name
        private static void PlaceTransform(object obj, string prop, double x, double y, double z, double yaw)
        {
            PropertyInfo tp = obj.GetType().GetProperty(prop);
            if (tp == null) return;
            object lt = tp.GetValue(obj);
            Type lt_t = lt.GetType();
            Action<string, double, double, double> vec = (name, vx, vy, vz) =>
            {
                PropertyInfo vp = lt_t.GetProperty(name);
                object vv = Activator.CreateInstance(vp.PropertyType);
                vp.PropertyType.GetProperty("x").SetValue(vv, (float)vx);
                vp.PropertyType.GetProperty("y").SetValue(vv, (float)vy);
                vp.PropertyType.GetProperty("z").SetValue(vv, (float)vz);
                vp.SetValue(lt, vv);
            };
            double fx = Math.Sin(yaw), fz = Math.Cos(yaw);   // yaw 0 = facing +z
            vec("forward", fx, 0, fz);
            vec("up", 0, 1, 0);
            vec("right", fz, 0, -fx);
            vec("trans", x, y, z);
            tp.SetValue(obj, lt);
        }

        // the new level's copies of the mode host's kept partitions (TeamDeathmatch_* under TeamDeathmatch; under Naboo_02's
        // Blast the layers keep TeamDeathmatch names, so a name prefix does not find them)
        private IEnumerable<string> ModeClones()
        {
            string modeDir = Dir(o.ModeHost);
            var keep = new HashSet<string>(o.ModeKeep, StringComparer.OrdinalIgnoreCase);
            string modePath = dstDir + o.ModeSubWorld;
            return clones.Where(kv => kv.Key.StartsWith(modeDir, StringComparison.OrdinalIgnoreCase) && keep.Contains(kv.Key.Substring(modeDir.Length)))
                         .Select(kv => kv.Value)
                         .Concat(clones.Values.Where(v => v.StartsWith(modePath + "_", StringComparison.OrdinalIgnoreCase)))
                         .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // a VisualEnvironmentReferenceObjectData for a root that has none: the fields of the first one found in the host
        // directory, a fresh instance guid, appended to the root's Objects
        private object NewVeReference(EbxAsset asset, IList objs, string hostDir)
        {
            object template = null;
            foreach (EbxAssetEntry e in am.EnumerateEbx().Where(x => x.Name.StartsWith(hostDir, StringComparison.OrdinalIgnoreCase)
                     && (x.Type == "LayerData" || x.Type == "SubWorldData" || x.Type == "LevelData")))
            {
                try { template = am.GetEbx(e).Objects.FirstOrDefault(x => x.GetType().Name == "VisualEnvironmentReferenceObjectData"); }
                catch { continue; }
                if (template != null) break;
            }
            if (template == null) return null;
            object obj = Activator.CreateInstance(template.GetType());
            foreach (PropertyInfo p in template.GetType().GetProperties())
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                object v = p.GetValue(template);
                if (v is IList) continue;
                p.SetValue(obj, v);
            }
            ((dynamic)obj).SetInstanceGuid(new FrostySdk.Ebx.AssetClassGuid(Guid.NewGuid(), -1));
            asset.AddObject(obj, false);
            FrostyCLI.Core.InProcessObjects.Mark(obj);
            objs.Add(new FrostySdk.Ebx.PointerRef(obj));
            o.Log("visual environment: the host's root had no reference; added one");
            return obj;
        }

        // the logic layers of the online PvP modes' sub-worlds (loaded only in their mode)
        private static readonly System.Text.RegularExpressions.Regex OnlineModeLayer = new System.Text.RegularExpressions.Regex(
            @"(_Online_Logic|^HeroArena_Logic|^Mode6_Logic|^ModeC_Logic|^PlanetaryMissions_Logic|^FantasyBattle_Logic)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private void AddAutoPlayerManager(string layer, int team1, int team2, Result r)
        {
            EbxAssetEntry fe = am.GetEbxEntry(layer);
            if (fe == null || fe.Type != "LayerData") { o.Log("auto-player bots: " + layer + " is not a layer; none added"); return; }
            EbxAsset fa = am.GetEbx(fe);
            if (!(Get(fa.RootObject, "Objects") is IList objs)) return;
            object m = FrostySdk.TypeLibrary.CreateObject("AutoPlayerManagerEntityData");
            if (m == null) { o.Log("auto-player bots: AutoPlayerManagerEntityData not in the SDK; none added"); return; }
            Guid g = Guid.NewGuid();
            ((dynamic)m).SetInstanceGuid(new FrostySdk.Ebx.AssetClassGuid(g, -1));
            foreach (var kv in new Dictionary<string, object>
            {
                { "Flags", EntityFlags(g, true) }, { "PlayerCount", -1 }, { "FillGameplayBotsTeam1", team1 },
                { "FillGameplayBotsTeam2", team2 }, { "OrphanTimeSeconds", 180f }, { "ResetForceFills", false },
            })
            {
                PropertyInfo pi = m.GetType().GetProperty(kv.Key);
                if (pi != null && pi.CanWrite) pi.SetValue(m, Convert.ChangeType(kv.Value, pi.PropertyType));
            }
            fa.AddObject(m, false);
            FrostyCLI.Core.InProcessObjects.Mark(m);
            objs.Add(new FrostySdk.Ebx.PointerRef(m));

            // Measured (MP3b/MP3c 2026-09-25): once started, the manager fills the teams and the round leaves pre-round
            // (Blast 8 v 8, HvV 4 v 4), but the bots never spawn a soldier -- fighting bots need the game's gameplay-bot
            // logic (Gameplay/GameModes/Mode9/Prefabs/PvE_BotLogic), which is not grafted here. An AutoPlayerEntity +
            // AutoplayerIsGameplayEntity pair next to it was tried and is not enough.
            // The manager does nothing until its ApplySettings event fires. The shipped levels fire it from the event that
            // streams their mode in (Tatooine_02, Kamino_03, Jakku_02, FOSD: GameModes_Flow SyncedBool.OnTrue ->
            // ApplySettings, Server). This manager sits inside the mode's own sub-world, so a delay that starts itself when
            // that sub-world streams in fires it: DelayEntityData AutoStart, RunOnce, Realm_Server, TimeDeltaType_World, as
            // the shipped self-starting delays are. It is a source only (see EntityFlags).
            object d = FrostySdk.TypeLibrary.CreateObject("DelayEntityData");
            if (d == null) { am.ModifyEbx(fe.Name, fa); o.Log("auto-player bots: DelayEntityData not in the SDK; " + fe.Name + " has a manager nothing starts"); return; }
            Guid dg = Guid.NewGuid();
            ((dynamic)d).SetInstanceGuid(new FrostySdk.Ebx.AssetClassGuid(dg, -1));
            TrySet(d, "Flags", EntityFlags(dg, false));
            TrySet(d, "Delay", 1f);
            TrySet(d, "AutoStart", true);
            TrySet(d, "RunOnce", true);
            TrySet(d, "RemoveDuplicateEvents", true);
            SetEnum(d, "Realm", 1);            // Realm_Server
            SetEnum(d, "TimeDeltaType", 2);    // TimeDeltaType_World
            fa.AddObject(d, false);
            FrostyCLI.Core.InProcessObjects.Mark(d);
            objs.Add(new FrostySdk.Ebx.PointerRef(d));

            am.ModifyEbx(fe.Name, fa);

            // a layer's connections live in the world partition that includes it (a LayerReferenceObjectData whose
            // Blueprint is the layer), as every shipped layer's do
            string parent = null;
            foreach (string part in clones.Values.Distinct())
            {
                EbxAssetEntry pe = am.GetEbxEntry(part);
                if (pe == null || (pe.Type != "SubWorldData" && pe.Type != "LevelData")) continue;
                EbxAsset pa = am.GetEbx(pe);
                if (!pa.Objects.Any(x => x.GetType().Name == "LayerReferenceObjectData" && Get(x, "Blueprint") is FrostySdk.Ebx.PointerRef bp
                        && bp.Type == PointerRefType.External && bp.External.FileGuid == fe.Guid)) continue;
                Func<Guid, FrostySdk.Ebx.PointerRef> inLayer = id => new FrostySdk.Ebx.PointerRef(new EbxImportReference { FileGuid = fe.Guid, ClassGuid = id });
                AddEventConnection(pa.RootObject, inLayer(dg), "Out", inLayer(g), "ApplySettings", 3);   // 3 = Server
                am.ModifyEbx(pe.Name, pa);
                parent = pe.Name;
                break;
            }
            o.Log(fe.Name + ": added an auto-player manager, bots " + team1 + " + " + team2 + (parent != null
                ? ", started 1 s after the sub-world streams in (" + Leaf(parent) + ": delay.Out -> ApplySettings)"
                : "; WARNING no world partition includes the layer, so nothing starts it"));
            if (parent == null) r.Warnings.Add("auto-player bots: nothing starts the manager in " + fe.Name);
        }

        /// <summary>An entity's Flags as the shipped ones carry them: the instance guid's low 24 bits (its first three
        /// bytes; FOSD's manager 78d5c95b -> 0x04d5c95b) and 0x04 &lt;&lt; 24 when it is the target of a server event
        /// connection -- measured over 176 shipped self-starting server delays: 36 of the 38 event targets have it (the
        /// other two are targets of client connections only) and none of the 138 others; every shipped auto-player
        /// manager, an ApplySettings target, has it. (0x01 &lt;&lt; 24 splits those delays 59/58 with no relation to their
        /// connections; the other high bits are left clear.)</summary>
        public static uint EntityFlags(Guid instance, bool eventTarget)
        {
            byte[] b = instance.ToByteArray();
            return (uint)(b[0] | b[1] << 8 | b[2] << 16) | (eventTarget ? 0x04u << 24 : 0u);
        }

        private static void SetEnum(object obj, string prop, int value)
        {
            PropertyInfo p = obj.GetType().GetProperty(prop);
            if (p == null || !p.CanWrite) return;
            Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            p.SetValue(obj, t.IsEnum ? Enum.ToObject(t, value) : Convert.ChangeType(value, t));
        }

        // an EventConnection on a partition root; events are named (hashed as the engine does)
        private static void AddEventConnection(object root, object source, string sourceEvent, object target, string targetEvent, int targetType)
        {
            IList list = (IList)Get(root, "EventConnections");
            Type ct = list.GetType().GetGenericArguments()[0];
            object c = Activator.CreateInstance(ct);
            // an object in this partition, or a reference already made (an instance of another partition, e.g. a layer's)
            ct.GetProperty("Source").SetValue(c, source is FrostySdk.Ebx.PointerRef sp ? sp : new FrostySdk.Ebx.PointerRef(source));
            ct.GetProperty("Target").SetValue(c, target is FrostySdk.Ebx.PointerRef tp ? tp : new FrostySdk.Ebx.PointerRef(target));
            foreach (var kv in new[] { new[] { "SourceEvent", sourceEvent }, new[] { "TargetEvent", targetEvent } })
            {
                PropertyInfo p = ct.GetProperty(kv[0]);
                object spec = p.GetValue(c) ?? Activator.CreateInstance(p.PropertyType);
                PropertyInfo id = spec.GetType().GetProperty("Id");
                id.SetValue(spec, Convert.ChangeType(FrostySdk.Utils.HashString(kv[1]), id.PropertyType));
                p.SetValue(c, spec);
            }
            PropertyInfo tt = ct.GetProperty("TargetType");
            if (tt != null) tt.SetValue(c, Enum.ToObject(Nullable.GetUnderlyingType(tt.PropertyType) ?? tt.PropertyType, targetType));
            list.Add(c);
        }

        private void Absorb(LevelDuplicator.Result d, Result r)
        {
            foreach (var kv in d.CloneOf) clones[kv.Key] = kv.Value;
            foreach (int b in d.NewBundleIds) newBundles.Add(b);
            r.Cloned += d.StructureAssets; r.Bundles += d.Bundles;
            r.Warnings.AddRange(d.Warnings);
        }

        private static bool IsStructure(string type) =>
            type == "LevelData" || type == "SubWorldData" || type == "LayerData" || type == "WorldPartData";

        private bool BundleExists(string name)
        {
            string full = "win32/" + name;
            return am.EnumerateBundles().Any(b => string.Equals(b.Name, full, StringComparison.OrdinalIgnoreCase));
        }

        // LevelDescriptionAsset.Bundles: LevelBundleLoad { Name (no "win32/"), TryKeepBetweenLevels }
        private static List<string> BundleLoadNames(object descRoot)
        {
            var names = new List<string>();
            if (!(Get(descRoot, "Bundles") is IList list)) return names;
            foreach (object bl in list)
            {
                object n = Get(bl, "Name") ?? bl;
                if (n != null) names.Add(n.ToString());
            }
            return names;
        }

        private IEnumerable<int> DescriptionBundles(string description)
        {
            if (description == null) yield break;
            var names = new HashSet<string>(BundleLoadNames(am.GetEbx(am.GetEbxEntry(description)).RootObject).Select(n => "win32/" + n), StringComparer.OrdinalIgnoreCase);
            foreach (BundleEntry b in am.EnumerateBundles()) if (names.Contains(b.Name)) yield return am.GetBundleId(b);
        }

        // Shared bundles that no level description names are loaded at startup (default_settings,
        // gameplay/wrgameconfiguration, ui/static ...); the rest load only with the levels that list them.
        private HashSet<int> GlobalSharedBundles()
        {
            var perLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry e in am.EnumerateEbx("LevelDescriptionAsset"))
            {
                try { foreach (string n in BundleLoadNames(am.GetEbx(e).RootObject)) perLevel.Add("win32/" + n); } catch { }
            }
            var ids = new HashSet<int>();
            foreach (BundleEntry b in am.EnumerateBundles())
                if (b.Type == BundleType.SharedBundle && !perLevel.Contains(b.Name)) ids.Add(am.GetBundleId(b));
            return ids;
        }

        // The new level loads the shared bundles of both hosts: the mode host's content leans on its own list
        // (Jakku HvV: gameplay/bundles/sharedbundles/common/animation/sharedbundleanimation_common and
        // .../frontend+mp/abilities/sharedbundleabilities_frontend+mp, which FOSD does not load - without them the
        // HvV sub-world faulted at game+0x1522B97 as it streamed in).
        private void MergeDescriptionBundles(string description, string modeDir, Result r)
        {
            EbxAssetEntry hostDesc = am.EnumerateEbx("LevelDescriptionAsset").FirstOrDefault(e => e.Name.StartsWith(modeDir, StringComparison.OrdinalIgnoreCase) && !e.IsAdded);
            if (hostDesc == null) { r.Warnings.Add("the mode host has no LevelDescriptionAsset under " + modeDir + "; its shared bundles are not loaded"); return; }
            EbxAsset da = am.GetEbx(am.GetEbxEntry(description));
            IList list = (IList)Get(da.RootObject, "Bundles");
            var have = new HashSet<string>(BundleLoadNames(da.RootObject), StringComparer.OrdinalIgnoreCase);
            object template = list.Count > 0 ? list[0] : null;
            int at = Math.Max(0, list.Count - 1);   // before the level's own root bundle (last)
            int added = 0;
            foreach (object bl in (IList)Get(am.GetEbx(hostDesc).RootObject, "Bundles"))
            {
                string n = (Get(bl, "Name") ?? "").ToString();
                if (n.Length == 0 || have.Contains(n) || n.StartsWith(modeDir, StringComparison.OrdinalIgnoreCase)) continue;   // not the host's own root
                object nb = Activator.CreateInstance(template != null ? template.GetType() : bl.GetType());
                SetString(nb, "Name", n);
                list.Insert(at++, nb);
                have.Add(n); added++;
                o.Log("description: + bundle " + n + " (from " + hostDesc.Name + ")");
            }
            if (added > 0) am.ModifyEbx(description, da);
        }

        private HashSet<int> LoadedBundlesFor(string description)
        {
            var ids = new HashSet<int>(newBundles);
            ids.UnionWith(GlobalSharedBundles());
            ids.UnionWith(DescriptionBundles(description));
            return ids;
        }

        private HashSet<int> LoadedBundles(string description)
        {
            var ids = new HashSet<int>(newBundles);
            // shared bundles (default_settings, gameplay/wrgameconfiguration, the common shared bundles ...) are loaded
            // at startup; an asset shipped in one must not be copied into a level bundle (a second AudioSystem,
            // VOSystem ... in the level bundle faulted at game+0x104A06F half a second after the level came up)
            foreach (BundleEntry b in am.EnumerateBundles()) if (b.Type == BundleType.SharedBundle) ids.Add(am.GetBundleId(b));
            if (description == null) return ids;
            dynamic desc = am.GetEbx(am.GetEbxEntry(description)).RootObject;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object b in desc.Bundles) { names.Add(b.ToString()); names.Add("win32/" + b.ToString()); }
            foreach (BundleEntry b in am.EnumerateBundles()) if (names.Contains(b.Name)) ids.Add(am.GetBundleId(b));
            return ids;
        }

        // Load, edit, prune unreachable internal objects, save. drop(obj) removes an object from the partition along
        // with every list element / connection that refers to it.
        private void Edit(string path, Dictionary<Guid, HashSet<Guid>> removedByFile, Result r,
                          Action<EbxAsset, object, Action<object>> edit)
        {
            EbxAssetEntry e = am.GetEbxEntry(path);
            if (e == null) throw new InvalidOperationException("not found: " + path);
            EbxAsset a = am.GetEbx(e);
            var dropped = new HashSet<object>(LevelDuplicator.RefEq.Instance);
            edit(a, a.RootObject, obj => dropped.Add(obj));
            if (dropped.Count > 0)
            {
                HashSet<Guid> gone;
                if (!removedByFile.TryGetValue(e.Guid, out gone)) removedByFile[e.Guid] = gone = new HashSet<Guid>();
                foreach (object obj in dropped)
                {
                    dynamic ig = ((dynamic)obj).GetInstanceGuid();
                    if ((bool)ig.IsExported) gone.Add((Guid)ig.ExportedGuid);
                }
                var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
                foreach (object obj in a.Objects.ToList()) if (!dropped.Contains(obj)) r.RefsDropped += DropInternal(obj, dropped, seen);
                foreach (object obj in dropped) a.RemoveObject(obj);
                r.Pruned += dropped.Count;
                // internal (non-exported) objects nothing reaches any more
                var reach = new HashSet<object>(LevelDuplicator.RefEq.Instance);
                foreach (object obj in a.Objects)
                    if ((bool)((dynamic)obj).GetInstanceGuid().IsExported || ReferenceEquals(obj, a.RootObject)) Reach(obj, reach, new HashSet<object>(LevelDuplicator.RefEq.Instance));
                foreach (object obj in a.Objects.ToList()) if (!reach.Contains(obj)) { a.RemoveObject(obj); r.Pruned++; }
                o.Log(path + ": pruned " + dropped.Count + " object(s)");
            }
            am.ModifyEbx(path, a);
        }

        private static IEnumerable<object> Resolve(EbxAsset a, IList refs)
        {
            foreach (object el in refs)
                if (el is FrostySdk.Ebx.PointerRef pr && pr.Type == PointerRefType.Internal && pr.Internal != null) yield return pr.Internal;
        }

        private EbxAssetEntry BlueprintEntry(object obj)
        {
            object bp = Get(obj, "Blueprint");
            if (bp is FrostySdk.Ebx.PointerRef pr && pr.Type == PointerRefType.External) return am.GetEbxEntry(pr.External.FileGuid);
            return null;
        }

        private static object Deref(object v) =>
            v is FrostySdk.Ebx.PointerRef pr ? (pr.Type == PointerRefType.Internal ? pr.Internal : null) : v;

        private static object Get(object obj, string name)
        {
            if (obj == null) return null;
            PropertyInfo p = obj.GetType().GetProperty(name);
            return p?.GetValue(obj);
        }

        private static void Set(object obj, string name, object value)
        {
            PropertyInfo p = obj.GetType().GetProperty(name);
            if (p == null || !p.CanWrite) throw new InvalidOperationException(obj.GetType().Name + " has no writable " + name);
            p.SetValue(obj, value);
        }

        private static void SetString(object obj, string name, string value)
        {
            PropertyInfo p = obj.GetType().GetProperty(name);
            p.SetValue(obj, StrOf(p.PropertyType, value));
        }

        private static object Str(IList list, string value)
        {
            Type t = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : typeof(string);
            return StrOf(t, value);
        }

        private static object StrOf(Type t, string value)
        {
            if (t == typeof(string)) return value;
            MethodInfo imp = t.GetMethod("op_Implicit", new[] { typeof(string) });
            if (imp != null) return imp.Invoke(null, new object[] { value });
            return Activator.CreateInstance(t, value);
        }

        // Removes list elements and connections that point at a dropped object; clears single pointers to one.
        private static int DropInternal(object obj, HashSet<object> dropped, HashSet<object> seen)
        {
            if (obj == null) return 0;
            Type t = obj.GetType();
            if (LevelDuplicator.IsLeaf(t) || t.Name == "PointerRef" || t.Name == "CString" || t.Name == "ResourceRef") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            Func<object, bool> hits = v =>
            {
                if (v is FrostySdk.Ebx.PointerRef pr) return pr.Type == PointerRefType.Internal && pr.Internal != null && dropped.Contains(pr.Internal);
                if (v == null || LevelDuplicator.IsLeaf(v.GetType())) return false;
                // a connection struct: Source / Target pointing at a dropped object
                foreach (string f in new[] { "Source", "Target" })
                    if (Get(v, f) is FrostySdk.Ebx.PointerRef p2 && p2.Type == PointerRefType.Internal && p2.Internal != null && dropped.Contains(p2.Internal)) return true;
                return false;
            };
            if (obj is IList list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object el = list[i];
                    if (hits(el)) { list.RemoveAt(i); n++; continue; }
                    int k = DropInternal(el, dropped, seen);
                    if (k > 0 && el != null && el.GetType().IsValueType) list[i] = el;
                    n += k;
                }
                return n;
            }
            foreach (PropertyInfo p in LevelDuplicator.Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v is FrostySdk.Ebx.PointerRef pr)
                {
                    if (pr.Type == PointerRefType.Internal && pr.Internal != null && dropped.Contains(pr.Internal) && p.CanWrite)
                    { p.SetValue(obj, new FrostySdk.Ebx.PointerRef()); n++; }
                    continue;
                }
                int k = DropInternal(v, dropped, seen);
                if (k > 0 && v != null && v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
                n += k;
            }
            return n;
        }

        // Same, for external references into pruned exported objects of other partitions.
        private static int DropRefs(EbxAsset a, Guid self, Dictionary<Guid, HashSet<Guid>> removedByFile)
        {
            if (removedByFile.Count == 0) return 0;
            Func<object, bool> ext = v =>
            {
                if (!(v is FrostySdk.Ebx.PointerRef pr) || pr.Type != PointerRefType.External) return false;
                HashSet<Guid> gone;
                return removedByFile.TryGetValue(pr.External.FileGuid, out gone) && gone.Contains(pr.External.ClassGuid);
            };
            int n = 0;
            var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
            foreach (object obj in a.Objects) n += DropExt(obj, ext, seen);
            return n;
        }

        private static int DropExt(object obj, Func<object, bool> hit, HashSet<object> seen)
        {
            if (obj == null) return 0;
            Type t = obj.GetType();
            if (LevelDuplicator.IsLeaf(t) || t.Name == "PointerRef" || t.Name == "CString" || t.Name == "ResourceRef") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            Func<object, bool> hits = v =>
            {
                if (hit(v)) return true;
                if (v == null || v is FrostySdk.Ebx.PointerRef || LevelDuplicator.IsLeaf(v.GetType())) return false;
                return hit(Get(v, "Source")) || hit(Get(v, "Target"));
            };
            if (obj is IList list)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object el = list[i];
                    if (hits(el)) { list.RemoveAt(i); n++; continue; }
                    int k = DropExt(el, hit, seen);
                    if (k > 0 && el != null && el.GetType().IsValueType) list[i] = el;
                    n += k;
                }
                return n;
            }
            foreach (PropertyInfo p in LevelDuplicator.Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v is FrostySdk.Ebx.PointerRef)
                {
                    if (hit(v) && p.CanWrite) { p.SetValue(obj, new FrostySdk.Ebx.PointerRef()); n++; }
                    continue;
                }
                int k = DropExt(v, hit, seen);
                if (k > 0 && v != null && v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
                n += k;
            }
            return n;
        }

        private static void Reach(object obj, HashSet<object> reach, HashSet<object> seen)
        {
            if (obj == null) return;
            if (obj is FrostySdk.Ebx.PointerRef pr)
            {
                if (pr.Type == PointerRefType.Internal && pr.Internal != null && reach.Add(pr.Internal)) Reach(pr.Internal, reach, seen);
                return;
            }
            Type t = obj.GetType();
            if (LevelDuplicator.IsLeaf(t) || t.Name == "CString" || t.Name == "ResourceRef") return;
            if (!t.IsValueType && !seen.Add(obj)) return;
            reach.Add(obj);
            if (obj is IList list) { foreach (object el in list) Reach(el, reach, seen); return; }
            foreach (PropertyInfo p in LevelDuplicator.Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                Reach(v, reach, seen);
            }
        }

        private static IEnumerable<Guid> Imports(EbxAsset a)
        {
            var files = new HashSet<Guid>();
            var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
            foreach (object obj in a.Objects) CollectImports(obj, files, seen);
            files.Remove(a.FileGuid);
            return files;
        }

        private static void CollectImports(object obj, HashSet<Guid> files, HashSet<object> seen)
        {
            if (obj == null) return;
            if (obj is FrostySdk.Ebx.PointerRef pr) { if (pr.Type == PointerRefType.External) files.Add(pr.External.FileGuid); return; }
            Type t = obj.GetType();
            if (LevelDuplicator.IsLeaf(t) || t.Name == "CString" || t.Name == "ResourceRef") return;
            if (!t.IsValueType && !seen.Add(obj)) return;
            if (obj is IList list) { foreach (object el in list) CollectImports(el, files, seen); return; }
            foreach (PropertyInfo p in LevelDuplicator.Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                CollectImports(v, files, seen);
            }
        }

        // In the description, any list of strings naming game modes (FOSD: Mode1..Mode9, ModeDefend ...) is set to
        // the new mode alone.
        private int ReplaceModeLists(object obj, HashSet<object> seen)
        {
            if (obj == null) return 0;
            Type t = obj.GetType();
            if (LevelDuplicator.IsLeaf(t) || t.Name == "PointerRef" || t.Name == "CString" || t.Name == "ResourceRef") return 0;
            if (!t.IsValueType && !seen.Add(obj)) return 0;
            int n = 0;
            if (obj is IList list)
            {
                bool strings = list.Count > 0 && list.Cast<object>().All(x => x != null && (x is string || x.GetType().Name == "CString"));
                if (strings && list.Cast<object>().Any(x => IsModeName(x.ToString())))
                {
                    o.Log("  description list [" + string.Join(", ", list.Cast<object>()) + "] -> [" + o.Mode + "]");
                    object m = Str(list, o.Mode);
                    list.Clear(); list.Add(m);
                    foreach (string extra in o.AlsoModes) list.Add(Str(list, extra));
                    return 1;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    object el = list[i];
                    int k = ReplaceModeLists(el, seen);
                    if (k > 0 && el != null && el.GetType().IsValueType) list[i] = el;
                    n += k;
                }
                return n;
            }
            foreach (PropertyInfo p in LevelDuplicator.Props(t))
            {
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                int k = ReplaceModeLists(v, seen);
                if (k > 0 && v != null && v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
                n += k;
            }
            return n;
        }

        private static readonly string[] KnownModes = {
            "Mode1", "Mode2", "Mode3", "Mode4", "Mode5", "Mode6", "Mode7", "Mode8", "Mode9", "ModeDefend", "ModeE", "ModeF",
            "PlanetaryBattles", "HeroesVersusVillains", "Blast", "SkirmishBlast", "TeamDeathmatch", "FantasyBattle",
            "PlanetaryMissions", "Arcade", "Onslaught", "Duel" };
        private static bool IsModeName(string s) => KnownModes.Contains(s, StringComparer.OrdinalIgnoreCase);

        // Team 1 in a line on one side of the centre, team 2 facing it on the other, neutral spawns on a ring - all at
        // the centre's height, facing the middle.
        private void LayOutSpawns(List<object> spawns)
        {
            double cx = o.SpawnCenter[0], cy = o.SpawnCenter[1], cz = o.SpawnCenter[2], R = o.SpawnRadius;
            var t1 = spawns.Where(s => (Get(s, "Team")?.ToString() ?? "") == "Team1").ToList();
            var t2 = spawns.Where(s => (Get(s, "Team")?.ToString() ?? "") == "Team2").ToList();
            var ne = spawns.Except(t1).Except(t2).ToList();
            for (int i = 0; i < t1.Count; i++) Place(t1[i], cx - R, cy, cz + (i - (t1.Count - 1) / 2.0) * 3, 0);          // faces +x
            for (int i = 0; i < t2.Count; i++) Place(t2[i], cx + R, cy, cz + (i - (t2.Count - 1) / 2.0) * 3, Math.PI);    // faces -x
            for (int i = 0; i < ne.Count; i++)
            {
                double a = 2 * Math.PI * i / Math.Max(1, ne.Count);
                double x = cx + 0.7 * R * Math.Cos(a), z = cz + 0.7 * R * Math.Sin(a);
                Place(ne[i], x, cy, z, Math.Atan2(cz - z, cx - x));
            }
        }

        // the new terrain's height under an entity (null: no terrain of the level's own, or a hole)
        private TerrainHeightSource heights;
        private bool heightsTried;
        private double? Ground(object obj)
        {
            if (!heightsTried)
            {
                heightsTried = true;
                if (o.TerrainPayload != null)
                {
                    var parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(o.TerrainPayload);
                    if (parsed.Success) heights = new TerrainHeightSource(parsed.Resource.Heightfield);
                }
            }
            if (heights == null) return null;
            try
            {
                dynamic t = obj.GetType().GetProperty("Transform").GetValue(obj);
                double x = t.trans.x, z = t.trans.z;
                if (heights.IsHole(x, z)) return null;
                return heights.SampleMetres(x, z);
            }
            catch { return null; }
        }

        private static void SetY(object obj, double y)
        {
            PropertyInfo tp = obj.GetType().GetProperty("Transform");
            object lt = tp.GetValue(obj);
            PropertyInfo vp = lt.GetType().GetProperty("trans");
            object v = vp.GetValue(lt);
            v.GetType().GetProperty("y").SetValue(v, (float)y);
            vp.SetValue(lt, v);
            tp.SetValue(obj, lt);
        }

        private static void MoveArena(object obj, double[] dxz, double? y)
        {
            PropertyInfo tp = obj.GetType().GetProperty("Transform");
            if (tp == null) return;
            object lt = tp.GetValue(obj);
            PropertyInfo vp = lt.GetType().GetProperty("trans");
            object v = vp.GetValue(lt);
            Type vt = vp.PropertyType;
            vt.GetProperty("x").SetValue(v, (float)((float)vt.GetProperty("x").GetValue(v) + dxz[0]));
            vt.GetProperty("z").SetValue(v, (float)((float)vt.GetProperty("z").GetValue(v) + dxz[1]));
            if (y.HasValue) vt.GetProperty("y").SetValue(v, (float)y.Value);
            vp.SetValue(lt, v);
            tp.SetValue(obj, lt);
        }

        // VolumeVectorShapeData.Points are world positions (the combat area ignores height)
        private static void ShiftPoints(object shape, double[] dxz)
        {
            if (!(Get(shape, "Points") is IList pts)) return;
            for (int i = 0; i < pts.Count; i++)
            {
                object v = pts[i];
                Type vt = v.GetType();
                vt.GetProperty("x").SetValue(v, (float)((float)vt.GetProperty("x").GetValue(v) + dxz[0]));
                vt.GetProperty("z").SetValue(v, (float)((float)vt.GetProperty("z").GetValue(v) + dxz[1]));
                pts[i] = v;
            }
        }

        private static object obj2t(object sp) => sp.GetType().GetProperty("Transform").GetValue(sp);

        private static void Offset(object obj, double[] d)
        {
            PropertyInfo tp = obj.GetType().GetProperty("Transform");
            object lt = tp.GetValue(obj);
            PropertyInfo vp = lt.GetType().GetProperty("trans");
            object v = vp.GetValue(lt);
            foreach (var c in new[] { "x", "y", "z" })
            {
                PropertyInfo cp = vp.PropertyType.GetProperty(c);
                cp.SetValue(v, (float)((float)cp.GetValue(v) + d[c == "x" ? 0 : c == "y" ? 1 : 2]));
            }
            vp.SetValue(lt, v);
            tp.SetValue(obj, lt);
        }

        // yaw: heading angle in the x/z plane, 0 = facing +x
        private static void Place(object obj, double x, double y, double z, double yaw)
        {
            PropertyInfo tp = obj.GetType().GetProperty("Transform");
            if (tp == null) return;
            object lt = tp.GetValue(obj);
            Type lt_t = lt.GetType();
            Func<string, double, double, double, object> vec = (name, vx, vy, vz) =>
            {
                PropertyInfo vp = lt_t.GetProperty(name);
                object v = Activator.CreateInstance(vp.PropertyType);
                vp.PropertyType.GetProperty("x").SetValue(v, (float)vx);
                vp.PropertyType.GetProperty("y").SetValue(v, (float)vy);
                vp.PropertyType.GetProperty("z").SetValue(v, (float)vz);
                vp.SetValue(lt, v);
                return v;
            };
            double fx = Math.Cos(yaw), fz = Math.Sin(yaw);
            // right-handed basis with up = +y: right = up x forward
            vec("forward", fx, 0, fz);
            vec("up", 0, 1, 0);
            vec("right", fz, 0, -fx);
            vec("trans", x, y, z);
            tp.SetValue(obj, lt);
        }
    }
}
