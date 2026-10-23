// VENDORED from BfiiMapTools tests\Bfii.Navmesh.Tests\ContainerRoundTripTests.cs (sha256:12C7E2807772C2A4).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System.Collections.Generic;
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Io;
using Xunit;

namespace Bfii.Navmesh.Tests
{
    /// <summary>
    /// The container gate: every image this library writes must parse back and re-serialize to the
    /// identical byte sequence, in both write modes, across the shape matrix.
    /// </summary>
    /// <remarks>
    /// This is an L0 result in master plan terms. It proves the reader and the writer are
    /// inverses. It proves nothing about whether BFII agrees with either, and no test in this file
    /// should ever be described as if it did.
    /// </remarks>
    public sealed class ContainerRoundTripTests
    {
        public static IEnumerable<object[]> ImageShapes()
        {
            foreach (ByteOrder order in new[] { ByteOrder.Little, ByteOrder.Big })
            {
                foreach (uint pointerSize in new uint[] { 1, 0 })
                {
                    yield return new object[] { order, pointerSize, 1, 1 };
                    yield return new object[] { order, pointerSize, 2, 3 };
                    yield return new object[] { order, pointerSize, 5, 4 };
                }
            }
        }

        [Theory]
        [MemberData(nameof(ImageShapes))]
        public void GridImageRoundTripsByteExact(ByteOrder order, uint pointerSize, int columns, int rows)
        {
            NavMeshImage image = SyntheticImage.Grid(columns, rows, 1f, order, pointerSize);
            byte[] payload = image.Write();

            NavRoundTripResult result = NavRoundTrip.Run(payload);

            Assert.True(result.IsByteExact, "preserving mode: " + result);
        }

        [Theory]
        [MemberData(nameof(ImageShapes))]
        public void GridImageRoundTripsByteExactWhenFullyReserialized(
            ByteOrder order, uint pointerSize, int columns, int rows)
        {
            NavMeshImage image = SyntheticImage.Grid(columns, rows, 1f, order, pointerSize);
            byte[] payload = image.Write();

            NavRoundTripResult result = NavRoundTrip.Run(payload, forceReserialize: true);

            Assert.True(result.IsByteExact, "reserialize mode: " + result);
        }

        [Fact]
        public void MultiGraphImageRoundTripsByteExact()
        {
            NavMeshImage image = SyntheticImage.MultiGraph(3, 3, 3);
            byte[] payload = image.Write();

            NavRoundTripResult preserving = NavRoundTrip.Run(payload);
            NavRoundTripResult reserialized = NavRoundTrip.Run(payload, forceReserialize: true);

            Assert.True(preserving.IsByteExact, "preserving mode: " + preserving);
            Assert.True(reserialized.IsByteExact, "reserialize mode: " + reserialized);
            Assert.Equal(3, preserving.Image.NavSet.Graphs.Count);
        }

        [Fact]
        public void ParsedImagePreservesEveryHeaderField()
        {
            NavMeshImage original = SyntheticImage.Grid(4, 4);
            original.RuntimeFlags = 0x1234ABCD;
            original.ConstantFlags = 0x0F0F0F0F;
            NavGraph originalGraph = original.NavSet.Graphs[0];
            originalGraph.Layer = 7;
            originalGraph.SerializedLinkDatumSize = 0x38;

            byte[] payload = original.Write();
            NavMeshImage parsed = NavMeshImage.Read(payload);
            NavGraph parsedGraph = parsed.NavSet.Graphs[0];

            Assert.Equal(original.Order, parsed.Order);
            Assert.Equal(NavMeshImage.ExpectedResourceVersion, parsed.ResourceVersion);
            Assert.Equal(NavMeshImage.ExpectedSectionId, parsed.SectionId);
            Assert.Equal(original.PointerSizeCode, parsed.PointerSizeCode);
            Assert.Equal(0x1234ABCDu, parsed.RuntimeFlags);
            Assert.Equal(0x0F0F0F0Fu, parsed.ConstantFlags);

            Assert.Equal(originalGraph.Version, parsedGraph.Version);
            Assert.Equal(7u, parsedGraph.Layer);
            Assert.Equal(originalGraph.BuildScale, parsedGraph.BuildScale);
            Assert.Equal(originalGraph.VoxelSize, parsedGraph.VoxelSize);
            Assert.Equal(originalGraph.AgentRadius, parsedGraph.AgentRadius);
            Assert.Equal(originalGraph.StepHeight, parsedGraph.StepHeight);
            Assert.Equal(originalGraph.AgentHeight, parsedGraph.AgentHeight);
            Assert.Equal(originalGraph.BuildUpAxis, parsedGraph.BuildUpAxis);
            Assert.Equal(0x38, parsedGraph.SerializedLinkDatumSize);
            Assert.Equal(originalGraph.Bounds, parsedGraph.Bounds);
        }

        [Fact]
        public void ParsedImagePreservesTheOpaqueGraphPadVerbatim()
        {
            NavMeshImage original = SyntheticImage.Grid(2, 2);
            byte[] expectedPad = (byte[])original.NavSet.Graphs[0].RawPad.Clone();

            // The synthetic pad is deliberately non-zero, so a writer that zeroed it would fail
            // here instead of passing because both sides were empty.
            Assert.Contains(expectedPad, b => b != 0);

            NavMeshImage parsed = NavMeshImage.Read(original.Write());

            Assert.Equal(NavGraph.PadLength, parsed.NavSet.Graphs[0].RawPad.Length);
            Assert.Equal(expectedPad, parsed.NavSet.Graphs[0].RawPad);
        }

        [Fact]
        public void AdjacencyResolvesToObjectsAndSurvivesRoundTrip()
        {
            NavMeshImage image = SyntheticImage.Grid(3, 3);
            NavMeshImage parsed = NavMeshImage.Read(image.Write());
            NavGraph graph = parsed.NavSet.Graphs[0];

            Assert.True(graph.Diagnostics.IsClean, "diagnostics: " + graph.Diagnostics.UnresolvedAdjacencyPointers);

            int resolved = 0;
            foreach (NavArea area in graph.Areas)
            {
                foreach (NavEdge edge in area.Edges)
                {
                    if (edge.AdjacentArea != null)
                    {
                        resolved++;
                        Assert.Contains(edge.AdjacentArea, graph.Areas);
                    }
                }
            }

            // A 3x3 grid of quads has 12 interior segments, each contributing two directed links.
            Assert.Equal(24, resolved);
        }

        [Fact]
        public void KdTreeIndexesEveryArea()
        {
            NavMeshImage image = SyntheticImage.Grid(4, 4);
            NavMeshImage parsed = NavMeshImage.Read(image.Write());
            NavGraph graph = parsed.NavSet.Graphs[0];

            Assert.Equal(0, graph.KdTree.Diagnostics.UnresolvedLeafOffsets);

            List<NavArea> leaves = new List<NavArea>();
            graph.KdTree.Root!.CollectAreas(leaves);

            Assert.Equal(graph.Areas.Count, leaves.Count);
            Assert.Equal(new HashSet<NavArea>(graph.Areas), new HashSet<NavArea>(leaves));
        }

        [Fact]
        public void SectionAndImageSizeFieldsDescribeTheActualPayload()
        {
            NavMeshImage image = SyntheticImage.Grid(3, 2);
            byte[] payload = image.Write();
            NavMeshImage parsed = NavMeshImage.Read(payload);

            Assert.Equal((uint)(payload.Length - NavMeshImage.ResourceHeaderSize), parsed.SourceImageSize);
        }

        [Fact]
        public void TruncatedPayloadFailsWithAnOffset()
        {
            byte[] payload = SyntheticImage.Grid(3, 3).Write();
            byte[] truncated = new byte[payload.Length - 40];
            System.Array.Copy(payload, truncated, truncated.Length);

            NavFormatException error = Assert.Throws<NavFormatException>(() => NavMeshImage.Read(truncated));
            Assert.Contains("declares", error.Message);
        }

        [Fact]
        public void UnknownResourceVersionIsRejectedRatherThanGuessed()
        {
            byte[] payload = SyntheticImage.Grid(2, 2, 1f, ByteOrder.Little).Write();

            // Resource version sits at offset 4, little endian in this image.
            payload[4] = 3;

            NavFormatException error = Assert.Throws<NavFormatException>(() => NavMeshImage.Read(payload));
            Assert.Contains("resource header version", error.Message);
        }

        [Fact]
        public void CorruptEndianMarkerIsRejected()
        {
            byte[] payload = SyntheticImage.Grid(2, 2).Write();
            payload[0] = 0x12;

            Assert.Throws<NavFormatException>(() => NavMeshImage.Read(payload));
        }
    }
}
