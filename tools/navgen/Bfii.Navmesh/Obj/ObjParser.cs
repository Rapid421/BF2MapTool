// VENDORED from BfiiMapTools src\Bfii.Navmesh\Obj\ObjParser.cs (sha256:F8A4DC7725414408).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Bfii.Navmesh.Primitives;

namespace Bfii.Navmesh.Obj
{
    /// <summary>Settings for OBJ parsing.</summary>
    public sealed class ObjParseOptions
    {
        /// <summary>
        /// Vertices closer than this in every axis are welded into one. 1e-4 matches the tolerance
        /// the community importer uses, which matters because a mesh authored against that
        /// tolerance must weld the same way here or shared edges stop being shared.
        /// </summary>
        public float WeldTolerance { get; set; } = 1e-4f;

        /// <summary>
        /// Largest polygon accepted. The area edge count is a 7-bit field, so 127 is a format
        /// limit, not a policy.
        /// </summary>
        public int MaxFaceVertices { get; set; } = 127;

        public static ObjParseOptions Default => new ObjParseOptions();
    }

    /// <summary>
    /// Reads the subset of Wavefront OBJ that describes a walkable surface.
    /// </summary>
    /// <remarks>
    /// Welding is grid based rather than pairwise: each position is quantised to a cell of the
    /// tolerance and looked up along with its 26 neighbours. That makes welding O(n) instead of
    /// O(n squared), which matters because shipped blobs reach several megabytes and an authored
    /// replacement can carry tens of thousands of vertices.
    /// </remarks>
    public static class ObjParser
    {
        public static ObjMesh ParseFile(string path, ObjParseOptions? options = null)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            using (StreamReader reader = new StreamReader(path))
            {
                return Parse(reader, options);
            }
        }

        public static ObjMesh ParseText(string text, ObjParseOptions? options = null)
        {
            using (StringReader reader = new StringReader(text ?? string.Empty))
            {
                return Parse(reader, options);
            }
        }

        public static ObjMesh Parse(TextReader reader, ObjParseOptions? options = null)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            options = options ?? ObjParseOptions.Default;

            List<Vec3> unique = new List<Vec3>();
            List<int> rawToUnique = new List<int>();
            List<ObjFace> faces = new List<ObjFace>();
            List<string> warnings = new List<string>();

            VertexWelder welder = new VertexWelder(options.WeldTolerance);
            int welded = 0;
            int lineNumber = 0;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;

                int comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line.Substring(0, comment);
                }

                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (StartsWithToken(trimmed, "v"))
                {
                    string[] parts = SplitTokens(trimmed);
                    if (parts.Length < 4)
                    {
                        throw new ObjParseException(
                            "line " + lineNumber + ": vertex needs three coordinates, found " + (parts.Length - 1));
                    }

                    Vec3 position = new Vec3(
                        ParseFloat(parts[1], lineNumber),
                        ParseFloat(parts[2], lineNumber),
                        ParseFloat(parts[3], lineNumber));

                    int uniqueBefore = unique.Count;
                    int index = welder.Add(position, unique);
                    if (unique.Count == uniqueBefore)
                    {
                        // Add matched an existing vertex instead of appending a new one.
                        welded++;
                    }

                    rawToUnique.Add(index);
                }
                else if (StartsWithToken(trimmed, "f"))
                {
                    string[] parts = SplitTokens(trimmed);
                    if (parts.Length < 4)
                    {
                        throw new ObjParseException(
                            "line " + lineNumber + ": face needs at least three vertices, found " + (parts.Length - 1));
                    }

                    List<int> indices = new List<int>(parts.Length - 1);
                    for (int i = 1; i < parts.Length; i++)
                    {
                        int raw = ParseFaceVertexIndex(parts[i], rawToUnique.Count, lineNumber);
                        int unifiedIndex = rawToUnique[raw];

                        // Consecutive duplicates after welding are a zero-length segment. Dropping
                        // them keeps the polygon well formed; the count is reported.
                        if (indices.Count > 0 && indices[indices.Count - 1] == unifiedIndex)
                        {
                            continue;
                        }

                        if (indices.Contains(unifiedIndex))
                        {
                            continue;
                        }

                        indices.Add(unifiedIndex);
                    }

                    // A closing repeat of the first vertex is also a zero-length segment.
                    while (indices.Count > 1 && indices[0] == indices[indices.Count - 1])
                    {
                        indices.RemoveAt(indices.Count - 1);
                    }

                    if (indices.Count < 3)
                    {
                        warnings.Add(
                            "line " + lineNumber + ": face collapsed to " + indices.Count +
                            " distinct vertices after welding and was dropped");
                        continue;
                    }

                    if (indices.Count > options.MaxFaceVertices)
                    {
                        throw new ObjParseException(
                            "line " + lineNumber + ": face has " + indices.Count + " vertices but the " +
                            "area edge count field holds at most " + options.MaxFaceVertices +
                            "; triangulate or subdivide this polygon");
                    }

                    faces.Add(new ObjFace(lineNumber, indices));
                }

                // Every other statement (vn, vt, g, o, s, usemtl, mtllib) is irrelevant to a
                // navigation surface and is skipped without comment.
            }

            if (welded > 0)
            {
                warnings.Add(
                    welded + " vertex position(s) were welded into an earlier vertex at tolerance " +
                    options.WeldTolerance.ToString("R", CultureInfo.InvariantCulture));
            }

            if (faces.Count == 0)
            {
                throw new ObjParseException("no usable faces found in the OBJ");
            }

            return new ObjMesh(unique, faces, rawToUnique.Count, welded, warnings);
        }

        private static bool StartsWithToken(string line, string token)
        {
            if (!line.StartsWith(token, StringComparison.Ordinal))
            {
                return false;
            }

            if (line.Length == token.Length)
            {
                return true;
            }

            char next = line[token.Length];
            return next == ' ' || next == '\t';
        }

        private static string[] SplitTokens(string line) =>
            line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        private static float ParseFloat(string token, int lineNumber)
        {
            float value;
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new ObjParseException("line " + lineNumber + ": cannot parse number '" + token + "'");
            }

            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ObjParseException("line " + lineNumber + ": coordinate is not finite");
            }

            return value;
        }

        /// <summary>
        /// Resolves one face token to a 0-based raw vertex index. Handles v, v/vt, v//vn and
        /// v/vt/vn, and the negative relative form the OBJ specification allows.
        /// </summary>
        private static int ParseFaceVertexIndex(string token, int vertexCount, int lineNumber)
        {
            int slash = token.IndexOf('/');
            string first = slash >= 0 ? token.Substring(0, slash) : token;

            int index;
            if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                throw new ObjParseException(
                    "line " + lineNumber + ": cannot parse face vertex index '" + token + "'");
            }

            if (index > 0)
            {
                index -= 1;
            }
            else if (index < 0)
            {
                index = vertexCount + index;
            }
            else
            {
                throw new ObjParseException("line " + lineNumber + ": face vertex index 0 is not valid in OBJ");
            }

            if (index < 0 || index >= vertexCount)
            {
                throw new ObjParseException(
                    "line " + lineNumber + ": face references vertex " + token + " but only " +
                    vertexCount + " vertices have been declared");
            }

            return index;
        }

        /// <summary>Spatial-hash vertex welder. Public only so the compiler can reuse the tolerance.</summary>
        private sealed class VertexWelder
        {
            private readonly float _tolerance;
            private readonly double _cellSize;
            private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();

            public VertexWelder(float tolerance)
            {
                if (!(tolerance > 0f))
                {
                    throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "weld tolerance must be positive");
                }

                _tolerance = tolerance;
                _cellSize = tolerance;
            }

            public int Add(Vec3 position, List<Vec3> unique)
            {
                long cx = (long)Math.Floor(position.X / _cellSize);
                long cy = (long)Math.Floor(position.Y / _cellSize);
                long cz = (long)Math.Floor(position.Z / _cellSize);

                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        for (long dz = -1; dz <= 1; dz++)
                        {
                            List<int>? bucket;
                            if (!_cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out bucket))
                            {
                                continue;
                            }

                            for (int i = 0; i < bucket.Count; i++)
                            {
                                Vec3 candidate = unique[bucket[i]];
                                if (Math.Abs(candidate.X - position.X) < _tolerance &&
                                    Math.Abs(candidate.Y - position.Y) < _tolerance &&
                                    Math.Abs(candidate.Z - position.Z) < _tolerance)
                                {
                                    return bucket[i];
                                }
                            }
                        }
                    }
                }

                int index = unique.Count;
                unique.Add(position);

                long key = Key(cx, cy, cz);
                List<int>? target;
                if (!_cells.TryGetValue(key, out target))
                {
                    target = new List<int>();
                    _cells.Add(key, target);
                }

                target.Add(index);
                return index;
            }

            private static long Key(long x, long y, long z)
            {
                unchecked
                {
                    long h = x * 73856093L;
                    h ^= y * 19349663L;
                    h ^= z * 83492791L;
                    return h;
                }
            }
        }
    }
}
