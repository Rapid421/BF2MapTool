// CLI-OWNED (not vendored): the tiled build path of navgen, added for maps too large for one Recast heightfield.
// tools\sync-navgen.ps1 only rewrites the vendored files; this file and Entry below are the CLI's own.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bfii.Navmesh.Compile;
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Obj;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using FrostyCLI.Core;

namespace Bfii.Navgen
{
    // Entry point of navgen.dll (StartupObject in the CLI-owned csproj): `build ... --tile-size N` runs the tiled build
    // below; everything else goes to the vendored Program.Main unchanged.
    public static class Entry
    {
        public static int Main(string[] args)
        {
            if (args.Length >= 4 && args[0] == "build" && Array.IndexOf(args, "--tile-size") > 0) return TiledBuild.Run(args);
            return Program.Main(args);
        }
    }

    // navgen build <geometry.obj> <donor.blob> <out.blob> --tile-size N [same options as the solo build]
    //
    // One Recast heightfield over a whole Walker Assault map (3 x 2.3 km at 0.2 m: 174 M columns) does not fit in
    // memory, so the map is voxelised in tiles of N x N cells (DotRecast BuildTiles, each tile with its own border so
    // the erosion and region rules see across the seam), and the tiles' polygon meshes are joined into one conforming
    // surface by NavTileStitch (weld on the voxel grid, split border edges at the neighbour's vertices). The result is
    // compiled into the donor graph exactly as the solo build does, and self-checked byte for byte.
    public static class TiledBuild
    {
        public static int Run(string[] args)
        {
            string geoPath = args[1], donorPath = args[2], outPath = args[3];
            var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 4; i + 1 < args.Length; i += 2) opt[args[i]] = args[i + 1];
            float F(string k, float d) => opt.TryGetValue(k, out var v) ? float.Parse(v, CultureInfo.InvariantCulture) : d;
            var clock = Stopwatch.StartNew();

            byte[] donorPayload = File.ReadAllBytes(donorPath);
            NavMeshImage image = NavMeshImage.Read(donorPayload);
            int graphIndex = (int)F("--graph", 0);
            var graph = image.NavSet.Graphs[graphIndex];
            float radius = F("--radius", (float)graph.AgentRadius), height = F("--height", (float)graph.AgentHeight);
            float climb = F("--climb", (float)graph.StepHeight);
            float cs = F("--cs", 0.25f), ch = F("--ch", 0.1f), slope = F("--slope", 45f);
            int tileSize = (int)F("--tile-size", 512);
            int threads = (int)F("--threads", Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2)));
            Console.WriteLine("donor           " + Path.GetFullPath(donorPath) + " graph " + graphIndex +
                              string.Format(CultureInfo.InvariantCulture, " (agent radius {0}, height {1}, step {2})", radius, height, climb));

            var geom = RcSampleInputGeomProvider.LoadFile(geoPath);
            if (F("--double-sided", 0f) > 0) geom = DoubleSided(geom, slope);
            RcVec3f bmin = geom.GetMeshBoundsMin(), bmax = geom.GetMeshBoundsMax();
            // --tile-origin x,z: put a tile corner there (the grid is extended below the geometry's minimum), so a
            // dense multi-level zone -- a base with floors stacked over ramps -- can sit inside ONE tile, away from
            // every seam; seams then fall on open ground, where stitching is exact.
            if (opt.TryGetValue("--tile-origin", out var originArg))
            {
                var o = originArg.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                float span = F("--tile-size", 512) * F("--cs", 0.25f);
                float ox = o[0] - (float)Math.Ceiling((o[0] - bmin.X) / span) * span, oz = o[1] - (float)Math.Ceiling((o[1] - bmin.Z) / span) * span;
                bmin = new RcVec3f(ox, bmin.Y, oz);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "tile grid       corner at ({0:0.##}, {1:0.##}); tiles every {2:0.#} m", o[0], o[1], span));
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "geometry        {0}: {1} vertices, {2} triangles; bounds ({3:0.#}, {4:0.#}, {5:0.#}) .. ({6:0.#}, {7:0.#}, {8:0.#}) [{9:0.0} s]",
                geoPath, geom.vertices.Length / 3, geom.faces.Length / 3, bmin.X, bmin.Y, bmin.Z, bmax.X, bmax.Y, bmax.Z, clock.Elapsed.TotalSeconds));

            // region sizes: the solo build passes side lengths in cells (8 -> 64 cells); the tiled constructor takes m2
            float regionMin = F("--region-min", 8), regionMerge = F("--region-merge", 20);
            var cfg = new RcConfig(true, tileSize, tileSize, RcConfig.CalcBorder(radius, cs), RcPartition.WATERSHED, cs, ch, slope, height, radius, climb,
                regionMin * regionMin * cs * cs, regionMerge * regionMerge * cs * cs, F("--edge-max", 12f), F("--edge-error", 1.3f), 6,
                6f, 1f, true, true, true, new RcAreaModification(0x3f), false);
            // own tile loop (BuildTile per tile, intermediates kept just long enough to read each border edge's real
            // span heights from the compact heightfield -- see NavTileStitch.Tile.EdgeHeights)
            RcRecast.CalcTileCount(bmin, bmax, cs, tileSize, tileSize, out int tw, out int td);
            var builder = new RcBuilder();
            var progress = new DotRecast.Core.RcAtomicInteger(0);
            var tileResults = new NavTileStitch.Tile[tw * td];
            int totalPolys = 0, withPolys = 0, heightMisses = 0;
            object sync = new object();
            Parallel.For(0, tw * td, new ParallelOptions { MaxDegreeOfParallelism = threads }, idx =>
            {
                int tx = idx % tw, tz = idx / tw;
                RcBuilderResult b = builder.BuildTile(geom, cfg, bmin, bmax, tx, tz, progress, tw * td, true);
                RcPolyMesh pm = b.Mesh;
                if (pm == null || pm.npolys == 0) return;
                NavTileStitch.Tile t = ToTile(pm, b.CompactHeightfield, bmin, cs, ch, out int misses);
                tileResults[idx] = t;
                lock (sync) { totalPolys += pm.npolys; withPolys++; heightMisses += misses; }
            });
            var tiles = tileResults.Where(t => t != null).ToList();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "recast tiles    {0} tile(s) of {1} x {1} cells ({2:0.#} m), {3} with polygons, {4} polygon(s); border columns without the region's span: {5} [{6:0.0} s]",
                tw * td, tileSize, tileSize * cs, withPolys, totalPolys, heightMisses, clock.Elapsed.TotalSeconds));
            int yTol = Math.Max(1, (int)Math.Floor(climb / ch));
            NavTileStitch.Result st = NavTileStitch.Stitch(tiles, tileSize, tileSize, yTol);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "stitch          {0} polygon(s), {1} vertices; {2} border edge(s), {3} pair(s) across seams; {4} vertices welded, {5} T-junction vertices inserted, {6} overlapping sliver(s) dropped [{7:0.0} s]",
                st.Polys.Count, st.Verts.Count, st.BorderEdges, st.PairedEdges, st.Welded, st.Inserted, st.DroppedOverlaps, clock.Elapsed.TotalSeconds));

            // connectivity over shared edges, for --min-component / --seeds
            int[] comp = Components(st, out int ncomp);
            bool[] keep = Enumerable.Repeat(true, st.Polys.Count).ToArray();
            Func<int[], double[][]> world = p => p.Select(v => new double[] { bmin.X + st.Verts[v][0] * (double)cs, bmin.Y + st.Verts[v][1] * (double)ch, bmin.Z + st.Verts[v][2] * (double)cs }).ToArray();
            var area = new double[ncomp];
            for (int p = 0; p < st.Polys.Count; p++) area[comp[p]] += Area(world(st.Polys[p]));
            float minComponent = F("--min-component", 0f);
            if (minComponent > 0)
            {
                for (int p = 0; p < st.Polys.Count; p++) keep[p] = area[comp[p]] >= minComponent;
                Console.WriteLine("components      " + ncomp + ", kept " + area.Count(a => a >= minComponent) + " of at least " + minComponent +
                                  " m2 (" + keep.Count(k => k) + " polygon(s), largest " + area.Max().ToString("0", CultureInfo.InvariantCulture) + " m2)");
            }
            else Console.WriteLine("components      " + ncomp + " (largest " + (ncomp > 0 ? area.Max().ToString("0", CultureInfo.InvariantCulture) : "0") + " m2 of " +
                                   area.Sum().ToString("0", CultureInfo.InvariantCulture) + " m2)");
            if (opt.TryGetValue("--seeds", out var seedArg))
            {
                var wanted = new HashSet<int>();
                foreach (var s in seedArg.Split(';'))
                {
                    var c = s.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    int best = -1; double bestDy = 3;
                    for (int p = 0; p < st.Polys.Count; p++)
                    {
                        double[][] poly = world(st.Polys[p]);
                        if (!Inside(poly, c[0], c[2])) continue;
                        double dy = Math.Abs(poly.Average(v => v[1]) - c[1]);
                        if (dy < bestDy) { bestDy = dy; best = p; }
                    }
                    if (best >= 0) wanted.Add(comp[best]);
                    else Console.WriteLine("seed " + s + ": no polygon under it");
                }
                for (int p = 0; p < st.Polys.Count; p++) keep[p] = keep[p] && wanted.Contains(comp[p]);
                Console.WriteLine("reachable       " + keep.Count(k => k) + " of " + st.Polys.Count + " polygon(s) in " + wanted.Count + " of " + ncomp + " component(s) under the seeds");
            }

            // polygon OBJ in world coordinates, every polygon in the corner order DICE's areas are stored in (negative
            // plan signed area = +Y area normal; NavTileStitch.NavPowerOrder decides it per polygon instead of assuming
            // Recast's winding), split into FLAT convex pieces as every shipped area is (NavTileStitch.PlanarPieces);
            // polygons with no plan area are dropped
            string polysPath = opt.TryGetValue("--polys", out var pp) ? pp : Path.ChangeExtension(outPath, ".polys.obj");
            double flatTolerance = F("--flat-tolerance", 0.02f);
            int maxEdges = 0, flat = 0, split = 0, pieces = 0;
            using (var w = new StreamWriter(polysPath))
            {
                w.WriteLine("# navgen tiled polygons from " + geoPath);
                foreach (int[] v in st.Verts)
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "v {0:0.####} {1:0.####} {2:0.####}", bmin.X + v[0] * cs, bmin.Y + v[1] * ch, bmin.Z + v[2] * cs));
                for (int p = 0; p < st.Polys.Count; p++)
                {
                    if (!keep[p]) continue;
                    int[] idx = NavTileStitch.NavPowerOrder(st.Polys[p], st.Verts);
                    if (idx == null) { flat++; continue; }
                    List<int[]> parts = NavTileStitch.PlanarPieces(idx, st.Verts, cs, ch, flatTolerance);
                    if (parts.Count == 0) { flat++; continue; }
                    if (parts.Count > 1 || parts[0].Length != idx.Length) { split++; pieces += parts.Count; }
                    foreach (int[] part in parts)
                    {
                        if (part.Length > NavArea.MaxEdgeCount) { Console.Error.WriteLine("polygon with " + part.Length + " edges exceeds a NavPower area's " + NavArea.MaxEdgeCount); return 1; }
                        maxEdges = Math.Max(maxEdges, part.Length);
                        w.WriteLine("f " + string.Join(" ", part.Select(v => (v + 1).ToString(CultureInfo.InvariantCulture))));
                    }
                }
            }
            if (flat > 0) Console.WriteLine("flat            " + flat + " polygon(s) with no plan area dropped (corners on one line seen from above)");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "planar          {0} polygon(s) off their plane by more than {1} m split into {2} flat piece(s)", split, flatTolerance, pieces));
            ObjMesh mesh = ObjParser.ParseFile(polysPath);
            var options = new NavCompileOptions { GraphIndex = graphIndex, UseLegacyIslandSentinel = true };
            NavCompileResult result = NavGraphCompiler.Compile(image, mesh, options);
            Console.WriteLine("compiled        " + result + "; largest area " + maxEdges + " edges");
            foreach (var warn in result.Warnings)
                Console.WriteLine(warn.StartsWith("LEGACY MODE", StringComparison.Ordinal)
                    ? "islands         every area stamped " + NavArea.SentinelIslandNum + ", as in every shipped DICE navmesh checked"
                    : "WARNING         " + warn);
            var order = image.ChecksumCalibration != null && image.ChecksumCalibration.DecidedOrder.HasValue
                ? image.ChecksumCalibration.DecidedOrder.Value : NavChecksum.DefaultWordOrder;
            byte[] output = image.Write(order);
            File.WriteAllBytes(outPath, output);
            var check = NavRoundTrip.Run(output);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "wrote           {0} ({1} bytes); self-check {2} [{3:0.0} s]",
                Path.GetFullPath(outPath), output.Length, check.IsByteExact ? "BYTE EXACT" : "FAILED", clock.Elapsed.TotalSeconds));
            return check.IsByteExact ? 0 : 1;
        }

        // A tile's polygon mesh in global voxel coordinates, plus the real height of each polygon's region in every
        // column along its tile-border edges (the compact heightfield's span carrying the polygon's region id).
        private static NavTileStitch.Tile ToTile(RcPolyMesh pm, RcCompactHeightfield chf, RcVec3f bmin, float cs, float ch, out int misses)
        {
            misses = 0;
            double ox = (pm.bmin.X - bmin.X) / cs, oz = (pm.bmin.Z - bmin.Z) / cs, oy = (pm.bmin.Y - bmin.Y) / ch;
            int gx = (int)Math.Round(ox), gz = (int)Math.Round(oz), gy = (int)Math.Round(oy);
            if (Math.Abs(ox - gx) > 1e-3 || Math.Abs(oz - gz) > 1e-3)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "tile at ({0}, {1}) is off the voxel grid", ox, oz));
            var t = new NavTileStitch.Tile();
            for (int i = 0; i < pm.nverts; i++) t.Verts.Add(new[] { gx + pm.verts[i * 3], gy + pm.verts[i * 3 + 1], gz + pm.verts[i * 3 + 2] });
            int nvp = pm.nvp, bs = pm.borderSize;
            int w = chf != null ? chf.width - 2 * chf.borderSize : 0, h = chf != null ? chf.height - 2 * chf.borderSize : 0;
            int cy = chf != null ? (int)Math.Round((chf.bmin.Y - bmin.Y) / ch) : 0;
            for (int p = 0; p < pm.npolys; p++)
            {
                var idx = new List<int>(nvp);
                for (int j = 0; j < nvp; j++)
                {
                    int v = pm.polys[p * 2 * nvp + j];
                    if (v == RcRecast.RC_MESH_NULL_IDX) break;
                    idx.Add(v);
                }
                if (idx.Count < 3) continue;
                int local = t.Polys.Count;
                t.Polys.Add(idx.ToArray());
                if (chf == null) continue;
                int reg = pm.regs[p];
                for (int j = 0; j < idx.Count; j++)
                {
                    int flag = pm.polys[p * 2 * nvp + nvp + j];
                    if (flag == RcRecast.RC_MESH_NULL_IDX || (flag & 0x8000) == 0) continue;
                    int dir = flag & 0xf;
                    int va = idx[j], vb = idx[(j + 1) % idx.Count];
                    bool alongZ = dir == 0 || dir == 2;           // x = 0 / x = w: the edge runs along z
                    int a0 = alongZ ? pm.verts[va * 3 + 2] : pm.verts[va * 3], b0 = alongZ ? pm.verts[vb * 3 + 2] : pm.verts[vb * 3];
                    int lo = Math.Min(a0, b0), hi = Math.Max(a0, b0);
                    if (hi <= lo) continue;
                    var hs = new int[hi - lo];
                    for (int c = lo; c < hi; c++)
                    {
                        int cx = alongZ ? (dir == 0 ? 0 : w - 1) : c, cz = alongZ ? c : (dir == 3 ? 0 : h - 1);
                        int col = (cx + chf.borderSize) + (cz + chf.borderSize) * chf.width;
                        int found = -1;
                        if (col >= 0 && col < chf.cells.Length)
                        {
                            RcCompactCell cell = chf.cells[col];
                            for (int s = cell.index; s < cell.index + cell.count; s++)
                                if (chf.spans[s].reg == reg) { found = cy + chf.spans[s].y; break; }
                            if (found < 0)
                            {
                                // the region does not reach this column (contour simplification): the span nearest the
                                // straight edge's height, within 1.5 m, is the floor the edge stands for
                                int ya = pm.verts[va * 3 + 1], yb = pm.verts[vb * 3 + 1];
                                double lin = ya + (yb - ya) * ((c + 0.5 - a0) / (double)(b0 - a0));
                                int bestDy = (int)Math.Ceiling(1.5 / ch) + 1;
                                for (int s = cell.index; s < cell.index + cell.count; s++)
                                {
                                    int dy = (int)Math.Abs(chf.spans[s].y - lin);
                                    if (dy < bestDy) { bestDy = dy; found = cy + chf.spans[s].y; }
                                }
                            }
                        }
                        if (found < 0) misses++;
                        hs[c - lo] = found;
                    }
                    t.EdgeHeights[((long)local << 8) | (uint)j] = hs;
                }
            }
            return t;
        }

        private static RcSampleInputGeomProvider DoubleSided(RcSampleInputGeomProvider geom, float slope)
        {
            float[] vs = geom.vertices;
            int[] fs = (int[])geom.faces.Clone();
            double cosMax = Math.Cos(slope * Math.PI / 180.0);
            int flipped = 0;
            for (int t = 0; t + 2 < fs.Length; t += 3)
            {
                int a = fs[t] * 3, b = fs[t + 1] * 3, c = fs[t + 2] * 3;
                double ux = vs[b] - vs[a], uy = vs[b + 1] - vs[a + 1], uz = vs[b + 2] - vs[a + 2];
                double wx = vs[c] - vs[a], wy = vs[c + 1] - vs[a + 1], wz = vs[c + 2] - vs[a + 2];
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len > 0 && -ny / len >= cosMax) { int tmp = fs[t + 1]; fs[t + 1] = fs[t + 2]; fs[t + 2] = tmp; flipped++; }
            }
            Console.WriteLine("double-sided    " + flipped + " downward face(s) turned up");
            return new RcSampleInputGeomProvider(vs, fs);
        }

        private static int[] Components(NavTileStitch.Result st, out int ncomp)
        {
            var parent = Enumerable.Range(0, st.Polys.Count).ToArray();
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            var seg = new Dictionary<long, int>();
            for (int p = 0; p < st.Polys.Count; p++)
            {
                int[] q = st.Polys[p];
                for (int e = 0; e < q.Length; e++)
                {
                    int a = q[e], b = q[(e + 1) % q.Length];
                    long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    if (seg.TryGetValue(key, out int other)) parent[Find(p)] = Find(other);
                    else seg[key] = p;
                }
            }
            var ids = new Dictionary<int, int>();
            var comp = new int[st.Polys.Count];
            for (int p = 0; p < comp.Length; p++)
            {
                int r = Find(p);
                if (!ids.TryGetValue(r, out int c)) ids[r] = c = ids.Count;
                comp[p] = c;
            }
            ncomp = ids.Count;
            return comp;
        }

        private static double Area(double[][] poly)
        {
            double a = 0;
            for (int i = 0, k = poly.Length - 1; i < poly.Length; k = i++) a += poly[k][0] * poly[i][2] - poly[i][0] * poly[k][2];
            return Math.Abs(a) / 2;
        }

        private static bool Inside(double[][] poly, double x, double z)
        {
            bool inside = false;
            for (int a = 0, b = poly.Length - 1; a < poly.Length; b = a++)
                if ((poly[a][2] > z) != (poly[b][2] > z) && x < (poly[b][0] - poly[a][0]) * (z - poly[a][2]) / (poly[b][2] - poly[a][2]) + poly[a][0]) inside = !inside;
            return inside;
        }
    }
}
