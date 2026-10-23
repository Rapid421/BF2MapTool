using System.Numerics;
using hk;
using HavokFb;

namespace HavokCollisionTool;

/// <summary>
/// Geometry checks that need no game data. Validates the OBJ -> shape path against the
/// same invariants ShapeExtensions.Export asserts when reading a stock shape:
/// every face plane's constant must equal the mean of -dot(vertex, normal) over that
/// face's vertices, and the plane normal must point away from the centroid.
/// </summary>
internal static class CubeSelfTest
{
    private const string CubeObj = """
        o cube
        v -1 -1 -1
        v  1 -1 -1
        v  1  1 -1
        v -1  1 -1
        v -1 -1  1
        v  1 -1  1
        v  1  1  1
        v -1  1  1
        f 1 4 3 2
        f 5 6 7 8
        f 1 2 6 5
        f 2 3 7 6
        f 3 4 8 7
        f 4 1 5 8
        """;

    public static int Run()
    {
        int failures = 0;

        string temp = Path.Combine(Path.GetTempPath(), $"havokselftest-{Guid.NewGuid():N}.obj");
        File.WriteAllText(temp, CubeObj);

        try
        {
            List<WavefrontObj> objects = WavefrontObj.ReadFromFile(temp);
            Check(ref failures, objects.Count == 1, $"parsed {objects.Count} object(s), expected 1");

            WavefrontObj cube = objects[0];
            Check(ref failures, cube._vertices.Count == 8, $"parsed {cube._vertices.Count} vertices, expected 8");
            Check(ref failures, cube._faces.Count == 6, $"parsed {cube._faces.Count} faces, expected 6");

            hknpConvexPolytopeShape shape = ShapeBuilder.BuildConvexShape(cube);

            // Vertices and planes are padded to a multiple of four.
            Check(ref failures, shape._vertices.Count % 4 == 0,
                $"vertex count {shape._vertices.Count} is not padded to a multiple of 4");
            Check(ref failures, shape._planes.Count % 4 == 0,
                $"plane count {shape._planes.Count} is not padded to a multiple of 4");
            Check(ref failures, shape._faces.Count == 6, $"built {shape._faces.Count} faces, expected 6");
            Check(ref failures, shape._indices.Count == 24, $"built {shape._indices.Count} indices, expected 24");

            // Per-face plane invariant, the assertion in ShapeExtensions.Export.
            for (int i = 0; i < 6; i++)
            {
                hknpConvexPolytopeShapeFace face = shape._faces[i]!;
                OpenTK.Mathematics.Vector4 plane = shape._planes[i];
                Vector3 normal = new(plane.X, plane.Y, plane.Z);

                Check(ref failures, Math.Abs(normal.Length() - 1f) < 1e-4f,
                    $"face {i}: plane normal is not unit length ({normal.Length()})");

                float distance = 0f;
                for (int k = 0; k < face._numIndices; k++)
                {
                    OpenTK.Mathematics.Vector4 v = shape._vertices[shape._indices[face._firstIndex + k]];
                    distance += -Vector3.Dot(new Vector3(v.X, v.Y, v.Z), normal);
                }
                distance /= face._numIndices;

                Check(ref failures, Math.Abs(distance - plane.W) < 1e-3f,
                    $"face {i}: plane constant {plane.W} != computed {distance}");

                // A unit cube centred on the origin: every face plane sits at distance 1.
                Check(ref failures, Math.Abs(Math.Abs(plane.W) - 1f) < 1e-3f,
                    $"face {i}: expected |d| == 1 for the unit cube, got {plane.W}");

                // Outward-facing: the origin is inside, so dot(normal, origin) + d < 0.
                Check(ref failures, plane.W < 0f,
                    $"face {i}: plane faces inward (d = {plane.W}); winding or normal sign is wrong");

                // Adjacent cube faces meet at 90 degrees -> half angle 45 deg -> 128 TIM units.
                Check(ref failures, face._minHalfAngle is >= 120 and <= 136,
                    $"face {i}: minHalfAngle {face._minHalfAngle} outside the expected ~128 for a cube");
            }

            // Vertex W carries the source index.
            for (int i = 0; i < 8; i++)
            {
                int packed = BitConverter.ToInt32(BitConverter.GetBytes(shape._vertices[i].W));
                Check(ref failures, (packed & ~0x3f000000) == i,
                    $"vertex {i}: packed index {packed & ~0x3f000000} != {i}");
            }

            // Volume of a 2x2x2 cube is 8.
            (float volume, Vector3 centroid) = MassProperties.ComputeVolumeAndCentroid(cube);
            Check(ref failures, Math.Abs(volume - 8f) < 1e-3f, $"volume {volume}, expected 8");
            Check(ref failures, centroid.Length() < 1e-3f, $"centroid {centroid}, expected the origin");

            // A cube wound the other way must still produce outward planes.
            string reversedObj = string.Join("\n", CubeObj.Split('\n').Select(line =>
                line.StartsWith("f ")
                    // Enumerable.Reverse by name: on .NET 9+ a bare .Reverse() on an
                    // array binds to MemoryExtensions.Reverse, which reverses in place
                    // and returns void.
                    ? "f " + string.Join(" ", Enumerable.Reverse(
                        line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                    : line));

            string reversedPath = Path.Combine(Path.GetTempPath(), $"havokselftest-rev-{Guid.NewGuid():N}.obj");
            File.WriteAllText(reversedPath, reversedObj);
            try
            {
                hknpConvexPolytopeShape reversed = ShapeBuilder.BuildConvexShape(
                    WavefrontObj.ReadFromFile(reversedPath)[0]);

                for (int i = 0; i < 6; i++)
                {
                    Check(ref failures, reversed._planes[i].W < 0f,
                        $"reversed-winding face {i}: plane still faces inward (d = {reversed._planes[i].W})");
                }
            }
            finally
            {
                try { File.Delete(reversedPath); } catch { /* best effort */ }
            }

            // Triangulated input must reach the same volume and set the triangle flag.
            WavefrontObj tris = objects[0];
            tris.ForceTris();
            hknpConvexPolytopeShape triShape = ShapeBuilder.BuildConvexShape(tris);
            Check(ref failures, triShape._flags.HasFlag(hknpShape_FlagsEnum.CONTAINS_ONLY_TRIANGLES),
                "triangulated cube did not set CONTAINS_ONLY_TRIANGLES");
            Check(ref failures, triShape._faces.Count == 12,
                $"triangulated cube built {triShape._faces.Count} faces, expected 12");

            CheckCompound(ref failures);
            CheckHull(ref failures);
            CheckMassPropertyKey(ref failures);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }

        Console.WriteLine(failures == 0 ? "selftest: PASS" : $"selftest: FAIL ({failures} check(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Mass properties must be filed under the key the engine looks up. 979 of 979 shipped
    /// property entries use 0xF100; earlier builds wrote 0xF102 on every shape, which nothing
    /// structural notices — only something that asks a shape for its mass.
    /// </summary>
    private static void CheckMassPropertyKey(ref int failures)
    {
        hknpConvexPolytopeShape shape = ShapeBuilder.BuildConvexShape(WavefrontObj.Read(CubeObj)[0]);
        List<hkRefCountedPropertiesEntry?> entries = shape._properties?._entries ?? new();

        Check(ref failures, entries.Count == 1,
            $"built shape has {entries.Count} property entries, expected exactly 1 (mass properties)");
        Check(ref failures,
            entries.All(e => e?._object is hknpShapeMassProperties && e._key == MassProperties.MassPropertiesKey),
            "built shape files its mass properties under " +
            string.Join(", ", entries.Select(e => $"0x{e!._key:X4}")) + ", expected 0xF100");

        // A shape an earlier build wrote gets refiled, including one reached through a compound.
        foreach (hkRefCountedPropertiesEntry? e in entries)
        {
            e!._key = 0xF102;
        }

        hknpStaticCompoundShape compound =
            CompoundBuilder.Build(new List<hknpShape> { shape }, CompoundBuilder.InheritUserData);
        int refiled = MassProperties.RepairKeys(compound);

        Check(ref failures, refiled == 1 && entries.All(e => e!._key == MassProperties.MassPropertiesKey),
            $"RepairKeys refiled {refiled} entries, expected 1; keys now " +
            string.Join(", ", entries.Select(e => $"0x{e!._key:X4}")));
    }

    /// <summary>
    /// Checks convexity detection and hull construction.
    ///
    /// The decisive test is the last one: for a random cloud, every input point must lie
    /// behind every face plane of the hull. That is the definition of a convex hull, and
    /// it catches the failure that matters — a hull with a face missing or wound inward
    /// still looks like a closed mesh but bounds the wrong volume.
    /// </summary>
    private static void CheckHull(ref int failures)
    {
        // 1. A cube is convex, and hulling it changes nothing but the winding order.
        WavefrontObj cube = WavefrontObj.Read(CubeObj)[0];
        float deviation = ConvexHull.ConvexityDeviation(cube, out _);
        Check(ref failures, deviation < 1e-4f, $"cube reported concave (deviation {deviation})");

        WavefrontObj cubeHull = ConvexHull.Build(cube);
        Check(ref failures, cubeHull._vertices.Count == 8,
            $"cube hull has {cubeHull._vertices.Count} vertices, expected 8");
        Check(ref failures, cubeHull._faces.Count == 6,
            $"cube hull has {cubeHull._faces.Count} faces, expected 6 quads (coplanar merge failed)");

        // 2. A point strictly inside must not survive into the hull, and a point sitting
        //    in the middle of a face must not become a corner.
        WavefrontObj padded = WavefrontObj.Read(
            CubeObj + "\nv 0 0 0\nv 0 0 -1\nf 9 9 9\nf 10 10 10\n")[0];
        WavefrontObj paddedHull = ConvexHull.Build(padded);
        Check(ref failures, paddedHull._vertices.Count == 8,
            $"hull kept {paddedHull._vertices.Count} vertices; interior and on-face points " +
            "should both have been dropped");

        // 3. An L-shaped prism is concave, and its hull is strictly bigger.
        WavefrontObj shape = WavefrontObj.Read(LShapeObj)[0];
        float lDeviation = ConvexHull.ConvexityDeviation(shape, out _);
        Check(ref failures, lDeviation > 0.1f,
            $"L-shape reported convex (deviation {lDeviation}); concavity detection is broken");

        WavefrontObj lHull = ConvexHull.Build(shape);
        (float before, _) = MassProperties.ComputeVolumeAndCentroid(shape);
        (float after, _) = MassProperties.ComputeVolumeAndCentroid(lHull);
        Check(ref failures, after > before * 1.1f,
            $"L-shape hull volume {after} is not meaningfully larger than the mesh's {before}");
        Check(ref failures, ConvexHull.ConvexityDeviation(lHull, out _) < 1e-3f,
            "the hull of the L-shape is itself concave");

        // 4. Random clouds: the hull must contain every input point, and must be a shape
        //    the rest of the pipeline can actually build.
        // Many trials on purpose. The bugs this catches appeared on roughly one cloud in
        // eight, so a handful of trials would have shipped them. Odd trials snap to a
        // coarse grid, which is the hard case and the realistic one: a modeller working on
        // grid produces piles of exactly coplanar points and exact duplicates.
        Random random = new(20260831);
        for (int trial = 0; trial < 120; trial++)
        {
            List<Vector3> cloud = new();
            int count = 12 + trial * 3;
            bool snap = (trial & 1) == 1;

            for (int i = 0; i < count; i++)
            {
                double x = random.NextDouble() * 20.0 - 10.0;
                double y = random.NextDouble() * 6.0 - 3.0;
                double z = random.NextDouble() * 20.0 - 10.0;

                if (snap)
                {
                    x = Math.Round(x * 2.0) / 2.0;
                    y = Math.Round(y * 2.0) / 2.0;
                    z = Math.Round(z * 2.0) / 2.0;
                }

                cloud.Add(new Vector3((float)x, (float)y, (float)z));
            }

            WavefrontObj hull;
            try
            {
                hull = ConvexHull.Build(AsPointSoup(cloud));
            }
            catch (Exception ex)
            {
                Check(ref failures, false, $"cloud {trial}: hull build threw: {ex.Message}");
                continue;
            }

            Check(ref failures, ConvexHull.ConvexityDeviation(hull, out _) < 1e-2f,
                $"cloud {trial}: the hull is not convex");

            float outside = FurthestOutside(hull, cloud);
            Check(ref failures, outside < 1e-2f,
                $"cloud {trial}: an input point lies {outside} outside the hull");

            Check(ref failures, hull._vertices.Count <= 256,
                $"cloud {trial}: hull has {hull._vertices.Count} vertices, over the byte-index limit");

            try
            {
                ShapeBuilder.BuildConvexShape(hull);
            }
            catch (Exception ex)
            {
                Check(ref failures, false, $"cloud {trial}: hull did not build a shape: {ex.Message}");
            }
        }

        // 5. A flat sheet has no volume, so there is no hull to build. It must say so
        //    rather than emit a shape that bounds nothing.
        WavefrontObj sheet = WavefrontObj.Read(
            "o sheet\nv 0 0 0\nv 4 0 0\nv 4 0 4\nv 0 0 4\nf 1 2 3 4\n")[0];

        bool refused = false;
        try
        {
            ConvexHull.Build(sheet);
        }
        catch (InvalidDataException)
        {
            refused = true;
        }

        Check(ref failures, refused, "a flat sheet produced a hull instead of an error");

        // Flat pieces have to be recognised *before* a shape is built from them: a render
        // mesh is full of decals and detail cards, and each one would otherwise become a
        // volumeless polytope sitting in the compound.
        Check(ref failures, ConvexHull.IsFlat(sheet), "a flat sheet was not detected as flat");
        Check(ref failures, ConvexHull.Prepare(sheet, 0, ConvexHull.Mode.Auto) == null,
            "a flat sheet was not skipped");
        Check(ref failures, !ConvexHull.IsFlat(cube), "a cube was misreported as flat");

        // A thin panel is still a solid, however thin, and must survive.
        WavefrontObj panel = WavefrontObj.Read(
            "o panel\nv 0 0 0\nv 4 0 0\nv 4 0.02 0\nv 0 0.02 0\n" +
            "v 0 0 4\nv 4 0 4\nv 4 0.02 4\nv 0 0.02 4\n" +
            "f 1 4 3 2\nf 5 6 7 8\nf 1 2 6 5\nf 2 3 7 6\nf 3 4 8 7\nf 4 1 5 8\n")[0];
        Check(ref failures, !ConvexHull.IsFlat(panel), "a thin panel was misreported as flat");
    }

    /// <summary>
    /// How far the worst point lies outside the hull's face planes. Zero means the hull
    /// really does contain the set it was built from.
    /// </summary>
    private static float FurthestOutside(WavefrontObj hull, List<Vector3> points)
    {
        Vector3 centre = Vector3.Zero;
        foreach (Vector4 v in hull._vertices)
        {
            centre += new Vector3(v.X, v.Y, v.Z);
        }
        centre /= hull._vertices.Count;

        float worst = 0f;

        foreach (List<Vector4> face in hull.GetFaces())
        {
            Vector3 a = new(face[0].X, face[0].Y, face[0].Z);
            Vector3 b = new(face[1].X, face[1].Y, face[1].Z);
            Vector3 c = new(face[2].X, face[2].Y, face[2].Z);

            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.LengthSquared() <= float.Epsilon)
            {
                continue;
            }

            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, centre - a) > 0f)
            {
                normal = -normal;
            }

            float offset = -Vector3.Dot(normal, a);
            foreach (Vector3 p in points)
            {
                worst = Math.Max(worst, Vector3.Dot(normal, p) + offset);
            }
        }

        return worst;
    }

    /// <summary>
    /// Wraps loose points as an OBJ object. The faces are degenerate — the hull builder
    /// only reads vertices — but they keep the reader's "no faces" guard happy.
    /// </summary>
    private static WavefrontObj AsPointSoup(List<Vector3> points)
    {
        System.Text.StringBuilder obj = new();
        obj.AppendLine("o cloud");
        foreach (Vector3 p in points)
        {
            obj.AppendLine(FormattableString.Invariant($"v {p.X} {p.Y} {p.Z}"));
        }
        for (int i = 1; i + 2 <= points.Count; i += 3)
        {
            obj.AppendLine($"f {i} {i + 1} {i + 2}");
        }
        return WavefrontObj.Read(obj.ToString())[0];
    }

    /// <summary>An L-shaped prism: convex everywhere except the inside corner.</summary>
    private const string LShapeObj = """
        o lshape
        v 0 0 0
        v 4 0 0
        v 4 1 0
        v 1 1 0
        v 1 4 0
        v 0 4 0
        v 0 0 2
        v 4 0 2
        v 4 1 2
        v 1 1 2
        v 1 4 2
        v 0 4 2
        f 6 5 4 3 2 1
        f 7 8 9 10 11 12
        f 1 2 8 7
        f 2 3 9 8
        f 3 4 10 9
        f 4 5 11 10
        f 5 6 12 11
        f 6 1 7 12
        """;

    /// <summary>
    /// Checks the static compound shape and, more importantly, its quantised AABB tree.
    ///
    /// The tree is the part with no second chance: a node that does not bound its
    /// children makes the traversal skip real geometry, and the result is collision with
    /// holes in it that only shows up in game. So this walks the tree exactly the way the
    /// runtime does — left child at i+1, right child at i+delta — and asserts that every
    /// decoded box still contains the child it is supposed to bound.
    /// </summary>
    private static void CheckCompound(ref int failures)
    {
        foreach (int count in new[] { 2, 3, 5, 8, 17 })
        {
            string path = Path.Combine(Path.GetTempPath(), $"havokselftest-compound-{Guid.NewGuid():N}.obj");
            File.WriteAllText(path, ScatteredCubes(count));

            try
            {
                List<hknpShape> children = WavefrontObj.ReadFromFile(path)
                    .Select(o => (hknpShape)ShapeBuilder.BuildConvexShape(o))
                    .ToList();

                Check(ref failures, children.Count == count,
                    $"compound({count}): read {children.Count} objects from the OBJ");

                hknpStaticCompoundShape compound =
                    CompoundBuilder.Build(children, CompoundBuilder.InheritUserData);

                Check(ref failures, compound._instances!._elements.Count == count,
                    $"compound({count}): {compound._instances._elements.Count} instances");
                Check(ref failures, compound._instances._firstFree == -1,
                    $"compound({count}): _firstFree is {compound._instances._firstFree}, expected -1");
                Check(ref failures, 1 << compound._numShapeKeyBits >= count,
                    $"compound({count}): _numShapeKeyBits {compound._numShapeKeyBits} cannot address {count} children");
                Check(ref failures, compound._dispatchType == hknpCollisionDispatchType_Enum.COMPOSITE,
                    $"compound({count}): dispatch type is {compound._dispatchType}");

                List<hkcdStaticTreeCodec3Axis6?> nodes =
                    compound._boundingVolumeData!._aabbTree!._nodes;

                Check(ref failures, nodes.Count == count * 2 - 1,
                    $"compound({count}): {nodes.Count} tree nodes, expected {count * 2 - 1}");

                OpenTK.Mathematics.Box3 domain = new(
                    new OpenTK.Mathematics.Vector3(compound._boundingVolumeData._aabbTree._domain!._min),
                    new OpenTK.Mathematics.Vector3(compound._boundingVolumeData._aabbTree._domain._max));

                bool[] reached = new bool[count];
                int visited = 0;
                Walk(ref failures, nodes, 0, domain, children, reached, ref visited, count);

                Check(ref failures, visited == nodes.Count,
                    $"compound({count}): traversal reached {visited} of {nodes.Count} nodes");
                Check(ref failures, reached.All(x => x),
                    $"compound({count}): some children have no leaf in the tree");

                // Every shipped instance record packs three int24 values into the transform's
                // otherwise-unused w lanes: flags in col0.w (0x40 for an identity placement
                // inside a prop), and in trans.w the index of the tree node whose leaf names
                // this instance. col1.w is left raw zero. Writing an identity matrix instead
                // put 0 / 0 / 1.0 there — matching no shipped record at all. The shape-size
                // lane (col2.w) needs the serialized layout, so it is checked at import.
                Dictionary<int, int> leafOf = new();
                for (int k = 0; k < nodes.Count; k++)
                {
                    hkcdStaticTreeCodec3Axis6 node = nodes[k]!;
                    if ((node._hiData & 0x80) == 0)
                    {
                        leafOf[node._loData | (node._hiData << 16)] = k;
                    }
                }

                static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
                // Bit 23 clear too: 1.0f is 0x3F800000, the identity value in trans.w.
                static bool IsInt24(float f) => (Bits(f) & unchecked((int)0xFF800000)) == 0x3F000000;
                static int Int24(float f) => Bits(f) & 0xFFFFFF;

                for (int i = 0; i < count; i++)
                {
                    hknpShapeInstance instance = compound._instances._elements[i]!;
                    OpenTK.Mathematics.Matrix4 t = instance._transform;

                    Check(ref failures, IsInt24(t.M14) && Int24(t.M14) == 0x40,
                        $"compound({count}) instance {i}: col0.w flags are 0x{Bits(t.M14):X8}, expected packed int24 0x40");
                    Check(ref failures, Bits(t.M24) == 0,
                        $"compound({count}) instance {i}: col1.w is 0x{Bits(t.M24):X8}, expected raw zero");
                    Check(ref failures, IsInt24(t.M44) && leafOf.TryGetValue(i, out int leaf) && Int24(t.M44) == leaf,
                        $"compound({count}) instance {i}: trans.w is 0x{Bits(t.M44):X8}, expected the packed index of its leaf node " +
                        $"({(leafOf.TryGetValue(i, out int l) ? l : -1)})");
                    Check(ref failures, instance._scale.W == 1f,
                        $"compound({count}) instance {i}: scale.w is {instance._scale.W}, expected 1.0 inside a prop compound");
                }

                // An import rewrites the whole res, template shapes included, so a compound an
                // earlier build wrote into a slot this import does not touch is carried through
                // as it was. Reproduced: shape 0 stayed 0/5 packed after a compressed import of
                // shape 1 — and came out half-filled, with the size lane written but flags and
                // back-link still empty. So repair must fill each missing lane independently.
                for (int i = 0; i < count; i++)
                {
                    OpenTK.Mathematics.Matrix4 legacy = OpenTK.Mathematics.Matrix4.Identity;
                    if (i % 2 == 0)
                    {
                        legacy.M34 = InstanceRecords.PackInt24(400);
                    }
                    compound._instances!._elements[i]!._transform = legacy;
                }

                int repaired = InstanceRecords.RepairLegacyRecords(compound);
                Check(ref failures, repaired == count,
                    $"compound({count}): repaired {repaired} legacy records, expected {count}");

                for (int i = 0; i < count; i++)
                {
                    OpenTK.Mathematics.Matrix4 t = compound._instances!._elements[i]!._transform;
                    Check(ref failures,
                        IsInt24(t.M14) && Int24(t.M14) == 0x40 && Bits(t.M24) == 0 &&
                        IsInt24(t.M44) && leafOf.TryGetValue(i, out int leaf) && Int24(t.M44) == leaf,
                        $"compound({count}) instance {i}: legacy record not repaired " +
                        $"(col0 0x{Bits(t.M14):X8}, col1 0x{Bits(t.M24):X8}, trans 0x{Bits(t.M44):X8})");
                }

                // A level placement (scale.w 0) is never guessed at: its flags carry bits a prop
                // child's do not, and inventing them could move collision.
                hknpShapeInstance placement = compound._instances!._elements[0]!;
                placement._transform = OpenTK.Mathematics.Matrix4.Identity;
                placement._scale = new OpenTK.Mathematics.Vector4(1f, 1f, 1f, 0f);
                InstanceRecords.RepairLegacyRecords(compound);
                Check(ref failures, Bits(placement._transform.M14) == 0,
                    $"compound({count}): repair modified a record with scale.w 0 (a level placement)");
            }
            finally
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
    }

    private static void Walk(ref int failures, List<hkcdStaticTreeCodec3Axis6?> nodes, int index,
                             OpenTK.Mathematics.Box3 parent, List<hknpShape> children,
                             bool[] reached, ref int visited, int count)
    {
        if (index < 0 || index >= nodes.Count)
        {
            Check(ref failures, false, $"compound({count}): node index {index} is out of range");
            return;
        }

        visited++;
        hkcdStaticTreeCodec3Axis6 node = nodes[index]!;
        OpenTK.Mathematics.Box3 box = CompoundBuilder.Decode(parent, node);

        if ((node._hiData & 0x80) == 0)
        {
            int child = node._loData | (node._hiData << 16);
            if (child < 0 || child >= count)
            {
                Check(ref failures, false, $"compound({count}): leaf names child {child}");
                return;
            }

            if (reached[child])
            {
                Check(ref failures, false, $"compound({count}): child {child} has two leaves");
            }
            reached[child] = true;

            OpenTK.Mathematics.Box3 actual = CompoundBuilder.GetAabb(children[child]);
            for (int i = 0; i < 3; i++)
            {
                Check(ref failures, box.Min[i] <= actual.Min[i] && box.Max[i] >= actual.Max[i],
                    $"compound({count}): leaf box for child {child} does not bound it on axis {i}: " +
                    $"[{box.Min[i]}, {box.Max[i]}] vs [{actual.Min[i]}, {actual.Max[i]}]");
            }
            return;
        }

        // Internal: the stored value is half the offset to the right child, and the left
        // child is always the next node.
        int offset = ((node._hiData & ~0x80) << 16 | node._loData) << 1;
        Walk(ref failures, nodes, index + 1, box, children, reached, ref visited, count);
        Walk(ref failures, nodes, index + offset, box, children, reached, ref visited, count);
    }

    /// <summary>Unit cubes on a diagonal, far enough apart to have disjoint boxes.</summary>
    private static string ScatteredCubes(int count)
    {
        System.Text.StringBuilder obj = new();

        for (int c = 0; c < count; c++)
        {
            float x = c * 3f;
            float y = (c % 3) * 2.5f;
            float z = (c % 5) * 1.75f;

            obj.AppendLine($"o piece{c}");
            foreach ((int sx, int sy, int sz) in new[]
                     {
                         (-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                         (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)
                     })
            {
                obj.AppendLine(System.FormattableString.Invariant(
                    $"v {x + sx} {y + sy} {z + sz}"));
            }

            int b = c * 8;
            obj.AppendLine($"f {b + 1} {b + 4} {b + 3} {b + 2}");
            obj.AppendLine($"f {b + 5} {b + 6} {b + 7} {b + 8}");
            obj.AppendLine($"f {b + 1} {b + 2} {b + 6} {b + 5}");
            obj.AppendLine($"f {b + 2} {b + 3} {b + 7} {b + 6}");
            obj.AppendLine($"f {b + 3} {b + 4} {b + 8} {b + 7}");
            obj.AppendLine($"f {b + 4} {b + 1} {b + 5} {b + 8}");
        }

        return obj.ToString();
    }

    private static void Check(ref int failures, bool condition, string message)
    {
        if (condition)
        {
            return;
        }

        Console.Error.WriteLine($"  FAIL {message}");
        failures++;
    }
}
