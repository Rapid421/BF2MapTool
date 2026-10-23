using System;
using System.Collections.Generic;
using FrostyCLI.Adapters.Texture;

namespace FrostyCLI.Tests
{
    public class TextureArrayHeaderTests
    {
        static void Block(List<byte> o, uint dsize, int csize, int code = 0x0970)
        {
            o.Add((byte)(dsize >> 24)); o.Add((byte)(dsize >> 16)); o.Add((byte)(dsize >> 8)); o.Add((byte)dsize);
            code |= (csize >> 16) & 0xF;
            o.Add((byte)(code >> 8)); o.Add((byte)code);
            o.Add((byte)((csize >> 8) & 0xFF)); o.Add((byte)(csize & 0xFF));
            for (int i = 0; i < csize; i++) o.Add(0);
        }

        // a 128^2 RGBA16F cube, mip-major: mip 0 of all six faces = 786432 B = 12 blocks of 64 KiB, mip 1 ends at 983040
        // (15 blocks); the header offsets are the compressed positions where mip 1 and mip 2 start
        [Test]
        public void CompressedOffsets_FindsMipStartsOnBlockBoundaries()
        {
            var o = new List<byte>();
            int[] sizes = { 40000, 41000, 42000, 43000, 44000, 45000, 46000, 47000, 48000, 49000, 50000, 70000, 30000, 31000, 32000, 5000, 900 };
            long expect1 = 0, expect2 = 0, pos = 0;
            for (int i = 0; i < sizes.Length; i++)
            {
                Block(o, i < 16 ? 0x10000u : 0x4000u, sizes[i]);
                pos += 8 + sizes[i];
                if (i == 11) expect1 = pos;
                if (i == 14) expect2 = pos;
            }
            uint[] r = HeadlessTextureImporter.CompressedOffsets(o.ToArray(), new long[] { 786432, 983040 });
            Assert.IsNotNull(r);
            Assert.AreEqual((uint)expect1, r[0]);
            Assert.AreEqual((uint)expect2, r[1]);
            // a block bigger than 64 KiB compressed (70000) carries bit 16 in the code nibble: counted, not truncated
            Assert.IsTrue(expect1 > 11 * 8 + 500000, "sizes summed with the code nibble");
            Assert.IsNull(HeadlessTextureImporter.CompressedOffsets(o.ToArray(), new long[] { 786433 }));
        }
    }
}
