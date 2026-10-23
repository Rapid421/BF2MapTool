using System.Globalization;
using System.Numerics;

namespace HavokFb;

/// <summary>
/// Wavefront OBJ reader.
///
/// Replaces the reader vendored from MagixGames/FrostyHavokPlugin@7f5dba4
/// (HavokFb/WavefrontObj.cs), which could not read output from Blender or Max:
/// it split on a single space (so aligned or tab-separated columns produced empty
/// tokens), advanced an extra line on comments and vertex-normal lines (silently
/// dropping geometry), treated the texture/normal fields of `f v/vt/vn` as extra
/// vertex indices, kept OBJ's 1-based indices as 0-based, did not rebase indices
/// per `o` group, and had an <c>AsTris</c> that returned an empty list.
///
/// This implementation keeps the members the rest of the tool uses.
/// </summary>
internal class WavefrontObj
{
    public struct Face
    {
        public List<int> _indices;

        public Face(params int[] indices)
        {
            _indices = indices.ToList();
        }

        public void Add(int vertex) => (_indices ??= new List<int>()).Add(vertex);

        public void Set(List<int> list) => _indices = list;

        public IEnumerable<int> Get() => _indices;

        /// <summary>Fan-triangulates this face.</summary>
        public List<Face> AsTris()
        {
            List<Face> tris = new();
            for (int i = 1; i < _indices.Count - 1; i++)
            {
                tris.Add(new Face(_indices[0], _indices[i], _indices[i + 1]));
            }
            return tris;
        }
    }

    public List<Vector4> _vertices = new();
    public List<Face> _faces = new();

    /// <summary>The `o`/`g` name, when the file declared one.</summary>
    public string Name { get; set; } = string.Empty;

    public static List<WavefrontObj> ReadFromFile(string file) => Read(File.ReadAllText(file));

    public static List<WavefrontObj> Read(string text)
    {
        // OBJ vertex indices are global across the whole file and keep counting across
        // `o` groups, so all positions are gathered first and rebased per group below.
        List<Vector4> allVertices = new();
        List<(string Name, List<List<int>> Faces)> groups = new();

        (string Name, List<List<int>> Faces) current = (string.Empty, new List<List<int>>());
        bool groupOpened = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            switch (tokens[0])
            {
                case "v":
                {
                    if (tokens.Length < 4)
                    {
                        throw new InvalidDataException($"Malformed vertex line: '{line}'");
                    }

                    float x = ParseFloat(tokens[1], line);
                    float y = ParseFloat(tokens[2], line);
                    float z = ParseFloat(tokens[3], line);
                    // The optional 4th component is OBJ's rational weight, not data we keep.
                    allVertices.Add(new Vector4(x, y, z, 0.5f));
                    break;
                }

                case "f":
                {
                    if (tokens.Length < 4)
                    {
                        throw new InvalidDataException(
                            $"Face with fewer than three vertices: '{line}'");
                    }

                    List<int> indices = new(tokens.Length - 1);
                    for (int i = 1; i < tokens.Length; i++)
                    {
                        // "v", "v/vt", "v//vn" and "v/vt/vn" — only the position matters.
                        string field = tokens[i].Split('/')[0];
                        int index = ParseInt(field, line);

                        // OBJ indices are 1-based; negative values count back from the end.
                        indices.Add(index > 0 ? index - 1 : allVertices.Count + index);
                    }
                    current.Faces.Add(indices);
                    break;
                }

                case "o":
                case "g":
                {
                    string name = tokens.Length > 1 ? string.Join(" ", tokens.Skip(1)) : string.Empty;

                    // Ignore a leading group declaration before any geometry.
                    if (!groupOpened)
                    {
                        groupOpened = true;
                        current.Name = name;
                        break;
                    }

                    groups.Add(current);
                    current = (name, new List<List<int>>());
                    break;
                }

                // vt, vn, s, usemtl, mtllib and anything else carry no collision geometry.
            }
        }

        groups.Add(current);

        List<WavefrontObj> result = new();
        foreach ((string Name, List<List<int>> Faces) group in groups)
        {
            if (group.Faces.Count == 0)
            {
                continue;
            }

            result.Add(Rebase(group.Name, group.Faces, allVertices));
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("OBJ contains no faces.");
        }

        return result;
    }

    /// <summary>
    /// Builds an object holding only the vertices its own faces reference, renumbered
    /// from zero. Convex polytope shapes index vertices with a byte, so carrying the
    /// whole file's vertex list into every object would blow the 256 limit immediately.
    /// </summary>
    private static WavefrontObj Rebase(string name, List<List<int>> faces, List<Vector4> allVertices)
    {
        WavefrontObj obj = new() { Name = name };
        Dictionary<int, int> remap = new();

        foreach (List<int> face in faces)
        {
            Face rebased = new(Array.Empty<int>());
            foreach (int globalIndex in face)
            {
                if (globalIndex < 0 || globalIndex >= allVertices.Count)
                {
                    throw new InvalidDataException(
                        $"Face references vertex {globalIndex + 1}, but the file declares " +
                        $"{allVertices.Count} vertices.");
                }

                if (!remap.TryGetValue(globalIndex, out int local))
                {
                    local = obj._vertices.Count;
                    remap[globalIndex] = local;
                    obj._vertices.Add(allVertices[globalIndex]);
                }

                rebased.Add(local);
            }
            obj._faces.Add(rebased);
        }

        return obj;
    }

    /// <summary>Face vertex positions, in face order.</summary>
    public IEnumerable<List<Vector4>> GetFaces()
    {
        foreach (Face face in _faces)
        {
            yield return face._indices.Select(i => _vertices[i]).ToList();
        }
    }

    public void ForceTris()
    {
        List<Face> tris = new();
        foreach (Face face in _faces)
        {
            if (face._indices.Count == 3)
            {
                tris.Add(face);
                continue;
            }
            tris.AddRange(face.AsTris());
        }
        _faces = tris;
    }

    private static float ParseFloat(string token, string line) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : throw new InvalidDataException($"Could not parse '{token}' as a number in: '{line}'");

    private static int ParseInt(string token, string line) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidDataException($"Could not parse '{token}' as an index in: '{line}'");
}
