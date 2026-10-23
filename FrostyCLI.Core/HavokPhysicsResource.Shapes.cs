using System;
using System.Collections.Generic;

namespace FrostyCLI.Core
{
    // Collision geometry of a HavokPhysicsData resource as triangles -- what a character standing on or walking into
    // the resource meets. Read-only: nothing here changes the resource.
    //
    // A root slot of the HavokPhysicsContainer (0 = what you stand on and bump into, 1 = what weapon fire is traced
    // against, 2 = character-only collision) is walked down its shape tree:
    //   hknpStaticCompoundShape   instance records (0x80: rotation columns, translation, scale, shape pointer at 0x50;
    //                             col0.w int24 = hknpShapeInstance flags, IS_ENABLED 0x40), recursively;
    //   hknpConvexPolytopeShape   vertex / plane / face / index hkRelArrays at +0x30 / +0x40 / +0x44 / +0x48 (face =
    //                             {u8 minHalfAngle, u8 numIndices, u16 firstIndex}); faces fanned, wound outward;
    //   hknpConvexShape           vertices only: their convex hull;
    //   hknpSphereShape           vertex 0 + convex radius (+0x14): a tessellated sphere;
    //   hknpCapsuleShape          endpoints at +0x50 / +0x60 + convex radius: the hull of two spheres;
    //   hknpCompressedMeshShape   data pointer at +0x60 -> hknpCompressedMeshShapeData, whose mesh tree (at +0x10)
    //                             holds sections (0x60 each: codec offset/scale at +0x30, first packed vertex +0x48,
    //                             shared-vertex run +0x4C, primitive run +0x50 (base << 8 | count), packed count +0x58),
    //                             primitives (4 vertex indices; a triangle when the last two repeat, else a quad),
    //                             packed vertices (u32: 11/11/10 bits over the section codec) and shared vertices
    //                             (u64: 21/21/22 bits over the tree domain). Mesh collision is two-sided, so every
    //                             triangle is written facing up.
    // The layouts were checked on shipped and converted resources: face index counts add up to the index array on
    // every polytope, and decoded mesh triangles stay inside the section domains with edge lengths of the same order
    // whether or not they use shared vertices (a wrong shared decode gives domain-long slivers).
    // Convex radii of polytopes (0-0.01 m on the Hoth resources) are left out; spheres and capsules are made of them.
    public sealed partial class HavokPhysicsResource
    {
        /// <summary>What a triangle extraction met, for reports.</summary>
        public sealed class ShapeCensus
        {
            public readonly SortedDictionary<string, int> Shapes = new SortedDictionary<string, int>(StringComparer.Ordinal);
            public readonly SortedSet<string> UnsupportedClasses = new SortedSet<string>(StringComparer.Ordinal);
            public int Instances, DisabledInstances, Triangles, Unsupported, Malformed;

            public void Count(string cls)
            {
                int n;
                Shapes.TryGetValue(cls, out n);
                Shapes[cls] = n + 1;
            }

            public void Add(ShapeCensus o)
            {
                foreach (KeyValuePair<string, int> kv in o.Shapes)
                {
                    int n;
                    Shapes.TryGetValue(kv.Key, out n);
                    Shapes[kv.Key] = n + kv.Value;
                }
                foreach (string c in o.UnsupportedClasses) UnsupportedClasses.Add(c);
                Instances += o.Instances; DisabledInstances += o.DisabledInstances; Triangles += o.Triangles;
                Unsupported += o.Unsupported; Malformed += o.Malformed;
            }

            public override string ToString()
            {
                var parts = new List<string>();
                foreach (KeyValuePair<string, int> kv in Shapes) parts.Add(kv.Key.Replace("hknp", "") + " " + kv.Value);
                string s = Triangles + " triangle(s) from " + string.Join(", ", parts.ToArray()) + "; " + Instances + " instance record(s)";
                if (DisabledInstances > 0) s += ", " + DisabledInstances + " disabled";
                if (Unsupported > 0) s += "; " + Unsupported + " unsupported shape(s) (" + string.Join(", ", new List<string>(UnsupportedClasses).ToArray()) + ")";
                if (Malformed > 0) s += "; " + Malformed + " malformed shape(s) skipped";
                return s;
            }
        }

        private Dictionary<int, string> shapeClasses;

        private string ShapeClass(int offset)
        {
            if (shapeClasses == null)
            {
                shapeClasses = new Dictionary<int, string>();
                foreach (KeyValuePair<int, string> obj in pack64.Objects) shapeClasses[obj.Key] = obj.Value;
            }
            string c;
            return shapeClasses.TryGetValue(offset, out c) ? c : "?";
        }

        /// <summary>Root shapes the HavokPhysicsContainer lists: slot 0 collision, 1 raycast, 2 character-only.</summary>
        public int RootSlotCount { get { return ContainerRoots(pack64).Count; } }

        /// <summary>Class name of a root slot's shape, or null when the slot does not exist.</summary>
        public string RootSlotClass(int slot)
        {
            List<int> roots = ContainerRoots(pack64);
            return slot >= 0 && slot < roots.Count ? ShapeClass(roots[slot]) : null;
        }

        /// <summary>
        /// Appends the triangles of one root slot, placed by <paramref name="world"/> (a LinearTransform as 12 numbers:
        /// right, up, forward, trans; p' = p.x right + p.y up + p.z forward + trans), to verts (x, y, z) and tris
        /// (vertex indices into verts / 3). Convex faces wind outward; mesh triangles face up. Returns false when the
        /// slot does not exist.
        /// </summary>
        public bool AppendSlotTriangles(int slot, double[] world, List<float> verts, List<int> tris, ShapeCensus census)
        {
            List<int> roots = ContainerRoots(pack64);
            if (slot < 0 || slot >= roots.Count) return false;
            WalkShape(roots[slot], world ?? IdentityTransform(), 0, verts, tris, census ?? new ShapeCensus());
            return true;
        }

        private void WalkShape(int off, double[] m, int depth, List<float> verts, List<int> tris, ShapeCensus census)
        {
            string cls = ShapeClass(off);
            census.Count(cls);
            if (depth > 16) { census.Malformed++; return; }
            try
            {
                switch (cls)
                {
                    case "hknpStaticCompoundShape": WalkCompound(off, m, depth, verts, tris, census); break;
                    case "hknpConvexPolytopeShape": EmitPolytope(off, m, verts, tris, census); break;
                    case "hknpConvexShape": EmitHull(ConvexVertices(off), m, verts, tris, census); break;
                    case "hknpSphereShape": EmitSphere(off, m, verts, tris, census); break;
                    case "hknpCapsuleShape": EmitCapsule(off, m, verts, tris, census); break;
                    case "hknpCompressedMeshShape": EmitCompressedMesh(off, m, verts, tris, census); break;
                    default: census.Unsupported++; census.UnsupportedClasses.Add(cls); break;
                }
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException || ex is IndexOutOfRangeException || ex is System.IO.InvalidDataException || ex is KeyNotFoundException)
            {
                census.Malformed++;
            }
        }

        private void WalkCompound(int off, double[] m, int depth, List<float> verts, List<int> tris, ShapeCensus census)
        {
            int arr;
            if (!pack64.Pointers.TryGetValue(off + 0x60, out arr)) { census.Malformed++; return; }
            int n = ReadInt32(source, off + 0x68);
            if (n < 0 || arr + n * InstanceSize > source.Length) { census.Malformed++; return; }
            for (int i = 0; i < n; i++)
            {
                int rec = arr + i * InstanceSize;
                census.Instances++;
                int flags = ReadInt32(source, rec + 0x0c) & 0xffffff;
                if ((flags & 0x40) == 0) { census.DisabledInstances++; continue; }   // hknpShapeInstance::IS_ENABLED
                int child;
                if (!pack64.Pointers.TryGetValue(rec + 0x50, out child)) { census.Malformed++; continue; }
                double[] local = InstanceTransform(source, rec);
                WalkShape(child, Compose(local, m), depth + 1, verts, tris, census);
            }
        }

        /// <summary>An instance record's transform: rotation columns scaled by the record's scale, then translation.</summary>
        internal static double[] InstanceTransform(byte[] b, int rec)
        {
            double sx = ReadSingle(b, rec + 0x40), sy = ReadSingle(b, rec + 0x44), sz = ReadSingle(b, rec + 0x48);
            if (sx == 0 && sy == 0 && sz == 0) { sx = sy = sz = 1; }
            return new double[]
            {
                ReadSingle(b, rec + 0x00) * sx, ReadSingle(b, rec + 0x04) * sx, ReadSingle(b, rec + 0x08) * sx,
                ReadSingle(b, rec + 0x10) * sy, ReadSingle(b, rec + 0x14) * sy, ReadSingle(b, rec + 0x18) * sy,
                ReadSingle(b, rec + 0x20) * sz, ReadSingle(b, rec + 0x24) * sz, ReadSingle(b, rec + 0x28) * sz,
                ReadSingle(b, rec + 0x30), ReadSingle(b, rec + 0x34), ReadSingle(b, rec + 0x38),
            };
        }

        private List<double[]> ConvexVertices(int off)
        {
            int count = ReadUInt16(source, off + 0x30), at = off + 0x30 + ReadUInt16(source, off + 0x32);
            int size = ObjectSizeFast(off);
            if (count <= 0 || count > 4096 || at + count * 16 > off + size) throw new System.IO.InvalidDataException("implausible vertex array");
            var v = new List<double[]>(count);
            for (int i = 0; i < count; i++) v.Add(new double[] { ReadSingle(source, at + i * 16), ReadSingle(source, at + i * 16 + 4), ReadSingle(source, at + i * 16 + 8) });
            return v;
        }

        private List<int> sortedObjects;

        private int ObjectSizeFast(int offset)
        {
            if (sortedObjects == null)
            {
                sortedObjects = new List<int>();
                foreach (KeyValuePair<int, string> obj in pack64.Objects) sortedObjects.Add(obj.Key);
                sortedObjects.Sort();
            }
            int idx = sortedObjects.BinarySearch(offset);
            if (idx < 0) idx = ~idx - 1;
            int end = pack64.DataBase + pack64.Data.Virt;
            if (idx + 1 < sortedObjects.Count && sortedObjects[idx + 1] < end) end = sortedObjects[idx + 1];
            return end - offset;
        }

        private void EmitPolytope(int off, double[] m, List<float> verts, List<int> tris, ShapeCensus census)
        {
            List<double[]> local = ConvexVertices(off);
            int nf = ReadUInt16(source, off + 0x44), fAt = off + 0x44 + ReadUInt16(source, off + 0x46);
            int ni = ReadUInt16(source, off + 0x48), iAt = off + 0x48 + ReadUInt16(source, off + 0x4a);
            int end = off + ObjectSizeFast(off);
            var faces = new List<int[]>();
            bool ok = nf > 0 && ni > 0 && fAt + nf * 4 <= end && iAt + ni <= end;
            int total = 0;
            for (int f = 0; ok && f < nf; f++)
            {
                int cnt = source[fAt + f * 4 + 1], first = ReadUInt16(source, fAt + f * 4 + 2);
                if (cnt < 3 || first + cnt > ni) { ok = false; break; }
                var face = new int[cnt];
                for (int k = 0; k < cnt; k++)
                {
                    face[k] = source[iAt + first + k];
                    if (face[k] >= local.Count) { ok = false; break; }
                }
                total += cnt;
                faces.Add(face);
            }
            if (!ok || total != ni) { EmitHull(local, m, verts, tris, census); return; }   // faces unusable: hull the vertices

            int b0 = verts.Count / 3;
            double[] centre = new double[3];
            var world = new List<double[]>(local.Count);
            foreach (double[] p in local)
            {
                double[] w = Apply(m, p);
                world.Add(w);
                for (int c = 0; c < 3; c++) centre[c] += w[c] / local.Count;
                verts.Add((float)w[0]); verts.Add((float)w[1]); verts.Add((float)w[2]);
            }
            foreach (int[] face in faces)
                for (int k = 1; k + 1 < face.Length; k++)
                    AddOutward(world, centre, b0, face[0], face[k], face[k + 1], tris, census);
        }

        // Winds a convex shape's triangle so its normal points away from the shape's centre (works under mirroring
        // transforms too); a triangle through the centre (a flat shape) faces up.
        private static void AddOutward(List<double[]> w, double[] centre, int b0, int a, int b, int c, List<int> tris, ShapeCensus census)
        {
            double[] n = Normal(w[a], w[b], w[c]);
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len < 1e-12) return;                                     // collinear corner of a fan
            double dx = (w[a][0] + w[b][0] + w[c][0]) / 3 - centre[0], dy = (w[a][1] + w[b][1] + w[c][1]) / 3 - centre[1], dz = (w[a][2] + w[b][2] + w[c][2]) / 3 - centre[2];
            double side = n[0] * dx + n[1] * dy + n[2] * dz;
            bool flip = Math.Abs(side) < 1e-9 * len ? n[1] < 0 : side < 0;
            tris.Add(b0 + a);
            if (flip) { tris.Add(b0 + c); tris.Add(b0 + b); }
            else { tris.Add(b0 + b); tris.Add(b0 + c); }
            census.Triangles++;
        }

        private void EmitHull(List<double[]> local, double[] m, List<float> verts, List<int> tris, ShapeCensus census)
        {
            var world = new List<double[]>(local.Count);
            foreach (double[] p in local) world.Add(Apply(m, p));
            List<int[]> hull = ConvexHull3.Build(world);
            if (hull == null || hull.Count == 0) { census.Malformed++; return; }
            int b0 = verts.Count / 3;
            double[] centre = new double[3];
            foreach (double[] w in world) { verts.Add((float)w[0]); verts.Add((float)w[1]); verts.Add((float)w[2]); for (int c = 0; c < 3; c++) centre[c] += w[c] / world.Count; }
            foreach (int[] t in hull) AddOutward(world, centre, b0, t[0], t[1], t[2], tris, census);
        }

        private void EmitSphere(int off, double[] m, List<float> verts, List<int> tris, ShapeCensus census)
        {
            List<double[]> v = ConvexVertices(off);
            double r = Math.Max(0, ReadSingle(source, off + 0x14));
            EmitHull(SpherePoints(v[0], r), m, verts, tris, census);
        }

        private void EmitCapsule(int off, double[] m, List<float> verts, List<int> tris, ShapeCensus census)
        {
            double r = Math.Max(0, ReadSingle(source, off + 0x14));
            double[] a = { ReadSingle(source, off + 0x50), ReadSingle(source, off + 0x54), ReadSingle(source, off + 0x58) };
            double[] b = { ReadSingle(source, off + 0x60), ReadSingle(source, off + 0x64), ReadSingle(source, off + 0x68) };
            List<double[]> pts = SpherePoints(a, r);
            pts.AddRange(SpherePoints(b, r));
            EmitHull(pts, m, verts, tris, census);
        }

        // Points of a once-subdivided icosahedron (42) at the given centre and radius.
        public static List<double[]> SpherePoints(double[] c, double r)
        {
            double t = (1 + Math.Sqrt(5)) / 2;
            var p = new List<double[]>
            {
                new[] { -1, t, 0.0 }, new[] { 1, t, 0.0 }, new[] { -1, -t, 0.0 }, new[] { 1, -t, 0.0 },
                new[] { 0.0, -1, t }, new[] { 0.0, 1, t }, new[] { 0.0, -1, -t }, new[] { 0.0, 1, -t },
                new[] { t, 0.0, -1 }, new[] { t, 0.0, 1 }, new[] { -t, 0.0, -1 }, new[] { -t, 0.0, 1 },
            };
            int[] f =
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            var mids = new Dictionary<long, int>();
            for (int i = 0; i < f.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = f[i + e], b = f[i + (e + 1) % 3];
                    long key = Math.Min(a, b) * 1000L + Math.Max(a, b);
                    if (mids.ContainsKey(key)) continue;
                    mids[key] = p.Count;
                    p.Add(new[] { (p[a][0] + p[b][0]) / 2, (p[a][1] + p[b][1]) / 2, (p[a][2] + p[b][2]) / 2 });
                }
            var o = new List<double[]>(p.Count);
            foreach (double[] q in p)
            {
                double l = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2]);
                o.Add(new[] { c[0] + q[0] / l * r, c[1] + q[1] / l * r, c[2] + q[2] / l * r });
            }
            return o;
        }

        private void EmitCompressedMesh(int off, double[] m, List<float> verts, List<int> tris, ShapeCensus census)
        {
            int data;
            if (!pack64.Pointers.TryGetValue(off + 0x60, out data)) { census.Malformed++; return; }
            int B = data + 0x10;                                   // hknpCompressedMeshShapeTree, inline
            double[] dmin = { ReadSingle(source, B + 0x10), ReadSingle(source, B + 0x14), ReadSingle(source, B + 0x18) };
            double[] dmax = { ReadSingle(source, B + 0x20), ReadSingle(source, B + 0x24), ReadSingle(source, B + 0x28) };
            int secs = ArrayPointer(B + 0x40), nsec = ReadInt32(source, B + 0x48);
            int prims = ArrayPointer(B + 0x50), nprim = ReadInt32(source, B + 0x58);
            int svIdx = ArrayPointer(B + 0x60), nsvIdx = ReadInt32(source, B + 0x68);
            int packed = ArrayPointer(B + 0x70), npacked = ReadInt32(source, B + 0x78);
            int shared = ArrayPointer(B + 0x80), nshared = ReadInt32(source, B + 0x88);
            if (nsec <= 0 || secs < 0 || nprim <= 0 || prims < 0) { census.Malformed++; return; }
            double sx = (dmax[0] - dmin[0]) / 0x1FFFFF, sy = (dmax[1] - dmin[1]) / 0x1FFFFF, sz = (dmax[2] - dmin[2]) / 0x3FFFFF;
            var local = new double[256][];
            for (int s = 0; s < nsec; s++)
            {
                int S = secs + s * 0x60;
                double ox = ReadSingle(source, S + 0x30), oy = ReadSingle(source, S + 0x34), oz = ReadSingle(source, S + 0x38);
                double qx = ReadSingle(source, S + 0x3c), qy = ReadSingle(source, S + 0x40), qz = ReadSingle(source, S + 0x44);
                int firstPacked = ReadInt32(source, S + 0x48);
                uint sv = (uint)ReadInt32(source, S + 0x4c), pr = (uint)ReadInt32(source, S + 0x50);
                int npk = source[S + 0x58];
                int svBase = (int)(sv >> 8), prBase = (int)(pr >> 8), prCount = (int)(pr & 0xff);
                if (prBase + prCount > nprim) { census.Malformed++; continue; }
                Func<int, double[]> vertex = k =>
                {
                    if (k < npk)
                    {
                        if (firstPacked + k >= npacked) throw new System.IO.InvalidDataException("packed vertex out of range");
                        uint p = (uint)ReadInt32(source, packed + 4 * (firstPacked + k));
                        return new double[] { ox + qx * (p & 0x7ff), oy + qy * ((p >> 11) & 0x7ff), oz + qz * (p >> 22) };
                    }
                    int si = svBase + k - npk;
                    if (si >= nsvIdx) throw new System.IO.InvalidDataException("shared index out of range");
                    int vi = ReadUInt16(source, svIdx + 2 * si);
                    if (vi >= nshared) throw new System.IO.InvalidDataException("shared vertex out of range");
                    ulong v = BitConverter.ToUInt64(source, shared + 8 * vi);
                    return new double[] { dmin[0] + sx * (v & 0x1FFFFF), dmin[1] + sy * ((v >> 21) & 0x1FFFFF), dmin[2] + sz * (v >> 42) };
                };
                for (int q = 0; q < prCount; q++)
                {
                    int at = prims + 4 * (prBase + q);
                    int a = source[at], b = source[at + 1], c = source[at + 2], d = source[at + 3];
                    if (c == d) EmitUp(vertex(a), vertex(b), vertex(c), m, verts, tris, census);
                    else
                    {
                        double[] va = vertex(a), vc = vertex(c);
                        EmitUp(va, vertex(b), vc, m, verts, tris, census);
                        EmitUp(va, vc, vertex(d), m, verts, tris, census);
                    }
                }
            }
        }

        private int ArrayPointer(int field)
        {
            int p;
            return pack64.Pointers.TryGetValue(field, out p) ? p : -1;
        }

        private static void EmitUp(double[] a, double[] b, double[] c, double[] m, List<float> verts, List<int> tris, ShapeCensus census)
        {
            double[] wa = Apply(m, a), wb = Apply(m, b), wc = Apply(m, c);
            double[] n = Normal(wa, wb, wc);
            if (n[0] * n[0] + n[1] * n[1] + n[2] * n[2] < 1e-18) return;
            int b0 = verts.Count / 3;
            foreach (double[] w in n[1] >= 0 ? new[] { wa, wb, wc } : new[] { wa, wc, wb })
            { verts.Add((float)w[0]); verts.Add((float)w[1]); verts.Add((float)w[2]); }
            tris.Add(b0); tris.Add(b0 + 1); tris.Add(b0 + 2);
            census.Triangles++;
        }

        // ---- LinearTransform arithmetic (12 numbers: right, up, forward, trans) --------------------------------------

        public static double[] IdentityTransform() { return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }; }

        public static double[] Apply(double[] m, double[] p)
        {
            return new double[]
            {
                p[0] * m[0] + p[1] * m[3] + p[2] * m[6] + m[9],
                p[0] * m[1] + p[1] * m[4] + p[2] * m[7] + m[10],
                p[0] * m[2] + p[1] * m[5] + p[2] * m[8] + m[11],
            };
        }

        /// <summary>a then b: world = b(a(p)).</summary>
        public static double[] Compose(double[] a, double[] b)
        {
            var o = new double[12];
            for (int row = 0; row < 3; row++)
                for (int c = 0; c < 3; c++)
                    o[row * 3 + c] = a[row * 3] * b[c] + a[row * 3 + 1] * b[3 + c] + a[row * 3 + 2] * b[6 + c];
            double[] t = Apply(b, new[] { a[9], a[10], a[11] });
            o[9] = t[0]; o[10] = t[1]; o[11] = t[2];
            return o;
        }

        private static double[] Normal(double[] a, double[] b, double[] c)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2], vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            return new[] { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
        }
    }

    /// <summary>Incremental 3D convex hull for the small point sets of collision shapes (a few hundred at most).</summary>
    public static class ConvexHull3
    {
        /// <summary>Outward-wound triangles over the given points, or null when they span no volume.</summary>
        public static List<int[]> Build(IList<double[]> p)
        {
            int n = p.Count;
            if (n < 4) return null;
            double scale = 0;
            foreach (double[] q in p) for (int c = 0; c < 3; c++) scale = Math.Max(scale, Math.Abs(q[c]));
            double eps = Math.Max(1e-9, scale * 1e-9) * 16;
            // initial tetrahedron: extreme pair, farthest from their line, farthest from their plane
            int i0 = 0, i1 = 0;
            for (int i = 1; i < n; i++) { if (p[i][0] < p[i0][0]) i0 = i; if (p[i][0] > p[i1][0]) i1 = i; }
            if (Dist2(p[i0], p[i1]) < eps * eps)
            {
                double best = -1;
                for (int i = 0; i < n; i++) { double d = Dist2(p[i0], p[i]); if (d > best) { best = d; i1 = i; } }
                if (best < eps * eps) return null;
            }
            int i2 = -1; double bl = eps * eps;
            for (int i = 0; i < n; i++)
            {
                double[] cr = Cross(Sub(p[i1], p[i0]), Sub(p[i], p[i0]));
                double d = Dot(cr, cr);
                if (d > bl) { bl = d; i2 = i; }
            }
            if (i2 < 0) return null;
            double[] pn = Cross(Sub(p[i1], p[i0]), Sub(p[i2], p[i0]));
            double pl = Math.Sqrt(Dot(pn, pn));
            int i3 = -1; double bd = eps;
            for (int i = 0; i < n; i++)
            {
                double d = Math.Abs(Dot(pn, Sub(p[i], p[i0]))) / pl;
                if (d > bd) { bd = d; i3 = i; }
            }
            if (i3 < 0) return null;                      // flat: no volume
            double[] inner = new double[3];
            foreach (int i in new[] { i0, i1, i2, i3 }) for (int c = 0; c < 3; c++) inner[c] += p[i][c] / 4;

            var faces = new List<Face>();
            Action<int, int, int> add = (a, b, c) =>
            {
                var f = new Face { A = a, B = b, C = c };
                f.N = Cross(Sub(p[b], p[a]), Sub(p[c], p[a]));
                if (Dot(f.N, Sub(inner, p[a])) > 0) { f.B = c; f.C = b; f.N = new[] { -f.N[0], -f.N[1], -f.N[2] }; }
                double l = Math.Sqrt(Dot(f.N, f.N));
                f.N = new[] { f.N[0] / l, f.N[1] / l, f.N[2] / l };
                f.D = Dot(f.N, p[f.A]);
                faces.Add(f);
            };
            add(i0, i1, i2); add(i0, i1, i3); add(i0, i2, i3); add(i1, i2, i3);
            for (int i = 0; i < n; i++)
            {
                if (i == i0 || i == i1 || i == i2 || i == i3) continue;
                var visible = new List<Face>();
                foreach (Face f in faces) if (!f.Dead && Dot(f.N, p[i]) - f.D > eps) visible.Add(f);
                if (visible.Count == 0) continue;
                // horizon: directed edges of visible faces whose reverse is not on a visible face
                var edges = new Dictionary<long, int[]>();
                foreach (Face f in visible)
                {
                    f.Dead = true;
                    foreach (int[] e in new[] { new[] { f.A, f.B }, new[] { f.B, f.C }, new[] { f.C, f.A } })
                    {
                        long rev = (long)e[1] * n + e[0];
                        if (edges.ContainsKey(rev)) edges.Remove(rev);
                        else edges[(long)e[0] * n + e[1]] = e;
                    }
                }
                foreach (int[] e in edges.Values) add(e[0], e[1], i);
                faces.RemoveAll(f => f.Dead);
            }
            var o = new List<int[]>(faces.Count);
            foreach (Face f in faces) o.Add(new[] { f.A, f.B, f.C });
            return o;
        }

        private sealed class Face { public int A, B, C; public double[] N; public double D; public bool Dead; }
        private static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double Dist2(double[] a, double[] b) { double[] d = Sub(a, b); return Dot(d, d); }
        private static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
    }
}
