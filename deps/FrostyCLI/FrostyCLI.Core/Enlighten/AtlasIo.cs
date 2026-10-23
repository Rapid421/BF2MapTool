using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>Lightmap atlas file helpers: Radiance RGBE read/write, PNG RGBA read/write, DDS block splicing.</summary>
    public static class AtlasIo
    {
        /// <summary>Radiance .hdr (flat or new-style RLE scanlines) -> float RGB, row-major.</summary>
        public static float[] ReadHdr(byte[] b, out int w, out int h)
        {
            int i = 0; w = h = 0;
            while (i < b.Length)
            {
                int j = Array.IndexOf(b, (byte)'\n', i);
                string line = Encoding.ASCII.GetString(b, i, j - i);
                i = j + 1;
                if (line.StartsWith("-Y") || line.StartsWith("+Y"))
                {
                    string[] p = line.Split(' ');
                    h = int.Parse(p[1]); w = int.Parse(p[3]);
                    break;
                }
            }
            var rgbe = new byte[w * h * 4];
            var row = new byte[w];
            for (int y = 0; y < h; y++)
            {
                if (b[i] == 2 && b[i + 1] == 2 && ((b[i + 2] << 8) | b[i + 3]) == w)
                {
                    i += 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int x = 0;
                        while (x < w)
                        {
                            int n = b[i++];
                            if (n > 128) { n -= 128; byte v = b[i++]; for (int k = 0; k < n; k++) row[x + k] = v; }
                            else { Array.Copy(b, i, row, x, n); i += n; }
                            x += n;
                        }
                        for (int k = 0; k < w; k++) rgbe[(y * w + k) * 4 + c] = row[k];
                    }
                }
                else
                {
                    Array.Copy(b, i, rgbe, y * w * 4, w * 4);
                    i += w * 4;
                }
            }
            var f = new float[w * h * 3];
            for (int p = 0; p < w * h; p++)
            {
                int e = rgbe[p * 4 + 3];
                if (e == 0) continue;
                float s = (float)Math.Pow(2, e - 136);
                f[p * 3] = rgbe[p * 4] * s; f[p * 3 + 1] = rgbe[p * 4 + 1] * s; f[p * 3 + 2] = rgbe[p * 4 + 2] * s;
            }
            return f;
        }

        /// <summary>Float RGB -> Radiance .hdr bytes (flat scanlines).</summary>
        public static byte[] WriteHdr(float[] rgb, int w, int h)
        {
            var ms = new MemoryStream();
            byte[] head = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y " + h + " +X " + w + "\n");
            ms.Write(head, 0, head.Length);
            var px = new byte[4];
            for (int p = 0; p < w * h; p++)
            {
                float r = rgb[p * 3], g = rgb[p * 3 + 1], bb = rgb[p * 3 + 2];
                float m = Math.Max(r, Math.Max(g, bb));
                if (m < 1e-32f) { px[0] = px[1] = px[2] = px[3] = 0; }
                else
                {
                    int e = (int)Math.Ceiling(Math.Log(m, 2) + 1e-9);
                    double mant = m / Math.Pow(2, e);
                    if (mant >= 1) { e++; mant /= 2; }
                    double sc = 256.0 / Math.Pow(2, e);
                    px[0] = (byte)Math.Min(255, Math.Floor(r * sc)); px[1] = (byte)Math.Min(255, Math.Floor(g * sc));
                    px[2] = (byte)Math.Min(255, Math.Floor(bb * sc)); px[3] = (byte)(e + 128);
                }
                ms.Write(px, 0, 4);
            }
            return ms.ToArray();
        }

        /// <summary>PNG (any format System.Drawing reads) -> RGBA bytes.</summary>
        public static byte[] ReadRgba(byte[] png, out int w, out int h)
        {
            using (var bmp = new Bitmap(new MemoryStream(png)))
            {
                w = bmp.Width; h = bmp.Height;
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var bgra = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
                bmp.UnlockBits(data);
                for (int p = 0; p < w * h; p++) { byte t = bgra[p * 4]; bgra[p * 4] = bgra[p * 4 + 2]; bgra[p * 4 + 2] = t; }
                return bgra;
            }
        }

        /// <summary>RGBA -> PNG with no gAMA / sRGB / iCCP chunk. System.Drawing writes gAMA, and the texture converter
        /// then treats the pixels as sRGB and linearises them for a UNORM target (0.569 -> 0.283): direction vectors
        /// and sky visibility must reach BC7_UNORM untouched.</summary>
        public static byte[] WriteRgbaPng(byte[] rgba, int w, int h)
        {
            return Bfii.TerrainBuild.PngWriter.Encode(w, h, 4, rgba);
        }

        /// <summary>Offset of the pixel data in a DDS file (128, or 148 with a DX10 header) and its size.</summary>
        public static int DdsDataOffset(byte[] dds, out int w, out int h, out uint dxgi)
        {
            if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S') throw new InvalidDataException("not a DDS file");
            h = BitConverter.ToInt32(dds, 12); w = BitConverter.ToInt32(dds, 16);
            uint fourcc = BitConverter.ToUInt32(dds, 84);
            dxgi = 0;
            if (fourcc == 0x30315844) { dxgi = BitConverter.ToUInt32(dds, 128); return 148; }
            return 128;
        }

        /// <summary>
        /// A larger copy of a one-mip BC6H / BC7 DDS: the old blocks keep their texel positions (top left), the new area
        /// is zero blocks (they decode to black) until tiles are spliced in. Width and height must be multiples of 4.
        /// </summary>
        public static byte[] GrowDds(byte[] dds, int newW, int newH)
        {
            int w, h; uint dxgi;
            int off = DdsDataOffset(dds, out w, out h, out dxgi);
            if (newW < w || newH < h || ((newW | newH | w | h) & 3) != 0) throw new ArgumentException("a DDS grows by whole blocks only");
            int oldPitch = w / 4 * 16, newPitch = newW / 4 * 16;
            var r = new byte[off + newPitch * (newH / 4)];
            Array.Copy(dds, r, off);
            BitConverter.GetBytes(newH).CopyTo(r, 12);
            BitConverter.GetBytes(newW).CopyTo(r, 16);
            BitConverter.GetBytes(newPitch * (newH / 4)).CopyTo(r, 20);     // linear size of the top mip
            BitConverter.GetBytes(1).CopyTo(r, 28);                         // one mip
            for (int by = 0; by < h / 4; by++)
                Array.Copy(dds, off + by * oldPitch, r, off + by * newPitch, oldPitch);
            return r;
        }

        /// <summary>One-mip 2D DDS with a DX10 header for an uncompressed format (R9G9B9E5 67, R8G8B8A8 28): 4 bytes per texel.</summary>
        public static byte[] WriteDds32(int w, int h, uint dxgi, byte[] texels)
        {
            if (texels.Length != w * h * 4) throw new ArgumentException("texels must be w x h x 4 bytes");
            var r = new byte[148 + texels.Length];
            Encoding.ASCII.GetBytes("DDS ").CopyTo(r, 0);
            BitConverter.GetBytes(124).CopyTo(r, 4);                        // header size
            BitConverter.GetBytes(0x1 | 0x2 | 0x4 | 0x8 | 0x1000).CopyTo(r, 8);   // caps, height, width, pitch, pixelformat
            BitConverter.GetBytes(h).CopyTo(r, 12);
            BitConverter.GetBytes(w).CopyTo(r, 16);
            BitConverter.GetBytes(w * 4).CopyTo(r, 20);                     // pitch
            BitConverter.GetBytes(1).CopyTo(r, 28);                         // one mip
            BitConverter.GetBytes(32).CopyTo(r, 76);                        // pixel format size
            BitConverter.GetBytes(0x4).CopyTo(r, 80);                       // DDPF_FOURCC
            BitConverter.GetBytes(0x30315844).CopyTo(r, 84);                // "DX10"
            BitConverter.GetBytes(0x1000).CopyTo(r, 108);                   // DDSCAPS_TEXTURE
            BitConverter.GetBytes(dxgi).CopyTo(r, 128);
            BitConverter.GetBytes(3).CopyTo(r, 132);                        // D3D10_RESOURCE_DIMENSION_TEXTURE2D
            BitConverter.GetBytes(1).CopyTo(r, 140);                        // array size
            Array.Copy(texels, 0, r, 148, texels.Length);
            return r;
        }

        /// <summary>DXGI R9G9B9E5_SHAREDEXP texel -> linear RGB (D3D rules: 9-bit mantissas, exponent bias 15, no implied 1).</summary>
        public static void DecodeR9G9B9E5(uint v, out float r, out float g, out float b)
        {
            double s = Math.Pow(2, (int)(v >> 27) - 15 - 9);
            r = (float)((v & 0x1FF) * s); g = (float)(((v >> 9) & 0x1FF) * s); b = (float)(((v >> 18) & 0x1FF) * s);
        }

        /// <summary>Linear RGB -> R9G9B9E5_SHAREDEXP (the D3D11 functional-spec encoder, round to nearest); negatives clamp to 0.</summary>
        public static uint EncodeR9G9B9E5(float r, float g, float b)
        {
            const double max = 511.0 / 512.0 * 65536.0;   // (2^9 - 1) / 2^9 * 2^(31 - 15)
            double rc = Math.Max(0, Math.Min(max, double.IsNaN(r) ? 0 : r)), gc = Math.Max(0, Math.Min(max, double.IsNaN(g) ? 0 : g)),
                   bc = Math.Max(0, Math.Min(max, double.IsNaN(b) ? 0 : b));
            double m = Math.Max(rc, Math.Max(gc, bc));
            if (m <= 0) return 0;
            int e = Math.Max(-16, (int)Math.Floor(Math.Log(m, 2))) + 1 + 15;
            double denom = Math.Pow(2, e - 15 - 9);
            if ((int)Math.Floor(m / denom + 0.5) == 512) { e++; denom *= 2; }
            uint rm = (uint)Math.Floor(rc / denom + 0.5), gm = (uint)Math.Floor(gc / denom + 0.5), bm = (uint)Math.Floor(bc / denom + 0.5);
            return (Math.Min(rm, 511u)) | (Math.Min(gm, 511u) << 9) | (Math.Min(bm, 511u) << 18) | ((uint)e << 27);
        }

        /// <summary>BC4_UNORM (DXGI 80) -> one byte per texel, row-major; w and h multiples of 4. The interpolated palette
        /// entries are rounded to the nearest byte (the GPU keeps the fraction; the difference is under half a step).</summary>
        public static byte[] DecodeBc4(byte[] blocks, int offset, int w, int h)
        {
            var o = new byte[w * h];
            var pal = new int[8];
            int bw = w / 4;
            for (int by = 0; by < h / 4; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    int p = offset + (by * bw + bx) * 8;
                    int a0 = blocks[p], a1 = blocks[p + 1];
                    pal[0] = a0; pal[1] = a1;
                    if (a0 > a1) for (int k = 1; k < 7; k++) pal[k + 1] = ((7 - k) * a0 + k * a1 + 3) / 7;
                    else { for (int k = 1; k < 5; k++) pal[k + 1] = ((5 - k) * a0 + k * a1 + 2) / 5; pal[6] = 0; pal[7] = 255; }
                    ulong bits = 0;
                    for (int k = 0; k < 6; k++) bits |= (ulong)blocks[p + 2 + k] << (8 * k);
                    for (int t = 0; t < 16; t++)
                        o[(by * 4 + t / 4) * w + bx * 4 + t % 4] = (byte)pal[(int)((bits >> (3 * t)) & 7)];
                }
            return o;
        }

        /// <summary>
        /// Copy the 4x4 blocks of rect (sx, sy, rw, rh) of block-compressed <paramref name="src"/> onto (dx, dy) of
        /// <paramref name="dst"/>; both must be 16-byte-per-block formats of the same kind (BC6H / BC7) and every
        /// coordinate a multiple of 4. Only the top mip is touched (the atlases ship with one).
        /// </summary>
        public static void CopyBlocks(byte[] src, byte[] dst, int sx, int sy, int dx, int dy, int rw, int rh)
        {
            if (((sx | sy | dx | dy | rw | rh) & 3) != 0) throw new ArgumentException("block copy needs 4-aligned rects");
            int sw, shh, dw, dh; uint sf, df;
            int so = DdsDataOffset(src, out sw, out shh, out sf), doff = DdsDataOffset(dst, out dw, out dh, out df);
            int sPitch = (sw + 3) / 4 * 16, dPitch = (dw + 3) / 4 * 16;
            for (int by = 0; by < rh / 4; by++)
                Array.Copy(src, so + (sy / 4 + by) * sPitch + sx / 4 * 16, dst, doff + (dy / 4 + by) * dPitch + dx / 4 * 16, rw / 4 * 16);
        }
    }
}
