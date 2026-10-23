// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavKdNode.cs (sha256:CAE9E34A24703854).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Io;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Format
{
    /// <summary>
    /// One node of the per-graph KD tree that indexes areas spatially.
    /// </summary>
    /// <remarks>
    /// Encoding, one packed 32-bit word (COMMUNITY_TESTED, FORMAT-PROVENANCE.md row K1):
    ///   bit 31      leaf flag
    ///   bits 30..28 split axis, internal nodes only
    ///   bits 27..0  byte offset from this node to its right child, internal nodes only
    ///   bits 30..0  graph-relative byte offset of the referenced area, leaf nodes only
    /// An internal node is followed by two floats (the left and right split planes) and then by
    /// its left child immediately; the right child sits at nodeStart + rightOffset. A leaf is the
    /// packed word and nothing else, so a leaf is 4 bytes and an internal node is 12 plus its
    /// subtrees.
    /// </remarks>
    public sealed class NavKdNode
    {
        private const uint LeafFlag = 0x80000000u;
        private const uint SplitAxisMask = 0x70000000u;
        private const int SplitAxisShift = 28;
        private const uint RightOffsetMask = 0x0FFFFFFFu;
        private const uint LeafOffsetMask = 0x7FFFFFFFu;

        private NavKdNode()
        {
        }

        /// <summary>Builds an internal node.</summary>
        public static NavKdNode Internal(Axis axis, NavKdNode left, float leftPlane, NavKdNode right, float rightPlane)
        {
            if (left == null)
            {
                throw new ArgumentNullException(nameof(left));
            }

            if (right == null)
            {
                throw new ArgumentNullException(nameof(right));
            }

            return new NavKdNode
            {
                IsLeaf = false,
                SplitAxis = axis,
                Left = left,
                LeftPlane = leftPlane,
                Right = right,
                RightPlane = rightPlane,
            };
        }

        /// <summary>Builds a leaf node referencing one area.</summary>
        public static NavKdNode Leaf(NavArea area)
        {
            if (area == null)
            {
                throw new ArgumentNullException(nameof(area));
            }

            return new NavKdNode
            {
                IsLeaf = true,
                LeafArea = area,
                LeafAreaOffset = area.GraphRelativeOffset,
            };
        }

        public bool IsLeaf { get; private set; }

        public Axis SplitAxis { get; private set; }

        public NavKdNode? Left { get; private set; }

        public NavKdNode? Right { get; private set; }

        public float LeftPlane { get; private set; }

        public float RightPlane { get; private set; }

        /// <summary>Resolved leaf target, or null when the stored offset matched no area.</summary>
        public NavArea? LeafArea { get; private set; }

        /// <summary>Leaf target exactly as stored, a graph-relative byte offset.</summary>
        public uint LeafAreaOffset { get; private set; }

        /// <summary>Serialized size of this node and everything under it.</summary>
        public int SizeInBytes()
        {
            if (IsLeaf)
            {
                return 4;
            }

            return 12 + Left!.SizeInBytes() + Right!.SizeInBytes();
        }

        /// <summary>Every area referenced by a leaf at or below this node, in traversal order.</summary>
        public void CollectAreas(List<NavArea> into)
        {
            if (IsLeaf)
            {
                if (LeafArea != null)
                {
                    into.Add(LeafArea);
                }

                return;
            }

            Left!.CollectAreas(into);
            Right!.CollectAreas(into);
        }

        /// <summary>Union of the bounds of every area under this node.</summary>
        public Bounds3 CalcBounds()
        {
            List<NavArea> areas = new List<NavArea>();
            CollectAreas(areas);

            if (areas.Count == 0)
            {
                throw new NavFormatException("KD node covers no resolvable area, so it has no bounds");
            }

            Bounds3 bounds = areas[0].CalcBounds();
            for (int i = 1; i < areas.Count; i++)
            {
                bounds = Bounds3.Union(bounds, areas[i].CalcBounds());
            }

            return bounds;
        }

        public int CountNodes() => IsLeaf ? 1 : 1 + Left!.CountNodes() + Right!.CountNodes();

        public int Depth() => IsLeaf ? 1 : 1 + Math.Max(Left!.Depth(), Right!.Depth());

        internal static NavKdNode Read(
            BlobReader reader,
            ByteOrder order,
            IReadOnlyDictionary<uint, NavArea> areasByOffset,
            NavKdTreeDiagnostics diagnostics)
        {
            int nodeStart = reader.Position;
            uint data = reader.ReadUInt32(order, "kdNode.data");

            if ((data & LeafFlag) != 0)
            {
                uint offset = data & LeafOffsetMask;
                NavArea? area;
                if (!areasByOffset.TryGetValue(offset, out area))
                {
                    diagnostics.UnresolvedLeafOffsets++;
                    area = null;
                }

                return new NavKdNode
                {
                    IsLeaf = true,
                    LeafArea = area,
                    LeafAreaOffset = offset,
                };
            }

            NavKdNode node = new NavKdNode
            {
                IsLeaf = false,
                SplitAxis = (Axis)((data & SplitAxisMask) >> SplitAxisShift),
                LeftPlane = reader.ReadSingle(order, "kdNode.leftPlane"),
                RightPlane = reader.ReadSingle(order, "kdNode.rightPlane"),
            };

            node.Left = Read(reader, order, areasByOffset, diagnostics);

            uint rightOffset = data & RightOffsetMask;
            int rightPosition = nodeStart + (int)rightOffset;
            if (rightPosition < nodeStart || rightPosition > reader.Length)
            {
                throw new NavFormatException(
                    "KD node at offset " + nodeStart + " points its right child to " + rightPosition +
                    ", which is outside the image");
            }

            reader.Position = rightPosition;
            node.Right = Read(reader, order, areasByOffset, diagnostics);
            return node;
        }

        internal void Write(BlobWriter writer, ByteOrder order)
        {
            int nodeStart = writer.Position;

            if (IsLeaf)
            {
                uint offset = LeafArea != null ? LeafArea.GraphRelativeOffset : LeafAreaOffset;
                if ((offset & ~LeafOffsetMask) != 0)
                {
                    throw new NavFormatException(
                        "KD leaf area offset " + offset + " does not fit in 31 bits");
                }

                writer.WriteUInt32(LeafFlag | offset, order);
                return;
            }

            // Placeholder: the right-child offset is only known once the left subtree is written.
            writer.WriteUInt32(0, order);
            writer.WriteSingle(LeftPlane, order);
            writer.WriteSingle(RightPlane, order);

            Left!.Write(writer, order);

            int rightOffset = writer.Position - nodeStart;
            if (((uint)rightOffset & ~RightOffsetMask) != 0)
            {
                throw new NavFormatException(
                    "KD subtree is too large: right-child offset " + rightOffset + " does not fit in 28 bits");
            }

            uint data = (((uint)SplitAxis << SplitAxisShift) & SplitAxisMask) | ((uint)rightOffset & RightOffsetMask);
            writer.PatchUInt32(nodeStart, data, order);

            Right!.Write(writer, order);
        }
    }

    /// <summary>Counters describing how cleanly a KD tree resolved against its graph.</summary>
    public sealed class NavKdTreeDiagnostics
    {
        /// <summary>Leaves whose stored area offset matched no area in the graph.</summary>
        public int UnresolvedLeafOffsets { get; set; }
    }
}
