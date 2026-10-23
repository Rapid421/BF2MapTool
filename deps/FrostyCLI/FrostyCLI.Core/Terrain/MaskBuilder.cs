#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\MaskBuilder.cs (sha256:4D001CE7E4BFB2BE).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Linq;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// Terrain layer masks as the renderer blends them: an ordered stack. Layer 0 is the base; every later layer is
    /// laid over what is below with its own alpha (0..255). BFII_EXACT that shipped masks are not normalised: where two
    /// or more masked layers overlap, their weights sum above 255 on 20-36% of texels (Tatooine, Naboo, Jakku), up to 1,940.
    /// </summary>
    public interface ILayerSource
    {
        int LayerCount { get; }

        /// <summary>Alpha of <paramref name="layer"/> over a texel of side <paramref name="texel"/> metres centred at (x, z).</summary>
        byte Alpha(int layer, double x, double z, double texel);
    }

    /// <summary>
    /// Per-layer alpha grids over the world square (row = z, column = x), each with a box mip chain so a coarse texel
    /// averages what it covers. Build it from normalised splat weights with <see cref="FromSplatWeights"/>.
    /// </summary>
    public sealed class DenseLayerSource : ILayerSource
    {
        private readonly double _minX, _minZ, _extent;
        private readonly List<List<(byte[] Data, int Size)>> _mips = new List<List<(byte[], int)>>();

        public int LayerCount => _mips.Count;

        public DenseLayerSource(double minX, double minZ, double extent, IList<byte[]> alphas, int size)
        {
            _minX = minX; _minZ = minZ; _extent = extent;
            foreach (var a in alphas)
            {
                if (a.Length != (long)size * size) throw new ArgumentException("every layer grid must be size*size");
                var chain = new List<(byte[], int)> { (a, size) };
                int s = size;
                byte[] cur = a;
                while (s > 1)
                {
                    int n = (s + 1) / 2;
                    var next = new byte[n * n];
                    for (int z = 0; z < n; z++)
                        for (int x = 0; x < n; x++)
                        {
                            int sum = 0, cnt = 0;
                            for (int dz = 0; dz < 2; dz++)
                                for (int dx = 0; dx < 2; dx++)
                                {
                                    int sx = 2 * x + dx, sz = 2 * z + dz;
                                    if (sx >= s || sz >= s) continue;
                                    sum += cur[sz * s + sx]; cnt++;
                                }
                            next[z * n + x] = (byte)((sum + cnt / 2) / cnt);
                        }
                    chain.Add((next, n));
                    cur = next; s = n;
                }
                _mips.Add(chain);
            }
        }

        /// <summary>
        /// Normalised splat weights (per texel they sum to any positive total) -> stack alphas that reproduce the same
        /// blend under the renderer's ordered lerp: <c>alpha_0 = 255</c>, <c>alpha_i = w_i / (w_0 + ... + w_i)</c>.
        /// </summary>
        public static List<byte[]> FromSplatWeights(IList<byte[]> weights)
        {
            int k = weights.Count, n = weights[0].Length;
            var outList = new List<byte[]>();
            for (int i = 0; i < k; i++) outList.Add(new byte[n]);
            for (int t = 0; t < n; t++)
            {
                int acc = 0;
                for (int i = 0; i < k; i++)
                {
                    int w = weights[i][t];
                    acc += w;
                    outList[i][t] = i == 0 ? (byte)255 : acc == 0 ? (byte)0 : (byte)Math.Min(255, (w * 255 + acc / 2) / acc);
                }
            }
            return outList;
        }

        public byte Alpha(int layer, double x, double z, double texel)
        {
            var chain = _mips[layer];
            int size0 = chain[0].Size;
            double texel0 = _extent / size0;
            int level = 0;
            while (level + 1 < chain.Count && texel0 * (1 << (level + 1)) <= texel * 1.0001) level++;
            var (data, size) = chain[level];
            double fx = (x - _minX) / _extent * size - 0.5, fz = (z - _minZ) / _extent * size - 0.5;
            if (fx < 0) fx = 0; else if (fx > size - 1) fx = size - 1;
            if (fz < 0) fz = 0; else if (fz > size - 1) fz = size - 1;
            int x0 = (int)fx, z0 = (int)fz;
            int x1 = Math.Min(x0 + 1, size - 1), z1 = Math.Min(z0 + 1, size - 1);
            double tx = fx - x0, tz = fz - z0;
            double v = data[z0 * size + x0] * (1 - tx) * (1 - tz) + data[z0 * size + x1] * tx * (1 - tz)
                     + data[z1 * size + x0] * (1 - tx) * tz + data[z1 * size + x1] * tx * tz;
            return (byte)Math.Floor(v + 0.5);
        }
    }

    /// <summary>One layer everywhere at full weight: the simplest valid mask (every patch's layer set is {0}).</summary>
    public sealed class BaseOnlyLayerSource : ILayerSource
    {
        public int LayerCount => 1;
        public byte Alpha(int layer, double x, double z, double texel) => 255;
    }

    public sealed class MaskBuildOptions
    {
        /// <summary>Deepest mask node level. A level-l node's subtile texel is <c>extent / 2^(l+8)</c> metres.</summary>
        public int MaxLevel = 4;

        public int TileSamplesPerSide = 72;          // 64 + 2*4, every shipped terrain
        public int TileBorder = 4;
        public int SubtileRelativeLevel = 2;         // 4 x 4 subcells per node (38 of 39 shipped terrains)
        public int CoverageGridLevels = 4;           // 35 of 39 shipped terrains
        public int BlurrinessLog2 = 1;
        public int AtlasSampleCountX = 4096;
        public double AtlasHeadroom = 0.25;

        /// <summary>
        /// Budget on tiled (non-full) subtiles; the deepest level is reduced until the build fits. 8,090 is the most
        /// any shipped terrain carries (Starkiller, atlas 8192x5376).
        /// </summary>
        public int MaxTiledSubtiles = 8090;

        /// <summary>Tallest shipped mask atlas (Takodana 4096x7424); past it the atlas goes 8192 wide, as Starkiller's.</summary>
        public int MaxAtlasRows = 7424;

        /// <summary>
        /// Adaptive depth: a node below <see cref="MaxLevel"/> gets children only where some layer's alpha at half its
        /// texel differs from its own texels by more than this (alpha units, 0..255). 0 = every node to MaxLevel.
        /// Shipped masks are adaptive (Tatooine: 16 of 64 L3 and 24 of 256 L4 nodes, leaves at L2, L3 and L4).
        /// </summary>
        public int RefineTolerance = 0;
    }

    public sealed class MaskBuildReport
    {
        public int Nodes, Subtiles, TiledSubtiles, FullSubtiles, MaxLevelUsed;
        public string Atlas = "";
        public SortedSet<string> LayerSets = new SortedSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds the mask raster tree (tag 1) from layer alphas (SPEC §3): every node from level 0 to
    /// <see cref="MaskBuildOptions.MaxLevel"/>; per node, per subcell, one subtile per layer with weight there;
    /// a subtile with 255 everywhere is "full" and carries no tile; subtiles at the deepest level are leaves (the
    /// runtime takes leaf subtiles on the way down and any subtile at its target level). Tiles are 64 texel-centred
    /// samples over the subcell plus a 4-texel border sampled beyond it (clamped at the world edge). Coverage grids use
    /// the widened-window rule that is a superset of every shipped grid.
    /// </summary>
    public static class MaskBuilder
    {
        public static MaskTree Build(ILayerSource source, TerrainFrame frame, MaskBuildOptions options, out MaskBuildReport report)
        {
            var rep = new MaskBuildReport();
            report = rep;
            int maxLevel = options.MaxLevel;
            MaskTree tree;
            while (true)
            {
                tree = BuildAt(source, frame, options, maxLevel, rep);
                if (rep.TiledSubtiles <= options.MaxTiledSubtiles || maxLevel == 0) break;
                maxLevel--;
                rep.LayerSets.Clear();
            }
            rep.MaxLevelUsed = maxLevel;
            return tree;
        }

        private static MaskTree BuildAt(ILayerSource source, TerrainFrame frame, MaskBuildOptions o, int maxLevel, MaskBuildReport rep)
        {
            int T = o.TileSamplesPerSide, B = o.TileBorder, pot = T - 2 * B;
            int perSide = 1 << o.SubtileRelativeLevel;
            int stride = MaskStride.ForLevel(o.CoverageGridLevels);
            var t = new MaskTree
            {
                TileSamplesPerSide = (uint)T,
                TilesPerNodeSide = (uint)perSide,
                SubtileRelativeLevel = (uint)o.SubtileRelativeLevel,
                BlurrinessLog2 = o.BlurrinessLog2,
                TreeCoverage = new AxisAlignedBox2
                {
                    MinX = F32.FromValue((float)frame.MinX), MinY = F32.FromValue((float)frame.MinZ),
                    MaxX = F32.FromValue((float)(frame.MinX + frame.Extent)), MaxY = F32.FromValue((float)(frame.MinZ + frame.Extent)),
                },
                CoverageGridLevelCount = (uint)o.CoverageGridLevels,
                FittedStrideLevel = o.CoverageGridLevels,
            };

            rep.Nodes = rep.Subtiles = rep.TiledSubtiles = rep.FullSubtiles = 0;
            t.Root = Node(new QuadtreeNodeId(0, 0, 0));
            t.NodeCount = (uint)rep.Nodes;
            t.SubtileCount = (uint)rep.Subtiles;
            t.PersistentNonTrivialSubtileCount = (uint)rep.TiledSubtiles;

            int wanted = (int)Math.Ceiling(rep.TiledSubtiles * (1 + o.AtlasHeadroom)) + 1;
            int x = o.AtlasSampleCountX, cols = x / T;
            int rows = (wanted + cols - 1) / cols;
            if (rows * T > o.MaxAtlasRows && x < 8192) { x = 8192; cols = x / T; rows = (wanted + cols - 1) / cols; }
            int y = Math.Max(128, ((rows * T + 127) / 128) * 128);
            t.ResourceAtlasSampleCountX = (uint)x;
            t.ResourceAtlasSampleCountY = (uint)y;
            rep.Atlas = x + "x" + y + " (" + cols * (y / T) + " tiles for " + rep.TiledSubtiles + ")";
            return t;

            MaskNode Node(QuadtreeNodeId id)
            {
                var n = new MaskNode { Id = id };
                rep.Nodes++;
                double size = frame.NodeSize(id.Level), sub = size / perSide, texel = sub / pot;
                double nx = frame.MinX + id.IndexX * size, nz = frame.MinZ + id.IndexY * size;
                bool leaf = id.Level >= maxLevel;
                var tiledAt = new List<(int Layer, int Cx, int Cz, byte[] Tile)>();

                // Subcells in quadtree order (two levels of (0,0) (1,0) (1,1) (0,1)), subtiles grouped by layer.
                var cells = SubcellOrder(o.SubtileRelativeLevel);
                var tiled = new List<byte[]>();
                var layersHere = new SortedSet<int>();
                for (int layer = 0; layer < source.LayerCount; layer++)
                {
                    foreach (var (cx, cz) in cells)
                    {
                        double sx0 = nx + cx * sub, sz0 = nz + cz * sub;
                        var tile = new byte[T * T];
                        bool any = false, full = true;
                        for (int j = 0; j < T; j++)
                        {
                            double wz = sz0 + (j - B + 0.5) * texel;
                            if (wz < frame.MinZ) wz = frame.MinZ + texel * 0.5;
                            if (wz > frame.MinZ + frame.Extent) wz = frame.MinZ + frame.Extent - texel * 0.5;
                            for (int i = 0; i < T; i++)
                            {
                                double wx = sx0 + (i - B + 0.5) * texel;
                                if (wx < frame.MinX) wx = frame.MinX + texel * 0.5;
                                if (wx > frame.MinX + frame.Extent) wx = frame.MinX + frame.Extent - texel * 0.5;
                                byte a = source.Alpha(layer, wx, wz, texel);
                                tile[j * T + i] = a;
                                if (a != 0) any = true;
                                if (a != 255) full = false;
                            }
                        }
                        if (!any) continue;
                        var s = new MaskSubtile
                        {
                            TerrainLayerIndex = checked((byte)layer),
                            WorldCoverage = new AxisAlignedBox2
                            {
                                MinX = F32.FromValue((float)sx0), MinY = F32.FromValue((float)sz0),
                                MaxX = F32.FromValue((float)(sx0 + sub)), MaxY = F32.FromValue((float)(sz0 + sub)),
                            },
                            IsLeafRaw = (byte)(leaf ? 1 : 0),
                            MaskDataFullRaw = (byte)(full ? 1 : 0),
                            HighResolutionCoverageGridStack = full ? FullGrid(o.CoverageGridLevels, stride)
                                                                   : CoverageGrid(tile, T, B, o.CoverageGridLevels, stride),
                        };
                        n.Subtiles.Add(s);
                        layersHere.Add(layer);
                        if (!full) { tiled.Add(tile); tiledAt.Add((layer, cx, cz, tile)); }
                    }
                }

                // adaptive depth: stop here unless a tiled layer has detail finer than this node's texel
                if (!leaf && o.RefineTolerance > 0 && !HasFinerDetail())
                {
                    leaf = true;
                    foreach (var st in n.Subtiles) st.IsLeafRaw = 1;
                }

                n.SubtileCount = checked((ushort)n.Subtiles.Count);
                n.NonTrivialSubtileCount = checked((ushort)tiled.Count);
                rep.Subtiles += n.Subtiles.Count;
                rep.TiledSubtiles += tiled.Count;
                rep.FullSubtiles += n.Subtiles.Count - tiled.Count;
                if (layersHere.Count > 0) rep.LayerSets.Add("{" + string.Join(",", layersHere) + "}");
                if (n.Subtiles.Count == 0) return n;      // nothing further for an empty node

                n.BodyPresent = true;
                n.PersistentRaw = 1;
                n.TileDataBytes = (uint)(tiled.Count * T * T);
                n.Tiles.AddRange(tiled);
                int recursive = tiled.Count;
                if (!leaf)
                {
                    n.HasChildrenRaw = 1;
                    for (int ci = 0; ci < 4; ci++)
                    {
                        var c = Node(id.Child(ci));
                        n.Children.Add(c);
                        recursive += c.NonTrivialPersistentSubtileCountRecursive;
                    }
                }
                n.NonTrivialPersistentSubtileCountRecursive = checked((ushort)Math.Min(recursive, ushort.MaxValue));
                return n;

                // Full and empty subtiles have no finer detail (a box average of 255 or 0 is 255 or 0 everywhere
                // under it); a tiled one does when an alpha at half the texel is off what the renderer would show if
                // the node stopped here -- its tile sampled bilinearly, border included -- by more than the tolerance.
                bool HasFinerDetail()
                {
                    double half = texel / 2;
                    foreach (var (layer, cx, cz, tile) in tiledAt)
                    {
                        double sx0 = nx + cx * sub, sz0 = nz + cz * sub;
                        for (int fj = 0; fj < 2 * pot; fj++)
                        {
                            double wz = sz0 + (fj + 0.5) * half;
                            double fz = (fj + 0.5) / 2 - 0.5 + B;
                            int iz = (int)fz, iz1 = Math.Min(iz + 1, T - 1);
                            double tz = fz - iz;
                            for (int fi = 0; fi < 2 * pot; fi++)
                            {
                                double wx = sx0 + (fi + 0.5) * half;
                                double fx = (fi + 0.5) / 2 - 0.5 + B;
                                int ix = (int)fx, ix1 = Math.Min(ix + 1, T - 1);
                                double tx = fx - ix;
                                double shown = tile[iz * T + ix] * (1 - tx) * (1 - tz) + tile[iz * T + ix1] * tx * (1 - tz)
                                             + tile[iz1 * T + ix] * (1 - tx) * tz + tile[iz1 * T + ix1] * tx * tz;
                                if (Math.Abs(source.Alpha(layer, wx, wz, half) - shown) > o.RefineTolerance) return true;
                            }
                        }
                    }
                    return false;
                }
            }
        }

        /// <summary>Subcells of a node in the order shipped subtiles use: quadtree order at every sub-level.</summary>
        public static List<(int X, int Z)> SubcellOrder(int relativeLevel)
        {
            var list = new List<(int, int)> { (0, 0) };
            for (int l = 0; l < relativeLevel; l++)
            {
                var next = new List<(int, int)>();
                foreach (var (x, z) in list)
                    for (int ci = 0; ci < 4; ci++)
                        next.Add((2 * x + QuadtreeNodeId.ChildOffsetX[ci], 2 * z + QuadtreeNodeId.ChildOffsetY[ci]));
                list = next;
            }
            return list;
        }

        private static byte[] FullGrid(int levels, int stride)
        {
            var g = new byte[stride];
            int bits = (int)((Math.Pow(4, levels) - 1) / 3);
            for (int b = 0; b < bits; b++) g[b >> 3] |= (byte)(1 << (b & 7));
            return g;
        }

        /// <summary>
        /// Levels coarse to fine (1x1 .. 2^(L-1) per side), row-major, LSB first; a cell is set when any texel of its
        /// window widened by the tile border is non-zero. BFII_EXACT as a superset on 6,451/6,451 shipped subtiles.
        /// </summary>
        public static byte[] CoverageGrid(byte[] tile, int T, int border, int levels, int stride)
        {
            var g = new byte[stride];
            int pot = T - 2 * border;
            int bit = 0;
            for (int l = 0; l < levels; l++)
            {
                int n = 1 << l;
                double cell = (double)pot / n;
                for (int r = 0; r < n; r++)
                {
                    int z0 = Math.Max(0, border + (int)Math.Floor(r * cell) - border);
                    int z1 = Math.Min(T, border + (int)Math.Ceiling((r + 1) * cell) + 1 + border);
                    for (int c = 0; c < n; c++, bit++)
                    {
                        int x0 = Math.Max(0, border + (int)Math.Floor(c * cell) - border);
                        int x1 = Math.Min(T, border + (int)Math.Ceiling((c + 1) * cell) + 1 + border);
                        bool set = false;
                        for (int z = z0; z < z1 && !set; z++)
                            for (int x = x0; x < x1; x++)
                                if (tile[z * T + x] != 0) { set = true; break; }
                        if (set) g[bit >> 3] |= (byte)(1 << (bit & 7));
                    }
                }
            }
            return g;
        }
    }
}
