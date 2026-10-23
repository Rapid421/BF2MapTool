// VENDORED from BfiiMapTools tests\Bfii.Navmesh.Tests\ObjAndCompilerTests.cs (sha256:3DDBC22B795E0719).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Compile;
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Islands;
using Bfii.Navmesh.Obj;
using Bfii.Navmesh.Primitives;
using Xunit;

namespace Bfii.Navmesh.Tests
{
    /// <summary>OBJ parsing and the surface-to-graph compiler.</summary>
    public sealed class ObjAndCompilerTests
    {
        private const string TwoTrianglesSharingAnEdge =
            "v 0 0 0\n" +
            "v 0 0 1\n" +
            "v 1 0 1\n" +
            "v 1 0 0\n" +
            "f 1 2 3\n" +
            "f 1 3 4\n";

        private const string TwoDisjointTriangles =
            "v 0 0 0\n" +
            "v 0 0 1\n" +
            "v 1 0 1\n" +
            "v 10 0 0\n" +
            "v 10 0 1\n" +
            "v 11 0 1\n" +
            "f 1 2 3\n" +
            "f 4 5 6\n";

        private const string ThreeFacesOnOneEdge =
            "v 0 0 0\n" +
            "v 0 0 1\n" +
            "v 1 0 1\n" +
            "v -1 0 1\n" +
            "v 0 1 1\n" +
            "f 1 2 3\n" +
            "f 1 2 4\n" +
            "f 1 2 5\n";

        [Fact]
        public void ParsesVerticesAndFaces()
        {
            ObjMesh mesh = ObjParser.ParseText(TwoTrianglesSharingAnEdge);

            Assert.Equal(4, mesh.Vertices.Count);
            Assert.Equal(2, mesh.Faces.Count);
            Assert.Equal(5, mesh.Faces[0].SourceLine);
            Assert.Equal(6, mesh.Faces[1].SourceLine);
        }

        [Fact]
        public void ParsesFaceTokensWithTextureAndNormalIndices()
        {
            ObjMesh mesh = ObjParser.ParseText(
                "v 0 0 0\nv 0 0 1\nv 1 0 1\nvt 0 0\nvn 0 1 0\nf 1/1/1 2/1/1 3/1/1\n");

            Assert.Single(mesh.Faces);
            Assert.Equal(new[] { 0, 1, 2 }, mesh.Faces[0].VertexIndices);
        }

        [Fact]
        public void ParsesNegativeRelativeFaceIndices()
        {
            ObjMesh mesh = ObjParser.ParseText("v 0 0 0\nv 0 0 1\nv 1 0 1\nf -3 -2 -1\n");

            Assert.Single(mesh.Faces);
            Assert.Equal(new[] { 0, 1, 2 }, mesh.Faces[0].VertexIndices);
        }

        [Fact]
        public void WeldsCoincidentVerticesSoSharedEdgesStayShared()
        {
            // The same square as two triangles, but written with duplicated corner vertices, which
            // is what a naive exporter produces. Without welding the two faces would share nothing
            // and the graph would have two islands instead of one.
            ObjMesh mesh = ObjParser.ParseText(
                "v 0 0 0\nv 0 0 1\nv 1 0 1\n" +
                "v 0 0 0\nv 1 0 1\nv 1 0 0\n" +
                "f 1 2 3\nf 4 5 6\n");

            Assert.Equal(4, mesh.Vertices.Count);
            Assert.Equal(2, mesh.WeldedVertexCount);
        }

        [Fact]
        public void CommentsAndUnknownStatementsAreIgnored()
        {
            ObjMesh mesh = ObjParser.ParseText(
                "# a comment\no object\ng group\ns off\nusemtl none\n" +
                "v 0 0 0 # trailing\nv 0 0 1\nv 1 0 1\nf 1 2 3\n");

            Assert.Equal(3, mesh.Vertices.Count);
            Assert.Single(mesh.Faces);
        }

        [Fact]
        public void DegenerateFaceIsDroppedWithAWarningRatherThanCompiled()
        {
            ObjMesh mesh = ObjParser.ParseText(
                "v 0 0 0\nv 0 0 1\nv 1 0 1\nv 0 0 0\nf 1 2 3\nf 1 4 1\n");

            Assert.Single(mesh.Faces);
            Assert.Contains(mesh.Warnings, w => w.Contains("collapsed"));
        }

        [Fact]
        public void FaceLargerThanTheEdgeCountFieldIsRejected()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < 200; i++)
            {
                sb.Append("v ").Append(i).Append(" 0 0\n");
            }

            sb.Append('f');
            for (int i = 1; i <= 200; i++)
            {
                sb.Append(' ').Append(i);
            }

            sb.Append('\n');

            ObjParseException error = Assert.Throws<ObjParseException>(() => ObjParser.ParseText(sb.ToString()));
            Assert.Contains("edge count field", error.Message);
        }

        [Fact]
        public void EmptyObjIsRejected()
        {
            Assert.Throws<ObjParseException>(() => ObjParser.ParseText("# nothing here\n"));
        }

        [Fact]
        public void CompilesTwoTrianglesIntoTwoAdjacentAreas()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            ObjMesh mesh = ObjParser.ParseText(TwoTrianglesSharingAnEdge);

            NavCompileResult result = NavGraphCompiler.Compile(donor, mesh);
            NavGraph graph = donor.NavSet.Graphs[0];

            Assert.Equal(2, result.AreaCount);
            Assert.Equal(5, result.EdgeCount);
            Assert.Equal(1, result.SharedEdgeCount);
            Assert.Equal(4, result.BoundaryEdgeCount);
            Assert.Equal(2, graph.Areas.Count);

            Assert.Equal(1, result.Islands.IslandCount);
            Assert.All(graph.Areas, a => Assert.Equal(1, a.IslandNum));
        }

        [Fact]
        public void CompiledAdjacencyIsBidirectional()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavGraphCompiler.Compile(donor, ObjParser.ParseText(TwoTrianglesSharingAnEdge));
            NavGraph graph = donor.NavSet.Graphs[0];

            NavArea a = graph.Areas[0];
            NavArea b = graph.Areas[1];

            Assert.Contains(a.Edges, e => ReferenceEquals(e.AdjacentArea, b));
            Assert.Contains(b.Edges, e => ReferenceEquals(e.AdjacentArea, a));
        }

        [Fact]
        public void CompiledTraversalCostFollowsTheDistanceRule()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavGraph graph = donor.NavSet.Graphs[0];
            graph.BuildScale = 1.8f;

            NavGraphCompiler.Compile(donor, ObjParser.ParseText(TwoTrianglesSharingAnEdge));

            NavArea a = graph.Areas[0];
            NavArea b = graph.Areas[1];
            double perMetre = Math.Floor(1000.0 / 1.8f);
            uint expected = (uint)((Vec3.Distance(a.Position, b.Position) * perMetre) + 1.0);

            foreach (NavEdge edge in a.Edges)
            {
                if (edge.AdjacentArea != null)
                {
                    Assert.Equal(expected, edge.Flags2);
                }
            }
        }

        [Fact]
        public void BoundaryEdgesCarryNoNeighbourAndNoCost()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavGraphCompiler.Compile(donor, ObjParser.ParseText(TwoTrianglesSharingAnEdge));

            foreach (NavArea area in donor.NavSet.Graphs[0].Areas)
            {
                foreach (NavEdge edge in area.Edges)
                {
                    if (edge.AdjacentArea == null)
                    {
                        Assert.Equal(0u, edge.Flags2);
                        Assert.Equal(0ul, edge.RawAdjacentAreaPointer);
                    }
                }
            }
        }

        [Fact]
        public void TwoDisjointSlabsCompileIntoTwoIslands()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavCompileResult result = NavGraphCompiler.Compile(donor, ObjParser.ParseText(TwoDisjointTriangles));

            Assert.Equal(2, result.Islands.IslandCount);
            Assert.Equal(1, donor.NavSet.Graphs[0].Areas[0].IslandNum);
            Assert.Equal(2, donor.NavSet.Graphs[0].Areas[1].IslandNum);
            Assert.Contains(result.Warnings, w => w.Contains("disconnected"));
        }

        [Fact]
        public void NonManifoldEdgeAbortsAndNamesEveryOffendingObjLine()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            int areasBefore = donor.NavSet.Graphs[0].Areas.Count;

            NavCompileException error = Assert.Throws<NavCompileException>(
                () => NavGraphCompiler.Compile(donor, ObjParser.ParseText(ThreeFacesOnOneEdge)));

            Assert.Contains("not manifold", error.Message);
            Assert.Single(error.Details);
            Assert.Contains("NON-MANIFOLD EDGE", error.Details[0]);

            // The three faces are on source lines 6, 7 and 8.
            Assert.Contains("6", error.Details[0]);
            Assert.Contains("7", error.Details[0]);
            Assert.Contains("8", error.Details[0]);

            // Nothing was written to the donor.
            Assert.Equal(areasBefore, donor.NavSet.Graphs[0].Areas.Count);
        }

        [Fact]
        public void CompilePreservesEveryDonorHeaderValue()
        {
            // The whole reason the route works is that the donor header, and therefore the image
            // version, is inherited rather than synthesised. Worker report 08 section 4.5.
            NavMeshImage donor = SyntheticImage.Grid(3, 3);
            NavGraph graph = donor.NavSet.Graphs[0];

            graph.Version = 43;
            graph.BuildScale = 2.5f;
            graph.VoxelSize = 0.15f;
            graph.AgentRadius = 0.45f;
            graph.StepHeight = 0.4f;
            graph.AgentHeight = 1.9f;
            graph.BuildUpAxis = Axis.Y;
            graph.SerializedLinkDatumSize = 0x38;
            byte[] pad = (byte[])graph.RawPad.Clone();

            NavGraphCompiler.Compile(donor, ObjParser.ParseText(TwoTrianglesSharingAnEdge));

            Assert.Equal(43u, graph.Version);
            Assert.Equal(2.5f, graph.BuildScale);
            Assert.Equal(0.15f, graph.VoxelSize);
            Assert.Equal(0.45f, graph.AgentRadius);
            Assert.Equal(0.4f, graph.StepHeight);
            Assert.Equal(1.9f, graph.AgentHeight);
            Assert.Equal(Axis.Y, graph.BuildUpAxis);
            Assert.Equal(0x38, graph.SerializedLinkDatumSize);
            Assert.Equal(pad, graph.RawPad);
            Assert.Equal(ByteOrderOf(donor), donor.Order);
        }

        [Fact]
        public void CompiledImageSerializesAndParsesBack()
        {
            NavMeshImage donor = SyntheticImage.Grid(3, 3);
            NavGraphCompiler.Compile(donor, ObjParser.ParseText(TwoDisjointTriangles));

            byte[] payload = donor.Write();
            NavRoundTripResult result = NavRoundTrip.Run(payload, forceReserialize: true);

            Assert.True(result.IsByteExact, result.ToString());

            NavGraph parsed = result.Image.NavSet.Graphs[0];
            Assert.Equal(2, parsed.Areas.Count);
            Assert.True(parsed.Diagnostics.IsClean);

            IslandVerification verification = IslandLabeler.Verify(parsed);
            Assert.True(verification.IsConsistent, verification.ToString());
            Assert.Equal(2, verification.ActualComponents);
        }

        [Fact]
        public void CompileThenExportReproducesTheInputFaceForFace()
        {
            // Worker report 08 acceptance test T1, run entirely offline.
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            ObjMesh input = ObjParser.ParseText(TwoTrianglesSharingAnEdge);
            NavGraphCompiler.Compile(donor, input);

            string exported = ObjWriter.WriteText(donor.NavSet.Graphs[0]);
            ObjMesh reimported = ObjParser.ParseText(exported);

            Assert.Equal(input.Faces.Count, reimported.Faces.Count);
            Assert.Equal(input.Vertices.Count, reimported.Vertices.Count);
            Assert.Equal(FaceVertexSets(input), FaceVertexSets(reimported));
        }

        [Fact]
        public void LegacyModeReproducesTheKnownDefectiveSentinelForComparison()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            NavCompileOptions options = new NavCompileOptions { UseLegacyIslandSentinel = true };

            NavCompileResult result = NavGraphCompiler.Compile(
                donor, ObjParser.ParseText(TwoDisjointTriangles), options);

            Assert.All(donor.NavSet.Graphs[0].Areas, a => Assert.Equal(NavArea.SentinelIslandNum, a.IslandNum));
            Assert.Contains(result.Warnings, w => w.Contains("LEGACY MODE"));

            IslandVerification verification = IslandLabeler.Verify(donor.NavSet.Graphs[0]);
            Assert.False(verification.IsConsistent);
        }

        [Fact]
        public void CompileTargetsTheRequestedGraphAndLeavesTheOthersAlone()
        {
            NavMeshImage donor = SyntheticImage.MultiGraph(3, 2, 2);
            int graph0Areas = donor.NavSet.Graphs[0].Areas.Count;
            int graph2Areas = donor.NavSet.Graphs[2].Areas.Count;

            NavGraphCompiler.Compile(
                donor,
                ObjParser.ParseText(TwoTrianglesSharingAnEdge),
                new NavCompileOptions { GraphIndex = 1 });

            Assert.Equal(graph0Areas, donor.NavSet.Graphs[0].Areas.Count);
            Assert.Equal(2, donor.NavSet.Graphs[1].Areas.Count);
            Assert.Equal(graph2Areas, donor.NavSet.Graphs[2].Areas.Count);
        }

        [Fact]
        public void OutOfRangeGraphIndexIsRejected()
        {
            NavMeshImage donor = SyntheticImage.Grid(2, 2);
            Assert.Throws<NavCompileException>(() => NavGraphCompiler.Compile(
                donor, ObjParser.ParseText(TwoTrianglesSharingAnEdge), new NavCompileOptions { GraphIndex = 4 }));
        }

        private static Bfii.Navmesh.Io.ByteOrder ByteOrderOf(NavMeshImage image) => image.Order;

        private static HashSet<string> FaceVertexSets(ObjMesh mesh)
        {
            HashSet<string> result = new HashSet<string>();
            foreach (ObjFace face in mesh.Faces)
            {
                List<string> corners = new List<string>(face.VertexCount);
                for (int i = 0; i < face.VertexCount; i++)
                {
                    corners.Add(mesh.Vertices[face.VertexIndices[i]].ToString());
                }

                corners.Sort(StringComparer.Ordinal);
                result.Add(string.Join(";", corners));
            }

            return result;
        }
    }
}
