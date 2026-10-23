using System.Collections.Generic;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class HavokTransformReaderTests
    {
        private static List<HavokTransformReader.Transform> Array(int count)
        {
            var list = new List<HavokTransformReader.Transform>();
            for (int i = 0; i < count; i++)
                list.Add(new HavokTransformReader.Transform
                {
                    Right = new float[] { 1, 0, 0 },
                    Up = new float[] { 0, 1, 0 },
                    Forward = new float[] { 0, 0, 1 },
                    Translation = new float[] { i, 0, 0 }
                });
            return list;
        }

        // A BFII physics resource carries the group's placement array alongside per-prop nested
        // compounds and a duplicate second collision slot.  Length is what tells them apart.
        [Test]
        public void SelectPlacements_PicksArrayMatchingExpectedCount()
        {
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(6), Array(67), Array(4) };
            var picked = HavokTransformReader.SelectPlacements(arrays, 67);
            Assert.IsNotNull(picked);
            Assert.AreEqual(67, picked.Count);
        }

        [Test]
        public void SelectPlacements_NotTheConcatenationOfEveryArray()
        {
            // Regression: summing all compounds gave 77 here, so a group needing 6 read garbage
            // once its running index passed the first array.
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(6), Array(67), Array(4) };
            Assert.IsNull(HavokTransformReader.SelectPlacements(arrays, 77));
            Assert.AreEqual(6, HavokTransformReader.SelectPlacements(arrays, 6).Count);
        }

        [Test]
        public void SelectPlacements_DuplicateCollisionSlot_TakesFirstMatch()
        {
            // Both slots hold the same instances; either answers the query identically.
            var first = Array(9);
            var arrays = new List<List<HavokTransformReader.Transform>> { first, Array(9) };
            Assert.IsTrue(ReferenceEquals(first, HavokTransformReader.SelectPlacements(arrays, 9)));
        }

        [Test]
        public void SelectPlacements_NoMatch_ReturnsNull()
        {
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(6), Array(4) };
            Assert.IsNull(HavokTransformReader.SelectPlacements(arrays, 5));
        }

        [Test]
        public void SelectPlacements_NullArrays_ReturnsNull()
        {
            Assert.IsNull(HavokTransformReader.SelectPlacements(null, 3));
        }

        [Test]
        public void DescribeSizes_ListsEveryCandidateLength()
        {
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(6), Array(67), Array(4) };
            Assert.AreEqual("6, 67, 4", HavokTransformReader.DescribeSizes(arrays));
        }

        [Test]
        public void DescribeSizes_Empty_ReadsAsNone()
        {
            Assert.AreEqual("none", HavokTransformReader.DescribeSizes(new List<List<HavokTransformReader.Transform>>()));
            Assert.AreEqual("none", HavokTransformReader.DescribeSizes(null));
        }

        private static List<HavokTransformReader.CompoundTree> Trees(params int[] nodeCounts)
        {
            var list = new List<HavokTransformReader.CompoundTree>();
            foreach (int n in nodeCounts)
                list.Add(new HavokTransformReader.CompoundTree
                {
                    NodeCount = n,
                    LeafCount = (n + 1) / 2,
                    StructureValid = true
                });
            return list;
        }

        // A compound of N instances owns a full binary AABB tree of exactly 2N-1 nodes. Nothing in
        // the file links a shape to its tree, so they are matched by that size law.
        [Test]
        public void TreesMatchInstanceArrays_EveryCompoundPaired_Succeeds()
        {
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(6), Array(67), Array(4) };
            string detail;
            // deliberately not in shape order -- shipped resources are not
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(arrays, Trees(133, 11, 7), out detail), detail);
            Assert.IsNull(detail);
        }

        [Test]
        public void TreesMatchInstanceArrays_DuplicateSlots_ConsumeDistinctTrees()
        {
            // Two collision slots hold the same instances, so two equal-sized trees are required --
            // one tree must not satisfy both compounds.
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(9), Array(9) };
            string detail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(arrays, Trees(17, 17), out detail), detail);
            Assert.IsFalse(HavokTransformReader.TreesMatchInstanceArrays(arrays, Trees(17), out detail));
            Assert.IsNotNull(detail);
        }

        [Test]
        public void TreesMatchInstanceArrays_WrongSizedTree_Fails()
        {
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(6) };
            string detail;
            Assert.IsFalse(HavokTransformReader.TreesMatchInstanceArrays(arrays, Trees(12), out detail));
            Assert.IsNotNull(detail);
        }

        // A tree of the right size is still useless if the traversal cannot reach its leaves.
        [Test]
        public void TreesMatchInstanceArrays_MalformedTree_FailsEvenWhenSizedRight()
        {
            var arrays = new List<List<HavokTransformReader.Transform>> { Array(6) };
            var trees = Trees(11);
            trees[0].StructureValid = false;
            trees[0].StructureError = "node 4 is reachable more than once";
            string detail;
            Assert.IsFalse(HavokTransformReader.TreesMatchInstanceArrays(arrays, trees, out detail));
            Assert.IsTrue(detail.IndexOf("reachable more than once", System.StringComparison.Ordinal) >= 0, detail);
        }

        [Test]
        public void TreesMatchInstanceArrays_NullTrees_IsNotAFailure()
        {
            // An unreadable tree list is reported by the reader, not re-reported here.
            string detail;
            Assert.IsTrue(HavokTransformReader.TreesMatchInstanceArrays(
                new List<List<HavokTransformReader.Transform>> { Array(3) }, null, out detail));
        }

        [Test]
        public void DescribeTrees_ListsNodeCounts()
        {
            Assert.AreEqual("133, 11, 7", HavokTransformReader.DescribeTrees(Trees(133, 11, 7)));
            Assert.AreEqual("none", HavokTransformReader.DescribeTrees(null));
        }

        [Test]
        public void TryReadCompounds_ShortBuffer_FailsWithoutThrowing()
        {
            List<List<HavokTransformReader.Transform>> arrays;
            List<HavokTransformReader.CompoundTree> trees;
            string error;
            Assert.IsFalse(HavokTransformReader.TryReadCompounds(new byte[16], out arrays, out trees, out error));
            Assert.IsNotNull(error);
            Assert.AreEqual(0, arrays.Count);
            Assert.AreEqual(0, trees.Count);
        }

        [Test]
        public void TryReadInstanceArrays_ShortBuffer_FailsWithoutThrowing()
        {
            List<List<HavokTransformReader.Transform>> arrays;
            string error;
            Assert.IsFalse(HavokTransformReader.TryReadInstanceArrays(new byte[16], out arrays, out error));
            Assert.IsNotNull(error);
            Assert.AreEqual(0, arrays.Count);
        }

        [Test]
        public void TryReadInstanceArrays_NullBuffer_FailsWithoutThrowing()
        {
            List<List<HavokTransformReader.Transform>> arrays;
            string error;
            Assert.IsFalse(HavokTransformReader.TryReadInstanceArrays(null, out arrays, out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryReadInstanceArrays_NoHkxMagic_FailsWithoutThrowing()
        {
            List<List<HavokTransformReader.Transform>> arrays;
            string error;
            var junk = new byte[1024];
            for (int i = 0; i < junk.Length; i++) junk[i] = (byte)(i & 0xff);
            Assert.IsFalse(HavokTransformReader.TryReadInstanceArrays(junk, out arrays, out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryReadPlacements_UnreadableBuffer_ReportsTheReadError()
        {
            List<HavokTransformReader.Transform> transforms;
            string error;
            Assert.IsFalse(HavokTransformReader.TryReadPlacements(new byte[16], 4, out transforms, out error));
            Assert.IsNull(transforms);
            Assert.IsNotNull(error);
        }
    }
}
