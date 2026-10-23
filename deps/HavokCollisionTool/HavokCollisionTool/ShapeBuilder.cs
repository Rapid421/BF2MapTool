using System.Numerics;
using hk;
using HavokFb;
using Box3 = OpenTK.Mathematics.Box3;

namespace HavokCollisionTool;

/// <summary>
/// Builds Havok shapes from Wavefront OBJ geometry.
///
/// Derived from MagixGames/FrostyHavokPlugin@7f5dba4:HavokFb/Program.cs
/// (ObjToHavok_1Shape / BuildConvexShape), with the plane-distance and
/// normalisation fixes noted inline.
/// </summary>
internal static class ShapeBuilder
{
    /// <summary>
    /// Builds a single <see cref="hknpConvexPolytopeShape"/> from one OBJ object.
    /// The mesh must already be convex: a polytope shape stores face planes, so a
    /// concave input produces planes that do not bound the geometry and the shape
    /// will collide as its convex hull at best.
    /// </summary>
    /// <param name="template">
    /// The shape being replaced, when there is one. Its mass properties are carried
    /// over and rescaled rather than invented — see <see cref="MassProperties"/>.
    /// </param>
    /// <param name="userData">
    /// Packed part and material indices. The high 16 bits select the physics material —
    /// footstep sounds, impact effects, penetration — so this is inherited from the
    /// shape being replaced. Writing 0 here, as this did before, silently reassigned
    /// every imported surface to material 0.
    /// </param>
    public static hknpConvexPolytopeShape BuildConvexShape(WavefrontObj objFile, hknpShape? template = null,
                                                           ulong userData = 0)
    {
        hknpConvexPolytopeShape shape = new()
        {
            // Flag set observed on stock convex polytope shapes.
            _flags = hknpShape_FlagsEnum.IS_CONVEX_SHAPE |
                     hknpShape_FlagsEnum.IS_CONVEX_POLYTOPE_SHAPE |
                     hknpShape_FlagsEnum.USE_SMALL_FACE_INDICES |
                     hknpShape_FlagsEnum.USE_NORMAL_TO_FIND_SUPPORT_PLANE,
            _dispatchType = hknpCollisionDispatchType_Enum.CONVEX,
            _userData = userData,
            _planes = new List<OpenTK.Mathematics.Vector4>(),
            _faces = new List<hknpConvexPolytopeShapeFace?>(),
            _indices = new List<byte>(),
            _vertices = new List<OpenTK.Mathematics.Vector4>()
        };

        if (objFile._vertices.Count == 0 || objFile._faces.Count == 0)
        {
            throw new InvalidDataException("OBJ object has no vertices or no faces.");
        }

        if (objFile._vertices.Count > byte.MaxValue + 1)
        {
            throw new InvalidDataException(
                $"Convex polytope shapes index vertices with a byte: {objFile._vertices.Count} vertices " +
                "exceeds the 256 limit. Simplify the collision mesh.");
        }

        // Face planes. Upstream divided the accumulated distance by the total face
        // count; the plane constant is the mean over *this face's* vertices, which is
        // what ShapeExtensions.Export asserts when reading a stock shape back.
        foreach (List<Vector4> faceVertices in objFile.GetFaces())
        {
            Vector4 plane = GetNormal(faceVertices);
            Vector3 normal = Vector3.Normalize(new Vector3(plane.X, plane.Y, plane.Z));

            float distance = 0.0f;
            foreach (Vector4 v in faceVertices)
            {
                distance += -Vector3.Dot(new Vector3(v.X, v.Y, v.Z), normal);
            }
            distance /= faceVertices.Count;

            shape._planes.Add(new OpenTK.Mathematics.Vector4(normal.X, normal.Y, normal.Z, distance));
        }

        EnsureOutwardPlanes(shape, objFile);

        // Vertices. W carries the source vertex index (hkVector4::setInt24W).
        for (int i = 0; i < objFile._vertices.Count; i++)
        {
            Vector4 v = objFile._vertices[i];
            float packedW = BitConverter.ToSingle(BitConverter.GetBytes(i | 0x3f000000));
            shape._vertices.Add(new OpenTK.Mathematics.Vector4(v.X, v.Y, v.Z, packedW));
        }

        // Flattened index/faces tables, needed both for the shape and for the
        // adjacency search that yields each face's minimum half angle.
        List<int> verticesPerFace = new();
        List<int> indices = new();
        foreach (WavefrontObj.Face f in objFile._faces)
        {
            verticesPerFace.Add(f._indices.Count);
            indices.AddRange(f._indices);
        }

        int maxNumEdgesPerFace = 0;
        int indicesOffset = 0;

        for (int i = 0; i < objFile._faces.Count; i++)
        {
            WavefrontObj.Face f = objFile._faces[i];

            foreach (int index in f.Get())
            {
                shape._indices.Add((byte)index);
            }

            int startIndex = indicesOffset;
            int length = f._indices.Count;
            maxNumEdgesPerFace = Math.Max(maxNumEdgesPerFace, length);

            hknpConvexPolytopeShapeFace face = new()
            {
                _firstIndex = (ushort)startIndex,
                _numIndices = (byte)length
            };

            // Minimum half angle between this face and each neighbour across a shared
            // edge, quantised into Havok's angular TIM units.
            float maxCosAngle = 0f;
            for (int j = length - 1, k = 0; k < length; j = k++)
            {
                int neighbour = FindFace(indices[startIndex + k], indices[startIndex + j], verticesPerFace, indices);
                if (neighbour < 0)
                {
                    // Open edge (non-closed mesh). Leave it out of the minimum.
                    continue;
                }

                Vector3 a = ToVec3(shape._planes[i]);
                Vector3 b = ToVec3(shape._planes[neighbour]);
                maxCosAngle = Math.Max(maxCosAngle, Vector3.Dot(a, b));
            }

            face._minHalfAngle = (byte)ConvertAngleToAngularTim((float)(Math.Acos(Math.Clamp(maxCosAngle, -1f, 1f)) * 0.5));

            shape._faces.Add(face);
            indicesOffset += length;
        }

        if (maxNumEdgesPerFace == 3)
        {
            shape._flags |= hknpShape_FlagsEnum.CONTAINS_ONLY_TRIANGLES;
        }

        // Vertex and plane tables are read four at a time (SIMD), so both are padded
        // to a multiple of four. Planes get a degenerate far plane, vertices repeat.
        while ((shape._vertices.Count & 3) != 0)
        {
            shape._vertices.Add(shape._vertices[^1]);
        }
        while ((shape._planes.Count & 3) != 0)
        {
            shape._planes.Add(new OpenTK.Mathematics.Vector4(0, 0, 0, float.MinValue));
        }

        shape._properties = MassProperties.Build(objFile, template);

        return shape;
    }

    public static Box3 ComputeAabb(IEnumerable<OpenTK.Mathematics.Vector4> vertices)
    {
        bool any = false;
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);

        foreach (OpenTK.Mathematics.Vector4 v in vertices)
        {
            Vector3 p = new(v.X, v.Y, v.Z);
            min = Vector3.Min(p, min);
            max = Vector3.Max(p, max);
            any = true;
        }

        if (!any)
        {
            return new Box3(OpenTK.Mathematics.Vector3.Zero, OpenTK.Mathematics.Vector3.Zero);
        }

        // Upstream seeded min/max with Vector3.Zero, which silently grew every AABB to
        // include the origin. Seed from the data instead.
        return new Box3(
            new OpenTK.Mathematics.Vector3(min.X, min.Y, min.Z),
            new OpenTK.Mathematics.Vector3(max.X, max.Y, max.Z));
    }

    /// <summary>
    /// A convex polytope is the intersection of its half-spaces, so every plane normal
    /// must point away from the interior. Reversed winding — common in exported collision
    /// meshes — inverts them all and produces a shape that bounds nothing. The mesh
    /// centroid is inside a convex body by definition, so it decides the sign.
    /// </summary>
    private static void EnsureOutwardPlanes(hknpConvexPolytopeShape shape, WavefrontObj objFile)
    {
        Vector3 centroid = Vector3.Zero;
        foreach (Vector4 v in objFile._vertices)
        {
            centroid += new Vector3(v.X, v.Y, v.Z);
        }
        centroid /= objFile._vertices.Count;

        int inverted = 0;
        foreach (OpenTK.Mathematics.Vector4 plane in shape._planes)
        {
            // Positive => the interior point sits outside this half-space.
            if (Vector3.Dot(ToVec3(plane), centroid) + plane.W > 1e-4f)
            {
                inverted++;
            }
        }

        if (inverted * 2 <= shape._planes.Count)
        {
            return;
        }

        Console.WriteLine(
            $"  note: {inverted}/{shape._planes.Count} face normals pointed inward " +
            "(reversed winding); flipping them.");

        for (int i = 0; i < shape._planes.Count; i++)
        {
            OpenTK.Mathematics.Vector4 p = shape._planes[i];
            shape._planes[i] = new OpenTK.Mathematics.Vector4(-p.X, -p.Y, -p.Z, -p.W);
        }

        // Winding drives the index order the reader walks, so reverse it to match.
        foreach (WavefrontObj.Face face in objFile._faces)
        {
            face._indices.Reverse();
        }
    }

    private static Vector3 ToVec3(OpenTK.Mathematics.Vector4 v) => new(v.X, v.Y, v.Z);

    /// <summary>
    /// Outward normal for counter-clockwise winding, the OBJ convention.
    /// Upstream used <c>Cross(v0 - v1, v2 - v0)</c>, which is the negation of this and
    /// yields inward-facing planes; a convex polytope with inward planes bounds nothing
    /// and collides as an empty volume.
    /// </summary>
    private static Vector3 GetNormal(Vector3 v0, Vector3 v1, Vector3 v2) => Vector3.Cross(v1 - v0, v2 - v0);

    private static Vector4 GetNormal(List<Vector4> verts)
    {
        if (verts.Count < 3)
        {
            throw new InvalidDataException("Face has fewer than three vertices.");
        }

        List<Vector3> v = verts.Select(x => new Vector3(x.X, x.Y, x.Z)).ToList();

        Vector3 normal;
        if (v.Count == 3)
        {
            normal = GetNormal(v[0], v[1], v[2]);
        }
        else
        {
            // Newell-style accumulation over the triangle fan for n-gons.
            normal = Vector3.Zero;
            for (int i = 1; i < v.Count - 1; i++)
            {
                normal += GetNormal(v[0], v[i], v[i + 1]);
            }
        }

        if (normal.LengthSquared() <= float.Epsilon)
        {
            throw new InvalidDataException("Degenerate face: cannot derive a normal.");
        }

        normal = Vector3.Normalize(normal);
        return new Vector4(normal.X, normal.Y, normal.Z, -1f);
    }

    /// <summary>
    /// Finds the face containing directed edge (a -> b). Returns -1 when the edge is
    /// unshared; upstream threw, which aborted import on any non-closed mesh.
    /// </summary>
    private static int FindFace(int a, int b, List<int> verticesPerFace, List<int> indices)
    {
        int indicesOffset = 0;
        for (int i = 0; i < verticesPerFace.Count; ++i)
        {
            int numIndices = verticesPerFace[i];
            for (int j = numIndices - 1, k = 0; k < numIndices; j = k++)
            {
                if (indices[indicesOffset + j] == a && indices[indicesOffset + k] == b)
                {
                    return i;
                }
            }
            indicesOffset += numIndices;
        }
        return -1;
    }

    private static int ConvertAngleToAngularTim(float angle) =>
        Math.Clamp((int)(angle * 510 / Math.PI + 0.5f), 0, 255);
}
