// VENDORED from BfiiMapTools tests\Bfii.Navmesh.Tests\FixtureTests.cs (sha256:2B62EDE922CFC7D4).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using Bfii.Navmesh.Compile;
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Islands;
using Bfii.Navmesh.Obj;
using Xunit;

namespace Bfii.Navmesh.Tests
{
    /// <summary>
    /// The two fixtures for the in-game island experiment. These assertions are what make the
    /// experiment interpretable: if the fixtures ever stop having the properties claimed in
    /// evidence\navmesh\TWO-ISLAND-TEST.md, the in-game result would mean nothing.
    /// </summary>
    public sealed class FixtureTests
    {
        private static NavCompileResult CompileFixture(string objText, out NavGraph graph)
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavCompileResult result = NavGraphCompiler.Compile(donor, ObjParser.ParseText(objText));
            graph = donor.NavSet.Graphs[0];
            return result;
        }

        [Fact]
        public void TwoIslandFixtureHasExactlyTwoComponents()
        {
            NavCompileResult result = CompileFixture(FixtureMeshes.TwoIsland(), out NavGraph graph);

            Assert.Equal(32, result.AreaCount);
            Assert.Equal(2, result.Islands.IslandCount);
            Assert.Equal(new[] { 16, 16 }, result.Islands.AreaCountsByIsland);

            IslandVerification verification = IslandLabeler.Verify(graph);
            Assert.True(verification.IsConsistent, verification.ToString());
            Assert.Equal(2, verification.ActualComponents);
        }

        [Fact]
        public void OneIslandControlHasExactlyOneComponent()
        {
            NavCompileResult result = CompileFixture(FixtureMeshes.OneIsland(), out NavGraph graph);

            Assert.Equal(32, result.AreaCount);
            Assert.Equal(1, result.Islands.IslandCount);

            IslandVerification verification = IslandLabeler.Verify(graph);
            Assert.True(verification.IsConsistent, verification.ToString());
            Assert.Equal(1, verification.ActualComponents);
        }

        [Fact]
        public void BothFixturesHaveTheSameFaceCountSoOnlyConnectivityDiffers()
        {
            ObjMesh two = ObjParser.ParseText(FixtureMeshes.TwoIsland());
            ObjMesh one = ObjParser.ParseText(FixtureMeshes.OneIsland());

            Assert.Equal(one.Faces.Count, two.Faces.Count);
            Assert.Equal(32, two.Faces.Count);
        }

        [Fact]
        public void TheTwoSlabsShareNoVertexAtAll()
        {
            ObjMesh mesh = ObjParser.ParseText(FixtureMeshes.TwoIsland());

            // 4x4 cells is 25 vertices per slab. If any vertex were shared the welded count would
            // drop below 50 and the fixture would silently stop being two islands.
            Assert.Equal(50, mesh.Vertices.Count);
            Assert.Equal(0, mesh.WeldedVertexCount);
        }

        [Fact]
        public void TheGapIsWiderThanAnyPlausibleStepOrAgentRadius()
        {
            ObjMesh mesh = ObjParser.ParseText(FixtureMeshes.TwoIsland());

            float maxSlabA = float.MinValue;
            float minSlabB = float.MaxValue;

            foreach (Bfii.Navmesh.Primitives.Vec3 v in mesh.Vertices)
            {
                if (v.X <= FixtureMeshes.SlabColumns * FixtureMeshes.CellSize)
                {
                    if (v.X > maxSlabA)
                    {
                        maxSlabA = v.X;
                    }
                }
                else if (v.X < minSlabB)
                {
                    minSlabB = v.X;
                }
            }

            Assert.Equal(FixtureMeshes.Gap, minSlabB - maxSlabA, 3);
            Assert.True(minSlabB - maxSlabA >= 6f);
        }

        [Fact]
        public void FixturesAreManifoldAndCompileWithoutWarningsOtherThanConnectivity()
        {
            NavCompileResult two = CompileFixture(FixtureMeshes.TwoIsland(), out _);
            NavCompileResult one = CompileFixture(FixtureMeshes.OneIsland(), out _);

            // The only warning the two-island fixture may raise is the expected one.
            Assert.All(two.Warnings, w => Assert.Contains("disconnected", w));
            Assert.Empty(one.Warnings);
        }

        [Fact]
        public void FixtureGraphsSerializeAndRoundTrip()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavGraphCompiler.Compile(donor, ObjParser.ParseText(FixtureMeshes.TwoIsland()));

            NavRoundTripResult result = NavRoundTrip.Run(donor.Write(), forceReserialize: true);

            Assert.True(result.IsByteExact, result.ToString());
            Assert.Equal(32, result.Image.NavSet.Graphs[0].Areas.Count);
        }

        [Fact]
        public void LegacySentinelOnTheFixtureIsDetectedAsOverMerged()
        {
            // The whole point of the in-game experiment, expressed offline: with the sentinel, a
            // two-component mesh claims to be one island, and Verify says so.
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavGraphCompiler.Compile(
                donor,
                ObjParser.ParseText(FixtureMeshes.TwoIsland()),
                new NavCompileOptions { UseLegacyIslandSentinel = true });

            IslandVerification verification = IslandLabeler.Verify(donor.NavSet.Graphs[0]);

            Assert.Equal(2, verification.ActualComponents);
            Assert.True(verification.MergedIds > 0);
            Assert.Equal(32, verification.SentinelAreas);
            Assert.False(verification.IsConsistent);
        }
    }
}
