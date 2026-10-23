#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainFormat\Primitives.cs (sha256:98EADA0712FC2F11).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
// using System.Buffers.Binary;  -- replaced with byte arithmetic below, for net48

namespace Bfii.TerrainFormat
{
    /// <summary>
    /// Target profile constants.
    /// Provenance: FROSTY_1063 — <c>FrostySdk.ProfileVersion.StarWarsBattlefrontII = 20171117</c>
    /// (installed 1.0.6.3 assembly). Master plan §10 C-06: the research pack's 20171116 is not the
    /// value any BFII-conditional branch tests. Code uses 20171117.
    /// </summary>
    public static class BfiiProfile
    {
        public const int DataVersion = 20171117;
        public const string ProfileName = "starwarsbattlefrontii";
        public const int GameBuild = 489592;

        /// <summary>
        /// Size of the <c>resMeta</c> blob that lives in the bundle record, and which some exporters
        /// prepend to a dumped <c>.res</c> file. Provenance: BFII_EXACT (W01 §4.3, §4.8);
        /// master plan §10 C-07.
        /// </summary>
        public const int ResMetaSize = 16;
    }

    /// <summary>
    /// A 32-bit float carried as its raw IEEE-754 bit pattern.
    /// <para>
    /// Identity round-trip (master plan §7 M1 gate D1) requires that every byte we emit equals the
    /// byte we read. Passing a payload through a <c>float</c> local is not bit-preserving for every
    /// possible encoding (a signalling NaN may be quieted), so the parser never materialises one.
    /// Consumers that want the numeric value ask for <see cref="Value"/> explicitly.
    /// </para>
    /// </summary>
    public readonly struct F32 : IEquatable<F32>
    {
        public readonly uint Bits;

        public F32(uint bits) => Bits = bits;

        public static F32 FromValue(float value) => new F32(BitConverter.ToUInt32(BitConverter.GetBytes(value), 0));

        public float Value => BitConverter.ToSingle(BitConverter.GetBytes(Bits), 0);

        public bool Equals(F32 other) => Bits == other.Bits;
        public override bool Equals(object? obj) => obj is F32 other && Equals(other);
        public override int GetHashCode() => (int)Bits;
        public override string ToString() => Value.ToString("R");
    }

    /// <summary>
    /// Axis-aligned 3D box as stored in a heightfield node record: six consecutive f32.
    /// Provenance: BFII_EXACT — W01 §4.4.1; both independent readers agree on the order.
    /// </summary>
    public struct AxisAlignedBox
    {
        public F32 MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
    }

    /// <summary>
    /// Axis-aligned 2D box: four consecutive f32 (minX, minY, maxX, maxY).
    /// Provenance: BFII_EXACT — W01 §4.4.2 (mask tree coverage and subtile world coverage).
    /// </summary>
    public struct AxisAlignedBox2
    {
        public F32 MinX, MinY, MaxX, MaxY;
    }

    /// <summary>
    /// Quadtree node identity. Child (x,y) offsets are <c>(0,0) (1,0) (1,1) (0,1)</c> in that order.
    /// Provenance: BFII_EXACT — W01 §4.4; the same order appears in every BFII raster tree walk.
    /// </summary>
    public readonly struct QuadtreeNodeId : IEquatable<QuadtreeNodeId>
    {
        public static readonly int[] ChildOffsetX = { 0, 1, 1, 0 };
        public static readonly int[] ChildOffsetY = { 0, 0, 1, 1 };

        public readonly ushort IndexX;
        public readonly ushort IndexY;
        public readonly byte Level;

        public QuadtreeNodeId(ushort indexX, ushort indexY, byte level)
        {
            IndexX = indexX;
            IndexY = indexY;
            Level = level;
        }

        public QuadtreeNodeId Child(int i) => new QuadtreeNodeId(
            (ushort)(ChildOffsetX[i] + 2 * IndexX),
            (ushort)(ChildOffsetY[i] + 2 * IndexY),
            (byte)(Level + 1));

        public bool Equals(QuadtreeNodeId other) =>
            IndexX == other.IndexX && IndexY == other.IndexY && Level == other.Level;

        public override bool Equals(object? obj) => obj is QuadtreeNodeId o && Equals(o);
        public override int GetHashCode() => (IndexX << 17) ^ (IndexY << 1) ^ Level;
        public override string ToString() => $"{IndexX}x{IndexY}:{Level}";
    }

    /// <summary>Raised when a payload cannot be parsed under the current options.</summary>
    public sealed class TerrainFormatException : Exception
    {
        public long Position { get; }

        public TerrainFormatException(string message, long position = -1) : base(message)
            => Position = position;
    }

    /// <summary>
    /// Bounds-checked little-endian reader over a byte window.
    /// <para>
    /// Every terrain RES scalar is little-endian. Provenance: BFII_EXACT — W01 §4.8
    /// (<c>get_u32_le</c>/<c>get_f32_le</c>/<c>get_u16_le</c> in the Rust reader; Frosty's
    /// <c>NativeReader</c> is LE).
    /// </para>
    /// <para>
    /// Out-of-window reads throw rather than returning garbage. That property is what makes the
    /// mask-stride search (<see cref="MaskStride"/>) safe: a wrong stride desynchronises
    /// immediately and aborts instead of allocating from a bogus length.
    /// </para>
    /// </summary>
    public sealed class ByteReader
    {
        private readonly byte[] _data;
        private readonly int _start;
        private readonly int _end;
        private int _pos;

        public ByteReader(byte[] data) : this(data, 0, data.Length) { }

        public ByteReader(byte[] data, int start, int length)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (start < 0 || length < 0 || start + length > data.Length)
                throw new ArgumentOutOfRangeException(nameof(length), "window outside buffer");
            _data = data;
            _start = start;
            _end = start + length;
            _pos = start;
        }

        /// <summary>Absolute position in the underlying buffer.</summary>
        public int Position
        {
            get => _pos;
            set
            {
                if (value < _start || value > _end)
                    throw new TerrainFormatException($"seek to {value} outside window [{_start},{_end}]", value);
                _pos = value;
            }
        }

        public int WindowStart => _start;
        public int WindowEnd => _end;
        public int Remaining => _end - _pos;
        public int Consumed => _pos - _start;

        private void Need(int n)
        {
            if (n < 0 || _end - _pos < n)
                throw new TerrainFormatException(
                    $"read of {n} byte(s) at {_pos} exceeds window end {_end}", _pos);
        }

        public byte ReadByte()
        {
            Need(1);
            return _data[_pos++];
        }

        public ushort ReadUInt16()
        {
            Need(2);
            ushort v = (ushort)(_data[_pos] | (_data[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public uint ReadUInt32()
        {
            Need(4);
            uint v = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
            _pos += 4;
            return v;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public F32 ReadF32() => new F32(ReadUInt32());

        public Guid ReadGuid()
        {
            Need(16);
            var g = GuidBytes.Read(_data, _pos);
            _pos += 16;
            return g;
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
                throw new TerrainFormatException($"negative read length {count} at {_pos}", _pos);
            Need(count);
            var buf = new byte[count];
            Buffer.BlockCopy(_data, _pos, buf, 0, count);
            _pos += count;
            return buf;
        }

        public AxisAlignedBox ReadBox()
        {
            AxisAlignedBox b;
            b.MinX = ReadF32(); b.MinY = ReadF32(); b.MinZ = ReadF32();
            b.MaxX = ReadF32(); b.MaxY = ReadF32(); b.MaxZ = ReadF32();
            return b;
        }

        public AxisAlignedBox2 ReadBox2()
        {
            AxisAlignedBox2 b;
            b.MinX = ReadF32(); b.MinY = ReadF32(); b.MaxX = ReadF32(); b.MaxY = ReadF32();
            return b;
        }
    }

    /// <summary>Growable little-endian writer. Mirrors <see cref="ByteReader"/> exactly.</summary>
    public sealed class ByteWriter
    {
        private byte[] _buf;
        private int _len;

        public ByteWriter(int capacity = 4096) => _buf = new byte[Math.Max(16, capacity)];

        public int Length => _len;

        private void Need(int n)
        {
            if (_len + n <= _buf.Length) return;
            int cap = _buf.Length;
            while (cap < _len + n) cap *= 2;
            Array.Resize(ref _buf, cap);
        }

        public void WriteByte(byte v)
        {
            Need(1);
            _buf[_len++] = v;
        }

        public void WriteUInt16(ushort v)
        {
            Need(2);
            _buf[_len] = (byte)v; _buf[_len + 1] = (byte)(v >> 8);
            _len += 2;
        }

        public void WriteUInt32(uint v)
        {
            Need(4);
            _buf[_len] = (byte)v; _buf[_len + 1] = (byte)(v >> 8); _buf[_len + 2] = (byte)(v >> 16); _buf[_len + 3] = (byte)(v >> 24);
            _len += 4;
        }

        public void WriteInt32(int v) => WriteUInt32(unchecked((uint)v));

        public void WriteF32(F32 v) => WriteUInt32(v.Bits);

        public void WriteGuid(Guid g)
        {
            Need(16);
            if (!GuidBytes.TryWrite(g, _buf, _len))
                throw new InvalidOperationException("Guid.TryWriteBytes failed");
            _len += 16;
        }

        public void WriteBytes(byte[] data)
        {
            if (data is null || data.Length == 0) return;
            Need(data.Length);
            Buffer.BlockCopy(data, 0, _buf, _len, data.Length);
            _len += data.Length;
        }

        public void WriteBox(in AxisAlignedBox b)
        {
            WriteF32(b.MinX); WriteF32(b.MinY); WriteF32(b.MinZ);
            WriteF32(b.MaxX); WriteF32(b.MaxY); WriteF32(b.MaxZ);
        }

        public void WriteBox2(in AxisAlignedBox2 b)
        {
            WriteF32(b.MinX); WriteF32(b.MinY); WriteF32(b.MaxX); WriteF32(b.MaxY);
        }

        /// <summary>Back-patch a u32 already written at <paramref name="offset"/>.</summary>
        public void PatchUInt32(int offset, uint value)
        {
            if (offset < 0 || offset + 4 > _len)
                throw new ArgumentOutOfRangeException(nameof(offset));
            _buf[offset] = (byte)value; _buf[offset + 1] = (byte)(value >> 8); _buf[offset + 2] = (byte)(value >> 16); _buf[offset + 3] = (byte)(value >> 24);
        }

        public byte[] ToArray()
        {
            var outBuf = new byte[_len];
            Buffer.BlockCopy(_buf, 0, outBuf, 0, _len);
            return outBuf;
        }
    }
}
