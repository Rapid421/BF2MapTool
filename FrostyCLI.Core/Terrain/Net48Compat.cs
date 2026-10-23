using System;

namespace Bfii.TerrainFormat
{
    // Hand-written, NOT vendored: the .NET Framework 4.8 stand-ins that tools\sync-terrain-format.ps1
    // rewrites the upstream net8.0 span calls into. Kept in its own file so re-syncing the format
    // sources never touches it.
    //
    // Guid's span constructor and TryWriteBytes use the same mixed-endian field layout as the
    // byte[] constructor and ToByteArray (first three fields little-endian, last eight raw), so
    // these read and write byte-identical Guids to the span forms they replace.
    internal static class GuidBytes
    {
        public static Guid Read(byte[] data, int offset)
        {
            var b = new byte[16];
            Buffer.BlockCopy(data, offset, b, 0, 16);
            return new Guid(b);
        }

        public static bool TryWrite(Guid value, byte[] buffer, int offset)
        {
            if (buffer == null || offset < 0 || offset + 16 > buffer.Length) return false;
            Buffer.BlockCopy(value.ToByteArray(), 0, buffer, offset, 16);
            return true;
        }
    }
}
