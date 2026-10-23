// VENDORED from BfiiMapTools src\Bfii.Navmesh\Io\BlobWriter.cs (sha256:2A5127FB67AB3C4F).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Io
{
    /// <summary>
    /// Growable in-memory writer with explicit byte order per write and back-patching by offset.
    /// </summary>
    /// <remarks>
    /// Back-patching is unavoidable in this format: three separate size fields and every adjacency
    /// pointer are only known after the structures they describe have been written. The writer
    /// therefore exposes absolute-offset patch helpers rather than a seekable stream, so a patch
    /// can never accidentally extend the buffer.
    /// </remarks>
    public sealed class BlobWriter
    {
        private byte[] _data;
        private int _length;

        public BlobWriter(int capacity = 4096)
        {
            _data = new byte[Math.Max(16, capacity)];
            _length = 0;
        }

        public int Position => _length;

        private void Ensure(int extra)
        {
            int needed = _length + extra;
            if (needed <= _data.Length)
            {
                return;
            }

            int size = _data.Length;
            while (size < needed)
            {
                size *= 2;
            }

            Array.Resize(ref _data, size);
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            Ensure(value.Length);
            Array.Copy(value, 0, _data, _length, value.Length);
            _length += value.Length;
        }

        public void WriteZeros(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            Ensure(count);
            Array.Clear(_data, _length, count);
            _length += count;
        }

        public void WriteUInt32(uint value, ByteOrder order)
        {
            Ensure(4);
            PutUInt32(_data, _length, value, order);
            _length += 4;
        }

        public void WriteInt32(int value, ByteOrder order) => WriteUInt32(unchecked((uint)value), order);

        public void WriteUInt64(ulong value, ByteOrder order)
        {
            uint hi = (uint)(value >> 32);
            uint lo = (uint)(value & 0xFFFFFFFFu);
            if (order == ByteOrder.Big)
            {
                WriteUInt32(hi, order);
                WriteUInt32(lo, order);
            }
            else
            {
                WriteUInt32(lo, order);
                WriteUInt32(hi, order);
            }
        }

        public void WriteSingle(float value, ByteOrder order) =>
            WriteUInt32(BitConverterCompat.SingleToUInt32(value), order);

        public void WriteVec3(Vec3 value, ByteOrder order)
        {
            WriteSingle(value.X, order);
            WriteSingle(value.Y, order);
            WriteSingle(value.Z, order);
        }

        public void WriteBounds3(Bounds3 value, ByteOrder order)
        {
            WriteVec3(value.Min, order);
            WriteVec3(value.Max, order);
        }

        /// <summary>Writes a pointer-sized field; see BlobReader.ReadPointer for the width code.</summary>
        public void WritePointer(ulong value, ByteOrder order, uint pointerSizeCode)
        {
            if (pointerSizeCode == 1)
            {
                WriteUInt64(value, order);
            }
            else
            {
                WriteUInt32((uint)value, order);
            }
        }

        /// <summary>Overwrites four bytes already written. Never extends the buffer.</summary>
        public void PatchUInt32(int offset, uint value, ByteOrder order)
        {
            if (offset < 0 || offset + 4 > _length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset), offset, "patch target is outside the written region");
            }

            PutUInt32(_data, offset, value, order);
        }

        public void PatchInt32(int offset, int value, ByteOrder order) =>
            PatchUInt32(offset, unchecked((uint)value), order);

        /// <summary>Overwrites a pointer-sized field already written.</summary>
        public void PatchPointer(int offset, ulong value, ByteOrder order, uint pointerSizeCode)
        {
            if (pointerSizeCode == 1)
            {
                uint hi = (uint)(value >> 32);
                uint lo = (uint)(value & 0xFFFFFFFFu);
                if (order == ByteOrder.Big)
                {
                    PatchUInt32(offset, hi, order);
                    PatchUInt32(offset + 4, lo, order);
                }
                else
                {
                    PatchUInt32(offset, lo, order);
                    PatchUInt32(offset + 4, hi, order);
                }
            }
            else
            {
                PatchUInt32(offset, (uint)value, order);
            }
        }

        public byte[] ToArray()
        {
            byte[] result = new byte[_length];
            Array.Copy(_data, 0, result, 0, _length);
            return result;
        }

        private static void PutUInt32(byte[] target, int offset, uint value, ByteOrder order)
        {
            if (order == ByteOrder.Big)
            {
                target[offset] = (byte)(value >> 24);
                target[offset + 1] = (byte)(value >> 16);
                target[offset + 2] = (byte)(value >> 8);
                target[offset + 3] = (byte)value;
            }
            else
            {
                target[offset] = (byte)value;
                target[offset + 1] = (byte)(value >> 8);
                target[offset + 2] = (byte)(value >> 16);
                target[offset + 3] = (byte)(value >> 24);
            }
        }

        /// <summary>
        /// First offset at which two buffers differ, or -1 when identical. Used by the round-trip
        /// gate so a failure names a byte offset instead of only saying not equal.
        /// </summary>
        public static int FirstDifference(byte[] a, byte[] b)
        {
            if (a == null)
            {
                throw new ArgumentNullException(nameof(a));
            }

            if (b == null)
            {
                throw new ArgumentNullException(nameof(b));
            }

            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i])
                {
                    return i;
                }
            }

            return a.Length == b.Length ? -1 : n;
        }

        /// <summary>Byte offsets at which two equal-length buffers differ, capped at limit.</summary>
        public static IReadOnlyList<int> Differences(byte[] a, byte[] b, int limit)
        {
            List<int> result = new List<int>();
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n && result.Count < limit; i++)
            {
                if (a[i] != b[i])
                {
                    result.Add(i);
                }
            }

            return result;
        }
    }
}
