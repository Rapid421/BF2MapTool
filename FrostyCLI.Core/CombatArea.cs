using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyLevelTools
{
    // A level's play-area boundary: the shapes its game modes use as their combat area.
    //
    // Nothing named "combat area" is placed in a Battlefront II level. Each mode's logic prefab carries the rule -- a
    // CombatAreaEntityData (no shape field of its own) and a CombatAreaManagerEntityData (Skirmish,
    // Gameplay/GameModes/Skirmish/PF_Skirmish_Logic: DamageDelay 10 s, Damage 900000, the out-of-combat death screen,
    // IgnoreHeight false) -- and the level hands the prefab its shape through an interface link: a LinkConnection whose
    // SourceFieldId is hash("CombatArea") (or CombatAreaOnline / CombatAreaSoldier) and whose Target is a
    // VolumeVectorShapeData (or its subclass SpawnLocationFinderShapeData). Tatooine, shipped: TeamDeathmatch_Skirmish
    // links PF_TeamDeathmatch_Skirmish.CombatArea to a 34-point polygon in TeamDeathmatch_Skirmish_Logic (y 57.81,
    // Height 40); TeamDeathmatch links its Online sub-world's CombatArea to a 127-point polygon in TeamDeathmatch_Shapes.
    //
    // The inside test (engine, AreaTestUtilBase::isInsideArea): the point-in-polygon test on x/z, and for a volume whose
    // Height > 0 also shapeCenter.y < y <= shapeCenter.y + Height. Height 0 means no vertical limit.
    public static class CombatArea
    {
        public static readonly string[] LinkFields = { "CombatArea", "CombatAreaOnline", "CombatAreaSoldier" };

        public sealed class Link
        {
            public EbxAssetEntry Partition;   // the partition whose LinkConnections hold the link
            public string Field;              // the interface field the shape is linked to
            public string Source;             // what receives the shape (its type and blueprint)
            public EbxAssetEntry ShapeAsset;  // the partition that holds the shape
            public EbxAsset ShapeAssetData;   // the loaded partition the shape lives in: modify and save this instance
            public object Shape;              // VolumeVectorShapeData / SpawnLocationFinderShapeData
            public Guid ShapeGuid;
        }

        // every combat-area link in the partitions of <level> (its directory), one per distinct shape and field
        public static List<Link> Find(AssetManager am, string level, IEnumerable<string> fields = null)
        {
            string dir = Dir(level);
            // a field is a port name, or a raw port id ("#-1609661850" / "-1609661850") for the unnamed ports some mode prefabs
            // take their boundaries through (pf_mode9's out-of-bounds stages, pf_strike_bombs, pf_gamemode_conquest_staged)
            var ids = new Dictionary<int, string>();
            foreach (string f in (fields ?? LinkFields).Distinct())
                ids[PortId(f)] = f;
            var result = new List<Link>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // one loaded instance per partition: GetEbx parses an unmodified shipped asset afresh on every call, so an
            // edit has to be made on, and saved from, the same instance
            var loaded = new Dictionary<Guid, EbxAsset>();
            Func<EbxAssetEntry, EbxAsset> load = x =>
            {
                if (!loaded.TryGetValue(x.Guid, out EbxAsset v)) { try { v = am.GetEbx(x); } catch { v = null; } loaded[x.Guid] = v; }
                return v;
            };
            foreach (EbxAssetEntry e in am.EnumerateEbx().Where(x => x.Name.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                EbxAsset a = load(e);
                if (a == null || !(Get(a.RootObject, "LinkConnections") is IList conns)) continue;
                foreach (object c in conns)
                {
                    int id;
                    try { id = Convert.ToInt32(Get(c, "SourceFieldId"), CultureInfo.InvariantCulture); } catch { continue; }
                    if (!ids.TryGetValue(id, out string field)) continue;
                    object src = Resolve(am, e, a, load, Get(c, "Source"), out _, out _);
                    object dst = Resolve(am, e, a, load, Get(c, "Target"), out EbxAssetEntry dstEntry, out EbxAsset dstAsset);
                    if (dst == null || !(Get(dst, "Points") is IList)) continue;
                    Guid g = InstanceGuid(dst);
                    if (!seen.Add(dstEntry.Name + "/" + g + "/" + field)) continue;
                    result.Add(new Link
                    {
                        Partition = e, Field = field, Source = Describe(am, src), ShapeAsset = dstEntry, ShapeAssetData = dstAsset,
                        Shape = dst, ShapeGuid = g,
                    });
                }
            }
            return result;
        }

        public static int PortId(string field)
        {
            string t = (field ?? "").Trim();
            return int.TryParse(t.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : FrostySdk.Utils.HashString(t);
        }

        // the shape's points in world space (Points are local to the shape's Transform)
        public static List<double[]> WorldPoints(object shape)
        {
            var list = new List<double[]>();
            if (!(Get(shape, "Points") is IList pts)) return list;
            double[][] m = Basis(Get(shape, "Transform"));
            foreach (object p in pts)
            {
                double[] v = Vec(p);
                if (v != null) list.Add(ToWorld(v, m));
            }
            return list;
        }

        public static double Height(object shape)
        {
            object h = Get(shape, "Height");
            return h == null ? 0 : Convert.ToDouble(h, CultureInfo.InvariantCulture);
        }

        // Replace the shape's outline with <polygon> (world x, z) standing at world height <floor>; <height> (> 0) caps it,
        // 0 lifts the vertical limit, null keeps the shape's own. The points stay local to the shape's Transform.
        public static void Rewrite(object shape, IList<double[]> polygon, double floor, double? height)
        {
            if (polygon == null || polygon.Count < 3) throw new ArgumentException("a combat area needs at least three points");
            if (!(Get(shape, "Points") is IList pts)) throw new InvalidOperationException("not a vector shape: " + shape.GetType().Name);
            Type vt = pts.Count > 0 ? pts[0].GetType() : pts.GetType().GetGenericArguments().FirstOrDefault();
            if (vt == null) throw new InvalidOperationException("cannot tell the point type of " + shape.GetType().Name);
            double[][] m = Basis(Get(shape, "Transform"));
            pts.Clear();
            foreach (double[] p in polygon)
            {
                double[] local = ToLocal(new[] { p[0], floor, p[1] }, m);
                object v = Activator.CreateInstance(vt);
                Set(ref v, "x", (float)local[0]); Set(ref v, "y", (float)local[1]); Set(ref v, "z", (float)local[2]);
                pts.Add(v);
            }
            var closed = shape.GetType().GetProperty("IsClosed");
            if (closed != null && closed.CanWrite) closed.SetValue(shape, true);
            if (height.HasValue)
            {
                var hp = shape.GetType().GetProperty("Height");
                if (hp == null) throw new InvalidOperationException(shape.GetType().Name + " has no Height");
                hp.SetValue(shape, Convert.ChangeType(height.Value, hp.PropertyType, CultureInfo.InvariantCulture));
            }
        }

        // ---- pure geometry (unit-tested) ----------------------------------------------------------------------------

        // "rect:x0,z0,x1,z1" or "x,z;x,z;x,z[;...]" -> world (x, z) outline, counter-clockwise seen from above
        public static List<double[]> ParsePolygon(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("empty combat area");
            spec = spec.Trim();
            var inv = CultureInfo.InvariantCulture;
            List<double[]> poly;
            if (spec.StartsWith("rect:", StringComparison.OrdinalIgnoreCase))
            {
                double[] v = spec.Substring(5).Split(',').Select(s => double.Parse(s.Trim(), inv)).ToArray();
                if (v.Length != 4) throw new ArgumentException("rect: takes x0,z0,x1,z1");
                double x0 = Math.Min(v[0], v[2]), x1 = Math.Max(v[0], v[2]), z0 = Math.Min(v[1], v[3]), z1 = Math.Max(v[1], v[3]);
                if (x1 - x0 <= 0 || z1 - z0 <= 0) throw new ArgumentException("rect: has no area");
                poly = new List<double[]> { new[] { x0, z0 }, new[] { x1, z0 }, new[] { x1, z1 }, new[] { x0, z1 } };
            }
            else
            {
                poly = spec.Split(';').Where(s => s.Trim().Length > 0).Select(s =>
                {
                    double[] v = s.Split(',').Select(t => double.Parse(t.Trim(), inv)).ToArray();
                    if (v.Length != 2) throw new ArgumentException("combat area point '" + s + "' is not x,z");
                    return v;
                }).ToList();
                if (poly.Count < 3) throw new ArgumentException("a combat area needs at least three points");
            }
            if (SignedArea(poly) < 0) poly.Reverse();
            if (Math.Abs(SignedArea(poly)) < 1e-6) throw new ArgumentException("the combat area has no area");
            return poly;
        }

        // shoelace area on (x, z); positive = counter-clockwise with x right and z up
        public static double SignedArea(IList<double[]> poly)
        {
            double s = 0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) s += poly[j][0] * poly[i][1] - poly[i][0] * poly[j][1];
            return s / 2;
        }

        // even-odd crossing test on x/z, the engine's rule
        public static bool Contains(IList<double[]> poly, double x, double z)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i][0], zi = poly[i][1], xj = poly[j][0], zj = poly[j][1];
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }
            return inside;
        }

        // inside the volume: in the outline and, when height > 0, floor < y <= floor + height
        public static bool Inside(IList<double[]> poly, double floor, double height, double x, double y, double z)
        {
            if (height > 0 && (y > floor + height || y <= floor)) return false;
            return Contains(poly, x, z);
        }

        // distance from (x, z) to the outline's nearest edge
        public static double EdgeDistance(IList<double[]> poly, double x, double z)
        {
            double best = double.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double ax = poly[j][0], az = poly[j][1], bx = poly[i][0], bz = poly[i][1];
                double dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
                double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (z - az) * dz) / l2));
                double px = ax + t * dx - x, pz = az + t * dz - z;
                best = Math.Min(best, Math.Sqrt(px * px + pz * pz));
            }
            return best;
        }

        // [x0, x1, z0, z1]
        public static double[] Bounds(IEnumerable<double[]> poly)
        {
            var p = poly.ToList();
            return new[] { p.Min(v => v[0]), p.Max(v => v[0]), p.Min(v => v[1]), p.Max(v => v[1]) };
        }

        // row-vector LinearTransform: world = x*right + y*up + z*forward + trans
        public static double[] ToWorld(double[] local, double[][] m)
        {
            var w = new double[3];
            for (int k = 0; k < 3; k++) w[k] = local[0] * m[0][k] + local[1] * m[1][k] + local[2] * m[2][k] + m[3][k];
            return w;
        }

        public static double[] ToLocal(double[] world, double[][] m)
        {
            double[] d = { world[0] - m[3][0], world[1] - m[3][1], world[2] - m[3][2] };
            var l = new double[3];
            for (int r = 0; r < 3; r++)
            {
                double n2 = m[r][0] * m[r][0] + m[r][1] * m[r][1] + m[r][2] * m[r][2];
                l[r] = n2 <= 0 ? 0 : (d[0] * m[r][0] + d[1] * m[r][1] + d[2] * m[r][2]) / n2;
            }
            return l;
        }

        // ---- helpers ----------------------------------------------------------------------------------------------

        // rows right, up, forward, trans of a LinearTransform (identity when absent)
        public static double[][] Basis(object transform)
        {
            double[][] id = { new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 }, new double[] { 0, 0, 0 } };
            if (transform == null) return id;
            double[] r = Vec(Get(transform, "right")), u = Vec(Get(transform, "up")), f = Vec(Get(transform, "forward")), t = Vec(Get(transform, "trans"));
            if (r == null || u == null || f == null || t == null) return id;
            if (r.All(v => v == 0) && u.All(v => v == 0) && f.All(v => v == 0)) return new[] { id[0], id[1], id[2], t };
            return new[] { r, u, f, t };
        }

        private static double[] Vec(object v)
        {
            if (v == null) return null;
            object x = Get(v, "x"), y = Get(v, "y"), z = Get(v, "z");
            if (x == null || y == null || z == null) return null;
            return new[] { Convert.ToDouble(x, CultureInfo.InvariantCulture), Convert.ToDouble(y, CultureInfo.InvariantCulture), Convert.ToDouble(z, CultureInfo.InvariantCulture) };
        }

        private static void Set(ref object boxed, string name, float value)
        {
            var p = boxed.GetType().GetProperty(name);
            if (p != null) { p.SetValue(boxed, value); return; }
            var f = boxed.GetType().GetField(name);
            if (f != null) f.SetValue(boxed, value);
        }

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name);
            if (p != null) return p.GetValue(o);
            var f = o.GetType().GetField(name);
            return f?.GetValue(o);
        }

        private static string Dir(string level) { string s = level.Trim().Trim('/'); return s.Substring(0, s.LastIndexOf('/') + 1); }

        private static Guid InstanceGuid(object o)
        {
            try { return (Guid)((dynamic)o).GetInstanceGuid().ExportedGuid; } catch { return Guid.Empty; }
        }

        // the object a PointerRef names -- an internal object of <asset>, or an exported object of another partition --
        // and the partition (entry and loaded instance) it lives in
        private static object Resolve(AssetManager am, EbxAssetEntry entry, EbxAsset asset, Func<EbxAssetEntry, EbxAsset> load,
                                      object pointer, out EbxAssetEntry owner, out EbxAsset ownerAsset)
        {
            owner = null; ownerAsset = null;
            if (!(pointer is PointerRef pr)) return null;
            if (pr.Type == PointerRefType.Internal) { owner = entry; ownerAsset = asset; return pr.Internal; }
            if (pr.Type != PointerRefType.External) return null;
            owner = am.GetEbxEntry(pr.External.FileGuid);
            if (owner == null) return null;
            ownerAsset = load(owner);
            if (ownerAsset == null) return null;
            foreach (object o in ownerAsset.Objects)
                if (InstanceGuid(o) == pr.External.ClassGuid) return o;
            return null;
        }

        private static string Describe(AssetManager am, object src)
        {
            if (src == null) return "?";
            string t = src.GetType().Name;
            if (Get(src, "Blueprint") is PointerRef bp && bp.Type == PointerRefType.External)
                return t + " " + (am.GetEbxEntry(bp.External.FileGuid)?.Name ?? bp.External.FileGuid.ToString());
            object bn = Get(src, "BundleName");
            return bn != null ? t + " " + bn : t;
        }
    }
}
