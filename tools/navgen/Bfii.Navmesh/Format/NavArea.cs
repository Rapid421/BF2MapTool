// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavArea.cs (sha256:0CD96868D3A59865).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Io;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Format
{
    /// <summary>One convex walkable polygon: the unit the planner searches over.</summary>
    /// <remarks>
    /// Layout, 64-bit pointer form, 72-byte header followed by EdgeCount edges
    /// (COMMUNITY_TESTED, FORMAT-PROVENANCE.md row A1):
    ///   pointer x4  runtime-only: proxy, dynamic data, first link, search parent
    ///   Vec3        position (12 bytes)
    ///   float       radius
    ///   uint32      search cost
    ///   uint32      usage flags
    ///   uint32      flags1  -- packs edge count and island number
    ///   uint32      flags2  -- packs usage count, cost multipliers and basis vertex
    ///   uint32      flags3
    ///   uint32      flags4
    /// With 32-bit pointers the header is 56 bytes.
    ///
    /// The four runtime pointers and the search cost are live-memory scratch; a shipped image can
    /// carry anything in them. They are preserved verbatim rather than normalised, because
    /// normalising them would break byte-exact round trip on a donor for no benefit.
    /// </remarks>
    public sealed class NavArea
    {
        /// <summary>Bit width of the island field. 18 bits, so ids run 0..262143.</summary>
        public const int IslandNumBits = 18;

        /// <summary>Bit position of the island field inside flags1.</summary>
        public const int IslandNumShift = 7;

        /// <summary>Largest storable island id, 0x3FFFF.</summary>
        public const int MaxIslandNum = (1 << IslandNumBits) - 1;

        /// <summary>
        /// The saturated value the only existing community importer stamps on every area instead
        /// of computed connectivity. Master plan risk R-06; worker report 08 section 8.2.
        /// </summary>
        public const int SentinelIslandNum = MaxIslandNum;

        /// <summary>Largest storable edge count, 7 bits.</summary>
        public const int MaxEdgeCount = 0x7F;

        private const uint IslandNumMask = ((1u << IslandNumBits) - 1u) << IslandNumShift;

        public NavArea()
        {
            Edges = new List<NavEdge>();
            SearchCost = uint.MaxValue;
        }

        public NavArea(IEnumerable<NavEdge> edges)
            : this()
        {
            Edges.AddRange(edges);
            EdgeCount = Edges.Count;
        }

        public List<NavEdge> Edges { get; }

        public Vec3 Position { get; set; }

        public float Radius { get; set; }

        /// <summary>Runtime scratch, preserved verbatim. Fresh areas use all ones.</summary>
        public uint SearchCost { get; set; }

        public uint UsageFlags { get; set; }

        public uint Flags1 { get; set; }

        public uint Flags2 { get; set; }

        public uint Flags3 { get; set; }

        public uint Flags4 { get; set; }

        /// <summary>Runtime scratch pointers, preserved verbatim in read order.</summary>
        public ulong RawProxyPointer { get; set; }

        public ulong RawDynamicDataPointer { get; set; }

        public ulong RawFirstLinkPointer { get; set; }

        public ulong RawSearchParentPointer { get; set; }

        /// <summary>
        /// Byte offset of this area from the start of its graph. This is the value edges and KD
        /// leaves point at, so it is an identity, not a convenience.
        /// </summary>
        public uint GraphRelativeOffset { get; set; }

        /// <summary>Edge count, low 7 bits of flags1.</summary>
        public int EdgeCount
        {
            get => (int)(Flags1 & 0x7Fu);
            set
            {
                if (value < 0 || value > MaxEdgeCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "edge count must fit in 7 bits");
                }

                Flags1 = (Flags1 & ~0x7Fu) | ((uint)value & 0x7Fu);
            }
        }

        /// <summary>
        /// Connected-component id, 18 bits at bit 7 of flags1. See IslandLabeler for how it is
        /// computed and SentinelIslandNum for the value that must never ship.
        /// </summary>
        public int IslandNum
        {
            get => (int)((Flags1 & IslandNumMask) >> IslandNumShift);
            set
            {
                if (value < 0 || value > MaxIslandNum)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "island id must fit in 18 bits");
                }

                Flags1 = (Flags1 & ~IslandNumMask) | (((uint)value << IslandNumShift) & IslandNumMask);
            }
        }

        /// <summary>Usage counter, low 10 bits of flags2.</summary>
        public int AreaUsageCount
        {
            get => (int)(Flags2 & 0x3FFu);
            set => Flags2 = (Flags2 & ~0x3FFu) | ((uint)value & 0x3FFu);
        }

        /// <summary>Obstacle cost multiplier, bits 16..19 of flags2, clamped to 1..15.</summary>
        public int ObstacleCostMultiplier
        {
            get => (int)((Flags2 & 0x000F0000u) >> 16);
            set => Flags2 = (Flags2 & ~0x000F0000u) | ((Clamp(value, 1, 15) << 16) & 0x000F0000u);
        }

        /// <summary>Static cost multiplier, bits 20..23 of flags2, clamped to 1..15.</summary>
        public int StaticCostMultiplier
        {
            get => (int)((Flags2 & 0x00F00000u) >> 20);
            set => Flags2 = (Flags2 & ~0x00F00000u) | ((Clamp(value, 1, 15) << 20) & 0x00F00000u);
        }

        /// <summary>Basis vertex index, bits 24..30 of flags2.</summary>
        public int BasisVertex
        {
            get => (int)((Flags2 & 0x7F000000u) >> 24);
            set => Flags2 = (Flags2 & ~0x7F000000u) | (((uint)value << 24) & 0x7F000000u);
        }

        public static int HeaderSizeInBytes(uint pointerSizeCode) => pointerSizeCode == 1 ? 72 : 56;

        public int SizeInBytes(uint pointerSizeCode) =>
            HeaderSizeInBytes(pointerSizeCode) + (Edges.Count * NavEdge.SizeInBytes(pointerSizeCode));

        public Bounds3 CalcBounds()
        {
            if (Edges.Count == 0)
            {
                throw new NavFormatException("area has no edges, so it has no bounds");
            }

            Vec3[] points = new Vec3[Edges.Count];
            for (int i = 0; i < Edges.Count; i++)
            {
                points[i] = Edges[i].Position;
            }

            return Bounds3.FromPoints(points);
        }

        /// <summary>
        /// Perimeter-weighted centroid of the polygon vertices. This is the position the traversal
        /// cost between two areas is measured between, so it must match what the runtime expects.
        /// </summary>
        public Vec3 CalcWeightedVertexCentre()
        {
            if (Edges.Count == 0)
            {
                throw new NavFormatException("area has no edges, so it has no centre");
            }

            double sx = 0, sy = 0, sz = 0, sLen = 0;
            int n = Edges.Count;

            for (int i = 0; i < n; i++)
            {
                Vec3 previous = Edges[((i - 1) % n + n) % n].Position;
                Vec3 current = Edges[i].Position;
                double len = Vec3.Distance(current, previous);

                sx += (previous.X + current.X) / 2.0 * len;
                sy += (previous.Y + current.Y) / 2.0 * len;
                sz += (previous.Z + current.Z) / 2.0 * len;
                sLen += len;
            }

            if (sLen <= 0.0)
            {
                // Fully degenerate polygon: fall back to the arithmetic mean so the writer still
                // produces a finite position instead of NaN.
                Vec3 sum = Vec3.Zero;
                for (int i = 0; i < n; i++)
                {
                    sum += Edges[i].Position;
                }

                return sum / n;
            }

            return new Vec3((float)(sx / sLen), (float)(sy / sLen), (float)(sz / sLen));
        }

        /// <summary>Distance from Position to the furthest vertex.</summary>
        public float CalcRadius()
        {
            float radius = 0f;
            for (int i = 0; i < Edges.Count; i++)
            {
                float d = Vec3.Distance(Position, Edges[i].Position);
                if (d > radius)
                {
                    radius = d;
                }
            }

            return radius;
        }

        /// <summary>
        /// Vertex furthest from the line through the first two vertices, clamped to index 2 or
        /// above so the stored triple is never collinear by construction.
        /// </summary>
        public int CalcBasisVertex()
        {
            if (Edges.Count < 3)
            {
                return 2;
            }

            int best = 0;
            float bestDistance = float.MinValue;
            Vec3 a = Edges[0].Position;
            Vec3 b = Edges[1].Position;

            for (int i = 0; i < Edges.Count; i++)
            {
                Vec3 p = Edges[i].Position;
                float d = Vec3.Distance(Vec3.PerpendicularFoot(p, a, b), p);
                if (d > bestDistance)
                {
                    best = i;
                    bestDistance = d;
                }
            }

            return best >= 2 ? best : 2;
        }

        /// <summary>Recomputes basis vertex, centre and radius, in that order.</summary>
        public void RecalculateDerivedGeometry()
        {
            EdgeCount = Edges.Count;
            BasisVertex = CalcBasisVertex();
            Position = CalcWeightedVertexCentre();
            Radius = CalcRadius();
        }

        internal static NavArea Read(BlobReader reader, ByteOrder order, uint pointerSizeCode, int graphStart)
        {
            NavArea area = new NavArea
            {
                GraphRelativeOffset = checked((uint)(reader.Position - graphStart)),
                RawProxyPointer = reader.ReadPointer(order, pointerSizeCode, "area.proxy"),
                RawDynamicDataPointer = reader.ReadPointer(order, pointerSizeCode, "area.dynamicData"),
                RawFirstLinkPointer = reader.ReadPointer(order, pointerSizeCode, "area.firstLink"),
                RawSearchParentPointer = reader.ReadPointer(order, pointerSizeCode, "area.searchParent"),
                Position = reader.ReadVec3(order, "area.position"),
                Radius = reader.ReadSingle(order, "area.radius"),
                SearchCost = reader.ReadUInt32(order, "area.searchCost"),
                UsageFlags = reader.ReadUInt32(order, "area.usageFlags"),
                Flags1 = reader.ReadUInt32(order, "area.flags1"),
                Flags2 = reader.ReadUInt32(order, "area.flags2"),
                Flags3 = reader.ReadUInt32(order, "area.flags3"),
                Flags4 = reader.ReadUInt32(order, "area.flags4"),
            };

            int count = area.EdgeCount;
            for (int i = 0; i < count; i++)
            {
                area.Edges.Add(NavEdge.Read(reader, order, pointerSizeCode));
            }

            return area;
        }

        internal void Write(BlobWriter writer, ByteOrder order, uint pointerSizeCode, int graphStart)
        {
            if (Edges.Count != EdgeCount)
            {
                throw new NavFormatException(
                    "area edge list holds " + Edges.Count + " edges but the packed edge count is " +
                    EdgeCount + "; set EdgeCount before writing");
            }

            GraphRelativeOffset = checked((uint)(writer.Position - graphStart));

            writer.WritePointer(RawProxyPointer, order, pointerSizeCode);
            writer.WritePointer(RawDynamicDataPointer, order, pointerSizeCode);
            writer.WritePointer(RawFirstLinkPointer, order, pointerSizeCode);
            writer.WritePointer(RawSearchParentPointer, order, pointerSizeCode);
            writer.WriteVec3(Position, order);
            writer.WriteSingle(Radius, order);
            writer.WriteUInt32(SearchCost, order);
            writer.WriteUInt32(UsageFlags, order);
            writer.WriteUInt32(Flags1, order);
            writer.WriteUInt32(Flags2, order);
            writer.WriteUInt32(Flags3, order);
            writer.WriteUInt32(Flags4, order);

            for (int i = 0; i < Edges.Count; i++)
            {
                Edges[i].Write(writer, order, pointerSizeCode);
            }
        }

        private static uint Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return (uint)min;
            }

            return value > max ? (uint)max : (uint)value;
        }
    }
}
