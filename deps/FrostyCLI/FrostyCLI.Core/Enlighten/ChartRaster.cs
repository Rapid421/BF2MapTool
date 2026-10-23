using System;
using System.Collections.Generic;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// A placed mesh's lightmap chart: its triangles rasterised in RadiosityTexCoord space (0..1 over the chart) onto a
    /// w x h texel rect, each covered texel carrying the world position and face normal of the surface under its centre.
    /// Uncovered texels next to covered ones are filled from their neighbours (a few rings), so bilinear filtering at a
    /// UV island's edge does not pull in black.
    /// </summary>
    public sealed class ChartRaster
    {
        public readonly int W, H;
        public readonly double[] X, Y, Z;     // world position per texel
        public readonly float[] N;            // world normal per texel (3 each)
        public readonly byte[] State;         // 0 empty, 1 covered, 2 dilated

        public ChartRaster(int w, int h)
        {
            W = w; H = h;
            X = new double[w * h]; Y = new double[w * h]; Z = new double[w * h];
            N = new float[w * h * 3]; State = new byte[w * h];
        }

        public int Covered { get { int c = 0; foreach (byte s in State) if (s == 1) c++; return c; } }

        /// <summary>
        /// Rasterise triangles (indices into the vertex arrays) given object-space positions <paramref name="pos"/> (3 per
        /// vertex) and radiosity uvs <paramref name="uv"/> (2 per vertex), placed with the row matrix right/up/forward/trans
        /// (world = x * right + y * up + z * forward + trans, Frostbite LinearTransform rows).
        /// </summary>
        public void Rasterise(float[] pos, float[] uv, IList<int> tris, double[] right, double[] up, double[] forward, double[] trans,
                              bool flipV = false, bool flipNormals = false)
        {
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                double[] pa = World(pos, a, right, up, forward, trans), pb = World(pos, b, right, up, forward, trans), pc = World(pos, c, right, up, forward, trans);
                double ex = pb[0] - pa[0], ey = pb[1] - pa[1], ez = pb[2] - pa[2], fx = pc[0] - pa[0], fy = pc[1] - pa[1], fz = pc[2] - pa[2];
                double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx, nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl < 1e-12) continue;
                nx /= nl; ny /= nl; nz /= nl;
                if (flipNormals) { nx = -nx; ny = -ny; nz = -nz; }
                double ua = uv[a * 2] * W, va = (flipV ? 1 - uv[a * 2 + 1] : uv[a * 2 + 1]) * H;
                double ub = uv[b * 2] * W, vb = (flipV ? 1 - uv[b * 2 + 1] : uv[b * 2 + 1]) * H;
                double uc = uv[c * 2] * W, vc = (flipV ? 1 - uv[c * 2 + 1] : uv[c * 2 + 1]) * H;
                double den = (vb - vc) * (ua - uc) + (uc - ub) * (va - vc);
                if (Math.Abs(den) < 1e-12) continue;
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ua, Math.Min(ub, uc)))), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ua, Math.Max(ub, uc))));
                int y0 = Math.Max(0, (int)Math.Floor(Math.Min(va, Math.Min(vb, vc)))), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(va, Math.Max(vb, vc))));
                for (int py = y0; py <= y1; py++)
                    for (int px = x0; px <= x1; px++)
                    {
                        double sx = px + 0.5, sy = py + 0.5;
                        double l0 = ((vb - vc) * (sx - uc) + (uc - ub) * (sy - vc)) / den;
                        double l1 = ((vc - va) * (sx - uc) + (ua - uc) * (sy - vc)) / den;
                        double l2 = 1 - l0 - l1;
                        const double eps = -1e-6;
                        if (l0 < eps || l1 < eps || l2 < eps) continue;
                        int i = py * W + px;
                        State[i] = 1;
                        X[i] = l0 * pa[0] + l1 * pb[0] + l2 * pc[0];
                        Y[i] = l0 * pa[1] + l1 * pb[1] + l2 * pc[1];
                        Z[i] = l0 * pa[2] + l1 * pb[2] + l2 * pc[2];
                        N[i * 3] = (float)nx; N[i * 3 + 1] = (float)ny; N[i * 3 + 2] = (float)nz;
                    }
            }
        }

        /// <summary>
        /// Texels a triangle touches but whose centre it misses (thin slivers) are covered too: conservative pass over the
        /// empty texels, taking the nearest point of any triangle within half a texel is expensive, so instead grow the
        /// covered set into empty neighbours <paramref name="rings"/> times, copying position and normal (state 2).
        /// </summary>
        public void Dilate(int rings = 2)
        {
            for (int r = 0; r < rings; r++)
            {
                var add = new List<int[]>();
                for (int py = 0; py < H; py++)
                    for (int px = 0; px < W; px++)
                    {
                        int i = py * W + px;
                        if (State[i] != 0) continue;
                        for (int k = 0; k < 8; k++)
                        {
                            int qx = px + Dx[k], qy = py + Dy[k];
                            if (qx < 0 || qy < 0 || qx >= W || qy >= H) continue;
                            int j = qy * W + qx;
                            if (State[j] == 0) continue;
                            add.Add(new[] { i, j });
                            break;
                        }
                    }
                foreach (int[] p in add)
                {
                    int i = p[0], j = p[1];
                    State[i] = 2; X[i] = X[j]; Y[i] = Y[j]; Z[i] = Z[j];
                    N[i * 3] = N[j * 3]; N[i * 3 + 1] = N[j * 3 + 1]; N[i * 3 + 2] = N[j * 3 + 2];
                }
            }
        }

        /// <summary>
        /// Whether (b - a) x (c - a) points out of the mesh: the signed volume of a closed mesh is positive then. Meshes
        /// are not always closed, but a house's shell is close enough for the sign.
        /// </summary>
        public static bool CrossPointsOut(float[] pos, IList<int> tris)
        {
            double v = 0;
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t] * 3, b = tris[t + 1] * 3, c = tris[t + 2] * 3;
                double bx = pos[b], by = pos[b + 1], bz = pos[b + 2], cx = pos[c], cy = pos[c + 1], cz = pos[c + 2];
                v += pos[a] * (by * cz - bz * cy) + pos[a + 1] * (bz * cx - bx * cz) + pos[a + 2] * (bx * cy - by * cx);
            }
            return v >= 0;
        }

        static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 }, Dy = { 0, 0, 1, -1, 1, -1, 1, -1 };

        static double[] World(float[] pos, int v, double[] r, double[] u, double[] f, double[] t)
        {
            double x = pos[v * 3], y = pos[v * 3 + 1], z = pos[v * 3 + 2];
            return new[] { x * r[0] + y * u[0] + z * f[0] + t[0], x * r[1] + y * u[1] + z * f[1] + t[1], x * r[2] + y * u[2] + z * f[2] + t[2] };
        }
    }
}
