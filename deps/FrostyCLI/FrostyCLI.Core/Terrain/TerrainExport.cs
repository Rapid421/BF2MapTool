#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\TerrainExport.cs (sha256:4D6CBB1F1657C84A).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// Reads a terrain's layer masks back out the way the renderer walks them (TerrainTextureGenerator
    /// getOverlappingMaskSubtiles): from the root down to a data level, any subtile at that level, and subtiles flagged
    /// leaf on the way. A layer's alpha at a point is its tile sampled bilinearly (texel-centred, with the tile border),
    /// or 255 for a full subtile. The result is the ordered alpha stack of SPEC §3, not normalised weights.
    /// </summary>
    public sealed class MaskReader
    {
        private readonly MaskTree _tree;
        private readonly int _t, _b;
        private readonly Dictionary<MaskSubtile, byte[]> _tiles = new Dictionary<MaskSubtile, byte[]>();

        public MaskReader(MaskTree tree)
        {
            _tree = tree;
            _t = (int)tree.TileSamplesPerSide;
            _b = (_t - (1 << IntLog2(_t))) / 2;
            foreach (var n in tree.EnumerateNodes())
            {
                int k = 0;
                foreach (var st in n.Subtiles)
                    if (!st.IsTrivial && k < n.Tiles.Count) _tiles[st] = n.Tiles[k++];
            }
        }

        /// <summary>Host layer indices that appear in any subtile, ascending.</summary>
        public IReadOnlyList<int> Layers =>
            _tree.EnumerateNodes().SelectMany(n => n.Subtiles).Select(s => (int)s.TerrainLayerIndex).Distinct().OrderBy(v => v).ToList();

        public int DeepestLevel => _tree.EnumerateNodes().Where(n => n.Subtiles.Count > 0).Select(n => (int)n.Id.Level).DefaultIfEmpty(0).Max();

        /// <summary>
        /// One byte per texel per requested layer over the tree's square: <paramref name="size"/> texels a side, row 0
        /// at min Z, texel-centred.
        /// </summary>
        public Dictionary<int, byte[]> Render(IEnumerable<int> layers, int size, int dataLevel)
        {
            var want = new HashSet<int>(layers);
            var outMaps = want.ToDictionary(l => l, l => new byte[(long)size * size]);
            var cov = _tree.TreeCoverage;
            double x0 = cov.MinX.Value, z0 = cov.MinY.Value, ext = cov.MaxX.Value - cov.MinX.Value;
            var hits = new List<MaskSubtile>();
            for (int j = 0; j < size; j++)
            {
                double z = z0 + (j + 0.5) * ext / size;
                for (int i = 0; i < size; i++)
                {
                    double x = x0 + (i + 0.5) * ext / size;
                    hits.Clear();
                    Collect(_tree.Root, x, z, dataLevel, hits);
                    foreach (var st in hits)
                    {
                        if (!want.Contains(st.TerrainLayerIndex)) continue;
                        byte a = Alpha(st, x, z);
                        var map = outMaps[st.TerrainLayerIndex];
                        long o = (long)j * size + i;
                        if (a > map[o]) map[o] = a;
                    }
                }
            }
            return outMaps;
        }

        /// <summary>One layer's alpha at a point (the renderer's walk to <paramref name="dataLevel"/>); 0 where unpainted.</summary>
        public byte AlphaAt(int layer, double x, double z, int dataLevel)
        {
            var hits = new List<MaskSubtile>();
            Collect(_tree.Root, x, z, dataLevel, hits);
            byte best = 0;
            foreach (var st in hits)
                if (st.TerrainLayerIndex == layer) { byte a = Alpha(st, x, z); if (a > best) best = a; }
            return best;
        }

        private static void Collect(MaskNode? node, double x, double z, int dataLevel, List<MaskSubtile> hits)
        {
            if (node == null) return;
            bool atData = node.Id.Level >= dataLevel || !node.HasChildren || node.Children.Count != 4;
            foreach (var st in node.Subtiles)
            {
                if (!(atData || st.IsLeafRaw != 0)) continue;
                var c = st.WorldCoverage;
                if (x >= c.MinX.Value && x < c.MaxX.Value && z >= c.MinY.Value && z < c.MaxY.Value) hits.Add(st);
            }
            if (atData) return;
            foreach (var child in node.Children)
            {
                if (child.Subtiles.Count == 0 && !child.HasChildren) continue;
                // children are visited by coverage of their own subtiles; a cheap bounds test on the node's square
                if (Contains(child, x, z)) Collect(child, x, z, dataLevel, hits);
            }
        }

        private static bool Contains(MaskNode n, double x, double z)
        {
            if (n.Subtiles.Count > 0)
            {
                double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
                foreach (var st in n.Subtiles)
                {
                    minX = Math.Min(minX, st.WorldCoverage.MinX.Value); maxX = Math.Max(maxX, st.WorldCoverage.MaxX.Value);
                    minZ = Math.Min(minZ, st.WorldCoverage.MinY.Value); maxZ = Math.Max(maxZ, st.WorldCoverage.MaxY.Value);
                }
                return x >= minX && x < maxX && z >= minZ && z < maxZ;
            }
            return true;
        }

        private byte Alpha(MaskSubtile st, double x, double z)
        {
            if (st.IsTrivial || !_tiles.TryGetValue(st, out var tile)) return 255;
            int pot = _t - 2 * _b;
            var c = st.WorldCoverage;
            double texel = (c.MaxX.Value - c.MinX.Value) / pot;
            double fx = (x - c.MinX.Value) / texel - 0.5 + _b, fz = (z - c.MinY.Value) / texel - 0.5 + _b;
            if (fx < 0) fx = 0; else if (fx > _t - 1) fx = _t - 1;
            if (fz < 0) fz = 0; else if (fz > _t - 1) fz = _t - 1;
            int ix = (int)fx, iz = (int)fz, ix1 = Math.Min(ix + 1, _t - 1), iz1 = Math.Min(iz + 1, _t - 1);
            double tx = fx - ix, tz = fz - iz;
            double v = tile[iz * _t + ix] * (1 - tx) * (1 - tz) + tile[iz * _t + ix1] * tx * (1 - tz)
                     + tile[iz1 * _t + ix] * (1 - tx) * tz + tile[iz1 * _t + ix1] * tx * tz;
            return (byte)Math.Floor(v + 0.5);
        }

        private static int IntLog2(int v) { int l = 0; while ((2 << l) <= v) l++; return l; }
    }

    /// <summary>The colour tree as an RGB image: the deepest data node at each texel, BC1/BC3 decoded (stored B,G,R).</summary>
    public static class ColorReader
    {
        public static byte[] Render(ColorTree tree, int size, int maxLevel = 16)
        {
            var rgb = new byte[(long)size * size * 3];
            var cov = tree.TreeCoverage;
            double x0 = cov.MinX.Value, z0 = cov.MinY.Value, ext = cov.MaxX.Value - cov.MinX.Value;
            int t = (int)tree.TileSamplesPerSide, b = (int)tree.TileBorderWidth, pot = t - 2 * b;
            var decoded = new Dictionary<ColorNode, byte[]>();
            for (int j = 0; j < size; j++)
            {
                double z = z0 + (j + 0.5) * ext / size;
                for (int i = 0; i < size; i++)
                {
                    double x = x0 + (i + 0.5) * ext / size;
                    // deepest data node over (x, z)
                    ColorNode? best = null;
                    var n = tree.Root;
                    double nx = x0, nz = z0, ns = ext;
                    while (n != null)
                    {
                        if (n.HasData && n.Tile != null) best = n;
                        if (!n.HasChildren || n.Children.Count != 4 || n.Id.Level >= maxLevel) break;
                        ns /= 2;
                        int cx = x >= nx + ns ? 1 : 0, cz = z >= nz + ns ? 1 : 0;
                        nx += cx * ns; nz += cz * ns;
                        n = n.Children[cz == 0 ? (cx == 0 ? 0 : 1) : (cx == 0 ? 3 : 2)];   // quadtree order (0,0) (1,0) (1,1) (0,1)
                    }
                    if (best == null) continue;
                    if (!decoded.TryGetValue(best, out var px)) decoded[best] = px = Bc.Decode(best.Tile!, t, t, tree.TextureFormat == ColorTree.FormatBc3);
                    double size0 = ext / (1 << best.Id.Level);
                    double bx = x0 + best.Id.IndexX * size0, bz = z0 + best.Id.IndexY * size0;
                    int u = (int)Math.Floor((x - bx) / size0 * pot) + b, v = (int)Math.Floor((z - bz) / size0 * pot) + b;
                    u = Math.Min(Math.Max(u, 0), t - 1); v = Math.Min(Math.Max(v, 0), t - 1);
                    int p = (v * t + u) * 4;
                    long o = ((long)j * size + i) * 3;
                    rgb[o] = px[p + 2]; rgb[o + 1] = px[p + 1]; rgb[o + 2] = px[p];     // stored B,G,R
                }
            }
            return rgb;
        }
    }

    /// <summary>
    /// The material tree (tag 3, SPEC §5) as a grid of 4-bit material indices, one per pixel centre: the deepest data
    /// node over each pixel, its RLE line decoded. Pixels no data node covers get 0. At size = 256 &lt;&lt; LevelMax the
    /// pixels are the nodes' own samples (MaterialBuilder samples the same centres), so the round trip is exact.
    /// </summary>
    public static class MaterialReader
    {
        public static int NativeSize(MaterialTree tree) => (int)tree.NodeSamplesPerSide << (int)tree.LevelMax;

        public static byte[] Render(MaterialTree tree, int size)
        {
            var idx = new byte[(long)size * size];
            var cov = tree.TreeCoverage;
            double x0 = cov.MinX.Value, z0 = cov.MinY.Value, ext = cov.MaxX.Value - cov.MinX.Value;
            int n = (int)tree.NodeSamplesPerSide;
            var lines = new Dictionary<MaterialNode, byte[][]>();
            for (int j = 0; j < size; j++)
            {
                double z = z0 + (j + 0.5) * ext / size;
                for (int i = 0; i < size; i++)
                {
                    double x = x0 + (i + 0.5) * ext / size;
                    MaterialNode? best = null;
                    var node = tree.Root;
                    double nx = x0, nz = z0, ns = ext;
                    while (node != null)
                    {
                        if (node.CarriesData) best = node;
                        if (!node.HasChildren || node.Children.Count != 4) break;
                        ns /= 2;
                        int cx = x >= nx + ns ? 1 : 0, cz = z >= nz + ns ? 1 : 0;
                        nx += cx * ns; nz += cz * ns;
                        node = node.Children[cz == 0 ? (cx == 0 ? 0 : 1) : (cx == 0 ? 3 : 2)];   // quadtree order
                    }
                    if (best == null) continue;
                    if (!lines.TryGetValue(best, out var decoded)) lines[best] = decoded = Decode(best, n);
                    double size0 = ext / (1 << best.Id.Level);
                    double bx = x0 + best.Id.IndexX * size0, bz = z0 + best.Id.IndexY * size0;
                    int u = Math.Min(Math.Max((int)Math.Floor((x - bx) / size0 * n), 0), n - 1);
                    int v = Math.Min(Math.Max((int)Math.Floor((z - bz) / size0 * n), 0), n - 1);
                    byte packed = decoded[v][u >> 1];
                    idx[(long)j * size + i] = (byte)((u & 1) == 0 ? packed & 0x0F : packed >> 4);
                }
            }
            return idx;
        }

        private static byte[][] Decode(MaterialNode node, int n)
        {
            var result = new byte[n][];
            int offset = 0;
            for (int l = 0; l < n; l++)
            {
                int count = l < node.LineSizes.Length ? node.LineSizes[l] : 0;
                byte[] line = count > 0 ? Rle4.DecodeLine(node.RleData, offset, count) : Array.Empty<byte>();
                if (line.Length < n / 2) Array.Resize(ref line, n / 2);
                result[l] = line;
                offset += count;
            }
            return result;
        }
    }

    /// <summary>BC1/BC3 block decoding to 4 bytes per texel (channel order as stored).</summary>
    public static class Bc
    {
        public static byte[] Decode(byte[] data, int w, int h, bool bc3)
        {
            var px = new byte[w * h * 4];
            int bw = (w + 3) / 4, bh = (h + 3) / 4, blockBytes = bc3 ? 16 : 8;
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int o = (by * bw + bx) * blockBytes + (bc3 ? 8 : 0);
                    if (o + 8 > data.Length) continue;
                    ushort c0 = (ushort)(data[o] | (data[o + 1] << 8)), c1 = (ushort)(data[o + 2] | (data[o + 3] << 8));
                    var pal = new byte[4][];
                    pal[0] = Rgb565(c0); pal[1] = Rgb565(c1);
                    if (c0 > c1 || bc3)
                    {
                        pal[2] = Mix(pal[0], pal[1], 2, 1, 3); pal[3] = Mix(pal[0], pal[1], 1, 2, 3);
                    }
                    else
                    {
                        pal[2] = Mix(pal[0], pal[1], 1, 1, 2); pal[3] = new byte[] { 0, 0, 0 };
                    }
                    uint idx = (uint)(data[o + 4] | (data[o + 5] << 8) | (data[o + 6] << 16) | (data[o + 7] << 24));
                    for (int k = 0; k < 16; k++)
                    {
                        int x = bx * 4 + (k & 3), y = by * 4 + (k >> 2);
                        if (x >= w || y >= h) continue;
                        var c = pal[(idx >> (2 * k)) & 3];
                        int p = (y * w + x) * 4;
                        px[p] = c[0]; px[p + 1] = c[1]; px[p + 2] = c[2]; px[p + 3] = 255;
                    }
                }
            return px;
        }

        private static byte[] Rgb565(ushort c)
        {
            int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
            return new[] { (byte)((r << 3) | (r >> 2)), (byte)((g << 2) | (g >> 4)), (byte)((b << 3) | (b >> 2)) };
        }

        private static byte[] Mix(byte[] a, byte[] b, int wa, int wb, int d) =>
            new[] { (byte)((a[0] * wa + b[0] * wb) / d), (byte)((a[1] * wa + b[1] * wb) / d), (byte)((a[2] * wa + b[2] * wb) / d) };
    }

    /// <summary>8-bit PNG writer (grey, RGB or RGBA), zlib with Adler-32 and chunk CRCs, readable anywhere.</summary>
    public static class PngWriter
    {
        public static void Write(string path, int width, int height, int channels, byte[] data)
        {
            File.WriteAllBytes(path, Encode(width, height, channels, data));
        }

        public static byte[] Encode(int width, int height, int channels, byte[] data)
        {
            int colorType = channels == 1 ? 0 : channels == 3 ? 2 : channels == 4 ? 6 : throw new ArgumentException("1, 3 or 4 channels");
            int stride = width * channels;
            if (data.Length < (long)stride * height) throw new ArgumentException("data is smaller than width*height*channels");
            var raw = new byte[(long)(stride + 1) * height];
            for (int y = 0; y < height; y++) Array.Copy(data, (long)y * stride, raw, (long)y * (stride + 1) + 1, stride);
            byte[] deflated;
            using (var ms = new MemoryStream())
            {
                using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true)) d.Write(raw, 0, raw.Length);
                deflated = ms.ToArray();
            }
            var z = new byte[deflated.Length + 6];
            z[0] = 0x78; z[1] = 0x9C;
            Array.Copy(deflated, 0, z, 2, deflated.Length);
            uint adler = Adler32(raw);
            z[z.Length - 4] = (byte)(adler >> 24); z[z.Length - 3] = (byte)(adler >> 16); z[z.Length - 2] = (byte)(adler >> 8); z[z.Length - 1] = (byte)adler;
            var png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            Be(ihdr, 0, width); Be(ihdr, 4, height); ihdr[8] = 8; ihdr[9] = (byte)colorType;
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", z);
            Chunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            Be(head, 0, data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head, 0, 8);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(head, 4, 4, 0xFFFFFFFF);
            crc = Crc32(data, 0, data.Length, crc) ^ 0xFFFFFFFF;
            var c = new byte[4]; Be(c, 0, (int)crc);
            s.Write(c, 0, 4);
        }

        private static void Be(byte[] b, int p, int v) { b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16); b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v; }

        private static uint[]? _crcTable;

        private static uint Crc32(byte[] d, int off, int len, uint crc)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                _crcTable = t;
            }
            for (int i = off; i < off + len; i++) crc = _crcTable[(crc ^ d[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint Adler32(byte[] d)
        {
            uint a = 1, b = 0;
            foreach (byte x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }
    }
}
