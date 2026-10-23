// VENDORED from BfiiMapTools tests\Bfii.Navmesh.Tests\IslandTests.cs (sha256:517305716358E698).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System.Collections.Generic;
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Islands;
using Bfii.Navmesh.Primitives;
using Xunit;

namespace Bfii.Navmesh.Tests
{
    /// <summary>
    /// Island labelling over synthetic graphs. This is the fix for master plan risk R-06.
    /// </summary>
    public sealed class IslandTests
    {
        /// <summary>
        /// Builds a graph of n isolated single-area components, with no adjacency at all.
        /// </summary>
        private static NavGraph DisconnectedAreas(int count)
        {
            NavGraph graph = new NavGraph();
            for (int i = 0; i < count; i++)
            {
                NavArea area = new NavArea();
                float x = i * 10f;
                area.Edges.Add(new NavEdge(new Vec3(x, 0f, 0f)));
                area.Edges.Add(new NavEdge(new Vec3(x, 0f, 1f)));
                area.Edges.Add(new NavEdge(new Vec3(x + 1f, 0f, 1f)));
                area.EdgeCount = 3;
                area.RecalculateDerivedGeometry();
                graph.Areas.Add(area);
            }

            return graph;
        }

        /// <summary>Links areas into one chain: 0-1-2-...-n.</summary>
        private static NavGraph ChainedAreas(int count)
        {
            NavGraph graph = DisconnectedAreas(count);
            for (int i = 0; i + 1 < count; i++)
            {
                graph.Areas[i].Edges[0].AdjacentArea = graph.Areas[i + 1];
                graph.Areas[i + 1].Edges[1].AdjacentArea = graph.Areas[i];
            }

            return graph;
        }

        [Fact]
        public void ConnectedGridIsOneIsland()
        {
            NavMeshImage image = SyntheticImage.Grid(4, 4);
            NavGraph graph = image.NavSet.Graphs[0];

            IslandLabelResult result = IslandLabeler.Label(graph);

            Assert.Equal(1, result.IslandCount);
            Assert.All(graph.Areas, a => Assert.Equal(1, a.IslandNum));
        }

        [Fact]
        public void TwoDisjointSlabsAreTwoIslands()
        {
            NavMeshImage image = SyntheticImage.TwoIslands(3, 3);
            NavGraph graph = image.NavSet.Graphs[0];

            IslandLabelResult result = IslandLabeler.Label(graph);

            Assert.Equal(2, result.IslandCount);

            HashSet<int> ids = new HashSet<int>();
            foreach (NavArea area in graph.Areas)
            {
                ids.Add(area.IslandNum);
            }

            Assert.Equal(new HashSet<int> { 1, 2 }, ids);
            Assert.Equal(new List<int> { 9, 9 }, new List<int>(result.AreaCountsByIsland));
        }

        [Fact]
        public void NumberingStartsAtOneByDefault()
        {
            NavGraph graph = DisconnectedAreas(3);

            IslandLabelResult result = IslandLabeler.Label(graph);

            Assert.Equal(3, result.IslandCount);
            Assert.Equal(1, result.FirstIslandId);
            Assert.Equal(1, graph.Areas[0].IslandNum);
            Assert.Equal(2, graph.Areas[1].IslandNum);
            Assert.Equal(3, graph.Areas[2].IslandNum);
        }

        [Fact]
        public void NumberingCanStartAtZeroWhenTheCallerHasEvidenceThatZeroIsValid()
        {
            NavGraph graph = DisconnectedAreas(2);

            IslandLabeler.Label(graph, IslandLabelOptions.ZeroBased);

            Assert.Equal(0, graph.Areas[0].IslandNum);
            Assert.Equal(1, graph.Areas[1].IslandNum);
        }

        [Fact]
        public void AChainOfAreasIsASingleIsland()
        {
            NavGraph graph = ChainedAreas(8);

            IslandLabelResult result = IslandLabeler.Label(graph);

            Assert.Equal(1, result.IslandCount);
            Assert.Equal(8, result.LargestIslandAreaCount);
        }

        [Fact]
        public void AdjacencyIsTreatedAsUndirectedEvenWhenOnlyOneSideNamesTheOther()
        {
            // A donor whose adjacency is recorded one way only must still be labelled as connected;
            // treating it as directed would split one island into two and change bot behaviour.
            NavGraph graph = DisconnectedAreas(2);
            graph.Areas[0].Edges[0].AdjacentArea = graph.Areas[1];

            IslandLabelResult result = IslandLabeler.Label(graph);

            Assert.Equal(1, result.IslandCount);
        }

        [Fact]
        public void SelfReferenceDoesNotCreateAnExtraIsland()
        {
            NavGraph graph = DisconnectedAreas(1);
            graph.Areas[0].Edges[0].AdjacentArea = graph.Areas[0];

            IslandLabelResult result = IslandLabeler.Label(graph);

            Assert.Equal(1, result.IslandCount);
        }

        [Fact]
        public void LabellingIsIdempotent()
        {
            NavMeshImage image = SyntheticImage.TwoIslands(2, 2);
            NavGraph graph = image.NavSet.Graphs[0];

            IslandLabeler.Label(graph);
            List<int> first = graph.Areas.ConvertAll(a => a.IslandNum);
            IslandLabeler.Label(graph);
            List<int> second = graph.Areas.ConvertAll(a => a.IslandNum);

            Assert.Equal(first, second);
        }

        [Fact]
        public void MoreComponentsThanTheFieldCanHoldIsAnErrorNotAWrappedId()
        {
            NavGraph graph = DisconnectedAreas(2);
            IslandLabelOptions options = new IslandLabelOptions { FirstIslandId = NavArea.MaxIslandNum };

            IslandOverflowException error = Assert.Throws<IslandOverflowException>(
                () => IslandLabeler.Label(graph, options));

            Assert.Contains("18-bit island field", error.Message);
        }

        [Fact]
        public void VerifyAcceptsCorrectlyLabelledGraphs()
        {
            NavMeshImage image = SyntheticImage.TwoIslands(3, 3);
            NavGraph graph = image.NavSet.Graphs[0];
            IslandLabeler.Label(graph);

            IslandVerification verification = IslandLabeler.Verify(graph);

            Assert.True(verification.IsConsistent, verification.ToString());
            Assert.Equal(2, verification.ActualComponents);
            Assert.Equal(0, verification.SentinelAreas);
        }

        [Fact]
        public void VerifyRejectsTheLegacySentinelOnADisconnectedMesh()
        {
            // This is the exact defect the session exists to fix, expressed as a test: the sentinel
            // merges two genuinely separate components into one claimed island.
            NavMeshImage image = SyntheticImage.TwoIslands(3, 3);
            NavGraph graph = image.NavSet.Graphs[0];
            foreach (NavArea area in graph.Areas)
            {
                area.IslandNum = NavArea.SentinelIslandNum;
            }

            IslandVerification verification = IslandLabeler.Verify(graph);

            Assert.False(verification.IsConsistent, verification.ToString());
            Assert.Equal(2, verification.ActualComponents);
            Assert.True(verification.MergedIds > 0);
            Assert.Equal(graph.Areas.Count, verification.SentinelAreas);
        }

        [Fact]
        public void VerifyRejectsAStaleLabellingThatSplitsOneComponent()
        {
            NavMeshImage image = SyntheticImage.Grid(3, 3);
            NavGraph graph = image.NavSet.Graphs[0];
            IslandLabeler.Label(graph);
            graph.Areas[0].IslandNum = 99;

            IslandVerification verification = IslandLabeler.Verify(graph);

            Assert.False(verification.IsConsistent, verification.ToString());
            Assert.True(verification.SplitComponents > 0);
        }

        [Fact]
        public void DescribeReportsTheSentinelAndWhetherZeroIsUsed()
        {
            NavMeshImage image = SyntheticImage.Grid(2, 2);
            NavGraph graph = image.NavSet.Graphs[0];
            foreach (NavArea area in graph.Areas)
            {
                area.IslandNum = NavArea.SentinelIslandNum;
            }

            IslandHistogram histogram = IslandLabeler.Describe(graph);

            Assert.Equal(4, histogram.AreaCount);
            Assert.Equal(1, histogram.DistinctIds);
            Assert.Equal(4, histogram.SentinelAreas);
            Assert.False(histogram.UsesIdZero);
        }

        [Fact]
        public void IslandNumbersSurviveSerialization()
        {
            NavMeshImage image = SyntheticImage.TwoIslands(2, 2);
            IslandLabeler.LabelAll(image.NavSet);

            NavMeshImage parsed = NavMeshImage.Read(image.Write());
            IslandVerification verification = IslandLabeler.Verify(parsed.NavSet.Graphs[0]);

            Assert.True(verification.IsConsistent, verification.ToString());
            Assert.Equal(2, verification.ActualComponents);
            Assert.Equal(0, verification.SentinelAreas);
        }
    }
}
