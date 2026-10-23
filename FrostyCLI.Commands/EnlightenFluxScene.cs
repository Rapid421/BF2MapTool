using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Frosty.Core;
using MeshSetPlugin.Resources;
using FrostySdk;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten flux-scene <level> --out scene.fxs`: the scene a Frostbite freeze bake hands DICE's Flux baker, rebuilt
    /// from a level's own data, for `tools/fluxbake`:
    /// - instances = the placements whose lightmap key has a mesh-instance record in the chosen bake (the Enlighten
    ///   database's instances), each with that record's uv transform (Flux SceneLoader.cpp applies it as given) and the
    ///   mesh's ReceiveOnly flag; geometry = LOD 0 sections with RadiosityTexCoord in the opaque / transparent
    ///   categories, vertex normals from the stream (or area-weighted face normals);
    /// - materials = the level's EnlightenShaderDatabase records (albedo, emissive intensity, opacity, backface), keyed
    ///   per section by {Fletcher32(mesh guid), hashQuickLowerCase(section name)} (EnlightenUtil.h); Flux emits
    ///   EmissiveIntensity x albedo;
    /// - lights = the level's Pbr*LightEntityData converted as FluxLightingUtils.cpp does (IES profiles as textures), the
    ///   VE sun as a directional light along getScaledShadowDir with SunScale as its bounce, the sky as the environment
    ///   cube: an optional captured sky (--sky-cube, 6 x 16 x 16 RGBA float in Flux's layout) combined with the VE
    ///   EnlightenComponent SkyBox by its blend mode (DistantLightingModule.cpp), or the SkyBox alone (VE bake mode);
    /// - probes = the bake's probe-set positions (set guid = END probe-set guid).
    /// Nothing is read from the shipped lightmaps or probes. --report writes what was matched and left out.
    /// </summary>
    public static class EnlightenFluxScene
    {
        public static int Execute(string[] args, ExportLogger logger)
        {
            string level = null, outFile = null, bakeFilter = null, skyCube = null, report = null, lightSrc = null;
            string planFile = null, materialsFile = null, probesFile = null;
            var emitters = new List<string>();
            float[] spareUv = null;
            int depth = 4, samples = 256, pathDepth = 5;
            bool noLights = false, noSun = false, noSky = false, flatNormals = false;
            double skyScale = 1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (a == "--bake" && i + 1 < args.Length) bakeFilter = args[++i];
                else if (a == "--sky-cube" && i + 1 < args.Length) skyCube = args[++i];
                else if (a == "--sky-scale" && i + 1 < args.Length) skyScale = double.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--report" && i + 1 < args.Length) report = args[++i];
                else if (a == "--lights" && i + 1 < args.Length) lightSrc = args[++i];
                else if (a == "--depth" && i + 1 < args.Length) depth = int.Parse(args[++i]);
                else if (a == "--samples" && i + 1 < args.Length) samples = int.Parse(args[++i]);
                else if (a == "--path-depth" && i + 1 < args.Length) pathDepth = int.Parse(args[++i]);
                else if (a == "--no-lights") noLights = true;
                else if (a == "--no-sun") noSun = true;
                else if (a == "--no-sky") noSky = true;
                else if (a == "--flat-normals") flatNormals = true;
                else if (a == "--flip-winding") FlipWinding = true;
                else if (a == "--plan" && i + 1 < args.Length) planFile = args[++i];
                else if (a == "--materials" && i + 1 < args.Length) materialsFile = args[++i];
                else if (a == "--probes" && i + 1 < args.Length) probesFile = args[++i];
                else if (a == "--emissive-scale" && i + 1 < args.Length) EmissiveScale = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (a == "--skip" && i + 1 < args.Length) Skip.AddRange(args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                else if (a == "--negate-normals") NegateNormals = true;
                else if (a == "--emitters" && i + 1 < args.Length) emitters.AddRange(args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                else if (!a.StartsWith("--")) level = a;
                else { Console.Error.WriteLine("enlighten flux-scene: unknown option " + a); return 1; }
            }
            if (level == null || outFile == null)
            {
                Console.Error.WriteLine("Usage: frostycli enlighten flux-scene <level> --out scene.fxs [--bake <name filter>] [--sky-cube cube.f32] [--sky-scale k] " +
                                        "[--lights <export-lights json|ebx>] [--no-lights] [--no-sun] [--no-sky] [--depth n] [--samples n] [--path-depth n] [--report r.json]");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("enlighten flux-scene: AssetManager not initialized."); return 1; }
            var rep = new JObject { ["level"] = level };

            // ---- the bake: its database supplies the instance records, the atlas size and the probe sets
            List<EnlightenRelight.Bake> bakes = EnlightenRelight.FindBakes(level, logger);
            EnlightenRelight.Bake bake = bakes.FirstOrDefault(b => bakeFilter == null || (b.Static ?? "").IndexOf(bakeFilter, StringComparison.OrdinalIgnoreCase) >= 0
                                                                   || (b.SubWorld ?? "").IndexOf(bakeFilter, StringComparison.OrdinalIgnoreCase) >= 0);
            if (bake == null) { Console.Error.WriteLine("enlighten flux-scene: no bake under " + level + (bakeFilter != null ? " matching " + bakeFilter : "")); return 1; }
            if (!EnlightenRelight.LoadBake(bake)) { Console.Error.WriteLine("enlighten flux-scene: could not load " + bake.Static); return 1; }
            EnlightenDatabase db = bake.Db;
            Console.Out.WriteLine(string.Format("bake {0} ({1}), atlas {2}x{3}, {4} records, {5} probe sets, VE {6}", bake.Static, bake.SubWorld,
                db.Width, db.Height, db.MeshInstances.Count, db.ProbeSets.Count, bake.Ve));
            rep["bake"] = bake.Static; rep["subWorld"] = bake.SubWorld; rep["ve"] = bake.Ve;
            var scene = new FluxScene { AtlasWidth = (int)db.Width, AtlasHeight = (int)db.Height, Samples = samples, Depth = pathDepth, PreExposure = db.PreExposure };
            var records = new Dictionary<uint, EnlightenDatabase.MeshInstance>();
            if (planFile != null)
            {
                // a new atlas (enlighten chart-plan): its records and size replace the bake's
                JObject plan = JObject.Parse(File.ReadAllText(planFile));
                scene.AtlasWidth = (int)plan["width"]; scene.AtlasHeight = (int)plan["height"];
                if (plan["spare"] is JObject sp) spareUv = ((JArray)sp["uv"]).Select(v => (float)(double)v).ToArray();
                foreach (JObject r in (JArray)plan["records"])
                {
                    var mi = new EnlightenDatabase.MeshInstance { Hash = Convert.ToUInt32((string)r["key"], 16) };
                    var uvb = new byte[32];
                    JArray u = (JArray)r["uv"];
                    for (int k = 0; k < 6; k++) Array.Copy(BitConverter.GetBytes((float)(double)u[k]), 0, uvb, k * 4, 4);
                    mi.UvTransforms.Add(uvb);
                    records[mi.Hash] = mi;
                }
                Console.Out.WriteLine(string.Format("plan {0}: atlas {1}x{2}, {3} records (replacing the bake's)", planFile, scene.AtlasWidth, scene.AtlasHeight, records.Count));
                rep["plan"] = planFile;
            }
            else foreach (var mi in db.MeshInstances) if (mi.UvTransforms.Count > 0) records[mi.Hash] = mi;
            if (materialsFile != null)
            {
                // GI materials for meshes outside the level's shader database: {"<mesh path>": {"albedo": [r,g,b], "emissive": e}}
                foreach (var kv in JObject.Parse(File.ReadAllText(materialsFile)))
                {
                    var o = (JObject)kv.Value;
                    var alb = o["albedo"] is JArray aa ? new[] { (float)aa[0], (float)aa[1], (float)aa[2] } : new[] { 0.3f, 0.3f, 0.3f };
                    MaterialOverrides[kv.Key] = new FluxScene.Material { Id = MeshGuid("material:" + kv.Key), Albedo = alb, Emissive = Fill3((float)(o["emissive"] ?? 0f) * EmissiveScale) };
                }
                Console.Out.WriteLine(string.Format("materials {0}: {1} mesh override(s)", materialsFile, MaterialOverrides.Count));
            }

            // ---- the level's Enlighten shader database (material albedo / emissive)
            var shaderDb = LoadShaderDatabase(level, rep);

            // ---- placements with a record
            var placements = new List<EnlightenKeys.Placement>();
            EnlightenKeys.Walk(level, depth, (name, dump) =>
            {
                placements.AddRange(EnlightenKeys.Collect(name, dump).Where(p => records.ContainsKey(p.Key) && !Skip.Any(k => p.MeshPath.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)));
            });
            var byKey = placements.GroupBy(p => p.Key).Select(g => g.First()).ToList();
            Console.Out.WriteLine(string.Format("{0} placement(s) carry {1} of the {2} records", placements.Count, byKey.Count, records.Count));
            rep["recordsMatched"] = byKey.Count; rep["records"] = records.Count;

            // ---- meshes
            var meshIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var meshFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);    // ReceiveOnly
            var matIds = new Dictionary<Guid, FluxScene.Material>();
            var stats = new SortedDictionary<string, int>();
            Action<string> count = k => { int v; stats.TryGetValue(k, out v); stats[k] = v + 1; };
            var defaultMat = new FluxScene.Material { Id = new Guid("00000000-0000-0000-0000-00000000f1f1") };
            var densities = new List<double>();
            foreach (var p in byKey)
            {
                Guid mid;
                if (!meshIds.TryGetValue(p.MeshPath, out mid))
                {
                    bool receiveOnly;
                    FluxScene.Mesh mesh = BuildMesh(p.MeshPath, p.MeshGuid, shaderDb, matIds, defaultMat, flatNormals, count, out receiveOnly);
                    mid = mesh != null ? mesh.Id : Guid.Empty;
                    meshIds[p.MeshPath] = mid;
                    meshFlags[p.MeshPath] = receiveOnly;
                    if (mesh != null) scene.Meshes.Add(mesh);
                }
                if (mid == Guid.Empty) { count("instances without geometry"); continue; }
                var rec = records[p.Key];
                var x = new FluxScene.Instance { Id = KeyGuid(p.Key), MeshId = mid, ReceiveOnly = meshFlags[p.MeshPath] };
                x.Transform = new[] { (float)p.Right[0], (float)p.Right[1], (float)p.Right[2], (float)p.Up[0], (float)p.Up[1], (float)p.Up[2],
                                      (float)p.Forward[0], (float)p.Forward[1], (float)p.Forward[2], (float)p.Trans[0], (float)p.Trans[1], (float)p.Trans[2] };
                byte[] uv = rec.UvTransforms[0];
                for (int k = 0; k < 6; k++) x.UvTransform[k] = BitConverter.ToSingle(uv, k * 4);
                scene.Instances.Add(x);
                if (x.ReceiveOnly) count("instances ReceiveOnly");
                FluxScene.Mesh dm = scene.Meshes.FirstOrDefault(m => m.Id == mid);
                if (dm != null) { double dens = dm.TexelDensity(x.Transform, x.UvTransform, scene.AtlasWidth, scene.AtlasHeight); if (dens > 0) densities.Add(dens); }
            }
            // ---- --emitters <mesh substring,...>: placements WITHOUT a record (probe-lit in game: no lightmap-uv stream)
            // that still belong in the light transport -- emissive strips, occluders. They enter the scene with their
            // --materials entry (or the default material); every vertex's lightmap uv is the centre of the plan's spare
            // rect, so they cover no texel of any record's chart. Mygeeto MBL: the 22 2005 glow-strip meshes.
            if (emitters.Count > 0)
            {
                if (spareUv == null) { Console.Error.WriteLine("enlighten flux-scene: --emitters needs a --plan with a spare rect (enlighten chart-plan)"); return 1; }
                var extra = new List<EnlightenKeys.Placement>();
                EnlightenKeys.Walk(level, depth, (name, dump) =>
                {
                    extra.AddRange(EnlightenKeys.Collect(name, dump).Where(p => !records.ContainsKey(p.Key) && emitters.Any(k => p.MeshPath.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)));
                });
                int added = 0;
                AllowNoRadiosity = true;
                foreach (var p in extra.GroupBy(q => q.Key).Select(g => g.First()))
                {
                    Guid mid;
                    if (!meshIds.TryGetValue(p.MeshPath, out mid))
                    {
                        bool ro;
                        FluxScene.Mesh mesh = BuildMesh(p.MeshPath, p.MeshGuid, shaderDb, matIds, defaultMat, flatNormals, count, out ro);
                        mid = mesh != null ? mesh.Id : Guid.Empty;
                        meshIds[p.MeshPath] = mid;
                        if (mesh != null) scene.Meshes.Add(mesh);
                    }
                    if (mid == Guid.Empty) { count("emitter instances without geometry"); continue; }
                    var x = new FluxScene.Instance { Id = KeyGuid(p.Key), MeshId = mid, ReceiveOnly = false };
                    x.Transform = new[] { (float)p.Right[0], (float)p.Right[1], (float)p.Right[2], (float)p.Up[0], (float)p.Up[1], (float)p.Up[2],
                                          (float)p.Forward[0], (float)p.Forward[1], (float)p.Forward[2], (float)p.Trans[0], (float)p.Trans[1], (float)p.Trans[2] };
                    Array.Copy(spareUv, x.UvTransform, 6);
                    scene.Instances.Add(x);
                    added++;
                }
                AllowNoRadiosity = false;
                Console.Out.WriteLine(string.Format("emitters: {0} placement(s) without a record added as emitter / occluder geometry", added));
                rep["emitterInstances"] = added;
            }
            scene.Materials.AddRange(matIds.Values);
            if (densities.Count > 0)
            {
                densities.Sort();
                Func<double, double> pq = q => densities[(int)Math.Min(densities.Count - 1, Math.Floor(q * densities.Count))];
                Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "texel density (texels/m, {0} placements): p10 {1:F2}, median {2:F2}, p90 {3:F2}", densities.Count, pq(0.1), pq(0.5), pq(0.9)));
                rep["texelDensity"] = new JObject { ["p10"] = pq(0.1), ["p25"] = pq(0.25), ["median"] = pq(0.5), ["p75"] = pq(0.75), ["p90"] = pq(0.9), ["count"] = densities.Count };
            }
            if (stats.ContainsKey("sections without a shader-database material")) scene.Materials.Add(defaultMat);

            // ---- sky and sun from the VE
            JObject ve = bake.Ve != null && App.AssetManager.GetEbxEntry(bake.Ve) != null
                ? EbxDumper.Dump(App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(bake.Ve)), resolve: true, deep: true) : null;
            JObject outdoor = ve != null ? Component(ve, "OutdoorLightComponentData") : null, enl = ve != null ? Component(ve, "EnlightenComponentData") : null;
            double rotX = Num(outdoor, "SunRotationX", 0), rotY = Num(outdoor, "SunRotationY", 45);
            double[] sunDir = FluxScene.SunDirection(rotX, rotY);
            var box = new FluxScene.SkyBox
            {
                Enable = Bool(enl, "SkyBoxEnable", false), SkyColor = Vec(enl, "SkyBoxSkyColor"), GroundColor = Vec(enl, "SkyBoxGroundColor"),
                SunLightColor = Vec(enl, "SkyBoxSunLightColor"), BackLightColor = Vec(enl, "SkyBoxBackLightColor"),
                SunLightSize = Num(enl, "SkyBoxSunLightColorSize", 0), BackLightSize = Num(enl, "SkyBoxBackLightColorSize", 0),
                BackLightRotationX = Num(enl, "SkyBoxBackLightRotationX", 0), BackLightRotationY = Num(enl, "SkyBoxBackLightRotationY", 0),
                BlendMode = enl != null && enl["SkyBoxBlendMode"] != null ? (string)enl["SkyBoxBlendMode"] : "SkyBoxBlendMode_Add", Blend = Num(enl, "SkyBoxBlend", 1)
            };
            float[] captured = null;
            if (skyCube != null)
            {
                byte[] raw = File.ReadAllBytes(skyCube);
                captured = new float[raw.Length / 4];
                Buffer.BlockCopy(raw, 0, captured, 0, raw.Length);
                if (captured.Length != 6 * FluxScene.CubeRes * FluxScene.CubeRes * 4) { Console.Error.WriteLine("enlighten flux-scene: --sky-cube must be 6 x 16 x 16 RGBA float"); return 1; }
            }
            // texture 0 is the environment cube: captured (in Flux's layout) combined with the SkyBox per texel
            FluxScene.Texture boxCube = FluxScene.BuildCube((dx, dy, dz) => box.Enable ? box.Eval(dx, dy, dz, sunDir) : new double[3]);
            var env = new FluxScene.Texture { W = 16, H = 16, Format = 2, Layout = 2, Rgba = new float[boxCube.Rgba.Length] };
            for (int t = 0; t < env.Rgba.Length / 4; t++)
            {
                double[] cap = captured != null ? new double[] { captured[t * 4], captured[t * 4 + 1], captured[t * 4 + 2] } : null;
                double[] bx = { boxCube.Rgba[t * 4], boxCube.Rgba[t * 4 + 1], boxCube.Rgba[t * 4 + 2] };
                double[] c = box.Enable ? box.Combine(cap, bx) : (cap ?? new double[3]);
                for (int k = 0; k < 3; k++) env.Rgba[t * 4 + k] = noSky ? 0f : (float)(c[k] * skyScale);
                env.Rgba[t * 4 + 3] = 1f;
            }
            scene.Textures.Add(env);
            // the sky light as fluxExportLightSources makes it: colour 0, the cube as its texture (Flux samples the texture
            // whenever one is set; texture reference = scene-file index 0, remapped by fluxbake to Flux's id)
            scene.Lights.Add(new FluxScene.Light { Id = new Guid("00000000-0000-0000-0000-0000000000e5"), Type = 5, Color = new[] { 0f, 0f, 0f }, TextureId = 0 });
            rep["sky"] = new JObject
            {
                ["skyBoxEnable"] = box.Enable, ["blend"] = box.BlendMode, ["skyColor"] = new JArray(box.SkyColor), ["groundColor"] = new JArray(box.GroundColor),
                ["captured"] = skyCube, ["scale"] = skyScale, ["disabled"] = noSky
            };
            bool sunOn = Bool(outdoor, "Enable", true);
            double sunScale = Num(enl, "SunScale", 1);
            bool sunDirect = sunOn && Bool(enl, "SunDirectLightmapEnable", false), sunIndirect = sunOn && sunScale > 0;
            if (!noSun && (sunDirect || sunIndirect))
            {
                double[] sd = FluxScene.ScaledShadowDirection(rotX, rotY, Num(outdoor, "SunShadowHeightScale", 1));
                double[] sc = Vec(outdoor, "SunColor", 1); double si = Num(outdoor, "SunIntensity", 0);
                scene.Lights.Add(new FluxScene.Light
                {
                    Id = new Guid("00000000-0000-0000-0000-00000000005a"), Type = 0, DirectEnable = sunDirect, IndirectEnable = sunIndirect,
                    Forward = new[] { (float)sd[0], (float)sd[1], (float)sd[2] }, Color = new[] { (float)(sc[0] * si), (float)(sc[1] * si), (float)(sc[2] * si) },
                    BounceColorScale = new[] { (float)sunScale, (float)sunScale, (float)sunScale }, DiskRadius = (float)(Math.PI / 180 * Num(outdoor, "SunAngularRadius", 0))
                });
                rep["sun"] = new JObject { ["dir"] = new JArray(sd), ["color"] = new JArray(sc[0] * si, sc[1] * si, sc[2] * si), ["bounce"] = sunScale, ["direct"] = sunDirect };
            }

            // ---- local lights
            if (!noLights)
            {
                string err; List<string> skipped;
                List<LightRecord> lrs;
                if (lightSrc != null && File.Exists(lightSrc))
                {
                    JToken j = JToken.Parse(File.ReadAllText(lightSrc));
                    lrs = ((j is JObject o ? o["lights"] : j) as JArray)?.ToObject<List<LightRecord>>() ?? new List<LightRecord>();
                }
                else lrs = LevelCommand.CollectLights(lightSrc ?? level, 6, out skipped, out err) ?? new List<LightRecord>();
                var iesTex = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                var why = new SortedDictionary<string, int>();
                int ok = 0, withIes = 0;
                foreach (LightRecord r in lrs)
                {
                    LightAssetRef iesRef = r.Refs?.FirstOrDefault(x => x.Field == "IESProfile" && !string.IsNullOrEmpty(x.Path));
                    IesProfile ies = null; uint tid = 0;
                    if (iesRef != null)
                    {
                        ies = LoadIes(iesRef.Path);
                        if (ies != null && !iesTex.TryGetValue(iesRef.Path, out tid))
                        {
                            var t = new FluxScene.Texture { W = ies.N, H = ies.N, Format = 2, Layout = 1, Rgba = new float[ies.N * ies.N * 4] };
                            for (int i = 0; i < ies.N * ies.N; i++) { t.Rgba[i * 4] = t.Rgba[i * 4 + 1] = t.Rgba[i * 4 + 2] = ies.Grid[i]; t.Rgba[i * 4 + 3] = 1; }
                            tid = (uint)scene.Textures.Count + 1;   // a local light names its texture by file index + 1 (0 = none)
                            scene.Textures.Add(t);
                            iesTex[iesRef.Path] = tid;
                        }
                    }
                    string w;
                    FluxScene.Light l = FluxScene.FromRecord(r.Type, r.Fields, r.Right, r.Up, r.Forward, r.Trans, ies, tid, out w);
                    if (l == null) { int c; why.TryGetValue(w, out c); why[w] = c + 1; continue; }
                    l.Id = KeyGuid((uint)(0x10000000 + ok));
                    scene.Lights.Add(l); ok++; if (ies != null) withIes++;
                }
                Console.Out.WriteLine(string.Format("lights: {0} record(s), {1} baked ({2} with an IES profile){3}", lrs.Count, ok, withIes,
                    why.Count > 0 ? "; left out: " + string.Join(", ", why.Select(kv => kv.Value + " " + kv.Key)) : ""));
                rep["lights"] = new JObject { ["records"] = lrs.Count, ["baked"] = ok, ["ies"] = withIes, ["leftOut"] = JObject.FromObject(why) };
            }

            // ---- probes
            if (probesFile != null)
            {
                // probe sets of the new bake: [{"guid": g, "positions": [x, y, z, ...]}, ...] (enlighten probe-plan)
                foreach (JObject ps in JArray.Parse(File.ReadAllText(probesFile)))
                    scene.ProbeSets.Add(new FluxScene.ProbeSet { Id = new Guid((string)ps["guid"]), Positions = ((JArray)ps["positions"]).Select(v => (float)v).ToArray() });
            }
            else foreach (var ps in db.ProbeSets)
            {
                if (ps.Count == 0) continue;
                var pos = new float[ps.Count * 3];
                for (int i = 0; i < ps.Count; i++) { float px, py, pz; ps.Position(i, out px, out py, out pz); pos[i * 3] = px; pos[i * 3 + 1] = py; pos[i * 3 + 2] = pz; }
                scene.ProbeSets.Add(new FluxScene.ProbeSet { Id = ps.Guid, Positions = pos });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
            scene.Write(outFile);
            long tris = scene.Meshes.Sum(m => (long)m.Faces.Length / 3);
            Console.Out.WriteLine(string.Format("enlighten flux-scene: {0} meshes ({1:N0} triangles), {2} instances, {3} materials, {4} lights, {5} probe sets ({6} probes) -> {7}",
                scene.Meshes.Count, tris, scene.Instances.Count, scene.Materials.Count, scene.Lights.Count, scene.ProbeSets.Count,
                scene.ProbeSets.Sum(p => p.Positions.Length / 3), outFile));
            foreach (var kv in stats) Console.Out.WriteLine("  " + kv.Key + ": " + kv.Value);
            Console.Out.WriteLine(string.Format("  winding vote: stored normal agrees with cross(b-a, c-a) on {0:N0} triangles, opposes on {1:N0}{2}", _voteSame, _voteOpp, FlipWinding ? " (written flipped)" : ""));
            Console.Out.WriteLine(string.Format("  degenerate geometry: {0:N0} zero-area triangle(s) left out, {1:N0} zero-length stream normal(s) rebuilt from face normals", droppedTriangles, repairedNormals));
            rep["degenerate"] = new JObject { ["droppedTriangles"] = droppedTriangles, ["repairedNormals"] = repairedNormals };
            rep["windingVote"] = new JObject { ["same"] = _voteSame, ["opposite"] = _voteOpp, ["flipped"] = FlipWinding };
            rep["meshes"] = scene.Meshes.Count; rep["triangles"] = tris; rep["instances"] = scene.Instances.Count; rep["materials"] = scene.Materials.Count;
            rep["stats"] = JObject.FromObject(stats);
            if (report != null) File.WriteAllText(report, rep.ToString(Formatting.Indented));
            return 0;
        }

        public static bool FlipWinding, NegateNormals;
        /// <summary>Triangles under this mesh-space area (m^2) are left out of the Flux scene (no area; NaN basis in Flux).</summary>
        public const double MinTriangleArea = 1e-8;
        static long droppedTriangles, repairedNormals;
        public static float EmissiveScale = 1f;
        static readonly Dictionary<string, FluxScene.Material> MaterialOverrides = new Dictionary<string, FluxScene.Material>(StringComparer.OrdinalIgnoreCase);
        static float[] Fill3(float v) { return new[] { v, v, v }; }
        public static readonly List<string> Skip = new List<string>();
        static long _voteSame, _voteOpp;

        /// <summary>Does the stored vertex normal agree with cross(b - a, c - a) of the triangle as the index buffer orders it?</summary>
        static void WindingVote(float[] p, GeoSection s, GeoElement nrm, int a, int b, int c)
        {
            double ex = p[b * 3] - p[a * 3], ey = p[b * 3 + 1] - p[a * 3 + 1], ez = p[b * 3 + 2] - p[a * 3 + 2];
            double fx = p[c * 3] - p[a * 3], fy = p[c * 3 + 1] - p[a * 3 + 1], fz = p[c * 3 + 2] - p[a * 3 + 2];
            double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
            float[] n = VertexCodec.Decode(s.Vertices, a * s.Stride + nrm.Offset, nrm.Format);
            double d = nx * n[0] + ny * n[1] + nz * n[2];
            if (d > 0) _voteSame++; else if (d < 0) _voteOpp++;
        }

        static Guid KeyGuid(uint key)
        {
            var b = new byte[16];
            BitConverter.GetBytes(key).CopyTo(b, 0);
            b[15] = 0x5c;
            return new Guid(b);
        }

        /// <summary>The level's EnlightenShaderDatabase: material records keyed by (w0, w1, w2) of their material guid.</summary>
        sealed class ShaderMaterial { public float[] Color = new float[3]; public float Emissive, Opacity; public uint Backface; public Guid Id; }

        static Dictionary<Tuple<uint, uint, uint>, ShaderMaterial> LoadShaderDatabase(string level, JObject rep)
        {
            var result = new Dictionary<Tuple<uint, uint, uint>, ShaderMaterial>();
            EbxAssetEntry le = App.AssetManager.GetEbxEntry(level);
            if (le == null) return result;
            JObject dump = EbxDumper.Dump(App.AssetManager.GetEbx(le), resolve: true, deep: false);
            string path = dump["EnlightenShaderDatabase"] is JObject p ? (string)p["__path"] : null;
            if (path == null) { Console.Out.WriteLine("warning: " + level + " has no EnlightenShaderDatabase; every surface gets the default material"); return result; }
            EbxAssetEntry se = App.AssetManager.GetEbxEntry(path);
            object root = se != null ? App.AssetManager.GetEbx(se).RootObject : null;
            ulong rid = 0;
            try { rid = (ulong)(dynamic)root.GetType().GetProperty("DatabaseResource").GetValue(root); } catch { }
            ResAssetEntry re = rid != 0 ? App.AssetManager.GetResEntry(rid) : null;
            if (re == null) { Console.Out.WriteLine("warning: shader database " + path + " has no resource"); return result; }
            byte[] d;
            using (Stream s = App.AssetManager.GetRes(re)) using (var ms = new MemoryStream()) { s.CopyTo(ms); d = ms.ToArray(); }
            for (int i = 0; i + 64 <= d.Length; i += 64)
            {
                uint w0 = BitConverter.ToUInt32(d, i), w1 = BitConverter.ToUInt32(d, i + 4), w2 = BitConverter.ToUInt32(d, i + 8);
                var m = new ShaderMaterial
                {
                    Color = new[] { BitConverter.ToSingle(d, i + 32), BitConverter.ToSingle(d, i + 36), BitConverter.ToSingle(d, i + 40) },
                    Emissive = BitConverter.ToSingle(d, i + 48), Opacity = BitConverter.ToSingle(d, i + 52), Backface = BitConverter.ToUInt32(d, i + 56)
                };
                var g = new byte[16]; Array.Copy(d, i, g, 0, 16); m.Id = new Guid(g);
                result[Tuple.Create(w0, w1, w2)] = m;
            }
            Console.Out.WriteLine(string.Format("shader database {0}: {1} materials ({2} emissive)", path, result.Count, result.Values.Count(m => m.Emissive > 0)));
            rep["shaderDatabase"] = new JObject { ["path"] = path, ["materials"] = result.Count };
            return result;
        }

        /// <summary>LOD 0 of a mesh as a Flux mesh: the lightmapped sections, their materials from the shader database.</summary>
        static bool AllowNoRadiosity;

        static FluxScene.Mesh BuildMesh(string meshPath, Guid meshGuid, Dictionary<Tuple<uint, uint, uint>, ShaderMaterial> shaderDb,
                                        Dictionary<Guid, FluxScene.Material> mats, FluxScene.Material defaultMat, bool flatNormals,
                                        Action<string> count, out bool receiveOnly)
        {
            receiveOnly = false;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(meshPath);
            if (e == null) { count("meshes not found"); return null; }
            try { receiveOnly = (bool)((dynamic)App.AssetManager.GetEbx(e).RootObject).ReceiveOnly; } catch { }
            GeoMesh geo;
            string err = GeoMesh.Read(e, out geo);
            if (err != null || geo == null || geo.Lods.Count == 0) { count("meshes unreadable"); return null; }
            GeoLod lod = geo.Lods[0];
            uint f32 = FluxScene.Fletcher32(meshGuid.ToByteArray());
            var verts = new List<float>();
            var faces = new List<int>();
            var faceMats = new List<Guid>();
            int decodedNormals = 0, computedNormals = 0;
            for (int si = 0; si < lod.Sections.Count; si++)
            {
                GeoSection s = lod.Sections[si];
                // the subsets the Enlighten export takes: opaque, transparent, transparent decal (EnlightenUtil.cpp)
                ushort cats = lod.CategoryBits(si);
                if (lod.Categories.Count > 0 && (cats & 0x7) == 0) continue;
                GeoElement pos = s.Find(VertexElementUsage.Pos), rad = s.Find(VertexElementUsage.RadiosityTexCoord);
                if (pos == null || (rad == null && !AllowNoRadiosity) || s.Prim != PrimitiveType.PrimitiveType_TriangleList || s.VertexCount == 0) { if (pos != null && rad == null) count("sections without radiosity uvs"); continue; }
                GeoElement nrm = s.Find(VertexElementUsage.Normal), uv0 = s.Find(VertexElementUsage.TexCoord0);
                int baseV = verts.Count / 10;
                var p = new float[s.VertexCount * 3];
                for (int v = 0; v < s.VertexCount; v++)
                {
                    int at = v * s.Stride;
                    float[] xyz = VertexCodec.Decode(s.Vertices, at + pos.Offset, pos.Format);
                    float[] r = rad != null ? VertexCodec.Decode(s.Vertices, at + rad.Offset, rad.Format) : new[] { 0.5f, 0.5f, 0f, 0f };
                    float[] t = uv0 != null ? VertexCodec.Decode(s.Vertices, at + uv0.Offset, uv0.Format) : new float[4];
                    float[] n = nrm != null && !flatNormals ? VertexCodec.Decode(s.Vertices, at + nrm.Offset, nrm.Format) : new float[4];
                    if (NegateNormals) { n[0] = -n[0]; n[1] = -n[1]; n[2] = -n[2]; }
                    p[v * 3] = xyz[0]; p[v * 3 + 1] = xyz[1]; p[v * 3 + 2] = xyz[2];
                    verts.AddRange(new[] { xyz[0], xyz[1], xyz[2], n[0], n[1], n[2], r[0], r[1], t[0], t[1] });
                }
                // material
                Guid matId = defaultMat.Id;
                ShaderMaterial sm;
                FluxScene.Material ov;
                if (MaterialOverrides.TryGetValue(meshPath, out ov))
                {
                    matId = ov.Id;
                    if (!mats.ContainsKey(matId)) mats[matId] = ov;
                    count("sections with a --materials override");
                }
                else if (shaderDb.TryGetValue(Tuple.Create(f32, FluxScene.HashQuickLowerCase(s.Name), 0u), out sm))
                {
                    matId = sm.Id;
                    if (!mats.ContainsKey(matId))
                        mats[matId] = new FluxScene.Material
                        {
                            Id = sm.Id, Albedo = sm.Color, Emissive = new[] { sm.Emissive * EmissiveScale, sm.Emissive * EmissiveScale, sm.Emissive * EmissiveScale }, Opacity = sm.Opacity, Backface = sm.Backface
                        };
                    count("sections with a shader-database material");
                }
                else count("sections without a shader-database material");
                int nIdx = s.Indices.Length - s.Indices.Length % 3;
                // area-weighted face normals per vertex: the normals of a section without a usable normal stream, and the
                // repair of stream normals that are zero-length or non-finite
                var acc = new double[s.VertexCount * 3];
                for (int i = 0; i < nIdx; i += 3)
                {
                    int a = (int)s.Indices[i], b = (int)s.Indices[i + 1], c = (int)s.Indices[i + 2];
                    if (a >= s.VertexCount || b >= s.VertexCount || c >= s.VertexCount) continue;
                    // face normal in the order Flux uses (indices 1, 0, 2: SceneLoader.cpp), area-weighted
                    double ex = p[a * 3] - p[b * 3], ey = p[a * 3 + 1] - p[b * 3 + 1], ez = p[a * 3 + 2] - p[b * 3 + 2];
                    double fx = p[c * 3] - p[b * 3], fy = p[c * 3 + 1] - p[b * 3 + 1], fz = p[c * 3 + 2] - p[b * 3 + 2];
                    double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
                    // Zero-area triangles are left out: Flux builds every triangle's shading basis from normalize(cross)
                    // (Scene.cpp), which is NaN for them, and a bounce ray that hits one carries the NaN back into the
                    // lightmap texels that traced it. They cover no area, so they bake nothing.
                    if (0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz) < MinTriangleArea) { droppedTriangles++; continue; }
                    if (nrm != null && !flatNormals) WindingVote(p, s, nrm, a, b, c);
                    foreach (int k in new[] { a, b, c }) { acc[k * 3] += nx; acc[k * 3 + 1] += ny; acc[k * 3 + 2] += nz; }
                    if (FlipWinding) { int tmp = b; b = c; c = tmp; }
                    faces.Add(baseV + a); faces.Add(baseV + b); faces.Add(baseV + c); faceMats.Add(matId);
                }
                bool computed = nrm == null || flatNormals;
                for (int v = 0; v < s.VertexCount; v++)
                {
                    int o = (baseV + v) * 10 + 3;
                    if (!computed)
                    {
                        double sx = verts[o], sy = verts[o + 1], sz = verts[o + 2], sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                        if (sl > 1e-6 && !double.IsNaN(sl) && !double.IsInfinity(sl)) continue;
                        repairedNormals++;
                    }
                    double l = Math.Sqrt(acc[v * 3] * acc[v * 3] + acc[v * 3 + 1] * acc[v * 3 + 1] + acc[v * 3 + 2] * acc[v * 3 + 2]);
                    if (l < 1e-20) { verts[o] = 0; verts[o + 1] = 1; verts[o + 2] = 0; continue; }   // only on dropped triangles
                    verts[o] = (float)(acc[v * 3] / l); verts[o + 1] = (float)(acc[v * 3 + 1] / l); verts[o + 2] = (float)(acc[v * 3 + 2] / l);
                }
                if (computed) computedNormals++;
                else decodedNormals++;
            }
            if (faces.Count == 0) { count("meshes without lightmapped sections"); return null; }
            if (computedNormals > 0) count("sections with computed normals");
            if (decodedNormals > 0) count("sections with stream normals");
            return new FluxScene.Mesh { Id = MeshGuid(meshPath), Vertices = verts.ToArray(), Faces = faces.ToArray(), FaceMaterials = faceMats.ToArray() };
        }

        static Guid MeshGuid(string path)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
                return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
        }

        static readonly Dictionary<string, IesProfile> IesCache = new Dictionary<string, IesProfile>(StringComparer.OrdinalIgnoreCase);
        static IesProfile LoadIes(string path)
        {
            IesProfile p;
            if (IesCache.TryGetValue(path, out p)) return p;
            try
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(path);
                object root = e != null ? App.AssetManager.GetEbx(e).RootObject : null;
                ulong rid = root != null ? (ulong)(dynamic)root.GetType().GetProperty("SourceResource").GetValue(root) : 0;
                ResAssetEntry re = rid != 0 ? App.AssetManager.GetResEntry(rid) : null;
                if (re != null)
                    using (Stream s = App.AssetManager.GetRes(re)) using (var ms = new MemoryStream()) { s.CopyTo(ms); p = IesProfile.Parse(ms.ToArray()); }
            }
            catch { p = null; }
            IesCache[path] = p;
            return p;
        }

        static JObject Component(JObject ve, string type)
        {
            foreach (JObject o in EnlightenKeys.Objects(ve)) if ((string)o["__type"] == type) return o;
            return null;
        }
        static double Num(JObject o, string k, double d) { JToken t = o?[k]; return t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? (double)t : d; }
        static bool Bool(JObject o, string k, bool d) { JToken t = o?[k]; return t != null && t.Type == JTokenType.Boolean ? (bool)t : d; }
        static double[] Vec(JObject o, string k, double d = 0)
        {
            if (o?[k] is JObject v && v["x"] != null) return new[] { (double)v["x"], (double)v["y"], (double)v["z"] };
            return new[] { d, d, d };
        }
    }
}
