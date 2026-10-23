using System;
using System.Collections.Generic;

namespace FrostyCLI.Core
{
    /// <summary>
    /// The terrain as navmesh input over a large region: an adaptive quadtree over a regular height grid. A cell is
    /// emitted as two triangles when every grid point inside or on it lies within <c>tolerance</c> metres of the
    /// bilinear patch through its corners and none of its grid cells is a hole; otherwise it splits, down to one grid
    /// cell, where hole cells are left out. A Walker Assault map sampled at 0.5 m is 28 million grid points; smooth snow
    /// collapses into cells of up to 2^maxLevel steps while trenches, rocks and hole rims keep full resolution.
    ///
    /// Neighbouring cells of different sizes meet in T-junctions whose height gap is at most the tolerance; Recast
    /// voxelises in plan view, where both sides cover the shared edge, so a tolerance under the cell height leaves no
    /// gap in the heightfield.
    /// </summary>
    public static class TerrainNavMesher
    {
        public sealed class Stats { public int GridPoints, HoleCells, Cells, Triangles, Vertices, FilledPoints; }

        /// <summary>Height at (x, z) in metres, or false where the terrain has no ground sample (inside a hole).</summary>
        public delegate bool TryHeight(double x, double z, out double metres);

        /// <param name="height">height at (x, z) in metres; false inside holes</param>
        /// <param name="hole">true when the grid cell centred at (x, z) is a terrain hole</param>
        public static Stats Build(TryHeight height, Func<double, double, bool> hole,
                                  double x0, double z0, double x1, double z1, double step, double tolerance, int maxLevel,
                                  List<float> verts, List<int> tris)
        {
            int nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / step - 1e-9)), nz = Math.Max(1, (int)Math.Ceiling((z1 - z0) / step - 1e-9));
            int w = nx + 1;
            var H = new float[(long)w * (nz + 1)];
            var missing = new List<long>();
            for (int j = 0; j <= nz; j++)
                for (int i = 0; i <= nx; i++)
                {
                    double h;
                    if (height(x0 + i * step, z0 + j * step, out h)) H[(long)j * w + i] = (float)h;
                    else { H[(long)j * w + i] = float.NaN; missing.Add((long)j * w + i); }
                }
            int filled = FillMissing(H, w, nz + 1, missing);
            // prefix sums of hole cells for O(1) "any hole in this block"
            var P = new int[(long)(nx + 1) * (nz + 1)];
            var stats = new Stats { GridPoints = w * (nz + 1), FilledPoints = filled };
            for (int j = 0; j < nz; j++)
                for (int i = 0; i < nx; i++)
                {
                    int hcell = hole(x0 + (i + 0.5) * step, z0 + (j + 0.5) * step) ? 1 : 0;
                    stats.HoleCells += hcell;
                    P[(long)(j + 1) * (nx + 1) + i + 1] = hcell + P[(long)j * (nx + 1) + i + 1] + P[(long)(j + 1) * (nx + 1) + i] - P[(long)j * (nx + 1) + i];
                }
            Func<int, int, int, int, int> holes = (i0, j0, i1, j1) =>
                P[(long)j1 * (nx + 1) + i1] - P[(long)j0 * (nx + 1) + i1] - P[(long)j1 * (nx + 1) + i0] + P[(long)j0 * (nx + 1) + i0];

            var index = new Dictionary<long, int>();
            Func<int, int, int> vtx = (i, j) =>
            {
                long key = (long)j * w + i;
                int id;
                if (!index.TryGetValue(key, out id))
                {
                    id = verts.Count / 3;
                    index[key] = id;
                    verts.Add((float)(x0 + i * step)); verts.Add(H[key]); verts.Add((float)(z0 + j * step));
                }
                return id;
            };

            int block = 1 << Math.Max(0, maxLevel);
            var stack = new Stack<int[]>();
            for (int bj = 0; bj < nz; bj += block)
                for (int bi = 0; bi < nx; bi += block)
                {
                    stack.Push(new[] { bi, bj, block });
                    while (stack.Count > 0)
                    {
                        int[] c = stack.Pop();
                        int i0 = c[0], j0 = c[1], s = c[2];
                        if (i0 >= nx || j0 >= nz) continue;
                        int i1 = i0 + s, j1 = j0 + s;
                        bool split = false;
                        if (s > 1)
                        {
                            if (i1 > nx || j1 > nz) split = true;                       // runs past the region edge
                            else if (holes(i0, j0, i1, j1) > 0) split = true;
                            else split = !Flat(H, w, i0, j0, s, tolerance);
                        }
                        else if (holes(i0, j0, i1, j1) > 0) continue;                    // a hole cell
                        if (split)
                        {
                            int h = s / 2;
                            stack.Push(new[] { i0, j0, h }); stack.Push(new[] { i0 + h, j0, h });
                            stack.Push(new[] { i0, j0 + h, h }); stack.Push(new[] { i0 + h, j0 + h, h });
                            continue;
                        }
                        int a = vtx(i0, j0), b = vtx(i1, j0), d = vtx(i0, j1), e = vtx(i1, j1);
                        // +Y normals: (a, d, b) and (b, d, e) seen from above with x right, z down the page
                        tris.Add(a); tris.Add(d); tris.Add(b);
                        tris.Add(b); tris.Add(d); tris.Add(e);
                        stats.Cells++;
                        stats.Triangles += 2;
                    }
                }
            stats.Vertices = index.Count;
            return stats;
        }

        // Grid points with no ground (inside a hole) take the mean of their measured neighbours, breadth first from the
        // hole rim: the corners of the ground cells round a hole stay at the rim's height instead of the hole marker's
        // 0 m. Returns how many points were filled.
        internal static int FillMissing(float[] H, int w, int h, List<long> missing)
        {
            if (missing.Count == 0) return 0;
            var queue = new Queue<long>();
            var queued = new HashSet<long>();
            foreach (long m in missing)
                foreach (long nb in Neighbours(m, w, h))
                    if (!float.IsNaN(H[nb])) { queue.Enqueue(m); queued.Add(m); break; }
            int filled = 0;
            while (queue.Count > 0)
            {
                long m = queue.Dequeue();
                double sum = 0; int n = 0;
                foreach (long nb in Neighbours(m, w, h)) if (!float.IsNaN(H[nb])) { sum += H[nb]; n++; }
                if (n == 0) continue;
                H[m] = (float)(sum / n);
                filled++;
                foreach (long nb in Neighbours(m, w, h))
                    if (float.IsNaN(H[nb]) && queued.Add(nb)) queue.Enqueue(nb);
            }
            // a region with no ground at all keeps 0
            foreach (long m in missing) if (float.IsNaN(H[m])) H[m] = 0;
            return filled;
        }

        private static IEnumerable<long> Neighbours(long m, int w, int h)
        {
            long i = m % w, j = m / w;
            if (i > 0) yield return m - 1;
            if (i + 1 < w) yield return m + 1;
            if (j > 0) yield return m - w;
            if (j + 1 < h) yield return m + w;
        }

        // every grid point of the closed cell within tolerance of the corners' bilinear patch
        internal static bool Flat(float[] H, int w, int i0, int j0, int s, double tolerance)
        {
            double h00 = H[(long)j0 * w + i0], h10 = H[(long)j0 * w + i0 + s], h01 = H[(long)(j0 + s) * w + i0], h11 = H[(long)(j0 + s) * w + i0 + s];
            for (int j = 0; j <= s; j++)
            {
                double v = (double)j / s;
                for (int i = 0; i <= s; i++)
                {
                    double u = (double)i / s;
                    double b = (h00 * (1 - u) + h10 * u) * (1 - v) + (h01 * (1 - u) + h11 * u) * v;
                    if (Math.Abs(H[(long)(j0 + j) * w + i0 + i] - b) > tolerance) return false;
                }
            }
            return true;
        }
    }
}
