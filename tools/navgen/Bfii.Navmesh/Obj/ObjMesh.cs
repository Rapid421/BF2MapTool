// VENDORED from BfiiMapTools src\Bfii.Navmesh\Obj\ObjMesh.cs (sha256:4D9621253EEA366E).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Obj
{
    /// <summary>One polygon of a parsed Wavefront OBJ, after vertex welding.</summary>
    public sealed class ObjFace
    {
        public ObjFace(int sourceLine, IReadOnlyList<int> vertexIndices)
        {
            SourceLine = sourceLine;
            VertexIndices = vertexIndices;
        }

        /// <summary>1-based line number in the source file, for error reporting.</summary>
        public int SourceLine { get; }

        /// <summary>Indices into <see cref="ObjMesh.Vertices"/>, in the order written.</summary>
        public IReadOnlyList<int> VertexIndices { get; }

        public int VertexCount => VertexIndices.Count;
    }

    /// <summary>
    /// A Wavefront OBJ reduced to what a navigation graph needs: welded vertices and polygons.
    /// </summary>
    /// <remarks>
    /// Normals, texture coordinates, groups, materials and smoothing are parsed only far enough to
    /// be skipped. A navigation surface has no shading.
    /// </remarks>
    public sealed class ObjMesh
    {
        public ObjMesh(
            IReadOnlyList<Vec3> vertices,
            IReadOnlyList<ObjFace> faces,
            int rawVertexCount,
            int weldedVertexCount,
            IReadOnlyList<string> warnings)
        {
            Vertices = vertices;
            Faces = faces;
            RawVertexCount = rawVertexCount;
            WeldedVertexCount = weldedVertexCount;
            Warnings = warnings;
        }

        /// <summary>Unique vertex positions after welding, in first-seen order.</summary>
        public IReadOnlyList<Vec3> Vertices { get; }

        public IReadOnlyList<ObjFace> Faces { get; }

        /// <summary>Vertices as written in the file, before welding.</summary>
        public int RawVertexCount { get; }

        /// <summary>Vertices that were merged into an earlier one.</summary>
        public int WeldedVertexCount { get; }

        public IReadOnlyList<string> Warnings { get; }

        public Bounds3 CalcBounds()
        {
            if (Vertices.Count == 0)
            {
                throw new ObjParseException("mesh has no vertices");
            }

            return Bounds3.FromPoints(Vertices);
        }
    }

    /// <summary>Raised when an OBJ cannot be turned into a navigation surface.</summary>
    public sealed class ObjParseException : Exception
    {
        public ObjParseException(string message)
            : base(message)
        {
        }

        public ObjParseException(string message, IReadOnlyList<string> details)
            : base(message)
        {
            Details = details;
        }

        /// <summary>Per-problem lines, each already carrying its source line numbers.</summary>
        public IReadOnlyList<string> Details { get; } = Array.Empty<string>();
    }
}
