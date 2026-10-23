// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavEdge.cs (sha256:561A15963E7BA6EA).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using Bfii.Navmesh.Io;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Format
{
    /// <summary>Edge classification packed into the high bit of the first edge flag word.</summary>
    public enum NavEdgeType
    {
        Normal = 0,
        Portal = 1,
    }

    /// <summary>
    /// One polygon corner of an area, and the outgoing segment that starts at it.
    /// </summary>
    /// <remarks>
    /// Layout, 64-bit pointer form, 32 bytes (COMMUNITY_TESTED, FORMAT-PROVENANCE.md row E1):
    ///   pointer  adjacent-area pointer, stored on disk as a graph-relative byte offset
    ///   Vec3     position (12 bytes)
    ///   uint32   flags1
    ///   uint32   flags2
    ///   uint32   pad
    /// With 32-bit pointers the first field is 4 bytes and the trailing pad is absent, giving 24.
    ///
    /// The segment this edge owns runs from this edge position to the next edge position in the
    /// area, wrapping at the end. Adjacency is therefore per segment, not per vertex.
    /// </remarks>
    public sealed class NavEdge
    {
        /// <summary>All ones in the high half. The value fresh edges are stamped with.</summary>
        public const uint DefaultFlags1 = 0xFFFF0000u;

        public NavEdge(Vec3 position)
        {
            Position = position;
            Flags1 = DefaultFlags1;
            Flags2 = 0;
            RawAdjacentAreaPointer = 0;
            RawTrailingPad = 0;
        }

        internal NavEdge()
        {
        }

        public Vec3 Position { get; set; }

        /// <summary>
        /// Packed word holding the obstacle id and the edge type. Preserved verbatim on read.
        /// </summary>
        public uint Flags1 { get; set; }

        /// <summary>
        /// Traversal cost across this segment. Zero on a boundary segment with no neighbour.
        /// </summary>
        public uint Flags2 { get; set; }

        /// <summary>
        /// The adjacency pointer exactly as it appeared on disk, a graph-relative byte offset.
        /// Preserved so an untouched graph re-serializes byte for byte.
        /// </summary>
        public ulong RawAdjacentAreaPointer { get; set; }

        /// <summary>The 4-byte pad after the flags in 64-bit pointer form, preserved verbatim.</summary>
        public uint RawTrailingPad { get; set; }

        /// <summary>
        /// Resolved neighbour. Null on a boundary segment. When non-null this wins over
        /// <see cref="RawAdjacentAreaPointer"/> at write time.
        /// </summary>
        public NavArea? AdjacentArea { get; set; }

        /// <summary>Byte offset at which this edge adjacency pointer was written, for patching.</summary>
        internal int WrittenPointerOffset { get; set; }

        /// <summary>Obstacle id, low 15 bits of the first flag word.</summary>
        public int ObstacleId
        {
            get => (int)(Flags1 & 0x7FFFu);
            set => Flags1 = (Flags1 & ~0x7FFFu) | ((uint)value & 0x7FFFu);
        }

        /// <summary>Edge type, bit 15 of the first flag word.</summary>
        public NavEdgeType EdgeType
        {
            get => (NavEdgeType)((Flags1 & 0x8000u) >> 15);
            set => Flags1 = (Flags1 & ~0x8000u) | (((uint)value & 1u) << 15);
        }

        public bool HasNeighbour => AdjacentArea != null || RawAdjacentAreaPointer != 0;

        public static int SizeInBytes(uint pointerSizeCode) => pointerSizeCode == 1 ? 32 : 24;

        internal static NavEdge Read(BlobReader reader, ByteOrder order, uint pointerSizeCode)
        {
            NavEdge edge = new NavEdge
            {
                RawAdjacentAreaPointer = reader.ReadPointer(order, pointerSizeCode, "edge.adjacentArea"),
                Position = reader.ReadVec3(order, "edge.position"),
                Flags1 = reader.ReadUInt32(order, "edge.flags1"),
                Flags2 = reader.ReadUInt32(order, "edge.flags2"),
            };

            if (pointerSizeCode == 1)
            {
                edge.RawTrailingPad = reader.ReadUInt32(order, "edge.pad");
            }

            return edge;
        }

        internal void Write(BlobWriter writer, ByteOrder order, uint pointerSizeCode)
        {
            WrittenPointerOffset = writer.Position;

            // Written as the raw value; patched to the resolved neighbour offset after every area
            // in the graph has a final offset. A boundary edge keeps its raw value, which is how
            // an untouched graph stays byte identical.
            writer.WritePointer(RawAdjacentAreaPointer, order, pointerSizeCode);
            writer.WriteVec3(Position, order);
            writer.WriteUInt32(Flags1, order);
            writer.WriteUInt32(Flags2, order);

            if (pointerSizeCode == 1)
            {
                writer.WriteUInt32(RawTrailingPad, order);
            }
        }
    }
}
