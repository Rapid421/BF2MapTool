using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// World-space triangles (an OBJ from `navmesh geometry --out`) with a bounding-volume hierarchy for occlusion rays:
    /// placed meshes that hide the sky from terrain texels and bounce light back onto them.
    /// </summary>
    public sealed class GeometrySoup
    {
        public float[] V;          // xyz per vertex
        public int[] T;            // 3 vertex indices per triangle
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        int[] _tri;                // triangle order for the BVH leaves
        Node[] _nodes;
        int _nodeCount;

        struct Node
        {
            public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
            public int Left, Right;     // children (inner) or -1
            public int First, Count;    // leaf triangle range
        }

        public int TriangleCount => T.Length / 3;

        /// <summary>
        /// Load the triangles of an OBJ. The group `terrain` (what `navmesh geometry` writes for the heightfield) is skipped:
        /// the relight marches the dense heightfield itself, and a coarse copy of the ground would shadow every texel.
        /// </summary>
        public static GeometrySoup LoadObj(string path, bool skipTerrain = true)
        {
            var v = new List<float>();
            var t = new List<int>();
            bool skip = false;
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("g ") || line.StartsWith("o "))
                    skip = skipTerrain && line.Substring(2).Trim().Equals("terrain", StringComparison.OrdinalIgnoreCase);
                else if (line.StartsWith("v "))
                {
                    string[] p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    v.Add(float.Parse(p[1], CultureInfo.InvariantCulture)); v.Add(float.Parse(p[2], CultureInfo.InvariantCulture));
                    v.Add(float.Parse(p[3], CultureInfo.InvariantCulture));
                }
                else if (line.StartsWith("f ") && !skip)
                {
                    string[] p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    var idx = new int[p.Length - 1];
                    for (int i = 1; i < p.Length; i++)
                    {
                        string s = p[i]; int slash = s.IndexOf('/');
                        int k = int.Parse(slash >= 0 ? s.Substring(0, slash) : s, CultureInfo.InvariantCulture);
                        idx[i - 1] = k > 0 ? k - 1 : v.Count / 3 + k;
                    }
                    for (int i = 1; i + 1 < idx.Length; i++) { t.Add(idx[0]); t.Add(idx[i]); t.Add(idx[i + 1]); }
                }
            }
            // a triangle with a non-finite corner would poison the bounding boxes it falls in
            var kept = new List<int>(t.Count);
            Func<int, bool> finite = k => !float.IsNaN(v[k * 3]) && !float.IsInfinity(v[k * 3]) && !float.IsNaN(v[k * 3 + 1]) &&
                                          !float.IsInfinity(v[k * 3 + 1]) && !float.IsNaN(v[k * 3 + 2]) && !float.IsInfinity(v[k * 3 + 2]);
            for (int i = 0; i + 2 < t.Count; i += 3)
                if (finite(t[i]) && finite(t[i + 1]) && finite(t[i + 2])) { kept.Add(t[i]); kept.Add(t[i + 1]); kept.Add(t[i + 2]); }
            var soup = new GeometrySoup { V = v.ToArray(), T = kept.ToArray() };
            soup.Build();
            return soup;
        }

        void Build()
        {
            int n = TriangleCount;
            _tri = new int[n];
            var cx = new float[n]; var cy = new float[n]; var cz = new float[n];
            for (int i = 0; i < n; i++)
            {
                _tri[i] = i;
                int a = T[3 * i] * 3, b = T[3 * i + 1] * 3, c = T[3 * i + 2] * 3;
                cx[i] = (V[a] + V[b] + V[c]) / 3; cy[i] = (V[a + 1] + V[b + 1] + V[c + 1]) / 3; cz[i] = (V[a + 2] + V[b + 2] + V[c + 2]) / 3;
            }
            _nodes = new Node[Math.Max(1, 2 * n)];
            _nodeCount = 0;
            if (n > 0) BuildNode(0, n, cx, cy, cz);
            if (_nodeCount > 0) { MinX = _nodes[0].MinX; MinY = _nodes[0].MinY; MinZ = _nodes[0].MinZ; MaxX = _nodes[0].MaxX; MaxY = _nodes[0].MaxY; MaxZ = _nodes[0].MaxZ; }
        }

        int BuildNode(int first, int count, float[] cx, float[] cy, float[] cz)
        {
            int id = _nodeCount++;
            var nd = new Node { MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxY = float.MinValue, MaxZ = float.MinValue, Left = -1, Right = -1 };
            float cminX = float.MaxValue, cminY = float.MaxValue, cminZ = float.MaxValue, cmaxX = float.MinValue, cmaxY = float.MinValue, cmaxZ = float.MinValue;
            for (int i = first; i < first + count; i++)
            {
                int tr = _tri[i];
                for (int k = 0; k < 3; k++)
                {
                    int vi = T[3 * tr + k] * 3;
                    nd.MinX = Math.Min(nd.MinX, V[vi]); nd.MinY = Math.Min(nd.MinY, V[vi + 1]); nd.MinZ = Math.Min(nd.MinZ, V[vi + 2]);
                    nd.MaxX = Math.Max(nd.MaxX, V[vi]); nd.MaxY = Math.Max(nd.MaxY, V[vi + 1]); nd.MaxZ = Math.Max(nd.MaxZ, V[vi + 2]);
                }
                cminX = Math.Min(cminX, cx[tr]); cminY = Math.Min(cminY, cy[tr]); cminZ = Math.Min(cminZ, cz[tr]);
                cmaxX = Math.Max(cmaxX, cx[tr]); cmaxY = Math.Max(cmaxY, cy[tr]); cmaxZ = Math.Max(cmaxZ, cz[tr]);
            }
            if (count <= 4)
            {
                nd.First = first; nd.Count = count;
                _nodes[id] = nd;
                return id;
            }
            float ex = cmaxX - cminX, ey = cmaxY - cminY, ez = cmaxZ - cminZ;
            int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
            float[] key = axis == 0 ? cx : axis == 1 ? cy : cz;
            float mid = axis == 0 ? (cminX + cmaxX) / 2 : axis == 1 ? (cminY + cmaxY) / 2 : (cminZ + cmaxZ) / 2;
            int i0 = first, i1 = first + count - 1;
            while (i0 <= i1)
            {
                if (key[_tri[i0]] < mid) i0++;
                else { int tmp = _tri[i0]; _tri[i0] = _tri[i1]; _tri[i1] = tmp; i1--; }
            }
            int leftCount = i0 - first;
            if (leftCount == 0 || leftCount == count) leftCount = count / 2;   // all centroids equal on the axis
            nd.Left = BuildNode(first, leftCount, cx, cy, cz);
            nd.Right = BuildNode(first + leftCount, count - leftCount, cx, cy, cz);
            _nodes[id] = nd;
            return id;
        }

        /// <summary>Closest hit along (o + t d), t in (0, tmax]; returns the distance or -1 and the geometric normal facing the ray.</summary>
        public double Cast(double ox, double oy, double oz, double dx, double dy, double dz, double tmax,
                           out float nx, out float ny, out float nz)
        {
            nx = ny = nz = 0;
            if (_nodeCount == 0) return -1;
            double best = tmax; int bestTri = -1;
            double ix = 1 / (Math.Abs(dx) < 1e-12 ? 1e-12 : dx), iy = 1 / (Math.Abs(dy) < 1e-12 ? 1e-12 : dy), iz = 1 / (Math.Abs(dz) < 1e-12 ? 1e-12 : dz);
            var stack = new int[64];
            int sp = 0;
            stack[sp++] = 0;
            while (sp > 0)
            {
                Node nd = _nodes[stack[--sp]];
                double t0 = ((ix >= 0 ? nd.MinX : nd.MaxX) - ox) * ix, t1 = ((ix >= 0 ? nd.MaxX : nd.MinX) - ox) * ix;
                double u0 = ((iy >= 0 ? nd.MinY : nd.MaxY) - oy) * iy, u1 = ((iy >= 0 ? nd.MaxY : nd.MinY) - oy) * iy;
                double w0 = ((iz >= 0 ? nd.MinZ : nd.MaxZ) - oz) * iz, w1 = ((iz >= 0 ? nd.MaxZ : nd.MinZ) - oz) * iz;
                double tn = Math.Max(t0, Math.Max(u0, w0)), tf = Math.Min(t1, Math.Min(u1, w1));
                if (tn > tf || tf < 0 || tn > best) continue;
                if (nd.Left < 0)
                {
                    for (int i = nd.First; i < nd.First + nd.Count; i++)
                    {
                        int tr = _tri[i];
                        double t = Hit(tr, ox, oy, oz, dx, dy, dz);
                        if (t > 1e-4 && t < best) { best = t; bestTri = tr; }
                    }
                }
                else if (sp < 62) { stack[sp++] = nd.Left; stack[sp++] = nd.Right; }
            }
            if (bestTri < 0) return -1;
            int a = T[3 * bestTri] * 3, b = T[3 * bestTri + 1] * 3, c = T[3 * bestTri + 2] * 3;
            double e1x = V[b] - V[a], e1y = V[b + 1] - V[a + 1], e1z = V[b + 2] - V[a + 2];
            double e2x = V[c] - V[a], e2y = V[c + 1] - V[a + 1], e2z = V[c + 2] - V[a + 2];
            double cx = e1y * e2z - e1z * e2y, cy = e1z * e2x - e1x * e2z, cz = e1x * e2y - e1y * e2x;
            double l = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (l > 0) { cx /= l; cy /= l; cz /= l; }
            if (cx * dx + cy * dy + cz * dz > 0) { cx = -cx; cy = -cy; cz = -cz; }
            nx = (float)cx; ny = (float)cy; nz = (float)cz;
            return best;
        }

        double Hit(int tr, double ox, double oy, double oz, double dx, double dy, double dz)
        {
            int a = T[3 * tr] * 3, b = T[3 * tr + 1] * 3, c = T[3 * tr + 2] * 3;
            double e1x = V[b] - V[a], e1y = V[b + 1] - V[a + 1], e1z = V[b + 2] - V[a + 2];
            double e2x = V[c] - V[a], e2y = V[c + 1] - V[a + 1], e2z = V[c + 2] - V[a + 2];
            double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            double det = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(det) < 1e-12) return -1;
            double inv = 1 / det;
            double sx = ox - V[a], sy = oy - V[a + 1], sz = oz - V[a + 2];
            double u = (sx * px + sy * py + sz * pz) * inv;
            if (u < 0 || u > 1) return -1;
            double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            double w = (dx * qx + dy * qy + dz * qz) * inv;
            if (w < 0 || u + w > 1) return -1;
            return (e2x * qx + e2y * qy + e2z * qz) * inv;
        }
    }
}
