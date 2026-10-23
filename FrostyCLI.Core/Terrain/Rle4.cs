#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainFormat\Rle4.cs (sha256:B593AFE6132266A1).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainFormat
{
    /// <summary>
    /// The per-line RLE of the material (3) and destruction depth (4) raster trees, and the 4-bit
    /// packing under it.
    /// <para>
    /// A decoded line is a run of packed bytes, each holding two 4-bit samples (low nibble = the even
    /// sample). The RLE over those bytes: two equal consecutive bytes <c>a a</c> are always followed by a
    /// count <c>n</c> and stand for <c>n + 1</c> copies of <c>a</c>; any other byte is a literal. A run
    /// is at most 256 bytes, so a line may be at most 256 bytes wide.
    /// </para>
    /// <para>
    /// Provenance: BFII_EXACT, 2026-09-23 — <see cref="EncodeLine"/> reproduces every one of the
    /// 904,970 shipped lines (645,376 material, 259,594 destruction depth, 39 terrains) byte for byte
    /// from its own decode. The scheme's shape matches FIFA's <c>PipelineTerrainUtility::rleCompress</c>
    /// (FIFA21_SOURCE); the code here is written from the measured format, not transliterated.
    /// </para>
    /// </summary>
    public static class Rle4
    {
        public const int MaxLineBytes = 256;

        public static byte[] DecodeLine(byte[] src, int offset, int count)
        {
            if (src is null) throw new ArgumentNullException(nameof(src));
            var output = new List<byte>(MaxLineBytes);
            int end = offset + count;
            int i = offset;
            while (i < end)
            {
                byte a = src[i];
                if (i + 1 < end && src[i + 1] == a)
                {
                    if (i + 2 >= end)
                        throw new TerrainFormatException($"RLE run at {i} has no count byte");
                    int n = src[i + 2] + 1;
                    for (int k = 0; k < n; k++) output.Add(a);
                    i += 3;
                }
                else
                {
                    output.Add(a);
                    i++;
                }
            }
            return output.ToArray();
        }

        public static byte[] EncodeLine(byte[] packed) => EncodeLine(packed, 0, packed.Length);

        public static byte[] EncodeLine(byte[] packed, int offset, int count)
        {
            if (packed is null) throw new ArgumentNullException(nameof(packed));
            if (count > MaxLineBytes)
                throw new ArgumentOutOfRangeException(nameof(count), $"an RLE line holds at most {MaxLineBytes} bytes");
            if (count == 0) return Array.Empty<byte>();

            var output = new List<byte>(count + 4);
            int i = offset, end = offset + count;
            while (i < end)
            {
                byte a = packed[i];
                int run = 1;
                while (i + run < end && packed[i + run] == a && run < 256) run++;
                if (run >= 2)
                {
                    output.Add(a);
                    output.Add(a);
                    output.Add((byte)(run - 1));
                }
                else
                {
                    output.Add(a);
                }
                i += run;
            }
            return output.ToArray();
        }

        /// <summary>Split a node's RLE bytes into its lines and decode each.</summary>
        public static byte[][] DecodeNode(RleRasterNode node)
        {
            if (node is null) throw new ArgumentNullException(nameof(node));
            var lines = new byte[node.LineSizes.Length][];
            int p = 0;
            for (int l = 0; l < lines.Length; l++)
            {
                int size = node.LineSizes[l];
                if (p + size > node.RleData.Length)
                    throw new TerrainFormatException($"node {node.Id}: line {l} overruns the RLE data");
                lines[l] = DecodeLine(node.RleData, p, size);
                p += size;
            }
            return lines;
        }

        /// <summary>Encode packed lines into a node's RLE bytes, line sizes and declared size.</summary>
        public static void EncodeNode(RleRasterNode node, byte[][] packedLines)
        {
            if (node is null) throw new ArgumentNullException(nameof(node));
            if (packedLines is null) throw new ArgumentNullException(nameof(packedLines));
            var all = new List<byte>();
            var sizes = new ushort[packedLines.Length];
            for (int l = 0; l < packedLines.Length; l++)
            {
                byte[] enc = EncodeLine(packedLines[l]);
                sizes[l] = checked((ushort)enc.Length);
                all.AddRange(enc);
            }
            node.RleData = all.ToArray();
            node.LineSizes = sizes;
            node.DeclaredRleSize = node.RleData.Length;
        }

        /// <summary>Two 4-bit samples per byte, low nibble first. An odd count is padded with the last sample.</summary>
        public static byte[] Pack(byte[] samples, int offset, int count)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            var packed = new byte[(count + 1) / 2];
            for (int i = 0; i < packed.Length; i++)
            {
                int s0 = samples[offset + Math.Min(2 * i, count - 1)] & 0x0F;
                int s1 = samples[offset + Math.Min(2 * i + 1, count - 1)] & 0x0F;
                packed[i] = (byte)(s0 | (s1 << 4));
            }
            return packed;
        }

        public static byte[] Unpack(byte[] packed, int samples)
        {
            if (packed is null) throw new ArgumentNullException(nameof(packed));
            var output = new byte[samples];
            for (int i = 0; i < samples; i++)
            {
                byte b = packed[i / 2];
                output[i] = (byte)((i & 1) == 0 ? b & 0x0F : b >> 4);
            }
            return output;
        }
    }
}
