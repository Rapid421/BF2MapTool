using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FrostyCLI.Core.Enlighten;

namespace FrostyCLI.Tests
{
    public class Swbf1EnlightenTests
    {
        // A small 2015 END: 0x50 header, 2 mesh instances, 1 terrain record, 2 uv transforms, 2 probe sets whose
        // +0xD0 points at a shared index list and +0xD8 at the positions (the 2015 layout).
        private static byte[] SampleEnd2015()
        {
            var ms = new MemoryStream();
            var h = new byte[0x50];
            EnlightenDatabase.Put(h, 0, 836); EnlightenDatabase.Put(h, 4, 900); EnlightenDatabase.Put(h, 8, 1);
            EnlightenDatabase.Put(h, 0x10, 2); EnlightenDatabase.Put(h, 0x14, 1);
            EnlightenDatabase.PutU64(h, 0x18, 0x50);
            long pTerr = 0x50 + 2 * 32, pUv = pTerr + 48, pProbe = pUv + 2 * 32, pIdx = pProbe + 2 * 240, pPos = pIdx + 32;
            EnlightenDatabase.PutU64(h, 0x20, (ulong)pTerr);
            EnlightenDatabase.Put(h, 0x34, 2); EnlightenDatabase.PutU64(h, 0x38, (ulong)pProbe);
            ms.Write(h, 0, h.Length);
            for (int i = 0; i < 2; i++)
            {
                var r = new byte[32];
                EnlightenDatabase.Put(r, 0, 100u + (uint)i); EnlightenDatabase.Put(r, 4, 1);
                EnlightenDatabase.PutU64(r, 8, (ulong)(pUv + i * 32));
                ms.Write(r, 0, 32);
            }
            var t = new byte[48];
            EnlightenDatabase.PutF(t, 0, 0.001f); EnlightenDatabase.PutF(t, 12, 0.002f); EnlightenDatabase.PutF(t, 16, 0.5f);
            EnlightenDatabase.PutF(t, 32, -256); EnlightenDatabase.PutF(t, 44, 256);
            ms.Write(t, 0, 48);
            for (int i = 0; i < 2; i++)
            {
                var uv = new byte[32];
                EnlightenDatabase.PutF(uv, 0, 0.01f * (i + 1)); EnlightenDatabase.PutF(uv, 12, 0.02f);
                EnlightenDatabase.PutF(uv, 16, 0.25f); EnlightenDatabase.PutF(uv, 20, 0.75f);
                ms.Write(uv, 0, 32);
            }
            for (int i = 0; i < 2; i++)
            {
                var ps = new byte[240];
                for (int k = 0; k < 16; k++) ps[k] = (byte)(i * 16 + k);
                uint n = i == 0 ? 3u : 1u;
                EnlightenDatabase.Put(ps, 0xB4, n); EnlightenDatabase.Put(ps, 0xB8, 1); EnlightenDatabase.Put(ps, 0xBC, 1);
                EnlightenDatabase.Put(ps, 0xC4, n); EnlightenDatabase.Put(ps, 0xC8, i == 0 ? 0u : 3u);
                ps[0xCC] = (byte)(i == 0 ? 1 : 0);
                EnlightenDatabase.PutU64(ps, 0xD0, (ulong)pIdx);
                EnlightenDatabase.PutU64(ps, 0xD8, (ulong)(pPos + (i == 0 ? 0 : 3 * 16)));
                ms.Write(ps, 0, 240);
            }
            for (int k = 0; k < 8; k++) ms.Write(BitConverter.GetBytes(k), 0, 4);
            for (int p = 0; p < 4; p++)
                foreach (float f in new[] { p * 1f, 2f * p - 3000f, 3f * p, 0f }) ms.Write(BitConverter.GetBytes(f), 0, 4);
            return ms.ToArray();
        }

        [Test]
        public void ParseEnd_ConvertsProbeSetsToTheBfiiLayout()
        {
            EnlightenDatabase db = Swbf1Enlighten.ParseEnd(SampleEnd2015());
            Assert.AreEqual(836u, db.Width);
            Assert.AreEqual(900u, db.Height);
            Assert.AreEqual(2, db.MeshInstances.Count);
            Assert.AreEqual(101u, db.MeshInstances[1].Hash);
            Assert.AreEqual(0.02f, db.MeshInstances[1].ScaleU());
            Assert.AreEqual(1, db.Terrain.Count);
            Assert.AreEqual(2, db.ProbeSets.Count);
            Assert.AreEqual(3u, db.ProbeSets[0].Count);
            Assert.AreEqual(1, (int)db.ProbeSets[0].Raw[0xCC]);
            Assert.AreEqual(0xFF, (int)db.ProbeSets[0].Raw[0xCE]);
            for (int o = 0xD0; o < 240; o++) Assert.IsTrue(db.ProbeSets[1].Raw[o] == 0, "byte " + o);
            float x, y, z;
            db.ProbeSets[1].Position(0, out x, out y, out z);
            Assert.AreEqual(3f, x); Assert.AreEqual(-2994f, y); Assert.AreEqual(9f, z);
        }

        [Test]
        public void ParsedEnd_WritesAsAParsableBfiiDatabase()
        {
            EnlightenDatabase db = Swbf1Enlighten.ParseEnd(SampleEnd2015());
            var bfii = new byte[EnlightenDatabase.HeaderSize];
            EnlightenDatabase.Put(bfii, 0x10, 2); EnlightenDatabase.Put(bfii, 0x14, 1);
            EnlightenDatabase.Put(bfii, 0, db.Width); EnlightenDatabase.Put(bfii, 4, db.Height);
            db.Header = bfii;
            byte[] meta, data = db.Write(out meta);
            EnlightenDatabase back = EnlightenDatabase.Parse(data, meta);
            Assert.IsTrue(back.Unmodelled == null, back.UnmodelledReason);
            Assert.AreEqual(2, back.ProbeSets.Count);
            Assert.AreEqual(4, back.ProbeSets.Sum(p => (int)p.Count));
            float x, y, z;
            back.ProbeSets[0].Position(2, out x, out y, out z);
            Assert.AreEqual(2f, x); Assert.AreEqual(-2996f, y);
        }

        [Test]
        public void R9G9B9E5_RoundTripsWithinHalfAMantissaStep()
        {
            foreach (float v in new[] { 0f, 1f, 0.0123f, 5.69793f, 20.1875f, 40375f, 11400.5f })
            {
                uint e = AtlasIo.EncodeR9G9B9E5(v, v * 0.5f, v * 0.25f);
                float r, g, b;
                AtlasIo.DecodeR9G9B9E5(e, out r, out g, out b);
                double tol = Math.Max(1e-9, v / 511.0);
                Assert.IsTrue(Math.Abs(r - v) <= tol, "r " + v + " -> " + r);
                Assert.IsTrue(Math.Abs(g - v * 0.5f) <= tol, "g " + v + " -> " + g);
            }
            Assert.AreEqual(256u | (256u << 9) | (256u << 18) | (16u << 27), AtlasIo.EncodeR9G9B9E5(1f, 1f, 1f));
        }

        [Test]
        public void DecodeBc4_InterpolatesTheEightEntryPalette()
        {
            // a0 255 > a1 0: eight entries; texel t takes index t % 8
            var block = new byte[8];
            block[0] = 255; block[1] = 0;
            ulong bits = 0;
            for (int t = 0; t < 16; t++) bits |= (ulong)(t % 8) << (3 * t);
            for (int k = 0; k < 6; k++) block[2 + k] = (byte)(bits >> (8 * k));
            byte[] o = AtlasIo.DecodeBc4(block, 0, 4, 4);
            Assert.AreEqual(255, (int)o[0]);
            Assert.AreEqual(0, (int)o[1]);
            Assert.AreEqual(219, (int)o[2]);     // (6 * 255 + 0) / 7 = 218.57
            Assert.AreEqual(36, (int)o[7]);      // (255 + 6 * 0) / 7 = 36.4
        }

        [Test]
        public void LightmapKey_TenIntsIsThe2015Hash()
        {
            var g = new Guid("036cd457-517f-453f-986e-3a77fb69ed39");
            double[] r = { 1, 0, 0 }, u = { 0, 1, 0 }, f = { 0, 0, 1 }, t = { 217.5, 360.25, -1641.875 };
            var bytes = new List<byte>(g.ToByteArray());
            foreach (int i in new[] { 21750, 36025, -164187, 0, 0, 0, 10000, 100, 100, 100 }) bytes.AddRange(BitConverter.GetBytes(i));
            Assert.AreEqual(LightmapKey.Fnv1(bytes), LightmapKey.Compute(g, r, u, f, t, 10));
            bytes.AddRange(BitConverter.GetBytes(0));
            Assert.AreEqual(LightmapKey.Fnv1(bytes), LightmapKey.Compute(g, r, u, f, t));
        }
    }
}
