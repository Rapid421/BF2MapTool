using System.Numerics;
using HavokFb;

namespace HavokCollisionTool;

/// <summary>
/// Convexity testing and 3D convex hull construction.
///
/// Why this exists: an <c>hknpConvexPolytopeShape</c> stores face *planes*, and the
/// shape it describes is the intersection of those half-spaces. That cannot represent a
/// dent. Feed it a concave mesh and it does not fail — it quietly collides as the mesh's
/// convex hull, so an archway becomes a solid block and a staircase becomes a ramp, with
/// nothing in the import log to say so.
///
/// So: every piece is tested, a concave one is reported by name with how much volume the
/// hull adds, and the hull is built deliberately instead of being stumbled into. The real
/// fix is still to split the piece into convex chunks and import as a compound — the hull
/// only makes the failure visible and predictable.
///
/// The hull itself is computed in double precision. The vertices arrive as floats, so
/// doubles leave nine or so digits of headroom on every plane test, and the visibility
/// decisions that drive the algorithm are then exact for any input a modeller would
/// produce. Built in float, with a tolerance loose enough to absorb float noise, faces
/// flicker in and out of visibility near coplanar geometry, the surface stops being
/// closed, and the hull quietly stops containing its own input.
/// </summary>
internal static class ConvexHull
{
    public enum Mode
    {
        /// <summary>Hull only the pieces that are actually concave. The default.</summary>
        Auto,

        /// <summary>Hull every piece, even one already convex.</summary>
        Always,

        /// <summary>Never hull; still report concavity.</summary>
        Never
    }

    public static Mode Parse(string value) => value.ToLowerInvariant() switch
    {
        "auto" => Mode.Auto,
        "always" => Mode.Always,
        "never" => Mode.Never,
        _ => throw new InvalidDataException(
            $"Unknown hull mode '{value}'. Expected auto, always or never.")
    };

    /// <summary>
    /// Returns the geometry to actually build the shape from, or null when the piece
    /// cannot be collision at all. Reports anything the caller should know about on the way.
    /// </summary>
    public static WavefrontObj Prepare(WavefrontObj obj, int index, Mode mode)
    {
        float deviation = ConvexityDeviation(obj, out float scale);
        bool concave = deviation > ReportTolerance(scale);

        // Render meshes are full of flat cards — decals, dirt overlays, single-sided
        // detail planes. A convex polytope built from one is a shape with no inside, which
        // is not collision; it is a degenerate object sitting in the compound waiting to
        // divide by zero. Drop it and say which piece went.
        if (IsFlat(obj))
        {
            Console.WriteLine(
                $"  piece {index} is flat — no thickness, so it cannot be a collision solid. " +
                "Skipping it.");
            return null;
        }

        if (concave)
        {
            Console.WriteLine(
                $"  piece {index} is CONCAVE — it dips {deviation:0.####} units behind its own " +
                "faces. A convex polytope cannot hold a dent.");
        }

        if (mode == Mode.Never || (mode == Mode.Auto && !concave))
        {
            if (concave)
            {
                Console.WriteLine(
                    "    hulling is off, so it will collide as its hull anyway — openings " +
                    "solid, steps smoothed. Split it into convex chunks and import as a compound.");
            }
            return obj;
        }

        WavefrontObj hull;
        try
        {
            hull = Build(obj);
        }
        catch (InvalidDataException ex)
        {
            Console.WriteLine($"    could not build a hull for piece {index}: {ex.Message}");
            return obj;
        }

        if (!concave)
        {
            Console.WriteLine(
                $"  piece {index}: rebuilt as its hull — " +
                $"{obj._vertices.Count} verts / {obj._faces.Count} faces -> " +
                $"{hull._vertices.Count} / {hull._faces.Count}.");
            return hull;
        }

        (float before, _) = MassProperties.ComputeVolumeAndCentroid(obj);
        (float after, _) = MassProperties.ComputeVolumeAndCentroid(hull);

        // A hull encloses the mesh, so it can never hold less volume. When the arithmetic
        // says otherwise the mesh's volume is the thing that is wrong, not the hull: the
        // signed-tetrahedron integral is only meaningful over a closed surface, and render
        // meshes are routinely open, doubled or self-overlapping. Reporting the number
        // anyway produced lines like "-99.4% more volume".
        string filled = before > 1e-6f && after >= before
            ? $"{(after - before) / before * 100f:0.#}% more volume"
            : "bigger by an amount that cannot be measured here — the source is not a closed " +
              "solid, so it has no volume to compare against";

        Console.WriteLine(
            $"    replaced with its convex hull ({hull._vertices.Count} verts, " +
            $"{hull._faces.Count} faces), which is {filled}. Anything hollow in that piece is " +
            "now solid — split it into convex chunks and import as a compound if that is wrong.");

        return hull;
    }

    /// <summary>
    /// Whether the geometry has no thickness: every vertex on one plane, or fewer than
    /// four distinct vertices. Measured against the piece's own size, so a large thin
    /// panel still counts as solid and a tiny flat card does not.
    /// </summary>
    public static bool IsFlat(WavefrontObj obj)
    {
        List<Vec3> points = obj._vertices.Select(ToVec3).ToList();
        double scale = Extent(points);
        double eps = ReportTolerance((float)scale);

        List<Vec3> distinct = Weld(points, out _);
        if (distinct.Count < 4)
        {
            return true;
        }

        // Best-fit is not needed: pick the widest baseline, then the point furthest off it,
        // and see whether anything at all leaves that plane.
        Vec3 a = distinct[0];
        Vec3 b = distinct[0];
        double best = -1.0;

        foreach (Vec3 p in distinct)
        {
            foreach (Vec3 q in distinct)
            {
                double d = (p - q).LengthSquared;
                if (d > best)
                {
                    best = d;
                    a = p;
                    b = q;
                }
            }
        }

        Vec3 axis = (b - a).Normalized;
        Vec3 c = default;
        best = eps;

        foreach (Vec3 p in distinct)
        {
            Vec3 d = p - a;
            double away = (d - axis * Vec3.Dot(d, axis)).Length;
            if (away > best)
            {
                best = away;
                c = p;
            }
        }

        if (best <= eps)
        {
            return true;   // a line
        }

        Vec3 normal = Vec3.Cross(b - a, c - a).Normalized;
        double offset = -Vec3.Dot(normal, a);

        foreach (Vec3 p in distinct)
        {
            if (Math.Abs(Vec3.Dot(normal, p) + offset) > eps)
            {
                return false;
            }
        }

        return true;
    }

    #region Tolerances

    /// <summary>
    /// The threshold for calling a mesh concave to the user. Deliberately loose: a dent
    /// a ten-thousandth of the model across is modelling noise, not an archway, and
    /// reporting it would be a false alarm on every piece.
    /// </summary>
    private static float ReportTolerance(float scale) => Math.Max(1e-5f, scale * 1e-4f);

    /// <summary>
    /// The threshold the hull algorithm itself decides visibility with. Nine orders of
    /// magnitude below the model size — comfortably above double-precision noise on
    /// float inputs, and far below any real feature.
    /// </summary>
    private static double BuildTolerance(double scale) => Math.Max(1e-12, scale * 1e-9);

    /// <summary>Points closer together than this are the same vertex.</summary>
    private static double WeldTolerance(double scale) => Math.Max(1e-9, scale * 1e-7);

    #endregion

    #region Double-precision vectors

    private readonly struct Vec3
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public double Length => Math.Sqrt(Dot(this, this));

        public double LengthSquared => Dot(this, this);

        public Vec3 Normalized
        {
            get
            {
                double length = Length;
                return length > 0.0 ? new Vec3(X / length, Y / length, Z / length) : this;
            }
        }

        public double this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;
    }

    private static Vec3 ToVec3(Vector4 v) => new(v.X, v.Y, v.Z);

    #endregion

    #region Convexity

    /// <summary>
    /// How far the geometry dips behind its own face planes: 0 for a convex mesh, and the
    /// depth of the deepest dent otherwise.
    ///
    /// Each face plane is oriented by the centroid rather than by winding, so a mesh
    /// exported with reversed normals — which <see cref="ShapeBuilder"/> handles further
    /// down — is not misreported as concave here.
    /// </summary>
    public static float ConvexityDeviation(WavefrontObj obj, out float scale)
    {
        List<Vec3> points = obj._vertices.Select(ToVec3).ToList();
        scale = (float)Extent(points);

        if (points.Count < 4 || obj._faces.Count == 0)
        {
            return 0f;
        }

        Vec3 centre = Centre(points);
        double worst = 0.0;

        foreach (WavefrontObj.Face face in obj._faces)
        {
            if (!FacePlane(points, face._indices, centre, out Vec3 normal, out double offset))
            {
                continue;
            }

            foreach (Vec3 p in points)
            {
                worst = Math.Max(worst, Vec3.Dot(normal, p) + offset);
            }
        }

        return (float)worst;
    }

    /// <summary>
    /// Outward plane of a face, oriented away from <paramref name="interior"/>. Returns
    /// false for a degenerate face, which carries no orientation to speak of.
    /// </summary>
    private static bool FacePlane(List<Vec3> points, List<int> indices, Vec3 interior,
                                  out Vec3 normal, out double offset)
    {
        normal = default;
        offset = 0.0;

        if (indices.Count < 3)
        {
            return false;
        }

        // Newell's method: stable for n-gons and for slightly non-planar faces, where a
        // single cross product of three consecutive vertices can be near-degenerate.
        double nx = 0.0;
        double ny = 0.0;
        double nz = 0.0;
        Vec3 centroid = new(0, 0, 0);

        for (int i = 0; i < indices.Count; i++)
        {
            Vec3 a = points[indices[i]];
            Vec3 b = points[indices[(i + 1) % indices.Count]];

            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
            centroid += a;
        }

        Vec3 accum = new(nx, ny, nz);
        if (accum.LengthSquared <= 0.0)
        {
            return false;
        }

        normal = accum.Normalized;
        centroid *= 1.0 / indices.Count;

        if (Vec3.Dot(normal, interior - centroid) > 0.0)
        {
            normal = new Vec3(-normal.X, -normal.Y, -normal.Z);
        }

        offset = -Vec3.Dot(normal, centroid);
        return true;
    }

    private static Vec3 Centre(List<Vec3> points)
    {
        Vec3 sum = new(0, 0, 0);
        foreach (Vec3 p in points)
        {
            sum += p;
        }
        return sum * (1.0 / points.Count);
    }

    private static double Extent(List<Vec3> points)
    {
        if (points.Count == 0)
        {
            return 1.0;
        }

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (Vec3 p in points)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }

        return new Vec3(maxX - minX, maxY - minY, maxZ - minZ).Length;
    }

    #endregion

    #region Hull construction

    private sealed class HullFace
    {
        public int A;
        public int B;
        public int C;
        public Vec3 Normal;
        public double Offset;
        public bool Dead;

        public double Distance(Vec3 p) => Vec3.Dot(Normal, p) + Offset;
    }

    /// <summary>
    /// Incremental convex hull. Starts from a tetrahedron of extreme points, then for each
    /// remaining point deletes every face it can see and re-bridges the resulting hole to
    /// it. Coplanar triangles are merged back into polygons at the end, so a box comes out
    /// as six quads rather than twelve triangles — which is both what shipped shapes look
    /// like and half as many planes for the runtime to test.
    /// </summary>
    public static WavefrontObj Build(WavefrontObj obj)
    {
        List<Vec3> points = Weld(obj._vertices.Select(ToVec3).ToList(), out double scale);
        double eps = BuildTolerance(scale);

        if (points.Count < 4)
        {
            throw new InvalidDataException(
                $"only {points.Count} distinct vertices; a solid needs at least 4.");
        }

        List<HullFace> faces = InitialTetrahedron(points, eps);

        for (int i = 0; i < points.Count; i++)
        {
            AddPoint(faces, points, i, eps);
        }

        // One pass in index order is not quite enough on its own, so sweep for anything
        // still outside and fold it in. A hull that does not contain its own input is not
        // a hull, it is a shape with a dent in it, and this is the check that guarantees
        // it never ships one: it either converges or throws.
        for (int pass = 0; ; pass++)
        {
            int worst = FurthestOutside(faces, points, eps);
            if (worst < 0)
            {
                break;
            }

            if (pass >= points.Count)
            {
                throw new InvalidDataException("hull did not converge.");
            }

            int before = faces.Count;
            AddPoint(faces, points, worst, eps);

            if (faces.Count == before)
            {
                throw new InvalidDataException(
                    $"hull could not absorb vertex {worst}; the geometry is too degenerate.");
            }
        }

        List<HullFace> live = faces.Where(f => !f.Dead).ToList();
        if (live.Count < 4)
        {
            throw new InvalidDataException("hull collapsed; the vertices are coplanar.");
        }

        List<List<int>> triangles = live.Select(f => new List<int> { f.A, f.B, f.C }).ToList();
        List<List<int>> merged = MergeCoplanar(live, points, scale);

        // Merging is a convenience — six quads instead of twelve triangles — and it must
        // never cost correctness. If the merged polygons no longer bound every point, keep
        // the triangles, which the loop above proved good.
        return Emit(points, Bounds(merged, points, eps) ? merged : triangles);
    }

    /// <summary>
    /// Index of the point lying furthest outside the current hull, or -1 when every point
    /// is on or behind every face.
    /// </summary>
    private static int FurthestOutside(List<HullFace> faces, List<Vec3> points, double eps)
    {
        int worst = -1;
        double furthest = eps;

        for (int i = 0; i < points.Count; i++)
        {
            foreach (HullFace face in faces)
            {
                if (face.Dead)
                {
                    continue;
                }

                double distance = face.Distance(points[i]);
                if (distance > furthest)
                {
                    furthest = distance;
                    worst = i;
                }
            }
        }

        return worst;
    }

    /// <summary>Whether a finished polygon set encloses every point.</summary>
    private static bool Bounds(List<List<int>> polygons, List<Vec3> points, double eps)
    {
        Vec3 centre = Centre(points);

        foreach (List<int> polygon in polygons)
        {
            if (!FacePlane(points, polygon, centre, out Vec3 normal, out double offset))
            {
                continue;
            }

            foreach (Vec3 p in points)
            {
                if (Vec3.Dot(normal, p) + offset > eps)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<Vec3> Weld(List<Vec3> points, out double scale)
    {
        scale = Extent(points);
        double epsSq = WeldTolerance(scale) * WeldTolerance(scale);

        List<Vec3> unique = new(points.Count);
        foreach (Vec3 p in points)
        {
            bool seen = false;
            foreach (Vec3 q in unique)
            {
                if ((p - q).LengthSquared <= epsSq)
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                unique.Add(p);
            }
        }

        return unique;
    }

    private static List<HullFace> InitialTetrahedron(List<Vec3> points, double eps)
    {
        // Widest pair among the axis extremes. Starting from a long edge rather than an
        // arbitrary one keeps the first plane well conditioned.
        int a = 0;
        int b = 0;
        double best = -1.0;

        List<int> extremes = AxisExtremes(points);
        foreach (int i in extremes)
        {
            foreach (int j in extremes)
            {
                double d = (points[i] - points[j]).LengthSquared;
                if (d > best)
                {
                    best = d;
                    a = i;
                    b = j;
                }
            }
        }

        if (best <= eps * eps)
        {
            throw new InvalidDataException("all vertices are at the same position.");
        }

        // Farthest from line ab.
        Vec3 axis = (points[b] - points[a]).Normalized;
        int c = -1;
        best = eps;

        for (int i = 0; i < points.Count; i++)
        {
            Vec3 d = points[i] - points[a];
            double away = (d - axis * Vec3.Dot(d, axis)).Length;
            if (away > best)
            {
                best = away;
                c = i;
            }
        }

        if (c < 0)
        {
            throw new InvalidDataException("all vertices lie on one line.");
        }

        // Farthest from plane abc, either side.
        Vec3 normal = Vec3.Cross(points[b] - points[a], points[c] - points[a]).Normalized;
        double offset = -Vec3.Dot(normal, points[a]);

        int d4 = -1;
        best = eps;

        for (int i = 0; i < points.Count; i++)
        {
            double away = Math.Abs(Vec3.Dot(normal, points[i]) + offset);
            if (away > best)
            {
                best = away;
                d4 = i;
            }
        }

        if (d4 < 0)
        {
            throw new InvalidDataException(
                "all vertices lie on one plane; a flat sheet has no volume to collide with.");
        }

        Vec3 interior = (points[a] + points[b] + points[c] + points[d4]) * 0.25;

        return new List<HullFace>
        {
            MakeFace(points, a, b, c, interior),
            MakeFace(points, a, b, d4, interior),
            MakeFace(points, a, c, d4, interior),
            MakeFace(points, b, c, d4, interior)
        };
    }

    private static List<int> AxisExtremes(List<Vec3> points)
    {
        int[] result = new int[6];

        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].X < points[result[0]].X) result[0] = i;
            if (points[i].X > points[result[1]].X) result[1] = i;
            if (points[i].Y < points[result[2]].Y) result[2] = i;
            if (points[i].Y > points[result[3]].Y) result[3] = i;
            if (points[i].Z < points[result[4]].Z) result[4] = i;
            if (points[i].Z > points[result[5]].Z) result[5] = i;
        }

        return result.Distinct().ToList();
    }

    /// <summary>Builds a face wound so its normal points away from <paramref name="interior"/>.</summary>
    private static HullFace MakeFace(List<Vec3> points, int a, int b, int c, Vec3 interior)
    {
        Vec3 normal = Vec3.Cross(points[b] - points[a], points[c] - points[a]);
        if (normal.LengthSquared <= 0.0)
        {
            throw new InvalidDataException("degenerate triangle while building the hull.");
        }

        normal = normal.Normalized;
        double offset = -Vec3.Dot(normal, points[a]);

        if (Vec3.Dot(normal, interior) + offset > 0.0)
        {
            (b, c) = (c, b);
            normal = new Vec3(-normal.X, -normal.Y, -normal.Z);
            offset = -offset;
        }

        return new HullFace { A = a, B = b, C = c, Normal = normal, Offset = offset };
    }

    private static void AddPoint(List<HullFace> faces, List<Vec3> points, int index, double eps)
    {
        Vec3 p = points[index];

        // Every face the point is in front of. Restricting this to the patch connected to
        // the nearest one leaves any other visible face alive on the far side of the hull,
        // and the point then stays outside that face however many passes it gets.
        List<HullFace> visible = new();
        foreach (HullFace face in faces)
        {
            if (!face.Dead && face.Distance(p) > eps)
            {
                visible.Add(face);
            }
        }

        if (visible.Count == 0)
        {
            // Inside the hull so far, or on its surface. Either way it adds nothing.
            return;
        }

        // Rim of the removed patch: the directed edges whose opposite face stayed behind.
        HashSet<(int, int)> edges = new();
        foreach (HullFace face in visible)
        {
            foreach ((int from, int to) in Edges(face))
            {
                edges.Add((from, to));
            }
        }

        List<(int From, int To)> horizon = new();
        foreach ((int from, int to) in edges)
        {
            if (!edges.Contains((to, from)))
            {
                horizon.Add((from, to));
            }
        }

        // The rim only has to be closed, not a single ring: a visible patch can pinch
        // itself at a vertex, and fanning both loops to the new point still seals it.
        if (horizon.Count < 3 || horizon.Any(e => IsDegenerateBridge(points, e.From, e.To, p)))
        {
            // A bridge with no area would be a face with no plane. Leave the hull closed
            // and let the convergence pass in Build decide what to do.
            return;
        }

        foreach (HullFace face in visible)
        {
            face.Dead = true;
        }

        // The rim edges keep the winding of the faces they came from, so bridging each to
        // the new point reproduces the outward orientation without another interior test.
        foreach ((int from, int to) in horizon)
        {
            Vec3 normal = Vec3.Cross(points[to] - points[from], p - points[from]).Normalized;
            faces.Add(new HullFace
            {
                A = from,
                B = to,
                C = index,
                Normal = normal,
                Offset = -Vec3.Dot(normal, points[from])
            });
        }
    }

    private static IEnumerable<(int From, int To)> Edges(HullFace face)
    {
        yield return (face.A, face.B);
        yield return (face.B, face.C);
        yield return (face.C, face.A);
    }

    private static bool IsDegenerateBridge(List<Vec3> points, int from, int to, Vec3 p) =>
        Vec3.Cross(points[to] - points[from], p - points[from]).LengthSquared <= 0.0;

    #endregion

    #region Coplanar merging

    /// <summary>
    /// Groups triangles that share a plane and traces the outline of each group, turning
    /// the hull's triangle soup back into the polygon faces the geometry actually has.
    /// A group whose outline is not a single closed loop is left as triangles.
    /// </summary>
    private static List<List<int>> MergeCoplanar(List<HullFace> faces, List<Vec3> points, double scale)
    {
        // Tight on purpose. Triangles that came from one real face agree to within
        // double-precision noise; anything looser starts merging genuinely distinct faces
        // of a finely tessellated hull, and the flat polygon that comes out does not bound
        // the geometry the triangles did — a hull with a dent in it, which is exactly what
        // this class exists to prevent.
        const double NormalTolerance = 1.0 - 1e-9;
        double eps = Math.Max(1e-9, scale * 1e-8);

        Dictionary<(int, int), int> owner = new();
        for (int i = 0; i < faces.Count; i++)
        {
            foreach ((int from, int to) in Edges(faces[i]))
            {
                owner[(from, to)] = i;
            }
        }

        bool[] taken = new bool[faces.Count];
        List<List<int>> polygons = new();

        for (int i = 0; i < faces.Count; i++)
        {
            if (taken[i])
            {
                continue;
            }

            // Grow across shared edges only. Two coplanar patches on opposite sides of the
            // hull are not one face, and stitching them into a single outline is nonsense.
            HullFace seed = faces[i];
            List<HullFace> group = new() { seed };
            Stack<int> pending = new();
            pending.Push(i);
            taken[i] = true;

            while (pending.Count > 0)
            {
                foreach ((int from, int to) in Edges(faces[pending.Pop()]))
                {
                    if (!owner.TryGetValue((to, from), out int neighbour) || taken[neighbour])
                    {
                        continue;
                    }

                    // Compared against the seed rather than the running group, so a long
                    // chain of barely-different faces cannot drift off the original plane.
                    if (Vec3.Dot(seed.Normal, faces[neighbour].Normal) < NormalTolerance ||
                        Math.Abs(seed.Offset - faces[neighbour].Offset) > eps)
                    {
                        continue;
                    }

                    taken[neighbour] = true;
                    group.Add(faces[neighbour]);
                    pending.Push(neighbour);
                }
            }

            List<int> loop = group.Count == 1 ? null : Outline(group);

            // Last check before committing: every corner of the merged outline has to sit
            // on the seed's plane. If the merge lost anything, keep the triangles.
            if (loop != null && !IsFlat(loop, points, seed, eps))
            {
                loop = null;
            }

            if (loop == null)
            {
                foreach (HullFace face in group)
                {
                    polygons.Add(new List<int> { face.A, face.B, face.C });
                }
                continue;
            }

            DropCollinear(loop, points, eps);
            if (loop.Count >= 3)
            {
                polygons.Add(loop);
            }
        }

        return polygons;
    }

    private static bool IsFlat(List<int> loop, List<Vec3> points, HullFace plane, double eps)
    {
        foreach (int index in loop)
        {
            if (Math.Abs(plane.Distance(points[index])) > eps)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks the boundary of a coplanar group. Returns null when the boundary is not one
    /// closed loop — a ring-shaped group, say, which no single polygon can express.
    /// </summary>
    private static List<int> Outline(List<HullFace> group)
    {
        HashSet<(int, int)> all = new();
        foreach (HullFace face in group)
        {
            foreach ((int from, int to) in Edges(face))
            {
                all.Add((from, to));
            }
        }

        Dictionary<int, int> next = new();
        foreach ((int from, int to) in all)
        {
            if (all.Contains((to, from)))
            {
                continue;   // shared with another triangle in the group: interior
            }

            if (!next.TryAdd(from, to))
            {
                return null;   // a vertex with two exits — not a simple loop
            }
        }

        if (next.Count < 3)
        {
            return null;
        }

        int start = next.Keys.First();
        List<int> loop = new() { start };

        for (int current = next[start]; current != start; current = next[current])
        {
            if (!next.ContainsKey(current) || loop.Count > next.Count)
            {
                return null;
            }
            loop.Add(current);
        }

        return loop.Count == next.Count ? loop : null;
    }

    /// <summary>
    /// Removes vertices that sit in the middle of a straight edge. They are on the hull
    /// but are not corners of it, and each one costs a byte of index and a slot against
    /// the 256-vertex limit for nothing.
    /// </summary>
    private static void DropCollinear(List<int> loop, List<Vec3> points, double eps)
    {
        for (int i = 0; i < loop.Count && loop.Count > 3; )
        {
            Vec3 previous = points[loop[(i - 1 + loop.Count) % loop.Count]];
            Vec3 current = points[loop[i]];
            Vec3 following = points[loop[(i + 1) % loop.Count]];

            Vec3 edge = following - previous;
            double length = edge.Length;

            if (length <= eps)
            {
                i++;
                continue;
            }

            double away = Vec3.Cross(edge * (1.0 / length), current - previous).Length;
            if (away <= eps)
            {
                loop.RemoveAt(i);
                if (i > 0)
                {
                    i--;   // the neighbour may now be collinear too
                }
                continue;
            }

            i++;
        }
    }

    #endregion

    /// <summary>Repacks the hull into an OBJ object holding only the vertices it uses.</summary>
    private static WavefrontObj Emit(List<Vec3> points, List<List<int>> polygons)
    {
        WavefrontObj hull = new();
        Dictionary<int, int> remap = new();

        foreach (List<int> polygon in polygons)
        {
            List<int> indices = new(polygon.Count);
            foreach (int index in polygon)
            {
                if (!remap.TryGetValue(index, out int local))
                {
                    local = hull._vertices.Count;
                    remap[index] = local;

                    Vec3 p = points[index];
                    // W matches what the OBJ reader produces, so downstream code cannot
                    // tell a hulled piece from an authored one.
                    hull._vertices.Add(new Vector4((float)p.X, (float)p.Y, (float)p.Z, 0.5f));
                }

                indices.Add(local);
            }

            hull._faces.Add(new WavefrontObj.Face(indices.ToArray()));
        }

        return hull;
    }
}
