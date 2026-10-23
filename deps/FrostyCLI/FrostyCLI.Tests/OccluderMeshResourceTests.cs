using System;
using System.IO;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class OccluderMeshResourceTests
    {
        // A quad (4 vertices, 2 triangles) laid out like shipped occluders: 0x40 header, vertex block at 0x40, a second
        // block, u16 indices, padded to 16.
        private static byte[] Quad(ushort vertexCount = 4, ushort indexCount = 6, uint flag = 0, ushort badIndex = 0)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(0);
            w.Write(2f); w.Write(0f); w.Write(3f); w.Write(0);
            w.Write(vertexCount); w.Write(indexCount);
            w.Write(0x70u); w.Write(0xB0u); w.Write(0x40u);
            w.Write(flag); w.Write(0u); w.Write(1u); w.Write(0u);
            while (ms.Length < 0x70) w.Write((byte)0);
            while (ms.Length < 0xB0) w.Write((byte)0);
            ushort[] idx = { 0, 1, 2, 2, 1, badIndex != 0 ? badIndex : (ushort)3 };
            for (int i = 0; i < indexCount && i < idx.Length; i++) w.Write(idx[i]);
            while (ms.Length % 16 != 0) w.Write((byte)0);
            return ms.ToArray();
        }

        // SWBF 2015 LightGround_01's occluder: header only, inverted bounds, every offset 0x40.
        private static byte[] Empty2015()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            for (int i = 0; i < 3; i++) w.Write(0x7F7F7F7Fu);
            w.Write(0u);
            for (int i = 0; i < 3; i++) w.Write(0xFF7F7F7Fu);
            w.Write(0u);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0x40u); w.Write(0x40u); w.Write(0x40u);
            w.Write(0u); w.Write(0u); w.Write(1u); w.Write(0u);
            return ms.ToArray();
        }

        [Test]
        public void Check_AcceptsShippedLayout()
        {
            Assert.IsNull(OccluderMeshResource.Check(Quad()));
            Assert.IsNull(OccluderMeshResource.Check(Quad(flag: 1)));
            Assert.AreEqual("4 vertices, 2 triangles, (0,0,0)..(2,0,3)", OccluderMeshResource.Describe(Quad()));
        }

        [Test]
        public void Check_AcceptsEmptyOccluder()
        {
            byte[] e = Empty2015();
            Assert.AreEqual(0x40, e.Length);
            Assert.IsNull(OccluderMeshResource.Check(e));
            Assert.AreEqual("empty", OccluderMeshResource.Describe(e));
        }

        [Test]
        public void Check_RejectsMalformed()
        {
            Assert.IsNotNull(OccluderMeshResource.Check(new byte[0x20]));
            Assert.IsNotNull(OccluderMeshResource.Check(Quad(flag: 2)));
            Assert.IsNotNull(OccluderMeshResource.Check(Quad(indexCount: 5)));
            Assert.IsNotNull(OccluderMeshResource.Check(Quad(badIndex: 4)));
            byte[] cut = Quad();
            Array.Resize(ref cut, 0xB4);                      // indices run past the end
            Assert.IsNotNull(OccluderMeshResource.Check(cut));
            byte[] moved = Quad();
            BitConverter.GetBytes(0x50u).CopyTo(moved, 0x2C);   // vertex block not at 0x40
            Assert.IsNotNull(OccluderMeshResource.Check(moved));
        }

        [Test]
        public void ParseHex_RoundTripsResMeta()
        {
            byte[] b = OccluderMeshResource.ParseHex("C018000000000000E0000000A0006001");
            Assert.AreEqual(16, b.Length);
            Assert.AreEqual((byte)0xC0, b[0]);
            Assert.AreEqual((byte)0x01, b[15]);
            Assert.IsNull(OccluderMeshResource.ParseHex("ABC"));
            Assert.IsNull(OccluderMeshResource.ParseHex(null));
        }
    }
}
