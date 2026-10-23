using System;
using System.Collections.Generic;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class HavokPhysicsResourceTests
    {
        private static List<HavokPhysicsResource.LeafBox> RandomBoxes(int count, int seed)
        {
            var rng = new Random(seed);
            var boxes = new List<HavokPhysicsResource.LeafBox>();
            for (int i = 0; i < count; i++)
            {
                float[] mn = { (float)(rng.NextDouble() * 900 - 450), (float)(rng.NextDouble() * 60 + 190), (float)(rng.NextDouble() * 900 - 450) };
                float[] mx = { mn[0] + (float)(rng.NextDouble() * 12 + 0.1), mn[1] + (float)(rng.NextDouble() * 6 + 0.1), mn[2] + (float)(rng.NextDouble() * 12 + 0.1) };
                boxes.Add(new HavokPhysicsResource.LeafBox { Min = mn, Max = mx });
            }
            return boxes;
        }

        private static void AssertRoundTrip(List<HavokPhysicsResource.LeafBox> boxes)
        {
            float[][] domain;
            Dictionary<int, int> leafNode;
            byte[] nodes = HavokPhysicsResource.BuildTree(boxes, out domain, out leafNode);
            int nodeCount = nodes.Length / 6;
            Assert.AreEqual(2 * boxes.Count - 1, nodeCount);
            Assert.AreEqual(boxes.Count, leafNode.Count);

            Dictionary<int, HavokPhysicsResource.LeafBox> decoded = HavokPhysicsResource.DecodeTree(nodes, 0, nodeCount, domain);
            Assert.AreEqual(boxes.Count, decoded.Count);
            for (int i = 0; i < boxes.Count; i++)
            {
                HavokPhysicsResource.LeafBox got = decoded[i];
                Assert.AreEqual(leafNode[i], got.Node);
                for (int a = 0; a < 3; a++)
                {
                    Assert.IsTrue(got.Min[a] <= boxes[i].Min[a] + 1e-4f, "leaf " + i + " axis " + a + " min not conservative");
                    Assert.IsTrue(got.Max[a] >= boxes[i].Max[a] - 1e-4f, "leaf " + i + " axis " + a + " max not conservative");
                    // Codec6 loses at most one quantisation step per side of the parent extent.
                    float extent = domain[1][a] - domain[0][a];
                    Assert.IsTrue(boxes[i].Min[a] - got.Min[a] <= extent, "leaf " + i + " far too loose");
                }
            }
        }

        [Test]
        public void Swbf1MaterialWord_IsRecognisedByMarkerNibble()
        {
            // SWBF 2015 words from Dune Sea packages; BFII words from Tatooine_01 Content.
            Assert.IsTrue(HavokPhysicsResource.IsSwbf1MaterialWord(0x28118000));
            Assert.IsTrue(HavokPhysicsResource.IsSwbf1MaterialWord(unchecked((int)0x8d108000)));
            Assert.IsTrue(HavokPhysicsResource.IsSwbf1MaterialWord(0x2ab58009));
            Assert.IsFalse(HavokPhysicsResource.IsSwbf1MaterialWord(0x2100e800));
            Assert.IsFalse(HavokPhysicsResource.IsSwbf1MaterialWord(0x0880f800));
            Assert.IsFalse(HavokPhysicsResource.IsSwbf1MaterialWord(0x09014809));
        }

        [Test]
        public void Swbf1MaterialWord_PicksSameIndexAndFlag_ThenFlag_ThenMostUsed_NeverTrailer()
        {
            // rows: 0 idx 0x10 flag 0 | 1 idx 0x11 flag 0 | 2 idx 0x01 flag 9 | 3 idx 0x0e flag 0 | 4 trailer
            int[] table = { 0x21010800, 0x21011800, 0x09001809, 0x2100e800, 0x00000800 };
            byte[] parts = { 3, 3, 3, 0, 1, 1, 4, 4, 4, 4 };
            // 2015 idx 0x11 flag 0 -> row 1 (same index and flag) even though row 3 is used more
            Assert.AreEqual(1, HavokPhysicsResource.PickHostRowForSwbf1Word(0x28118000, table, parts));
            // 2015 idx 0x55 flag 9 -> row 2 (only flag-9 row)
            Assert.AreEqual(2, HavokPhysicsResource.PickHostRowForSwbf1Word(0x2a558009, table, parts));
            // 2015 idx 0x55 flag 3 -> most-used material row (3), not the trailer (4) that parts use more
            Assert.AreEqual(3, HavokPhysicsResource.PickHostRowForSwbf1Word(0x2a558003, table, parts));
            // no BFII rows at all -> -1
            Assert.AreEqual(-1, HavokPhysicsResource.PickHostRowForSwbf1Word(0x28118000, new[] { 0x28118000, 0x8000 }, parts));
        }

        [Test]
        public void BuildTree_SingleLeaf_IsOneNodeMatchingDomain()
        {
            var boxes = new List<HavokPhysicsResource.LeafBox>
            {
                new HavokPhysicsResource.LeafBox { Min = new[] { 1f, 2f, 3f }, Max = new[] { 4f, 5f, 6f } }
            };
            float[][] domain;
            Dictionary<int, int> leafNode;
            byte[] nodes = HavokPhysicsResource.BuildTree(boxes, out domain, out leafNode);
            Assert.AreEqual(6, nodes.Length);
            Assert.AreEqual(0, leafNode[0]);
            Assert.AreEqual(0, (int)nodes[3] & 0x80);
            Assert.AreEqual(1f, domain[0][0]);
            Assert.AreEqual(6f, domain[1][2]);
        }

        [Test]
        public void BuildTree_RoundTrips_SmallSet()
        {
            AssertRoundTrip(RandomBoxes(7, 1));
        }

        [Test]
        public void BuildTree_RoundTrips_LargeSet()
        {
            AssertRoundTrip(RandomBoxes(1500, 2));
        }

        [Test]
        public void BuildTree_RoundTrips_DegenerateAxis()
        {
            var boxes = RandomBoxes(20, 3);
            foreach (HavokPhysicsResource.LeafBox b in boxes) { b.Min[1] = 200f; b.Max[1] = 200f; }
            AssertRoundTrip(boxes);
        }

        [Test]
        public void DecodeTree_RejectsOutOfRangeNode()
        {
            // Internal root whose right child points past the end.
            byte[] nodes = { 0, 0, 0, 0x80, 0x40, 0x00, 0, 0, 0, 0, 0, 0 };
            Assert.Throws<System.IO.InvalidDataException>(delegate
            {
                HavokPhysicsResource.DecodeTree(nodes, 0, 2, new[] { new[] { 0f, 0f, 0f, 0f }, new[] { 1f, 1f, 1f, 0f } });
            });
        }

        [Test]
        public void FlagsFor_IdentityUnitScale_IsBaseWord()
        {
            float[][] cols = { new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f } };
            Assert.AreEqual(0x62, HavokPhysicsResource.FlagsFor(cols, new[] { 1f, 1f, 1f }));
        }

        [Test]
        public void FlagsFor_RotationAndScale_SetTheirBits()
        {
            float[][] rot = { new[] { 0f, 0f, -1f }, new[] { 0f, 1f, 0f }, new[] { 1f, 0f, 0f } };
            Assert.AreEqual(0x66, HavokPhysicsResource.FlagsFor(rot, new[] { 1f, 1f, 1f }));
            float[][] ident = { new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f } };
            Assert.AreEqual(0x6a, HavokPhysicsResource.FlagsFor(ident, new[] { 0.5f, 0.5f, 0.5f }));
            Assert.AreEqual(0x6a, HavokPhysicsResource.FlagsFor(ident, new[] { 1.000001f, 1f, 1f }));
            Assert.AreEqual(0x6e, HavokPhysicsResource.FlagsFor(rot, new[] { 2f, 2f, 2f }));
        }

        [Test]
        public void FlagsFor_TinyRotationNoise_IsStillIdentity()
        {
            float[][] near = { new[] { 1f, 0f, 1e-5f }, new[] { 0f, 1f, 0f }, new[] { -1e-5f, 0f, 1f } };
            Assert.AreEqual(0x62, HavokPhysicsResource.FlagsFor(near, new[] { 1f, 1f, 1f }));
        }

        [Test]
        public void Parse_RejectsGarbage()
        {
            Assert.Throws<System.IO.InvalidDataException>(delegate { HavokPhysicsResource.Parse(new byte[512]); });
        }

        // The 2015 Arctic_01 base prefab's world transform (a yaw of ~217.6 degrees) applied to a record:
        // the result must equal LightPort.Compose, which export-layer --flatten-prefabs uses for world rows.
        [Test]
        public void ComposeRigid_MatchesLightPortCompose()
        {
            float[][] pCols = { new[] { -0.792f, 0f, -0.6105f }, new[] { 0f, 1f, 0f }, new[] { 0.6105f, 0f, -0.792f } };
            float[] pTrans = { 270.8798f, 359.68f, -1635.7933f };
            float[][] cols = { new[] { 0f, 0f, -1f }, new[] { 0f, 1f, 0f }, new[] { 1f, 0f, 0f } };
            float[] trans = { 72.96f, -0.64f, -85.76f };
            float[][] outCols; float[] outTrans;
            HavokPhysicsResource.ComposeRigid(cols, trans, pCols, pTrans, out outCols, out outTrans);
            var parent = new LinearTransformData { Right = pCols[0], Up = pCols[1], Forward = pCols[2], Trans = pTrans };
            var local = new LinearTransformData { Right = cols[0], Up = cols[1], Forward = cols[2], Trans = trans };
            LinearTransformData world = LightPort.Compose(local, parent);
            float[][] expCols = { world.Right, world.Up, world.Forward };
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 3; k++) Assert.IsTrue(Math.Abs(outCols[c][k] - expCols[c][k]) < 1e-5f, "col " + c + " axis " + k);
            for (int k = 0; k < 3; k++) Assert.IsTrue(Math.Abs(outTrans[k] - world.Trans[k]) < 1e-3f, "trans axis " + k);
            // The census's world position of this placement (Corridor_01_Damaged_01_Ramp_1024x768x512, 2015 id 9433).
            Assert.IsTrue(Math.Abs(outTrans[0] - 160.7375f) < 0.01f && Math.Abs(outTrans[1] - 359.04f) < 0.01f && Math.Abs(outTrans[2] + 1612.4135f) < 0.01f,
                "world position " + outTrans[0] + "," + outTrans[1] + "," + outTrans[2]);
        }

        [Test]
        public void ComposeRigid_IdentityParentKeepsRecord()
        {
            float[][] id = { new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }, new[] { 0f, 0f, 1f } };
            float[][] cols = { new[] { 0.6f, 0f, -0.8f }, new[] { 0f, 1f, 0f }, new[] { 0.8f, 0f, 0.6f } };
            float[] trans = { 1f, 2f, 3f };
            float[][] outCols; float[] outTrans;
            HavokPhysicsResource.ComposeRigid(cols, trans, id, new float[3], out outCols, out outTrans);
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 3; k++) Assert.AreEqual(cols[c][k], outCols[c][k]);
            for (int k = 0; k < 3; k++) Assert.AreEqual(trans[k], outTrans[k]);
        }
    }
}
