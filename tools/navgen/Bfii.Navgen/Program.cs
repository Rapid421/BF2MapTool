// VENDORED from BfiiMapTools src\Bfii.Navmesh\tools\Bfii.Navgen\Program.cs (sha256:F50ECB17C3CC0F61).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Bfii.Navmesh.Compile;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Primitives;
using Bfii.Navmesh.Obj;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace Bfii.Navgen
{
    // navgen build <geometry.obj> <donor.blob> <out.blob> [options]
    //
    // A BFII navmesh from a level's walkable world: the triangle soup `frostycli navmesh geometry` exports (terrain +
    // placed meshes, world space, Y up) is voxelised by Recast (DotRecast, MIT): walkable spans with agent-height
    // clearance, eroded by the agent radius, split into regions, traced into contours and turned into convex polygons
    // that share their vertices and edges -- a manifold surface by construction. Those polygons are compiled into the
    // donor's graph by Bfii.Navmesh (donor-seeded: version, byte order and agent header inherited), written, and
    // self-checked (the blob must round-trip byte for byte). Agent radius, height and step default to the donor graph's.
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length < 4 || args[0] != "build")
            {
                Console.Error.WriteLine(
                    "usage: navgen build <geometry.obj> <donor.blob> <out.blob> [--region x0,z0,x1,z1] [--cs 0.25] [--ch 0.1]\n" +
                    "         [--slope 45] [--radius r] [--height h] [--climb c] [--region-min 8] [--edge-max 12] [--edge-error 1.3]\n" +
                    "         [--graph N] [--polys out_polys.obj] [--double-sided 1]");
                return 2;
            }
            string geoPath = args[1], donorPath = args[2], outPath = args[3];
            var opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 4; i + 1 < args.Length; i += 2) opt[args[i]] = args[i + 1];
            float F(string k, float d) => opt.TryGetValue(k, out var v) ? float.Parse(v, CultureInfo.InvariantCulture) : d;

            byte[] donorPayload = File.ReadAllBytes(donorPath);
            NavMeshImage image = NavMeshImage.Read(donorPayload);
            int graphIndex = (int)F("--graph", 0);
            var graph = image.NavSet.Graphs[graphIndex];
            float radius = F("--radius", (float)graph.AgentRadius), height = F("--height", (float)graph.AgentHeight);
            float climb = F("--climb", (float)graph.StepHeight);
            float cs = F("--cs", 0.25f), ch = F("--ch", 0.1f), slope = F("--slope", 45f);
            Console.WriteLine("donor           " + Path.GetFullPath(donorPath) + " graph " + graphIndex +
                              string.Format(CultureInfo.InvariantCulture, " (agent radius {0}, height {1}, step {2})", radius, height, climb));

            var geom = RcSampleInputGeomProvider.LoadFile(geoPath);
            // --double-sided 1: render meshes are often single-sided -- on Kamino the hangar's ceiling, seen from below,
            // is the only face of the deck above it, and Recast walks only faces that point up. Near-horizontal faces
            // that point down are turned over, so the top of such a slab is floor, as in the game's solid collision.
            if (F("--double-sided", 0f) > 0)
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
                geom = new RcSampleInputGeomProvider(vs, fs);
                Console.WriteLine("double-sided    " + flipped + " downward face(s) turned up");
            }
            RcVec3f bmin = geom.GetMeshBoundsMin(), bmax = geom.GetMeshBoundsMax();
            if (opt.TryGetValue("--region", out var reg))
            {
                var r = reg.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                bmin = new RcVec3f(r[0], bmin.Y, r[1]); bmax = new RcVec3f(r[2], bmax.Y, r[3]);
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "geometry        {0}: {1} vertices, {2} triangles; bounds ({3:0.#}, {4:0.#}, {5:0.#}) .. ({6:0.#}, {7:0.#}, {8:0.#})",
                geoPath, geom.vertices.Length / 3, geom.faces.Length / 3, bmin.X, bmin.Y, bmin.Z, bmax.X, bmax.Y, bmax.Z));

            var cfg = new RcConfig(RcPartition.WATERSHED, cs, ch, slope, height, radius, climb,
                (int)F("--region-min", 8), (int)F("--region-merge", 20), F("--edge-max", 12f), F("--edge-error", 1.3f), 6,
                6f, 1f, true, true, true, new RcAreaModification(0x3f), false);
            var bcfg = new RcBuilderConfig(cfg, bmin, bmax);
            RcBuilderResult built = new RcBuilder().Build(geom, bcfg, false);
            RcPolyMesh pm = built.Mesh;
            Console.WriteLine("recast          " + pm.npolys + " polygon(s), " + pm.nverts + " vertices (cell " + cs + " x " + ch + ")");
            if (pm.npolys == 0) { Console.Error.WriteLine("no walkable polygons"); return 1; }

            // --seeds x,y,z;x,y,z...: keep only the polygons connected to a seed (spawn points, a street) -- rooftops and
            // other islands no agent can reach are dropped, as DICE's navmeshes leave them out
            int nvpAll = pm.nvp;
            bool[] keep = Enumerable.Repeat(true, pm.npolys).ToArray();
            // --min-component m2: drop connected pieces smaller than this (rooftops, ledges). DICE's navmeshes have none:
            // a bot that spawns off the mesh is snapped onto the main network, while one that spawns on an island can
            // never leave it (TNAV3: 8 bots stuck on 21-287 m2 islands at spawn spots DICE's Blast mesh does not cover).
            float minComponent = F("--min-component", 0f);
            if (minComponent > 0)
            {
                var comp = Components(pm, out int ncomp);
                var area = new double[ncomp];
                for (int p = 0; p < pm.npolys; p++) area[comp[p]] += PolyArea(pm, p);
                for (int p = 0; p < pm.npolys; p++) keep[p] = area[comp[p]] >= minComponent;
                Console.WriteLine("components      " + ncomp + ", kept " + area.Count(a => a >= minComponent) + " of at least " + minComponent +
                                  " m2 (" + keep.Count(k => k) + " polygon(s), largest " + area.Max().ToString("0", CultureInfo.InvariantCulture) + " m2)");
            }
            if (opt.TryGetValue("--seeds", out var seedArg))
            {
                var comp = Enumerable.Repeat(-1, pm.npolys).ToArray();
                int ncomp = 0;
                for (int p = 0; p < pm.npolys; p++)
                {
                    if (comp[p] >= 0) continue;
                    var stack = new Stack<int>(); stack.Push(p); comp[p] = ncomp;
                    while (stack.Count > 0)
                    {
                        int q = stack.Pop();
                        for (int j = 0; j < nvpAll; j++)
                        {
                            if (pm.polys[q * 2 * nvpAll + j] == RcRecast.RC_MESH_NULL_IDX) break;
                            int nb = pm.polys[q * 2 * nvpAll + nvpAll + j];
                            if (nb == RcRecast.RC_MESH_NULL_IDX || (nb & 0x8000) != 0) continue;
                            if (comp[nb] < 0) { comp[nb] = ncomp; stack.Push(nb); }
                        }
                    }
                    ncomp++;
                }
                var wanted = new HashSet<int>();
                foreach (var s in seedArg.Split(';'))
                {
                    var c = s.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    // the polygon under the seed whose height is nearest (within 3 m)
                    int best = -1; double bestDy = 3;
                    for (int p = 0; p < pm.npolys; p++)
                    {
                        var poly = new List<(double X, double Y, double Z)>();
                        for (int j = 0; j < nvpAll; j++)
                        {
                            int v = pm.polys[p * 2 * nvpAll + j];
                            if (v == RcRecast.RC_MESH_NULL_IDX) break;
                            poly.Add((pm.bmin.X + pm.verts[v * 3] * pm.cs, pm.bmin.Y + pm.verts[v * 3 + 1] * pm.ch, pm.bmin.Z + pm.verts[v * 3 + 2] * pm.cs));
                        }
                        bool inside = false;
                        for (int a = 0, b = poly.Count - 1; a < poly.Count; b = a++)
                            if ((poly[a].Z > c[2]) != (poly[b].Z > c[2]) &&
                                c[0] < (poly[b].X - poly[a].X) * (c[2] - poly[a].Z) / (poly[b].Z - poly[a].Z) + poly[a].X) inside = !inside;
                        if (!inside) continue;
                        double dy = Math.Abs(poly.Average(v => v.Y) - c[1]);
                        if (dy < bestDy) { bestDy = dy; best = p; }
                    }
                    if (best >= 0) wanted.Add(comp[best]);
                    else Console.WriteLine("seed " + s + ": no polygon under it");
                }
                for (int p = 0; p < pm.npolys; p++) keep[p] = wanted.Contains(comp[p]);
                Console.WriteLine("reachable       " + keep.Count(k => k) + " of " + pm.npolys + " polygon(s) in " + wanted.Count + " of " + ncomp + " component(s) under the seeds");
            }

            // the polygon mesh as an OBJ (world coordinates; winding as the shipped navmeshes: area normal +Y)
            string polysPath = opt.TryGetValue("--polys", out var pp) ? pp : Path.ChangeExtension(outPath, ".polys.obj");
            using (var w = new StreamWriter(polysPath))
            {
                w.WriteLine("# navgen polygons from " + geoPath);
                for (int i = 0; i < pm.nverts; i++)
                    w.WriteLine(string.Format(CultureInfo.InvariantCulture, "v {0:0.####} {1:0.####} {2:0.####}",
                        pm.bmin.X + pm.verts[i * 3] * pm.cs, pm.bmin.Y + pm.verts[i * 3 + 1] * pm.ch, pm.bmin.Z + pm.verts[i * 3 + 2] * pm.cs));
                int nvp = pm.nvp;
                for (int p = 0; p < pm.npolys; p++)
                {
                    if (!keep[p]) continue;
                    var idx = new List<int>();
                    for (int j = 0; j < nvp; j++)
                    {
                        int v = pm.polys[p * 2 * nvp + j];
                        if (v == RcRecast.RC_MESH_NULL_IDX) break;
                        idx.Add(v);
                    }
                    if (idx.Count < 3) continue;
                    // Recast polygons wind clockwise seen from above (+Y); reverse them for +Y area normals
                    idx.Reverse();
                    w.WriteLine("f " + string.Join(" ", idx.Select(v => (v + 1).ToString(CultureInfo.InvariantCulture))));
                }
            }

            ObjMesh mesh = ObjParser.ParseFile(polysPath);
            // Island ids: every shipped navmesh checked stamps all of its areas with the sentinel 262143 (`navmesh
            // inspect`: Endor 5,577 areas, Tatooine 861, Kessel 964, Crait 1,998 Skirmish areas, one id each), so the
            // sentinel is DICE's own format, not the community importer's defect the compiler warns about.
            var options = new NavCompileOptions { GraphIndex = graphIndex, UseLegacyIslandSentinel = true };
            NavCompileResult result = NavGraphCompiler.Compile(image, mesh, options);
            Console.WriteLine("compiled        " + result);
            foreach (var warn in result.Warnings)
                Console.WriteLine(warn.StartsWith("LEGACY MODE", StringComparison.Ordinal)
                    ? "islands         every area stamped " + NavArea.SentinelIslandNum + ", as in every shipped DICE navmesh checked"
                    : "WARNING         " + warn);
            var order = image.ChecksumCalibration != null && image.ChecksumCalibration.DecidedOrder.HasValue
                ? image.ChecksumCalibration.DecidedOrder.Value : NavChecksum.DefaultWordOrder;
            byte[] output = image.Write(order);
            File.WriteAllBytes(outPath, output);
            var check = NavRoundTrip.Run(output);
            Console.WriteLine("wrote           " + Path.GetFullPath(outPath) + " (" + output.Length + " bytes); self-check " + (check.IsByteExact ? "BYTE EXACT" : "FAILED"));
            return check.IsByteExact ? 0 : 1;
        }

        private static int[] Components(RcPolyMesh pm, out int ncomp)
        {
            int nvp = pm.nvp;
            var comp = Enumerable.Repeat(-1, pm.npolys).ToArray();
            ncomp = 0;
            for (int p = 0; p < pm.npolys; p++)
            {
                if (comp[p] >= 0) continue;
                var stack = new Stack<int>(); stack.Push(p); comp[p] = ncomp;
                while (stack.Count > 0)
                {
                    int q = stack.Pop();
                    for (int j = 0; j < nvp; j++)
                    {
                        if (pm.polys[q * 2 * nvp + j] == RcRecast.RC_MESH_NULL_IDX) break;
                        int nb = pm.polys[q * 2 * nvp + nvp + j];
                        if (nb == RcRecast.RC_MESH_NULL_IDX || (nb & 0x8000) != 0) continue;
                        if (comp[nb] < 0) { comp[nb] = ncomp; stack.Push(nb); }
                    }
                }
                ncomp++;
            }
            return comp;
        }

        private static double PolyArea(RcPolyMesh pm, int p)
        {
            int nvp = pm.nvp;
            var xs = new List<double>(); var zs = new List<double>();
            for (int j = 0; j < nvp; j++)
            {
                int v = pm.polys[p * 2 * nvp + j];
                if (v == RcRecast.RC_MESH_NULL_IDX) break;
                xs.Add(pm.verts[v * 3] * pm.cs); zs.Add(pm.verts[v * 3 + 2] * pm.cs);
            }
            double a = 0;
            for (int i = 0, k = xs.Count - 1; i < xs.Count; k = i++) a += xs[k] * zs[i] - xs[i] * zs[k];
            return Math.Abs(a) / 2;
        }
    }
}
