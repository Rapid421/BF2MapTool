#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\MinMaxLevels.cs (sha256:29024D4C5458C36E).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// A node's min/max pyramid, finest level first in memory (<c>Levels[0]</c>: <c>pot/16</c> cells per side of
    /// (min, max) pairs), written coarsest first with the min first in each pair (BFII_EXACT layout
    /// <c>ReverseLevel/MinFirst/InclusiveSharedEdge</c>).
    /// <para>
    /// Hierarchical (BFII_EXACT, 2026-09-23, 100% of nodes on Tatooine, Jakku and Naboo): a leaf's finest cells take
    /// the min/max of its own samples over 17x17 windows (cells share their edge sample); a node with data children
    /// takes each child's second level for that quadrant, so skipped top-level nodes and parents carry the extremes of
    /// the whole subtree; every coarser level merges 2x2. The single coarsest cell is the subtree range, which FIFA's
    /// writer also uses for the node's box. Hole markers (0) are skipped.
    /// </para>
    /// </summary>
    public sealed class MinMaxLevels
    {
        public ushort[][] Levels = Array.Empty<ushort[]>();

        /// <summary>The subtree's (min, max), or null when it holds no ground sample.</summary>
        public (ushort Lo, ushort Hi)? Range
        {
            get
            {
                var top = Levels[Levels.Length - 1];
                if (top[0] > top[1]) return null;
                return (top[0], top[1]);
            }
        }

        public static MinMaxLevels Build(ushort[]? samples, int side, int border, int pot, int depth, MinMaxLevels?[] children)
        {
            int cells0 = pot / 16;
            var m = new MinMaxLevels { Levels = new ushort[depth][] };
            var fin = new ushort[cells0 * cells0 * 2];
            int half = cells0 / 2;
            for (int cz = 0; cz < cells0; cz++)
            {
                for (int cx = 0; cx < cells0; cx++)
                {
                    int q = cz < half ? (cx < half ? 0 : 1) : (cx < half ? 3 : 2);
                    var child = children[q];
                    ushort lo = ushort.MaxValue, hi = 0;
                    if (child != null)
                    {
                        var cl = child.Levels[1];
                        int lx = cx < half ? cx : cx - half, lz = cz < half ? cz : cz - half;
                        lo = cl[(lz * half + lx) * 2];
                        hi = cl[(lz * half + lx) * 2 + 1];
                    }
                    else if (samples != null)
                    {
                        for (int z = 0; z <= 16; z++)
                            for (int x = 0; x <= 16; x++)
                            {
                                ushort v = samples[(border + cz * 16 + z) * side + border + cx * 16 + x];
                                if (v == 0) continue;
                                if (v < lo) lo = v;
                                if (v > hi) hi = v;
                            }
                    }
                    fin[(cz * cells0 + cx) * 2] = lo;
                    fin[(cz * cells0 + cx) * 2 + 1] = hi;
                }
            }
            m.Levels[0] = fin;
            for (int l = 1; l < depth; l++)
            {
                int c = cells0 >> l, p = cells0 >> (l - 1);
                var prev = m.Levels[l - 1];
                var lv = new ushort[c * c * 2];
                for (int z = 0; z < c; z++)
                    for (int x = 0; x < c; x++)
                    {
                        ushort lo = ushort.MaxValue, hi = 0;
                        for (int dz = 0; dz < 2; dz++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int i = ((2 * z + dz) * p + 2 * x + dx) * 2;
                                if (prev[i] < lo) lo = prev[i];
                                if (prev[i + 1] > hi) hi = prev[i + 1];
                            }
                        lv[(z * c + x) * 2] = lo;
                        lv[(z * c + x) * 2 + 1] = hi;
                    }
                m.Levels[l] = lv;
            }
            return m;
        }

        /// <summary>Coarsest level first, min first in each pair.</summary>
        public byte[] ToBytes()
        {
            int total = 0;
            foreach (var l in Levels) total += l.Length;
            var b = new byte[total * 2];
            int p = 0;
            for (int li = Levels.Length - 1; li >= 0; li--)
                foreach (ushort v in Levels[li]) { b[p++] = (byte)v; b[p++] = (byte)(v >> 8); }
            return b;
        }
    }
}
