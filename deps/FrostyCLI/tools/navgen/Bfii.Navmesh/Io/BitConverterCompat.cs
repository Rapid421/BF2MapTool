// VENDORED from BfiiMapTools src\Bfii.Navmesh\Io\BitConverterCompat.cs (sha256:9B1BC3AF4D130FFD).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;

namespace Bfii.Navmesh.Io
{
    /// <summary>
    /// float to uint bit reinterpretation that behaves identically on every runtime this assembly
    /// targets.
    /// </summary>
    /// <remarks>
    /// netstandard2.0 has no BitConverter.SingleToUInt32Bits, and BitConverter.GetBytes depends on
    /// host endianness, which is exactly the variable this library must control. Going through
    /// int keeps the conversion host-independent: the returned uint is the IEEE-754 bit pattern in
    /// logical order, and the byte order is applied afterwards by the reader/writer.
    /// </remarks>
    internal static class BitConverterCompat
    {
        public static uint SingleToUInt32(float value) =>
            unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public static float UInt32ToSingle(uint bits) =>
            BitConverter.ToSingle(BitConverter.GetBytes(unchecked((int)bits)), 0);
    }
}
