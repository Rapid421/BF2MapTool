using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyLevelTools
{
    // The plan for a new level that plays Skirmish (Arcade: SkirmishBlast / Onslaught / Duel) with bots on a shipped map's
    // shell: what `level create --skirmish-from <map>` keeps, drops and wires, worked out from the map's own data.
    //
    // Proven on Tatooine (19 AI, none stuck, 2026-09-24): the map's root with its own layers and world parts, GameModes with
    // its layers and its automation sub-world, TeamDeathmatch and its Skirmish child grafted from the same map (Online
    // left out), the map's own game-mode flow left to start the mode (keep-mode-lists), the mode's mirror limited to
    // GameModes (the root bundle is already mirrored once; twice faulted the game), an outdoor visual environment when the
    // kept partitions have none (Tatooine's sunny one lives in the city sub-world), the map's Lobby sub-world whole (the
    // spawn screen's dark room; placed buildings freeze every bot without it) and its lighting sub-world whole (the baked
    // GI; placed buildings draw black without it), and every other root sub-world -- the city, backdrops, cinematics,
    // other modes -- left out. Across all sixteen shipped Skirmish maps the same rules keep the
    // partitions holding the terrain entity and the Skirmish-gated pathfinding stream.
    public sealed class SkirmishShell
    {
        public string Host, Dir, ModeSubWorld, VisualEnvironment, TerrainData;
        public readonly List<string> Keep = new List<string>();
        public readonly List<string> Dropped = new List<string>();
        public readonly List<string> ModeKeep = new List<string>();
        public readonly List<Guid> Streams = new List<Guid>();
        public readonly Dictionary<Guid, string> StreamPartition = new Dictionary<Guid, string>();
        public readonly List<string> ClearStaticGroups = new List<string>();
        // with keepContent: the map's content sub-worlds (its city / art), kept loaded but emptied, so their buildings and
        // props can be placed on the new ground from their own bundle (copying them into the level's bundle faulted the
        // load at game+0x15AC131, TK5/TK6 2026-09-24; Tatooine rebuilt this way ran 19 AI, TP2)
        public readonly List<string> ContentSubWorlds = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<KeyValuePair<string, double[]>> SpawnPoints = new List<KeyValuePair<string, double[]>>();   // (team, x y z)
        public double[] Play;              // x0, x1, z0, z1 of the Skirmish play space
        public double SpawnY;              // median spawn height
        public int SpawnCount;
        // modes the level plays besides Arcade (`level create --modes`): Blast keeps TeamDeathmatch's Online half; any other
        // mode brings the GameModes sub-world that serves it (SubWorldReferenceObjectData.InclusionSettings ->
        // SubWorldInclusionSetting.EnabledOptions: Tatooine HeroArena = [HeroesVersusVillains], Mode9 = [Mode9, ModeDefend,
        // ModeE, ModeF], ...), its layers and sub-worlds grafted with the mode, art left out
        public readonly List<string> ExtraModes = new List<string>();
        public readonly List<string> ExtraModeSubWorlds = new List<string>();
        // navmesh streams of the extra modes (Tatooine: 5c0e8973 for Mode9/ModeDefend/ModeE/ModeF -- co-op and Instant
        // Action missions), stream -> the modes gating it; the Skirmish stream(s) stay in Streams
        public readonly Dictionary<Guid, string> ExtraStreams = new Dictionary<Guid, string>();
        // art of a mode sub-world (placed for the host's city): its props, collision proxies, ambient life and run-ups
        private static readonly Regex ModeArt = new Regex(
            @"(^art$|_art$|^art_|_art_|props|architecture|backdrop|foliage|vista|livingworld|runup|_collision$|decals?$)",
            RegexOptions.IgnoreCase);

        // The map's Lobby sub-world stays whole (every map has one, autoloaded): its art layer is the spawn screen's dark
        // room (UI/InGame/Spawn/PF_Spawn_Darkroom_01), and without it any placed building left every bot frozen -- their
        // physics bodies never reached the Havok world (the probe's teleport faulted inside hknp). Bisected in place on
        // Tatooine, 2026-09-24: TP2 (all) moving, TP3/TP5 (no Lobby) frozen, TP6 (no Lobby_Logic) moving, TP7 (no
        // Lobby_Art) frozen; the lighting sub-world Tatooine_012 made no difference to the bots (TP4).
        // The map's lighting sub-world stays whole too (Tatooine_012 = the Lighting_Sunny/Night/Sunset bakes rolled per
        // launch + HighEnd; Yavin: Lighting3; others: Lighting, LightSettings, Crait_Lighting, LightingLogic ...), art-named
        // layers inside it excepted: without it the level has no Enlighten system and every placed static model draws pure
        // black (TK16's houses; in place with it, TP2, they are lit). Its variants carry their own visual environments, so
        // no extra one is added then.
        private static readonly Regex LobbyTree = new Regex(@"^lobby$", RegexOptions.IgnoreCase);
        private static readonly Regex LightingTree = new Regex(@"^(highend|.*light(ing|settings)\w*)$", RegexOptions.IgnoreCase);
        // The lighting tree is the root sub-world whose sub-worlds carry the Enlighten bakes; its name is no guide
        // (Tatooine_012 is Tatooine's lighting, but Yavin_012 is Yavin's art -- temples, hangars, trees -- and Yavin's
        // lighting is Lighting3), so a sub-world counts when a StaticEnlightenEntityData sits somewhere under it.
        private bool IsLightingTree(string subWorld) => LightingTree.IsMatch(subWorld) || SubtreeHas(subWorld, "StaticEnlightenEntityData");

        private bool SubtreeHas(string partition, string typeName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { partition };
            var stack = new Stack<string>(); stack.Push(partition);
            while (stack.Count > 0)
            {
                string p = stack.Pop();
                if (Objects(p).Any(o => o.GetType().Name == typeName)) return true;
                foreach (var k in Children(p)) if (k.Key == "sub" && parts.ContainsKey(k.Value) && seen.Add(k.Value)) stack.Push(k.Value);
            }
            return false;
        }
        // ...except the variants' layers placed for the host's city: Tatooine's Sunny_Light / Sunny_VFX / Sunset_VFX /
        // Night_Light hold its street lamps (672 prefabs), smoke plumes (255 effects), local probe, reflection and
        // shadow-cache volumes -- all floating over a new layout (TK17, seen by the Enlighten session). The timelines
        // (Night_Lighting) and the *_Setup layers stay.
        private static readonly Regex HostCityLights = new Regex(@"_(vfx|light)$", RegexOptions.IgnoreCase);
        // The lighting variants' shadow extrusions go as well: baked along each variant's sun with the host's city standing,
        // they keep casting crisp shadows of every old building (domes as circles) on the new ground (TK17/TK19). The
        // Lobby tree's extrusions belong to geometry that is still there and stay.
        public readonly List<string> ShadowExtrusionDrops = new List<string>();
        //
        // Layers of the shell: the map's logic, light, sound and effects stay; its art, wrecks, collision proxies and
        // other modes' gameplay go (Jakku's Stardestroyer_03 is one placement of a whole crashed star destroyer, so the
        // test is by name, not by placement count -- audited on all sixteen Skirmish maps, 2026-09-24). A layer matching
        // neither list is left out; the ones holding the terrain entity or the Skirmish stream always stay.
        // Also placed for the host's city and so wrong on a new layout (Tatooine, TK17/TK19): Occluders (planes and volumes
        // that stood inside its buildings -- in the open they draw black and cull what is behind them), Default_Lighting
        // (31 wall and ceiling lamp prefabs, 15 lights, a probe volume) and the Pathfinding layer (22 AI jump/move links
        // onto roofs and through doors; the navmesh streams live in Pathfinding_Logic, which stays).
        private static readonly Regex DropLayer = new Regex(
            @"(^art$|_art$|^art_|_art_|zone\d|^area\d|props|architecture|ruins|hangar|backdrop|foliage|vista|^fb_|^ga_|decals?$|" +
            @"collision|blockout|gameplay|livingworld|stardestroyer|wreck|destruction|^content|occluder|^default_light|^pathfinding$)",
            RegexOptions.IgnoreCase);
        private static readonly Regex KeepLayer = new Regex(
            @"(^default|^setup|sound|light|pathfinding|fx|screenshots|shadow|terrainlid|schematic|_\d{3}$)",
            RegexOptions.IgnoreCase);

        private readonly AssetManager am;
        private bool keepContent;
        private readonly Dictionary<string, EbxAssetEntry> parts = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);

        private SkirmishShell(AssetManager am) { this.am = am; }

        public static SkirmishShell Plan(AssetManager am, string host, Action<string> log, bool keepContent = false, IEnumerable<string> modes = null)
        {
            var s = new SkirmishShell(am) { keepContent = keepContent };
            s.ExtraModes.AddRange((modes ?? new string[0]).Select(m => m.Trim()).Where(m => m.Length > 0 && !IsArcade(m)).Distinct(StringComparer.OrdinalIgnoreCase));
            EbxAssetEntry root = am.GetEbxEntry(host);
            if (root == null || root.Type != "LevelData") throw new InvalidOperationException("--skirmish-from: not a LevelData: " + host);
            s.Host = root.Name;
            s.Dir = root.Name.Substring(0, root.Name.LastIndexOf('/') + 1);
            foreach (EbxAssetEntry e in am.EnumerateEbx())
                if (e.Name.StartsWith(s.Dir, StringComparison.OrdinalIgnoreCase) && e.Name.IndexOf('/', s.Dir.Length) < 0
                    && (e.Type == "LevelData" || e.Type == "SubWorldData" || e.Type == "LayerData" || e.Type == "WorldPartData"))
                    s.parts[e.Name.Substring(s.Dir.Length)] = e;
            s.FindMode();
            s.AddExtraModes();
            s.WalkShell(log);
            s.FindStreams();
            s.FindSpawns();
            s.FindVisualEnvironment();
            s.FindTerrain();
            foreach (string n in s.Notes) log(n);
            return s;
        }

        // Options for LevelCreator; explicit options given after --skirmish-from still override these.
        public void Apply(LevelCreator.Options o)
        {
            o.TerrainHost = Host; o.TerrainKeep = Keep.ToArray();
            o.ModeHost = Host; o.Mode = "SkirmishBlast";
            o.AlsoModes = new[] { "SkirmishOnslaught", "SkirmishDuel" }.Concat(ExtraModes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            o.ModeSubWorld = ModeSubWorld; o.ModeKeep = ModeKeep.ToArray();
            o.KeepModeLists = true; o.ModeMirrorExtra = new[] { "GameModes" };
            o.BotsPerTeam = 0; o.BotsTeam1 = 0; o.BotsTeam2 = 0;
            o.ArenaMode = true; o.ArenaOffset = new double[] { 0, 0 };
            if (SpawnCount > 0) o.SpawnY = Math.Round(SpawnY, 1);
            o.VisualEnvironment = VisualEnvironment ?? "keep";
            o.ClearStaticGroups = ClearStaticGroups.ToArray();
            o.EmptyPartitions = ContentSubWorlds.ToArray();
            o.DropShadowExtrusions = ShadowExtrusionDrops.ToArray();
            if (ContentSubWorlds.Count > 0) o.VehicleLayer = ContentSubWorlds[0];
        }

        // ---- the shell -------------------------------------------------------------------------------------------------

        private void WalkShell(Action<string> log)
        {
            string rootName = Host.Substring(Dir.Length);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dropped = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<KeyValuePair<string, string>>();         // (partition, mode: root | gamemodes | layer)
            stack.Push(new KeyValuePair<string, string>(rootName, "root"));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                foreach (var kid in Children(cur.Key))
                {
                    string c = kid.Value;
                    if (ModeKeep.Contains(c, StringComparer.OrdinalIgnoreCase) || c.StartsWith("TeamDeathmatch", StringComparison.OrdinalIgnoreCase)
                        || keep.Contains(c) || !parts.ContainsKey(c)) continue;
                    if (cur.Value == "lighting" && kid.Key == "layer" && HostCityLights.IsMatch(c)) { dropped.Add(c + " (layer, the host city's lights/effects)"); continue; }
                    if (cur.Value == "lighting" && kid.Key == "layer" && DropLayer.IsMatch(c) && !Needed(c)) { dropped.Add(c + " (layer, art inside the lighting tree)"); continue; }
                    bool treeRoot = cur.Value == "root" && kid.Key == "sub" && (LobbyTree.IsMatch(c) || IsLightingTree(c));
                    if (cur.Value == "lobby" || cur.Value == "lighting" || treeRoot)
                    {
                        string tree = treeRoot ? (LobbyTree.IsMatch(c) ? "lobby" : "lighting") : cur.Value;
                        keep.Add(c); stack.Push(new KeyValuePair<string, string>(c, tree));      // the whole tree, art included
                        if (tree == "lighting" && Objects(c).Any(o => o.GetType().Name == "ShadowExtrusionDataEntityData")) ShadowExtrusionDrops.Add(c);
                        continue;
                    }
                    if (kid.Key == "layer")
                    {
                        bool mode = cur.Value == "gamemodes" || c.StartsWith("GameMode", StringComparison.OrdinalIgnoreCase);
                        // GameModes_CollisionPass: the host city's clutter collision -- 270 invisible boxes, 112 of them
                        // 1-5 m at ground level over Tatooine's arena, which the navmesh cannot see
                        if (mode && c.IndexOf("collision", StringComparison.OrdinalIgnoreCase) >= 0 && !Needed(c))
                        { dropped.Add(c + " (layer, the host's collision boxes)"); continue; }
                        if (!mode && (DropLayer.IsMatch(c) || !KeepLayer.IsMatch(c)) && !Needed(c)) { dropped.Add(c + " (layer)"); continue; }
                        keep.Add(c); stack.Push(new KeyValuePair<string, string>(c, "layer"));
                    }
                    else if (cur.Value == "root" && c.Equals("GameModes", StringComparison.OrdinalIgnoreCase))
                    {
                        keep.Add(c); stack.Push(new KeyValuePair<string, string>(c, "gamemodes"));
                    }
                    else if (cur.Value == "gamemodes" && c.IndexOf("automation", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        keep.Add(c); stack.Push(new KeyValuePair<string, string>(c, "layer"));
                    }
                    else if (keepContent && cur.Value == "root" && ContentSubWorlds.Count == 0 && IsMainContent(c))
                    {
                        keep.Add(c); ContentSubWorlds.Add(c);         // kept, emptied; its layers are not walked
                    }
                    else dropped.Add(c);
                }
            }
            Keep.AddRange(keep.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            Dropped.AddRange(dropped);
            // GameModes' own static model group is the geometry of the other modes (barricades, ruins)
            if (keep.Contains("GameModes") && Objects("GameModes").Any(x => x.GetType().Name == "StaticModelGroupEntityData"))
                ClearStaticGroups.Add("GameModes");
            // the root's own static group is the host city's fixtures (Tatooine: 25 wall lights, ceiling lamps, light
            // cones, 12 Enlighten proxies, pipes, scrap, an invalidate-light plane), and its shadow extrusion was baked
            // for them; both go ("<root>" = the new level's root). Its ShadowExtrusionLevelData (the sun directions) stays.
            if (Objects(rootName).Any(x => x.GetType().Name == "StaticModelGroupEntityData")) ClearStaticGroups.Add("<root>");
            if (Objects(rootName).Any(x => x.GetType().Name == "ShadowExtrusionDataEntityData")) ShadowExtrusionDrops.Add("<root>");
            Notes.Add("shell of " + Host + ": keeps " + Keep.Count + " partition(s) (" + string.Join(", ", Keep) + "); leaves out " + string.Join(", ", Dropped));
        }

        // the root sub-world carrying the map's city: its static model group (Tatooine Content; not Content_024, backdrops)
        // and a network registry of its own, which `level add` registers placed buildings in. (The bots that froze with
        // buildings in TK7/TK8/TK10 were the missing Lobby, not the registry -- see LobbyTree.)
        private bool IsMainContent(string subWorld) =>
            !Regex.IsMatch(subWorld, "lobby|cinematic|gamemode|lighting|sound|extrusion", RegexOptions.IgnoreCase)
            && Objects(subWorld).Any(o => o.GetType().Name == "StaticModelGroupEntityData")
            && am.GetEbxEntry(Dir + subWorld + "_networkregistry_Win32") != null;

        // a partition that holds the terrain entity, a pathfinding stream or the pathfinding system -- kept whatever its name
        private bool Needed(string partition) =>
            Objects(partition).Any(o => o.GetType().Name == "TerrainEntityData" || o.GetType().Name == "PathfindingStreamEntityData"
                                        || o.GetType().Name == "PathfindingSystemEntityData");

        // (kind, partition name relative to the directory) of a partition's layer / world part / sub-world references
        private IEnumerable<KeyValuePair<string, string>> Children(string partition)
        {
            foreach (object o in Objects(partition))
            {
                string t = o.GetType().Name;
                if (t == "SubWorldReferenceObjectData")
                {
                    string bn = Get(o, "BundleName")?.ToString() ?? "";
                    if (bn.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) yield return new KeyValuePair<string, string>("sub", bn.Substring(Dir.Length));
                }
                else if (t == "LayerReferenceObjectData" || t == "WorldPartReferenceObjectData")
                {
                    string n = ExternalName(Get(o, "Blueprint"));
                    if (n != null && n.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) yield return new KeyValuePair<string, string>("layer", n.Substring(Dir.Length));
                }
            }
        }

        // ---- the mode ----------------------------------------------------------------------------------------------------

        // The mode sub-world is GameModes' child that nests the Skirmish sub-world (TeamDeathmatch on most maps, Blast on
        // Naboo_02); everything under it but its Online half comes along.
        private void FindMode()
        {
            string gameModes = parts.Keys.FirstOrDefault(k => k.Equals("GameModes", StringComparison.OrdinalIgnoreCase));
            Func<string, bool> nestsSkirmish = k => Children(k).Any(c => c.Key == "sub" && c.Value.IndexOf("Skirmish", StringComparison.OrdinalIgnoreCase) >= 0);
            string mode = gameModes == null ? null : Children(gameModes).Where(c => c.Key == "sub" && parts.ContainsKey(c.Value))
                                                                         .Select(c => c.Value).FirstOrDefault(nestsSkirmish);
            mode = mode ?? parts.Keys.FirstOrDefault(k => k.Equals("TeamDeathmatch", StringComparison.OrdinalIgnoreCase));
            if (mode == null) throw new InvalidOperationException("--skirmish-from: " + Host + " has no Skirmish (no GameModes sub-world nests a Skirmish sub-world)");
            ModeSubWorld = mode;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mode };
            var stack = new Stack<string>(); stack.Push(mode);
            // the Online half is online Blast's: kept only when the level plays Blast
            bool online = ExtraModes.Contains("Blast", StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
                foreach (var c in Children(stack.Pop()))
                    if (parts.ContainsKey(c.Value) && (online || c.Value.IndexOf("Online", StringComparison.OrdinalIgnoreCase) < 0) && seen.Add(c.Value)) stack.Push(c.Value);
            ModeKeep.AddRange(seen.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            if (!ModeKeep.Any(k => k.IndexOf("Skirmish", StringComparison.OrdinalIgnoreCase) >= 0))
                Notes.Add("warning: no Skirmish partition under " + mode + " -- no Skirmish AI on this map");
            Notes.Add("mode sub-world " + mode + " with " + (ModeKeep.Count - 1) + " partition(s) under it");
        }

        private static bool IsArcade(string m) =>
            m.Equals("SkirmishBlast", StringComparison.OrdinalIgnoreCase) || m.Equals("SkirmishOnslaught", StringComparison.OrdinalIgnoreCase)
            || m.Equals("SkirmishDuel", StringComparison.OrdinalIgnoreCase);

        // the game modes a GameModes sub-world reference is streamed for: InclusionSettings -> Settings[] -> EnabledOptions
        private static List<string> ModesOf(object subWorldRef)
        {
            var modes = new List<string>();
            object inc = Deref(Get(subWorldRef, "InclusionSettings"));
            if (!(Get(inc, "Settings") is IList settings)) return modes;
            foreach (object st in settings)
                if (Get(Deref(st), "EnabledOptions") is IList opts)
                    foreach (object m in opts) if (m != null) modes.Add(m.ToString());
            return modes;
        }

        private static object Deref(object v) => v is PointerRef pr ? (pr.Type == PointerRefType.Internal ? pr.Internal : null) : v;

        // every GameModes sub-world serving a requested mode joins the mode graft: its whole tree, art layers left out,
        // its static model groups (the mode's own city geometry) cleared
        private void AddExtraModes()
        {
            if (ExtraModes.Count == 0) return;
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string gameModes = parts.Keys.FirstOrDefault(k => k.Equals("GameModes", StringComparison.OrdinalIgnoreCase));
            foreach (object o in gameModes == null ? new List<object>() : Objects(gameModes))
            {
                if (o.GetType().Name != "SubWorldReferenceObjectData") continue;
                string bn = Get(o, "BundleName")?.ToString() ?? "";
                if (!bn.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) continue;
                string sub = bn.Substring(Dir.Length);
                var enabled = ModesOf(o);
                if (sub.Equals(ModeSubWorld, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string m in enabled) if (ExtraModes.Contains(m, StringComparer.OrdinalIgnoreCase)) covered.Add(m);
                    continue;
                }
                var wanted = enabled.Where(m => ExtraModes.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
                if (wanted.Count == 0 || !parts.ContainsKey(sub)) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sub };
                var stack = new Stack<string>(); stack.Push(sub);
                int kept = 0;
                while (stack.Count > 0)
                {
                    string p = stack.Pop();
                    if (!ModeKeep.Contains(p, StringComparer.OrdinalIgnoreCase)) { ModeKeep.Add(p); kept++; }
                    if (Objects(p).Any(x => x.GetType().Name == "StaticModelGroupEntityData")) ClearStaticGroups.Add(p);
                    foreach (var kid in Children(p))
                    {
                        if (!parts.ContainsKey(kid.Value) || !seen.Add(kid.Value)) continue;
                        if (kid.Key == "layer" && ModeArt.IsMatch(kid.Value) && !Needed(kid.Value))
                        { Dropped.Add(kid.Value + " (layer, art of the " + sub + " mode)"); continue; }
                        stack.Push(kid.Value);
                    }
                }
                ExtraModeSubWorlds.Add(sub);
                foreach (string m in wanted) covered.Add(m);
                Notes.Add("mode sub-world " + sub + " for " + string.Join(", ", wanted) + ": " + kept + " partition(s)");
            }
            foreach (string m in ExtraModes.Where(m => !covered.Contains(m)))
                Notes.Add("warning: no GameModes sub-world of " + Host + " is streamed for " + m + " -- the level will list it but not play it");
        }

        // A stream is the Skirmish one when an InclusionSetting naming SkirmishBlast reaches its StreamIn within a few
        // connections (InclusionSetting -> AND / SyncedBool -> OnTrue -> StreamIn); connections live in whichever
        // partition owns both ends, so every partition of the map is read.
        private void FindStreams()
        {
            var skirmish = new HashSet<Guid>();
            var extra = new Dictionary<Guid, List<string>>();          // InclusionSetting -> the extra modes it names
            var streams = new Dictionary<Guid, string>();
            var edges = new Dictionary<Guid, HashSet<Guid>>();
            foreach (var kv in parts)
            {
                EbxAsset a;
                try { a = am.GetEbx(kv.Value); } catch { continue; }
                foreach (object o in a.Objects)
                {
                    string t = o.GetType().Name;
                    if (t == "InclusionSettingEntityData" && Get(o, "Settings") is IList items)
                    {
                        var named = items.Cast<object>().Select(x => x?.ToString() ?? "").ToList();
                        if (named.Any(x => string.Equals(x, "SkirmishBlast", StringComparison.OrdinalIgnoreCase))) skirmish.Add(InstanceGuid(o));
                        var mine = named.Where(x => ExtraModes.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
                        if (mine.Count > 0) extra[InstanceGuid(o)] = mine;
                    }
                    else if (t == "PathfindingStreamEntityData") streams[InstanceGuid(o)] = kv.Key;
                }
                foreach (string list in new[] { "EventConnections", "PropertyConnections", "LinkConnections" })
                {
                    if (!(Get(a.RootObject, list) is IList conns)) continue;
                    foreach (object c in conns)
                    {
                        Guid s = RefGuid(Get(c, "Source")), d = RefGuid(Get(c, "Target"));
                        if (s == Guid.Empty || d == Guid.Empty) continue;
                        if (!edges.TryGetValue(s, out var set)) edges[s] = set = new HashSet<Guid>();
                        set.Add(d);
                    }
                }
            }
            Func<Guid, HashSet<Guid>> reach = g =>
            {
                var hit = new HashSet<Guid>();
                var seen = new HashSet<Guid> { g };
                var frontier = new HashSet<Guid> { g };
                for (int hop = 0; hop < 5 && frontier.Count > 0; hop++)
                {
                    var next = new HashSet<Guid>();
                    foreach (Guid f in frontier)
                        if (edges.TryGetValue(f, out var outs)) foreach (Guid n in outs) if (seen.Add(n)) next.Add(n);
                    foreach (Guid n in next) if (streams.ContainsKey(n)) hit.Add(n);
                    frontier = next;
                }
                return hit;
            };
            var found = new HashSet<Guid>();
            foreach (Guid g in skirmish) found.UnionWith(reach(g));
            foreach (Guid g in found.OrderBy(x => x)) { Streams.Add(g); StreamPartition[g] = streams[g]; }
            var byStream = new Dictionary<Guid, SortedSet<string>>();
            foreach (var kv in extra)
                foreach (Guid st in reach(kv.Key))
                {
                    if (found.Contains(st)) continue;
                    if (!byStream.TryGetValue(st, out var ms)) byStream[st] = ms = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    ms.UnionWith(kv.Value);
                }
            foreach (var kv in byStream.OrderBy(x => x.Key))
            {
                ExtraStreams[kv.Key] = string.Join(",", kv.Value);
                StreamPartition[kv.Key] = streams[kv.Key];
                Notes.Add("navmesh stream " + kv.Key + " in " + streams[kv.Key] + " for " + ExtraStreams[kv.Key]);
            }
            if (Streams.Count == 0) Notes.Add("warning: no pathfinding stream gated on SkirmishBlast found; pick one with navmesh build --stream");
            else Notes.Add("Skirmish navmesh stream(s): " + string.Join(", ", Streams.Select(g => g + " in " + StreamPartition[g])));
        }

        // ---- spawns, play space, lighting, terrain -----------------------------------------------------------------------

        private void FindSpawns()
        {
            var ys = new List<double>();
            var shape = new List<double[]>();
            var volume = new List<double[]>();
            foreach (string k in ModeKeep)
                foreach (object o in Objects(k))
                {
                    string t = o.GetType().Name;
                    if (t == "AlternateSpawnEntityData" && Trans(Get(o, "Transform")) is double[] p)
                    {
                        ys.Add(p[1]); Grow(ref Play, p);
                        SpawnPoints.Add(new KeyValuePair<string, double[]>(Get(o, "Team")?.ToString() ?? "", p));
                    }
                    else if (t == "SpawnLocationFinderShapeData" && k.IndexOf("Skirmish", StringComparison.OrdinalIgnoreCase) >= 0) shape.AddRange(Points(o));
                    else if (t == "VolumeVectorShapeData") volume.AddRange(Points(o));
                }
            SpawnCount = ys.Count;
            if (ys.Count > 0) { ys.Sort(); SpawnY = ys[ys.Count / 2]; }
            var pts = shape.Count > 0 ? shape : volume;
            if (pts.Count > 0) { Play = null; foreach (var p in pts) Grow(ref Play, p); }
            Notes.Add(SpawnCount + " spawn(s), median height " + SpawnY.ToString("0.0") + " m; play space " +
                      (Play == null ? "unknown" : string.Format("x {0:0}..{1:0}, z {2:0}..{3:0}", Play[0], Play[1], Play[2], Play[3])));
        }

        private void FindVisualEnvironment()
        {
            Func<string, bool> outdoor = n => n != null && n.IndexOf("/global/", StringComparison.OrdinalIgnoreCase) < 0
                && Regex.IsMatch(n, @"/ve_[^/]*$", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(n, @"interior|cutscene|ending|disableoutdoor", RegexOptions.IgnoreCase);
            var kept = Keep.Concat(new[] { Host.Substring(Dir.Length) }).SelectMany(k => Objects(k))
                           .Where(o => o.GetType().Name == "VisualEnvironmentReferenceObjectData").Select(o => ExternalName(Get(o, "Blueprint")));
            if (kept.Any(outdoor)) { VisualEnvironment = null; Notes.Add("visual environment: the shell's own"); return; }
            var all = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in parts.Keys)
                foreach (object o in Objects(k))
                    if (o.GetType().Name == "VisualEnvironmentReferenceObjectData")
                    {
                        string n = ExternalName(Get(o, "Blueprint"));
                        if (outdoor(n)) all[n] = all.TryGetValue(n, out int c) ? c + 1 : 1;
                    }
            Func<string, int> rank = n => Regex.IsMatch(n, "sunny|_day|clear", RegexOptions.IgnoreCase) ? 0
                                        : Regex.IsMatch(n, "cloudy|default", RegexOptions.IgnoreCase) ? 1 : 2;
            VisualEnvironment = all.Keys.OrderBy(rank).ThenByDescending(n => all[n]).FirstOrDefault();
            Notes.Add("visual environment: " + (VisualEnvironment ?? "none found outdoors; the shell's own kept"));
        }

        private void FindTerrain()
        {
            foreach (string k in Keep)
                foreach (object o in Objects(k))
                    if (o.GetType().Name == "TerrainEntityData")
                    {
                        TerrainData = ExternalName(Get(o, "TerrainAsset"));
                        Notes.Add("terrain: " + (TerrainData ?? "?") + " in " + k + " (build your terrain with terrain create --host " +
                                  (TerrainData ?? "?").ToLowerInvariant() + ".streamingtree_win32)");
                        return;
                    }
            Notes.Add("warning: no terrain entity in the kept partitions -- this map cannot take a terrain of its own");
        }

        // ---- helpers -----------------------------------------------------------------------------------------------------

        private readonly Dictionary<string, List<object>> objectCache = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

        private List<object> Objects(string partition)
        {
            if (objectCache.TryGetValue(partition, out var list)) return list;
            list = new List<object>();
            if (parts.TryGetValue(partition, out var e))
                try { list.AddRange(am.GetEbx(e).Objects); } catch { }
            objectCache[partition] = list;
            return list;
        }

        private static object Get(object o, string prop) => o?.GetType().GetProperty(prop)?.GetValue(o);

        private string ExternalName(object pointer)
        {
            if (!(pointer is PointerRef pr) || pr.Type != PointerRefType.External) return null;
            return am.GetEbxEntry(pr.External.FileGuid)?.Name;
        }

        private static Guid InstanceGuid(object o)
        {
            try { return (Guid)((dynamic)o).GetInstanceGuid().ExportedGuid; } catch { return Guid.Empty; }
        }

        private static Guid RefGuid(object pointer)
        {
            if (!(pointer is PointerRef pr)) return Guid.Empty;
            if (pr.Type == PointerRefType.External) return pr.External.ClassGuid;
            if (pr.Type == PointerRefType.Internal) return InstanceGuid(pr.Internal);
            return Guid.Empty;
        }

        private static double[] Trans(object lt)
        {
            try { dynamic t = lt; return new double[] { t.trans.x, t.trans.y, t.trans.z }; } catch { return null; }
        }

        private static IEnumerable<double[]> Points(object shape)
        {
            if (!(Get(shape, "Points") is IList pts)) yield break;
            double[] origin = Trans(Get(shape, "Transform")) ?? new double[3];
            foreach (object p in pts)
            {
                double[] v = null;
                try { dynamic d = p; v = new double[] { (double)d.x + origin[0], (double)d.y + origin[1], (double)d.z + origin[2] }; } catch { }
                if (v != null) yield return v;
            }
        }

        private static void Grow(ref double[] box, double[] p)
        {
            if (box == null) { box = new[] { p[0], p[0], p[2], p[2] }; return; }
            box[0] = Math.Min(box[0], p[0]); box[1] = Math.Max(box[1], p[0]);
            box[2] = Math.Min(box[2], p[2]); box[3] = Math.Max(box[3], p[2]);
        }
    }
}
