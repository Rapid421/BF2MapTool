using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten keys <level>`: the lightmap key (createLightMapInstanceHashKey) of every static mesh placement a level
    /// loads - static-model-group member instances and object references to object blueprints - checked against the
    /// mesh-instance records of the level's bakes. Proves the key recipe on shipped data before charts are written for
    /// new placements, and lists which placements have a chart.
    /// </summary>
    public static class EnlightenKeys
    {
        /// <summary>One placed mesh: the mesh asset, its instance GUID (the key's GUID) and the world transform rows.</summary>
        public sealed class Placement
        {
            public string Partition, Source, MeshPath, Override;
            public string InnerOverride;  // a prefab's mesh: the override of the reference inside the prefab that places it
            public Guid MeshGuid;
            public double[] Right, Up, Forward, Trans;
            public Guid ObjectGuid;       // the placing object (for edits)
            public Guid GroupGuid;        // a group instance: the StaticModelGroupEntityData's instance guid (for edits)
            public int GroupId = -1, ObjectId = -1;   // InternalId of the group / placing object when it is not exported
            public int Member = -1, Instance = -1;
            public uint Key;
        }

        public static int Execute(string[] args, ExportLogger logger)
        {
            string level = null; int depth = 3; bool list = false; string only = null;
            var extraBakes = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--depth" && i + 1 < args.Length) depth = int.Parse(args[++i]);
                else if (args[i] == "--list") list = true;
                else if (args[i] == "--only" && i + 1 < args.Length) only = args[++i];
                else if (args[i] == "--bake" && i + 1 < args.Length) extraBakes.Add(args[++i]);
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (level == null) { Console.Error.WriteLine("Usage: frostycli enlighten keys <level> [--depth n] [--only <partition substring>] [--bake <EnlightenDataAsset>]... [--list]"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("enlighten keys: AssetManager not initialized."); return 1; }

            // one partition at a time: keep the placements and the bakes, drop each dump
            var placements = new List<Placement>();
            var keys = new Dictionary<string, HashSet<uint>>();
            int partitionCount = 0;
            Walk(level, depth, (name, dump) =>
            {
                partitionCount++;
                if (only == null || name.IndexOf(only, StringComparison.OrdinalIgnoreCase) >= 0) placements.AddRange(Collect(name, dump));
                var entities = new JArray(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                EnlightenCommand.CollectEnlightenReferences(dump, entities, paths);
                foreach (JObject e in entities)
                {
                    if ((string)e["type"] != "StaticEnlightenEntityData" || e["dynamicEnlightenData"] == null) continue;
                    string dyn = (string)e["dynamicEnlightenData"];
                    if (keys.ContainsKey(dyn)) continue;
                    EnlightenDatabase db = LoadEnd(dyn);
                    if (db != null) keys[dyn] = new HashSet<uint>(db.MeshInstances.Select(m => m.Hash));
                }
            });
            // a deep dump can show an object at more than one reference: one placement per member instance / object
            placements = placements.GroupBy(p => p.Partition + "|" + p.Source + "|" + p.MeshPath + "|" + p.Key + "|" + p.Member + "|" + p.Instance + "|" + p.ObjectGuid)
                                   .Select(g => g.First()).ToList();
            foreach (string eb in extraBakes)
            {
                EnlightenDatabase db = LoadEnd(eb);
                if (db == null) { Console.Error.WriteLine("enlighten keys: no database for " + eb); return 1; }
                keys[eb] = new HashSet<uint>(db.MeshInstances.Select(m => m.Hash));
            }
            Console.Out.WriteLine(string.Format("{0} partition(s), {1} static mesh placement(s), {2} bake(s): {3}", partitionCount, placements.Count,
                keys.Count, string.Join(", ", keys.Select(k => Path.GetFileName(k.Key) + " (" + k.Value.Count + " records)"))));
            foreach (var group in placements.GroupBy(p => p.Partition))
            {
                var items = group.ToList();
                string line = string.Format("  {0}: {1} placement(s)", group.Key, items.Count);
                foreach (var k in keys)
                    line += string.Format(", {0} in {1}", items.Count(p => k.Value.Contains(p.Key)), Path.GetFileName(k.Key));
                Console.Out.WriteLine(line);
                if (list)
                    foreach (var p in items)
                        Console.Out.WriteLine(string.Format("    {0:x8} {1} {2} ({3:F2}, {4:F2}, {5:F2}) {6}{7}", p.Key, p.Source, p.MeshPath, p.Trans[0], p.Trans[1], p.Trans[2],
                            p.Override ?? "", keys.Any(k => k.Value.Contains(p.Key)) ? "  [chart]" : ""));
            }
            int total = placements.Count, hit = placements.Count(p => keys.Values.Any(k => k.Contains(p.Key)));
            Console.Out.WriteLine(string.Format("enlighten keys: {0} of {1} placements have a record in a bake ({2:P1})", hit, total, total == 0 ? 0 : (double)hit / total));
            return 0;
        }

        /// <summary>The level and every sub-world / layer it references (BFS over BundleName and layer blueprints).</summary>
        public static List<KeyValuePair<string, JObject>> Partitions(string root, int depth)
        {
            var result = new List<KeyValuePair<string, JObject>>();
            Walk(root, depth, (n, d) => result.Add(new KeyValuePair<string, JObject>(n, d)));
            return result;
        }

        /// <summary>Visit the level and every sub-world / layer it references, one dump at a time.</summary>
        public static void Walk(string root, int depth, Action<string, JObject> visit)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (!seen.Add(item.Key)) continue;
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(item.Key);
                if (entry == null) continue;
                JObject dump;
                try { dump = EbxDumper.Dump(App.AssetManager.GetEbx(entry), resolve: true, deep: true); }
                catch { continue; }
                visit(entry.Name, dump);
                if (item.Value >= depth) continue;
                foreach (JObject o in Objects(dump))
                {
                    string next = null;
                    if (o["BundleName"] is JValue bn && bn.Type == JTokenType.String && !string.IsNullOrEmpty((string)bn))
                    {
                        next = (string)bn;
                        if (next.StartsWith("win32/", StringComparison.OrdinalIgnoreCase)) next = next.Substring(6);
                    }
                    else if ((string)o["__type"] == "LayerReferenceObjectData" && o["Blueprint"] is JObject bp && bp["__path"] != null)
                        next = (string)bp["__path"];
                    if (next != null) queue.Enqueue(new KeyValuePair<string, int>(next, item.Value + 1));
                }
            }
        }

        static readonly Dictionary<string, List<Tuple<string, Guid>>> BlueprintMeshes = new Dictionary<string, List<Tuple<string, Guid>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Static mesh placements of one partition: group member instances and object references.</summary>
        public static List<Placement> Collect(string partition, JObject dump)
        {
            var list = new List<Placement>();
            foreach (JObject o in Objects(dump))
            {
                string type = (string)o["__type"];
                if (type == "StaticModelGroupEntityData" && o["MemberDatas"] is JArray members)
                {
                    // Havok-backed members (no InstanceTransforms) keep their placements in the group's physics
                    // resource, in member order: the static compound whose instance count is theirs (level move/remove)
                    List<HavokTransformReader.Transform> havok = null;
                    int havokBase = 0;
                    // a group placed by a prefab (2015's E3 base) carries its own Transform: instance transforms,
                    // inline or Havok, are local to it, and the lightmap key is taken in world space
                    Placement gt = FromTransform(o["Transform"] as JObject);
                    bool groupMoved = gt != null && !IsIdentity(gt);
                    double[][] gm = groupMoved ? new[] { gt.Right, gt.Up, gt.Forward, gt.Trans } : null;
                    for (int m = 0; m < members.Count; m++)
                    {
                        var md = members[m] as JObject;
                        if (md == null) continue;
                        int count = md["InstanceCount"] != null ? (int)md["InstanceCount"] : 0;
                        var inline = md["InstanceTransforms"] as JArray;
                        bool isHavok = (inline == null || inline.Count == 0) && count > 0;
                        int myBase = havokBase;
                        if (isHavok) havokBase += count;
                        if (!(md["MeshAsset"] is JObject ma) || ma["__classGuid"] == null) continue;
                        var overrides = md["InstanceRadiosityTypeOverride"] as JArray;
                        if (isHavok && havok == null) havok = HavokPlacements(partition, InstanceGuid(o), InternalId(o)) ?? new List<HavokTransformReader.Transform>();
                        int n = isHavok ? count : (inline != null ? inline.Count : 0);
                        for (int i = 0; i < n; i++)
                        {
                            Placement p;
                            if (isHavok)
                            {
                                if (myBase + i >= havok.Count) break;
                                var t = havok[myBase + i];
                                p = new Placement { Right = D(t.Right), Up = D(t.Up), Forward = D(t.Forward), Trans = D(t.Translation) };
                            }
                            else p = FromTransform(inline[i] as JObject);
                            if (p == null) continue;
                            if (groupMoved)
                            {
                                double[][] w = Compose(new[] { p.Right, p.Up, p.Forward, p.Trans }, gm);
                                p.Right = w[0]; p.Up = w[1]; p.Forward = w[2]; p.Trans = w[3];
                            }
                            p.Partition = partition; p.Source = "group"; p.MeshPath = (string)ma["__path"];
                            p.MeshGuid = new Guid((string)ma["__classGuid"]); p.Member = m; p.Instance = i; p.GroupGuid = InstanceGuid(o); p.GroupId = InternalId(o);
                            p.Override = overrides != null && i < overrides.Count ? (string)overrides[i] : null;
                            p.Key = LightmapKey.Compute(p.MeshGuid, p.Right, p.Up, p.Forward, p.Trans);
                            list.Add(p);
                        }
                    }
                }
                else if ((type == "ObjectReferenceObjectData" || type == "SpatialPrefabReferenceObjectData") && o["Blueprint"] is JObject bp && bp["__path"] != null)
                {
                    var p0 = FromTransform(o["BlueprintTransform"] as JObject);
                    if (p0 == null) continue;
                    var direct = MeshesOf((string)bp["__path"]);
                    foreach (var mesh in direct)
                    {
                        var p = FromTransform(o["BlueprintTransform"] as JObject);
                        p.Partition = partition; p.Source = "object"; p.MeshPath = mesh.Item1; p.MeshGuid = mesh.Item2;
                        p.Override = (string)o["RadiosityTypeOverride"];
                        p.ObjectGuid = InstanceGuid(o); p.ObjectId = InternalId(o);
                        p.Key = LightmapKey.Compute(p.MeshGuid, p.Right, p.Up, p.Forward, p.Trans);
                        list.Add(p);
                    }
                    if (direct.Count > 0) continue;
                    // a prefab: the meshes its own references place, each at child transform x placement
                    double[][] outer = { p0.Right, p0.Up, p0.Forward, p0.Trans };
                    foreach (var inner in PrefabMeshes((string)bp["__path"], 0))
                    {
                        double[][] w = Compose(inner.Item3, outer);
                        var p = new Placement { Right = w[0], Up = w[1], Forward = w[2], Trans = w[3] };
                        p.Partition = partition; p.Source = "prefab"; p.MeshPath = inner.Item1; p.MeshGuid = inner.Item2;
                        p.Override = (string)o["RadiosityTypeOverride"];
                        p.InnerOverride = inner.Item4;
                        p.ObjectGuid = InstanceGuid(o);
                        p.Key = LightmapKey.Compute(p.MeshGuid, p.Right, p.Up, p.Forward, p.Trans);
                        list.Add(p);
                    }
                }
            }
            return list;
        }

        static readonly Dictionary<string, List<Tuple<string, Guid, double[][], string>>> PrefabCache =
            new Dictionary<string, List<Tuple<string, Guid, double[][], string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The meshes a prefab places through its own object / spatial-prefab references, recursively: (mesh path, mesh
        /// instance guid, {right, up, forward, trans} in the prefab's space, the placing reference's RadiosityTypeOverride).
        /// </summary>
        public static List<Tuple<string, Guid, double[][], string>> PrefabMeshes(string blueprint, int depth)
        {
            List<Tuple<string, Guid, double[][], string>> r;
            if (PrefabCache.TryGetValue(blueprint, out r)) return r;
            r = new List<Tuple<string, Guid, double[][], string>>();
            PrefabCache[blueprint] = r;                       // guards a cycle too
            if (depth > 6) return r;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(blueprint);
            if (e == null) return r;
            JObject d;
            try { d = EbxDumper.Dump(App.AssetManager.GetEbx(e), resolve: true, deep: true); }
            catch { return r; }
            if (!(d["Objects"] is JArray objs)) return r;
            foreach (JToken tok in objs)
            {
                var o = tok as JObject;
                if (o == null) continue;
                string type = (string)o["__type"];
                if ((type != "ObjectReferenceObjectData" && type != "SpatialPrefabReferenceObjectData") || !(o["Blueprint"] is JObject bp) || bp["__path"] == null) continue;
                var t = FromTransform(o["BlueprintTransform"] as JObject);
                if (t == null) continue;
                double[][] local = { t.Right, t.Up, t.Forward, t.Trans };
                string child = (string)bp["__path"], ov = (string)o["RadiosityTypeOverride"];
                var direct = MeshesOf(child);
                foreach (var m in direct) r.Add(Tuple.Create(m.Item1, m.Item2, local, ov));
                if (direct.Count > 0) continue;
                foreach (var inner in PrefabMeshes(child, depth + 1)) r.Add(Tuple.Create(inner.Item1, inner.Item2, Compose(inner.Item3, local), inner.Item4 ?? ov));
            }
            return r;
        }

        /// <summary>Row-vector transforms: child in parent space x parent = child in the grandparent's space.</summary>
        public static double[][] Compose(double[][] c, double[][] p)
        {
            Func<double[], double[]> mul = v => new[]
            {
                v[0] * p[0][0] + v[1] * p[1][0] + v[2] * p[2][0],
                v[0] * p[0][1] + v[1] * p[1][1] + v[2] * p[2][1],
                v[0] * p[0][2] + v[1] * p[1][2] + v[2] * p[2][2]
            };
            double[] t = mul(c[3]);
            return new[] { mul(c[0]), mul(c[1]), mul(c[2]), new[] { t[0] + p[3][0], t[1] + p[3][1], t[2] + p[3][2] } };
        }

        /// <summary>The meshes of an object blueprint's StaticModelEntityData (its root object, no local transform).</summary>
        public static List<Tuple<string, Guid>> MeshesOf(string blueprint)
        {
            List<Tuple<string, Guid>> r;
            if (BlueprintMeshes.TryGetValue(blueprint, out r)) return r;
            r = new List<Tuple<string, Guid>>();
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(blueprint);
            if (e != null)
            {
                try
                {
                    JObject d = EbxDumper.Dump(App.AssetManager.GetEbx(e), resolve: true, deep: true);
                    foreach (JObject o in Objects(d))
                        if ((string)o["__type"] == "StaticModelEntityData" && o["Mesh"] is JObject m && m["__classGuid"] != null && m["__path"] != null)
                            r.Add(Tuple.Create((string)m["__path"], new Guid((string)m["__classGuid"])));
                }
                catch { }
            }
            BlueprintMeshes[blueprint] = r;
            return r;
        }

        static double[] D(float[] v) => v == null ? null : new double[] { v[0], v[1], v[2] };

        static bool IsIdentity(Placement t)
        {
            double[][] rows = { t.Right, t.Up, t.Forward };
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(rows[r][c] - (r == c ? 1 : 0)) > 1e-6) return false;
            return Math.Abs(t.Trans[0]) < 1e-6 && Math.Abs(t.Trans[1]) < 1e-6 && Math.Abs(t.Trans[2]) < 1e-6;
        }

        static readonly Dictionary<string, List<HavokTransformReader.Transform>> HavokCache =
            new Dictionary<string, List<HavokTransformReader.Transform>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The Havok placement array of a static group (by exported guid, else InternalId, in the partition).</summary>
        internal static List<HavokTransformReader.Transform> HavokPlacements(string partition, Guid groupGuid, int groupId)
        {
            string key = partition + "|" + groupGuid + "|" + groupId;
            List<HavokTransformReader.Transform> r;
            if (HavokCache.TryGetValue(key, out r)) return r;
            r = null;
            try
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(partition);
                if (e != null)
                {
                    object group = null;
                    foreach (object o in App.AssetManager.GetEbx(e).Objects)
                    {
                        if (o == null || o.GetType().Name != "StaticModelGroupEntityData") continue;
                        dynamic ig = ((dynamic)o).GetInstanceGuid();
                        Guid g = ig.ExportedGuid; int id = (int)ig.InternalId;
                        if ((groupGuid != Guid.Empty && g == groupGuid) || (groupGuid == Guid.Empty && id == groupId)) { group = o; break; }
                    }
                    string res = group != null ? LevelCommand.FindPhysicsResourcePath(group) : null;
                    List<List<HavokTransformReader.Transform>> arrays; string err;
                    if (res != null && LevelCommand.TryLoadPhysicsArrays(res, out arrays, out err))
                        r = HavokTransformReader.SelectPlacements(arrays, LevelCommand.CountHavokPlacements(LevelCommand.ReadList(group, "MemberDatas")));
                }
            }
            catch { r = null; }
            HavokCache[key] = r;
            return r;
        }

        static Placement FromTransform(JObject t)
        {
            if (t == null) return null;
            Func<string, double[]> v = k => t[k] is JObject j ? new[] { (double)j["x"], (double)j["y"], (double)j["z"] } : null;
            var p = new Placement { Right = v("right"), Up = v("up"), Forward = v("forward"), Trans = v("trans") };
            return p.Right == null || p.Up == null || p.Forward == null || p.Trans == null ? null : p;
        }

        internal static int InternalId(JObject o)
        {
            if (o["__InstanceGuid"] is JObject ig && ig["InternalId"] != null) return (int)ig["InternalId"];
            return -1;
        }

        internal static Guid InstanceGuid(JObject o)
        {
            if (o["__InstanceGuid"] is JObject ig && ig["ExportedGuid"] != null) return new Guid((string)ig["ExportedGuid"]);
            return Guid.Empty;
        }

        static EnlightenDatabase LoadEnd(string dynamicAsset)
        {
            EbxAssetEntry dyn = App.AssetManager.GetEbxEntry(dynamicAsset);
            if (dyn == null) return null;
            object root = App.AssetManager.GetEbx(dyn).RootObject;
            object v = root.GetType().GetProperty("DatabaseResource")?.GetValue(root);
            if (v == null) return null;
            ulong rid; try { rid = (ulong)(dynamic)v; } catch { return null; }
            ResAssetEntry res = App.AssetManager.GetResEntry(rid);
            if (res == null) return null;
            using (Stream s = App.AssetManager.GetRes(res))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                var db = EnlightenDatabase.Parse(ms.ToArray(), (res.ModifiedEntry?.ResMeta ?? res.ResMeta));
                return db.Unmodelled != null ? null : db;
            }
        }

        public static IEnumerable<JObject> Objects(JToken t)
        {
            if (t is JObject o) { yield return o; foreach (var p in o.Properties()) foreach (var c in Objects(p.Value)) yield return c; }
            else if (t is JArray a) foreach (var v in a) foreach (var c in Objects(v)) yield return c;
        }
    }
}
