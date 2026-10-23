// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavSet.cs (sha256:4A428FD7A11C41C7).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Io;

namespace Bfii.Navmesh.Format
{
    /// <summary>
    /// The collection of graphs in one image, one graph per agent type on the level.
    /// </summary>
    /// <remarks>
    /// Layout (COMMUNITY_TESTED, FORMAT-PROVENANCE.md row S1):
    ///   uint32  endianness flag, repeated from the resource header
    ///   uint32  version
    ///   int32   graph count
    ///   graphs
    ///
    /// A level whose PathfindingTypesOnLevel array has more than one entry has more than one
    /// graph here. Worker report 08 section 3.3 records that the only existing community importer
    /// replaces graph 0 and silently drops the rest on export; this reader and writer carry every
    /// graph, and the compiler takes an explicit graph index.
    /// </remarks>
    public sealed class NavSet
    {
        public NavSet()
        {
            Graphs = new List<NavGraph>();
            Version = NavGraph.BfiiImageVersion;
        }

        /// <summary>
        /// The order flag stored inside the set header. Normally identical to the image order; it
        /// is kept separately so a donor that disagrees with itself still round-trips.
        /// </summary>
        public ByteOrder DeclaredOrder { get; set; }

        public uint Version { get; set; }

        public List<NavGraph> Graphs { get; }

        internal static NavSet Read(BlobReader reader, ByteOrder order, uint pointerSizeCode)
        {
            NavSet set = new NavSet
            {
                DeclaredOrder = NavEndianFlag.Decode(
                    reader.ReadUInt32(order, "navSet.endianFlag"), "navSet.endianFlag"),
            };

            set.Version = reader.ReadUInt32(order, "navSet.version");
            int graphCount = reader.ReadInt32(order, "navSet.graphCount");

            if (graphCount < 0)
            {
                throw new NavFormatException("navigation set declares a negative graph count: " + graphCount);
            }

            for (int i = 0; i < graphCount; i++)
            {
                set.Graphs.Add(NavGraph.Read(reader, order, pointerSizeCode));
            }

            return set;
        }

        internal void Write(BlobWriter writer, ByteOrder order, uint pointerSizeCode)
        {
            writer.WriteUInt32(NavEndianFlag.Encode(DeclaredOrder), order);
            writer.WriteUInt32(Version, order);
            writer.WriteInt32(Graphs.Count, order);

            for (int i = 0; i < Graphs.Count; i++)
            {
                Graphs[i].Write(writer, order, pointerSizeCode);
            }
        }
    }

    /// <summary>
    /// The 32-bit order flag: all ones for big endian, all zeros for little endian.
    /// </summary>
    /// <remarks>
    /// Both encodings are palindromic across a byte swap, which is what lets the flag be read
    /// before the order is known. Any other value means the cursor is not where the reader thinks
    /// it is, so it is a hard parse error rather than a default.
    /// </remarks>
    public static class NavEndianFlag
    {
        public const uint BigEndian = 0xFFFFFFFFu;
        public const uint LittleEndian = 0x00000000u;

        public static ByteOrder Decode(uint value, string what)
        {
            if (value == BigEndian)
            {
                return ByteOrder.Big;
            }

            if (value == LittleEndian)
            {
                return ByteOrder.Little;
            }

            throw new NavFormatException(
                what + " is 0x" + value.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) +
                ", which is neither the big-endian nor the little-endian marker");
        }

        public static uint Encode(ByteOrder order) => order == ByteOrder.Big ? BigEndian : LittleEndian;
    }
}
