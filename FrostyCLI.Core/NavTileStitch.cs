using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Joins the polygon meshes of a tiled Recast build into ONE conforming polygon surface -- what a NavPower graph
    /// needs, since the compiler (and the runtime) link two areas only through an edge both of them own.
    ///
    /// Recast builds each tile on its own region partition, so where two tiles meet their polygons end on the shared
    /// border line at different points: tile A may run one edge along z 0..10 where tile B has edges 0..4 and 4..10.
    /// Detour hides that with portal links between overlapping border edges; a NavPower image cannot. Stitching does
    /// what Detour's link search does, then makes it explicit in the geometry:
    ///  1. vertices with identical voxel coordinates are one vertex;
    ///  2. border edges (both ends on the same tile border line) of DIFFERENT tiles are paired when their spans along
    ///     the line overlap and their heights agree within yTolerance voxels at both ends of the overlap (so a floor
    ///     meets its own floor, never a ledge of another layer above or below it);
    ///  3. for each pair, endpoints at the same border point are welded, and every endpoint of one edge that lies
    ///     strictly inside the other is inserted into it (the T-junction), so both sides own the same segments.
    /// Vertices of the same tile are never inserted into each other: one tile's mesh is already conforming, and a
    /// long border edge of a sloped sliver can pass within tolerance of an unrelated polygon of another layer.
    /// Polygons stay convex (inserted vertices are collinear with their edge) and may exceed Recast's 6 vertices (a
    /// NavPower area holds up to 127).
    ///
    /// Pure logic over integer voxel coordinates, shared by navgen (net8, links this file) and the CLI tests.
    /// </summary>
    public static class NavTileStitch
    {
        public sealed class Tile
        {
            /// <summary>Vertices in GLOBAL voxel coordinates (x, y, z) -- x/z on the build grid, y in cell heights.</summary>
            public readonly List<int[]> Verts = new List<int[]>();
            /// <summary>Polygons as indices into Verts, wound as Recast wrote them.</summary>
            public readonly List<int[]> Polys = new List<int[]>();
            /// <summary>
            /// Optional: for a polygon's border edge (key: polygon index &lt;&lt; 8 | edge index), the height (global voxel y)
            /// of the polygon's own region in each voxel column along the edge, from its low end; -1 where the region has
            /// no span. A polygon edge is a straight line between two vertices, but a long border edge over sloped ground
            /// is not the ground: two polygons of different layers can have edges whose interpolated heights agree. The
            /// region's real span heights tell the layers apart.
            /// </summary>
            public readonly Dictionary<long, int[]> EdgeHeights = new Dictionary<long, int[]>();
        }

        public sealed class Result
        {
            public readonly List<int[]> Verts = new List<int[]>();
            public readonly List<int[]> Polys = new List<int[]>();
            public int Welded, Inserted, BorderEdges, PairedEdges, DroppedOverlaps;
        }

        internal sealed class BorderEdge
        {
            public int Tile, Poly, Edge;     // polygon index in the global list, edge k = (p[k], p[k+1])
            public int A, B;                 // global vertex ids of the edge's ends
            public int Along;                // 0: runs along x (z is the border), 2: runs along z (x is the border)
            public int Lo, Hi;               // span along the line
            public int[] Heights;            // optional real heights per voxel column from Lo
        }

        /// <param name="tiles">the tiles' polygon meshes</param>
        /// <param name="tileSizeX">tile size in voxels along x (tile borders lie at multiples of it)</param>
        /// <param name="tileSizeZ">tile size in voxels along z</param>
        /// <param name="yTolerance">largest height difference, in voxels, of two border edges that are one floor</param>
        public static Result Stitch(IList<Tile> tiles, int tileSizeX, int tileSizeZ, int yTolerance)
        {
            var result = new Result();
            // 1. exact weld over all tiles; polygons in global ids
            var exact = new Dictionary<long, int>();
            var polys = new List<int[]>();
            var polyTile = new List<int>();
            var polyLocal = new List<int>();
            for (int t = 0; t < tiles.Count; t++)
            {
                Tile tile = tiles[t];
                var ids = new int[tile.Verts.Count];
                for (int i = 0; i < ids.Length; i++)
                {
                    int[] v = tile.Verts[i];
                    long key = Key(v[0], v[1], v[2]);
                    int id;
                    if (!exact.TryGetValue(key, out id))
                    {
                        id = result.Verts.Count;
                        exact[key] = id;
                        result.Verts.Add(new[] { v[0], v[1], v[2] });
                    }
                    ids[i] = id;
                }
                for (int lp = 0; lp < tile.Polys.Count; lp++)
                {
                    int[] p = tile.Polys[lp];
                    var q = new int[p.Length];
                    for (int k = 0; k < p.Length; k++) q[k] = ids[p[k]];
                    polys.Add(q);
                    polyTile.Add(t);
                    polyLocal.Add(lp);
                }
            }
            List<int[]> V = result.Verts;

            // 2. border edges, by line
            var lines = new Dictionary<long, List<BorderEdge>>();
            for (int pi = 0; pi < polys.Count; pi++)
            {
                int[] p = polys[pi];
                for (int k = 0; k < p.Length; k++)
                {
                    int a = p[k], b = p[(k + 1) % p.Length];
                    int[] va = V[a], vb = V[b];
                    BorderEdge e = null;
                    if (va[2] == vb[2] && va[0] != vb[0] && IsBorder(va[2], tileSizeZ))
                        e = new BorderEdge { Along = 0, Lo = Math.Min(va[0], vb[0]), Hi = Math.Max(va[0], vb[0]) };
                    else if (va[0] == vb[0] && va[2] != vb[2] && IsBorder(va[0], tileSizeX))
                        e = new BorderEdge { Along = 2, Lo = Math.Min(va[2], vb[2]), Hi = Math.Max(va[2], vb[2]) };
                    if (e == null) continue;
                    e.Tile = polyTile[pi]; e.Poly = pi; e.Edge = k; e.A = a; e.B = b;
                    int[] hs;
                    if (tiles[e.Tile].EdgeHeights.TryGetValue(((long)polyLocal[pi] << 8) | (uint)k, out hs) && hs != null && hs.Length == e.Hi - e.Lo) e.Heights = hs;
                    long line = e.Along == 0 ? (1L << 40) | (uint)va[2] : (2L << 40) | (uint)va[0];
                    List<BorderEdge> list;
                    if (!lines.TryGetValue(line, out list)) lines[line] = list = new List<BorderEdge>();
                    list.Add(e);
                    result.BorderEdges++;
                }
            }

            // 3. pair overlapping edges of different tiles; join them along every run of columns where they are one
            //    floor. A Recast edge on a seam can span two levels (an entrance ramp: 369 m at one end, 390 m at the
            //    other), so a pair is not accepted or refused whole: each run of agreeing columns gets vertices at its
            //    ends on both edges, and everything inside the run is shared.
            var inserts = new Dictionary<long, List<int>>();       // (poly << 8 | edge) -> vertices to insert
            var created = new Dictionary<long, List<int>>();       // (line, column) -> vertices made at run ends
            var unions = new List<int[]>();
            foreach (KeyValuePair<long, List<BorderEdge>> kv in lines)
            {
                List<BorderEdge> list = kv.Value;
                list.Sort((x, y) => x.Lo.CompareTo(y.Lo));
                for (int i = 0; i < list.Count; i++)
                {
                    BorderEdge e = list[i];
                    for (int j = i + 1; j < list.Count && list[j].Lo < e.Hi; j++)
                    {
                        BorderEdge f = list[j];
                        if (f.Tile == e.Tile) continue;
                        int s0 = Math.Max(e.Lo, f.Lo), s1 = Math.Min(e.Hi, f.Hi);
                        if (s1 <= s0) continue;
                        List<int[]> runs = Runs(V, e, f, s0, s1, yTolerance);
                        if (runs.Count == 0) continue;
                        result.PairedEdges++;
                        foreach (int[] run in runs) ConnectRun(V, kv.Key, e, f, run[0], run[1], inserts, created, unions, yTolerance);
                    }
                }
            }
            var parent = new int[V.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            foreach (int[] u in unions) Union(parent, u[0], u[1]);

            // 4. rebuild polygons: welded ids, inserted vertices in edge order, no repeats
            var outTile = new List<int>();
            Func<int, int> find = null;
            find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            for (int i = 0; i < parent.Length; i++) if (find(i) != i) result.Welded++;
            for (int pi = 0; pi < polys.Count; pi++)
            {
                int[] p = polys[pi];
                var q = new List<int>(p.Length + 2);
                for (int k = 0; k < p.Length; k++)
                {
                    Push(q, find(p[k]));
                    List<int> add;
                    if (!inserts.TryGetValue(((long)pi << 8) | (uint)k, out add)) continue;
                    int[] va = V[p[k]], vb = V[p[(k + 1) % p.Length]];
                    int along = va[2] == vb[2] ? 0 : 2;
                    bool asc = vb[along] > va[along];
                    add.Sort((x, y) => asc ? V[x][along].CompareTo(V[y][along]) : V[y][along].CompareTo(V[x][along]));
                    foreach (int m in add) { int r = find(m); if (Push(q, r)) result.Inserted++; }
                }
                while (q.Count > 1 && q[0] == q[q.Count - 1]) q.RemoveAt(q.Count - 1);
                if (q.Count >= 3 && new HashSet<int>(q).Count == q.Count) { result.Polys.Add(q.ToArray()); outTile.Add(polyTile[pi]); }
            }
            WeldPositions(result.Polys, outTile, V);
            result.DroppedOverlaps = DropOverlaps(result.Polys, outTile, V);
            return result;
        }

        // Vertices at one voxel position are one vertex (run ends created on one seam can land on another edge's
        // vertex): the compiler welds equal positions, so the overlap repair has to see the same topology.
        internal static void WeldPositions(List<int[]> polys, List<int> tileOf, List<int[]> V)
        {
            var first = new Dictionary<long, int>();
            var map = new int[V.Count];
            for (int i = 0; i < V.Count; i++)
            {
                long key = Key(V[i][0], V[i][1], V[i][2]);
                int f;
                if (!first.TryGetValue(key, out f)) first[key] = f = i;
                map[i] = f;
            }
            for (int p = polys.Count - 1; p >= 0; p--)
            {
                var q = new List<int>(polys[p].Length);
                foreach (int v in polys[p]) { int m = map[v]; if (q.Count == 0 || q[q.Count - 1] != m) q.Add(m); }
                while (q.Count > 1 && q[0] == q[q.Count - 1]) q.RemoveAt(q.Count - 1);
                if (q.Count >= 3 && new HashSet<int>(q).Count == q.Count) polys[p] = q.ToArray();
                else { polys.RemoveAt(p); tileOf.RemoveAt(p); }
            }
        }

        /// <summary>
        /// Recast can leave a sliver region of a tile under a larger polygon of the same tile where both touch the tile
        /// border (the border vertices between them are removed, so the larger polygon's border edge runs over the
        /// sliver): after stitching, three or four polygons then own one border segment. Keeps, per segment and tile,
        /// the largest polygon (plan area) and drops the others -- the sliver's ground is under the larger polygon.
        /// Returns the number of polygons dropped.
        /// </summary>
        internal static int DropOverlaps(List<int[]> polys, List<int> tileOf, List<int[]> V)
        {
            var seg = new Dictionary<long, List<int>>();
            for (int p = 0; p < polys.Count; p++)
            {
                int[] q = polys[p];
                for (int k = 0; k < q.Length; k++)
                {
                    int a = q[k], b = q[(k + 1) % q.Length];
                    long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    List<int> l;
                    if (!seg.TryGetValue(key, out l)) seg[key] = l = new List<int>(2);
                    l.Add(p);
                }
            }
            var drop = new HashSet<int>();
            foreach (List<int> users in seg.Values)
            {
                if (users.Count < 3) continue;
                var best = new Dictionary<int, int>();          // tile -> largest polygon
                foreach (int p in users)
                {
                    int b;
                    if (!best.TryGetValue(tileOf[p], out b) || PlanArea(polys[p], V) > PlanArea(polys[b], V)) best[tileOf[p]] = p;
                }
                var keep = new List<int>(best.Values);
                keep.Sort((x, y) => PlanArea(polys[y], V).CompareTo(PlanArea(polys[x], V)));
                if (keep.Count > 2) keep.RemoveRange(2, keep.Count - 2);
                foreach (int p in users) if (!keep.Contains(p)) drop.Add(p);
            }
            if (drop.Count == 0) return 0;
            var kept = new List<int[]>(polys.Count - drop.Count);
            var keptTiles = new List<int>(polys.Count - drop.Count);
            for (int p = 0; p < polys.Count; p++) if (!drop.Contains(p)) { kept.Add(polys[p]); keptTiles.Add(tileOf[p]); }
            polys.Clear(); polys.AddRange(kept);
            tileOf.Clear(); tileOf.AddRange(keptTiles);
            return drop.Count;
        }

        internal static double PlanArea(int[] poly, List<int[]> V)
        {
            return Math.Abs(SignedPlanArea2(poly, V)) / 2;
        }

        /// <summary>Twice the plan (x, z) area with its sign: the sum of x[i] * z[i+1] - x[i+1] * z[i]. Exact on voxel
        /// coordinates, so zero means every corner lies on one line seen from above.</summary>
        public static double SignedPlanArea2(int[] poly, List<int[]> V)
        {
            double a = 0;
            for (int i = 0, k = poly.Length - 1; i < poly.Length; k = i++)
                a += (double)V[poly[k]][0] * V[poly[i]][2] - (double)V[poly[i]][0] * V[poly[k]][2];
            return a;
        }

        /// <summary>
        /// The polygon in the corner order a NavPower area is stored in, or null when it has no plan area.
        /// Every area of the shipped navmeshes checked -- DICE's four Hoth_01 streams, 44,397 areas -- runs with a
        /// NEGATIVE <see cref="SignedPlanArea2"/>, so its first two corners and basis vertex give the area a +Y normal.
        /// navgen used to reverse Recast's polygons for "+Y normals", which put all 71,730 areas of the Hoth 2015
        /// navmesh the other way round: every area normal pointed down, and the runtime's walk over an area's edges
        /// (32-byte edge records from area+0x48, count = flags1 &amp; 0x7F) spun forever at game+0x78A2C43 -- the
        /// intermittent GameSimulation stall of every Hoth build that carried a generated navmesh. A polygon whose
        /// corners all lie on one line seen from above (2 in that build) has no normal at all and is dropped.
        /// </summary>
        public static int[] NavPowerOrder(int[] poly, List<int[]> V)
        {
            double s = SignedPlanArea2(poly, V);
            if (s == 0) return null;
            if (s < 0) return poly;
            var r = (int[])poly.Clone();
            Array.Reverse(r);
            return r;
        }

        /// <summary>
        /// A polygon (in NavPower corner order) as FLAT convex pieces, the only kind of area DICE ships: every corner of
        /// every area of the four Hoth_01 streams lies on the plane through its first two corners and basis vertex
        /// (largest height error 0.02 m over 44,397 areas). Recast polygons follow the ground instead -- half the Hoth
        /// 2015 areas were off their plane, 2,719 by more than 2 m (a seam across a trench carries corners 1.9 m apart
        /// in height 0.4 m apart in plan) -- and the game's area walk stalled (GameSimulation watchdog) or wrote through
        /// a null neighbour (game+0x78A6B24) on exactly such areas.
        /// Corners at one plan position (a vertical edge: a stitched run end over another floor) keep the one nearer the
        /// polygon's mean height. A flat polygon is returned whole; any other is ear-clipped into triangles, which are
        /// flat by construction, and adjacent pieces are merged again while the union stays convex and flat. No corner is
        /// added, so every boundary segment -- and with it every link to a neighbour -- is kept. Empty: nothing left.
        /// </summary>
        /// <param name="cs">cell size (plan units of V), <paramref name="ch"/> cell height, <paramref name="tolerance"/> in world units</param>
        public static List<int[]> PlanarPieces(int[] poly, List<int[]> V, double cs, double ch, double tolerance)
        {
            var result = new List<int[]>();
            var p = new List<int>(poly);
            // vertical edges: two consecutive corners at one plan position
            bool changed = true;
            while (changed && p.Count >= 3)
            {
                changed = false;
                for (int k = 0; k < p.Count; k++)
                {
                    int a = p[k], b = p[(k + 1) % p.Count];
                    if (V[a][0] != V[b][0] || V[a][2] != V[b][2]) continue;
                    double mean = 0;
                    foreach (int v in p) mean += V[v][1];
                    mean /= p.Count;
                    p.RemoveAt(Math.Abs(V[a][1] - mean) > Math.Abs(V[b][1] - mean) ? k : (k + 1) % p.Count);
                    changed = true;
                    break;
                }
            }
            if (p.Count < 3 || SignedPlanArea2(p.ToArray(), V) == 0) return result;
            if (Flat(p, V, cs, ch, tolerance)) { result.Add(p.ToArray()); return result; }

            // ear clipping: every piece has positive plan area and the polygon's orientation
            double sign = Math.Sign(SignedPlanArea2(p.ToArray(), V));
            var pieces = new List<List<int>>();
            var rest = new List<int>(p);
            while (rest.Count > 3)
            {
                int ear = -1;
                for (int k = 0; k < rest.Count && ear < 0; k++)
                {
                    int a = rest[(k + rest.Count - 1) % rest.Count], b = rest[k], c = rest[(k + 1) % rest.Count];
                    if (Math.Sign(SignedPlanArea2(new[] { a, b, c }, V)) == sign) ear = k;
                }
                if (ear < 0) break;                                   // only collinear corners left
                int ea = rest[(ear + rest.Count - 1) % rest.Count], eb = rest[ear], ec = rest[(ear + 1) % rest.Count];
                pieces.Add(new List<int> { ea, eb, ec });
                rest.RemoveAt(ear);
            }
            if (rest.Count == 3 && Math.Sign(SignedPlanArea2(rest.ToArray(), V)) == sign) pieces.Add(rest);

            // merge adjacent pieces across a shared diagonal while the union is convex and flat
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < pieces.Count && !merged; i++)
                    for (int j = i + 1; j < pieces.Count && !merged; j++)
                    {
                        List<int> u = Union(pieces[i], pieces[j]);
                        if (u == null || !Convex(u, V, sign) || !Flat(u, V, cs, ch, tolerance)) continue;
                        pieces[i] = u;
                        pieces.RemoveAt(j);
                        merged = true;
                    }
            }
            foreach (List<int> piece in pieces) result.Add(piece.ToArray());
            return result;
        }

        // the corners' largest height error, in world units, from the plane through corners 0, 1 and the corner
        // furthest from the line through them (NavArea's basis vertex)
        internal static bool Flat(IList<int> p, List<int[]> V, double cs, double ch, double tolerance)
        {
            if (p.Count <= 3) return true;
            Func<int, double[]> W = v => new[] { V[v][0] * cs, V[v][1] * ch, V[v][2] * cs };
            double[] a = W(p[0]), b = W(p[1]);
            double[] ab = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double abl = ab[0] * ab[0] + ab[1] * ab[1] + ab[2] * ab[2];
            if (abl == 0) return false;
            int basis = 2;
            double best = -1;
            for (int k = 0; k < p.Count; k++)
            {
                double[] q = W(p[k]);
                double t = ((q[0] - a[0]) * ab[0] + (q[1] - a[1]) * ab[1] + (q[2] - a[2]) * ab[2]) / abl;
                double dx = q[0] - a[0] - t * ab[0], dy = q[1] - a[1] - t * ab[1], dz = q[2] - a[2] - t * ab[2];
                double d = dx * dx + dy * dy + dz * dz;
                if (d > best) { best = d; basis = k; }
            }
            if (basis < 2) basis = 2;
            double[] c = W(p[basis]);
            double[] ac = { c[0] - a[0], c[1] - a[1], c[2] - a[2] };
            double nx = ab[1] * ac[2] - ab[2] * ac[1], ny = ab[2] * ac[0] - ab[0] * ac[2], nz = ab[0] * ac[1] - ab[1] * ac[0];
            if (Math.Abs(ny) < 1e-9) return false;                   // a vertical plane: no height over the area
            for (int k = 0; k < p.Count; k++)
            {
                double[] q = W(p[k]);
                double y = a[1] - (nx * (q[0] - a[0]) + nz * (q[2] - a[2])) / ny;
                if (Math.Abs(q[1] - y) > tolerance) return false;
            }
            return true;
        }

        // two pieces sharing exactly one edge (in opposite directions) as one corner loop; null otherwise
        private static List<int> Union(List<int> a, List<int> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                int u = a[i], v = a[(i + 1) % a.Count];
                int j = b.IndexOf(v);
                if (j < 0 || b[(j + 1) % b.Count] != u) continue;
                var r = new List<int>();
                for (int k = 0; k < a.Count; k++) r.Add(a[(i + 1 + k) % a.Count]);        // v ... u
                for (int k = 2; k < b.Count; k++) r.Add(b[(j + k) % b.Count]);            // b after u, up to before v
                return r.Distinct().Count() == r.Count ? r : null;
            }
            return null;
        }

        // convex in plan with the polygon's orientation (collinear corners allowed)
        private static bool Convex(List<int> p, List<int[]> V, double sign)
        {
            for (int k = 0; k < p.Count; k++)
            {
                double s = SignedPlanArea2(new[] { p[(k + p.Count - 1) % p.Count], p[k], p[(k + 1) % p.Count] }, V);
                if (s != 0 && Math.Sign(s) != sign) return false;
            }
            return true;
        }

        /// <summary>Column runs [r0, r1) of the overlap [s0, s1) where e and f are one floor (heights within yTol), at
        /// least two columns long. Real span heights where both edges carry them, else the straight edges' heights.</summary>
        internal static List<int[]> Runs(List<int[]> V, BorderEdge e, BorderEdge f, int s0, int s1, int yTol)
        {
            var runs = new List<int[]>();
            bool real = e.Heights != null && f.Heights != null;
            int start = -1;
            for (int c = s0; c <= s1; c++)
            {
                bool ok = false;
                if (c < s1)
                {
                    if (real)
                    {
                        int he = e.Heights[c - e.Lo], hf = f.Heights[c - f.Lo];
                        ok = he >= 0 && hf >= 0 && Math.Abs(he - hf) <= yTol;
                    }
                    else ok = Math.Abs(HeightAt(V, e, c + 0.5) - HeightAt(V, f, c + 0.5)) <= yTol;
                }
                if (ok && start < 0) start = c;
                if (!ok && start >= 0)
                {
                    if (c - start >= 2) runs.Add(new[] { start, c });
                    start = -1;
                }
            }
            return runs;
        }

        // Joins e and f over the columns [r0, r1): every point that bounds a shared segment -- the run's two ends and
        // the edges' own ends inside it -- becomes one vertex inserted into both edges (ends already there are welded).
        private static void ConnectRun(List<int[]> V, long line, BorderEdge e, BorderEdge f, int r0, int r1,
                                       Dictionary<long, List<int>> inserts, Dictionary<long, List<int>> created, List<int[]> unions, int yTol)
        {
            var points = new SortedSet<int> { r0, r1 };
            foreach (int v in new[] { e.A, e.B, f.A, f.B })
            {
                int c = V[v][e.Along];
                if (c > r0 && c < r1) points.Add(c);
            }
            foreach (int c in points)
            {
                int ve = EndAt(V, e, c), vf = EndAt(V, f, c);
                int v;
                if (ve >= 0 && vf >= 0)
                {
                    if (ve != vf && Math.Abs(V[ve][1] - V[vf][1]) <= yTol) unions.Add(new[] { ve, vf });
                    continue;                                            // both edges already end here
                }
                if (ve >= 0) v = ve;
                else if (vf >= 0) v = vf;
                else
                {
                    // a run end inside both edges: one new vertex at the floor's height there
                    int col = c < r1 ? c : c - 1;
                    double y = (FloorAt(V, e, col) + FloorAt(V, f, col)) / 2;
                    v = MakeVertex(V, created, line, e.Along, c, (int)Math.Round(y), V[e.A], yTol);
                }
                if (c > e.Lo && c < e.Hi && v != ve) Insert(inserts, e, v);
                if (c > f.Lo && c < f.Hi && v != vf) Insert(inserts, f, v);
            }
        }

        private static int EndAt(List<int[]> V, BorderEdge e, int c)
        {
            if (V[e.A][e.Along] == c) return e.A;
            if (V[e.B][e.Along] == c) return e.B;
            return -1;
        }

        // the floor height at a column: the region's real span height, else the straight edge's
        private static double FloorAt(List<int[]> V, BorderEdge e, int col)
        {
            if (e.Heights != null && col >= e.Lo && col < e.Hi && e.Heights[col - e.Lo] >= 0) return e.Heights[col - e.Lo];
            return HeightAt(V, e, col + 0.5);
        }

        private static int MakeVertex(List<int[]> V, Dictionary<long, List<int>> created, long line, int along, int c, int y, int[] onLine, int yTol)
        {
            long key = line * 4194304L + c;
            List<int> list;
            if (!created.TryGetValue(key, out list)) created[key] = list = new List<int>();
            foreach (int id in list) if (Math.Abs(V[id][1] - y) <= yTol) return id;
            int[] v = along == 0 ? new[] { c, y, onLine[2] } : new[] { onLine[0], y, c };
            V.Add(v);
            list.Add(V.Count - 1);
            return V.Count - 1;
        }

        private static void Insert(Dictionary<long, List<int>> inserts, BorderEdge e, int v)
        {
            long key = ((long)e.Poly << 8) | (uint)e.Edge;
            List<int> list;
            if (!inserts.TryGetValue(key, out list)) inserts[key] = list = new List<int>();
            if (!list.Contains(v)) list.Add(v);
        }

        // e takes f's endpoints: welded where they sit on e's ends, inserted where they fall strictly inside e
        private static void Connect(List<int[]> V, BorderEdge e, BorderEdge f, int[] parent, Dictionary<long, List<int>> inserts, int yTol)
        {
            foreach (int v in new[] { f.A, f.B })
            {
                int c = V[v][e.Along];
                if (c < e.Lo || c > e.Hi) continue;
                if (c == e.Lo || c == e.Hi)
                {
                    int end = V[e.A][e.Along] == c ? e.A : e.B;
                    if (end != v && Math.Abs(V[end][1] - V[v][1]) <= yTol) Union(parent, end, v);
                    continue;
                }
                long key = ((long)e.Poly << 8) | (uint)e.Edge;
                List<int> list;
                if (!inserts.TryGetValue(key, out list)) inserts[key] = list = new List<int>();
                if (!list.Contains(v)) list.Add(v);
            }
        }

        // Are e and f one floor over their overlap [s0, s1)? With real heights on both sides, every column where both
        // regions have a span must agree within the tolerance (adjacent columns across the seam: a 45 degree slope
        // differs by one cell size); without them, the straight edges' heights at both ends of the overlap.
        internal static bool SameFloor(List<int[]> V, BorderEdge e, BorderEdge f, int s0, int s1, int yTol)
        {
            if (e.Heights != null && f.Heights != null)
            {
                int compared = 0;
                for (int c = s0; c < s1; c++)
                {
                    int he = e.Heights[c - e.Lo], hf = f.Heights[c - f.Lo];
                    if (he < 0 || hf < 0) continue;
                    if (Math.Abs(he - hf) > yTol) return false;
                    compared++;
                }
                if (compared > 0) return true;
            }
            return Math.Abs(HeightAt(V, e, s0) - HeightAt(V, f, s0)) <= yTol && Math.Abs(HeightAt(V, e, s1) - HeightAt(V, f, s1)) <= yTol;
        }

        private static double HeightAt(List<int[]> V, BorderEdge e, double s)
        {
            int[] a = V[e.A], b = V[e.B];
            double t = (double)(s - a[e.Along]) / (b[e.Along] - a[e.Along]);
            return a[1] + t * (b[1] - a[1]);
        }

        private static void Union(int[] parent, int a, int b)
        {
            while (parent[a] != a) a = parent[a];
            while (parent[b] != b) b = parent[b];
            if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
        }

        private static bool Push(List<int> q, int v)
        {
            if (q.Count > 0 && q[q.Count - 1] == v) return false;
            q.Add(v);
            return true;
        }

        private static bool IsBorder(int c, int size) { return size > 0 && ((c % size) + size) % size == 0; }
        private static long Key(int x, int y, int z) { return ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF); }
    }
}
