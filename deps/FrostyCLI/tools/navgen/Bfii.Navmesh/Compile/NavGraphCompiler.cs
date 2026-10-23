// VENDORED from BfiiMapTools src\Bfii.Navmesh\Compile\NavGraphCompiler.cs (sha256:4BFCDF813956F05A).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Io;
using Bfii.Navmesh.Islands;
using Bfii.Navmesh.Obj;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Compile
{
    /// <summary>Settings for compiling a walkable surface into a navigation graph.</summary>
    public sealed class NavCompileOptions
    {
        /// <summary>Which graph in the donor set to replace. Graph 0 unless told otherwise.</summary>
        public int GraphIndex { get; set; }

        /// <summary>Island numbering settings. See IslandLabeler for why the default starts at 1.</summary>
        public IslandLabelOptions Islands { get; set; } = IslandLabelOptions.Default;

        /// <summary>
        /// Stamp the saturated sentinel on every area instead of computing connectivity. Present
        /// only so the pre-fix behaviour can be reproduced side by side in a test or an
        /// experiment; it is never the right choice for content.
        /// </summary>
        public bool UseLegacyIslandSentinel { get; set; }

        /// <summary>Obstacle cost multiplier stamped on every new area, clamped to 1..15.</summary>
        public int ObstacleCostMultiplier { get; set; } = 1;

        /// <summary>Static cost multiplier stamped on every new area, clamped to 1..15.</summary>
        public int StaticCostMultiplier { get; set; } = 1;

        /// <summary>Recompute the graph bounds from the compiled geometry.</summary>
        public bool RecalculateGraphBounds { get; set; } = true;

        public static NavCompileOptions Default => new NavCompileOptions();
    }

    /// <summary>What a compile produced, for logging and for tests.</summary>
    public sealed class NavCompileResult
    {
        public NavCompileResult(
            int areaCount,
            int edgeCount,
            int sharedEdgeCount,
            int boundaryEdgeCount,
            IslandLabelResult islands,
            IReadOnlyList<string> warnings)
        {
            AreaCount = areaCount;
            EdgeCount = edgeCount;
            SharedEdgeCount = sharedEdgeCount;
            BoundaryEdgeCount = boundaryEdgeCount;
            Islands = islands;
            Warnings = warnings;
        }

        public int AreaCount { get; }

        /// <summary>Distinct undirected segments in the surface.</summary>
        public int EdgeCount { get; }

        /// <summary>Segments shared by exactly two faces, so exactly two areas are adjacent.</summary>
        public int SharedEdgeCount { get; }

        /// <summary>Segments belonging to one face only: the outer boundary and any hole rim.</summary>
        public int BoundaryEdgeCount { get; }

        public IslandLabelResult Islands { get; }

        public IReadOnlyList<string> Warnings { get; }

        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture,
            "{0} area(s), {1} edge(s) ({2} shared, {3} boundary); {4}",
            AreaCount,
            EdgeCount,
            SharedEdgeCount,
            BoundaryEdgeCount,
            Islands);
    }

    /// <summary>
    /// Compiles a manifold triangle or polygon surface into one navigation graph, in place, on a
    /// donor image.
    /// </summary>
    /// <remarks>
    /// The compile is deliberately donor seeded and that is a feature, not a limitation. Every
    /// header value that decides how the runtime interprets the graph — image version, byte order,
    /// pointer width, build scale, voxel size, agent radius, step height, agent height, build up
    /// axis and the opaque 252-byte tail — is inherited from the donor and never synthesised. That
    /// is what keeps the output at the image version BFII reads, and it is why no version
    /// negotiation is ever attempted. Worker report 08 section 4.5 calls this out as the reason the
    /// route works at all.
    ///
    /// Replaced wholesale: the area list, every edge, all adjacency, all traversal costs, the KD
    /// tree, the island numbering and, by default, the graph bounds.
    ///
    /// Manifold input only. An edge shared by three or more faces is a hard error naming every
    /// offending source line, because such a mesh has no well-defined adjacency and silently
    /// picking two of the three faces would produce a graph that looks correct and paths wrongly.
    /// </remarks>
    public static class NavGraphCompiler
    {
        public static NavCompileResult Compile(NavMeshImage image, ObjMesh mesh, NavCompileOptions? options = null)
        {
            if (image == null)
            {
                throw new ArgumentNullException(nameof(image));
            }

            options = options ?? NavCompileOptions.Default;

            if (options.GraphIndex < 0 || options.GraphIndex >= image.NavSet.Graphs.Count)
            {
                throw new NavCompileException(
                    "graph index " + options.GraphIndex + " is out of range: the donor image holds " +
                    image.NavSet.Graphs.Count + " graph(s)");
            }

            return Compile(image.NavSet.Graphs[options.GraphIndex], mesh, options);
        }

        public static NavCompileResult Compile(NavGraph graph, ObjMesh mesh, NavCompileOptions? options = null)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            options = options ?? NavCompileOptions.Default;

            List<string> warnings = new List<string>(mesh.Warnings);

            // Pass 1: index every undirected segment and record which faces use it.
            Dictionary<long, SegmentUse> segments = new Dictionary<long, SegmentUse>(mesh.Faces.Count * 2);
            List<SegmentUse> overUsed = new List<SegmentUse>();

            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                ObjFace face = mesh.Faces[f];
                int n = face.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    int a = face.VertexIndices[i];
                    int b = face.VertexIndices[(i + 1) % n];
                    long key = SegmentKey(a, b);

                    SegmentUse? use;
                    if (!segments.TryGetValue(key, out use))
                    {
                        use = new SegmentUse(a, b);
                        segments.Add(key, use);
                    }

                    use.FaceIndices.Add(f);
                    use.SourceLines.Add(face.SourceLine);

                    if (use.FaceIndices.Count == 3)
                    {
                        overUsed.Add(use);
                    }
                }
            }

            if (overUsed.Count > 0)
            {
                List<string> details = new List<string>(overUsed.Count);
                for (int i = 0; i < overUsed.Count; i++)
                {
                    SegmentUse use = overUsed[i];
                    details.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "NON-MANIFOLD EDGE {0} -> {1} shared by {2} faces at .obj line(s) {3}",
                        Describe(mesh, use.VertexA),
                        Describe(mesh, use.VertexB),
                        use.FaceIndices.Count,
                        string.Join(", ", use.SourceLines.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture)).ToArray())));
                }

                throw new NavCompileException(
                    overUsed.Count + " edge(s) are shared by three or more faces, so the surface is " +
                    "not manifold and has no well-defined adjacency. Import cancelled; nothing was " +
                    "written.",
                    details);
            }

            // Pass 2: one area per face, one edge per corner. Edge i owns the segment that starts
            // at vertex i and ends at vertex i+1, wrapping, which is what makes adjacency per
            // segment rather than per vertex.
            List<NavArea> areas = new List<NavArea>(mesh.Faces.Count);
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                ObjFace face = mesh.Faces[f];
                NavArea area = new NavArea();

                for (int i = 0; i < face.VertexCount; i++)
                {
                    area.Edges.Add(new NavEdge(mesh.Vertices[face.VertexIndices[i]]));
                }

                area.EdgeCount = area.Edges.Count;
                area.AreaUsageCount = 0;
                area.ObstacleCostMultiplier = options.ObstacleCostMultiplier;
                area.StaticCostMultiplier = options.StaticCostMultiplier;
                area.UsageFlags = 0;
                area.Flags3 = 0;
                area.Flags4 = 0;
                area.SearchCost = uint.MaxValue;
                area.RawProxyPointer = 0;
                area.RawDynamicDataPointer = 0;
                area.RawFirstLinkPointer = 0;
                area.RawSearchParentPointer = 0;
                area.RecalculateDerivedGeometry();

                areas.Add(area);
            }

            // Pass 3: wire adjacency for every segment used by exactly two faces, then stamp the
            // traversal cost from the finished area centres. Costs must come after every centre is
            // final, which is why this is a separate pass.
            int sharedEdges = 0;
            int boundaryEdges = 0;

            foreach (KeyValuePair<long, SegmentUse> entry in segments)
            {
                SegmentUse use = entry.Value;
                if (use.FaceIndices.Count == 1)
                {
                    boundaryEdges++;
                    continue;
                }

                sharedEdges++;

                int faceA = use.FaceIndices[0];
                int faceB = use.FaceIndices[1];
                NavArea areaA = areas[faceA];
                NavArea areaB = areas[faceB];

                int edgeA = FindEdgeIndex(mesh.Faces[faceA], use.VertexA, use.VertexB);
                int edgeB = FindEdgeIndex(mesh.Faces[faceB], use.VertexA, use.VertexB);

                if (edgeA < 0 || edgeB < 0)
                {
                    throw new NavCompileException(
                        "internal consistency failure: segment " + use.VertexA + "-" + use.VertexB +
                        " was indexed against faces at .obj lines " + mesh.Faces[faceA].SourceLine +
                        " and " + mesh.Faces[faceB].SourceLine + " but could not be located in both");
                }

                areaA.Edges[edgeA].AdjacentArea = areaB;
                areaB.Edges[edgeB].AdjacentArea = areaA;
            }

            graph.Areas.Clear();
            graph.Areas.AddRange(areas);
            graph.RecalculateTraversalCosts();

            if (options.RecalculateGraphBounds)
            {
                graph.RecalculateBounds();
            }

            // Pass 4: islands. This is the step the community importer does not perform.
            IslandLabelResult islands;
            if (options.UseLegacyIslandSentinel)
            {
                for (int i = 0; i < areas.Count; i++)
                {
                    areas[i].IslandNum = NavArea.SentinelIslandNum;
                }

                islands = new IslandLabelResult(0, NavArea.SentinelIslandNum, Array.Empty<int>(), 0);
                warnings.Add(
                    "LEGACY MODE: every area was stamped with the saturated island sentinel " +
                    NavArea.SentinelIslandNum + " instead of computed connectivity. This reproduces " +
                    "the known-defective community behaviour and must not be used for content.");
            }
            else
            {
                islands = IslandLabeler.Label(graph, options.Islands);
                if (islands.IslandCount > 1)
                {
                    warnings.Add(
                        "the surface has " + islands.IslandCount + " disconnected component(s); a bot " +
                        "cannot walk between them without an authored off-mesh link");
                }
            }

            // Pass 5: the spatial index, last, because it stores area offsets and area layout is
            // only final once every area exists.
            graph.KdTree.Rebuild(graph.Areas);

            return new NavCompileResult(
                areas.Count, segments.Count, sharedEdges, boundaryEdges, islands, warnings);
        }

        private static int FindEdgeIndex(ObjFace face, int vertexA, int vertexB)
        {
            int n = face.VertexCount;
            for (int i = 0; i < n; i++)
            {
                int a = face.VertexIndices[i];
                int b = face.VertexIndices[(i + 1) % n];
                if ((a == vertexA && b == vertexB) || (a == vertexB && b == vertexA))
                {
                    return i;
                }
            }

            return -1;
        }

        private static long SegmentKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        private static string Describe(ObjMesh mesh, int vertexIndex) =>
            mesh.Vertices[vertexIndex].ToString();

        private sealed class SegmentUse
        {
            public SegmentUse(int vertexA, int vertexB)
            {
                VertexA = vertexA;
                VertexB = vertexB;
                FaceIndices = new List<int>(2);
                SourceLines = new List<int>(2);
            }

            public int VertexA { get; }

            public int VertexB { get; }

            public List<int> FaceIndices { get; }

            public List<int> SourceLines { get; }
        }
    }

    /// <summary>Raised when a surface cannot be compiled into a navigation graph.</summary>
    public sealed class NavCompileException : Exception
    {
        public NavCompileException(string message)
            : base(message)
        {
        }

        public NavCompileException(string message, IReadOnlyList<string> details)
            : base(message)
        {
            Details = details;
        }

        /// <summary>Per-problem lines, each naming the offending source line numbers.</summary>
        public IReadOnlyList<string> Details { get; } = Array.Empty<string>();
    }
}
