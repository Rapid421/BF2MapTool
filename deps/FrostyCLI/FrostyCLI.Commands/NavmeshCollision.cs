using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // `navmesh geometry --collision`: the level's walkable world read from its COLLISION, not its render meshes.
    //
    // Render meshes stand in for collision only where the two agree. They do not on ported levels: a static group
    // transplanted whole keeps 2015's converted physics while its members draw BFII meshes (or a stand-in box);
    // character-only slabs and OBB / sphere blockers have no render mesh at all; hangar shells drawn by one mesh can
    // be walled and floored by another group's compound; Kamino's corridors carry render triangles with no collision
    // at body height. What a soldier -- and so a bot -- meets is the server's physics world, so this reads exactly that:
    //   * every StaticModelGroupEntityData's physics bodies: the group HavokPhysicsData resource, root slot
    //     CollisionRootShapeIndex of each server body (slot 0 for the group body, slot 2 for a character-only body);
    //   * every placed blueprint's StaticModelPhysicsComponentData bodies: the HavokAsset's resource, same rule,
    //     through prefabs recursively (objects a prefab's PropertyConnection moves by Transform -- door leaves -- are
    //     left out, as DICE's navmeshes run through closed doors);
    //   * OBBCollisionEntityData / SphereCollisionEntityData blockers;
    // kept when the body is on the server (Realm Server / ClientAndServer, physics world 0, not a proxy), does not move
    // (not dynamic / keyframed) and sits on a collision layer characters collide with. Shapes are decoded by
    // HavokPhysicsResource.AppendSlotTriangles (compounds, convex polytopes, spheres, capsules, compressed meshes).
    public static class NavmeshCollisionLayers
    {
        /// <summary>Does a body on this RigidBodyCollisionLayer stop a soldier?</summary>
        public static bool Blocks(string layer) { return layer != null && NavmeshCollision.CharacterLayers.Contains(layer); }
    }

    internal sealed class NavmeshCollision
    {
        // RigidBodyCollisionLayer values a soldier collides with. Left out: vehicle-only, debris, ragdoll, water,
        // camera, no-collision, starfighter / vehicle bodies, and the "no character collision" dynamic layer.
        internal static readonly HashSet<string> CharacterLayers = new HashSet<string>(StringComparer.Ordinal)
        {
            "RigidBodyCollisionLayer_StaticLayer",
            "RigidBodyCollisionLayer_CharacterCollisionGeometryLayer",
            "RigidBodyCollisionLayer_TerrainLayer",
            "RigidBodyCollisionLayer_TerrainAndStaticCollisionLayer",
            "RigidBodyCollisionLayer_VehicleAndCharacterCollisionLayer",
            "RigidBodyCollisionLayer_PlayerCollisionLayer",
            "RigidBodyCollisionLayer_AICollisionLayer",
            "RigidBodyCollisionLayer_AiCollisionBodyLayer",
            "RigidBodyCollisionLayer_NoVehicleCollisionLayer",
        };

        public readonly List<float> Verts = new List<float>();
        public readonly List<int> Tris = new List<int>();
        public readonly List<KeyValuePair<int, string>> Labels = new List<KeyValuePair<int, string>>();   // (first triangle, label)
        public readonly HavokPhysicsResource.ShapeCensus Census = new HavokPhysicsResource.ShapeCensus();
        public readonly SortedDictionary<string, int> SkippedBodies = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Warnings = new List<string>();
        public int GroupBodies, EntityBodies, Volumes, Placements, MovedLeftOut, NoPhysics;
        // blueprint name substrings left out (--skip): door panels a prefab moves in a way MovedObjects does not see
        public string[] Skip = new string[0];
        public int SkippedByName;

        private readonly Dictionary<string, HavokPhysicsResource> _res = new Dictionary<string, HavokPhysicsResource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _badRes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _blueprints = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public void CollectLevel(string level)
        {
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(level);
            if (e == null) throw new ArgumentException("level not found: " + level);
            _visited.Add(e.Name);
            Visit(App.AssetManager.GetEbx(e).RootObject, HavokPhysicsResource.IdentityTransform(), e.Name);
        }

        private void Visit(object root, double[] world, string where)
        {
            IList objects = LevelCommand.ReadList(root, "Objects");
            if (objects == null) return;
            foreach (object raw in objects)
            {
                object o = LevelCommand.ResolveInternal(raw);
                if (o == null) continue;
                string t = o.GetType().Name;
                if (t == "StaticModelGroupEntityData")
                {
                    Group(o, Compose(Transform(Get(o, "Transform")), world), where);
                }
                else if (t == "SubWorldReferenceObjectData" || t == "LayerReferenceObjectData" || t == "WorldPartReferenceObjectData")
                {
                    double[] w = Compose(Transform(Get(o, "BlueprintTransform")), world);
                    object part = t != "SubWorldReferenceObjectData" ? LevelCommand.ResolveInternal(Get(o, "Blueprint")) : null;
                    if (part != null && !(Get(o, "Blueprint") is PointerRef pr0 && pr0.Type == PointerRefType.External))
                    {
                        Visit(part, w, where);
                        continue;
                    }
                    string child = t == "SubWorldReferenceObjectData"
                        ? Convert.ToString(Get(o, "BundleName"), CultureInfo.InvariantCulture)
                        : LevelCommand.ResolveBlueprintPath(Get(o, "Blueprint"));
                    if (string.IsNullOrEmpty(child)) continue;
                    EbxAssetEntry ce = App.AssetManager.GetEbxEntry(child);
                    if (ce == null || !_visited.Add(ce.Name)) continue;
                    try { Visit(App.AssetManager.GetEbx(ce).RootObject, w, ce.Name); }
                    catch (Exception ex) { Warnings.Add("could not read " + child + ": " + LevelCommand.Describe(ex)); }
                }
                else Placed(o, world, 0);
            }
        }

        // Anything placed in a partition or a prefab: a blueprint reference, a static model, a collision volume.
        private void Placed(object o, double[] world, int depth)
        {
            string t = o.GetType().Name;
            if (t == "OBBCollisionEntityData" || t == "SphereCollisionEntityData") { Volume(o, world); return; }
            object bp = Get(o, "Blueprint");
            if (bp is PointerRef pr && pr.Type == PointerRefType.External)
            {
                string name = LevelCommand.ResolveBlueprintPath(bp);
                if (name == null) return;
                Placements++;
                Blueprint(name, Compose(Transform(Get(o, "BlueprintTransform")), world), depth + 1);
                return;
            }
            if (Get(o, "Components") is IList) Entity(o, Compose(Transform(Get(o, "Transform")), world), t);
        }

        private void Blueprint(string name, double[] world, int depth)
        {
            if (depth > 8) return;
            foreach (string sk in Skip)
                if (name != null && name.IndexOf(sk, StringComparison.OrdinalIgnoreCase) >= 0) { SkippedByName++; return; }
            object root;
            if (!_blueprints.TryGetValue(name, out root))
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(name);
                try { root = e != null ? App.AssetManager.GetEbx(e).RootObject : null; }
                catch (Exception ex) { Warnings.Add("could not read blueprint " + name + ": " + LevelCommand.Describe(ex)); root = null; }
                _blueprints[name] = root;
            }
            if (root == null) return;
            object obj = LevelCommand.ResolveInternal(Get(root, "Object"));
            if (obj != null) { Entity(obj, world, name); return; }
            IList objects = LevelCommand.ReadList(root, "Objects");
            if (objects == null) return;
            HashSet<object> moved = NavmeshCommand.MovedObjects(root);
            foreach (object raw in objects)
            {
                object o = LevelCommand.ResolveInternal(raw);
                if (o == null) continue;
                if (moved.Contains(o)) { MovedLeftOut++; continue; }
                Placed(o, world, depth);
            }
        }

        // A static model (or any entity with components): the server bodies of its physics components.
        private void Entity(object entity, double[] world, string label)
        {
            if (!(Get(entity, "Components") is IList comps)) return;
            bool any = false;
            foreach (object raw in comps)
            {
                object c = LevelCommand.ResolveInternal(raw);
                if (!(Get(c, "PhysicsBodies") is IList bodies)) continue;
                double[] cw = Compose(Transform(Get(c, "Transform")), world);
                foreach (object rb in bodies)
                {
                    object body = LevelCommand.ResolveInternal(rb);
                    if (body == null) continue;
                    string res; int slot;
                    if (!Usable(body, out res, out slot)) continue;
                    if (Add(res, slot, Compose(Transform(Get(body, "Transform")), cw), "entity " + ShortName(label)))
                    { EntityBodies++; any = true; }
                }
            }
            if (!any) NoPhysics++;
        }

        private void Group(object group, double[] world, string where)
        {
            if (!(Get(group, "Components") is IList comps)) return;
            foreach (object raw in comps)
            {
                object c = LevelCommand.ResolveInternal(raw);
                if (c == null || c.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                if (!(Get(c, "PhysicsBodies") is IList bodies)) continue;
                foreach (object rb in bodies)
                {
                    object body = LevelCommand.ResolveInternal(rb);
                    if (body == null) continue;
                    string res; int slot;
                    if (!Usable(body, out res, out slot)) continue;
                    if (Add(res, slot, Compose(Transform(Get(body, "Transform")), world), "group " + ShortName(res) + " slot " + slot)) GroupBodies++;
                }
            }
        }

        // Is this body part of what a soldier collides with on the server? Out: its physics resource and root slot.
        private bool Usable(object body, out string res, out int slot)
        {
            res = null; slot = 0;
            string realm = Convert.ToString(Get(body, "Realm"), CultureInfo.InvariantCulture) ?? "";
            string type = Convert.ToString(Get(body, "RigidBodyType"), CultureInfo.InvariantCulture) ?? "";
            string layer = Convert.ToString(Get(body, "CollisionLayer"), CultureInfo.InvariantCulture) ?? "";
            string motion = Convert.ToString(Get(body, "MotionType"), CultureInfo.InvariantCulture) ?? "";
            int worldIndex = ToInt(Get(body, "WorldIndex"));
            string skip = null;
            if (realm == "Realm_Client") skip = "client-only body";
            else if (worldIndex != 0) skip = "physics world " + worldIndex;
            else if (type == "RBTypeProxy") skip = "proxy body";
            else if (motion.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0 || motion.IndexOf("Keyframed", StringComparison.OrdinalIgnoreCase) >= 0) skip = "moving body (" + motion + ")";
            else if (!CharacterLayers.Contains(layer)) skip = "layer " + layer.Replace("RigidBodyCollisionLayer_", "");
            if (skip != null) { Count(skip); return false; }
            object asset = Get(body, "Asset");
            object havok = LevelCommand.ResolveInternal(asset);
            if (havok == null && asset is PointerRef pr && pr.Type == PointerRefType.External)
            {
                EbxAssetEntry he = App.AssetManager.GetEbxEntry(pr.External.FileGuid);
                try { havok = he != null ? App.AssetManager.GetEbx(he).RootObject : null; } catch { havok = null; }
            }
            res = ResName(Get(havok, "Resource"));
            if (res == null) { Count("no physics resource"); return false; }
            slot = ToInt(Get(body, "CollisionRootShapeIndex"));
            return true;
        }

        private static string ResName(object resourceRef)
        {
            if (resourceRef == null) return null;
            ulong rid;
            try { rid = (ulong)(dynamic)resourceRef; } catch { return null; }
            if (rid == 0) return null;
            ResAssetEntry re = App.AssetManager.GetResEntry(rid);
            return re != null ? re.Name : null;
        }

        private bool Add(string res, int slot, double[] world, string label)
        {
            HavokPhysicsResource r;
            if (!_res.TryGetValue(res, out r))
            {
                if (_badRes.Contains(res)) return false;
                byte[] bytes; string err;
                ResAssetEntry re = App.AssetManager.GetResEntry(res);
                if (re == null || !LevelCommand.TryReadResBytes(re, out bytes, out err)) { _badRes.Add(res); Warnings.Add("cannot read physics resource " + res); return false; }
                try { r = HavokPhysicsResource.Parse(bytes); }
                catch (Exception ex) { _badRes.Add(res); Warnings.Add("cannot parse " + res + ": " + ex.Message); return false; }
                _res[res] = r;
            }
            Labels.Add(new KeyValuePair<int, string>(Tris.Count / 3, label));
            if (!r.AppendSlotTriangles(slot, world, Verts, Tris, Census))
            {
                Labels.RemoveAt(Labels.Count - 1);
                Warnings.Add(res + " has no root slot " + slot);
                return false;
            }
            return true;
        }

        private void Volume(object v, double[] world)
        {
            if (Get(v, "Enabled") is bool en && !en) { Count("disabled volume"); return; }
            string layer = Convert.ToString(Get(v, "CollisionLayer"), CultureInfo.InvariantCulture) ?? "";
            if (Get(v, "PhysicsBodies") is IList bodies && bodies.Count > 0)
            {
                object b = LevelCommand.ResolveInternal(bodies[0]);
                string bl = Convert.ToString(Get(b, "CollisionLayer"), CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(bl)) layer = bl;
            }
            if (!CharacterLayers.Contains(layer)) { Count("volume on layer " + layer.Replace("RigidBodyCollisionLayer_", "")); return; }
            double[] w = Compose(Transform(Get(v, "Transform")), world);
            var local = new List<double[]>();
            if (v.GetType().Name == "OBBCollisionEntityData")
            {
                object he = Get(v, "HalfExtents");
                double hx = ToDouble(Get(he, "x")), hy = ToDouble(Get(he, "y")), hz = ToDouble(Get(he, "z"));
                for (int c = 0; c < 8; c++) local.Add(new[] { (c & 1) != 0 ? hx : -hx, (c & 2) != 0 ? hy : -hy, (c & 4) != 0 ? hz : -hz });
            }
            else
            {
                double r = ToDouble(Get(v, "Radius"));
                // shipped spheres carry Position == Transform.trans; a clone that kept its template's Position is
                // reported (the runtime may read either)
                if (Get(v, "Position") is object p && Get(v, "Transform") is object tr)
                {
                    double[] t = Transform(tr);
                    if (Math.Abs(ToDouble(Get(p, "x")) - t[9]) + Math.Abs(ToDouble(Get(p, "y")) - t[10]) + Math.Abs(ToDouble(Get(p, "z")) - t[11]) > 0.01)
                        Warnings.Add(string.Format(CultureInfo.InvariantCulture,
                            "sphere blocker at ({0:0.##}, {1:0.##}, {2:0.##}) carries Position ({3:0.##}, {4:0.##}, {5:0.##}); shipped spheres keep the two equal",
                            t[9], t[10], t[11], ToDouble(Get(p, "x")), ToDouble(Get(p, "y")), ToDouble(Get(p, "z"))));
                }
                local = HavokPhysicsResource.SpherePoints(new double[] { 0, 0, 0 }, r);
            }
            var worldPts = local.Select(p => HavokPhysicsResource.Apply(w, p)).ToList();
            List<int[]> hull = ConvexHull3.Build(worldPts);
            if (hull == null) { Count("degenerate volume"); return; }
            Labels.Add(new KeyValuePair<int, string>(Tris.Count / 3, "volume " + v.GetType().Name.Replace("CollisionEntityData", "")));
            int b0 = Verts.Count / 3;
            foreach (double[] p in worldPts) { Verts.Add((float)p[0]); Verts.Add((float)p[1]); Verts.Add((float)p[2]); }
            foreach (int[] tri in hull) { Tris.Add(b0 + tri[0]); Tris.Add(b0 + tri[1]); Tris.Add(b0 + tri[2]); Census.Triangles++; }
            Volumes++;
        }

        private void Count(string why)
        {
            int n;
            SkippedBodies.TryGetValue(why, out n);
            SkippedBodies[why] = n + 1;
        }

        private static string ShortName(string path)
        {
            if (path == null) return "?";
            int s = path.LastIndexOf('/');
            return s >= 0 ? path.Substring(s + 1) : path;
        }

        private static object Get(object o, string name) { return LevelCommand.ReadMember(o, name); }

        private static int ToInt(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static double ToDouble(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        // LinearTransform -> 12 numbers (right, up, forward, trans); identity when absent
        internal static double[] Transform(object lt)
        {
            if (lt == null) return HavokPhysicsResource.IdentityTransform();
            try
            {
                var o = new double[12];
                string[] rows = { "right", "up", "forward", "trans" };
                for (int r = 0; r < 4; r++)
                {
                    object v = Get(lt, rows[r]);
                    if (v == null) return HavokPhysicsResource.IdentityTransform();
                    o[r * 3] = ToDouble(Get(v, "x")); o[r * 3 + 1] = ToDouble(Get(v, "y")); o[r * 3 + 2] = ToDouble(Get(v, "z"));
                }
                // an all-zero rotation (an unset transform) is identity
                if (o[0] == 0 && o[1] == 0 && o[2] == 0 && o[3] == 0 && o[4] == 0 && o[5] == 0) { o[0] = 1; o[4] = 1; o[8] = 1; }
                return o;
            }
            catch { return HavokPhysicsResource.IdentityTransform(); }
        }

        private static double[] Compose(double[] a, double[] b) { return HavokPhysicsResource.Compose(a, b); }
    }
}
