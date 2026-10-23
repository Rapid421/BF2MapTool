using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten geometry <level> --out soup.obj`: the render geometry a level draws -- every static model group member
    /// (EBX- or Havok-placed), object reference and prefab, down to the model entities of the blueprints -- at the finest LOD within a triangle budget --
    /// in world space, one OBJ group per placement, named after its mesh. It is the occluder and bounce surface
    /// set for `enlighten relight --geometry / --host-geometry`: a level's navmesh geometry leaves out what nobody walks
    /// on (Mos Eisley's houses are static group members; 446 of Tatooine's 478 in-town wall lamps have no navmesh
    /// triangle within 3 m).
    /// </summary>
    public static class EnlightenGeometry
    {
        public static int Execute(string[] args, ExportLogger logger)
        {
            string source = null, outFile = null;
            double[] region = null;
            double minSize = 0.5, maxSize = 400;
            int maxDepth = 6, maxTris = 2000;
            bool radiosityOnly = false;
            // never part of a level's static bake: the front end's and cinematics' sets, gameplay vehicles, UI
            var skip = new List<string> { "levels/frontend/", "cinematics/", "gameplay/", "ui/" };
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (a == "--region" && i + 1 < args.Length)
                    region = args[++i].Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                else if (a == "--min-size" && i + 1 < args.Length) minSize = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--max-depth" && i + 1 < args.Length) maxDepth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--max-tris" && i + 1 < args.Length) maxTris = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--max-size" && i + 1 < args.Length) maxSize = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--radiosity-only") radiosityOnly = true;
                else if (a == "--skip" && i + 1 < args.Length) skip.AddRange(args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                else if (!a.StartsWith("--")) source = a;
                else { Console.Error.WriteLine("enlighten geometry: unknown option '" + a + "'"); return 1; }
            }
            if (source == null || outFile == null || (region != null && region.Length != 4))
            {
                Console.Error.WriteLine("Usage: frostycli enlighten geometry <level|layer|blueprint> --out soup.obj [--region x0,z0,x1,z1] [--min-size m] [--max-size m] " +
                    "[--max-tris n] [--max-depth n] [--radiosity-only] [--skip substring,...]");
                return 1;
            }
            var sw = Stopwatch.StartNew();
            // each mesh at its finest LOD within the budget: light needs the walls, not the trim (Tatooine at LOD 0 is
            // tens of millions of triangles)
            int savedMax = RadiosityUvPort.MaxTriangles;
            RadiosityUvPort.MaxTriangles = maxTris;
            try { return Run(source, outFile, region, minSize, maxDepth, maxTris, sw, maxSize, radiosityOnly, skip); }
            finally { RadiosityUvPort.MaxTriangles = savedMax; }
        }

        static int Run(string source, string outFile, double[] region, double minSize, int maxDepth, int maxTris, Stopwatch sw,
                       double maxSize, bool radiosityOnly, List<string> skip)
        {
            List<string> skipped; string error;
            List<LevelCommand.MeshPlacement> placements = LevelCommand.CollectMeshes(source, maxDepth, out skipped, out error);
            if (placements == null) { Console.Error.WriteLine("enlighten geometry: " + error); return 1; }
            Console.Out.WriteLine(string.Format("{0} mesh placement(s) of {1} mesh(es) ({2:F1}s){3}", placements.Count,
                placements.Select(p => p.Mesh).Distinct(StringComparer.OrdinalIgnoreCase).Count(), sw.Elapsed.TotalSeconds,
                skipped.Count > 0 ? string.Format("; {0} asset(s) not walked, e.g. {1}", skipped.Count, skipped[0]) : ""));

            var meshes = new Dictionary<string, Tuple<float[], int[], float>>(StringComparer.OrdinalIgnoreCase);
            int unreadable = 0, small = 0, outside = 0, written = 0, huge = 0, skippedName = 0, noRadiosity = 0;
            long vertsOut = 0, trisOut = 0;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            using (var w = new StreamWriter(outFile, false, new System.Text.UTF8Encoding(false), 1 << 20))
            {
                w.WriteLine("# render geometry of " + source + (region != null ? string.Format(CultureInfo.InvariantCulture, ", region {0},{1},{2},{3}", region[0], region[1], region[2], region[3]) : "")
                            + " (frostycli enlighten geometry, finest LOD within " + maxTris + " triangles, min size " + minSize.ToString(CultureInfo.InvariantCulture) + " m)");
                foreach (LevelCommand.MeshPlacement p in placements)
                {
                    if (skip.Any(k => p.Mesh.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) { skippedName++; continue; }
                    Tuple<float[], int[], float> m;
                    if (!meshes.TryGetValue(p.Mesh, out m)) meshes[p.Mesh] = m = Read(p.Mesh, radiosityOnly);
                    if (m == null) { if (radiosityOnly) noRadiosity++; else unreadable++; continue; }
                    // a mesh's size in the world: its local diagonal under the placement's scale
                    double scale = Math.Max(Len(p.Right), Math.Max(Len(p.Up), Len(p.Forward)));
                    if (m.Item3 * scale < minSize) { small++; continue; }
                    // sky domes and set shells enclose the level: the bake never had them overhead
                    if (m.Item3 * scale > maxSize) { huge++; continue; }
                    if (region != null)
                    {
                        double reach = m.Item3 * scale;
                        if (p.Trans[0] < region[0] - reach || p.Trans[0] > region[2] + reach || p.Trans[2] < region[1] - reach || p.Trans[2] > region[3] + reach)
                        { outside++; continue; }
                    }
                    float[] pos = m.Item1; int[] tris = m.Item2;
                    w.Write("g "); w.WriteLine(p.Mesh);
                    for (int v = 0; v < pos.Length; v += 3)
                    {
                        double x = pos[v], y = pos[v + 1], z = pos[v + 2];
                        double wx = x * p.Right[0] + y * p.Up[0] + z * p.Forward[0] + p.Trans[0];
                        double wy = x * p.Right[1] + y * p.Up[1] + z * p.Forward[1] + p.Trans[1];
                        double wz = x * p.Right[2] + y * p.Up[2] + z * p.Forward[2] + p.Trans[2];
                        w.Write("v ");
                        w.Write(wx.ToString("0.###", CultureInfo.InvariantCulture)); w.Write(' ');
                        w.Write(wy.ToString("0.###", CultureInfo.InvariantCulture)); w.Write(' ');
                        w.WriteLine(wz.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    long b = vertsOut + 1;
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        w.Write("f "); w.Write(b + tris[t]); w.Write(' '); w.Write(b + tris[t + 1]); w.Write(' '); w.WriteLine(b + tris[t + 2]);
                    }
                    vertsOut += pos.Length / 3; trisOut += tris.Length / 3; written++;
                }
            }
            Console.Out.WriteLine(string.Format("enlighten geometry: {0} placement(s) written ({1:N0} triangles, {2:N0} vertices) to {3}; left out: {4} unreadable mesh, " +
                "{5} under {6} m, {9} over {10} m, {11} by name{12}{7} ({8:F1}s)", written, trisOut, vertsOut, outFile, unreadable, small, minSize,
                region != null ? ", " + outside + " outside the region" : "", sw.Elapsed.TotalSeconds, huge, maxSize, skippedName,
                radiosityOnly ? ", " + noRadiosity + " without lightmap uvs" : ""));
            return 0;
        }

        static double Len(float[] v) { return Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); }

        static bool Finite(float v) { return !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 1e5f; }

        /// <summary>LOD-0 positions and triangles of every section, and the local bounding diagonal; null when unreadable.</summary>
        static Tuple<float[], int[], float> Read(string meshPath, bool radiosityOnly = false)
        {
            var verts = new List<RadiosityUvPort.Vertex>();
            var sections = new List<RadiosityUvPort.Section>();
            bool hasRad; string note;
            string err;
            try { err = RadiosityUvPort.ReadWithTriangles(meshPath, verts, sections, out hasRad, out note); }
            catch (Exception ex) { err = ex.Message; }
            if (err != null || verts.Count == 0) return null;
            var pos = new float[verts.Count * 3];
            var bad = new bool[verts.Count];
            float lx = float.MaxValue, ly = float.MaxValue, lz = float.MaxValue, hx = float.MinValue, hy = float.MinValue, hz = float.MinValue;
            for (int i = 0; i < verts.Count; i++)
            {
                pos[i * 3] = verts[i].X; pos[i * 3 + 1] = verts[i].Y; pos[i * 3 + 2] = verts[i].Z;
                // a LOD whose position format decodes to NaN / infinity (Mos Eisley's backdrop parts) contributes nothing
                if (!Finite(verts[i].X) || !Finite(verts[i].Y) || !Finite(verts[i].Z)) { bad[i] = true; continue; }
                lx = Math.Min(lx, verts[i].X); ly = Math.Min(ly, verts[i].Y); lz = Math.Min(lz, verts[i].Z);
                hx = Math.Max(hx, verts[i].X); hy = Math.Max(hy, verts[i].Y); hz = Math.Max(hz, verts[i].Z);
            }
            var kept = new List<int>();
            foreach (RadiosityUvPort.Section s in sections)
            {
                // --radiosity-only: the sections the bake lightmaps (Enlighten's scene); foliage cards and probe-lit props
                // carry no lightmap uvs and never blocked the bake's light
                if (radiosityOnly && !s.HasRadiosity) continue;
                for (int t = 0; t + 2 < s.Triangles.Length; t += 3)
                {
                    int a = s.Triangles[t], b = s.Triangles[t + 1], c = s.Triangles[t + 2];
                    if (bad[a] || bad[b] || bad[c]) continue;
                    kept.Add(a); kept.Add(b); kept.Add(c);
                }
            }
            int[] tris = kept.ToArray();
            if (tris.Length == 0) return null;
            for (int i = 0; i < verts.Count; i++) if (bad[i]) { pos[i * 3] = pos[i * 3 + 1] = pos[i * 3 + 2] = 0; }
            float diag = (float)Math.Sqrt((hx - lx) * (hx - lx) + (hy - ly) * (hy - ly) + (hz - lz) * (hz - lz));
            return Tuple.Create(pos, tris, diag);
        }
    }
}
