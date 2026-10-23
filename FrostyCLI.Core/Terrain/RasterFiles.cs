#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\RasterFiles.cs (sha256:DEC2DDC709CEE1A0).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// A decoded raster: <see cref="Width"/> x <see cref="Height"/> pixels of <see cref="Channels"/> interleaved
    /// channels, row 0 first as stored in the file. Integer formats are normalised to 0..1 (u8 / 255, u16 / 65535);
    /// float formats keep their values (metres, for a heightmap).
    /// </summary>
    public sealed class Raster
    {
        public int Width, Height, Channels;
        public float[] Data = Array.Empty<float>();
        /// <summary>True when the file stored floats (EXR half/float, raw float32): values are not normalised.</summary>
        public bool IsFloat;
        /// <summary>Bits per channel in the file (8, 16, 32).</summary>
        public int BitDepth;
        public string Format = "";

        public float this[int x, int y, int c] => Data[((long)y * Width + x) * Channels + c];

        /// <summary>One channel as its own row-major grid.</summary>
        public float[] Channel(int c)
        {
            if (c < 0 || c >= Channels) throw new ArgumentOutOfRangeException(nameof(c));
            var outData = new float[(long)Width * Height];
            for (long i = 0; i < outData.Length; i++) outData[i] = Data[i * Channels + c];
            return outData;
        }

        /// <summary>Luminance-free "value" channel: the first channel (grey, or red for colour images).</summary>
        public float[] Value() => Channel(0);

        /// <summary>The rows in the opposite order (row 0 last).</summary>
        public Raster FlipRows()
        {
            var r = new Raster { Width = Width, Height = Height, Channels = Channels, IsFloat = IsFloat, BitDepth = BitDepth, Format = Format };
            r.Data = new float[Data.Length];
            long row = (long)Width * Channels;
            for (int y = 0; y < Height; y++)
                Array.Copy(Data, (long)y * row, r.Data, (long)(Height - 1 - y) * row, row);
            return r;
        }
    }

    /// <summary>
    /// Reads the raster files terrain authoring takes in: 16-bit raw heightmaps (.r16/.raw16), 32-bit float raw
    /// (.r32/.raw32/.f32), PNG (grey/RGB/RGBA/palette, 8 or 16 bit, not interlaced) and OpenEXR (single-part scanline;
    /// NONE, RLE, ZIPS and ZIP compression; HALF, FLOAT and UINT channels). Raw files are square unless the size is
    /// given. No dependencies beyond the base library, so it vendors into net48.
    /// </summary>
    public static class RasterFile
    {
        public static Raster Read(string path, int rawWidth = 0, int rawHeight = 0)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".png": return ReadPng(bytes);
                case ".exr": return ReadExr(bytes);
                case ".r16": case ".raw16": case ".raw": return ReadRaw16(bytes, rawWidth, rawHeight);
                case ".r32": case ".raw32": case ".f32": return ReadRaw32(bytes, rawWidth, rawHeight);
                default:
                    if (IsPng(bytes)) return ReadPng(bytes);
                    if (IsExr(bytes)) return ReadExr(bytes);
                    throw new InvalidDataException("unknown raster format '" + ext + "' (use .png, .exr, .r16 or .r32)");
            }
        }

        public static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;
        public static bool IsExr(byte[] b) => b.Length > 8 && b[0] == 0x76 && b[1] == 0x2F && b[2] == 0x31 && b[3] == 0x01;

        // ------------------------------------------------------------------------------------------ raw

        public static Raster ReadRaw16(byte[] bytes, int width = 0, int height = 0)
        {
            if (bytes.Length % 2 != 0) throw new InvalidDataException("a 16-bit raw heightmap has an even byte count");
            long count = bytes.Length / 2;
            Square(count, ref width, ref height, "16-bit raw");
            var r = new Raster { Width = width, Height = height, Channels = 1, BitDepth = 16, Format = "raw16" };
            r.Data = new float[count];
            for (long i = 0; i < count; i++) r.Data[i] = (bytes[2 * i] | (bytes[2 * i + 1] << 8)) / 65535f;
            return r;
        }

        public static Raster ReadRaw32(byte[] bytes, int width = 0, int height = 0)
        {
            if (bytes.Length % 4 != 0) throw new InvalidDataException("a 32-bit float raw heightmap has a byte count divisible by 4");
            long count = bytes.Length / 4;
            Square(count, ref width, ref height, "32-bit float raw");
            var r = new Raster { Width = width, Height = height, Channels = 1, BitDepth = 32, IsFloat = true, Format = "raw32" };
            r.Data = new float[count];
            for (long i = 0; i < count; i++) r.Data[i] = BitConverter.ToSingle(bytes, (int)(4 * i));
            return r;
        }

        private static void Square(long count, ref int width, ref int height, string what)
        {
            if (width > 0 && height > 0)
            {
                if ((long)width * height != count) throw new InvalidDataException(what + ": " + count + " samples is not " + width + " x " + height);
                return;
            }
            long side = (long)Math.Round(Math.Sqrt(count));
            if (side * side != count)
                throw new InvalidDataException(what + ": " + count + " samples is not a square grid; give the size (a .json sidecar or --raw-size WxH)");
            width = height = (int)side;
        }

        // ------------------------------------------------------------------------------------------ png

        public static Raster ReadPng(byte[] b)
        {
            if (!IsPng(b)) throw new InvalidDataException("not a PNG file");
            int pos = 8;
            int width = 0, height = 0, depth = 0, colorType = -1, interlace = 0;
            byte[]? palette = null;
            var idat = new MemoryStream();
            while (pos + 8 <= b.Length)
            {
                int len = BigEndian32(b, pos);
                string type = Encoding.ASCII.GetString(b, pos + 4, 4);
                int data = pos + 8;
                if (len < 0 || data + len > b.Length) throw new InvalidDataException("PNG chunk " + type + " runs past the end of the file");
                if (type == "IHDR")
                {
                    width = BigEndian32(b, data); height = BigEndian32(b, data + 4);
                    depth = b[data + 8]; colorType = b[data + 9]; interlace = b[data + 12];
                }
                else if (type == "PLTE") { palette = new byte[len]; Array.Copy(b, data, palette, 0, len); }
                else if (type == "IDAT") idat.Write(b, data, len);
                else if (type == "IEND") break;
                pos = data + len + 4;   // skip CRC
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException("PNG has no IHDR");
            if (interlace != 0) throw new InvalidDataException("interlaced PNG is not supported; save it without interlacing");
            int channels;
            switch (colorType)
            {
                case 0: channels = 1; break;
                case 2: channels = 3; break;
                case 3: channels = 1; break;
                case 4: channels = 2; break;
                case 6: channels = 4; break;
                default: throw new InvalidDataException("PNG colour type " + colorType + " is not supported");
            }
            if (colorType == 3 && palette == null) throw new InvalidDataException("palette PNG without a PLTE chunk");
            if (depth != 8 && depth != 16 && !(colorType == 0 || colorType == 3))
                throw new InvalidDataException("PNG bit depth " + depth + " is not supported for colour type " + colorType);

            byte[] raw = Inflate(idat.ToArray(), zlib: true);
            int bitsPerPixel = depth * channels;
            int bpp = Math.Max(1, bitsPerPixel / 8);                 // filter byte distance
            int stride = (width * bitsPerPixel + 7) / 8;
            if (raw.Length < (long)(stride + 1) * height) throw new InvalidDataException("PNG image data is truncated");
            var cur = new byte[stride];
            var prev = new byte[stride];
            int outChannels = colorType == 3 ? 3 : channels;
            var r = new Raster { Width = width, Height = height, Channels = outChannels, BitDepth = depth == 16 ? 16 : 8, Format = "png" };
            r.Data = new float[(long)width * height * outChannels];
            int p = 0;
            for (int y = 0; y < height; y++)
            {
                int filter = raw[p++];
                Array.Copy(raw, p, cur, 0, stride);
                p += stride;
                Unfilter(filter, cur, prev, bpp);
                long o = (long)y * width * outChannels;
                for (int x = 0; x < width; x++)
                {
                    if (colorType == 3)
                    {
                        int idx = Bits(cur, x, depth);
                        for (int c = 0; c < 3; c++)
                            r.Data[o + x * 3 + c] = (3 * idx + c < palette!.Length ? palette[3 * idx + c] : 0) / 255f;
                    }
                    else if (depth == 16)
                    {
                        for (int c = 0; c < channels; c++)
                        {
                            int k = (x * channels + c) * 2;
                            r.Data[o + x * channels + c] = ((cur[k] << 8) | cur[k + 1]) / 65535f;
                        }
                    }
                    else if (depth == 8)
                    {
                        for (int c = 0; c < channels; c++) r.Data[o + x * channels + c] = cur[x * channels + c] / 255f;
                    }
                    else
                    {
                        // 1/2/4-bit grey
                        int max = (1 << depth) - 1;
                        r.Data[o + x] = Bits(cur, x, depth) / (float)max;
                    }
                }
                var t = prev; prev = cur; cur = t;
            }
            return r;
        }

        private static int Bits(byte[] row, int x, int depth)
        {
            if (depth == 8) return row[x];
            int bit = x * depth;
            int shift = 8 - depth - (bit & 7);
            return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
        }

        private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
        {
            int n = cur.Length;
            switch (filter)
            {
                case 0: return;
                case 1: for (int i = bpp; i < n; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]); return;
                case 2: for (int i = 0; i < n; i++) cur[i] = (byte)(cur[i] + prev[i]); return;
                case 3:
                    for (int i = 0; i < n; i++)
                        cur[i] = (byte)(cur[i] + (((i >= bpp ? cur[i - bpp] : 0) + prev[i]) >> 1));
                    return;
                case 4:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0, b2 = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                        int pa = Math.Abs(b2 - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b2 - 2 * c);
                        int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b2 : c;
                        cur[i] = (byte)(cur[i] + pred);
                    }
                    return;
                default: throw new InvalidDataException("PNG filter type " + filter + " is invalid");
            }
        }

        private static int BigEndian32(byte[] b, int p) => (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];

        /// <summary>Inflates a zlib stream (2-byte header, deflate data, Adler-32) or raw deflate.</summary>
        public static byte[] Inflate(byte[] data, bool zlib, int expected = 0)
        {
            int start = zlib ? 2 : 0;
            using (var input = new MemoryStream(data, start, data.Length - start))
            using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = expected > 0 ? new MemoryStream(expected) : new MemoryStream())
            {
                inflater.CopyTo(output);
                return output.ToArray();
            }
        }

        // ------------------------------------------------------------------------------------------ exr

        private sealed class ExrChannel
        {
            public string Name = "";
            public int PixelType;          // 0 UINT, 1 HALF, 2 FLOAT
            public int XSampling = 1, YSampling = 1;
            public int Bytes => PixelType == 1 ? 2 : 4;
        }

        public static Raster ReadExr(byte[] b)
        {
            if (!IsExr(b)) throw new InvalidDataException("not an OpenEXR file");
            int version = b[4];
            int flags = b[5] | (b[6] << 8) | (b[7] << 16);
            if (version != 2) throw new InvalidDataException("OpenEXR version " + version + " is not supported");
            if ((flags & 0x2) != 0) throw new InvalidDataException("tiled OpenEXR is not supported; save scanline");
            if ((flags & 0x10) != 0) throw new InvalidDataException("multi-part OpenEXR is not supported; save a single layer");
            if ((flags & 0x8) != 0) throw new InvalidDataException("deep OpenEXR is not supported");

            int pos = 8;
            var channels = new List<ExrChannel>();
            int compression = -1;
            int xMin = 0, yMin = 0, xMax = -1, yMax = -1;
            while (true)
            {
                string name = CString(b, ref pos);
                if (name.Length == 0) break;
                string type = CString(b, ref pos);
                int size = BitConverter.ToInt32(b, pos); pos += 4;
                int v = pos;
                if (name == "channels" && type == "chlist")
                {
                    while (true)
                    {
                        string cn = CString(b, ref v);
                        if (cn.Length == 0) break;
                        var ch = new ExrChannel { Name = cn, PixelType = BitConverter.ToInt32(b, v) };
                        v += 4 + 4;                                // pixel type, pLinear + 3 reserved
                        ch.XSampling = BitConverter.ToInt32(b, v); v += 4;
                        ch.YSampling = BitConverter.ToInt32(b, v); v += 4;
                        channels.Add(ch);
                    }
                }
                else if (name == "compression") compression = b[v];
                else if (name == "dataWindow")
                {
                    xMin = BitConverter.ToInt32(b, v); yMin = BitConverter.ToInt32(b, v + 4);
                    xMax = BitConverter.ToInt32(b, v + 8); yMax = BitConverter.ToInt32(b, v + 12);
                }
                pos += size;
            }
            if (channels.Count == 0) throw new InvalidDataException("OpenEXR has no channels");
            foreach (var ch in channels)
                if (ch.XSampling != 1 || ch.YSampling != 1) throw new InvalidDataException("sub-sampled OpenEXR channels are not supported");
            int linesPerChunk;
            switch (compression)
            {
                case 0: case 1: case 2: linesPerChunk = 1; break;
                case 3: linesPerChunk = 16; break;
                default:
                    throw new InvalidDataException("OpenEXR compression " + compression +
                        " is not supported; save with ZIP, ZIPS, RLE or no compression (Blender: Codec = ZIP)");
            }
            int width = xMax - xMin + 1, height = yMax - yMin + 1;
            if (width <= 0 || height <= 0) throw new InvalidDataException("OpenEXR data window is empty");

            // Channels are stored in chlist order (alphabetical). Output: the value channel first -- Y, else R, else the
            // first -- then the rest in file order.
            int valueIndex = channels.FindIndex(c => c.Name == "Y");
            if (valueIndex < 0) valueIndex = channels.FindIndex(c => c.Name == "R");
            if (valueIndex < 0) valueIndex = 0;
            var order = new List<int> { valueIndex };
            for (int i = 0; i < channels.Count; i++) if (i != valueIndex) order.Add(i);
            var outSlot = new int[channels.Count];
            for (int k = 0; k < order.Count; k++) outSlot[order[k]] = k;

            int chunks = (height + linesPerChunk - 1) / linesPerChunk;
            var offsets = new long[chunks];
            for (int i = 0; i < chunks; i++) { offsets[i] = BitConverter.ToInt64(b, pos); pos += 8; }

            int lineBytes = 0;
            foreach (var ch in channels) lineBytes += ch.Bytes * width;
            var r = new Raster { Width = width, Height = height, Channels = channels.Count, IsFloat = true, BitDepth = 32, Format = "exr" };
            r.Data = new float[(long)width * height * channels.Count];
            for (int ci = 0; ci < chunks; ci++)
            {
                long o = offsets[ci];
                int y0 = BitConverter.ToInt32(b, (int)o) - yMin;
                int packed = BitConverter.ToInt32(b, (int)o + 4);
                int lines = Math.Min(linesPerChunk, height - y0);
                int expected = lineBytes * lines;
                byte[] block;
                if (packed == expected) { block = new byte[expected]; Array.Copy(b, o + 8, block, 0, expected); }
                else
                {
                    var src = new byte[packed];
                    Array.Copy(b, o + 8, src, 0, packed);
                    block = compression == 1 ? ExrRle(src, expected) : Inflate(src, zlib: true, expected);
                    ExrPredictorAndInterleave(ref block);
                    if (block.Length != expected) throw new InvalidDataException("OpenEXR chunk " + ci + " inflated to the wrong size");
                }
                int p = 0;
                for (int line = 0; line < lines; line++)
                {
                    long rowBase = (long)(y0 + line) * width * channels.Count;
                    for (int c = 0; c < channels.Count; c++)
                    {
                        var ch = channels[c];
                        int slot = outSlot[c];
                        for (int x = 0; x < width; x++, p += ch.Bytes)
                        {
                            float value = ch.PixelType == 1 ? HalfToFloat((ushort)(block[p] | (block[p + 1] << 8)))
                                        : ch.PixelType == 2 ? BitConverter.ToSingle(block, p)
                                        : BitConverter.ToUInt32(block, p);
                            r.Data[rowBase + (long)x * channels.Count + slot] = value;
                        }
                    }
                }
            }
            return r;
        }

        private static string CString(byte[] b, ref int pos)
        {
            int start = pos;
            while (b[pos] != 0) pos++;
            string s = Encoding.ASCII.GetString(b, start, pos - start);
            pos++;
            return s;
        }

        private static byte[] ExrRle(byte[] src, int expected)
        {
            var outBytes = new byte[expected];
            int i = 0, o = 0;
            while (i < src.Length && o < expected)
            {
                int count = (sbyte)src[i++];
                if (count < 0)
                {
                    int n = -count;
                    for (int k = 0; k < n && o < expected; k++) outBytes[o++] = src[i++];
                }
                else
                {
                    byte v = src[i++];
                    for (int k = 0; k < count + 1 && o < expected; k++) outBytes[o++] = v;
                }
            }
            return outBytes;
        }

        /// <summary>Undoes the byte predictor and the even/odd split OpenEXR's ZIP and RLE compressors apply.</summary>
        private static void ExrPredictorAndInterleave(ref byte[] t)
        {
            int n = t.Length;
            for (int i = 1; i < n; i++) t[i] = (byte)(t[i - 1] + t[i] - 128);
            var outBytes = new byte[n];
            int half = (n + 1) / 2;
            int a = 0, bIdx = half, o = 0;
            while (o < n)
            {
                if (a < half) outBytes[o++] = t[a++];
                if (o < n) outBytes[o++] = t[bIdx++];
            }
            t = outBytes;
        }

        public static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
            float value;
            if (exp == 0) value = (float)(mant * Math.Pow(2, -24));
            else if (exp == 31) value = mant == 0 ? float.PositiveInfinity : float.NaN;
            else value = (float)((1 + mant / 1024.0) * Math.Pow(2, exp - 15));
            return sign == 1 ? -value : value;
        }
    }
}
