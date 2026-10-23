// VENDORED from BfiiMapTools src\Bfii.Navmesh\Io\BlobReader.cs (sha256:00395C22D486BACF).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Io
{
    /// <summary>
    /// Forward cursor over an in-memory navigation image with explicit byte order per read.
    /// </summary>
    /// <remarks>
    /// Deliberately a struct-free class over a plain byte[] rather than a Stream: every offset in
    /// this format is expressed relative to a structure start, and several of them are patched
    /// after the fact, so random access into one contiguous buffer is the natural shape. Every
    /// read is bounds checked and throws <see cref="NavFormatException"/> rather than
    /// IndexOutOfRange, so a truncated donor reports a diagnosable error.
    /// </remarks>
    public sealed class BlobReader
    {
        private readonly byte[] _data;

        public BlobReader(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            Position = 0;
        }

        /// <summary>Absolute byte offset of the cursor.</summary>
        public int Position { get; set; }

        public int Length => _data.Length;

        public int Remaining => _data.Length - Position;

        /// <summary>The underlying buffer. Used for verbatim slice preservation.</summary>
        public byte[] Buffer => _data;

        public void Require(int count, string what)
        {
            if (count < 0)
            {
                throw new NavFormatException(
                    "negative read length " + count + " for " + what + " at offset " + Position);
            }

            if (Position + count > _data.Length)
            {
                throw new NavFormatException(
                    "truncated image: " + what + " needs " + count + " bytes at offset " +
                    Position + " but only " + Remaining + " remain");
            }
        }

        public byte[] ReadBytes(int count, string what)
        {
            Require(count, what);
            byte[] result = new byte[count];
            Array.Copy(_data, Position, result, 0, count);
            Position += count;
            return result;
        }

        public void Skip(int count, string what)
        {
            Require(count, what);
            Position += count;
        }

        public uint ReadUInt32(ByteOrder order, string what)
        {
            Require(4, what);
            int p = Position;
            Position += 4;
            return order == ByteOrder.Big
                ? ((uint)_data[p] << 24) | ((uint)_data[p + 1] << 16) | ((uint)_data[p + 2] << 8) | _data[p + 3]
                : ((uint)_data[p + 3] << 24) | ((uint)_data[p + 2] << 16) | ((uint)_data[p + 1] << 8) | _data[p];
        }

        public int ReadInt32(ByteOrder order, string what) => unchecked((int)ReadUInt32(order, what));

        public ulong ReadUInt64(ByteOrder order, string what)
        {
            Require(8, what);
            uint a = ReadUInt32(order, what);
            uint b = ReadUInt32(order, what);
            return order == ByteOrder.Big
                ? ((ulong)a << 32) | b
                : ((ulong)b << 32) | a;
        }

        public float ReadSingle(ByteOrder order, string what)
        {
            uint bits = ReadUInt32(order, what);
            return BitConverterCompat.UInt32ToSingle(bits);
        }

        public Vec3 ReadVec3(ByteOrder order, string what) => new Vec3(
            ReadSingle(order, what),
            ReadSingle(order, what),
            ReadSingle(order, what));

        public Bounds3 ReadBounds3(ByteOrder order, string what) => new Bounds3(
            ReadVec3(order, what),
            ReadVec3(order, what));

        /// <summary>
        /// Reads a pointer-sized field. The image records pointer width as a code, not a byte
        /// count: code 1 means 64-bit pointers, anything else means 32-bit.
        /// COMMUNITY_TESTED (FORMAT-PROVENANCE.md, row H4).
        /// </summary>
        public ulong ReadPointer(ByteOrder order, uint pointerSizeCode, string what) =>
            pointerSizeCode == 1 ? ReadUInt64(order, what) : ReadUInt32(order, what);
    }
}
