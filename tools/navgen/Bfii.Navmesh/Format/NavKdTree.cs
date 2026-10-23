// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavKdTree.cs (sha256:8704D69CF58D2896).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Io;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Format
{
    /// <summary>
    /// The per-graph spatial index: a bounding box, a node-region byte count, then the node tree.
    /// </summary>
    /// <remarks>
    /// Layout (COMMUNITY_TESTED, FORMAT-PROVENANCE.md row K2):
    ///   Bounds3  24 bytes
    ///   uint32   size of the node region that follows
    ///   nodes    see NavKdNode
    ///
    /// The whole region is captured verbatim as SourceImage on read. When the tree has not been
    /// rebuilt and the areas it indexes have not moved, that slice is re-emitted unchanged, which
    /// is what makes round trip on a real donor byte exact by construction rather than by luck.
    /// </remarks>
    public sealed class NavKdTree
    {
        public NavKdTree()
        {
            Diagnostics = new NavKdTreeDiagnostics();
        }

        public Bounds3 Bounds { get; set; }

        public NavKdNode? Root { get; set; }

        /// <summary>The exact bytes this tree was read from, or null when it was built in memory.</summary>
        public byte[]? SourceImage { get; set; }

        /// <summary>Set when the tree has been rebuilt and SourceImage no longer describes it.</summary>
        public bool IsDirty { get; set; }

        public NavKdTreeDiagnostics Diagnostics { get; }

        public int NodeCount => Root == null ? 0 : Root.CountNodes();

        public int Depth => Root == null ? 0 : Root.Depth();

        /// <summary>
        /// Rebuilds the tree over <paramref name="areas"/> by recursive median split on the axis
        /// with the largest spread of area-bounds centres.
        /// </summary>
        /// <remarks>
        /// Split rule: sort by bounds centre on the chosen axis, cut at floor(count / 2), recurse.
        /// The left plane is the maximum of the left subtree bounds on that axis and the right
        /// plane is the minimum of the right subtree bounds, so the two planes bracket the gap
        /// between the halves rather than sharing one value.
        /// </remarks>
        public void Rebuild(IReadOnlyList<NavArea> areas)
        {
            if (areas == null)
            {
                throw new ArgumentNullException(nameof(areas));
            }

            if (areas.Count == 0)
            {
                throw new NavFormatException("cannot build a KD tree over zero areas");
            }

            Root = Build(new List<NavArea>(areas));
            Bounds = Root.CalcBounds();
            IsDirty = true;
            SourceImage = null;
        }

        private static NavKdNode Build(List<NavArea> areas)
        {
            if (areas.Count == 1)
            {
                return NavKdNode.Leaf(areas[0]);
            }

            Bounds3[] bounds = new Bounds3[areas.Count];
            for (int i = 0; i < areas.Count; i++)
            {
                bounds[i] = areas[i].CalcBounds();
            }

            Axis axis = WidestSpreadAxis(bounds);

            // Stable ordering: ties on the centre coordinate keep their previous relative order, so
            // the tree is a deterministic function of the area list.
            List<NavArea> sorted = StableSortByCentre(areas, bounds, axis);

            int split = sorted.Count / 2;
            List<NavArea> leftAreas = sorted.GetRange(0, split);
            List<NavArea> rightAreas = sorted.GetRange(split, sorted.Count - split);

            NavKdNode left = Build(leftAreas);
            NavKdNode right = Build(rightAreas);

            float leftPlane = left.CalcBounds().Max[axis];
            float rightPlane = right.CalcBounds().Min[axis];

            return NavKdNode.Internal(axis, left, leftPlane, right, rightPlane);
        }

        private static Axis WidestSpreadAxis(Bounds3[] bounds)
        {
            Axis best = Axis.X;
            double bestSpread = -1.0;

            for (int a = 0; a < 3; a++)
            {
                double mean = 0.0;
                for (int i = 0; i < bounds.Length; i++)
                {
                    mean += bounds[i].Center[a];
                }

                mean /= bounds.Length;

                double sumSquares = 0.0;
                for (int i = 0; i < bounds.Length; i++)
                {
                    double d = bounds[i].Center[a] - mean;
                    sumSquares += d * d;
                }

                double spread = Math.Sqrt(sumSquares / bounds.Length);
                if (spread > bestSpread)
                {
                    bestSpread = spread;
                    best = (Axis)a;
                }
            }

            return best;
        }

        private static List<NavArea> StableSortByCentre(List<NavArea> areas, Bounds3[] bounds, Axis axis)
        {
            int n = areas.Count;
            int[] order = new int[n];
            float[] keys = new float[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                keys[i] = bounds[i].Center[axis];
            }

            // Insertion sort: stable, and n is the size of one KD split, not the whole graph.
            for (int i = 1; i < n; i++)
            {
                int idx = order[i];
                float key = keys[idx];
                int j = i - 1;
                while (j >= 0 && keys[order[j]] > key)
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = idx;
            }

            List<NavArea> result = new List<NavArea>(n);
            for (int i = 0; i < n; i++)
            {
                result.Add(areas[order[i]]);
            }

            return result;
        }

        internal static NavKdTree Read(
            BlobReader reader,
            ByteOrder order,
            IReadOnlyDictionary<uint, NavArea> areasByOffset,
            int declaredBytes)
        {
            int start = reader.Position;
            NavKdTree tree = new NavKdTree
            {
                Bounds = reader.ReadBounds3(order, "kdTree.bounds"),
            };

            uint nodeBytes = reader.ReadUInt32(order, "kdTree.nodeBytes");
            int nodesStart = reader.Position;
            reader.Require((int)nodeBytes, "kdTree.nodes");

            tree.Root = NavKdNode.Read(reader, order, areasByOffset, tree.Diagnostics);

            int nodesEnd = nodesStart + (int)nodeBytes;
            if (reader.Position > nodesEnd)
            {
                throw new NavFormatException(
                    "KD node region overran its declared size: consumed " + (reader.Position - nodesStart) +
                    " of " + nodeBytes + " bytes at offset " + nodesStart);
            }

            // The right-child jump means the cursor can legitimately finish before the declared
            // end when the last node written is not the last node in address order. Snap to the
            // declared end so the graph reader stays aligned.
            reader.Position = nodesEnd;

            int consumed = reader.Position - start;
            if (declaredBytes >= 0 && consumed != declaredBytes)
            {
                throw new NavFormatException(
                    "KD tree consumed " + consumed + " bytes but the graph header declared " +
                    declaredBytes + " at offset " + start);
            }

            tree.SourceImage = new byte[consumed];
            Array.Copy(reader.Buffer, start, tree.SourceImage, 0, consumed);
            return tree;
        }

        internal void Write(BlobWriter writer, ByteOrder order, bool areaLayoutUnchanged)
        {
            if (!IsDirty && areaLayoutUnchanged && SourceImage != null)
            {
                writer.WriteBytes(SourceImage);
                return;
            }

            if (Root == null)
            {
                throw new NavFormatException(
                    "KD tree must be rebuilt before writing: it has no root and no reusable source image");
            }

            writer.WriteBounds3(Bounds, order);
            int sizeOffset = writer.Position;
            writer.WriteUInt32(0, order);

            int nodesStart = writer.Position;
            Root.Write(writer, order);
            int nodeBytes = writer.Position - nodesStart;

            writer.PatchUInt32(sizeOffset, (uint)nodeBytes, order);
        }
    }
}
