// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavGraph.cs (sha256:A7CAE233903FA460).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Io;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Format
{
    /// <summary>
    /// One navigation graph: a header, a contiguous run of areas, and a KD tree over them.
    /// </summary>
    /// <remarks>
    /// Header layout at image version 43 (COMMUNITY_TESTED, FORMAT-PROVENANCE.md row G1). Offsets
    /// are from the start of the graph:
    ///   0x00 uint32  version
    ///   0x04 uint32  layer
    ///   0x08 int32   area region byte count
    ///   0x0C int32   KD tree byte count
    ///   0x10 int32   link record byte count
    ///   0x14 int32   link data byte count           -- version 43 and above only
    ///   0x18 int32   serialized link datum size     -- version 43 and above only
    ///   0x1C int32   link datum count               -- version 43 and above only
    ///   0x20 int32   total graph byte count
    ///   0x24 float   build scale
    ///   0x28 float   voxel size
    ///   0x2C float   agent radius
    ///   0x30 float   step height
    ///   0x34 float   agent height
    ///   0x38 Bounds3 graph bounds (24 bytes)
    ///   0x50 uint32  build up axis
    ///   0x54 byte    pad, 252 bytes, not always zero
    /// giving a 336-byte header. Below version 43 the three link-data fields are absent and the
    /// header is 324 bytes.
    ///
    /// BFII reads image version 43. That is an INFERRED value with two corroborations, recorded in
    /// worker report 08 section 4.5 and evidence\navmesh\FORMAT-PROVENANCE.md row G2, not a value
    /// read out of the BFII executable by this session. The practical consequence is the reason
    /// the whole compiler is donor seeded: preserving a donor header preserves its version, so no
    /// version negotiation is ever attempted.
    /// </remarks>
    public sealed class NavGraph
    {
        /// <summary>The image version at and above which the three link-data fields are present.</summary>
        public const uint LinkDataFieldsMinVersion = 43;

        /// <summary>The image version BFII is understood to read.</summary>
        public const uint BfiiImageVersion = 43;

        /// <summary>Byte length of the opaque tail of the graph header.</summary>
        public const int PadLength = 252;

        public NavGraph()
        {
            Areas = new List<NavArea>();
            KdTree = new NavKdTree();
            RawPad = new byte[PadLength];
            Version = BfiiImageVersion;
            Diagnostics = new NavGraphDiagnostics();
        }

        public uint Version { get; set; }

        public uint Layer { get; set; }

        public float BuildScale { get; set; }

        public float VoxelSize { get; set; }

        public float AgentRadius { get; set; }

        public float StepHeight { get; set; }

        public float AgentHeight { get; set; }

        public Bounds3 Bounds { get; set; }

        public Axis BuildUpAxis { get; set; }

        /// <summary>The 252-byte header tail, preserved verbatim. Shipped graphs do not zero it.</summary>
        public byte[] RawPad { get; set; }

        /// <summary>Link fields, preserved verbatim. Every graph seen so far reports zero links.</summary>
        public int RawLinkRecordBytes { get; set; }

        public int RawLinkDataBytes { get; set; }

        public int SerializedLinkDatumSize { get; set; }

        public int RawLinkDatumCount { get; set; }

        public List<NavArea> Areas { get; }

        public NavKdTree KdTree { get; set; }

        public NavGraphDiagnostics Diagnostics { get; }

        /// <summary>
        /// Area offsets as read, used to decide whether the KD tree source image is still valid.
        /// Null for a graph that was built in memory.
        /// </summary>
        public uint[]? SourceAreaOffsets { get; private set; }

        public bool HasLinkDataFields => Version >= LinkDataFieldsMinVersion;

        public int HeaderSizeInBytes => HasLinkDataFields ? 336 : 324;

        /// <summary>Recomputes the graph bounds as the union of every area vertex.</summary>
        public void RecalculateBounds()
        {
            List<Vec3> points = new List<Vec3>();
            for (int i = 0; i < Areas.Count; i++)
            {
                List<NavEdge> edges = Areas[i].Edges;
                for (int j = 0; j < edges.Count; j++)
                {
                    points.Add(edges[j].Position);
                }
            }

            if (points.Count == 0)
            {
                throw new NavFormatException("cannot compute graph bounds: the graph has no vertices");
            }

            Bounds = Bounds3.FromPoints(points);
        }

        /// <summary>
        /// Traversal cost stamped on a segment joining two areas. Distance between the area
        /// centres, scaled by the integer cost-per-metre implied by the build scale, plus one.
        /// COMMUNITY_TESTED (FORMAT-PROVENANCE.md row C1).
        /// </summary>
        public uint TraversalCost(NavArea a, NavArea b)
        {
            double perMetre = Math.Floor(1000.0 / BuildScale);
            double cost = (Vec3.Distance(a.Position, b.Position) * perMetre) + 1.0;

            if (cost < 0.0)
            {
                return 0;
            }

            return cost >= uint.MaxValue ? uint.MaxValue : (uint)cost;
        }

        /// <summary>Recomputes every segment cost from the current area centres.</summary>
        public void RecalculateTraversalCosts()
        {
            for (int i = 0; i < Areas.Count; i++)
            {
                NavArea area = Areas[i];
                for (int j = 0; j < area.Edges.Count; j++)
                {
                    NavEdge edge = area.Edges[j];
                    if (edge.AdjacentArea != null)
                    {
                        edge.Flags2 = TraversalCost(area, edge.AdjacentArea);
                    }
                }
            }
        }

        internal static NavGraph Read(BlobReader reader, ByteOrder order, uint pointerSizeCode)
        {
            int graphStart = reader.Position;
            NavGraph graph = new NavGraph
            {
                Version = reader.ReadUInt32(order, "graph.version"),
            };

            graph.Layer = reader.ReadUInt32(order, "graph.layer");
            int areaBytes = reader.ReadInt32(order, "graph.areaBytes");
            int kdTreeBytes = reader.ReadInt32(order, "graph.kdTreeBytes");
            graph.RawLinkRecordBytes = reader.ReadInt32(order, "graph.linkRecordBytes");

            if (graph.HasLinkDataFields)
            {
                graph.RawLinkDataBytes = reader.ReadInt32(order, "graph.linkDataBytes");
                graph.SerializedLinkDatumSize = reader.ReadInt32(order, "graph.serializedLinkDatumSize");
                graph.RawLinkDatumCount = reader.ReadInt32(order, "graph.linkDatumCount");
            }

            int totalBytes = reader.ReadInt32(order, "graph.totalBytes");
            graph.BuildScale = reader.ReadSingle(order, "graph.buildScale");
            graph.VoxelSize = reader.ReadSingle(order, "graph.voxelSize");
            graph.AgentRadius = reader.ReadSingle(order, "graph.agentRadius");
            graph.StepHeight = reader.ReadSingle(order, "graph.stepHeight");
            graph.AgentHeight = reader.ReadSingle(order, "graph.agentHeight");
            graph.Bounds = reader.ReadBounds3(order, "graph.bounds");
            graph.BuildUpAxis = (Axis)reader.ReadUInt32(order, "graph.buildUpAxis");
            graph.RawPad = reader.ReadBytes(PadLength, "graph.pad");

            if (reader.Position - graphStart != graph.HeaderSizeInBytes)
            {
                throw new NavFormatException(
                    "graph header consumed " + (reader.Position - graphStart) + " bytes, expected " +
                    graph.HeaderSizeInBytes + " at offset " + graphStart);
            }

            if (areaBytes < 0 || kdTreeBytes < 0)
            {
                throw new NavFormatException(
                    "graph at offset " + graphStart + " declares a negative region size (areas=" +
                    areaBytes + ", kdTree=" + kdTreeBytes + ")");
            }

            int areasStart = reader.Position;
            int areasEnd = areasStart + areaBytes;
            reader.Require(areaBytes, "graph.areas");

            while (reader.Position < areasEnd)
            {
                graph.Areas.Add(NavArea.Read(reader, order, pointerSizeCode, graphStart));
            }

            if (reader.Position != areasEnd)
            {
                throw new NavFormatException(
                    "graph area region overran: consumed " + (reader.Position - areasStart) + " of " +
                    areaBytes + " bytes at offset " + areasStart);
            }

            Dictionary<uint, NavArea> areasByOffset = new Dictionary<uint, NavArea>(graph.Areas.Count);
            for (int i = 0; i < graph.Areas.Count; i++)
            {
                NavArea area = graph.Areas[i];
                if (areasByOffset.ContainsKey(area.GraphRelativeOffset))
                {
                    throw new NavFormatException(
                        "two areas share graph-relative offset " + area.GraphRelativeOffset);
                }

                areasByOffset.Add(area.GraphRelativeOffset, area);
            }

            graph.ResolveAdjacency(areasByOffset);

            graph.KdTree = NavKdTree.Read(reader, order, areasByOffset, kdTreeBytes);
            graph.KdTree.IsDirty = false;
            graph.Diagnostics.UnresolvedKdLeafOffsets = graph.KdTree.Diagnostics.UnresolvedLeafOffsets;

            int consumed = reader.Position - graphStart;
            if (consumed != totalBytes)
            {
                throw new NavFormatException(
                    "graph consumed " + consumed + " bytes but declared a total of " + totalBytes +
                    " at offset " + graphStart);
            }

            graph.SourceAreaOffsets = new uint[graph.Areas.Count];
            for (int i = 0; i < graph.Areas.Count; i++)
            {
                graph.SourceAreaOffsets[i] = graph.Areas[i].GraphRelativeOffset;
            }

            return graph;
        }

        /// <summary>
        /// Turns stored adjacency offsets into object references. An offset that matches no area is
        /// counted and left as a raw value rather than treated as a parse failure, so a donor with
        /// an adjacency convention this library has not seen still round-trips byte for byte.
        /// </summary>
        private void ResolveAdjacency(IReadOnlyDictionary<uint, NavArea> areasByOffset)
        {
            for (int i = 0; i < Areas.Count; i++)
            {
                List<NavEdge> edges = Areas[i].Edges;
                for (int j = 0; j < edges.Count; j++)
                {
                    NavEdge edge = edges[j];
                    if (edge.RawAdjacentAreaPointer == 0)
                    {
                        continue;
                    }

                    if (edge.RawAdjacentAreaPointer > uint.MaxValue)
                    {
                        Diagnostics.UnresolvedAdjacencyPointers++;
                        continue;
                    }

                    NavArea? target;
                    if (areasByOffset.TryGetValue((uint)edge.RawAdjacentAreaPointer, out target))
                    {
                        edge.AdjacentArea = target;
                    }
                    else
                    {
                        Diagnostics.UnresolvedAdjacencyPointers++;
                    }
                }
            }
        }

        internal void Write(BlobWriter writer, ByteOrder order, uint pointerSizeCode)
        {
            int graphStart = writer.Position;

            writer.WriteUInt32(Version, order);
            writer.WriteUInt32(Layer, order);

            int areaBytesOffset = writer.Position;
            writer.WriteInt32(0, order);
            int kdTreeBytesOffset = writer.Position;
            writer.WriteInt32(0, order);

            writer.WriteInt32(RawLinkRecordBytes, order);

            if (HasLinkDataFields)
            {
                writer.WriteInt32(RawLinkDataBytes, order);
                writer.WriteInt32(SerializedLinkDatumSize, order);
                writer.WriteInt32(RawLinkDatumCount, order);
            }

            int totalBytesOffset = writer.Position;
            writer.WriteInt32(0, order);

            writer.WriteSingle(BuildScale, order);
            writer.WriteSingle(VoxelSize, order);
            writer.WriteSingle(AgentRadius, order);
            writer.WriteSingle(StepHeight, order);
            writer.WriteSingle(AgentHeight, order);
            writer.WriteBounds3(Bounds, order);
            writer.WriteUInt32((uint)BuildUpAxis, order);

            if (RawPad == null || RawPad.Length != PadLength)
            {
                throw new NavFormatException(
                    "graph pad must be exactly " + PadLength + " bytes, found " +
                    (RawPad == null ? "null" : RawPad.Length.ToString()));
            }

            writer.WriteBytes(RawPad);

            int areasStart = writer.Position;
            for (int i = 0; i < Areas.Count; i++)
            {
                Areas[i].Write(writer, order, pointerSizeCode, graphStart);
            }

            int areaBytes = writer.Position - areasStart;

            // Adjacency can only be patched once every area has a final offset.
            for (int i = 0; i < Areas.Count; i++)
            {
                List<NavEdge> edges = Areas[i].Edges;
                for (int j = 0; j < edges.Count; j++)
                {
                    NavEdge edge = edges[j];
                    if (edge.AdjacentArea != null)
                    {
                        writer.PatchPointer(
                            edge.WrittenPointerOffset,
                            edge.AdjacentArea.GraphRelativeOffset,
                            order,
                            pointerSizeCode);
                    }
                }
            }

            bool layoutUnchanged = AreaLayoutMatchesSource();

            int kdStart = writer.Position;
            KdTree.Write(writer, order, layoutUnchanged);
            int kdTreeBytes = writer.Position - kdStart;

            int totalBytes = writer.Position - graphStart;

            writer.PatchInt32(areaBytesOffset, areaBytes, order);
            writer.PatchInt32(kdTreeBytesOffset, kdTreeBytes, order);
            writer.PatchInt32(totalBytesOffset, totalBytes, order);
        }

        /// <summary>
        /// True when every area landed at exactly the offset it had when read, which is the
        /// precondition for re-emitting a KD tree source image that stores those offsets.
        /// </summary>
        private bool AreaLayoutMatchesSource()
        {
            if (SourceAreaOffsets == null || SourceAreaOffsets.Length != Areas.Count)
            {
                return false;
            }

            for (int i = 0; i < Areas.Count; i++)
            {
                if (Areas[i].GraphRelativeOffset != SourceAreaOffsets[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Counters describing how cleanly a graph resolved against itself.</summary>
    public sealed class NavGraphDiagnostics
    {
        /// <summary>Edge adjacency values that matched no area offset in the same graph.</summary>
        public int UnresolvedAdjacencyPointers { get; set; }

        /// <summary>KD leaves whose stored area offset matched no area in the same graph.</summary>
        public int UnresolvedKdLeafOffsets { get; set; }

        public bool IsClean => UnresolvedAdjacencyPointers == 0 && UnresolvedKdLeafOffsets == 0;
    }
}
