#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\OccluderPlaneFit.cs (sha256:FDE5FD2418255912).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// The per-node occluder grid stack: levels coarsest first, level k holds <c>(2^k + 1)^2</c> corner heights in
    /// sample units (SPEC §2).
    /// <para>
    /// Shape: FIFA's <c>PipelineHeightfieldTree::internalCalculateOccluderGrid</c> (FIFA21_SOURCE). For every
    /// target cell a plane is locked through the cell's two diagonals and lowered until every source sample in the
    /// cell lies on or above it; the plane's height at the four corners is min-merged into the grid. The finest
    /// level of a node with four data children is their next-coarser grids stitched together (BFII_EXACT on 92-93%
    /// of shipped points; the rest are edge seams); each coarser level is fitted to the finer one.
    /// </para>
    /// <para>
    /// This is conservative by construction: a plane below every sample of a cell is below the rendered surface of
    /// that cell at every LOD (coarser LODs interpolate a subset of the same samples). It is not DICE's exact
    /// output: DICE fitted leaf grids on the unblurred 2x source raster, which the resource does not carry.
    /// Hole samples (0) drive the touched corners to 0, as in FIFA.
    /// </para>
    /// </summary>
    public static class OccluderPlaneFit
    {
        /// <param name="samples">The node tile, <paramref name="side"/>^2 samples including borders.</param>
        /// <param name="childFinestButOne">Per child (quadtree order) its level <c>depth-2</c> grid, or null.</param>
        public static ushort[][] BuildNode(ushort[] samples, int side, int border, int pot, int depth,
            double nodeSize, double metresPerUnit, ushort[]?[]? childFinestButOne, double marginMetres = 0)
        {
            double marginUnits = marginMetres / metresPerUnit;
            var grids = new ushort[depth][];
            int finest = depth - 1;
            int pts = (1 << finest) + 1;
            var target = new ushort[pts * pts];
            for (int i = 0; i < target.Length; i++) target[i] = ushort.MaxValue;

            int children = 0;
            if (childFinestButOne != null && finest >= 1)
            {
                int cpts = (1 << (finest - 1)) + 1;
                for (int ci = 0; ci < 4; ci++)
                {
                    var cg = childFinestButOne[ci];
                    if (cg == null) continue;
                    children++;
                    int ox = ChildX[ci] * (cpts - 1), oz = ChildZ[ci] * (cpts - 1);
                    for (int z = 0; z < cpts; z++)
                        for (int x = 0; x < cpts; x++)
                        {
                            int t = (z + oz) * pts + x + ox;
                            ushort v = cg[z * cpts + x];
                            if (v < target[t]) target[t] = v;
                        }
                }
            }

            // The margin is applied once, on the fit to real samples; stitched child grids and coarser levels
            // (fitted to already-lowered grids) inherit it.
            if (children < 4)
                Fit(target, pts, samples, side, border, pot, nodeSize, metresPerUnit, marginUnits);
            grids[finest] = target;

            for (int k = finest - 1; k >= 0; k--)
            {
                int kp = (1 << k) + 1;
                var g = new ushort[kp * kp];
                for (int i = 0; i < g.Length; i++) g[i] = ushort.MaxValue;
                int fp = (1 << (k + 1)) + 1;
                Fit(g, kp, grids[k + 1], fp, 0, fp - 1, nodeSize, metresPerUnit, 0);
                grids[k] = g;
            }
            return grids;
        }

        private static readonly int[] ChildX = { 0, 1, 1, 0 };
        private static readonly int[] ChildZ = { 0, 0, 1, 1 };

        /// <summary>Concatenate levels coarsest first, the order BFII stores them.</summary>
        public static byte[] ToBytes(ushort[][] grids)
        {
            int total = 0;
            foreach (var g in grids) total += g.Length;
            var bytes = new byte[total * 2];
            int p = 0;
            foreach (var g in grids)
                foreach (ushort v in g) { bytes[p++] = (byte)v; bytes[p++] = (byte)(v >> 8); }
            return bytes;
        }

        private static void Fit(ushort[] target, int pts, ushort[] source, int stride, int border, int sourcePot,
            double nodeSize, double mpu, double marginUnits)
        {
            int cells = pts - 1;
            int ppt = sourcePot / cells;
            double tmpp = nodeSize / cells, smpp = nodeSize / sourcePot;

            for (int tz = 0; tz < cells; tz++)
            {
                for (int tx = 0; tx < cells; tx++)
                {
                    int x0 = tx * ppt, z0 = tz * ppt, x1 = (tx + 1) * ppt, z1 = (tz + 1) * ppt;
                    double h00 = S(x0, z0) * mpu, h01 = S(x0, z1) * mpu, h10 = S(x1, z0) * mpu, h11 = S(x1, z1) * mpu;

                    // Plane through the diagonals: normal = (c11 - c00) x (c10 - c01); its y is 2*tmpp^2 > 0.
                    double d0x = tmpp, d0y = h11 - h00, d0z = tmpp;
                    double d1x = tmpp, d1y = h10 - h01, d1z = -tmpp;
                    double nx = d0y * d1z - d0z * d1y;
                    double ny = d0z * d1x - d0x * d1z;
                    double nz = d0x * d1y - d0y * d1x;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    nx /= len; ny /= len; nz /= len;

                    double pmin = double.MaxValue;
                    bool hole = false;
                    for (int sz = z0; sz <= z1; sz++)
                        for (int sx = x0; sx <= x1; sx++)
                        {
                            ushort raw = S(sx, sz);
                            if (raw == 0) { hole = true; continue; }
                            double dot = nx * (sx * smpp) + ny * (raw * mpu) + nz * (sz * smpp);
                            if (dot < pmin) pmin = dot;
                        }

                    Corner(tx, tz, tx * tmpp, tz * tmpp);
                    Corner(tx, tz + 1, tx * tmpp, (tz + 1) * tmpp);
                    Corner(tx + 1, tz + 1, (tx + 1) * tmpp, (tz + 1) * tmpp);
                    Corner(tx + 1, tz, (tx + 1) * tmpp, tz * tmpp);

                    void Corner(int cx, int cz, double wx, double wz)
                    {
                        double v;
                        if (hole || pmin == double.MaxValue) v = 0;
                        else
                        {
                            v = (pmin - (nx * wx + nz * wz)) / (ny * mpu) - marginUnits;
                            if (v < 1) v = 1;
                            if (v > 65535) v = 65535;
                        }
                        ushort u = (ushort)Math.Floor(v);
                        int t = cz * pts + cx;
                        if (u < target[t]) target[t] = u;
                    }
                }
            }

            ushort S(int x, int z)
            {
                if (x > sourcePot) x = sourcePot;
                if (z > sourcePot) z = sourcePot;
                return source[(z + border) * stride + x + border];
            }
        }
    }

    /// <summary>
    /// The per-node tessellation density map (<c>densityMapAtlas</c> in the terrain tessellation shader): u8,
    /// <c>pot/4 + 3</c> samples per side (35 for a 137 tile), border 1.
    /// <para>
    /// FIFA21_SOURCE shape: a curvature filter of the heights, <c>pow(|k|, exponent) * 255 * gain</c>, max-filtered.
    /// The per-terrain exponent and gain are not shipped; this uses the discrete Laplacian of the node's own samples,
    /// max over each density cell, <c>255 * sqrt(min(1, |laplacian| / <see cref="SaturationMetres"/>))</c>.
    /// </para>
    /// </summary>
    public static class DensityMapBuilder
    {
        public static double SaturationMetres = 0.25;

        public static byte[] BuildNode(ushort[] samples, int side, int border, int pot, int densitySide,
            int densityBorder, double metresPerUnit)
        {
            int dpot = densitySide - 2 * densityBorder - 1;
            int per = pot / dpot;
            var outBytes = new byte[densitySide * densitySide];
            for (int j = 0; j < densitySide; j++)
            {
                for (int i = 0; i < densitySide; i++)
                {
                    int cx = (i - densityBorder) * per, cz = (j - densityBorder) * per;
                    double worst = 0;
                    for (int dz = -per / 2; dz <= per / 2; dz++)
                        for (int dx = -per / 2; dx <= per / 2; dx++)
                        {
                            int x = Clamp(cx + dx + border, 1, side - 2), z = Clamp(cz + dz + border, 1, side - 2);
                            int c = samples[z * side + x];
                            int l = samples[z * side + x - 1], r = samples[z * side + x + 1];
                            int u = samples[(z - 1) * side + x], d = samples[(z + 1) * side + x];
                            if (c == 0 || l == 0 || r == 0 || u == 0 || d == 0) continue;
                            double lap = Math.Abs((l + r + u + d - 4.0 * c) * metresPerUnit);
                            if (lap > worst) worst = lap;
                        }
                    double v = 255.0 * Math.Sqrt(Math.Min(1.0, worst / SaturationMetres));
                    outBytes[j * densitySide + i] = (byte)Math.Round(v);
                }
            }
            return outBytes;

            static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        }
    }
}
