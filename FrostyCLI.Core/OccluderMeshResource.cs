using System;
using System.Globalization;

namespace FrostyCLI.Core
{
    /// <summary>
    /// A mesh's OccluderMeshResource (res type OccluderMesh, 817145171 in both SWBF 2015 and BFII): the simplified geometry
    /// the software occlusion culler rasterises. Both games write the same 0x40-byte header, checked on 24 shipped BFII
    /// occluders and the 2015 Walker Assault ones:
    /// <code>
    /// 0x00 float3 min, pad      0x10 float3 max, pad
    /// 0x20 u16 vertexCount, u16 indexCount    0x24 u32 (second block offset)   0x28 u32 indexOffset (u16 indices)
    /// 0x2C u32 vertexOffset (0x40)            0x30 u32 flag (0 or 1)           0x34 u32 0   0x38 u32 1   0x3C u32 0
    /// </code>
    /// An empty occluder (2015 LightGround_01) is just the header: min = +FLT_MAX, max = -FLT_MAX, no vertices, every
    /// offset 0x40. The port carries the bytes verbatim; this only proves they parse under that layout before a mesh is
    /// pointed at them, because a mesh whose occluder cannot be read faults on creation (game+0x6E6ED6A).
    /// </summary>
    public static class OccluderMeshResource
    {
        public const uint ResType = 817145171;
        public const int HeaderSize = 0x40;

        public struct Header
        {
            public float[] Min, Max;
            public int VertexCount, IndexCount;
            public uint BlockOffset, IndexOffset, VertexOffset, Flag;
        }

        /// <summary>Null when <paramref name="data"/> is a well-formed occluder, else why not.</summary>
        public static string Check(byte[] data)
        {
            Header h;
            return TryParse(data, out h);
        }

        public static string TryParse(byte[] data, out Header h)
        {
            h = new Header();
            if (data == null || data.Length < HeaderSize) return "shorter than the 0x40-byte occluder header (" + (data == null ? 0 : data.Length) + " bytes)";
            h.Min = new[] { BitConverter.ToSingle(data, 0), BitConverter.ToSingle(data, 4), BitConverter.ToSingle(data, 8) };
            h.Max = new[] { BitConverter.ToSingle(data, 16), BitConverter.ToSingle(data, 20), BitConverter.ToSingle(data, 24) };
            h.VertexCount = BitConverter.ToUInt16(data, 0x20);
            h.IndexCount = BitConverter.ToUInt16(data, 0x22);
            h.BlockOffset = BitConverter.ToUInt32(data, 0x24);
            h.IndexOffset = BitConverter.ToUInt32(data, 0x28);
            h.VertexOffset = BitConverter.ToUInt32(data, 0x2C);
            h.Flag = BitConverter.ToUInt32(data, 0x30);
            if (h.VertexOffset != HeaderSize) return "vertex offset 0x" + h.VertexOffset.ToString("X") + ", expected 0x40";
            if (h.IndexCount % 3 != 0) return h.IndexCount + " indices is not a triangle list";
            if (h.BlockOffset < h.VertexOffset || h.BlockOffset > h.IndexOffset) return "block offsets out of order (0x" + h.VertexOffset.ToString("X") + ", 0x" + h.BlockOffset.ToString("X") + ", 0x" + h.IndexOffset.ToString("X") + ")";
            // (vertex storage is not a fixed stride: 8, 12 and ~13-18 bytes per vertex all occur in shipped occluders)
            if (h.VertexCount > 0 && h.BlockOffset == h.VertexOffset) return h.VertexCount + " vertices but no vertex block";
            if ((long)h.IndexOffset + (long)h.IndexCount * 2 > data.Length) return "indices run past the end (" + h.IndexCount + " at 0x" + h.IndexOffset.ToString("X") + " in " + data.Length + " bytes)";
            if (h.Flag > 1) return "flag at 0x30 is " + h.Flag + ", expected 0 or 1";
            if (h.VertexCount == 0 && h.IndexCount == 0) return null;
            for (int i = 0; i < h.IndexCount; i++)
            {
                int ix = BitConverter.ToUInt16(data, (int)h.IndexOffset + i * 2);
                if (ix >= h.VertexCount) return "index " + i + " = " + ix + " but only " + h.VertexCount + " vertices";
            }
            for (int k = 0; k < 3; k++)
                if (float.IsNaN(h.Min[k]) || float.IsNaN(h.Max[k]) || h.Min[k] > h.Max[k]) return "bounds are not a box";
            return null;
        }

        public static string Describe(byte[] data)
        {
            Header h;
            string err = TryParse(data, out h);
            if (err != null) return "unreadable: " + err;
            if (h.VertexCount == 0) return "empty";
            return string.Format(CultureInfo.InvariantCulture, "{0} vertices, {1} triangles, ({2:0.###},{3:0.###},{4:0.###})..({5:0.###},{6:0.###},{7:0.###})",
                h.VertexCount, h.IndexCount / 3, h.Min[0], h.Min[1], h.Min[2], h.Max[0], h.Max[1], h.Max[2]);
        }

        public static byte[] ParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            if (hex.Length % 2 != 0) return null;
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i])) return null;
            return b;
        }
    }
}
