#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\MaskEditor.cs (sha256:D0A5D38E6F19414D).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Linq;
using Bfii.TerrainDerive;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// Edits a shipped mask tree in place: paints one layer over a region at every level (max with what is there), adding
    /// subtiles where the layer had none, and re-derives every count the format carries (subtile counts, tile bytes,
    /// recursive non-trivial counts, header totals, atlas size). Everything outside the region stays byte-identical.
    /// Used to cut the host's hole layer into a terrain whose heightfield gained holes (SPEC §3, §2 holes).
    /// </summary>
    public static class MaskEditor
    {
        public sealed class Report
        {
            public int SubtilesAdded, SubtilesChanged, NodesTouched;
        }

        /// <param name="alphaAt">alpha (0..255) of the painted layer for a texel of side <c>texel</c> centred at (x, z)</param>
        /// <param name="region">world rectangle outside which nothing is touched (x = MinX..MaxX, z = MinY..MaxY)</param>
        public static Report Paint(MaskTree tree, int layer, Func<double, double, double, byte> alphaAt, AxisAlignedBox2 region)
        {
            if (tree.Root == null) throw new ArgumentException("mask tree has no root");
            if (layer < 0 || layer > 254) throw new ArgumentOutOfRangeException(nameof(layer));
            var rep = new Report();
            int T = (int)tree.TileSamplesPerSide;
            int B = (T - Pot(T)) / 2;
            int pot = T - 2 * B;
            int perSide = (int)tree.TilesPerNodeSide;
            int levels = (int)tree.CoverageGridLevelCount;
            int stride = MaskStride.ForLevel(tree.FittedStrideLevel);
            var cells = MaskBuilder.SubcellOrder((int)tree.SubtileRelativeLevel);
            double x0 = tree.TreeCoverage.MinX.Value, z0 = tree.TreeCoverage.MinY.Value;
            double ext = tree.TreeCoverage.MaxX.Value - x0;
            double rx0 = region.MinX.Value, rz0 = region.MinY.Value, rx1 = region.MaxX.Value, rz1 = region.MaxY.Value;

            foreach (var node in tree.EnumerateNodes())
            {
                if (!node.BodyPresent || node.Subtiles.Count == 0) continue;
                double size = ext / (1 << node.Id.Level), sub = size / perSide, texel = sub / pot;
                double nx = x0 + node.Id.IndexX * size, nz = z0 + node.Id.IndexY * size;
                if (nx >= rx1 || nx + size <= rx0 || nz >= rz1 || nz + size <= rz0) continue;
                bool leaf = !node.HasChildren;
                bool touched = false;
                foreach (var (cx, cz) in cells)
                {
                    double sx0 = nx + cx * sub, sz0 = nz + cz * sub;
                    if (sx0 >= rx1 || sx0 + sub <= rx0 || sz0 >= rz1 || sz0 + sub <= rz0) continue;
                    var paint = new byte[T * T];
                    bool any = false;
                    for (int j = 0; j < T; j++)
                    {
                        double wz = sz0 + (j - B + 0.5) * texel;
                        for (int i = 0; i < T; i++)
                        {
                            double wx = sx0 + (i - B + 0.5) * texel;
                            if (wx < rx0 - texel || wx > rx1 + texel || wz < rz0 - texel || wz > rz1 + texel) continue;
                            byte a = alphaAt(wx, wz, texel);
                            paint[j * T + i] = a;
                            if (a != 0) any = true;
                        }
                    }
                    if (!any) continue;

                    // the subtile of this layer over this subcell, if there is one
                    int idx = node.Subtiles.FindIndex(s => s.TerrainLayerIndex == layer && Near(s.WorldCoverage.MinX.Value, sx0, sub)
                                                           && Near(s.WorldCoverage.MinY.Value, sz0, sub));
                    if (idx >= 0)
                    {
                        var st = node.Subtiles[idx];
                        if (st.IsTrivial) continue;                        // already opaque everywhere
                        int t = TileIndex(node, idx);
                        var tile = node.Tiles[t];
                        bool changed = false;
                        for (int k = 0; k < tile.Length; k++)
                            if (paint[k] > tile[k]) { tile[k] = paint[k]; changed = true; }
                        if (!changed) continue;
                        st.HighResolutionCoverageGridStack = MaskBuilder.CoverageGrid(tile, T, B, levels, stride);
                        rep.SubtilesChanged++;
                        touched = true;
                    }
                    else
                    {
                        var st = new MaskSubtile
                        {
                            TerrainLayerIndex = (byte)layer,
                            WorldCoverage = new AxisAlignedBox2
                            {
                                MinX = F32.FromValue((float)sx0), MinY = F32.FromValue((float)sz0),
                                MaxX = F32.FromValue((float)(sx0 + sub)), MaxY = F32.FromValue((float)(sz0 + sub)),
                            },
                            IsLeafRaw = (byte)(leaf ? 1 : 0),
                            MaskDataFullRaw = 0,
                            HighResolutionCoverageGridStack = MaskBuilder.CoverageGrid(paint, T, B, levels, stride),
                        };
                        // subtiles run by layer, then by subcell in quadtree order
                        int cellRank = cells.IndexOf((cx, cz));
                        int at = 0;
                        while (at < node.Subtiles.Count && Before(node.Subtiles[at], layer, cellRank, cells, nx, nz, sub)) at++;
                        int tileAt = 0;
                        for (int k = 0; k < at; k++) if (!node.Subtiles[k].IsTrivial) tileAt++;
                        node.Subtiles.Insert(at, st);
                        node.Tiles.Insert(tileAt, paint);
                        rep.SubtilesAdded++;
                        touched = true;
                    }
                }
                if (!touched) continue;
                rep.NodesTouched++;
                node.SubtileCount = checked((ushort)node.Subtiles.Count);
                node.NonTrivialSubtileCount = checked((ushort)node.Tiles.Count);
                node.TileDataBytes = (uint)(node.Tiles.Count * T * T);
            }

            // recursive non-trivial counts and the header totals
            Recount(tree.Root);
            var all = tree.EnumerateNodes().ToList();
            tree.SubtileCount = (uint)all.Sum(n => n.Subtiles.Count);
            tree.PersistentNonTrivialSubtileCount = (uint)all.Where(n => n.Persistent).Sum(n => n.Tiles.Count);
            int cols = (int)(tree.ResourceAtlasSampleCountX / (uint)T);
            int need = (int)tree.PersistentNonTrivialSubtileCount + 2;               // + the white and black tiles
            int rows = (need + cols - 1) / cols;
            uint y = (uint)Math.Max(128, ((rows * T + 127) / 128) * 128);
            if (y > tree.ResourceAtlasSampleCountY) tree.ResourceAtlasSampleCountY = y;
            return rep;
        }

        private static int Recount(MaskNode n)
        {
            int total = n.Persistent ? n.Tiles.Count : 0;
            foreach (var c in n.Children) total += Recount(c);
            if (n.BodyPresent || n.Subtiles.Count > 0) n.NonTrivialPersistentSubtileCountRecursive = (ushort)Math.Min(total, ushort.MaxValue);
            return total;
        }

        private static bool Before(MaskSubtile s, int layer, int cellRank, List<(int X, int Z)> cells, double nx, double nz, double sub)
        {
            if (s.TerrainLayerIndex != layer) return s.TerrainLayerIndex < layer;
            int cx = (int)Math.Round((s.WorldCoverage.MinX.Value - nx) / sub), cz = (int)Math.Round((s.WorldCoverage.MinY.Value - nz) / sub);
            return cells.IndexOf((cx, cz)) < cellRank;
        }

        private static int TileIndex(MaskNode node, int subtileIndex)
        {
            int t = 0;
            for (int k = 0; k < subtileIndex; k++) if (!node.Subtiles[k].IsTrivial) t++;
            return t;
        }

        private static bool Near(double a, double b, double size) => Math.Abs(a - b) < size * 1e-3;

        private static int Pot(int v) { int p = 1; while (p * 2 <= v) p *= 2; return p; }
    }

    /// <summary>
    /// Heights for an edit of a shipped terrain: inside the heightmap's rectangle the heightmap, everywhere else the host's
    /// own samples (exact). The frame is the host's.
    /// </summary>
    public sealed class RegionOverHostHeightSource : IHeightSource
    {
        private readonly TerrainFrame _frame;
        private readonly TreeHeightSource _host;
        private readonly RasterHeightSource _region;
        private readonly HeightmapPlacement _rect;

        public RegionOverHostHeightSource(TerrainFrame hostFrame, TreeHeightSource host, RasterHeightSource region, HeightmapPlacement rect)
        {
            _frame = hostFrame; _host = host; _region = region; _rect = rect;
        }

        public bool Covers(long gx, long gz) => Inside(gx, gz) || _host.Covers(gx, gz);

        public ushort Sample(long gx, long gz, bool allowHole) =>
            Inside(gx, gz) ? _region.Sample(gx, gz, allowHole) : _host.Sample(gx, gz, allowHole);

        private bool Inside(long gx, long gz)
        {
            double x = _frame.WorldX(gx), z = _frame.WorldZ(gz);
            const double eps = 1e-6;
            return x >= _rect.MinX - eps && x <= _rect.MaxX + eps && z >= _rect.MinZ - eps && z <= _rect.MaxZ + eps;
        }
    }

    public static class HoleLayer
    {
        /// <summary>
        /// The renderer's layer count and hole layer from the VisualTerrain resource (FIFA21_SOURCE VisualTerrain.cpp:1447,
        /// TerrainPipeline.cpp:1441; BFII_EXACT on the 39 shipped): after the global settings, <c>0xFFFFFFFF</c> then
        /// <c>u32 layerCount</c>, then per layer <c>u32 meshScatteringTypeCount</c> + the types + <c>u8 displacementEnabled</c> +
        /// <c>u8 holesEnabled</c> (= !physicsEnable). On every shipped terrain with a hole layer it is the last layer, so
        /// the final byte decides. Tatooine: 11 layers (TerrainData lists 17), no hole layer; Hoth: 9, layer 8.
        /// </summary>
        public static bool FromVisualTerrain(byte[] visual, out int layerCount, out int? holeLayer)
        {
            layerCount = 0; holeLayer = null;
            if (visual == null || visual.Length < 0x100) return false;
            // the count sits at 0xF5 on 38 of 39 shipped (after an int that is -1 on most, 15 on Geonosis, 11 on A1 M1END);
            // Crait's globals are longer and put it after the next 0xFFFFFFFF
            int n = U32(visual, 0xF5);
            if (n < 1 || n > 64)
            {
                n = 0;
                for (int i = 0xF0; i + 8 <= Math.Min(visual.Length, 0x200) && n == 0; i++)
                {
                    if (visual[i] != 0xFF || visual[i + 1] != 0xFF || visual[i + 2] != 0xFF || visual[i + 3] != 0xFF) continue;
                    int c = U32(visual, i + 4);
                    if (c >= 1 && c <= 64 && i + 4 != 0xF5) n = c;
                }
                if (n == 0) return false;
            }
            layerCount = n;
            // the per-layer stream ends the resource; the last layer's final byte is its holesEnabled
            if (visual[visual.Length - 1] == 1) holeLayer = n - 1;
            return true;
        }

        private static int U32(byte[] b, int p) => p + 4 <= b.Length ? b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24) : -1;

        /// <summary>
        /// The host's hole layer, from its own holes: the layer whose alpha is highest at the host's hole samples relative
        /// to ordinary ground. The engine's rule is "the first layer with physics disabled" (VisualTerrain holesEnabled);
        /// on Endor 01, Hoth 01 and Takodana 01 that is the layer this finds (their last layer). Null without host holes.
        /// </summary>
        public static int? FromHostHoles(TerrainStreamingTreeResource host, out string note)
        {
            note = "";
            var hf = host.Heightfield;
            if (hf == null || host.Mask == null) { note = "no heightfield or mask"; return null; }
            var frame = TreeHeightSource.FrameOf(hf);
            var reader = new MaskReader(host.Mask);
            int side = (int)hf.NodeSamplesPerSide, b = (int)hf.NodeBorderWidth, pot = side - 2 * b - 1;
            var holes = new List<(double X, double Z)>();
            var ground = new List<(double X, double Z)>();
            var rnd = new Random(1);
            foreach (var n in hf.EnumerateNodes())
            {
                if (n.Payload == null || n.ContainsHolesRaw == 0) continue;
                var s = SampleBytes.ToSamples(n.Payload.AtlasData);
                double size = frame.NodeSize(n.Id.Level), step = size / pot;
                for (int j = b; j <= b + pot; j++)
                    for (int i = b; i <= b + pot; i++)
                    {
                        double x = frame.MinX + n.Id.IndexX * size + (i - b) * step, z = frame.MinZ + n.Id.IndexY * size + (j - b) * step;
                        if (s[j * side + i] == 0) { if (holes.Count < 4000 && rnd.Next(4) == 0) holes.Add((x, z)); }
                        else if (ground.Count < 4000 && rnd.Next(16) == 0) ground.Add((x, z));
                    }
            }
            if (holes.Count == 0) { note = "the host has no holes"; return null; }
            int level = reader.DeepestLevel;
            var layers = reader.Layers;
            double best = double.MinValue; int bestLayer = -1; double bh = 0, bg = 0;
            foreach (int l in layers)
            {
                if (l == 0) continue;
                double h = holes.Average(p => (double)reader.AlphaAt(l, p.X, p.Z, level));
                double g = ground.Count == 0 ? 0 : ground.Average(p => (double)reader.AlphaAt(l, p.X, p.Z, level));
                if (h - g > best) { best = h - g; bestLayer = l; bh = h; bg = g; }
            }
            note = $"layer {bestLayer}: mean alpha {bh:0} on {holes.Count} host hole samples vs {bg:0} on ground next to them";
            return bestLayer >= 0 && bh > 128 ? bestLayer : (int?)null;
        }
    }
}
