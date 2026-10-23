using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Texture;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten relight`: re-light the terrain tiles of a level's Enlighten bakes for a new terrain, keeping the
    /// bake's layout (tile rects, charts) and encoding (BFII shader, SPEC.md §4). The sky and bounce are fitted to the
    /// bake's own shipped terrain texels on the host terrain, then evaluated on the new terrain; the new tile blocks
    /// are compressed (BC6H / BC7) and spliced into the atlases, everything else stays byte for byte.
    /// </summary>
    public static class EnlightenRelight
    {
        internal sealed class Bake
        {
            public string SubWorld, Static, Dynamic, Ve;
            public EnlightenDatabase Db;
            public Dictionary<string, string> Textures = new Dictionary<string, string>();
            public double[] Rho, ESun; public float[] Sun;
            public SkyModel Model;
            public int SunIndex;
            public double ESunScale = 1, SunIntensity;
            public double[] SunColor;
            public ResAssetEntry DbRes, FlsRes;
            public StaticEnlightenDatabase Fls;
            public float[] ExtrusionSun, VeSun;      // towards the sun
            public float[] SunOut;                   // the sun of the new bake (--sun-rotate), else null
            public string ExtrusionSource, RhoSource;
            public bool RhoOverridden;
            public float[] BaseIrr; public byte[][] BaseDir; public byte[] BaseSky; public int BaseW, BaseH;   // decoded shipped atlas
            public double[] SunGain;                                                                           // bounced sun / VE sun
            public double HeightScale = 1.0;                                                                   // VE SunShadowHeightScale
            public double[] OwnRho;                                                                            // the bake's own VE TerrainColor (no donor)
            public double[] UserSkyScale, UserSunScale;                                                        // --sky-scale / --sun-scale applied to Model
        }

        internal static readonly string[] Roles = { "StaticIrradianceTexture", "StaticDirectionTexture", "StaticDirectionTextureG",
                                           "StaticDirectionTextureB", "StaticSkyVisibilityTexture" };

        public static int Execute(string[] args, ExportLogger logger)
        {
            string level = null, terrain = null, hostTerrain = null, outDir = null, onlyBakes = null, geometry = null, hostGeometry = null;
            bool import = true, probes = false, onlyNew = false;
            double[] terrainColor = null;
            double extendTile = 0; int extendTexels = 256;
            var chartParts = new List<string>(); double chartDensity = 1.5; bool chartAll = false; string chartMatch = null;
            string cloneInto = null;
            double[] chartTint = { 1, 1, 1 };
            double sunRotateDeg = 0;
            double? sunElevation = null;
            double sunGain = 1.0, exclude = -1;
            var excludeDiscs = new List<double[]>();
            int rasterSize = 2049;
            var lightSpecs = new List<string>(); var hostLightSpecs = new List<string>(); double lightGain = 1.0, lightReach = 40.0; double[] lightAlbedo = null, meshAlbedo = null; int lightPhotons = 4096, lightBounces = 3;
            double[] skyScale = null, sunScale = null;
            Func<string, double[]> rgb = s =>
            {
                double[] v = s.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                return v.Length == 1 ? new[] { v[0], v[0], v[0] } : v;
            };
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--terrain" && i + 1 < args.Length) terrain = args[++i];
                else if (a == "--host-terrain" && i + 1 < args.Length) hostTerrain = args[++i];
                else if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (a == "--bakes" && i + 1 < args.Length) onlyBakes = args[++i];
                else if (a == "--no-import") import = false;
                else if (a == "--probes") probes = true;
                else if (a == "--extend" && i + 1 < args.Length)
                {
                    string[] v = args[++i].Split(',');
                    extendTile = double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (v.Length > 1) extendTexels = int.Parse(v[1]);
                }
                else if (a == "--only-new-tiles") onlyNew = true;
                else if (a == "--charts" && i + 1 < args.Length) chartParts.Add(args[++i]);
                else if (a == "--chart-density" && i + 1 < args.Length) chartDensity = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "--chart-all") chartAll = true;
                else if (a == "--chart-match" && i + 1 < args.Length) chartMatch = args[++i];
                else if (a == "--clone-into" && i + 1 < args.Length) cloneInto = args[++i].TrimEnd('/');
                else if (a == "--sun-rotate" && i + 1 < args.Length) sunRotateDeg = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "--sun-elevation" && i + 1 < args.Length) sunElevation = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "--chart-tint" && i + 1 < args.Length)
                    chartTint = args[++i].Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                else if (a == "--terrain-color" && i + 1 < args.Length)
                    terrainColor = args[++i].Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                else if (a == "--sun-gain" && i + 1 < args.Length) sunGain = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "--raster" && i + 1 < args.Length) rasterSize = int.Parse(args[++i]);
                else if (a == "--geometry" && i + 1 < args.Length) geometry = args[++i];
                else if (a == "--host-geometry" && i + 1 < args.Length) hostGeometry = args[++i];
                else if (a == "--calibration-exclude" && i + 1 < args.Length)
                    excludeDiscs.Add(args[++i].Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
                else if (a == "--calibration-radius" && i + 1 < args.Length) exclude = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "--lights" && i + 1 < args.Length) lightSpecs.Add(args[++i]);
                else if (a == "--host-lights" && i + 1 < args.Length) hostLightSpecs.Add(args[++i]);
                else if (a == "--light-gain" && i + 1 < args.Length) lightGain = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "--light-reach" && i + 1 < args.Length) lightReach = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else if (a == "--light-albedo" && i + 1 < args.Length) lightAlbedo = rgb(args[++i]);
                else if (a == "--light-photons" && i + 1 < args.Length) lightPhotons = int.Parse(args[++i]);
                else if (a == "--light-bounces" && i + 1 < args.Length) lightBounces = int.Parse(args[++i]);
                else if (a == "--mesh-albedo" && i + 1 < args.Length) meshAlbedo = rgb(args[++i]);
                else if (a == "--sky-scale" && i + 1 < args.Length) skyScale = rgb(args[++i]);
                else if (a == "--sun-scale" && i + 1 < args.Length) sunScale = rgb(args[++i]);
                else if (!a.StartsWith("--")) level = a;
            }
            if (level == null || terrain == null)
            {
                Console.Error.WriteLine("Usage: frostycli enlighten relight <level-or-lighting-subworld> --terrain <res|terrain ebx|file> " +
                    "[--host-terrain <res|ebx|file>] [--bakes <FL_a,FL_b>] [--out <dir>] [--no-import] [--sun-gain g] " +
                    "[--calibration-exclude x,z,r]... [--calibration-radius r] [--geometry soup.obj] [--host-geometry soup.obj] [--probes] " +
                    "[--terrain-color r,g,b] [--extend <tile metres>[,<texels>]] [--only-new-tiles] [--charts <partition>]... [--chart-density t/m] [--chart-all] [--chart-match <mesh path substring>] [--clone-into <asset prefix>] [--sun-rotate <degrees>] [--sun-elevation <VE degrees>] " +
                    "[--lights <lights.json|ebx>[@bake]]... [--host-lights <lights.json|ebx>[@bake]]... [--light-gain g] [--light-reach m] [--light-albedo r,g,b] [--mesh-albedo r,g,b] [--light-photons n] [--light-bounces n] [--sky-scale r,g,b] [--sun-scale r,g,b]");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("enlighten relight: AssetManager not initialized."); return 1; }
            outDir = outDir ?? Path.Combine(Path.GetTempPath(), "frostycli-relight-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(outDir);
            var sw = Stopwatch.StartNew();

            // ---- bakes
            List<Bake> bakes = FindBakes(level, logger);
            List<Bake> allBakes = bakes;
            if (onlyBakes != null)
            {
                var keep = new HashSet<string>(onlyBakes.Split(','), StringComparer.OrdinalIgnoreCase);
                bakes = bakes.Where(b => keep.Contains(Path.GetFileName(b.Static))).ToList();
            }
            if (bakes.Count == 0) { Console.Error.WriteLine("enlighten relight: no Enlighten bakes found under " + level); return 1; }
            foreach (Bake b in bakes)
            {
                if (!LoadBake(b)) return 1;
                for (int c = 0; c < 3; c++) b.ESun[c] *= sunGain;
                b.OwnRho = (double[])b.Rho.Clone();
            }
            // the level's own copies of the lighting assets: everything below reads and writes them, the host's stay shipped
            if (cloneInto != null)
                foreach (Bake b in bakes)
                    if (!CloneBakeInto(b, cloneInto, logger)) return 1;
            // TerrainColor only counts where a VE overrides it; Tatooine's Sunset and Night VEs leave it at the type
            // default (0.1) and their probes only agree with the Sunny sand (0.356, 0.305, 0.238) - SPEC.md 7.
            // the donor may be a bake left out by --bakes (relighting Night alone still takes the Sunny sand)
            Bake rhoDonor = bakes.FirstOrDefault(x => x.RhoOverridden);
            double[] donorRho = rhoDonor != null ? rhoDonor.Rho : null;
            string donorVe = rhoDonor != null ? rhoDonor.Ve : null;
            if (donorRho == null)
                foreach (Bake o in allBakes.Where(x => !bakes.Contains(x)))
                {
                    bool overridden;
                    double[] r = VeTerrainColor(o.Ve, out overridden);
                    if (r != null && overridden) { donorRho = r; donorVe = o.Ve; break; }
                }
            foreach (Bake b in bakes)
            {
                if (terrainColor != null) { b.Rho = (double[])terrainColor.Clone(); b.RhoSource = "--terrain-color"; }
                else if (!b.RhoOverridden && donorRho != null) { b.Rho = (double[])donorRho.Clone(); b.RhoSource = donorVe + " (own VE does not override it)"; }
                Console.Out.WriteLine(string.Format("bake {0}: sun ({1:F3}, {2:F3}, {3:F3}) elevation {4:F1} deg from {5}{6}; terrain colour ({7:F3}, {8:F3}, {9:F3}) from {10}",
                    Path.GetFileName(b.Static), b.Sun[0], b.Sun[1], b.Sun[2], Math.Asin(Math.Max(-1, Math.Min(1, b.Sun[1]))) * 180 / Math.PI,
                    b.ExtrusionSource != null ? b.ExtrusionSource + " (ShadowExtrusionLightDirection)"
                        : string.Format(System.Globalization.CultureInfo.InvariantCulture, "the VE (SunShadowHeightScale {0})", b.HeightScale),
                    b.ExtrusionSource != null && b.VeSun != null ? string.Format(", the VE says {0:F1} deg", Math.Asin(Math.Max(-1, Math.Min(1, b.VeSun[1]))) * 180 / Math.PI) : "",
                    b.Rho[0], b.Rho[1], b.Rho[2], b.RhoSource ?? "the type default"));
            }
            // one tile layout for all bakes (the relight keeps the host layout). Bakes without terrain tiles (a spawn
            // room's own bake, Hoth's FL_Hoth_01_Splitscreen_Spawn) are left alone; when the terrain bakes disagree,
            // the largest group is relit and the others are named (relight them with --bakes)
            foreach (Bake b in bakes.Where(x => x.Db.Terrain.Count == 0).ToList())
            {
                Console.Out.WriteLine("bake " + Path.GetFileName(b.Static) + ": no terrain tiles (not a terrain bake); left as shipped");
                bakes.Remove(b);
            }
            if (bakes.Count == 0) { Console.Error.WriteLine("enlighten relight: no bake with terrain tiles"); return 1; }
            Func<Bake, string> sig = x => x.Db.Width + "x" + x.Db.Height + ":" + string.Join(",", x.Db.Terrain.Select(t => Convert.ToBase64String(t.Raw)));
            var groups = bakes.GroupBy(sig).OrderByDescending(g => g.Count()).ThenByDescending(g => g.First().Db.Terrain.Count).ToList();
            if (groups.Count > 1)
            {
                foreach (var g in groups.Skip(1))
                    Console.Out.WriteLine("bakes " + string.Join(", ", g.Select(x => Path.GetFileName(x.Static))) + ": another tile layout; skipped (relight them with --bakes)");
                bakes = groups[0].ToList();
            }
            EnlightenDatabase layout = bakes[0].Db;
            var suns = new float[bakes.Count][];
            for (int i = 0; i < bakes.Count; i++) { bakes[i].SunIndex = i; suns[i] = bakes[i].Sun; }
            Console.Out.WriteLine(string.Format("bakes: {0}; {1} terrain tiles, atlas {2}x{3}", string.Join(", ", bakes.Select(b => Path.GetFileName(b.Static))),
                layout.Terrain.Count, layout.Width, layout.Height));

            // ---- terrains
            double minX = layout.Terrain.Min(t => t.MinX), minZ = layout.Terrain.Min(t => t.MinZ);
            double maxX = layout.Terrain.Max(t => t.MaxX), maxZ = layout.Terrain.Max(t => t.MaxZ);
            DenseHeightfield target = TerrainRaster(terrain, rasterSize, logger);
            DenseHeightfield host = hostTerrain != null ? TerrainRaster(hostTerrain, rasterSize, logger) : target;
            if (target == null || host == null) return 1;
            GeometrySoup soup = geometry != null ? GeometrySoup.LoadObj(geometry) : null;
            GeometrySoup hostSoup = hostGeometry != null ? GeometrySoup.LoadObj(hostGeometry) : null;
            if (soup != null)
                Console.Out.WriteLine(string.Format("geometry: {0} triangles, x {1:F0}..{2:F0}, z {3:F0}..{4:F0}", soup.TriangleCount,
                    soup.MinX, soup.MaxX, soup.MinZ, soup.MaxZ));
            Console.Out.WriteLine(string.Format("terrains rasterised ({0:F1}s): tiles cover {1}..{2} x {3}..{4}", sw.Elapsed.TotalSeconds, minX, maxX, minZ, maxZ));

            // ---- shipped tile texels of every bake (calibration) and the base DDS files
            var shipped = new Dictionary<Bake, TileTexels[]>();
            var baseDds = new Dictionary<string, byte[]>();
            foreach (Bake b in bakes)
            {
                shipped[b] = ReadTiles(b, layout, outDir, baseDds);
                if (shipped[b] == null) return 1;
            }

            // ---- per-tile brightness of the open-sky texels: a tile far darker than the map (never baked, a black stub)
            // stays out of the fit, and the per-tile row weight is floored at a quarter of the map's median
            var dead = bakes.ToDictionary(b => b, b => new bool[layout.Terrain.Count]);
            var floor = new Dictionary<Bake, double[]>();
            foreach (Bake b in bakes)
            {
                var med = new double[layout.Terrain.Count][];
                for (int ti = 0; ti < layout.Terrain.Count; ti++)
                {
                    TileTexels tt = shipped[b][ti];
                    med[ti] = new double[3];
                    for (int c = 0; c < 3; c++)
                    {
                        var v = new List<double>();
                        for (int t = 0; t < tt.L0.Length / 3; t++) if (tt.Sky[t * 4] >= 235) v.Add(tt.L0[t * 3 + c]);
                        med[ti][c] = v.Count == 0 ? double.NaN : v.OrderBy(x => x).ElementAt(v.Count / 2);
                    }
                }
                var mapMed = new double[3];
                for (int c = 0; c < 3; c++)
                {
                    var v = med.Where(m => !double.IsNaN(m[c])).Select(m => m[c]).OrderBy(x => x).ToList();
                    mapMed[c] = v.Count == 0 ? 0 : v[v.Count / 2];
                }
                floor[b] = mapMed.Select(x => 0.25 * x).ToArray();
                var deadList = new List<int>();
                for (int ti = 0; ti < layout.Terrain.Count; ti++)
                {
                    if (double.IsNaN(med[ti][1])) continue;
                    double lum = 0.2126 * med[ti][0] + 0.7152 * med[ti][1] + 0.0722 * med[ti][2];
                    double mapLum = 0.2126 * mapMed[0] + 0.7152 * mapMed[1] + 0.0722 * mapMed[2];
                    if (lum < 0.02 * mapLum) { dead[b][ti] = true; deadList.Add(ti); }
                }
                if (deadList.Count > 0)
                    Console.Out.WriteLine(string.Format("fit {0}: tile(s) {1} are black under open sky in the shipped bake (not baked); left out of the fit",
                        Path.GetFileName(b.Static), string.Join(", ", deadList)));
            }

            // ---- fit: host moments per tile, accumulated per bake
            var acc = bakes.ToDictionary(b => b, b => new[] { new SkyModel.Accumulator(), new SkyModel.Accumulator(), new SkyModel.Accumulator() });
            var flat = bakes.ToDictionary(b => b, b => new List<double>[] { new List<double>(), new List<double>(), new List<double>() });
            var qSky = bakes.ToDictionary(b => b, b => new List<float>[] { new List<float>(), new List<float>(), new List<float>(), new List<float>() });
            double[,] T = SkyModel.LobeMatrix();
            int calib = 0;
            for (int ti = 0; ti < layout.Terrain.Count; ti++)
            {
                TexelMoments m = TileMoments(host, layout, ti, suns, hostSoup);
                foreach (Bake b in bakes)
                {
                    TileTexels tt = shipped[b][ti];
                    var use = new bool[m.Count];
                    if (dead[b][ti]) continue;
                    for (int t = 0; t < m.Count; t++)
                    {
                        bool ok = tt.Sky[t * 4] >= 235 && m.SkyVis[t] >= 0.9f;
                        if (exclude > 0 && Math.Max(Math.Abs(m.Wx[t]), Math.Abs(m.Wz[t])) <= exclude) ok = false;
                        foreach (double[] disc in excludeDiscs)
                            if ((m.Wx[t] - disc[0]) * (m.Wx[t] - disc[0]) + (m.Wz[t] - disc[1]) * (m.Wz[t] - disc[1]) < disc[2] * disc[2]) ok = false;
                        use[t] = ok;
                        if (!ok) continue;
                        if (b == bakes[0]) calib++;
                        if (t % 4 == 0)
                        {
                            qSky[b][0].Add(m.SkyVis[t]); qSky[b][1].Add(tt.Sky[t * 4]);
                            qSky[b][2].Add(m.Ao[t]); qSky[b][3].Add(tt.Sky[t * 4 + 3]);
                            if (m.Nrm[t * 3 + 1] > 0.995f && m.SkyVis[t] > 0.99f)
                                for (int c = 0; c < 3; c++)
                                    flat[b][c].Add(tt.L0[t * 3 + c] * SkyModel.Y0 * SkyModel.ShL1(m.Nrm[t * 3], m.Nrm[t * 3 + 1], m.Nrm[t * 3 + 2],
                                        tt.D[(t * 3 + c) * 3], tt.D[(t * 3 + c) * 3 + 1], tt.D[(t * 3 + c) * 3 + 2]));
                        }
                    }
                    SkyModel.Accumulate(acc[b], m, b.SunIndex, T, tt.L0, tt.D, use, floor[b]);
                }
            }
            foreach (Bake b in bakes)
            {
                double[] fl = flat[b].Select(l => l.Count == 0 ? 0 : l.OrderBy(v => v).ElementAt(l.Count / 2)).ToArray();
                // the sun the bake bounced, measured on the shipped probes over sunlit open ground, goes into the fit
                // itself so the fitted sky and the bounce add up to the shipped texels
                double[] gain = SunGainFromProbes(b, host, exclude, fl);
                b.SunGain = gain ?? new[] { 1.0, 1.0, 1.0 };
                double[] eSun = b.ESun.Select((e, c) => e * b.SunGain[c]).ToArray();
                b.Model = SkyModel.Solve(acc[b], b.Rho, eSun, fl, b.SunIndex);
                b.Model.CalibrationTexels = calib;
                SkyModel.QuantileMap(qSky[b][0], qSky[b][1], out b.Model.SkyQuantX, out b.Model.SkyQuantY);
                SkyModel.QuantileMap(qSky[b][2], qSky[b][3], out b.Model.AoQuantX, out b.Model.AoQuantY);
                Console.Out.WriteLine(string.Format("fit {0}: {1} calibration texels, open-ground shading ({2:F1}, {3:F1}, {4:F1}), kappa ({5:F4}, {6:F4}, {7:F4})",
                    Path.GetFileName(b.Static), calib, fl[0], fl[1], fl[2], b.Model.Kappa[0], b.Model.Kappa[1], b.Model.Kappa[2]));
                Console.Out.WriteLine(gain != null
                    ? string.Format("   sun bounce calibrated on the shipped open-ground probes: gain ({0:F3}, {1:F3}, {2:F3})", gain[0], gain[1], gain[2])
                    : "   sun bounce: no sunlit open-ground probes to calibrate on; the visual environment's sun as is");
                File.WriteAllText(Path.Combine(outDir, Path.GetFileName(b.Static) + ".model.json"), ModelJson(b).ToString());
            }
            Console.Out.WriteLine(string.Format("fitted ({0:F1}s)", sw.Elapsed.TotalSeconds));

            // ---- lamps: the lights each bake bounces (Flux bakes a light's indirect part when AffectRadiosity is on)
            // ---- lamp gain: measured on the host's own lights where given (--host-lights), else --light-gain
            var lampGain = bakes.ToDictionary(b => b, b => lightGain);
            if (hostLightSpecs.Count > 0)
            {
                Dictionary<Bake, List<LocalLight>> hostPer = LoadLightSpecs(hostLightSpecs, bakes, "--host-lights");
                if (hostPer == null) return 1;
                foreach (Bake b in bakes)
                {
                    if (hostPer[b].Count == 0) continue;
                    double[] alb = lightAlbedo ?? b.Rho, malb = meshAlbedo ?? new[] { 0.5, 0.5, 0.5 };
                    string report;
                    double g = CalibrateLampGain(b, hostPer[b], host, hostSoup, layout, suns, shipped[b], alb, malb, lightPhotons, lightBounces, out report);
                    Console.Out.WriteLine("lights " + Path.GetFileName(b.Static) + ": " + report);
                    if (g > 0) lampGain[b] = g * lightGain;
                }
            }

            // ---- lamps: the lights each bake bounces (Flux bakes a light's indirect part when AffectRadiosity is on)
            var lampField = new Dictionary<Bake, Lamps>();
            if (lightSpecs.Count > 0)
            {
                Dictionary<Bake, List<LocalLight>> perBake = LoadLightSpecs(lightSpecs, bakes, "--lights");
                if (perBake == null) return 1;
                var sets = new Dictionary<string, Lamps>();
                foreach (Bake b in bakes)
                {
                    if (perBake[b].Count == 0) continue;
                    // what the lamps' light bounces off: the ground has the bake's TerrainColor (the VE's, else a sibling
                    // bake's - Tatooine Night takes the Sunny sand, and the shipped night texels near lamps follow it,
                    // SPEC.md 11), meshes --mesh-albedo; --light-albedo overrides the ground's
                    double[] alb = lightAlbedo ?? b.Rho;
                    // meshes: 0.5 grey unless told (Tatooine Night: ground sand + meshes 0.5 + 3 bounces leaves the shipped
                    // lamp light 1.35-2x the model's, against 3-25x with the ground's albedo everywhere)
                    double[] malb = meshAlbedo ?? new[] { 0.5, 0.5, 0.5 };
                    // bakes given the same lights (and albedo) share one set
                    string key = string.Join("|", perBake[b].Select(l => l.GetHashCode())) + "|" + string.Join(",", alb) + "|" + string.Join(",", malb);
                    Lamps set;
                    if (!sets.TryGetValue(key, out set))
                    {
                        set = new Lamps { Field = new LightField(perBake[b], target, soup),
                                          Photons = LampPhotons.Trace(perBake[b], target, soup, alb, lightPhotons, lightBounces, meshAlbedo: malb) };
                        sets[key] = set;
                        Console.Out.WriteLine(string.Format("lights: {0} photons traced for {1} light(s): {2} deposits on the ground, {3} on meshes ({4:F1}s)",
                            set.Photons.Emitted, perBake[b].Count, set.Photons.TerrainDeposits, set.Photons.MeshDeposits, sw.Elapsed.TotalSeconds));
                    }
                    lampField[b] = set;
                    // texels: the photons carry the albedo, kappa takes their radiance to model units; probes: the surfaces
                    // they see reflect albedo / pi of what lands on them
                    double gain = lampGain[b];
                    for (int c = 0; c < 3; c++) { b.Model.LightBounce[c] = b.Model.Kappa[c] * gain; b.Model.LampAlbedo[c] = alb[c] * gain; }
                    b.Model.MeshLampAlbedo = malb.Select(v => v * gain).ToArray();
                    Console.Out.WriteLine(string.Format("lights {0}: the lamps' light bounces off the ground with albedo ({1:F3}, {2:F3}, {3:F3}), off meshes with ({4:F3}, {5:F3}, {6:F3}){7}",
                        Path.GetFileName(b.Static), alb[0], alb[1], alb[2], malb[0], malb[1], malb[2], gain != 1 ? string.Format(" x gain {0:F2}", gain) : ""));
                }
                Console.Out.WriteLine(string.Format("lights: {0} light set(s) ready ({1:F1}s)", sets.Count, sw.Elapsed.TotalSeconds));
            }

            // ---- custom sun: the lighting turned about +Y after the fit (sky looked up at R(-a) d, sun at R(a) s); a new
            // elevation moves the VE sun (SunRotationY) and the baked sun follows it through SunShadowHeightScale
            double skyRot = sunRotateDeg * Math.PI / 180;
            var sunsOut = suns;
            // --sky-scale / --sun-scale: the fitted sky (and the sky-lit ground) and the bounced sun, per channel; the
            // level's own VE copy gets the sun's colour scaled the same way so its runtime sun matches
            foreach (Bake b in bakes)
            {
                b.UserSkyScale = skyScale; b.UserSunScale = sunScale;
                for (int c = 0; c < 3; c++)
                {
                    if (skyScale != null)
                    {
                        for (int p = 0; p < b.Model.W[c].Length; p++) b.Model.W[c][p] *= skyScale[c];
                        b.Model.Bounce[c][0] *= skyScale[c];
                    }
                    if (sunScale != null) b.Model.Bounce[c][1] *= sunScale[c];
                }
            }
            if (skyScale != null || sunScale != null)
                Console.Out.WriteLine(string.Format("lighting scaled: sky ({0}), sun ({1})",
                    skyScale != null ? string.Join(", ", skyScale.Select(v => v.ToString("0.###"))) : "1",
                    sunScale != null ? string.Join(", ", sunScale.Select(v => v.ToString("0.###"))) : "1"));
            if (sunScale != null && cloneInto == null)
                Console.Out.WriteLine("warning: --sun-scale without --clone-into scales the baked bounce only; the level's runtime sun keeps its colour");
            if (sunRotateDeg != 0 || sunElevation.HasValue || (sunScale != null && cloneInto != null))
            {
                if (cloneInto == null) { Console.Error.WriteLine("enlighten relight: --sun-rotate / --sun-elevation need --clone-into (the level's own VE copy carries the new sun)"); return 1; }
                sunsOut = new float[bakes.Count][];
                for (int i = 0; i < bakes.Count; i++)
                {
                    Bake b = bakes[i];
                    if (sunElevation.HasValue)
                    {
                        // VE convention: sun = (sin X cos Y, sin Y, cos X cos Y); the turn is X - a
                        double X = Math.Atan2(b.VeSun[0], b.VeSun[2]) - skyRot, Y = sunElevation.Value * Math.PI / 180;
                        var ve = new[] { (float)(Math.Sin(X) * Math.Cos(Y)), (float)Math.Sin(Y), (float)(Math.Cos(X) * Math.Cos(Y)) };
                        sunsOut[i] = ScaledSun(ve, b.HeightScale);
                    }
                    else
                    {
                        double rx, rz;
                        TerrainRelight.RotateY(b.Sun[0], b.Sun[2], skyRot, out rx, out rz);
                        sunsOut[i] = new[] { (float)rx, b.Sun[1], (float)rz };
                    }
                    b.SunOut = sunsOut[i];
                    if (!SetSunInLevel(b, cloneInto, sunRotateDeg, sunElevation, logger, sunScale)) return 1;
                    Console.Out.WriteLine(string.Format("bake {0}: sun now ({1:F3}, {2:F3}, {3:F3}), elevation {4:F1} deg{5}", Path.GetFileName(b.Static),
                        sunsOut[i][0], sunsOut[i][1], sunsOut[i][2], Math.Asin(Math.Max(-1, Math.Min(1, sunsOut[i][1]))) * 180 / Math.PI,
                        sunElevation.HasValue ? string.Format(" (VE {0:F1} deg, SunShadowHeightScale {1})", sunElevation.Value, b.HeightScale) : ""));
                }
                Console.Out.WriteLine(string.Format("sun {0} in the bake and in the level's own VE / shadow-extrusion copies",
                    string.Join(" and ", new[] { sunRotateDeg != 0 ? string.Format("turned {0:F1} deg about +Y", sunRotateDeg) : null,
                                                 sunElevation.HasValue ? string.Format("raised to {0:F1} deg in the VE", sunElevation.Value) : null }.Where(s => s != null))));
            }

            // ---- T2: tiles for new ground the host's tiles do not cover, in a grown atlas
            int firstNew = layout.Terrain.Count;
            if (extendTile > 0)
            {
                int added = ExtendTiles(bakes, target, extendTile, extendTexels, baseDds, probes);
                if (added < 0) return 1;
                Console.Out.WriteLine(string.Format("extend: {0} tile(s) of {1} m at {2} texels for ground the host's tiles do not cover; atlas now {3}x{4}",
                    added, extendTile, extendTexels, layout.Width, layout.Height));
            }
            // ---- T3: charts for placed meshes, in rows under the atlas
            List<EnlightenCharts.Chart> charts = null;
            if (chartParts.Count > 0)
            {
                var dbs = bakes.Select(b => b.Db).ToList();
                charts = EnlightenCharts.Plan(chartParts, dbs, chartDensity, chartAll, chartMatch);
                if (charts == null) return 1;
                EnlightenCharts.Place(charts, dbs, baseDds);
                EnlightenCharts.Rasterise(charts);
                Console.Out.WriteLine(string.Format("charts: {0} placement(s), {1} texels covered; atlas now {2}x{3}", charts.Count,
                    charts.Sum(c => c.Raster.Covered), layout.Width, layout.Height));
                foreach (var c in charts)
                    Console.Out.WriteLine(string.Format("  {0:x8} {1} at ({2:F1}, {3:F1}, {4:F1}): {5}x{6} texels at ({7}, {8}), {9} covered",
                        c.Placement.Key, c.Placement.MeshPath, c.Placement.Trans[0], c.Placement.Trans[1], c.Placement.Trans[2], c.W, c.H, c.X, c.Y, c.Raster.Covered));
            }
            if (onlyNew && firstNew == layout.Terrain.Count && (charts == null || charts.Count == 0))
            { Console.Error.WriteLine("enlighten relight: --only-new-tiles with no new tile"); return 1; }
            int fromTile = onlyNew ? firstNew : 0;

            // ---- predict the new terrain, encode, splice, import
            var tiles = new List<int[]>();
            foreach (var t in layout.Terrain) { int x0, x1, y0, y1; t.TexelRect(layout.Width, layout.Height, out x0, out x1, out y0, out y1); tiles.Add(new[] { x0, x1, y0, y1 }); }
            if (tiles.Any(r => ((r[0] | r[1] | r[2] | r[3]) & 3) != 0))
                Console.Out.WriteLine("tiles: some rects are not on 4x4 block boundaries; their blocks are re-encoded with the neighbouring texels kept");
            var pred = bakes.ToDictionary(b => b, b => new TileTexels[layout.Terrain.Count]);
            var lampTexels = bakes.ToDictionary(b => b, b => 0);
            for (int ti = fromTile; ti < layout.Terrain.Count; ti++)
            {
                TexelMoments m = TileMoments(target, layout, ti, sunsOut, soup, skyRot);
                Lamps attached = null; bool any = false;
                foreach (Bake b in bakes)
                {
                    Lamps f; lampField.TryGetValue(b, out f);
                    if (!any || f != attached) { AttachLamps(m, f, target); attached = f; any = true; }
                    lampTexels[b] += m.LampTexels;
                    var tt = new TileTexels(m.Count);
                    b.Model.Predict(m, tt.L0, tt.D);
                    if (m.X0 != null)
                    {
                        tt.Lamp = new bool[m.Count]; tt.LampLum = new float[m.Count];
                        double[] lb = b.Model.LightBounce;
                        for (int t = 0; t < m.Count; t++)
                        {
                            tt.Lamp[t] = m.X0[t * 3] + m.X0[t * 3 + 1] + m.X0[t * 3 + 2] > 0;
                            tt.LampLum[t] = (float)(0.2126 * lb[0] * m.X0[t * 3] + 0.7152 * lb[1] * m.X0[t * 3 + 1] + 0.0722 * lb[2] * m.X0[t * 3 + 2]);
                        }
                    }
                    for (int t = 0; t < m.Count; t++)
                    {
                        byte sv = (byte)Math.Max(0, Math.Min(255, Math.Round(SkyModel.Interp(m.SkyVis[t], b.Model.SkyQuantX, b.Model.SkyQuantY))));
                        byte ao = (byte)Math.Max(0, Math.Min(255, Math.Round(SkyModel.Interp(m.Ao[t], b.Model.AoQuantX, b.Model.AoQuantY))));
                        tt.Sky[t * 4] = tt.Sky[t * 4 + 1] = tt.Sky[t * 4 + 2] = sv; tt.Sky[t * 4 + 3] = ao;
                    }
                    pred[b][ti] = tt;
                }
            }
            // chart texels: the same model on the rasterised surfaces (their own heights and normals)
            var chartRects = new List<int[]>();
            var chartPred = bakes.ToDictionary(b => b, b => new List<TileTexels>());
            if (charts != null)
                foreach (var c in charts)
                {
                    var idx = Enumerable.Range(0, c.W * c.H).Where(i => c.Raster.State[i] != 0).ToArray();
                    var xs = idx.Select(i => c.Raster.X[i]).ToArray(); var zs = idx.Select(i => c.Raster.Z[i]).ToArray();
                    var ys = idx.Select(i => c.Raster.Y[i]).ToArray();
                    var nrm = new float[idx.Length * 3];
                    for (int k = 0; k < idx.Length; k++) for (int ax = 0; ax < 3; ax++) nrm[k * 3 + ax] = c.Raster.N[idx[k] * 3 + ax];
                    TexelMoments m = TerrainRelight.Moments(target, xs, zs, sunsOut, 0.0, soup, 80.0, ys, nrm, skyRot);
                    chartRects.Add(new[] { c.X, c.X + c.W, c.Y, c.Y + c.H });
                    Lamps attached = null; bool any = false;
                    foreach (Bake b in bakes)
                    {
                        Lamps f; lampField.TryGetValue(b, out f);
                        if (!any || f != attached) { AttachLamps(m, f, target, ys, nrm); attached = f; any = true; }
                        lampTexels[b] += m.LampTexels;
                        var part = new TileTexels(m.Count);
                        b.Model.Predict(m, part.L0, part.D);
                        var tt = new TileTexels(c.W * c.H);
                        for (int k = 0; k < idx.Length; k++)
                        {
                            int t = idx[k];
                            // --chart-tint: a diagnostic colour on the chart irradiance, to see in game that the charts are read
                            for (int q = 0; q < 3; q++) tt.L0[t * 3 + q] = (float)(part.L0[k * 3 + q] * chartTint[q]);
                            for (int q = 0; q < 9; q++) tt.D[t * 9 + q] = part.D[k * 9 + q];
                            byte sv = (byte)Math.Max(0, Math.Min(255, Math.Round(SkyModel.Interp(m.SkyVis[k], b.Model.SkyQuantX, b.Model.SkyQuantY))));
                            byte ao = (byte)Math.Max(0, Math.Min(255, Math.Round(SkyModel.Interp(m.Ao[k], b.Model.AoQuantX, b.Model.AoQuantY))));
                            tt.Sky[t * 4] = tt.Sky[t * 4 + 1] = tt.Sky[t * 4 + 2] = sv; tt.Sky[t * 4 + 3] = ao;
                        }
                        chartPred[b].Add(tt);
                    }
                }
            Console.Out.WriteLine(string.Format("predicted ({0:F1}s)", sw.Elapsed.TotalSeconds));
            foreach (Bake b in bakes)
                if (lampField.ContainsKey(b))
                    Console.Out.WriteLine(string.Format("lights {0}: {1} texel(s) carry the lamps' bounced light (photons within {2} m)", Path.GetFileName(b.Static), lampTexels[b], LampPhotons.Radius));
            // how far the new tiles are from the shipped ones (on the host's own terrain this is the model's error;
            // on a new terrain it measures the change): luminance of L0, all texels and open-sky texels
            foreach (Bake b in bakes)
            {
                var all = new List<double>(); var open = new List<double>(); var lamp = new List<double>(); var lampRatio = new List<double>();
                // lamp texels binned by the lights' predicted share of L0: shipped / (prediction without the lights)
                // should follow 1 + share where the lights are modelled right
                double[] shareEdges = { 0, 0.01, 0.05, 0.2, 1, double.MaxValue };
                double excessShipped = 0, modelled = 0, baseSum = 0;
                var binShipped = Enumerable.Range(0, shareEdges.Length - 1).Select(_ => new List<double>()).ToArray();
                var binShare = Enumerable.Range(0, shareEdges.Length - 1).Select(_ => new List<double>()).ToArray();
                for (int ti = fromTile; ti < Math.Min(firstNew, layout.Terrain.Count); ti++)
                {
                    TileTexels s0 = shipped[b][ti], p0 = pred[b][ti];
                    if (s0 == null || p0 == null) continue;
                    for (int t = 0; t < p0.L0.Length / 3; t += 3)
                    {
                        double ls = 0.2126 * s0.L0[t * 3] + 0.7152 * s0.L0[t * 3 + 1] + 0.0722 * s0.L0[t * 3 + 2];
                        double lp = 0.2126 * p0.L0[t * 3] + 0.7152 * p0.L0[t * 3 + 1] + 0.0722 * p0.L0[t * 3 + 2];
                        if (ls <= 1e-6) continue;
                        double e = Math.Abs(lp / ls - 1);
                        all.Add(e);
                        if (s0.Sky[t * 4] >= 235) open.Add(e);
                        if (p0.Lamp != null && p0.Lamp[t])
                        {
                            lamp.Add(e); lampRatio.Add(lp / ls);
                            double noLamp = lp - p0.LampLum[t];
                            excessShipped += ls - noLamp; modelled += p0.LampLum[t]; baseSum += noLamp;
                            if (noLamp > 1e-6)
                            {
                                double share = p0.LampLum[t] / noLamp;
                                int k = 0;
                                while (k < shareEdges.Length - 2 && share >= shareEdges[k + 1]) k++;
                                binShipped[k].Add(ls / noLamp); binShare[k].Add(share);
                            }
                        }
                    }
                }
                Func<List<double>, double, double> q = (l, f) => l.Count == 0 ? 0 : l[(int)Math.Min(l.Count - 1, l.Count * f)];
                all.Sort(); open.Sort(); lamp.Sort(); lampRatio.Sort();
                Console.Out.WriteLine(string.Format("compare {0}: new tiles vs shipped, L0 luminance |rel diff| median {1:P1} p90 {2:P1} (all {3} texels); " +
                    "open sky median {4:P1} p90 {5:P1} ({6})", Path.GetFileName(b.Static), q(all, 0.5), q(all, 0.9), all.Count, q(open, 0.5), q(open, 0.9), open.Count));
                if (lamp.Count > 0)
                {
                    Console.Out.WriteLine(string.Format("compare {0}: texels that see lamp-lit surfaces: |rel diff| median {1:P1} p90 {2:P1}; new / shipped median {3:F3} " +
                        "(p10 {4:F3}, p90 {5:F3}; {6} texels)", Path.GetFileName(b.Static), q(lamp, 0.5), q(lamp, 0.9), q(lampRatio, 0.5), q(lampRatio, 0.1), q(lampRatio, 0.9), lamp.Count));
                    // totals over those texels: what the shipped bake has above the lamp-less model vs what the lamps add
                    Console.Out.WriteLine(string.Format("   totals: shipped - model without lights = {0:G4}, lights modelled = {1:G4} (ratio {2:F2}); model without lights = {3:G4}",
                        excessShipped, modelled, modelled > 0 ? excessShipped / modelled : 0, baseSum));
                    for (int k = 0; k < binShipped.Length; k++)
                    {
                        if (binShipped[k].Count == 0) continue;
                        binShipped[k].Sort(); binShare[k].Sort();
                        Console.Out.WriteLine(string.Format("   lights' share {0:P0}..{1}: shipped / (new without lights) median {2:F3} (p25 {3:F3}, p75 {4:F3}), share median {5:P1}, {6} texels",
                            shareEdges[k], shareEdges[k + 1] > 1e9 ? "" : shareEdges[k + 1].ToString("P0"), q(binShipped[k], 0.5), q(binShipped[k], 0.25), q(binShipped[k], 0.75),
                            q(binShare[k], 0.5), binShipped[k].Count));
                    }
                }
            }
            int failures = 0;
            foreach (Bake b in bakes)
                failures += WriteAtlases(b, layout, tiles.Skip(fromTile).Concat(chartRects).ToList(),
                    pred[b].Skip(fromTile).Concat(chartPred[b]).ToArray(), baseDds, outDir, import, logger);
            if (charts != null && charts.Count > 0 && import) failures += EnlightenCharts.SetOverrides(charts, logger);
            if (probes) failures += RelightProbes(bakes, host, target, soup, exclude, outDir, import, skyRot, lampField, lightReach);
            else if (extendTile > 0 || (charts != null && charts.Count > 0)) failures += WriteDatabases(bakes, outDir, import);
            Console.Out.WriteLine(string.Format("enlighten relight: {0} bake(s), {1} tiles each, {2} ({3:F1}s); files in {4}", bakes.Count,
                layout.Terrain.Count, failures == 0 ? (import ? "imported" : "written") : failures + " FAILED", sw.Elapsed.TotalSeconds, outDir));
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// `enlighten export <level> --out <dir>`: every bake the level loads, as files: the Enlighten database and static
        /// database (framed: 16-byte res meta, then the payload), the five atlases (DDS as stored, irradiance as Radiance
        /// HDR, directions and sky visibility as PNG) and bake.json - the sun and terrain colour the bake was made with
        /// (and where they come from), the atlas, every terrain tile (world box, texel rect), every mesh chart (key, texel
        /// rect) and every probe set.
        /// </summary>
        public static int Export(string[] args, ExportLogger logger)
        {
            string level = null, outDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (level == null || outDir == null) { Console.Error.WriteLine("Usage: frostycli enlighten export <level-or-lighting-subworld> --out <dir>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("enlighten export: AssetManager not initialized."); return 1; }
            List<Bake> bakes = FindBakes(level, logger);
            if (bakes.Count == 0) { Console.Error.WriteLine("enlighten export: no Enlighten bakes found under " + level); return 1; }
            foreach (Bake b in bakes) if (!LoadBake(b)) return 1;
            Bake rhoDonor = bakes.FirstOrDefault(x => x.RhoOverridden);
            foreach (Bake b in bakes)
                if (!b.RhoOverridden && rhoDonor != null) { b.Rho = (double[])rhoDonor.Rho.Clone(); b.RhoSource = rhoDonor.Ve + " (own VE does not override it)"; }
            int failures = 0;
            foreach (Bake b in bakes)
            {
                string dir = Path.Combine(outDir, Path.GetFileName(b.Static));
                Directory.CreateDirectory(dir);
                try
                {
                    byte[] m1, d1 = b.Db.Write(out m1);
                    File.WriteAllBytes(Path.Combine(dir, "database.framed.bin"), Frame(m1, d1));
                    if (b.Fls != null) { byte[] m2, d2 = b.Fls.Write(out m2); File.WriteAllBytes(Path.Combine(dir, "static.framed.bin"), Frame(m2, d2)); }
                    foreach (string role in Roles)
                    {
                        File.WriteAllBytes(Path.Combine(dir, role + ".dds"), ExportTexture(b.Textures[role], "dds", dir));
                        string fmt = role == "StaticIrradianceTexture" ? "hdr" : "png";
                        File.WriteAllBytes(Path.Combine(dir, role + "." + fmt), ExportTexture(b.Textures[role], fmt, dir));
                    }
                    var db = b.Db;
                    var tiles = new JArray();
                    for (int ti = 0; ti < db.Terrain.Count; ti++)
                    {
                        var t = db.Terrain[ti]; int x0, x1, y0, y1;
                        t.TexelRect(db.Width, db.Height, out x0, out x1, out y0, out y1);
                        tiles.Add(new JObject { ["index"] = ti, ["world"] = new JArray(t.MinX, t.MinZ, t.MaxX, t.MaxZ), ["texels"] = new JArray(x0, x1, y0, y1) });
                    }
                    var charts = new JArray();
                    foreach (var mi in db.MeshInstances)
                        if (mi.UvTransforms.Count > 0)
                            charts.Add(new JObject
                            {
                                ["key"] = mi.Hash.ToString("x8"),
                                ["texels"] = new JArray(Math.Round(mi.OffsetU() * db.Width, 3), Math.Round(mi.OffsetV() * db.Height, 3),
                                                         Math.Round(mi.ScaleU() * db.Width, 3), Math.Round(mi.ScaleV() * db.Height, 3))
                            });
                    var sets = new JArray();
                    foreach (var ps in db.ProbeSets)
                        sets.Add(new JObject
                        {
                            ["guid"] = ps.Guid.ToString(), ["dims"] = new JArray(ps.DimX, ps.DimY, ps.DimZ), ["count"] = ps.Count, ["offset"] = ps.GlobalOffset,
                            ["priority"] = ps.Priority, ["blend"] = ps.BlendDistance, ["terrainGrid"] = ps.IsTerrainGrid,
                            ["aabb"] = new JArray(ps.MinX, ps.MinY, ps.MinZ, ps.MaxX, ps.MaxY, ps.MaxZ)
                        });
                    var j = new JObject
                    {
                        ["static"] = b.Static, ["dynamic"] = b.Dynamic, ["subWorld"] = b.SubWorld, ["ve"] = b.Ve,
                        ["sun"] = new JArray(b.Sun), ["sunSource"] = b.ExtrusionSource ?? "ve", ["veSun"] = b.VeSun != null ? new JArray(b.VeSun) : null,
                        ["terrainColor"] = new JArray(b.Rho), ["terrainColorSource"] = b.RhoSource ?? "type default",
                        ["sunIrradianceVe"] = new JArray(b.ESun),
                        ["atlas"] = new JObject { ["width"] = db.Width, ["height"] = db.Height, ["format"] = db.OutputFormat, ["preExposure"] = db.PreExposure },
                        ["textures"] = new JObject(Roles.Select(r => new JProperty(r, b.Textures[r]))),
                        ["tiles"] = tiles, ["charts"] = charts, ["probeSets"] = sets,
                        ["staticProbes"] = b.Fls != null ? b.Fls.ProbeCount : 0
                    };
                    File.WriteAllText(Path.Combine(dir, "bake.json"), j.ToString());
                    Console.Out.WriteLine(string.Format("{0}: atlas {1}x{2}, {3} tiles, {4} charts, {5} probe sets ({6} probes) -> {7}", Path.GetFileName(b.Static),
                        db.Width, db.Height, db.Terrain.Count, charts.Count, sets.Count, b.Fls != null ? b.Fls.ProbeCount : 0, dir));
                }
                catch (Exception ex) { Console.Error.WriteLine("enlighten export: " + b.Static + ": " + ex.Message); failures++; }
            }
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Probes: a self-check on the host terrain first (open-ground terrain probes recomputed with the fitted sky must
        /// match the shipped ones), then the terrain grids move onto the new ground and every probe is re-lit.
        /// </summary>
        static int RelightProbes(List<Bake> bakes, DenseHeightfield host, DenseHeightfield target, GeometrySoup soup, double exclude,
                                 string outDir, bool import, double skyRot = 0, Dictionary<Bake, Lamps> lampField = null, double lightReach = 40)
        {
            int failures = 0;
            foreach (Bake b in bakes)
            {
                if (b.Fls == null) { Console.Out.WriteLine("probes: " + b.Static + " has no static database; skipped"); continue; }
                // calibration on copies: the sky part and the bounce part of the open-ground terrain probes, fitted per
                // channel to the shipped probes (what the bake's sky box and ground really were), then a self-check
                double r = exclude > 0 ? exclude : 0;
                Func<EnlightenDatabase.ProbeSet, bool> openGround = ps =>
                {
                    if (!ps.IsTerrainGrid) return false;
                    float x, y, z; ps.Position(0, out x, out y, out z);
                    return Math.Max(Math.Abs(x), Math.Abs(z)) > r + 100;
                };
                var skyPart = StaticEnlightenDatabase.Parse(b.Fls.Write(out byte[] ms), ms);
                var bouncePart = StaticEnlightenDatabase.Parse(b.Fls.Write(out byte[] mb), mb);
                // the check is against the host's shipped probes: the model as fitted, without --sky/--sun-scale
                SkyModel check = b.Model.Unscaled(b.UserSkyScale, b.UserSunScale);
                ProbeRelight.Relight(b.Db, skyPart, host, null, check, b.Sun, openGround, part: 1);
                ProbeRelight.Relight(b.Db, bouncePart, host, null, check, b.Sun, openGround, part: 2);
                // samples (shipped, sky part, bounce part) per channel; the relight is linear in both scales, so any
                // scale pair's self-check follows from the two parts without re-lighting
                var dirs = new[] { new[] { 0.0, 1, 0 }, new[] { 0.0, -1, 0 }, new[] { 1.0, 0, 0 }, new[] { -1.0, 0, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 0, -1 } };
                var sy = new List<double>[3]; var sa = new List<double>[3]; var sb = new List<double>[3];
                for (int ch = 0; ch < 3; ch++) { sy[ch] = new List<double>(); sa[ch] = new List<double>(); sb[ch] = new List<double>(); }
                foreach (var ps in b.Db.ProbeSets)
                {
                    if (!openGround(ps)) continue;
                    for (int j = 0; j < ps.Count; j += 3)
                    {
                        int pi = (int)ps.GlobalOffset + j;
                        foreach (var n in dirs)
                            for (int ch = 0; ch < 3; ch++)
                            {
                                double y = ProbeRelight.Evaluate(b.Fls, pi, ch, n[0], n[1], n[2]);
                                if (Math.Abs(y) < 1e-4) continue;
                                sy[ch].Add(y);
                                sa[ch].Add(ProbeRelight.Evaluate(skyPart, pi, ch, n[0], n[1], n[2]));
                                sb[ch].Add(ProbeRelight.Evaluate(bouncePart, pi, ch, n[0], n[1], n[2]));
                            }
                    }
                }
                var one = new[] { 1.0, 1.0, 1.0 };
                var skyScale = new double[3]; var bounceScale = new double[3];
                for (int ch = 0; ch < 3; ch++) FitScales(sy[ch], sa[ch], sb[ch], out skyScale[ch], out bounceScale[ch]);
                double med1, p901, med2, p902;
                SelfCheck(sy[1], sa[1], sb[1], 1, 1, out med1, out p901);
                SelfCheck(sy[1], sa[1], sb[1], skyScale[1], bounceScale[1], out med2, out p902);
                bool useScales = med2 < med1;
                Console.Out.WriteLine(string.Format("probes {0}: self-check on the host, open-ground probes (G, 6 directions, n={1}): |rel err| median {2:P1}, p90 {3:P1}; " +
                    "with sky x ({4:F3}, {5:F3}, {6:F3}), bounce x ({7:F3}, {8:F3}, {9:F3}) fitted on them: median {10:P1}, p90 {11:P1} -> {12}",
                    Path.GetFileName(b.Static), sy[1].Count, med1, p901, skyScale[0], skyScale[1], skyScale[2], bounceScale[0], bounceScale[1], bounceScale[2],
                    med2, p902, useScales ? "scales kept" : "scales dropped"));
                if (!useScales) { skyScale = one; bounceScale = (double[])one.Clone(); }
                int moved = ProbeRelight.MoveTerrainGrids(b.Db, target);
                Lamps lamps = null;
                if (lampField != null) lampField.TryGetValue(b, out lamps);
                int lit = ProbeRelight.Relight(b.Db, b.Fls, target, soup, b.Model, b.SunOut ?? b.Sun, ps => true, skyScale: skyScale, bounceScale: bounceScale,
                                               skyRotate: skyRot, lights: lamps != null ? lamps.Field : null, lightReach: lightReach,
                                               photons: lamps != null ? lamps.Photons : null);
                if (lamps != null)
                    Console.Out.WriteLine(string.Format("probes {0}: {1} probe(s) within {2} m of a light carry its bounce", Path.GetFileName(b.Static),
                        ProbeRelight.LampProbesLastRun, lightReach));
                byte[] endMeta, flsMeta;
                byte[] endData = b.Db.Write(out endMeta), flsData = b.Fls.Write(out flsMeta);
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(b.DbRes.Name) + ".framed.bin"), Frame(endMeta, endData));
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(b.FlsRes.Name) + ".framed.bin"), Frame(flsMeta, flsData));
                if (import)
                {
                    try
                    {
                        App.AssetManager.ModifyRes(b.DbRes.Name, endData, endMeta);
                        App.AssetManager.ModifyRes(b.FlsRes.Name, flsData, flsMeta);
                    }
                    catch (Exception ex) { Console.Error.WriteLine("probes: writing " + b.Static + ": " + ex.Message); failures++; }
                }
                Console.Out.WriteLine(string.Format("probes {0}: {1} terrain-grid probes moved onto the new ground, {2} probes re-lit, {3} kept as shipped " +
                    "(under the ground or off the terrain){4}", Path.GetFileName(b.Static), moved, lit, ProbeRelight.KeptLastRun, import ? ", databases imported" : ""));
            }
            return failures;
        }

        /// <summary>
        /// T2: cover the target terrain's frame with tiles on a grid of <paramref name="tileMetres"/> aligned to the frame
        /// origin (so each tile is a quadtree node of the terrain: the runtime uses the first record whose box covers a
        /// patch, shrunk 1%). A grid cell gets a tile unless an existing tile covers its centre. New tiles are packed in rows
        /// below the atlas; every bake's database grows the same way (<see cref="EnlightenDatabase.ResizeAtlas"/>) and so do
        /// the base DDS files. Returns the number of tiles added, or -1.
        /// </summary>
        static int ExtendTiles(List<Bake> bakes, DenseHeightfield target, double tileMetres, int texels, Dictionary<string, byte[]> baseDds,
                               bool grids = false)
        {
            if ((texels & 3) != 0 || texels < 4) { Console.Error.WriteLine("enlighten relight: --extend texels must be a multiple of 4"); return -1; }
            EnlightenDatabase layout = bakes[0].Db;
            double fx0 = target.MinX, fz0 = target.MinZ, ext = target.Spacing * (target.N - 1);
            int cells = (int)Math.Round(ext / tileMetres);
            if (cells < 1 || Math.Abs(cells * tileMetres - ext) > 1e-3 * ext)
            { Console.Error.WriteLine(string.Format("enlighten relight: the terrain frame ({0} m) is not a whole number of {1} m tiles", ext, tileMetres)); return -1; }
            var boxes = new List<double[]>();
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    double x0 = fx0 + i * tileMetres, z0 = fz0 + j * tileMetres, cx = x0 + tileMetres / 2, cz = z0 + tileMetres / 2;
                    if (layout.Terrain.Any(t => cx >= t.MinX && cx <= t.MaxX && cz >= t.MinZ && cz <= t.MaxZ)) continue;
                    boxes.Add(new[] { x0, z0, x0 + tileMetres, z0 + tileMetres });
                }
            if (boxes.Count == 0) return 0;
            uint w = layout.Width, h = layout.Height;
            int perRow = Math.Max(1, (int)(w / texels));
            int rows = (boxes.Count + perRow - 1) / perRow;
            uint newH = (uint)((h + rows * texels + 3) / 4 * 4);
            int top = (int)((h + 3) / 4 * 4);
            newH = (uint)Math.Max(newH, top + rows * texels);
            foreach (Bake b in bakes)
            {
                b.Db.ResizeAtlas(w, newH);
                for (int k = 0; k < boxes.Count; k++)
                {
                    var t = new EnlightenDatabase.TerrainLightmap();
                    t.Place(boxes[k][0], boxes[k][1], boxes[k][2], boxes[k][3], k % perRow * texels, top + k / perRow * texels, texels, w, newH);
                    b.Db.Terrain.Add(t);
                }
            }
            foreach (string key in baseDds.Keys.ToList()) baseDds[key] = AtlasIo.GrowDds(baseDds[key], (int)w, (int)newH);
            // probes for the new ground: the shipped terrain grids are one per tile, so the new tiles get one each too
            // (the probe relight then lights them); without --probes they would stay black, so they are only added with it
            if (grids)
                foreach (Bake b in bakes)
                {
                    var template = b.Db.ProbeSets.FirstOrDefault(ps => ps.IsTerrainGrid);
                    if (template == null || b.Fls == null) continue;
                    int dims = (int)template.DimX;
                    uint next = b.Db.ProbeSets.Where(ps => ps.Count > 0).Select(ps => ps.GlobalOffset + ps.Count).DefaultIfEmpty(0u).Max();
                    if (next != b.Fls.ProbeCount) { Console.Error.WriteLine("enlighten relight: " + b.Static + ": probe offsets do not end at the FLS probe count; no grids added"); continue; }
                    foreach (double[] box in boxes)
                    {
                        byte[] seed = System.Security.Cryptography.MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(
                            string.Format(System.Globalization.CultureInfo.InvariantCulture, "terrain-grid {0} {1} {2} {3}", box[0], box[1], box[2], box[3])));
                        var ps = EnlightenDatabase.ProbeSet.TerrainGrid(template, box[0], box[1], box[2], box[3], dims, (x, z) => target.Sample(x, z), next, new Guid(seed));
                        b.Db.ProbeSets.Add(ps);
                        next += ps.Count;
                    }
                    var grown = new byte[next * 144];
                    Array.Copy(b.Fls.Probes, grown, b.Fls.Probes.Length);
                    b.Fls.Probes = grown;
                }
            return boxes.Count;
        }

        /// <summary>
        /// Give a bake its own lighting assets under <paramref name="prefix"/>: the five atlases (EBX + res + chunk), the
        /// StaticEnlightenData and the EnlightenDataAsset (EBX + database res), the static data pointed at the new atlases,
        /// the lighting sub-world's Static/DynamicEnlightenEntityData pointed at the new data, and the new assets added
        /// to that sub-world's bundle with their resources. The bake then reads and writes the copies, so the host level
        /// (and every other level that loads its lighting) keeps the shipped bake. Idempotent: existing copies are reused.
        /// </summary>
        internal static bool CloneBakeInto(Bake b, string prefix, ExportLogger logger)
        {
            var fmt = new OutputFormatter(json: false, output: TextWriter.Null);
            Func<string, string> copyOf = src => prefix + "/" + Path.GetFileName(src);
            Func<string, string[], bool> run = (op, a) =>
            {
                int rc = WriteOpBus.Execute(op, a, logger, fmt);
                if (rc != 0) Console.Error.WriteLine("enlighten relight: --clone-into: " + op + " " + string.Join(" ", a) + " failed");
                return rc == 0;
            };
            var newTex = new Dictionary<string, string>();
            foreach (string role in Roles)
            {
                string dst = copyOf(b.Textures[role]);
                if (App.AssetManager.GetEbxEntry(dst) == null)
                {
                    EbxAssetEntry made;
                    string err = FrostyCLI.Adapters.Texture.TextureCloner.Clone(b.Textures[role], dst, out made);
                    if (err != null) { Console.Error.WriteLine("enlighten relight: --clone-into " + dst + ": " + err); return false; }
                }
                newTex[role] = dst;
            }
            string st = copyOf(b.Static), dy = copyOf(b.Dynamic);
            foreach (var pair in new[] { Tuple.Create(b.Static, st), Tuple.Create(b.Dynamic, dy) })
                if (App.AssetManager.GetEbxEntry(pair.Item2) == null)
                {
                    EbxAssetEntry made;
                    string err = AssetCloner.Clone(pair.Item1, pair.Item2, out made, "DatabaseResource");
                    if (err != null) { Console.Error.WriteLine("enlighten relight: --clone-into " + pair.Item2 + ": " + err); return false; }
                }
            foreach (string role in Roles)
                if (!run("ebx-set-ref", new[] { st, "--prop", role, "--target", newTex[role] })) return false;
            if (!run("ebx-set-ref", new[] { b.SubWorld, "--object", "StaticEnlightenEntityData", "--prop", "EnlightenData", "--target", st })) return false;
            if (!run("ebx-set-ref", new[] { b.SubWorld, "--object", "StaticEnlightenEntityData", "--prop", "DynamicEnlightenData", "--target", dy })) return false;
            if (!run("ebx-set-ref", new[] { b.SubWorld, "--object", "DynamicEnlightenEntityData", "--prop", "EnlightenData", "--target", dy })) return false;
            // the sub-world's own bundle
            string bundle = null;
            foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
            {
                string n = be.Name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? be.Name.Substring(6) : be.Name;
                if (string.Equals(n, b.SubWorld, StringComparison.OrdinalIgnoreCase)) { bundle = be.Name; break; }
            }
            if (bundle == null) { Console.Error.WriteLine("enlighten relight: --clone-into: no bundle named after " + b.SubWorld); return false; }
            foreach (string asset in newTex.Values.Concat(new[] { st, dy }))
                if (!run("bundle-add", new[] { asset, "--to", bundle, "--with-resources" })) return false;
            // the bake now reads and writes the copies
            b.Static = st; b.Dynamic = dy; b.Textures = newTex;
            b.DbRes = App.AssetManager.GetResEntry(Rid(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(dy)).RootObject, "DatabaseResource"));
            b.FlsRes = App.AssetManager.GetResEntry(Rid(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(st)).RootObject, "DatabaseResource"));
            if (b.DbRes == null || b.FlsRes == null) { Console.Error.WriteLine("enlighten relight: --clone-into: the copies have no database res"); return false; }
            Console.Out.WriteLine(string.Format("bake {0}: own copies under {1} (5 atlases, static and dynamic data), bundled in {2}", Path.GetFileName(st), prefix, bundle));
            return true;
        }

        /// <summary>
        /// Turn the level's sun with the bake: clone the bake's VE into <paramref name="prefix"/> (once), set its outdoor
        /// light's SunRotationX to SunRotationX - a (sun = (sin X cos Y, sin Y, cos X cos Y), so the azimuth atan2(z, x)
        /// = 90 - X grows by a), point the lighting sub-world's VE reference at the copy and bundle it; and set the
        /// sub-world's ShadowExtrusionLightDirection (the level's own copy of the setup layer) to the turned bake sun.
        /// </summary>
        internal static bool SetSunInLevel(Bake b, string prefix, double degrees, double? elevation, ExportLogger logger, double[] colorScale = null)
        {
            var fmt = new OutputFormatter(json: false, output: TextWriter.Null);
            Func<string, string[], bool> run = (op, a) =>
            {
                int rc = WriteOpBus.Execute(op, a, logger, fmt);
                if (rc != 0) Console.Error.WriteLine("enlighten relight: custom sun: " + op + " " + string.Join(" ", a) + " failed");
                return rc == 0;
            };
            if (b.Ve == null) { Console.Error.WriteLine("enlighten relight: custom sun: " + b.Static + " has no VE"); return false; }
            string ve = prefix + "/" + Path.GetFileName(b.Ve);
            if (App.AssetManager.GetEbxEntry(ve) == null && !run("ebx-clone", new[] { b.Ve, "--as", ve })) return false;
            JObject vd = EbxDumper.Dump(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(b.Ve)), resolve: true, deep: true);
            JObject ol = Objects(vd).FirstOrDefault(o => (string)o["__type"] == "OutdoorLightComponentData");
            if (ol == null) { Console.Error.WriteLine("enlighten relight: custom sun: no OutdoorLightComponentData in " + b.Ve); return false; }
            double x = (double)ol["SunRotationX"] - degrees;
            x = ((x % 360) + 360) % 360;
            if (!run("ebx-set", new[] { ve, "--object", "OutdoorLightComponentData", "--prop", "SunRotationX", "--value", x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) })) return false;
            if (elevation.HasValue &&
                !run("ebx-set", new[] { ve, "--object", "OutdoorLightComponentData", "--prop", "SunRotationY", "--value", elevation.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) })) return false;
            // --sun-scale: the runtime sun keeps the colour its baked bounce was made with
            if (colorScale != null && ol["SunColor"] is JObject sc)
            {
                string[] axes = { "x", "y", "z" };
                for (int k = 0; k < 3; k++)
                {
                    double v = (double)sc[axes[k]] * colorScale[k];
                    if (!run("ebx-set", new[] { ve, "--object", "OutdoorLightComponentData", "--prop", "SunColor." + axes[k], "--value", v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) })) return false;
                }
            }
            if (!run("ebx-set-ref", new[] { b.SubWorld, "--object", "VisualEnvironmentReferenceObjectData", "--prop", "Blueprint", "--target", ve })) return false;
            string bundle = null;
            foreach (BundleEntry be in App.AssetManager.EnumerateBundles())
            {
                string n = be.Name.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? be.Name.Substring(6) : be.Name;
                if (string.Equals(n, b.SubWorld, StringComparison.OrdinalIgnoreCase)) { bundle = be.Name; break; }
            }
            if (bundle == null || !run("bundle-add", new[] { ve, "--to", bundle })) return false;
            if (b.ExtrusionSource != null && b.SunOut != null)
            {
                string[] v = { (-b.SunOut[0]).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                               (-b.SunOut[1]).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                               (-b.SunOut[2]).ToString("R", System.Globalization.CultureInfo.InvariantCulture) };
                string[] axes = { "x", "y", "z" };
                for (int k = 0; k < 3; k++)
                    if (!run("ebx-set", new[] { b.ExtrusionSource, "--object", "ShadowExtrusionLightDirectionEntityData", "--prop", "LightDirection." + axes[k], "--value", v[k] })) return false;
            }
            b.Ve = ve;
            return true;
        }

        /// <summary>Write (and import) every bake's Enlighten database: after T2 the tile records and atlas size changed.</summary>
        static int WriteDatabases(List<Bake> bakes, string outDir, bool import)
        {
            int failures = 0;
            foreach (Bake b in bakes)
            {
                byte[] meta, data = b.Db.Write(out meta);
                File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(b.DbRes.Name) + ".framed.bin"), Frame(meta, data));
                if (!import) continue;
                try { App.AssetManager.ModifyRes(b.DbRes.Name, data, meta); }
                catch (Exception ex) { Console.Error.WriteLine("enlighten relight: writing " + b.DbRes.Name + ": " + ex.Message); failures++; }
            }
            return failures;
        }

        /// <summary>Scales s, b minimising the relative L1 error of s*a + b*c against y (IRLS from the relative L2 fit).</summary>
        static void FitScales(List<double> y, List<double> a, List<double> c, out double s, out double b)
        {
            s = 1; b = 1;
            int n = y.Count;
            if (n < 10) return;
            var w = new double[n];
            for (int i = 0; i < n; i++) w[i] = 1.0 / (y[i] * y[i]);
            for (int it = 0; it < 8; it++)
            {
                double saa = 0, sab = 0, sbb = 0, say = 0, sby = 0;
                for (int i = 0; i < n; i++)
                {
                    saa += a[i] * a[i] * w[i]; sab += a[i] * c[i] * w[i]; sbb += c[i] * c[i] * w[i]; say += a[i] * y[i] * w[i]; sby += c[i] * y[i] * w[i];
                }
                double det = saa * sbb - sab * sab;
                if (!(det > 0)) return;
                s = Math.Max(0, (say * sbb - sby * sab) / det);
                b = Math.Max(0, (sby * saa - say * sab) / det);
                for (int i = 0; i < n; i++)
                {
                    double ay = Math.Abs(y[i]), r = Math.Abs(s * a[i] + b * c[i] - y[i]);
                    w[i] = 1.0 / (ay * Math.Max(r, 0.002 * ay));
                }
            }
        }

        static void SelfCheck(List<double> y, List<double> a, List<double> c, double s, double b, out double median, out double p90)
        {
            var e = new List<double>(y.Count);
            for (int i = 0; i < y.Count; i++) e.Add(Math.Abs((s * a[i] + b * c[i]) / y[i] - 1));
            e.Sort();
            median = e.Count > 0 ? e[e.Count / 2] : 0;
            p90 = e.Count > 0 ? e[(int)(e.Count * 0.9)] : 0;
        }

        /// <summary>
        /// The terrain probes 2 m above open ground see the sunlit ground below them: their shipped simpleShL2(down)
        /// is TerrainColor x (open-ground shading + E_sun/pi x cos(sun)), which pins the sun that the bake bounced
        /// (Tatooine: 1.17-1.29 x SunIntensity x SunScale x SunColor). Returns the per-channel gain, or null.
        /// </summary>
        static double[] SunGainFromProbes(Bake b, DenseHeightfield host, double exclude, double[] flatShading)
        {
            if (b.Fls == null || b.Sun[1] <= 0.05f) return null;
            byte[] sunShadow = host.SunShadow(b.Sun);
            var down = new List<double>[] { new List<double>(), new List<double>(), new List<double>() };
            foreach (var ps in b.Db.ProbeSets)
            {
                if (!ps.IsTerrainGrid) continue;
                for (int j = 0; j < ps.Count; j += 3)
                {
                    float x, y, z; ps.Position(j, out x, out y, out z);
                    if (exclude > 0 && Math.Max(Math.Abs(x), Math.Abs(z)) <= exclude + 100) continue;
                    float nx, ny, nz; host.Normal(x, z, out nx, out ny, out nz);
                    if (ny < 0.995f) continue;
                    if (host.Lit(sunShadow, x, z) < 0.99f) continue;      // ground in the terrain's shadow bounces no sun
                    int pi = (int)ps.GlobalOffset + j;
                    for (int c = 0; c < 3; c++) down[c].Add(ProbeRelight.Evaluate(b.Fls, pi, c, 0, -1, 0));
                }
            }
            if (down[0].Count < 50) return null;
            var g = new double[3];
            for (int c = 0; c < 3; c++)
            {
                down[c].Sort();
                double d = down[c][down[c].Count / 2];
                double eSunCos = b.ESun[c] * b.Sun[1];
                if (eSunCos <= 0 || b.Rho[c] <= 0) return null;
                g[c] = Math.PI * (d / b.Rho[c] - flatShading[c]) / eSunCos;
                g[c] = Math.Max(0.0, Math.Min(8.0, g[c]));   // 0: an overcast bake bounces no sun
            }
            return g;
        }

        internal static byte[] Frame(byte[] meta, byte[] data)
        {
            var r = new byte[16 + data.Length];
            Array.Copy(meta, r, 16);
            Array.Copy(data, 0, r, 16, data.Length);
            return r;
        }

        sealed class TileTexels
        {
            public float[] L0; public float[] D; public byte[] Sky;
            public bool[] Lamp;             // texels that see a surface lit by a light (the bake's --lights), else null
            public float[] LampLum;         // the lights' share of the predicted L0 luminance, per texel
            public TileTexels(int n) { L0 = new float[n * 3]; D = new float[n * 9]; Sky = new byte[n * 4]; }
        }

        static JObject ModelJson(Bake b)
        {
            var m = b.Model;
            return new JObject
            {
                ["static"] = b.Static, ["dynamic"] = b.Dynamic, ["ve"] = b.Ve, ["sun"] = new JArray(b.Sun),
                ["sunSource"] = b.ExtrusionSource ?? "ve", ["veSun"] = b.VeSun != null ? new JArray(b.VeSun) : null, ["terrainColorSource"] = b.RhoSource,
                ["terrainColor"] = new JArray(b.Rho), ["sunIrradiance"] = new JArray(b.ESun), ["sunGain"] = b.SunGain != null ? new JArray(b.SunGain) : null,
                ["weights"] = new JArray(m.W.Select(w => new JArray(w))), ["bounce"] = new JArray(m.Bounce.Select(w => new JArray(w))),
                ["kappa"] = new JArray(m.Kappa), ["flatShading"] = new JArray(m.FlatShading), ["calibrationTexels"] = m.CalibrationTexels,
                ["lightBounce"] = new JArray(m.LightBounce)
            };
        }

        /// <summary>
        /// Light records from a `level export-lights` JSON file ({"lights": [...]}) or from an EBX asset (a lighting
        /// layer, a sub-world, a level, a prefab), walked the way export-lights walks it.
        /// </summary>
        static List<LightRecord> LoadLightRecords(string src, out string error)
        {
            error = null;
            if (File.Exists(src))
            {
                try
                {
                    JToken j = JToken.Parse(File.ReadAllText(src));
                    JToken list = j is JObject o ? o["lights"] : j;
                    if (!(list is JArray)) { error = "no \"lights\" array"; return null; }
                    return list.ToObject<List<LightRecord>>();
                }
                catch (Exception ex) { error = ex.Message; return null; }
            }
            List<string> skipped;
            List<LightRecord> records = LevelCommand.CollectLights(src, 6, out skipped, out error);
            if (records != null && skipped.Count > 0)
                Console.Out.WriteLine(string.Format("lights {0}: {1} asset(s) not walked ({2}{3})", src, skipped.Count,
                    string.Join("; ", skipped.Take(3)), skipped.Count > 3 ? "; ..." : ""));
            return records;
        }

        /// <summary>
        /// The lights of `src[@bake]` specs (export-lights JSON or an EBX path; IES profiles resolved), per bake; null on
        /// an error (reported).
        /// </summary>
        static Dictionary<Bake, List<LocalLight>> LoadLightSpecs(List<string> specs, List<Bake> bakes, string option)
        {
            var perBake = bakes.ToDictionary(b => b, b => new List<LocalLight>());
            foreach (string spec in specs)
            {
                string src = spec, filter = null;
                int at = spec.LastIndexOf('@');
                if (at > 0) { src = spec.Substring(0, at); filter = spec.Substring(at + 1); }
                string err;
                List<LightRecord> records = LoadLightRecords(src, out err);
                if (records == null) { Console.Error.WriteLine("enlighten relight: " + option + " " + src + ": " + err); return null; }
                var ok = new List<LocalLight>();
                var why = new SortedDictionary<string, int>();
                int withIes = 0;
                foreach (LightRecord r in records)
                {
                    string w;
                    LightAssetRef iesRef = r.Refs == null ? null : r.Refs.FirstOrDefault(x => x.Field == "IESProfile" && !string.IsNullOrEmpty(x.Path));
                    IesProfile ies = iesRef != null ? LoadIes(iesRef.Path) : null;
                    LocalLight l = LocalLight.FromRecord(r.Type, r.Fields, r.Trans, r.Forward, ies, out w);
                    if (l != null) { ok.Add(l); if (l.Ies != null) withIes++; }
                    else { int k; why.TryGetValue(w, out k); why[w] = k + 1; }
                }
                if (withIes > 0) Console.Out.WriteLine(string.Format("lights {0}: {1} shaped by an IES profile", src, withIes));
                var into = bakes.Where(b => filter == null || Path.GetFileName(b.Static).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (into.Count == 0) { Console.Error.WriteLine("enlighten relight: " + option + " " + spec + ": no bake matches '" + filter + "'"); return null; }
                foreach (Bake b in into) perBake[b].AddRange(ok);
                Console.Out.WriteLine(string.Format("lights {0} ({1}): {2} light(s), {3} bounce into {4}{5}", src, option, records.Count, ok.Count,
                    string.Join(", ", into.Select(b => Path.GetFileName(b.Static))),
                    why.Count == 0 ? "" : " (left out: " + string.Join(", ", why.Select(kv => kv.Value + " " + kv.Key)) + ")"));
            }
            return perBake;
        }

        /// <summary>
        /// The lamp gain the host's own bake implies: its lights traced on the host terrain and geometry, the host tiles in
        /// their reach predicted with and without them, and on the texels where the lights matter (their share of L0 at
        /// least 5%) the median of (shipped - b0 x without) / lights, b0 = the shipped / predicted ratio of the lamp texels
        /// where the lights add under 1% (what the model misses near buildings anyway). Tatooine Night: about 1.6. Returns
        /// 0 when too few texels decide it.
        /// </summary>
        static double CalibrateLampGain(Bake b, List<LocalLight> lights, DenseHeightfield host, GeometrySoup hostSoup, EnlightenDatabase layout,
                                        float[][] suns, TileTexels[] shipped, double[] alb, double[] malb, int photons, int bounces, out string report)
        {
            var ph = LampPhotons.Trace(lights, host, hostSoup, alb, photons, bounces, meshAlbedo: malb);
            double x0 = lights.Min(l => l.X - l.Radius) - 40, x1 = lights.Max(l => l.X + l.Radius) + 40;
            double z0 = lights.Min(l => l.Z - l.Radius) - 40, z1 = lights.Max(l => l.Z + l.Radius) + 40;
            double[] k = b.Model.Kappa;
            var baseRatios = new List<double>(); var samples = new List<double[]>();
            int tilesUsed = 0;
            for (int ti = 0; ti < layout.Terrain.Count; ti++)
            {
                var t = layout.Terrain[ti];
                if (t.MaxX < x0 || t.MinX > x1 || t.MaxZ < z0 || t.MinZ > z1 || shipped[ti] == null) continue;
                TexelMoments m = TileMoments(host, layout, ti, suns, hostSoup);
                TerrainRelight.LampMoments(m, host, m.Wx, m.Wz, ph);
                if (m.LampTexels == 0) continue;
                tilesUsed++;
                float[] x0s = m.X0; m.X0 = null;
                var l0 = new float[m.Count * 3]; var d = new float[m.Count * 9];
                b.Model.Predict(m, l0, d);                          // without the lights
                m.X0 = x0s;
                TileTexels s0 = shipped[ti];
                for (int q = 0; q < m.Count; q++)
                {
                    double lamp = 0.2126 * k[0] * x0s[q * 3] + 0.7152 * k[1] * x0s[q * 3 + 1] + 0.0722 * k[2] * x0s[q * 3 + 2];
                    if (lamp <= 0) continue;
                    double without = 0.2126 * l0[q * 3] + 0.7152 * l0[q * 3 + 1] + 0.0722 * l0[q * 3 + 2];
                    double ls = 0.2126 * s0.L0[q * 3] + 0.7152 * s0.L0[q * 3 + 1] + 0.0722 * s0.L0[q * 3 + 2];
                    if (without <= 1e-6 || ls <= 1e-6) continue;
                    double share = lamp / without;
                    if (share < 0.01) baseRatios.Add(ls / without);
                    else if (share >= 0.05) samples.Add(new[] { ls, without, lamp });
                }
            }
            baseRatios.Sort();
            double b0 = baseRatios.Count > 20 ? baseRatios[baseRatios.Count / 2] : 1.0;
            var gains = samples.Select(v => (v[0] - b0 * v[1]) / v[2]).OrderBy(v => v).ToList();
            if (gains.Count < 50)
            {
                report = string.Format("--host-lights: only {0} texel(s) where the host's lights matter; gain not measured", gains.Count);
                return 0;
            }
            double g = Math.Max(0.1, Math.Min(10, gains[gains.Count / 2]));
            report = string.Format("--host-lights: the shipped bake near the host's {0} light(s) implies lamp gain {1:F2} (IQR {2:F2}-{3:F2}; " +
                "{4} texels over {5} tile(s), near-building baseline {6:F2})", lights.Count, g, gains[gains.Count / 4], gains[gains.Count * 3 / 4],
                gains.Count, tilesUsed, b0);
            return g;
        }

        static readonly Dictionary<string, IesProfile> IesCache = new Dictionary<string, IesProfile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>An IesProfileAsset's profile (its SourceResource), or null.</summary>
        static IesProfile LoadIes(string path)
        {
            IesProfile p;
            if (IesCache.TryGetValue(path, out p)) return p;
            try
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(path);
                ResAssetEntry res = e != null ? App.AssetManager.GetResEntry(Rid(App.AssetManager.GetEbx(e).RootObject, "SourceResource")) : null;
                p = res != null ? IesProfile.Parse(ReadRes(res)) : null;
            }
            catch { p = null; }
            if (p == null) Console.Out.WriteLine("warning: IES profile " + path + " unreadable; its lights use their own intensity");
            IesCache[path] = p;
            return p;
        }

        /// <summary>Lamp moments of a moment set for one bake's light field (X0/X1 cleared when the bake has none).</summary>
        static void AttachLamps(TexelMoments m, Lamps lamps, DenseHeightfield hf, double[] ys = null, float[] normals = null)
        {
            if (lamps == null) { m.X0 = null; m.X1 = null; m.LampTexels = 0; return; }
            TerrainRelight.LampMoments(m, hf, m.Wx, m.Wz, lamps.Photons, ys, normals);
        }

        /// <summary>One bake's lights: their direct light on any surface (what probes see) and their photon map.</summary>
        sealed class Lamps { public LightField Field; public LampPhotons Photons; }

        static TexelMoments TileMoments(DenseHeightfield hf, EnlightenDatabase layout, int ti, float[][] suns, GeometrySoup soup = null, double skyRot = 0)
        {
            var t = layout.Terrain[ti];
            int x0, x1, y0, y1;
            t.TexelRect(layout.Width, layout.Height, out x0, out x1, out y0, out y1);
            int w = x1 - x0, h = y1 - y0;
            var xs = new double[w * h]; var zs = new double[w * h];
            for (int py = 0; py < h; py++)
                for (int px = 0; px < w; px++)
                {
                    double x, z;
                    t.TexelWorld(layout.Width, layout.Height, x0 + px, y0 + py, out x, out z);
                    xs[py * w + px] = x; zs[py * w + px] = z;
                }
            return TerrainRelight.Moments(hf, xs, zs, suns, 0.3, soup, 80.0, null, null, skyRot);
        }

        // ---------------------------------------------------------------- discovery

        internal static List<Bake> FindBakes(string root, ExportLogger logger)
        {
            var result = new List<Bake>();
            var queue = new Queue<KeyValuePair<string, int>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (!seen.Add(item.Key)) continue;
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(item.Key);
                if (entry == null) continue;
                JObject dump;
                try { dump = EbxDumper.Dump(App.AssetManager.GetEbx(entry), resolve: true, deep: true); }
                catch { continue; }
                var entities = new JArray();
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                EnlightenCommand.CollectEnlightenReferences(dump, entities, paths);
                foreach (JObject e in entities)
                {
                    if ((string)e["type"] != "StaticEnlightenEntityData") continue;
                    var b = new Bake { SubWorld = entry.Name, Static = (string)e["enlightenData"], Dynamic = (string)e["dynamicEnlightenData"] };
                    b.Ve = FindVe(dump);
                    string src;
                    b.ExtrusionSun = FindExtrusionSun(dump, entry.Name, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), out src);
                    b.ExtrusionSource = src;
                    if (b.Static != null && b.Dynamic != null) result.Add(b);
                }
                if (item.Value >= 3) continue;
                foreach (string bundle in Strings(dump, "BundleName"))
                {
                    string candidate = bundle.StartsWith("win32/", StringComparison.OrdinalIgnoreCase) ? bundle.Substring(6) : bundle;
                    queue.Enqueue(new KeyValuePair<string, int>(candidate, item.Value + 1));
                }
            }
            return result;
        }

        /// <summary>
        /// The light direction the lighting sub-world's shadow extrusion was baked with (ShadowExtrusionLightDirectionEntityData,
        /// usually in a referenced "&lt;lighting&gt;_Setup" layer), negated: the direction towards the sun at bake time.
        /// BFII_EXACT on Tatooine Sunny (equal to the VE sun); on Sunset it differs from the VE (31.6 vs 15.5 deg) and is the
        /// sun the bake's probes and lightmaps were lit with (SPEC.md 7).
        /// </summary>
        static float[] FindExtrusionSun(JObject dump, string name, int depth, HashSet<string> seen, out string source)
        {
            source = null;
            if (!seen.Add(name)) return null;
            foreach (JObject o in Objects(dump))
                if ((string)o["__type"] == "ShadowExtrusionLightDirectionEntityData" && o["LightDirection"] is JObject d)
                {
                    double x = -(double)d["x"], y = -(double)d["y"], z = -(double)d["z"], l = Math.Sqrt(x * x + y * y + z * z);
                    if (l < 1e-6) continue;
                    source = name;
                    return new[] { (float)(x / l), (float)(y / l), (float)(z / l) };
                }
            if (depth >= 2) return null;
            foreach (JObject o in Objects(dump))
            {
                if ((string)o["__type"] != "LayerReferenceObjectData" || !(o["Blueprint"] is JObject bp) || bp["__path"] == null) continue;
                string path = (string)bp["__path"];
                EbxAssetEntry le = App.AssetManager.GetEbxEntry(path);
                if (le == null || seen.Contains(path)) continue;
                JObject ld;
                try { ld = EbxDumper.Dump(App.AssetManager.GetEbx(le), resolve: true, deep: true); }
                catch { continue; }
                float[] r = FindExtrusionSun(ld, path, depth + 1, seen, out source);
                if (r != null) return r;
            }
            return null;
        }

        static string FindVe(JToken dump)
        {
            foreach (JObject o in Objects(dump))
                if ((string)o["__type"] == "VisualEnvironmentReferenceObjectData" && o["Blueprint"] is JObject bp && bp["__path"] != null)
                    return (string)bp["__path"];
            return null;
        }

        static IEnumerable<JObject> Objects(JToken t)
        {
            if (t is JObject o) { yield return o; foreach (var p in o.Properties()) foreach (var c in Objects(p.Value)) yield return c; }
            else if (t is JArray a) foreach (var v in a) foreach (var c in Objects(v)) yield return c;
        }

        static IEnumerable<string> Strings(JToken t, string field)
        {
            foreach (JObject o in Objects(t))
                if (o[field] is JValue v && v.Type == JTokenType.String && !string.IsNullOrEmpty((string)v)) yield return (string)v;
        }

        internal static bool LoadBake(Bake b)
        {
            // databases
            EbxAssetEntry dyn = App.AssetManager.GetEbxEntry(b.Dynamic);
            if (dyn == null) { Console.Error.WriteLine("enlighten relight: " + b.Dynamic + " not found"); return false; }
            ulong rid = Rid(App.AssetManager.GetEbx(dyn).RootObject, "DatabaseResource");
            ResAssetEntry dbRes = App.AssetManager.GetResEntry(rid);
            if (dbRes == null) { Console.Error.WriteLine("enlighten relight: no database for " + b.Dynamic); return false; }
            b.Db = EnlightenDatabase.Parse(ReadRes(dbRes), (dbRes.ModifiedEntry?.ResMeta ?? dbRes.ResMeta));
            b.DbRes = dbRes;
            if (b.Db.Unmodelled != null) { Console.Error.WriteLine("enlighten relight: " + dbRes.Name + " is " + b.Db.UnmodelledReason); return false; }
            // textures
            EbxAssetEntry st = App.AssetManager.GetEbxEntry(b.Static);
            object stRoot = App.AssetManager.GetEbx(st).RootObject;
            ResAssetEntry flsRes = App.AssetManager.GetResEntry(Rid(stRoot, "DatabaseResource"));
            if (flsRes != null)
            {
                b.FlsRes = flsRes;
                b.Fls = StaticEnlightenDatabase.Parse(ReadRes(flsRes), (flsRes.ModifiedEntry?.ResMeta ?? flsRes.ResMeta));
                if (b.Fls.Unmodelled != null) b.Fls = null;
            }
            JObject sd = EbxDumper.Dump(App.AssetManager.GetEbx(st), resolve: true, deep: false);
            foreach (string role in Roles)
                if (sd[role] is JObject p && p["__path"] != null) b.Textures[role] = (string)p["__path"];
            if (b.Textures.Count != 5) { Console.Error.WriteLine("enlighten relight: " + b.Static + " lacks some of the 5 lightmap textures"); return false; }
            // VE: sun and terrain albedo
            b.Rho = new[] { 0.3, 0.3, 0.3 }; b.ESun = new[] { 0.0, 0.0, 0.0 }; b.Sun = new[] { 0f, 1f, 0f };
            if (b.Ve != null && App.AssetManager.GetEbxEntry(b.Ve) != null)
            {
                JObject ve = EbxDumper.Dump(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(b.Ve)), resolve: true, deep: true);
                foreach (JObject o in Objects(ve))
                {
                    string type = (string)o["__type"];
                    if (type == "EnlightenComponentData" && o["TerrainColor"] is JObject tc)
                    {
                        b.Rho = new[] { (double)tc["x"], (double)tc["y"], (double)tc["z"] };
                        b.ESunScale = o["SunScale"] != null ? (double)o["SunScale"] : 1.0;
                        b.RhoOverridden = o["PropertyOverrides"] is JArray ov && ov.Any(v => (string)v == "TerrainColor");
                        if (b.RhoOverridden) b.RhoSource = b.Ve;
                    }
                    if (type == "OutdoorLightComponentData" && o["SunColor"] is JObject sc)
                    {
                        double th = (double)o["SunRotationX"] * Math.PI / 180, ph = (double)o["SunRotationY"] * Math.PI / 180;
                        b.Sun = new[] { (float)(Math.Sin(th) * Math.Cos(ph)), (float)Math.Sin(ph), (float)(Math.Cos(th) * Math.Cos(ph)) };
                        double si = (double)o["SunIntensity"];
                        b.SunColor = new[] { (double)sc["x"], (double)sc["y"], (double)sc["z"] };
                        b.SunIntensity = si;
                        if (o["SunShadowHeightScale"] != null) b.HeightScale = (double)o["SunShadowHeightScale"];
                    }
                }
                for (int c = 0; c < 3; c++) b.ESun[c] = b.SunIntensity * b.ESunScale * (b.SunColor != null ? b.SunColor[c] : 1);
            }
            else Console.Out.WriteLine("warning: no visual environment found for " + b.Static + "; terrain bounce uses defaults");
            b.VeSun = b.Sun;
            // Flux bakes (and the renderer shadows) the sun along normal(sun x (1, 1/SunShadowHeightScale, 1))
            // (FluxLightingUtils.cpp getScaledShadowDir); the setup layers' ShadowExtrusionLightDirection holds the same
            // direction (Tatooine: height scale 0.45, VE 15.5 deg -> 31.6 deg), so it wins when the level has one
            if (b.ExtrusionSun != null) b.Sun = b.ExtrusionSun;
            else b.Sun = ScaledSun(b.VeSun, b.HeightScale);
            return true;
        }

        /// <summary>A VE's Enlighten TerrainColor and whether its PropertyOverrides list it; null without one.</summary>
        static double[] VeTerrainColor(string ve, out bool overridden)
        {
            overridden = false;
            if (ve == null || App.AssetManager.GetEbxEntry(ve) == null) return null;
            try
            {
                JObject d = EbxDumper.Dump(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(ve)), resolve: true, deep: true);
                foreach (JObject o in Objects(d))
                    if ((string)o["__type"] == "EnlightenComponentData" && o["TerrainColor"] is JObject tc)
                    {
                        overridden = o["PropertyOverrides"] is JArray ov && ov.Any(v => (string)v == "TerrainColor");
                        return new[] { (double)tc["x"], (double)tc["y"], (double)tc["z"] };
                    }
            }
            catch { }
            return null;
        }

        /// <summary>The baked sun for a VE sun and SunShadowHeightScale: normal(s.x, s.y / scale, s.z).</summary>
        public static float[] ScaledSun(float[] s, double heightScale)
        {
            double hs = Math.Max(1e-4, heightScale);
            double x = s[0], y = s[1] / hs, z = s[2], n = Math.Sqrt(x * x + y * y + z * z);
            return new[] { (float)(x / n), (float)(y / n), (float)(z / n) };
        }

        static ulong Rid(object obj, string member)
        {
            object v = obj.GetType().GetProperty(member)?.GetValue(obj);
            if (v == null) return 0;
            try { return (ulong)(dynamic)v; } catch { return 0; }
        }

        internal static byte[] ReadRes(ResAssetEntry e)
        {
            using (Stream s = App.AssetManager.GetRes(e))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }

        // ---------------------------------------------------------------- terrain

        static DenseHeightfield TerrainRaster(string spec, int size, ExportLogger logger)
        {
            byte[] payload = null;
            if (File.Exists(spec)) payload = File.ReadAllBytes(spec);
            else
            {
                ResAssetEntry e = App.AssetManager.GetResEntry(spec.ToLowerInvariant().EndsWith(".streamingtree_win32") ? spec.ToLowerInvariant()
                    : spec.ToLowerInvariant() + ".streamingtree_win32") ?? App.AssetManager.GetResEntry(spec.ToLowerInvariant());
                if (e == null) { Console.Error.WriteLine("enlighten relight: no terrain resource for " + spec); return null; }
                payload = ReadRes(e);
            }
            TerrainRecord record;
            byte[] isolated = TerrainPayload.Isolate(payload, TerrainPayload.TagHeightfield, out record);
            if (isolated == null) { Console.Error.WriteLine("enlighten relight: " + spec + " has no heightfield"); return null; }
            var parsed = Bfii.TerrainFormat.TerrainStreamingTreeParser.Parse(isolated);
            if (parsed.Resource == null || parsed.Resource.Heightfield == null || parsed.Resource.Heightfield.Root == null)
            { Console.Error.WriteLine("enlighten relight: heightfield did not parse: " + parsed.Failure); return null; }
            var tree = parsed.Resource.Heightfield;
            var box = tree.Root.BoundingBox;
            // TerrainHeightSource caches decoded nodes in a Dictionary: one sampler per worker thread
            using (var src = new System.Threading.ThreadLocal<TerrainHeightSource>(() => new TerrainHeightSource(tree)))
            {
                double x0 = box.MinX.Value, z0 = box.MinZ.Value, ext = box.MaxX.Value - box.MinX.Value;
                return DenseHeightfield.From((x, z) => src.Value.SampleMetres(x, z), x0, z0, ext, size);
            }
        }

        // ---------------------------------------------------------------- atlases

        internal static byte[] ExportTexture(string ebx, string format, string outDir)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebx);
            var handler = Session.Plugins.GetHandler(entry);
            string path = Path.Combine(outDir, "tmp_" + Path.GetFileName(ebx) + "." + format);
            if (handler == null || !handler.Export(entry, path, format)) throw new IOException("could not export " + ebx + " as " + format);
            byte[] b = File.ReadAllBytes(path);
            File.Delete(path);
            return b;
        }

        static TileTexels[] ReadTiles(Bake b, EnlightenDatabase layout, string outDir, Dictionary<string, byte[]> baseDds)
        {
            try
            {
                int w, h;
                float[] irr = AtlasIo.ReadHdr(ExportTexture(b.Textures["StaticIrradianceTexture"], "hdr", outDir), out w, out h);
                var dirs = new byte[3][];
                string[] dr = { "StaticDirectionTexture", "StaticDirectionTextureG", "StaticDirectionTextureB" };
                for (int c = 0; c < 3; c++) dirs[c] = AtlasIo.ReadRgba(ExportTexture(b.Textures[dr[c]], "png", outDir), out w, out h);
                byte[] sky = AtlasIo.ReadRgba(ExportTexture(b.Textures["StaticSkyVisibilityTexture"], "png", outDir), out w, out h);
                foreach (string role in Roles) baseDds[b.Textures[role]] = ExportTexture(b.Textures[role], "dds", outDir);
                b.BaseIrr = irr; b.BaseDir = dirs; b.BaseSky = sky; b.BaseW = w; b.BaseH = h;
                var outT = new TileTexels[layout.Terrain.Count];
                for (int ti = 0; ti < layout.Terrain.Count; ti++)
                {
                    int x0, x1, y0, y1;
                    layout.Terrain[ti].TexelRect(layout.Width, layout.Height, out x0, out x1, out y0, out y1);
                    int tw = x1 - x0, th = y1 - y0;
                    var tt = new TileTexels(tw * th);
                    for (int py = 0; py < th; py++)
                        for (int px = 0; px < tw; px++)
                        {
                            int t = py * tw + px, a = (y0 + py) * w + (x0 + px);
                            for (int c = 0; c < 3; c++)
                            {
                                tt.L0[t * 3 + c] = irr[a * 3 + c];
                                for (int ax = 0; ax < 3; ax++) tt.D[(t * 3 + c) * 3 + ax] = dirs[c][a * 4 + ax] / 255f * 2 - 1;
                            }
                            for (int k = 0; k < 4; k++) tt.Sky[t * 4 + k] = sky[a * 4 + k];
                        }
                    outT[ti] = tt;
                }
                return outT;
            }
            catch (Exception ex) { Console.Error.WriteLine("enlighten relight: reading " + b.Static + ": " + ex.Message); return null; }
        }

        static int WriteAtlases(Bake b, EnlightenDatabase layout, List<int[]> tiles, TileTexels[] pred, Dictionary<string, byte[]> baseDds,
                                string outDir, bool import, ExportLogger logger)
        {
            // composite: the decoded atlas with every new tile / chart written in. The rects that get re-encoded are the
            // tiles widened to whole 4x4 blocks; the texels a widening adds come from the composite, so a neighbour keeps
            // its value (shipped, or new when it is a tile too) and overlapping widened rects agree.
            int W = (int)layout.Width, H = (int)layout.Height;
            bool aligned = tiles.All(r => ((r[0] | r[1] | r[2] | r[3]) & 3) == 0);
            float[] cIrr = null; byte[][] cDir = null; byte[] cSky = null;
            if (!aligned)
            {
                cIrr = new float[(long)W * H * 3]; cDir = new[] { new byte[W * H * 4], new byte[W * H * 4], new byte[W * H * 4] }; cSky = new byte[W * H * 4];
                if (b.BaseIrr != null)
                {
                    int cw = Math.Min(W, b.BaseW), ch = Math.Min(H, b.BaseH);
                    for (int y = 0; y < ch; y++)
                    {
                        Array.Copy(b.BaseIrr, (long)y * b.BaseW * 3, cIrr, (long)y * W * 3, cw * 3);
                        for (int c = 0; c < 3; c++) Array.Copy(b.BaseDir[c], y * b.BaseW * 4, cDir[c], y * W * 4, cw * 4);
                        Array.Copy(b.BaseSky, y * b.BaseW * 4, cSky, y * W * 4, cw * 4);
                    }
                }
                for (int ti = 0; ti < tiles.Count; ti++)
                {
                    int x0 = tiles[ti][0], y0 = tiles[ti][2], tw = tiles[ti][1] - x0, th = tiles[ti][3] - y0;
                    TileTexels tt = pred[ti];
                    for (int py = 0; py < th; py++)
                        for (int px = 0; px < tw; px++)
                        {
                            int t = py * tw + px, a = (y0 + py) * W + x0 + px;
                            for (int c = 0; c < 3; c++)
                            {
                                cIrr[(long)a * 3 + c] = tt.L0[t * 3 + c];
                                for (int ax = 0; ax < 3; ax++)
                                    cDir[c][a * 4 + ax] = (byte)Math.Max(0, Math.Min(255, Math.Round((tt.D[(t * 3 + c) * 3 + ax] * 0.5 + 0.5) * 255)));
                                cDir[c][a * 4 + 3] = 255;
                            }
                            for (int k = 0; k < 4; k++) cSky[a * 4 + k] = tt.Sky[t * 4 + k];
                        }
                }
                tiles = tiles.Select(r => new[] { r[0] & ~3, Math.Min(W, (r[1] + 3) & ~3), r[2] & ~3, Math.Min(H, (r[3] + 3) & ~3) }).ToList();
            }
            // mosaic: the rects side by side in a grid of slots, each slot the largest rect's size (all 4-aligned)
            int cols = (int)Math.Ceiling(Math.Sqrt(tiles.Count));
            int slotW = tiles.Max(r => r[1] - r[0]), slotH = tiles.Max(r => r[3] - r[2]);
            int rows = (tiles.Count + cols - 1) / cols;
            int mw = cols * slotW, mh = rows * slotH;
            var mIrr = new float[mw * mh * 3];
            var mDir = new[] { new byte[mw * mh * 4], new byte[mw * mh * 4], new byte[mw * mh * 4] };
            var mSky = new byte[mw * mh * 4];
            for (int ti = 0; ti < tiles.Count; ti++)
            {
                int sx = ti % cols * slotW, sy = ti / cols * slotH;
                int tw = tiles[ti][1] - tiles[ti][0], th = tiles[ti][3] - tiles[ti][2];
                TileTexels tt = pred[ti];
                for (int py = 0; py < th; py++)
                    for (int px = 0; px < tw; px++)
                    {
                        int m = (sy + py) * mw + sx + px;
                        if (aligned)
                        {
                            int t = py * tw + px;
                            for (int c = 0; c < 3; c++)
                            {
                                mIrr[m * 3 + c] = tt.L0[t * 3 + c];
                                for (int ax = 0; ax < 3; ax++)
                                    mDir[c][m * 4 + ax] = (byte)Math.Max(0, Math.Min(255, Math.Round((tt.D[(t * 3 + c) * 3 + ax] * 0.5 + 0.5) * 255)));
                                mDir[c][m * 4 + 3] = 255;
                            }
                            for (int k = 0; k < 4; k++) mSky[m * 4 + k] = tt.Sky[t * 4 + k];
                        }
                        else
                        {
                            int a = (tiles[ti][2] + py) * W + tiles[ti][0] + px;
                            for (int c = 0; c < 3; c++)
                            {
                                mIrr[m * 3 + c] = cIrr[(long)a * 3 + c];
                                for (int k = 0; k < 4; k++) mDir[c][m * 4 + k] = cDir[c][a * 4 + k];
                            }
                            for (int k = 0; k < 4; k++) mSky[m * 4 + k] = cSky[a * 4 + k];
                        }
                    }
            }
            int failures = 0;
            var jobs = new List<Tuple<string, byte[], ImageFormat, int>>
            {
                Tuple.Create("StaticIrradianceTexture", AtlasIo.WriteHdr(mIrr, mw, mh), ImageFormat.HDR, 95),
                Tuple.Create("StaticDirectionTexture", AtlasIo.WriteRgbaPng(mDir[0], mw, mh), ImageFormat.PNG, 98),
                Tuple.Create("StaticDirectionTextureG", AtlasIo.WriteRgbaPng(mDir[1], mw, mh), ImageFormat.PNG, 98),
                Tuple.Create("StaticDirectionTextureB", AtlasIo.WriteRgbaPng(mDir[2], mw, mh), ImageFormat.PNG, 98),
                Tuple.Create("StaticSkyVisibilityTexture", AtlasIo.WriteRgbaPng(mSky, mw, mh), ImageFormat.PNG, 98),
            };
            // the uncompressed tiles, for inspection: <bake>.tiles.<role>.hdr/.png plus the slot layout
            string stem = Path.Combine(outDir, Path.GetFileName(b.Static) + ".tiles.");
            foreach (var job in jobs) File.WriteAllBytes(stem + job.Item1 + (job.Item3 == ImageFormat.HDR ? ".hdr" : ".png"), job.Item2);
            File.WriteAllText(stem + "layout.json", new JObject
            {
                ["cols"] = cols, ["slotW"] = slotW, ["slotH"] = slotH,
                ["tiles"] = new JArray(tiles.Select(r => new JArray(r[0], r[1], r[2], r[3])))
            }.ToString());
            foreach (var job in jobs)
            {
                string ebx = b.Textures[job.Item1];
                try
                {
                    byte[] dst = (byte[])baseDds[ebx].Clone();
                    int dw, dh; uint dxgi;
                    AtlasIo.DdsDataOffset(dst, out dw, out dh, out dxgi);
                    int fmt = dxgi == 96 || dxgi == 95 || dxgi == 99 ? (int)dxgi : job.Item4;
                    byte[] mosaic = Compress(job.Item2, job.Item3, fmt);
                    for (int ti = 0; ti < tiles.Count; ti++)
                        AtlasIo.CopyBlocks(mosaic, dst, ti % cols * slotW, ti / cols * slotH, tiles[ti][0], tiles[ti][2],
                            tiles[ti][1] - tiles[ti][0], tiles[ti][3] - tiles[ti][2]);
                    string file = Path.Combine(outDir, Path.GetFileName(ebx) + ".dds");
                    File.WriteAllBytes(file, dst);
                    if (import)
                    {
                        string err = HeadlessTextureImporter.Import(App.AssetManager.GetEbxEntry(ebx), file, "DDS", false);
                        if (err != null) { Console.Error.WriteLine("enlighten relight: import " + ebx + ": " + err); failures++; }
                    }
                    Console.Out.WriteLine("  " + (import ? "imported " : "wrote ") + ebx);
                }
                catch (Exception ex) { Console.Error.WriteLine("enlighten relight: " + ebx + ": " + ex.Message); failures++; }
            }
            return failures;
        }

        internal static byte[] Compress(byte[] image, ImageFormat kind, int dxgi)
        {
            var options = new TextureImportOptions { type = FrostySdk.Resources.TextureType.TT_2d, format = dxgi, generateMipmaps = false };
            var blob = new BlobData();
            DxTexInterop.ConvertImageToDDS(image, image.Length, kind, options, ref blob);
            try { return blob.Data; }
            finally { DxTexInterop.ReleaseBlob(blob); }
        }
    }
}
