// VENDORED from BfiiMapTools tests\Bfii.Navmesh.Tests\BitfieldTests.cs (sha256:95AFBC56F9E626AE).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Primitives;
using Xunit;

namespace Bfii.Navmesh.Tests
{
    /// <summary>
    /// The packed fields. These masks decide whether a graph is read correctly at all, and the
    /// island field in particular is the one this whole session exists to write properly.
    /// </summary>
    public sealed class BitfieldTests
    {
        [Fact]
        public void IslandFieldIsEighteenBitsWideAtBitSeven()
        {
            Assert.Equal(18, NavArea.IslandNumBits);
            Assert.Equal(7, NavArea.IslandNumShift);
            Assert.Equal(262143, NavArea.MaxIslandNum);
            Assert.Equal(0x3FFFF, NavArea.MaxIslandNum);
        }

        [Fact]
        public void TheLegacySentinelIsExactlyTheSaturatedIslandValue()
        {
            // Worker report 08 section 3.3: the community importer writes 262143 for every area.
            Assert.Equal(262143, NavArea.SentinelIslandNum);
            Assert.Equal(NavArea.MaxIslandNum, NavArea.SentinelIslandNum);
        }

        [Fact]
        public void IslandNumberSurvivesEveryValueInRange()
        {
            NavArea area = new NavArea();

            for (int value = 0; value <= NavArea.MaxIslandNum; value += 997)
            {
                area.IslandNum = value;
                Assert.Equal(value, area.IslandNum);
            }

            area.IslandNum = NavArea.MaxIslandNum;
            Assert.Equal(NavArea.MaxIslandNum, area.IslandNum);
        }

        [Fact]
        public void IslandNumberAndEdgeCountDoNotOverlap()
        {
            NavArea area = new NavArea();
            area.EdgeCount = 127;
            area.IslandNum = NavArea.MaxIslandNum;

            Assert.Equal(127, area.EdgeCount);
            Assert.Equal(NavArea.MaxIslandNum, area.IslandNum);

            area.IslandNum = 0;
            Assert.Equal(127, area.EdgeCount);

            area.EdgeCount = 3;
            area.IslandNum = 12345;
            Assert.Equal(3, area.EdgeCount);
            Assert.Equal(12345, area.IslandNum);
        }

        [Fact]
        public void IslandNumberDoesNotDisturbTheHighBitsOfFlags1()
        {
            NavArea area = new NavArea { Flags1 = 0xFFFFFFFF };
            uint before = area.Flags1;

            area.IslandNum = 0;

            // Only bits 7..24 may have changed.
            uint changed = before ^ area.Flags1;
            Assert.Equal(0x01FFFF80u, changed);
        }

        [Fact]
        public void OutOfRangeIslandNumberIsRejectedRatherThanTruncated()
        {
            NavArea area = new NavArea();
            Assert.Throws<System.ArgumentOutOfRangeException>(() => area.IslandNum = NavArea.MaxIslandNum + 1);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => area.IslandNum = -1);
        }

        [Fact]
        public void EdgeCountIsSevenBitsAndRejectsOverflow()
        {
            NavArea area = new NavArea();
            area.EdgeCount = NavArea.MaxEdgeCount;
            Assert.Equal(127, area.EdgeCount);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => area.EdgeCount = 128);
        }

        [Fact]
        public void CostMultipliersClampIntoTheirNibbles()
        {
            NavArea area = new NavArea();

            area.ObstacleCostMultiplier = 0;
            Assert.Equal(1, area.ObstacleCostMultiplier);

            area.ObstacleCostMultiplier = 99;
            Assert.Equal(15, area.ObstacleCostMultiplier);

            area.StaticCostMultiplier = 7;
            Assert.Equal(7, area.StaticCostMultiplier);
            Assert.Equal(15, area.ObstacleCostMultiplier);
        }

        [Fact]
        public void AreaUsageCountAndBasisVertexShareFlags2Cleanly()
        {
            NavArea area = new NavArea();
            area.AreaUsageCount = 1023;
            area.BasisVertex = 100;
            area.ObstacleCostMultiplier = 3;
            area.StaticCostMultiplier = 9;

            Assert.Equal(1023, area.AreaUsageCount);
            Assert.Equal(100, area.BasisVertex);
            Assert.Equal(3, area.ObstacleCostMultiplier);
            Assert.Equal(9, area.StaticCostMultiplier);
        }

        [Fact]
        public void EdgeObstacleIdAndTypeSharTheFirstFlagWord()
        {
            NavEdge edge = new NavEdge(Vec3.Zero);
            Assert.Equal(NavEdge.DefaultFlags1, edge.Flags1);

            edge.ObstacleId = 0x1234;
            edge.EdgeType = NavEdgeType.Portal;

            Assert.Equal(0x1234, edge.ObstacleId);
            Assert.Equal(NavEdgeType.Portal, edge.EdgeType);

            edge.EdgeType = NavEdgeType.Normal;
            Assert.Equal(0x1234, edge.ObstacleId);
            Assert.Equal(NavEdgeType.Normal, edge.EdgeType);
        }

        [Fact]
        public void AreaAndEdgeRecordSizesMatchThePointerWidth()
        {
            Assert.Equal(72, NavArea.HeaderSizeInBytes(1));
            Assert.Equal(56, NavArea.HeaderSizeInBytes(0));
            Assert.Equal(32, NavEdge.SizeInBytes(1));
            Assert.Equal(24, NavEdge.SizeInBytes(0));
        }
    }
}
