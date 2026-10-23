#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\HeightSources.cs (sha256:8D3EB7E6910D56A6).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// The terrain's frame: a square world extent, the heightfield tile geometry and the deepest level.
    /// <para>
    /// Every heightfield sample of every node is one sample of a single global grid at the deepest level
    /// (<see cref="GridSize"/> per side), taken every <c>2^(MaxLevel - level)</c> samples. BFII_EXACT: a parent's
    /// samples equal its children's at every second position, and a node's border copies its neighbour, on all
    /// shipped terrains away from hole markers (SPEC §2). Positions are therefore integer indices into that grid;
    /// nothing is ever resampled between levels.
    /// </para>
    /// </summary>
    public sealed class TerrainFrame
    {
        public double MinX;
        public double MinZ;

        /// <summary>World side length in metres (the root node's square).</summary>
        public double Extent;

        /// <summary>Metres at sample value 65535 (header <c>worldSizeY</c>).</summary>
        public float WorldSizeY;

        /// <summary>137 (128 interior intervals) or 265 (256); both ship.</summary>
        public int TileSamplesPerSide = 137;

        public int BorderWidth = 4;

        /// <summary>Deepest heightfield level.</summary>
        public int MaxLevel;

        /// <summary>Interior intervals per node side: 128 for a 137 tile.</summary>
        public int Pot => TileSamplesPerSide - 2 * BorderWidth - 1;

        /// <summary>Global grid samples per side at <see cref="MaxLevel"/>.</summary>
        public long GridSize => ((long)Pot << MaxLevel) + 1;

        public double Spacing => Extent / (GridSize - 1);

        public double NodeSize(int level) => Extent / (1L << level);

        public double MetresPerUnit => WorldSizeY / 65535.0;

        /// <summary>World x of a global-grid index (may lie outside the world for border samples).</summary>
        public double WorldX(long gx) => MinX + gx * Spacing;

        public double WorldZ(long gz) => MinZ + gz * Spacing;

        public ushort ToSample(double metres)
        {
            double raw = Math.Floor(metres / MetresPerUnit + 0.5);   // half up, as DICE's interpolated samples
            if (raw < 1) raw = 1;                  // 0 is the hole marker
            if (raw > 65535) raw = 65535;
            return (ushort)raw;
        }

        public static int MaxLevelFor(double extent, double spacing, int pot)
        {
            int level = 0;
            while (extent / ((long)pot << level) > spacing * 1.0001 && level < 16) level++;
            return level;
        }

        public void Validate()
        {
            if (Extent <= 0) throw new ArgumentException("extent must be positive");
            if (WorldSizeY <= 0) throw new ArgumentException("worldSizeY must be positive");
            if (TileSamplesPerSide != 137 && TileSamplesPerSide != 265)
                throw new ArgumentException("tile must be 137 or 265 samples (the shipped geometries)");
            if (BorderWidth != 4) throw new ArgumentException("border width must be 4 (every shipped terrain)");
            if (MaxLevel < 2 || MaxLevel > 12) throw new ArgumentException("max level must be 2..12");
        }
    }

    /// <summary>A source of height samples on the frame's global grid.</summary>
    public interface IHeightSource
    {
        /// <summary>
        /// The sample at global index (gx, gz), clamped into the world. <paramref name="allowHole"/> false asks for
        /// ground even where the source has a hole (used on non-leaf levels, which keep ground in shipped data).
        /// </summary>
        ushort Sample(long gx, long gz, bool allowHole);

        /// <summary>False where the source has no data (a shipped tree's empty regions); the builder then clamps
        /// a border sample to the node's own edge.</summary>
        bool Covers(long gx, long gz);
    }

    /// <summary>
    /// A heightmap given as metres on a regular grid over the frame's world square (row = z, column = x).
    /// Sampled bilinearly at global-grid positions; NaN marks a hole.
    /// </summary>
    public sealed class DenseHeightSource : IHeightSource
    {
        private readonly TerrainFrame _frame;
        private readonly float[] _metres;
        private readonly int _size;

        public bool Covers(long gx, long gz) => true;

        public DenseHeightSource(TerrainFrame frame, float[] metres, int size)
        {
            if (metres is null) throw new ArgumentNullException(nameof(metres));
            if (size < 2 || metres.Length != (long)size * size)
                throw new ArgumentException("heightmap must hold size*size samples");
            _frame = frame; _metres = metres; _size = size;
        }

        public ushort Sample(long gx, long gz, bool allowHole)
        {
            long n = _frame.GridSize - 1;
            if (gx < 0) gx = 0; else if (gx > n) gx = n;
            if (gz < 0) gz = 0; else if (gz > n) gz = n;
            double fx = (double)gx / n * (_size - 1), fz = (double)gz / n * (_size - 1);
            int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz);
            if (x0 >= _size - 1) x0 = _size - 2;
            if (z0 >= _size - 1) z0 = _size - 2;
            double tx = fx - x0, tz = fz - z0;
            float a = _metres[(long)z0 * _size + x0], b = _metres[(long)z0 * _size + x0 + 1];
            float c = _metres[(long)(z0 + 1) * _size + x0], d = _metres[(long)(z0 + 1) * _size + x0 + 1];

            // Holes: the nearest source sample decides; ground elsewhere blends only non-hole corners.
            float nearest = (tx < 0.5 ? (tz < 0.5 ? a : c) : (tz < 0.5 ? b : d));
            if (allowHole && float.IsNaN(nearest)) return 0;

            double w = 0, h = 0;
            Add(a, (1 - tx) * (1 - tz)); Add(b, tx * (1 - tz)); Add(c, (1 - tx) * tz); Add(d, tx * tz);
            if (w <= 1e-12) return _frame.ToSample(FallbackGround(x0, z0));
            return _frame.ToSample(h / w);

            void Add(float v, double wt)
            {
                if (float.IsNaN(v) || wt <= 0) return;
                h += v * wt; w += wt;
            }
        }

        private double FallbackGround(int x0, int z0)
        {
            for (int r = 1; r < _size; r++)
            {
                double sum = 0; int count = 0;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue;
                        int x = x0 + dx, z = z0 + dz;
                        if (x < 0 || z < 0 || x >= _size || z >= _size) continue;
                        float v = _metres[(long)z * _size + x];
                        if (!float.IsNaN(v)) { sum += v; count++; }
                    }
                if (count > 0) return sum / count;
            }
            return _frame.MetresPerUnit;
        }
    }

    /// <summary>
    /// Heights read back out of a parsed heightfield tree: the deepest data node that holds the point, bilinear
    /// between its own samples when that node is coarser than the grid. Exact at every sample a node actually
    /// stores, which is what makes rebuilding a shipped terrain from itself a real test of the builder.
    /// </summary>
    public sealed class TreeHeightSource : IHeightSource
    {
        private readonly TerrainFrame _frame;
        private readonly HeightfieldTree _tree;
        private readonly Dictionary<HeightfieldNode, ushort[]> _cache = new Dictionary<HeightfieldNode, ushort[]>();

        public TreeHeightSource(TerrainFrame frame, HeightfieldTree tree)
        {
            _frame = frame; _tree = tree;
        }

        /// <summary>The frame a parsed tree implies: root box, tile geometry, deepest data level.</summary>
        public static TerrainFrame FrameOf(HeightfieldTree tree)
        {
            if (tree.Root is null) throw new ArgumentException("tree has no root");
            var box = tree.Root.BoundingBox;
            int maxLevel = 0;
            foreach (var n in tree.EnumerateNodes()) if (n.Payload != null && n.Id.Level > maxLevel) maxLevel = n.Id.Level;
            return new TerrainFrame
            {
                MinX = box.MinX.Value, MinZ = box.MinZ.Value, Extent = box.MaxX.Value - box.MinX.Value,
                WorldSizeY = tree.WorldSizeY.Value, TileSamplesPerSide = (int)tree.NodeSamplesPerSide,
                BorderWidth = (int)tree.NodeBorderWidth, MaxLevel = maxLevel,
            };
        }

        public bool Covers(long gx, long gz) => Deepest(gx, gz) != null;

        private HeightfieldNode? Deepest(long gx, long gz)
        {
            long n = _frame.GridSize - 1;
            if (gx < 0) gx = 0; else if (gx > n) gx = n;
            if (gz < 0) gz = 0; else if (gz > n) gz = n;
            HeightfieldNode? best = null;
            var node = _tree.Root;
            int level = 0;
            while (node != null)
            {
                if (node.Payload != null) best = node;
                if (!node.HasChildren || node.Children.Count != 4) break;
                long half = (long)_frame.Pot << (_frame.MaxLevel - level - 1);
                long lx = gx - (long)node.Id.IndexX * half * 2, lz = gz - (long)node.Id.IndexY * half * 2;
                int cx = lx >= half ? 1 : 0, cz = lz >= half ? 1 : 0;
                node = node.Children[cz == 0 ? (cx == 0 ? 0 : 1) : (cx == 0 ? 3 : 2)];
                level++;
            }
            return best;
        }

        public ushort Sample(long gx, long gz, bool allowHole)
        {
            long n = _frame.GridSize - 1;
            if (gx < 0) gx = 0; else if (gx > n) gx = n;
            if (gz < 0) gz = 0; else if (gz > n) gz = n;

            // Deepest data node containing the point.
            HeightfieldNode? best = null;
            var node = _tree.Root;
            int level = 0;
            while (node != null)
            {
                if (node.Payload != null) best = node;
                if (!node.HasChildren || node.Children.Count != 4) break;
                int shift = _frame.MaxLevel - level - 1;
                long half = (long)_frame.Pot << shift;           // child side in grid samples
                long lx = gx - (long)node.Id.IndexX * half * 2, lz = gz - (long)node.Id.IndexY * half * 2;
                int cx = lx >= half ? 1 : 0, cz = lz >= half ? 1 : 0;
                if (lx >= half * 2) cx = 1;
                if (lz >= half * 2) cz = 1;
                int ci = cz == 0 ? (cx == 0 ? 0 : 1) : (cx == 0 ? 3 : 2);
                node = node.Children[ci];
                level++;
                if (node.Id.Level != level) break;
            }
            if (best is null) return 1;

            ushort[] s = Samples(best);
            int side = _frame.TileSamplesPerSide, b = _frame.BorderWidth;
            long step = 1L << (_frame.MaxLevel - best.Id.Level);
            long ox = (long)best.Id.IndexX * _frame.Pot * step, oz = (long)best.Id.IndexY * _frame.Pot * step;
            double fx = (double)(gx - ox) / step, fz = (double)(gz - oz) / step;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            if (ix >= _frame.Pot) ix = _frame.Pot - 1;
            if (iz >= _frame.Pot) iz = _frame.Pot - 1;
            double tx = fx - ix, tz = fz - iz;
            ushort c00 = s[(iz + b) * side + ix + b], c10 = s[(iz + b) * side + ix + 1 + b];
            ushort c01 = s[(iz + 1 + b) * side + ix + b], c11 = s[(iz + 1 + b) * side + ix + 1 + b];
            if (tx == 0 && tz == 0)
            {
                if (c00 != 0 || allowHole) return c00;
            }
            if (allowHole)
            {
                ushort nearest = tx < 0.5 ? (tz < 0.5 ? c00 : c01) : (tz < 0.5 ? c10 : c11);
                if (nearest == 0) return 0;
            }
            double w = 0, h = 0;
            Add(c00, (1 - tx) * (1 - tz)); Add(c10, tx * (1 - tz)); Add(c01, (1 - tx) * tz); Add(c11, tx * tz);
            if (w <= 1e-12) return 1;
            // Round half up: matches DICE's border samples interpolated from a coarser node on 99.94% of 197,280.
            double v = Math.Floor(h / w + 0.5);
            return (ushort)(v < 1 ? 1 : v > 65535 ? 65535 : v);

            void Add(ushort v, double wt)
            {
                if (v == 0 || wt <= 0) return;
                h += v * wt; w += wt;
            }
        }

        private ushort[] Samples(HeightfieldNode n)
        {
            if (!_cache.TryGetValue(n, out var s))
            {
                var raw = n.Payload!.AtlasData;
                s = new ushort[raw.Length / 2];
                for (int i = 0; i < s.Length; i++) s[i] = (ushort)(raw[2 * i] | (raw[2 * i + 1] << 8));
                _cache[n] = s;
            }
            return s;
        }
    }
}
