using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using MeshSetPlugin.Resources;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten chart-plan <level> --match <mesh substring>... --out plan.json`: a new lightmap atlas for a level's static
    /// mesh placements. Every placement whose mesh path contains a --match string (static group members, object
    /// references, prefabs; EnlightenKeys.Collect) gets a chart: a w x h texel rect sized so its radiosity uvs cover the
    /// placed surface at the wanted texel density -- side = density x sqrt(world area / radiosity-uv area), 4-aligned,
    /// clamped -- with the density falling off outside the play area (density x R / (R + d), never under --far-density;
    /// d = horizontal distance of the placement's bounding box from the play circle). Charts are shelf-packed, tallest
    /// first, into rows of --width texels with --pad texels between them; when the atlas grows past --max-height every
    /// density is scaled down and the pack is redone. Records carry the lightmap key (LightmapKey.Compute, BFII_EXACT) and the
    /// uv transform as the END stores it (w/W, 0, 0, h/H, x/W, y/H). Same placement and key twice = one record.
    /// </summary>
    public static class EnlightenChartPlan
    {
        sealed class Item
        {
            public EnlightenKeys.Placement P;
            public double World, UvArea, Dist;
            public int W, H, X, Y;
            public float[] Bmin, Bmax;
        }

        public static int Execute(string[] args, ExportLogger logger)
        {
            string level = null, outFile = null;
            var match = new List<string>();
            double density = 8.0, far = 1.0, cx = 0, cz = 0, r = 200, scaleMax = 1.0;
            int width = 4096, maxHeight = 4096, pad = 2, depth = 3, minSide = 8, maxSide = 1024;
            bool hostRecords = false;
            string sidesFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (a == "--match" && i + 1 < args.Length) match.Add(args[++i]);
                else if (a == "--density" && i + 1 < args.Length) density = D(args[++i]);
                else if (a == "--far-density" && i + 1 < args.Length) far = D(args[++i]);
                else if (a == "--play" && i + 1 < args.Length) { var v = args[++i].Split(','); cx = D(v[0]); cz = D(v[1]); r = D(v[2]); }
                else if (a == "--width" && i + 1 < args.Length) width = int.Parse(args[++i]);
                else if (a == "--max-height" && i + 1 < args.Length) maxHeight = int.Parse(args[++i]);
                else if (a == "--pad" && i + 1 < args.Length) pad = int.Parse(args[++i]);
                else if (a == "--depth" && i + 1 < args.Length) depth = int.Parse(args[++i]);
                else if (a == "--min-side" && i + 1 < args.Length) minSide = int.Parse(args[++i]);
                else if (a == "--max-side" && i + 1 < args.Length) maxSide = int.Parse(args[++i]);
                else if (a == "--also-host-records") hostRecords = true;
                else if (a == "--sides" && i + 1 < args.Length) sidesFile = args[++i];
                else if (!a.StartsWith("--")) level = a;
                else { Console.Error.WriteLine("enlighten chart-plan: unknown option " + a); return 1; }
            }
            if (level == null || outFile == null || match.Count == 0)
            {
                Console.Error.WriteLine("Usage: frostycli enlighten chart-plan <level> --match <mesh substring>... --out plan.json [--density t/m] [--far-density t/m] " +
                                        "[--play x,z,radius] [--width n] [--max-height n] [--pad n] [--depth n] [--min-side n] [--max-side n]");
                return 1;
            }
            // --also-host-records: placements that already have a record in the level's current bake (a host's lobby /
            // end-of-round props placed with override None) keep a chart in the new atlas, or they would draw black
            var hostKeys = new HashSet<uint>();
            if (hostRecords)
                foreach (var hb in EnlightenRelight.FindBakes(level, logger))
                    if (EnlightenRelight.LoadBake(hb)) foreach (var mi in hb.Db.MeshInstances) hostKeys.Add(mi.Hash);
            var placements = new List<EnlightenKeys.Placement>();
            int hostKept = 0;
            EnlightenKeys.Walk(level, depth, (name, dump) =>
            {
                foreach (var p in EnlightenKeys.Collect(name, dump))
                {
                    if (match.Any(m => p.MeshPath.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) placements.Add(p);
                    else if (hostKeys.Contains(p.Key)) { placements.Add(p); hostKept++; }
                }
            });
            placements = placements.GroupBy(p => p.Key).Select(g => g.First()).ToList();
            var geo = new Dictionary<string, Tuple<float[], float[], int[]>>(StringComparer.OrdinalIgnoreCase);
            var items = new List<Item>();
            int noUv = 0;
            foreach (var p in placements)
            {
                Tuple<float[], float[], int[]> g;
                if (!geo.TryGetValue(p.MeshPath, out g)) geo[p.MeshPath] = g = ReadMesh(p.MeshPath);
                if (g == null) { noUv++; continue; }
                var it = new Item { P = p };
                Measure(g, p, it);
                if (it.UvArea < 1e-9 || it.World < 1e-6) { noUv++; continue; }
                double dx = Math.Max(0, Math.Max(it.Bmin[0] - cx, cx - it.Bmax[0])), dz = Math.Max(0, Math.Max(it.Bmin[2] - cz, cz - it.Bmax[2]));
                it.Dist = Math.Max(0, Math.Sqrt(dx * dx + dz * dz) - r);
                items.Add(it);
            }
            // --sides <json {"<mesh path>": side}>: a mesh whose radiosity uvs were packed for an exact square of that many
            // texels (uv = pixel / side; Mygeeto MBL work/mbl/charts.py) gets exactly that chart -- the chart generator's
            // padding and 4-texel block alignment only hold at its own resolution. Such charts ignore the density rule and
            // are never rescaled; the atlas must fit them (an error otherwise).
            var nativeSides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (sidesFile != null)
                foreach (var kv in JObject.Parse(File.ReadAllText(sidesFile))) nativeSides[kv.Key] = (int)kv.Value;
            // one spare 8 x 8 rect no record owns: `flux-scene --emitters` parks the (degenerate) lightmap uvs of
            // emitter / occluder-only geometry there
            var spare = new Item { W = 8, H = 8 };
            double scale = scaleMax;
            int height = 0;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                foreach (var it in items)
                {
                    double dens = Math.Max(far, density * r / (r + it.Dist)) * scale;
                    double side = dens * Math.Sqrt(it.World / it.UvArea);
                    int s = (int)Math.Ceiling(side / 4.0) * 4;
                    it.W = it.H = Math.Max(minSide, Math.Min(maxSide, s));
                    int native;
                    if (nativeSides.TryGetValue(it.P.MeshPath, out native)) it.W = it.H = native;
                }
                var packed = new List<Item>(items) { spare };
                height = Pack(packed, width, pad);
                if (height <= maxHeight) break;
                if (nativeSides.Count > 0 && items.All(i => nativeSides.ContainsKey(i.P.MeshPath)))
                {
                    Console.Error.WriteLine("enlighten chart-plan: the --sides charts need " + width + "x" + height + ", over --max-height " + maxHeight);
                    return 1;
                }
                scale *= Math.Sqrt((double)maxHeight / height) * 0.98;
            }
            height = (height + 3) / 4 * 4;
            var recs = new JArray();
            long texels = 0;
            foreach (var it in items.OrderBy(i => i.P.Key))
            {
                texels += (long)it.W * it.H;
                recs.Add(new JObject
                {
                    ["key"] = it.P.Key.ToString("x8"), ["mesh"] = it.P.MeshPath, ["partition"] = it.P.Partition, ["source"] = it.P.Source,
                    ["x"] = it.X, ["y"] = it.Y, ["w"] = it.W, ["h"] = it.H, ["world_m2"] = Math.Round(it.World, 3), ["dist_m"] = Math.Round(it.Dist, 1),
                    ["uv"] = new JArray((double)it.W / width, 0.0, 0.0, (double)it.H / height, (double)it.X / width, (double)it.Y / height)
                });
            }
            var plan = new JObject
            {
                ["level"] = level, ["width"] = width, ["height"] = height, ["densityScale"] = scale, ["density"] = density, ["farDensity"] = far,
                ["play"] = new JArray(cx, cz, r), ["pad"] = pad, ["records"] = recs, ["skippedNoRadiosityUvs"] = noUv, ["hostRecordPlacements"] = hostKept,
                ["nativeSides"] = items.Count(i => nativeSides.ContainsKey(i.P.MeshPath)),
                ["spare"] = new JObject { ["x"] = spare.X, ["y"] = spare.Y, ["w"] = spare.W, ["h"] = spare.H,
                    ["uv"] = new JArray((double)spare.W / width, 0.0, 0.0, (double)spare.H / height, (double)spare.X / width, (double)spare.Y / height) }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            File.WriteAllText(outFile, plan.ToString(Formatting.Indented));
            var sides = items.Select(i => i.W).OrderBy(v => v).ToList();
            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "enlighten chart-plan: {0} charts ({1} placements matched, {2} without radiosity uvs) in {3}x{4} ({5:P1} filled), density scale {6:F3}, chart side median {7} p90 {8} max {9} -> {10}",
                items.Count, placements.Count, noUv, width, height, (double)texels / ((long)width * height), scale,
                sides.Count > 0 ? sides[sides.Count / 2] : 0, sides.Count > 0 ? sides[sides.Count * 9 / 10] : 0, sides.Count > 0 ? sides.Last() : 0, outFile));
            return 0;
        }

        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        static int Pack(List<Item> items, int width, int pad)
        {
            int x = 0, y = 0, rowH = 0;
            foreach (var it in items.OrderByDescending(i => i.H).ThenByDescending(i => i.W))
            {
                int w = it.W + pad, h = it.H + pad;
                if (x + w > width) { x = 0; y += rowH; rowH = 0; }
                it.X = x; it.Y = y;
                x += (w + 3) / 4 * 4; rowH = Math.Max(rowH, (h + 3) / 4 * 4);
            }
            return y + rowH;
        }

        /// <summary>LOD 0 lightmapped sections: positions, radiosity uvs, triangles; null without radiosity uvs.</summary>
        internal static Tuple<float[], float[], int[]> ReadMesh(string meshPath)
        {
            var verts = new List<RadiosityUvPort.Vertex>();
            var sections = new List<RadiosityUvPort.Section>();
            bool hasRad; string note;
            string err;
            try { err = RadiosityUvPort.ReadWithTriangles(meshPath, verts, sections, out hasRad, out note); }
            catch (Exception ex) { err = ex.Message; hasRad = false; }
            if (err != null || !hasRad) return null;
            var pos = new float[verts.Count * 3]; var uv = new float[verts.Count * 2];
            for (int i = 0; i < verts.Count; i++)
            {
                pos[i * 3] = verts[i].X; pos[i * 3 + 1] = verts[i].Y; pos[i * 3 + 2] = verts[i].Z;
                uv[i * 2] = verts[i].RU; uv[i * 2 + 1] = verts[i].RV;
            }
            var tris = new List<int>();
            foreach (var s in sections) if (s.HasRadiosity) tris.AddRange(s.Triangles);
            return tris.Count == 0 ? null : Tuple.Create(pos, uv, tris.ToArray());
        }

        static void Measure(Tuple<float[], float[], int[]> g, EnlightenKeys.Placement p, Item it)
        {
            float[] pos = g.Item1, uv = g.Item2; int[] t = g.Item3;
            Func<int, double[]> xf = v =>
            {
                double x = pos[v * 3], y = pos[v * 3 + 1], z = pos[v * 3 + 2];
                return new[] { x * p.Right[0] + y * p.Up[0] + z * p.Forward[0] + p.Trans[0], x * p.Right[1] + y * p.Up[1] + z * p.Forward[1] + p.Trans[1],
                               x * p.Right[2] + y * p.Up[2] + z * p.Forward[2] + p.Trans[2] };
            };
            it.Bmin = new[] { float.MaxValue, float.MaxValue, float.MaxValue }; it.Bmax = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int k = 0; k + 2 < t.Length; k += 3)
            {
                double[] a = xf(t[k]), b = xf(t[k + 1]), c = xf(t[k + 2]);
                foreach (var q in new[] { a, b, c })
                    for (int d = 0; d < 3; d++) { it.Bmin[d] = Math.Min(it.Bmin[d], (float)q[d]); it.Bmax[d] = Math.Max(it.Bmax[d], (float)q[d]); }
                double ex = b[0] - a[0], ey = b[1] - a[1], ez = b[2] - a[2], fx = c[0] - a[0], fy = c[1] - a[1], fz = c[2] - a[2];
                double cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
                it.World += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
                double u1 = uv[t[k + 1] * 2] - uv[t[k] * 2], v1 = uv[t[k + 1] * 2 + 1] - uv[t[k] * 2 + 1];
                double u2 = uv[t[k + 2] * 2] - uv[t[k] * 2], v2 = uv[t[k + 2] * 2 + 1] - uv[t[k] * 2 + 1];
                it.UvArea += 0.5 * Math.Abs(u1 * v2 - v1 * u2);
            }
        }
    }
}
