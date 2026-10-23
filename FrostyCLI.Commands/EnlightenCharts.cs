using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// T3: lightmap charts for meshes placed in a new level. Each placement (object reference to an object blueprint)
    /// gets a rect in grown atlas rows, a mesh-instance record keyed by createLightMapInstanceHashKey, texels lit by the
    /// same sky model as the terrain (its radiosity-uv triangles rasterised to world positions and normals), and its
    /// RadiosityTypeOverride set to None so the runtime registers the lightmap (addLightMapHandle takes None only).
    /// </summary>
    public static class EnlightenCharts
    {
        public sealed class Chart
        {
            public EnlightenKeys.Placement Placement;
            public int W, H, X, Y;
            public ChartRaster Raster;
            public string Partition;
        }

        /// <summary>
        /// Placements to chart in the given partitions: object references whose override is Dynamic (how `level add`
        /// places buildings) and whose mesh has radiosity uvs; one chart per placement. Returns null on error.
        /// </summary>
        public static List<Chart> Plan(IEnumerable<string> partitions, List<EnlightenDatabase> dbs, double density, bool all, string match = null)
        {
            var charts = new List<Chart>();
            var meshCache = new Dictionary<string, Tuple<float[], float[], int[], bool>>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in partitions)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(part);
                if (e == null) { Console.Error.WriteLine("enlighten relight: --charts " + part + " not found"); return null; }
                JObject dump = EbxDumper.Dump(App.AssetManager.GetEbx(e), resolve: true, deep: true);
                // object references to an object blueprint, and prefabs: every mesh a prefab places, at child transform x
                // placement (the composed keys hit the shipped Tatooine bake: the cantina's dome, walls, floor and bars)
                var placements = EnlightenKeys.Collect(e.Name, dump).Where(p => p.Source == "object" || p.Source == "prefab")
                    .GroupBy(p => p.ObjectGuid + "|" + p.MeshPath + "|" + p.Key).Select(g => g.First()).ToList();
                foreach (var p in placements)
                {
                    if (match != null && p.MeshPath.IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!all && p.Override != "RadiosityTypeOverride_Dynamic") continue;
                    if (p.Source == "prefab" && p.InnerOverride == "RadiosityTypeOverride_Dynamic")
                    { Console.Out.WriteLine("charts: " + p.MeshPath + " is probe-lit inside its prefab; skipped"); continue; }
                    if (dbs.Any(db => db.MeshInstances.Any(m => m.Hash == p.Key))) { Console.Out.WriteLine("charts: " + p.MeshPath + " already has a record (key " + p.Key.ToString("x8") + ")"); continue; }
                    Tuple<float[], float[], int[], bool> geo;
                    if (!meshCache.TryGetValue(p.MeshPath, out geo)) { geo = ReadMesh(p.MeshPath); meshCache[p.MeshPath] = geo; }
                    if (geo == null) { Console.Out.WriteLine("charts: " + p.MeshPath + " has no radiosity uvs; skipped"); continue; }
                    // chart size from the placed surface area at `density` texels per metre, 4-aligned (block copies)
                    double area = SurfaceArea(geo.Item1, geo.Item3, p);
                    int side = (int)Math.Ceiling(Math.Sqrt(area) * density / 4.0) * 4;
                    side = Math.Max(8, Math.Min(512, side));
                    charts.Add(new Chart { Placement = p, W = side, H = side, Partition = e.Name });
                }
            }
            return charts;
        }

        /// <summary>Pack the charts in rows under the atlas (shelf packing), grow every bake's atlas and add the records.</summary>
        public static void Place(List<Chart> charts, List<EnlightenDatabase> dbs, Dictionary<string, byte[]> baseDds)
        {
            if (charts.Count == 0) return;
            EnlightenDatabase layout = dbs[0];
            uint w = layout.Width, h0 = layout.Height;
            int top = (int)((h0 + 3) / 4 * 4), x = 0, y = top, rowH = 0;
            foreach (var c in charts.OrderByDescending(c => c.H))
            {
                if (x + c.W > w) { x = 0; y += rowH; rowH = 0; }
                c.X = x; c.Y = y; x += c.W; rowH = Math.Max(rowH, c.H);
            }
            uint newH = (uint)((y + rowH + 3) / 4 * 4);
            foreach (var db in dbs)
            {
                db.ResizeAtlas(w, newH);
                foreach (var c in charts)
                {
                    var uv = new byte[32];
                    EnlightenDatabase.PutF(uv, 0, (float)((double)c.W / w)); EnlightenDatabase.PutF(uv, 12, (float)((double)c.H / newH));
                    EnlightenDatabase.PutF(uv, 16, (float)((double)c.X / w)); EnlightenDatabase.PutF(uv, 20, (float)((double)c.Y / newH));
                    var mi = new EnlightenDatabase.MeshInstance { Hash = c.Placement.Key };
                    mi.UvTransforms.Add(uv);
                    db.MeshInstances.Add(mi);
                }
                // the runtime binary-searches the records by key
                db.MeshInstances.Sort((a, b) => a.Hash.CompareTo(b.Hash));
            }
            foreach (string key in baseDds.Keys.ToList()) baseDds[key] = AtlasIo.GrowDds(baseDds[key], (int)w, (int)newH);
        }

        /// <summary>Rasterise every chart: texel world positions and normals, dilated two rings.</summary>
        public static void Rasterise(List<Chart> charts)
        {
            var cache = new Dictionary<string, Tuple<float[], float[], int[], bool>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in charts)
            {
                Tuple<float[], float[], int[], bool> geo;
                if (!cache.TryGetValue(c.Placement.MeshPath, out geo)) { geo = ReadMesh(c.Placement.MeshPath); cache[c.Placement.MeshPath] = geo; }
                var p = c.Placement;
                c.Raster = new ChartRaster(c.W, c.H);
                c.Raster.Rasterise(geo.Item1, geo.Item2, geo.Item3, p.Right, p.Up, p.Forward, p.Trans, flipV: false, flipNormals: !geo.Item4);
                c.Raster.Dilate(2);
            }
        }

        /// <summary>Set RadiosityTypeOverride None on the charted object references (the lightmap registers only then).</summary>
        public static int SetOverrides(List<Chart> charts, ExportLogger logger)
        {
            int failures = 0;
            var formatter = new OutputFormatter(json: false, output: TextWriter.Null);
            foreach (var g in charts.GroupBy(c => c.Partition + "|" + c.Placement.ObjectGuid))
            {
                var c = g.First();
                string[] a = { c.Partition, "--object", c.Placement.ObjectGuid.ToString(), "--prop", "RadiosityTypeOverride", "--value", "RadiosityTypeOverride_None" };
                int rc = WriteOpBus.Execute("ebx-set", a, logger, formatter);
                if (rc != 0) { Console.Error.WriteLine("charts: could not set the override on " + c.Placement.ObjectGuid + " in " + c.Partition); failures++; }
            }
            return failures;
        }

        /// <summary>Positions (3 per vertex), radiosity uvs (2 per vertex), triangles of the radiosity sections, winding flag.</summary>
        static Tuple<float[], float[], int[], bool> ReadMesh(string meshPath)
        {
            var verts = new List<RadiosityUvPort.Vertex>();
            var sections = new List<RadiosityUvPort.Section>();
            bool hasRad; string note;
            string err = RadiosityUvPort.ReadWithTriangles(meshPath, verts, sections, out hasRad, out note);
            if (err != null || !hasRad) return null;
            var pos = new float[verts.Count * 3]; var uv = new float[verts.Count * 2];
            for (int i = 0; i < verts.Count; i++)
            {
                pos[i * 3] = verts[i].X; pos[i * 3 + 1] = verts[i].Y; pos[i * 3 + 2] = verts[i].Z;
                uv[i * 2] = verts[i].RU; uv[i * 2 + 1] = verts[i].RV;
            }
            var tris = new List<int>();
            foreach (var s in sections) if (s.HasRadiosity) tris.AddRange(s.Triangles);
            if (tris.Count == 0) return null;
            bool outward = ChartRaster.CrossPointsOut(pos, tris);
            return Tuple.Create(pos, uv, tris.ToArray(), outward);
        }

        static double SurfaceArea(float[] pos, int[] tris, EnlightenKeys.Placement p)
        {
            double sr = Math.Sqrt(p.Right[0] * p.Right[0] + p.Right[1] * p.Right[1] + p.Right[2] * p.Right[2]);
            double su = Math.Sqrt(p.Up[0] * p.Up[0] + p.Up[1] * p.Up[1] + p.Up[2] * p.Up[2]);
            double sf = Math.Sqrt(p.Forward[0] * p.Forward[0] + p.Forward[1] * p.Forward[1] + p.Forward[2] * p.Forward[2]);
            double a = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int i = tris[t] * 3, j = tris[t + 1] * 3, k = tris[t + 2] * 3;
                double ex = (pos[j] - pos[i]) * sr, ey = (pos[j + 1] - pos[i + 1]) * su, ez = (pos[j + 2] - pos[i + 2]) * sf;
                double fx = (pos[k] - pos[i]) * sr, fy = (pos[k + 1] - pos[i + 1]) * su, fz = (pos[k + 2] - pos[i + 2]) * sf;
                double cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
                a += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
            }
            return a;
        }
    }
}
