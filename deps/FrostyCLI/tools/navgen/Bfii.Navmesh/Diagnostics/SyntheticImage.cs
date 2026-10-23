// VENDORED from BfiiMapTools src\Bfii.Navmesh\Diagnostics\SyntheticImage.cs (sha256:C27C4556B6F99C8D).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Io;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Diagnostics
{
    /// <summary>
    /// Builds navigation images in memory, for tests and for exercising the tool without a donor.
    /// </summary>
    /// <remarks>
    /// A SYNTHETIC IMAGE IS NOT A VALID BFII DONOR AND MUST NEVER BE SHIPPED AS CONTENT.
    ///
    /// Its header values — build scale, voxel size, agent radius, step height, agent height, the
    /// opaque 252-byte tail — are invented, not observed. They exist so the reader and writer can
    /// be tested against each other with no game data present. That is an L0 result in the master
    /// plan sense: it proves this library agrees with itself, and it proves nothing at all about
    /// what BFII accepts.
    ///
    /// Real work uses a shipped blob as the donor, which is what carries the correct header and
    /// therefore the correct image version.
    /// </remarks>
    public static class SyntheticImage
    {
        /// <summary>
        /// Builds an image whose single graph covers a regular grid of quads on the XZ plane.
        /// </summary>
        public static NavMeshImage Grid(
            int columns,
            int rows,
            float cellSize = 1f,
            ByteOrder order = ByteOrder.Little,
            uint pointerSizeCode = 1,
            uint version = NavGraph.BfiiImageVersion)
        {
            NavMeshImage image = NewImage(order, pointerSizeCode);
            image.NavSet.Version = version;
            image.NavSet.Graphs.Add(GridGraph(columns, rows, cellSize, 0f, version));
            return image;
        }

        /// <summary>Builds an image with several graphs, to exercise the multi-graph path.</summary>
        public static NavMeshImage MultiGraph(
            int graphCount,
            int columns,
            int rows,
            ByteOrder order = ByteOrder.Little,
            uint pointerSizeCode = 1,
            uint version = NavGraph.BfiiImageVersion)
        {
            NavMeshImage image = NewImage(order, pointerSizeCode);
            image.NavSet.Version = version;

            for (int i = 0; i < graphCount; i++)
            {
                NavGraph graph = GridGraph(columns, rows, 1f, i * 100f, version);
                graph.Layer = (uint)i;
                image.NavSet.Graphs.Add(graph);
            }

            return image;
        }

        /// <summary>
        /// Builds an image whose graph is two disconnected grids, for island tests.
        /// </summary>
        public static NavMeshImage TwoIslands(
            int columns,
            int rows,
            float gap = 10f,
            ByteOrder order = ByteOrder.Little,
            uint pointerSizeCode = 1)
        {
            NavMeshImage image = NewImage(order, pointerSizeCode);
            NavGraph graph = NewGraph(NavGraph.BfiiImageVersion);

            List<NavArea> areas = new List<NavArea>();
            AppendGrid(areas, columns, rows, 1f, 0f);
            AppendGrid(areas, columns, rows, 1f, (columns * 1f) + gap);

            graph.Areas.AddRange(areas);
            LinkGridNeighbours(graph);
            graph.RecalculateTraversalCosts();
            graph.RecalculateBounds();
            graph.KdTree.Rebuild(graph.Areas);

            image.NavSet.Graphs.Add(graph);
            return image;
        }

        private static NavMeshImage NewImage(ByteOrder order, uint pointerSizeCode)
        {
            NavMeshImage image = new NavMeshImage
            {
                Order = order,
                PointerSizeCode = pointerSizeCode,
                RuntimeFlags = 0,
                ConstantFlags = 0,
            };

            image.NavSet.DeclaredOrder = order;
            return image;
        }

        private static NavGraph NewGraph(uint version)
        {
            NavGraph graph = new NavGraph
            {
                Version = version,
                Layer = 0,
                BuildScale = 1.8f,
                VoxelSize = 0.2f,
                AgentRadius = 0.4f,
                StepHeight = 0.35f,
                AgentHeight = 1.8f,
                BuildUpAxis = Axis.Y,
                SerializedLinkDatumSize = 0x38,
            };

            // A recognisable, non-zero tail so a test that loses the pad fails loudly instead of
            // passing because both sides happened to be zero.
            for (int i = 0; i < graph.RawPad.Length; i++)
            {
                graph.RawPad[i] = (byte)((i * 7) + 3);
            }

            return graph;
        }

        private static NavGraph GridGraph(int columns, int rows, float cellSize, float originX, uint version)
        {
            NavGraph graph = NewGraph(version);
            AppendGrid(graph.Areas, columns, rows, cellSize, originX);
            LinkGridNeighbours(graph);
            graph.RecalculateTraversalCosts();
            graph.RecalculateBounds();
            graph.KdTree.Rebuild(graph.Areas);
            return graph;
        }

        private static void AppendGrid(List<NavArea> areas, int columns, int rows, float cellSize, float originX)
        {
            if (columns < 1 || rows < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(columns), "grid must have at least one cell");
            }

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    float x0 = originX + (c * cellSize);
                    float x1 = x0 + cellSize;
                    float z0 = r * cellSize;
                    float z1 = z0 + cellSize;

                    NavArea area = new NavArea();
                    area.Edges.Add(new NavEdge(new Vec3(x0, 0f, z0)));
                    area.Edges.Add(new NavEdge(new Vec3(x0, 0f, z1)));
                    area.Edges.Add(new NavEdge(new Vec3(x1, 0f, z1)));
                    area.Edges.Add(new NavEdge(new Vec3(x1, 0f, z0)));
                    area.EdgeCount = 4;
                    area.ObstacleCostMultiplier = 1;
                    area.StaticCostMultiplier = 1;
                    area.RecalculateDerivedGeometry();
                    areas.Add(area);
                }
            }
        }

        /// <summary>
        /// Wires adjacency by matching shared segments, the same rule the compiler uses. Written
        /// independently of the compiler so a synthetic fixture does not assume the code under
        /// test is correct.
        /// </summary>
        private static void LinkGridNeighbours(NavGraph graph)
        {
            Dictionary<string, List<KeyValuePair<NavArea, int>>> bySegment =
                new Dictionary<string, List<KeyValuePair<NavArea, int>>>();

            for (int i = 0; i < graph.Areas.Count; i++)
            {
                NavArea area = graph.Areas[i];
                int n = area.Edges.Count;
                for (int j = 0; j < n; j++)
                {
                    Vec3 a = area.Edges[j].Position;
                    Vec3 b = area.Edges[(j + 1) % n].Position;
                    string key = SegmentKey(a, b);

                    List<KeyValuePair<NavArea, int>>? bucket;
                    if (!bySegment.TryGetValue(key, out bucket))
                    {
                        bucket = new List<KeyValuePair<NavArea, int>>();
                        bySegment.Add(key, bucket);
                    }

                    bucket.Add(new KeyValuePair<NavArea, int>(area, j));
                }
            }

            foreach (KeyValuePair<string, List<KeyValuePair<NavArea, int>>> entry in bySegment)
            {
                if (entry.Value.Count != 2)
                {
                    continue;
                }

                KeyValuePair<NavArea, int> first = entry.Value[0];
                KeyValuePair<NavArea, int> second = entry.Value[1];
                first.Key.Edges[first.Value].AdjacentArea = second.Key;
                second.Key.Edges[second.Value].AdjacentArea = first.Key;
            }
        }

        private static string SegmentKey(Vec3 a, Vec3 b)
        {
            string ka = Format(a);
            string kb = Format(b);
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
        }

        private static string Format(Vec3 v) => string.Format(
            System.Globalization.CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", v.X, v.Y, v.Z);
    }
}
