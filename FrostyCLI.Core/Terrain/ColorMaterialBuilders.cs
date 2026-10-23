#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\ColorMaterialBuilders.cs (sha256:5B3287E112B29A30).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Linq;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    /// <summary>A colour map over the world square (the baked terrain colour of the colour tree).</summary>
    public interface IColorSource
    {
        /// <summary>RGB of a texel of side <paramref name="texel"/> metres centred at (x, z).</summary>
        (byte R, byte G, byte B) Color(double x, double z, double texel);
    }

    public sealed class ConstantColorSource : IColorSource
    {
        private readonly (byte, byte, byte) _c;
        public ConstantColorSource(byte r, byte g, byte b) { _c = (r, g, b); }
        public (byte R, byte G, byte B) Color(double x, double z, double texel) => _c;
    }

    /// <summary>An RGB image (row = z) over the world square, with a box mip chain.</summary>
    public sealed class DenseColorSource : IColorSource
    {
        private readonly double _minX, _minZ, _extent;
        private readonly List<(byte[] Rgb, int Size)> _mips = new List<(byte[], int)>();

        public DenseColorSource(double minX, double minZ, double extent, byte[] rgb, int size)
        {
            if (rgb.Length != (long)size * size * 3) throw new ArgumentException("rgb must be size*size*3");
            _minX = minX; _minZ = minZ; _extent = extent;
            _mips.Add((rgb, size));
            int s = size; var cur = rgb;
            while (s > 1)
            {
                int n = (s + 1) / 2;
                var next = new byte[n * n * 3];
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                        for (int ch = 0; ch < 3; ch++)
                        {
                            int sum = 0, cnt = 0;
                            for (int dz = 0; dz < 2; dz++)
                                for (int dx = 0; dx < 2; dx++)
                                {
                                    int sx = 2 * x + dx, sz = 2 * z + dz;
                                    if (sx >= s || sz >= s) continue;
                                    sum += cur[(sz * s + sx) * 3 + ch]; cnt++;
                                }
                            next[(z * n + x) * 3 + ch] = (byte)((sum + cnt / 2) / cnt);
                        }
                _mips.Add((next, n)); cur = next; s = n;
            }
        }

        public (byte R, byte G, byte B) Color(double x, double z, double texel)
        {
            double texel0 = _extent / _mips[0].Size;
            int level = 0;
            while (level + 1 < _mips.Count && texel0 * (1 << (level + 1)) <= texel * 1.0001) level++;
            var (data, size) = _mips[level];
            double fx = (x - _minX) / _extent * size - 0.5, fz = (z - _minZ) / _extent * size - 0.5;
            if (fx < 0) fx = 0; else if (fx > size - 1) fx = size - 1;
            if (fz < 0) fz = 0; else if (fz > size - 1) fz = size - 1;
            int x0 = (int)fx, z0 = (int)fz, x1 = Math.Min(x0 + 1, size - 1), z1 = Math.Min(z0 + 1, size - 1);
            double tx = fx - x0, tz = fz - z0;
            byte Ch(int ch)
            {
                double v = data[(z0 * size + x0) * 3 + ch] * (1 - tx) * (1 - tz) + data[(z0 * size + x1) * 3 + ch] * tx * (1 - tz)
                         + data[(z1 * size + x0) * 3 + ch] * (1 - tx) * tz + data[(z1 * size + x1) * 3 + ch] * tx * tz;
                return (byte)Math.Floor(v + 0.5);
            }
            return (Ch(0), Ch(1), Ch(2));
        }
    }

    public sealed class ColorBuildOptions
    {
        public int FirstDataLevel = 2;       // L0/L1 skipped, as on the shipped MP terrains
        public int MaxLevel = 4;
        public uint TextureFormat = ColorTree.FormatBc1;
        public int AtlasSampleCountX = 4096;
        public double AtlasHeadroom = 0.25;
    }

    /// <summary>
    /// Builds the colour raster tree (tag 2, SPEC §4): every node from <see cref="ColorBuildOptions.FirstDataLevel"/>
    /// to <see cref="ColorBuildOptions.MaxLevel"/> carries a 260x260 tile (256 texel-centred samples + 2 border),
    /// block-compressed BC1, stored B,G,R; the levels above are skipped.
    /// </summary>
    public static class ColorBuilder
    {
        public static ColorTree Build(IColorSource source, TerrainFrame frame, ColorBuildOptions o)
        {
            const int T = 260, B = 2, pot = 256;
            var t = new ColorTree
            {
                TileSamplesPerSide = T, TileBorderWidth = B, Blurriness = 0, TextureFormat = o.TextureFormat, ChannelCount = 4,
                TreeCoverage = new AxisAlignedBox2
                {
                    MinX = F32.FromValue((float)frame.MinX), MinY = F32.FromValue((float)frame.MinZ),
                    MaxX = F32.FromValue((float)(frame.MinX + frame.Extent)), MaxY = F32.FromValue((float)(frame.MinZ + frame.Extent)),
                },
            };
            int nodes = 0, persistent = 0;
            t.Root = Node(new QuadtreeNodeId(0, 0, 0));
            t.NodeCount = (uint)nodes;
            t.PersistentNodeCount = (uint)persistent;
            int wanted = (int)Math.Ceiling(persistent * (1 + o.AtlasHeadroom)) + 1;
            int cols = o.AtlasSampleCountX / T, rows = (wanted + cols - 1) / cols;
            t.ResourceAtlasSampleCountX = (uint)o.AtlasSampleCountX;
            t.ResourceAtlasSampleCountY = (uint)Math.Max(128, ((rows * T + 127) / 128) * 128);
            return t;

            ColorNode Node(QuadtreeNodeId id)
            {
                nodes++;
                var n = new ColorNode { Id = id, TailRead = true, HasChildrenRaw = (byte)(id.Level < o.MaxLevel ? 1 : 0) };
                if (id.Level < o.FirstDataLevel)
                {
                    n.SkippedRaw = 1;
                }
                else
                {
                    n.HasDataRaw = 1;
                    n.PersistentRaw = 1;
                    persistent++;
                    double size = frame.NodeSize(id.Level), texel = size / pot;
                    double nx = frame.MinX + id.IndexX * size, nz = frame.MinZ + id.IndexY * size;
                    var bgra = new byte[T * T * 4];
                    for (int j = 0; j < T; j++)
                    {
                        double wz = Clamp(nz + (j - B + 0.5) * texel, frame.MinZ, frame.MinZ + frame.Extent);
                        for (int i = 0; i < T; i++)
                        {
                            double wx = Clamp(nx + (i - B + 0.5) * texel, frame.MinX, frame.MinX + frame.Extent);
                            var c = source.Color(wx, wz, texel);
                            int p = (j * T + i) * 4;
                            bgra[p] = c.B; bgra[p + 1] = c.G; bgra[p + 2] = c.R; bgra[p + 3] = 255;   // stored B,G,R
                        }
                    }
                    n.Tile = o.TextureFormat == ColorTree.FormatBc1 ? Bc1.Encode(bgra, T, T) : Bc1.EncodeBc3(bgra, T, T);
                }
                if (n.HasChildren) for (int ci = 0; ci < 4; ci++) n.Children.Add(Node(id.Child(ci)));
                return n;
            }

            static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
        }
    }

    /// <summary>
    /// Minimal BC1/BC3 encoder: per 4x4 block, endpoints along the colour's principal extent (bounding-box diagonal
    /// through the block's range), then nearest-palette indices. Adequate for low-frequency terrain colour maps.
    /// Input is 4 bytes per texel in the channel order to be stored.
    /// </summary>
    public static class Bc1
    {
        public static byte[] Encode(byte[] px, int w, int h) => EncodeBlocks(px, w, h, false);
        public static byte[] EncodeBc3(byte[] px, int w, int h) => EncodeBlocks(px, w, h, true);

        private static byte[] EncodeBlocks(byte[] px, int w, int h, bool bc3)
        {
            int bw = (w + 3) / 4, bh = (h + 3) / 4, bpb = bc3 ? 16 : 8;
            var outBytes = new byte[bw * bh * bpb];
            var block = new int[16 * 4];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int sx = Math.Min(bx * 4 + x, w - 1), sy = Math.Min(by * 4 + y, h - 1);
                            for (int c = 0; c < 4; c++) block[(y * 4 + x) * 4 + c] = px[(sy * w + sx) * 4 + c];
                        }
                    int o = (by * bw + bx) * bpb;
                    if (bc3) { EncodeAlpha(block, outBytes, o); o += 8; }
                    EncodeColor(block, outBytes, o, bc3);
                }
            return outBytes;
        }

        private static void EncodeAlpha(int[] b, byte[] dst, int o)
        {
            int lo = 255, hi = 0;
            for (int i = 0; i < 16; i++) { int a = b[i * 4 + 3]; if (a < lo) lo = a; if (a > hi) hi = a; }
            dst[o] = (byte)hi; dst[o + 1] = (byte)lo;
            ulong bits = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = b[i * 4 + 3];
                int best = 0, bestErr = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int v = k == 0 ? hi : k == 1 ? lo : hi == lo ? hi : ((8 - k) * hi + (k - 1) * lo) / 7;
                    int e = Math.Abs(v - a);
                    if (e < bestErr) { bestErr = e; best = k; }
                }
                bits |= (ulong)best << (3 * i);
            }
            for (int k = 0; k < 6; k++) dst[o + 2 + k] = (byte)(bits >> (8 * k));
        }

        private static void EncodeColor(int[] b, byte[] dst, int o, bool forceFour)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i < 16; i++)
            {
                int r = b[i * 4], g = b[i * 4 + 1], bl = b[i * 4 + 2];
                if (r < minR) minR = r; if (g < minG) minG = g; if (bl < minB) minB = bl;
                if (r > maxR) maxR = r; if (g > maxG) maxG = g; if (bl > maxB) maxB = bl;
            }
            ushort c0 = To565(maxR, maxG, maxB), c1 = To565(minR, minG, minB);
            if (c0 < c1) { var tmp = c0; c0 = c1; c1 = tmp; }
            var pal = new int[4, 3];
            From565(c0, out pal[0, 0], out pal[0, 1], out pal[0, 2]);
            From565(c1, out pal[1, 0], out pal[1, 1], out pal[1, 2]);
            bool four = c0 > c1;
            for (int c = 0; c < 3; c++)
            {
                if (four) { pal[2, c] = (2 * pal[0, c] + pal[1, c]) / 3; pal[3, c] = (pal[0, c] + 2 * pal[1, c]) / 3; }
                else { pal[2, c] = (pal[0, c] + pal[1, c]) / 2; pal[3, c] = pal[2, c]; }
            }
            uint idx = 0;
            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestErr = int.MaxValue;
                for (int k = 0; k < 4; k++)
                {
                    int dr = pal[k, 0] - b[i * 4], dg = pal[k, 1] - b[i * 4 + 1], db = pal[k, 2] - b[i * 4 + 2];
                    int e = dr * dr + dg * dg + db * db;
                    if (e < bestErr) { bestErr = e; best = k; }
                }
                idx |= (uint)best << (2 * i);
            }
            dst[o] = (byte)c0; dst[o + 1] = (byte)(c0 >> 8); dst[o + 2] = (byte)c1; dst[o + 3] = (byte)(c1 >> 8);
            dst[o + 4] = (byte)idx; dst[o + 5] = (byte)(idx >> 8); dst[o + 6] = (byte)(idx >> 16); dst[o + 7] = (byte)(idx >> 24);
        }

        private static ushort To565(int r, int g, int b) => (ushort)(((r * 31 + 127) / 255) << 11 | ((g * 63 + 127) / 255) << 5 | ((b * 31 + 127) / 255));

        private static void From565(ushort c, out int r, out int g, out int b)
        {
            r = ((c >> 11) & 31) * 255 / 31; g = ((c >> 5) & 63) * 255 / 63; b = (c & 31) * 255 / 31;
        }
    }

    /// <summary>A 4-bit physics material index over the world square.</summary>
    public interface IMaterialSource
    {
        byte Index(double x, double z);
    }

    public sealed class ConstantMaterialSource : IMaterialSource
    {
        private readonly byte _i;
        public ConstantMaterialSource(byte index) { _i = (byte)(index & 15); }
        public byte Index(double x, double z) => _i;
    }

    /// <summary>Material indices as a byte grid (row = z) over the world square, nearest sample.</summary>
    public sealed class DenseMaterialSource : IMaterialSource
    {
        private readonly double _minX, _minZ, _extent;
        private readonly byte[] _idx;
        private readonly int _size;
        public DenseMaterialSource(double minX, double minZ, double extent, byte[] indices, int size)
        {
            _minX = minX; _minZ = minZ; _extent = extent; _idx = indices; _size = size;
        }
        public byte Index(double x, double z)
        {
            int i = (int)((x - _minX) / _extent * _size), j = (int)((z - _minZ) / _extent * _size);
            if (i < 0) i = 0; else if (i >= _size) i = _size - 1;
            if (j < 0) j = 0; else if (j >= _size) j = _size - 1;
            return (byte)(_idx[j * _size + i] & 15);
        }
    }

    /// <summary>
    /// Builds the material raster tree (tag 3, SPEC §5): 256x256 4-bit indices per node at one level, RLE per line;
    /// ancestors carry no data (hasData 0, persistent 0), as on the shipped MP terrains. <paramref name="decls"/> is
    /// the packed <c>MaterialDecl</c> per index (take the host level's: they name its physics materials).
    /// </summary>
    public static class MaterialBuilder
    {
        public static MaterialTree Build(IMaterialSource source, TerrainFrame frame, int dataLevel, IList<uint> decls, uint background)
        {
            const int n = 256;
            var t = new MaterialTree
            {
                NodeSamplesPerSide = n, BlurrinessLog2 = 0,
                TreeCoverage = new AxisAlignedBox2
                {
                    MinX = F32.FromValue((float)frame.MinX), MinY = F32.FromValue((float)frame.MinZ),
                    MaxX = F32.FromValue((float)(frame.MinX + frame.Extent)), MaxY = F32.FromValue((float)(frame.MinZ + frame.Extent)),
                },
                LevelMax = (uint)dataLevel,
                BackgroundMaterialDecl = background,
            };
            t.MaterialDecls.AddRange(decls);
            int nodes = 0, persistent = 0;
            t.Root = Node(new QuadtreeNodeId(0, 0, 0));
            t.NodeCount = (uint)nodes;
            t.PersistentNodeCount = (uint)persistent;
            return t;

            MaterialNode Node(QuadtreeNodeId id)
            {
                nodes++;
                var node = new MaterialNode { Id = id };
                if (id.Level < dataLevel)
                {
                    node.HasChildrenRaw = 1;
                    for (int ci = 0; ci < 4; ci++) node.Children.Add(Node(id.Child(ci)));
                    return node;
                }
                node.HasDataRaw = 1; node.PersistentRaw = 1; persistent++;
                double size = frame.NodeSize(id.Level), cell = size / n;
                double nx = frame.MinX + id.IndexX * size, nz = frame.MinZ + id.IndexY * size;
                var lines = new byte[n][];
                var samples = new byte[n];
                for (int j = 0; j < n; j++)
                {
                    for (int i = 0; i < n; i++) samples[i] = source.Index(nx + (i + 0.5) * cell, nz + (j + 0.5) * cell);
                    lines[j] = Rle4.Pack(samples, 0, n);
                }
                Rle4.EncodeNode(node, lines);
                return node;
            }
        }
    }

    /// <summary>
    /// The smallest destruction depth tree (tag 4) shipped MP terrains carry: a root and four children, no data
    /// (Geonosis, Scarif, Hoth_02). The tree is mandatory in the payload; destruction depth is not authored here.
    /// </summary>
    public static class DestructionDepthBuilder
    {
        public static DestructionDepthTree Empty(TerrainFrame frame)
        {
            var t = new DestructionDepthTree
            {
                NodeSamplesPerSide = (uint)frame.TileSamplesPerSide, BlurrinessLog2 = 0,
                TreeCoverage = new AxisAlignedBox2
                {
                    MinX = F32.FromValue((float)frame.MinX), MinY = F32.FromValue((float)frame.MinZ),
                    MaxX = F32.FromValue((float)(frame.MinX + frame.Extent)), MaxY = F32.FromValue((float)(frame.MinZ + frame.Extent)),
                },
                NodeCount = 5, PersistentNodeCount = 0, LevelMax = 1, NodeBorderWidth = (uint)frame.BorderWidth,
            };
            t.Root = new DestructionDepthNode { Id = new QuadtreeNodeId(0, 0, 0), PersistentRaw = 1, HasChildrenRaw = 1 };
            for (int ci = 0; ci < 4; ci++)
                t.Root.Children.Add(new DestructionDepthNode { Id = t.Root.Id.Child(ci), PersistentRaw = 1 });
            return t;
        }
    }
}
