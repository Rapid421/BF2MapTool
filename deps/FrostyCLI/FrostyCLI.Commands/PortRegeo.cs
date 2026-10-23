using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Commands
{
    // Shared material/mesh-field steps of the geometry port, and `port regeo`: the geometry port applied IN PLACE to a mesh an
    // earlier `port import` created. The blueprint, physics asset, mesh EBX and every GUID stay as they are, so the group
    // members and entities that place it, their collision and the dependency closure remain valid; only the mesh's sections,
    // vertex/index data, shader block depot, materials and variation-database record are rebuilt from the source.
    public static class PortRegeo
    {
        /// <summary>Luminance-unit parameter handling: names (case-insensitive) whose values scale by <see cref="LuminanceScale"/>.</summary>
        public static HashSet<string> LuminanceParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static float LuminanceScale = 1f;
        public static readonly List<string> LuminanceLog = new List<string>();

        // ------------------------------------------------------------------ materials

        /// <summary>
        /// Gives each material of <paramref name="mesh"/> the source material exactly: its shader (<paramref name="shaderTarget"/>),
        /// and ONLY the source's bool, vector, texture and conditional parameters and scalar fields -- whatever the template
        /// material carried is removed (a cloned TIE fighter material brought its tiling vector and three vehicle
        /// conditionals into a hangar wall). Extra template materials beyond the source's count are dropped. The same
        /// parameters are written to the shader block depot's holders (textures by instance guid; scalars as Float32, other
        /// vectors as Vec, bools as Boolean). Returns null or an error.
        /// </summary>
        public static string ApplyMaterials(EbxAssetEntry mesh, PortManifest m, IList<EbxAssetEntry> shaderTarget, Dictionary<string, EbxAssetEntry> texTarget,
            List<string> report, ExportLogger logger, out int depotSet, out int depotMissed)
        {
            depotSet = 0; depotMissed = 0;
            EbxAsset asset = App.AssetManager.GetEbx(mesh);
            IList materials = ReadList(asset.RootObject, "Materials");
            if (materials == null) return "mesh has no Materials list";
            if (materials.Count > m.Mesh.Materials.Count)
            {
                int extra = materials.Count - m.Mesh.Materials.Count;
                while (materials.Count > m.Mesh.Materials.Count)
                {
                    object last = ResolveInternal(materials[materials.Count - 1]);
                    materials.RemoveAt(materials.Count - 1);
                    try { if (last != null) asset.RemoveObject(last); } catch { }
                }
                report.Add(extra + " template material(s) beyond the source's " + m.Mesh.Materials.Count + " removed");
            }
            var fieldMisses = new List<string>();
            var depotParams = new List<Tuple<int, string, string>>();   // material, name, value
            for (int i = 0; i < m.Mesh.Materials.Count && i < materials.Count; i++)
            {
                PortMaterial pm = m.Mesh.Materials[i];
                object mat = ResolveInternal(materials[i]) ?? materials[i];
                EbxAssetEntry se = shaderTarget[i];
                Guid shaderClass = InstanceGuidOf(App.AssetManager.GetEbx(se).RootObject);
                EbxRefWriter.SetRefByPath(mat, "Shader.Shader", se.Guid, shaderClass);
                asset.AddDependency(se.Guid);
                object shader = ReadMember(mat, "Shader");
                foreach (string listName in new[] { "BoolParameters", "VectorParameters", "VectorArrayParameters", "TextureParameters", "ConditionalParameters" })
                {
                    IList l = ReadList(shader, listName);
                    if (l != null) l.Clear();
                }
                IList tp = ReadList(shader, "TextureParameters");
                foreach (PortTexture t in pm.Textures)
                {
                    EbxAssetEntry te;
                    if (t.Guid == null || !texTarget.TryGetValue(t.Guid, out te)) { report.Add("material " + i + " texture " + t.Param + " (" + t.Path + ") not resolved"); continue; }
                    Guid texClass;
                    try { texClass = InstanceGuidOf(App.AssetManager.GetEbx(te).RootObject); } catch { texClass = Guid.Empty; }
                    object param = EbxWriteCommand.CreateObjectByType("TextureShaderParameter");
                    PropertyPathWriter.SetValue(param, "ParameterName", t.Param);
                    EbxRefWriter.SetRefByPath(param, "Value", te.Guid, texClass);
                    tp.Add(param);
                    asset.AddDependency(te.Guid);
                    depotParams.Add(Tuple.Create(i, t.Param, (texClass != Guid.Empty ? texClass : te.Guid).ToString("D")));
                }
                IList bp = ReadList(shader, "BoolParameters");
                foreach (PortBool b in pm.Bools)
                {
                    object param = EbxWriteCommand.CreateObjectByType("BoolShaderParameter");
                    if (param == null || bp == null) { report.Add("BoolShaderParameter not available; " + b.Param + " not written"); continue; }
                    PropertyPathWriter.SetValue(param, "ParameterName", b.Param);
                    PropertyPathWriter.SetValue(param, "Value", b.Value ? "true" : "false");
                    bp.Add(param);
                    depotParams.Add(Tuple.Create(i, b.Param, b.Value ? "true" : "false"));
                }
                IList vp = ReadList(shader, "VectorParameters");
                foreach (PortVector v in pm.Vectors)
                {
                    float[] val = (float[])v.Value.Clone();
                    if (LuminanceParams.Contains(v.Param) && LuminanceScale != 1f)
                    {
                        string before = string.Join(",", val.Select(f => f.ToString("R", CultureInfo.InvariantCulture)));
                        bool color = v.Type != null && v.Type.EndsWith("Color", StringComparison.OrdinalIgnoreCase);
                        for (int c = 0; c < (color ? 3 : 4); c++) val[c] *= LuminanceScale;
                        LuminanceLog.Add(mesh.Name + " material " + i + " " + v.Param + ": " + before + " x" + LuminanceScale);
                    }
                    object param = EbxWriteCommand.CreateObjectByType("VectorShaderParameter");
                    if (param == null || vp == null) { report.Add("VectorShaderParameter not available; " + v.Param + " not written"); continue; }
                    PropertyPathWriter.SetValue(param, "ParameterName", v.Param);
                    if (v.Type != null) { try { PropertyPathWriter.SetValue(param, "ParameterType", v.Type); } catch { } }
                    PropertyPathWriter.SetValue(param, "Value.x", val[0].ToString("R", CultureInfo.InvariantCulture));
                    PropertyPathWriter.SetValue(param, "Value.y", val[1].ToString("R", CultureInfo.InvariantCulture));
                    PropertyPathWriter.SetValue(param, "Value.z", val[2].ToString("R", CultureInfo.InvariantCulture));
                    PropertyPathWriter.SetValue(param, "Value.w", val[3].ToString("R", CultureInfo.InvariantCulture));
                    vp.Add(param);
                    // shipped depots store every VectorShaderParameter as a 4-float Vec, Scalar ones included (DICE's
                    // HangarMediumBlastdoor_Left_01: EmissiveIntensity = 4,0,0,0)
                    depotParams.Add(Tuple.Create(i, v.Param, string.Join(",", val.Select(f => f.ToString("R", CultureInfo.InvariantCulture)))));
                }
                IList cp = ReadList(shader, "ConditionalParameters");
                foreach (PortConditional c in pm.Conditionals)
                {
                    EbxAssetEntry ce = null;
                    Guid cg;
                    if (c.AssetGuid != null && Guid.TryParse(c.AssetGuid, out cg)) ce = App.AssetManager.GetEbxEntry(cg);
                    if (ce == null && c.AssetPath != null) ce = App.AssetManager.GetEbxEntry(c.AssetPath);
                    if (ce == null) { report.Add("material " + i + " conditional " + (c.AssetPath ?? c.AssetGuid) + " is not in this game; not written"); continue; }
                    object param = EbxWriteCommand.CreateObjectByType("ConditionalShaderParameter");
                    if (param == null || cp == null) { report.Add("ConditionalShaderParameter not available"); continue; }
                    EbxRefWriter.SetRefByPath(param, "ConditionalAsset", ce.Guid, InstanceGuidOf(App.AssetManager.GetEbx(ce).RootObject));
                    try { PropertyPathWriter.SetValue(param, "Value", c.Value); } catch (Exception ex) { report.Add("conditional value " + c.Value + ": " + ex.Message); }
                    cp.Add(param);
                    asset.AddDependency(ce.Guid);
                }
                PortCommand.ApplyFields(mat, pm.Fields, fieldMisses);
                string terr = ApplyTessellation(asset, mat, i, pm, texTarget, report);
                if (terr != null) return terr;
            }
            if (fieldMisses.Count > 0) report.Add("material field(s) the target type lacks: " + string.Join(", ", fieldMisses.Distinct().Take(12)));
            if (!EbxWriteCommand.CallModifyEbx(mesh.Name, asset, logger)) return "could not save the mesh EBX";
            // the depot holders
            string firstError = null;
            foreach (var dp in depotParams)
            {
                int sections;
                string err = MaterialParamEditor.SetParam(mesh, dp.Item1, dp.Item2, dp.Item3, logger, true, out sections);
                if (err == null) depotSet++; else { depotMissed++; if (firstError == null) firstError = err; }
            }
            if (depotMissed > 0) report.Add(depotMissed + " parameter(s) not written to the shader block depot (" + firstError + ")");
            return null;
        }

        /// <summary>The source mesh's own settings where this game's mesh type has the same field, and the primary donor's MeshSet flags.</summary>
        public static void ApplyMeshFields(EbxAssetEntry mesh, PortManifest m, List<string> report, ExportLogger logger)
        {
            if (m.Mesh.Fields == null || m.Mesh.Fields.Count == 0) { report.Add("the package carries no mesh fields (old export): the template's LOD group/cull settings stay"); return; }
            EbxAsset asset = App.AssetManager.GetEbx(mesh);
            var miss = new List<string>();
            List<string> set = PortCommand.ApplyFields(asset.RootObject, m.Mesh.Fields, miss);
            // The source's occluder mesh (a culling-only proxy in its own resource) is not carried by the port: a mesh with
            // OccluderMeshEnable and no loadable OccluderMeshResource crashes the game on creation (game+0x6E6ED6A, a few
            // seconds after world-up; 15 of the Hoth 2015 meshes enable one, the hangar shells among them).
            try
            {
                dynamic root = asset.RootObject;
                if ((bool)root.OccluderMeshEnable && (ulong)root.OccluderMeshResource == 0)
                {
                    root.OccluderMeshEnable = false;
                    report.Add("the source enables an occluder mesh; its resource is not carried, so OccluderMeshEnable = false (culling only; game+0x6E6ED6A otherwise)");
                }
            }
            catch (Exception ex) { report.Add("could not check the occluder settings: " + ex.Message); }
            // A lightmapped (static Enlighten) source whose surfaces BFII compiles only without lightmap uvs: no drawn section can
            // carry them, so the mesh is lit the way BFII ships those surfaces -- by probes (DICE's ShieldGenerator_01_Ruin on the
            // same SS_PropsNonMetallicPreset_3UV). A static mesh without uvs cannot be charted and renders flat.
            try
            {
                dynamic root = asset.RootObject;
                string srcType;
                m.Mesh.Fields.TryGetValue("EnlightenType", out srcType);
                if (srcType == "EnlightenType_Static" && PortGeo.LastRadiosityDropped > 0)
                {
                    if (PortGeo.LastRadiosityKept == 0)
                    {
                        PropertyPathWriter.SetValue(root, "EnlightenType", "EnlightenType_Dynamic");
                        report.Add("EnlightenType_Static -> EnlightenType_Dynamic: none of the " + PortGeo.LastRadiosityDropped + " drawn lightmapped section(s) has a BFII permutation with lightmap uvs");
                    }
                    else report.Add("WARNING " + PortGeo.LastRadiosityDropped + " drawn section(s) lose their lightmap uvs (no permutation), " + PortGeo.LastRadiosityKept + " keep them; EnlightenType stays static");
                }
            }
            catch (Exception ex) { report.Add("could not check the Enlighten type: " + ex.Message); }
            if (!EbxWriteCommand.CallModifyEbx(mesh.Name, asset, logger)) { report.Add("could not save the mesh fields"); return; }
            string et;
            if (m.Mesh.Fields.TryGetValue("EnlightenType", out et)) report.Add("EnlightenType = " + et + " (source)");
            if (miss.Count > 0) report.Add("mesh field(s) not carried: " + string.Join(", ", miss.Take(12)));
        }

        /// <summary>Replaces the mesh's variation-database record (variation 0) with one derived from its materials now; adds one when absent.</summary>
        public static string RefreshMvdb(string mvdb, EbxAssetEntry mesh, ExportLogger logger, OutputFormatter formatter)
        {
            EbxAssetEntry db = App.AssetManager.GetEbxEntry(mvdb);
            if (db == null) return "mvdb not found: " + mvdb;
            EbxAsset dbAsset = App.AssetManager.GetEbx(db);
            IList entries = MvdbCommand.FindEntries(dbAsset.RootObject);
            if (entries != null && MvdbCommand.EntryExists(entries, mesh.Guid, 0))
            {
                if (MvdbCommand.ExecuteRemove(new[] { mvdb, "--mesh", mesh.Name }, logger, formatter) != 0) return "could not remove the old record";
            }
            return MvdbCommand.ExecuteAdd(new[] { mvdb, "--mesh", mesh.Name }, logger, formatter) == 0 ? null : "mvdb add failed";
        }

        // ------------------------------------------------------------------ port regeo

        // `port regeo --materials-only`: the material half of Execute below, on the mesh as it stands.
        private static int MaterialsOnly(string meshPath, EbxAssetEntry mesh, PortManifest m, List<EbxAssetEntry> shaderTarget,
            Dictionary<string, EbxAssetEntry> texTarget, List<string> created, int kept, int fallen, int newTex, string mvdb, string bundle,
            List<string> report, ExportLogger logger, OutputFormatter formatter)
        {
            EbxAsset meshAsset = App.AssetManager.GetEbx(mesh);
            IList materials = ReadList(meshAsset.RootObject, "Materials");
            // The shader block depot was built on the donors of each material's CURRENT surface; a different shader would
            // feed the new surface's parameters to the old surface's compiled permutations (memory bfii-rvm-database). That
            // needs the geometry port, so refuse here.
            for (int i = 0; i < m.Mesh.Materials.Count && i < shaderTarget.Count; i++)
            {
                object mat = i < materials.Count ? (ResolveInternal(materials[i]) ?? materials[i]) : null;
                object shader = mat != null ? ReadMember(mat, "Shader") : null;
                Guid cg, cc;
                bool same = shader != null && PortCommand.TryExternal(ReadMember(shader, "Shader"), out cg, out cc) && cg == shaderTarget[i].Guid;
                if (!same)
                {
                    Console.Error.WriteLine("port regeo: --materials-only: material " + i + " would change shader to " + shaderTarget[i].Name +
                                            "; the depot is compiled for the current one -- run a full regeo");
                    return 1;
                }
            }
            string merr = PortCommand.EnsureMaterialCountPublic(meshAsset, materials, m.Mesh.Materials.Count);
            if (merr != null) { Console.Error.WriteLine("port regeo: " + merr); return 1; }
            if (!EbxWriteCommand.CallModifyEbx(meshPath, meshAsset, logger)) return 1;
            int depotSet, depotMissed;
            string aerr = ApplyMaterials(mesh, m, shaderTarget, texTarget, report, logger, out depotSet, out depotMissed);
            if (aerr != null) { Console.Error.WriteLine("port regeo: " + aerr); return 1; }
            if (mvdb != null)
            {
                string verr = RefreshMvdb(mvdb, mesh, logger, formatter);
                if (verr != null) { Console.Error.WriteLine("port regeo: " + verr); return 1; }
            }
            if (bundle != null)
            {
                foreach (string name in created)
                    if (BundleCommand.ExecuteAdd(new[] { name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                foreach (EbxAssetEntry te in texTarget.Values)
                    if (!created.Contains(te.Name) && BundleCommand.ExecuteAdd(new[] { te.Name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                int scanned;
                foreach (EbxAssetEntry dt in LevelCommand.DepotTextures(mesh, out scanned))
                    if (BundleCommand.ExecuteAdd(new[] { dt.Name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
            }
            formatter.WriteResult("OK", "port regeo", new Dictionary<string, string>
            {
                { "mesh", meshPath }, { "mode", "materials-only" }, { "shaders_kept", kept.ToString(CultureInfo.InvariantCulture) },
                { "shaders_fallback", fallen.ToString(CultureInfo.InvariantCulture) }, { "textures_new", newTex.ToString(CultureInfo.InvariantCulture) },
                { "depot_params_set", depotSet.ToString(CultureInfo.InvariantCulture) }, { "depot_params_missed", depotMissed.ToString(CultureInfo.InvariantCulture) }
            });
            foreach (string r in report) logger.LogWarning("port regeo: {0}", r);
            return 0;
        }

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string dir = null, meshPath = null, geoPath = null, residentPath = null, mvdb = null, bundle = null, shaderFallback = null, texTemplate = null, shaderMapFile = null;
            bool allowForeign = false, materialsOnly = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--mesh" && i + 1 < args.Length) meshPath = args[++i];
                else if (args[i] == "--materials-only") materialsOnly = true;
                else if (args[i] == "--geo" && i + 1 < args.Length) geoPath = args[++i];
                else if (args[i] == "--resident" && i + 1 < args.Length) residentPath = args[++i];
                else if (args[i] == "--mvdb" && i + 1 < args.Length) mvdb = args[++i];
                else if (args[i] == "--bundle" && i + 1 < args.Length) bundle = args[++i];
                else if (args[i] == "--shader-fallback" && i + 1 < args.Length) shaderFallback = args[++i];
                else if (args[i] == "--shader-map" && i + 1 < args.Length) shaderMapFile = args[++i];
                else if (args[i] == "--texture-template" && i + 1 < args.Length) texTemplate = args[++i];
                else if (args[i] == "--allow-foreign-donors") allowForeign = true;
                else if (args[i] == "--luminance-scale" && i + 1 < args.Length) LuminanceScale = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--luminance-params" && i + 1 < args.Length) { foreach (string n in args[++i].Split(',')) { if (n.Trim().Length > 0) LuminanceParams.Add(n.Trim()); } }
                else if (!args[i].StartsWith("--")) dir = args[i];
            }
            if (dir == null || meshPath == null) { Console.Error.WriteLine("port regeo: usage: port regeo <package-dir> --mesh <existing mesh> [--materials-only] [--geo f] [--resident f] [--mvdb db] [--bundle b] (got: " + string.Join(" | ", args) + ")"); return 1; }
            PortCommand.LoadShaderMap(shaderMapFile);
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("port regeo: no project loaded"); return 1; }
            PortManifest m;
            try { m = PortManifest.Load(dir); } catch (Exception ex) { Console.Error.WriteLine("port regeo: " + ex.Message); return 1; }
            // --materials-only: shaders, textures, material parameters, variation record and bundling from the package, the
            // mesh's geometry, LODs, bounds and radiosity uvs untouched -- so it can run after the lighting stage wrote 2015's
            // lightmap uvs into the ported meshes (a full regeo would rebuild the vertex data without them).
            GeoMesh geo = null;
            if (!materialsOnly)
            {
                if (geoPath == null) geoPath = Path.Combine(dir, m.Mesh.GeoFile ?? "mesh.meshgeo");
                string gerr = GeoMesh.Load(geoPath, out geo);
                if (gerr != null) { Console.Error.WriteLine("port regeo: " + gerr); return 1; }
            }
            EbxAssetEntry mesh = App.AssetManager.GetEbxEntry(meshPath);
            if (mesh == null) { Console.Error.WriteLine("port regeo: mesh not found: " + meshPath); return 1; }
            string asDir = meshPath.Contains("/") ? meshPath.Substring(0, meshPath.LastIndexOf('/')) : "";
            var report = new List<string>();
            var created = new List<string>();

            var shaderTarget = new List<EbxAssetEntry>();
            int kept = 0, fallen = 0;
            if (PortCommand.ResolveShaders(m, shaderFallback ?? PortCommand.DefaultShaderFallback, report, shaderTarget, ref kept, ref fallen) != 0) return 1;
            PortCommand.DropKeptGraphDefaults(m, shaderTarget, report);
            var texTarget = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);
            int reusedTex = 0, newTex = 0;
            if (PortCommand.ResolveTextures(m, dir, asDir, texTemplate, report, created, texTarget, ref reusedTex, ref newTex, logger, formatter) != 0) return 1;

            if (materialsOnly) return MaterialsOnly(meshPath, mesh, m, shaderTarget, texTarget, created, kept, fallen, newTex, mvdb, bundle, report, logger, formatter);

            // LOD count: the port needs every source LOD
            int lods; List<List<string>> names; List<List<int>> mats;
            string derr = MeshCloner.DescribeSections(mesh, out lods, out names, out mats);
            if (derr != null) { Console.Error.WriteLine("port regeo: " + derr); return 1; }
            if (lods > geo.Lods.Count)
            {
                string terr = MeshCloner.TrimLods(mesh, geo.Lods.Count);
                if (terr != null) { Console.Error.WriteLine("port regeo: " + terr); return 1; }
                int removed;
                terr = MeshCloner.TrimDepotLods(mesh, geo.Lods.Count, out removed);
                if (terr != null) { Console.Error.WriteLine("port regeo: " + terr); return 1; }
                report.Add("mesh LODs trimmed " + lods + " -> " + geo.Lods.Count);
            }
            else if (lods < geo.Lods.Count)
            {
                string lerr = MeshCloner.GrowLods(mesh, geo.Lods.Count);
                if (lerr != null) { Console.Error.WriteLine("port regeo: the mesh has " + lods + " LOD(s), the source " + geo.Lods.Count + ": " + lerr); return 1; }
                report.Add("mesh LODs grown " + lods + " -> " + geo.Lods.Count + " (new LOD records, chunks and depot entries)");
            }
            int resident;
            MeshCloner.MarkAllLodsResident(mesh, out resident);

            // materials: count first (the depot holders are per material), then the geometry, then parameters
            EbxAsset meshAsset = App.AssetManager.GetEbx(mesh);
            IList materials = ReadList(meshAsset.RootObject, "Materials");
            string merr = PortCommand.EnsureMaterialCountPublic(meshAsset, materials, m.Mesh.Materials.Count);
            if (merr != null) { Console.Error.WriteLine("port regeo: " + merr); return 1; }
            if (!EbxWriteCommand.CallModifyEbx(meshPath, meshAsset, logger)) return 1;

            var plan = new List<string>();
            string perr = PortGeo.Run(mesh, geo, shaderTarget, new PortGeo.Options { ResidentPath = residentPath, AllowForeign = allowForeign, SourceTessellation = PortGeo.Options.TessellationOf(m), SourceFields = PortGeo.Options.FieldsOf(m), FallbackMaterials = PortGeo.Options.FallbacksOf(m, shaderTarget) }, logger, report, plan);
            if (perr != null) { Console.Error.WriteLine("port regeo: geometry port failed: " + perr); return 1; }
            foreach (string pl in plan) logger.Log("port regeo: section {0}", pl);
            string bdetail;
            string bberr = MeshCloner.RecomputeBoundingBox(mesh, 64f, out bdetail);
            if (bberr != null) { Console.Error.WriteLine("port regeo: " + bberr); return 1; }
            int depotSet, depotMissed;
            string aerr = ApplyMaterials(mesh, m, shaderTarget, texTarget, report, logger, out depotSet, out depotMissed);
            if (aerr != null) { Console.Error.WriteLine("port regeo: " + aerr); return 1; }
            ApplyMeshFields(mesh, m, report, logger);
            if (mvdb != null)
            {
                string verr = RefreshMvdb(mvdb, mesh, logger, formatter);
                if (verr != null) { Console.Error.WriteLine("port regeo: " + verr); return 1; }
            }
            if (bundle != null)
            {
                foreach (string name in created)
                    if (BundleCommand.ExecuteAdd(new[] { name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                foreach (EbxAssetEntry te in texTarget.Values)
                    if (!created.Contains(te.Name) && BundleCommand.ExecuteAdd(new[] { te.Name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                // the mesh's res/chunks (a grown LOD adds chunks) -- idempotent for what is already in the bundle
                if (BundleCommand.ExecuteAdd(new[] { meshPath, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                // every texture the rebuilt depot binds by instance guid (the surface's standard bindings kept in the holders):
                // none of them shows as an EBX dependency, and an unbundled one faults at game+0x1522B97 (memory
                // bf2-instance-vs-file-guid)
                int scanned;
                foreach (EbxAssetEntry dt in LevelCommand.DepotTextures(mesh, out scanned))
                    if (BundleCommand.ExecuteAdd(new[] { dt.Name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
            }
            formatter.WriteResult("OK", "port regeo", new Dictionary<string, string>
            {
                { "mesh", meshPath }, { "lods", geo.Lods.Count.ToString(CultureInfo.InvariantCulture) },
                { "sections", plan.Count.ToString(CultureInfo.InvariantCulture) }, { "shaders_kept", kept.ToString(CultureInfo.InvariantCulture) },
                { "shaders_fallback", fallen.ToString(CultureInfo.InvariantCulture) }, { "textures_new", newTex.ToString(CultureInfo.InvariantCulture) },
                { "depot_params_set", depotSet.ToString(CultureInfo.InvariantCulture) }, { "bounding", bdetail }
            });
            foreach (string r in report) logger.LogWarning("port regeo: {0}", r);
            return 0;
        }

        // port geo-plan --list <file: one package dir per line> [--resident f] [--allow-foreign-donors] [--verbose]: the donor
        // choice for every section of every package, without a project change -- the list of what the geometry port can and
        // cannot do before running it.
        public static int ExecutePlan(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = null, residentPath = null, shaderMapFile = null; bool allowForeign = false, verbose = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--shader-map" && i + 1 < args.Length) shaderMapFile = args[++i];
                else if (args[i] == "--list" && i + 1 < args.Length) list = args[++i];
                else if (args[i] == "--resident" && i + 1 < args.Length) residentPath = args[++i];
                else if (args[i] == "--allow-foreign-donors") allowForeign = true;
                else if (args[i] == "--verbose") verbose = true;
            }
            if (list == null) { Console.Error.WriteLine("port geo-plan: need --list <file>"); return 1; }
            PortCommand.LoadShaderMap(shaderMapFile);
            int ok = 0, bad = 0, foreignPkgs = 0, lossy = 0;
            foreach (string raw in File.ReadAllLines(list))
            {
                string dir = raw.Trim();
                if (dir.Length == 0 || dir.StartsWith("#")) continue;
                PortManifest m; GeoMesh geo;
                try { m = PortManifest.Load(dir); } catch (Exception ex) { Console.WriteLine("FAIL " + dir + ": " + ex.Message); bad++; continue; }
                string gerr = GeoMesh.Load(Path.Combine(dir, m.Mesh.GeoFile ?? "mesh.meshgeo"), out geo);
                if (gerr != null) { Console.WriteLine("FAIL " + dir + ": " + gerr); bad++; continue; }
                var report = new List<string>(); var shaderTarget = new List<EbxAssetEntry>(); int kept = 0, fallen = 0;
                if (PortCommand.ResolveShaders(m, PortCommand.DefaultShaderFallback, report, shaderTarget, ref kept, ref fallen) != 0) { Console.WriteLine("FAIL " + dir + ": shader resolution"); bad++; continue; }
                var plan = new List<string>();
                string err = PortGeo.Run(null, geo, shaderTarget, new PortGeo.Options { ResidentPath = residentPath, AllowForeign = allowForeign, PlanOnly = true, SourceTessellation = PortGeo.Options.TessellationOf(m), SourceFields = PortGeo.Options.FieldsOf(m), FallbackMaterials = PortGeo.Options.FallbacksOf(m, shaderTarget) }, logger, report, plan);
                bool foreign = plan.Any(x => x.Contains(" FOREIGN"));
                bool dropped = plan.Any(x => x.Contains("dropped") && !x.Contains("(empty:") ) || plan.Any(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"dropped [^(]*\(empty: [^)]*\)") == false && x.Contains("dropped"));
                if (foreign) foreignPkgs++;
                if (err == null) ok++; else bad++;
                Console.WriteLine((err == null ? "OK   " : "NO   ") + Path.GetFileName(dir) + " (" + geo.Lods.Count + " LODs, " + m.Mesh.Materials.Count + " materials, shaders kept " + kept + " fallback " + fallen + ")" + (foreign ? " FOREIGN" : ""));
                foreach (string r in report) if (verbose || r.StartsWith("no donor") || r.Contains("not in this game")) Console.WriteLine("       " + r);
                if (verbose) foreach (string pl in plan) Console.WriteLine("       " + pl);
                else foreach (string pl in plan.Where(x => x.StartsWith("lod0/")).Where(x => x.Contains("dropped") || x.Contains("filled") || x.Contains("NO DONOR") || x.Contains("FOREIGN"))) Console.WriteLine("       " + pl);
            }
            formatter.WriteResult(bad == 0 ? "OK" : "WARN", "port geo-plan", new Dictionary<string, string>
            { { "packages_ok", ok.ToString(CultureInfo.InvariantCulture) }, { "packages_without_donors", bad.ToString(CultureInfo.InvariantCulture) }, { "packages_with_foreign_donors", foreignPkgs.ToString(CultureInfo.InvariantCulture) } });
            return 0;
        }

        // port prune-refs <asset> --prefix <asset path prefix> | --dangling: removes from every list in <asset> the external
        // references to assets whose path starts with the prefix (a network registry's Objects after the blueprints were
        // taken out, a variation database's entries ...), or with --dangling the references to an instance their partition no
        // longer exports (the closure audit's MISSING rule: registry entries of entities `level clear`/`level remove` took
        // out). Reports what it removed; the asset is saved only if something changed.
        public static int ExecutePruneRefs(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string assetPath = null, prefix = null; bool dangling = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--prefix" && i + 1 < args.Length) prefix = args[++i];
                else if (args[i] == "--dangling") dangling = true;
                else if (!args[i].StartsWith("--")) assetPath = args[i];
            }
            if (assetPath == null || (prefix == null && !dangling)) { Console.Error.WriteLine("port prune-refs: usage: port prune-refs <asset> --prefix <path prefix> | --dangling"); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            if (entry == null) { Console.Error.WriteLine("port prune-refs: not found: " + assetPath); return 1; }
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            int removed = 0;
            var seen = new HashSet<object>();
            var dang = dangling ? new DanglingRefs() : null;
            foreach (object obj in asset.Objects) removed += Prune(obj, prefix, seen, 0, dang, logger);
            if (removed > 0 && !EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            formatter.WriteResult("OK", "port prune-refs", new Dictionary<string, string> { { "asset", assetPath }, { "prefix", prefix ?? "" }, { "dangling", dangling ? "true" : "false" }, { "removed", removed.ToString(CultureInfo.InvariantCulture) } });
            return 0;
        }

        // port repoint-refs <asset> --from <ebx path> --to <ebx path | null>: every EXTERNAL reference in <asset> to the
        // partition <from> is re-pointed to <to> (or nulled). The class guid follows the exported instance's POSITION:
        // the n-th exported instance of <from> maps to the n-th exported instance of <to>, which is exactly right for an
        // ebx-clone / mesh clone (same object order, fresh guids). Single fields, list elements and references nested in
        // structs (written back) are all handled. Built for owned copies of shipped content whose chunks a mod cannot
        // bundle (Mygeeto E-Web: clone the turret, re-point it to cloned meshes, null the refused sounds).
        public static int ExecuteRepointRefs(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string assetPath = null, from = null, to = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--to" && i + 1 < args.Length) to = args[++i];
                else if (!args[i].StartsWith("--")) assetPath = args[i];
            }
            if (assetPath == null || from == null || to == null) { Console.Error.WriteLine("port repoint-refs: usage: port repoint-refs <asset> --from <path> --to <path|null>"); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath), fe = App.AssetManager.GetEbxEntry(from);
            if (entry == null || fe == null) { Console.Error.WriteLine("port repoint-refs: not found: " + (entry == null ? assetPath : from)); return 1; }
            bool toNull = string.Equals(to, "null", StringComparison.OrdinalIgnoreCase);
            EbxAssetEntry te = toNull ? null : App.AssetManager.GetEbxEntry(to);
            if (!toNull && te == null) { Console.Error.WriteLine("port repoint-refs: not found: " + to); return 1; }
            List<Guid> fromExp = ExportedGuids(fe), toExp = te != null ? ExportedGuids(te) : null;
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            int changed = 0, unmapped = 0;
            Func<object, object> swap = pr =>
            {
                object ext = ReadMember(pr, "External");
                if (!(ReadMember(ext, "FileGuid") is Guid fg) || fg != fe.Guid) return null;
                if (toNull) { changed++; return Activator.CreateInstance(pr.GetType()); }
                Guid cg = ReadMember(ext, "ClassGuid") is Guid g ? g : Guid.Empty;
                int idx = fromExp.IndexOf(cg);
                if (idx < 0 || idx >= toExp.Count) { unmapped++; return null; }
                changed++;
                return EbxRefWriter.BuildExternalPointerRef(pr.GetType(), te.Guid, toExp[idx]);
            };
            var seen = new HashSet<object>();
            foreach (object obj in asset.Objects) RepointWalk(obj, swap, seen, 0);
            if (unmapped > 0) { Console.Error.WriteLine("port repoint-refs: " + unmapped + " reference(s) name an instance of " + from + " that has no counterpart in " + to); return 1; }
            if (changed > 0)
            {
                if (te != null) asset.AddDependency(te.Guid);
                if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            }
            formatter.WriteResult("OK", "port repoint-refs", new Dictionary<string, string> { { "asset", assetPath }, { "from", from }, { "to", to }, { "changed", changed.ToString(CultureInfo.InvariantCulture) } });
            return 0;
        }

        // port repoint-map <asset>... --map <file: from-path TAB to-path per line>: repoint-refs for a whole table at once, one
        // walk per asset. External references to any <from> partition move to the instance at the same exported position in
        // <to> (the n-th exported instance of from -> the n-th of to, right for two port-imported assets of one kind). Used to
        // swap a level's imported pieces for re-imported ones (CoruscantBF3 CBL: lightmap-capable templates) while every
        // placement, transform and connection stays.
        public static int ExecuteRepointMap(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mapFile = null;
            var assets = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--map" && i + 1 < args.Length) mapFile = args[++i];
                else if (!args[i].StartsWith("--")) assets.Add(args[i]);
            }
            if (mapFile == null || assets.Count == 0) { Console.Error.WriteLine("port repoint-map: usage: port repoint-map <asset>... --map <file>"); return 1; }
            var fromTo = new Dictionary<Guid, Tuple<EbxAssetEntry, List<Guid>, List<Guid>>>();   // from file guid -> (to, from exports, to exports)
            int pairs = 0, missing = 0;
            foreach (string raw in File.ReadAllLines(mapFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split('\t');
                if (p.Length < 2) continue;
                EbxAssetEntry fe = App.AssetManager.GetEbxEntry(p[0].Trim()), te = App.AssetManager.GetEbxEntry(p[1].Trim());
                if (fe == null || te == null) { missing++; logger.LogWarning("port repoint-map: not found: {0}", fe == null ? p[0] : p[1]); continue; }
                fromTo[fe.Guid] = Tuple.Create(te, ExportedGuids(fe), ExportedGuids(te));
                pairs++;
            }
            if (missing > 0) { Console.Error.WriteLine("port repoint-map: " + missing + " map entr(ies) name a missing asset"); return 1; }
            int failures = 0;
            long totalChanged = 0;
            foreach (string assetPath in assets)
            {
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
                if (entry == null) { Console.Error.WriteLine("port repoint-map: not found: " + assetPath); failures++; continue; }
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                int changed = 0, unmapped = 0;
                var usedTargets = new HashSet<Guid>();
                Func<object, object> swap = pr =>
                {
                    object ext = ReadMember(pr, "External");
                    if (!(ReadMember(ext, "FileGuid") is Guid fg)) return null;
                    Tuple<EbxAssetEntry, List<Guid>, List<Guid>> t;
                    if (!fromTo.TryGetValue(fg, out t)) return null;
                    Guid cg = ReadMember(ext, "ClassGuid") is Guid g ? g : Guid.Empty;
                    int idx = t.Item2.IndexOf(cg);
                    if (idx < 0 || idx >= t.Item3.Count) { unmapped++; return null; }
                    changed++;
                    usedTargets.Add(t.Item1.Guid);
                    return EbxRefWriter.BuildExternalPointerRef(pr.GetType(), t.Item1.Guid, t.Item3[idx]);
                };
                var seen = new HashSet<object>();
                foreach (object obj in asset.Objects) RepointWalk(obj, swap, seen, 0);
                if (unmapped > 0) { Console.Error.WriteLine("port repoint-map: " + assetPath + ": " + unmapped + " reference(s) without a counterpart instance"); failures++; continue; }
                if (changed > 0)
                {
                    foreach (Guid tg in usedTargets) asset.AddDependency(tg);
                    if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) { failures++; continue; }
                }
                totalChanged += changed;
                logger.Log("port repoint-map {0}: {1} reference(s) repointed", assetPath, changed);
            }
            formatter.WriteResult(failures == 0 ? "OK" : "WARN", "port repoint-map", new Dictionary<string, string>
            {
                { "pairs", pairs.ToString(CultureInfo.InvariantCulture) }, { "assets", assets.Count.ToString(CultureInfo.InvariantCulture) },
                { "changed", totalChanged.ToString(CultureInfo.InvariantCulture) }, { "failures", failures.ToString(CultureInfo.InvariantCulture) }
            });
            return failures == 0 ? 0 : 1;
        }

        private static List<Guid> ExportedGuids(EbxAssetEntry e)
        {
            var list = new List<Guid>();
            foreach (object obj in App.AssetManager.GetEbx(e).Objects)
            {
                dynamic ig = ((dynamic)obj).GetInstanceGuid();
                if ((bool)ig.IsExported) list.Add((Guid)ig.ExportedGuid);
            }
            return list;
        }

        // Walk o's public properties; a PointerRef value that `swap` replaces (non-null result) is written back. Value-type
        // containers (structs) are walked as boxed copies and written back when anything inside them changed.
        private static bool RepointWalk(object o, Func<object, object> swap, HashSet<object> seen, int depth)
        {
            if (o == null || depth > 12 || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || o is Guid) return false;
            if (!o.GetType().IsValueType && !seen.Add(o)) return false;
            bool any = false;
            foreach (PropertyInfo pi in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = pi.GetValue(o); } catch { continue; }
                if (v == null) continue;
                if (v.GetType().Name == "PointerRef")
                {
                    object nv = swap(v);
                    if (nv != null && pi.CanWrite) { pi.SetValue(o, nv); any = true; }
                    continue;
                }
                if (v is IList list)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        object e = list[i];
                        if (e == null) continue;
                        if (e.GetType().Name == "PointerRef")
                        {
                            object nv = swap(e);
                            if (nv != null) { list[i] = nv; any = true; }
                        }
                        else if (e.GetType().IsValueType && !e.GetType().IsPrimitive && !e.GetType().IsEnum && !(e is Guid))
                        {
                            if (RepointWalk(e, swap, seen, depth + 1)) { list[i] = e; any = true; }
                        }
                        else if (!(e is string) && !e.GetType().IsPrimitive) any |= RepointWalk(e, swap, seen, depth + 1);
                    }
                    continue;
                }
                if (v.GetType().IsValueType)
                {
                    if (RepointWalk(v, swap, seen, depth + 1) && pi.CanWrite) { pi.SetValue(o, v); any = true; }
                }
                else any |= RepointWalk(v, swap, seen, depth + 1);
            }
            return any;
        }

        private static int Prune(object o, string prefix, HashSet<object> seen, int depth, DanglingRefs dang = null, ExportLogger logger = null)
        {
            if (o == null || depth > 6 || o.GetType().IsValueType && o.GetType().Name != "PointerRef" || o is string) return 0;
            if (!o.GetType().IsValueType && !seen.Add(o)) return 0;
            int removed = 0;
            foreach (PropertyInfo pi in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = pi.GetValue(o); } catch { continue; }
                var list = v as IList;
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object e = list[i];
                    if (e != null && e.GetType().Name == "PointerRef")
                    {
                        object ext = ReadMember(e, "External");
                        object fg = ext != null ? ReadMember(ext, "FileGuid") : null;
                        if (fg is Guid && (Guid)fg != Guid.Empty)
                        {
                            EbxAssetEntry t = App.AssetManager.GetEbxEntry((Guid)fg);
                            string why;
                            if (prefix != null && t != null && t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { list.RemoveAt(i); removed++; }
                            else if (dang != null && dang.IsDangling(e, out why)) { list.RemoveAt(i); removed++; logger?.Log("port prune-refs: dangling {0}", why); }
                        }
                    }
                    else if (e != null && !(e is string) && !e.GetType().IsPrimitive) removed += Prune(e, prefix, seen, depth + 1, dang, logger);
                }
            }
            return removed;
        }

        // port mesh-facts --list <file: one blueprint per line> --out <json>: per blueprint the mesh its entity renders (path,
        // file guid, the class guid the blueprint's Mesh reference names) and per LOD per section the vertex/primitive counts and
        // whether the section declares RadiosityTexCoord. Runs under either game: one pass per side, a script compares them.
        public static int ExecuteMeshFacts(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string list = null, outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--list" && i + 1 < args.Length) list = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
            }
            if (list == null || outPath == null) { Console.Error.WriteLine("port mesh-facts: need --list <file> --out <json>"); return 1; }
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            int ok = 0, miss = 0;
            foreach (string raw in File.ReadAllLines(list))
            {
                string bp = raw.Trim();
                if (bp.Length == 0 || bp.StartsWith("#") || result.ContainsKey(bp)) continue;
                var facts = new Dictionary<string, object>();
                try
                {
                    EbxAssetEntry be = App.AssetManager.GetEbxEntry(bp);
                    if (be == null) { facts["error"] = "blueprint not found"; result[bp] = facts; miss++; continue; }
                    object entity = ResolveInternal(ReadMember(App.AssetManager.GetEbx(be).RootObject, "Object"));
                    object meshRef = ReadMember(entity, "Mesh");
                    object ext = meshRef != null ? ReadMember(meshRef, "External") : null;
                    object fg = ext != null ? ReadMember(ext, "FileGuid") : null, cg = ext != null ? ReadMember(ext, "ClassGuid") : null;
                    if (!(fg is Guid) || (Guid)fg == Guid.Empty) { facts["error"] = "no external Mesh reference"; result[bp] = facts; miss++; continue; }
                    EbxAssetEntry me = App.AssetManager.GetEbxEntry((Guid)fg);
                    facts["mesh"] = me != null ? me.Name : null;
                    facts["meshFileGuid"] = ((Guid)fg).ToString("D");
                    facts["meshClassGuid"] = cg is Guid ? ((Guid)cg).ToString("D") : null;
                    GeoMesh g;
                    if (me != null && GeoMesh.Read(me, out g) == null)
                    {
                        var lods = new List<object>();
                        foreach (GeoLod l in g.Lods)
                        {
                            var secs = new List<object>();
                            foreach (GeoSection sec in l.Sections)
                                secs.Add(new object[] { sec.Name, sec.VertexCount, sec.PrimitiveCount, sec.Find(FrostySdk.VertexElementUsage.RadiosityTexCoord) != null,
                                                        ChannelHash(sec, FrostySdk.VertexElementUsage.Pos, 3, 1e-4f), ChannelHash(sec, FrostySdk.VertexElementUsage.RadiosityTexCoord, 2, 1e-5f) });
                            lods.Add(secs);
                        }
                        facts["lods"] = lods;
                    }
                    ok++;
                }
                catch (Exception ex) { facts["error"] = ex.Message; miss++; }
                result[bp] = facts;
            }
            File.WriteAllText(outPath, Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented));
            formatter.WriteResult("OK", "port mesh-facts", new Dictionary<string, string> { { "blueprints", ok.ToString(CultureInfo.InvariantCulture) }, { "unresolved", miss.ToString(CultureInfo.InvariantCulture) }, { "out", outPath } });
            return 0;
        }

        // port repoint-member <level> --group <i> --member <j> --blueprint <bp> [--expect <bp now>] [--expect-members <n>]:
        // points one static-group member at another blueprint's ENTITY (DICE members reference the blueprint's
        // StaticModelEntityData, not its root). Everything else of the member (instances, physics part ranges, network ids)
        // stays: the group's own converted 2015 physics already carries the member's collision. Refuses when the member does
        // not currently name --expect or the group has a different member count (guards against index drift).
        public static int ExecuteRepointMember(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, bp = null, expect = null; int group = -1, member = -1, expectMembers = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--group" && i + 1 < args.Length) group = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--member" && i + 1 < args.Length) member = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--blueprint" && i + 1 < args.Length) bp = args[++i];
                else if (args[i] == "--expect" && i + 1 < args.Length) expect = args[++i];
                else if (args[i] == "--expect-members" && i + 1 < args.Length) expectMembers = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (level == null || bp == null || group < 0 || member < 0) { Console.Error.WriteLine("port repoint-member: usage: port repoint-member <level> --group i --member j --blueprint <bp> [--expect <bp>] [--expect-members n]"); return 1; }
            EbxAssetEntry le = App.AssetManager.GetEbxEntry(level), be = App.AssetManager.GetEbxEntry(bp);
            if (le == null || be == null) { Console.Error.WriteLine("port repoint-member: " + (le == null ? "level" : "blueprint") + " not found"); return 1; }
            EbxAsset la = App.AssetManager.GetEbx(le);
            string err;
            object g = LevelCommand.FindStaticGroup(la.RootObject, group, out err);
            if (g == null) { Console.Error.WriteLine("port repoint-member: " + err); return 1; }
            IList members;
            object m = LevelCommand.FindMember(g, member, out members, out err);
            if (m == null) { Console.Error.WriteLine("port repoint-member: " + err); return 1; }
            if (expectMembers >= 0 && members.Count != expectMembers) { Console.Error.WriteLine("port repoint-member: group " + group + " has " + members.Count + " members, expected " + expectMembers); return 1; }
            string now = LevelCommand.ResolveBlueprintPath(ReadMember(m, "MemberType"));
            if (expect != null && !string.Equals(now, expect, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("port repoint-member: member " + member + " names " + now + ", expected " + expect); return 1; }
            // A member draws its own MeshAsset, not its blueprint's mesh: MemberType alone left the stand-in's mesh drawn (and a
            // 1-part mesh on a 2-part slot crashed on damage, game+0x601CD1). `level repoint-members` sets both and checks the
            // slot's part structure against the new blueprint.
            return LevelRepointMembers.Execute(new[] { level, "--set", group.ToString(CultureInfo.InvariantCulture) + ":" + member.ToString(CultureInfo.InvariantCulture) + "=" + bp }, logger, formatter);
        }

        // port check-members <level>: every static-group member against its MemberType blueprint -- PhysicsPartCountPerInstance
        // must equal len(Object.PhysicsPartInfos) (a larger count crashes the game at game+0x601CD1 when an instance takes
        // damage: ServerStaticModelPhysicsComponent walks the member's part count over an array sized by the blueprint) and
        // PartComponentCount must not exceed the blueprint's PartComponentData count (game+0xBC0848). Exit 2 on any finding.
        public static int ExecuteCheckMembers(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = args.FirstOrDefault(a => !a.StartsWith("--"));
            if (level == null) { Console.Error.WriteLine("port check-members: missing <level>"); return 1; }
            EbxAssetEntry le = App.AssetManager.GetEbxEntry(level);
            if (le == null) { Console.Error.WriteLine("port check-members: not found: " + level); return 1; }
            EbxAsset la = App.AssetManager.GetEbx(le);
            int groups = 0, members = 0, bad = 0;
            var cache = new Dictionary<Guid, int[]>();
            var bpMeshCache = new Dictionary<Guid, Guid>();
            for (int gi = 0; ; gi++)
            {
                string err;
                object g = LevelCommand.FindStaticGroup(la.RootObject, gi, out err);
                if (g == null) break;
                groups++;
                IList list = ReadList(g, "MemberDatas");
                for (int mi = 0; list != null && mi < list.Count; mi++)
                {
                    object m = list[mi];
                    members++;
                    object mt = ReadMember(m, "MemberType");
                    object ext = mt != null ? ReadMember(mt, "External") : null;
                    object fg = ext != null ? ReadMember(ext, "FileGuid") : null;
                    if (!(fg is Guid) || (Guid)fg == Guid.Empty) continue;
                    int[] bp;
                    if (!cache.TryGetValue((Guid)fg, out bp))
                    {
                        bp = new[] { -1, -1 };
                        try
                        {
                            EbxAssetEntry be = App.AssetManager.GetEbxEntry((Guid)fg);
                            object entity = be != null ? ResolveInternal(ReadMember(App.AssetManager.GetEbx(be).RootObject, "Object")) : null;
                            IList infos = ReadList(entity, "PhysicsPartInfos");
                            bp[0] = infos != null ? infos.Count : 0;
                            int pcd = 0;
                            IList comps = ReadList(entity, "Components");
                            if (comps != null) foreach (object c in comps) { object pc = ResolveInternal(c); if (pc != null && pc.GetType().Name == "PartComponentData") pcd++; }
                            bp[1] = pcd;
                        }
                        catch { }
                        cache[(Guid)fg] = bp;
                    }
                    // the member draws its own MeshAsset: it must be the blueprint's mesh
                    Guid memberMesh = ExternalFileGuid(ReadMember(m, "MeshAsset")), bpMesh;
                    if (!bpMeshCache.TryGetValue((Guid)fg, out bpMesh))
                    {
                        bpMesh = Guid.Empty;
                        try
                        {
                            EbxAssetEntry be2 = App.AssetManager.GetEbxEntry((Guid)fg);
                            object ent2 = be2 != null ? ResolveInternal(ReadMember(App.AssetManager.GetEbx(be2).RootObject, "Object")) : null;
                            bpMesh = ExternalFileGuid(ReadMember(ent2, "Mesh"));
                        }
                        catch { }
                        bpMeshCache[(Guid)fg] = bpMesh;
                    }
                    int perInstance = Convert.ToInt32(ReadMember(m, "PhysicsPartCountPerInstance") ?? 0, CultureInfo.InvariantCulture);
                    int partComponents = Convert.ToInt32(ReadMember(m, "PartComponentCount") ?? 0, CultureInfo.InvariantCulture);
                    string name = LevelCommand.ResolveBlueprintPath(mt);
                    var why = new List<string>();
                    if (bp[0] >= 0 && perInstance > 0 && perInstance != bp[0]) why.Add("PhysicsPartCountPerInstance " + perInstance + " != PhysicsPartInfos " + bp[0]);
                    if (bp[1] >= 0 && partComponents > bp[1]) why.Add("PartComponentCount " + partComponents + " > PartComponentData " + bp[1]);
                    if (bpMesh != Guid.Empty && memberMesh != Guid.Empty && memberMesh != bpMesh)
                    {
                        EbxAssetEntry mm1 = App.AssetManager.GetEbxEntry(memberMesh), mm2 = App.AssetManager.GetEbxEntry(bpMesh);
                        why.Add("MeshAsset " + (mm1 != null ? mm1.Name : memberMesh.ToString()) + " is not the blueprint's mesh " + (mm2 != null ? mm2.Name : bpMesh.ToString()));
                    }
                    if (why.Count > 0) { bad++; Console.WriteLine("MISMATCH group " + gi + " member " + mi + " " + name + ": " + string.Join("; ", why)); }
                }
            }
            formatter.WriteResult(bad == 0 ? "OK" : "WARN", "port check-members", new Dictionary<string, string> { { "groups", groups.ToString() }, { "members", members.ToString() }, { "mismatches", bad.ToString() } });
            return bad == 0 ? 0 : 2;
        }

        // FNV-1a 64 over a channel's decoded values quantised to <q> (format independent: Half3 and Float3 positions of the same
        // mesh hash alike); null when the section lacks the channel.
        private static string ChannelHash(GeoSection s, FrostySdk.VertexElementUsage u, int comps, float q)
        {
            GeoElement e = s.Find(u);
            if (e == null) return null;
            ulong h = 14695981039346656037UL;
            for (int v = 0; v < s.VertexCount; v++)
            {
                float[] x = VertexCodec.Decode(s.Vertices, v * s.Stride + e.Offset, e.Format);
                for (int c = 0; c < comps; c++)
                {
                    long k = (long)Math.Round(x[c] / q);
                    for (int b = 0; b < 8; b++) { h ^= (byte)(k >> (8 * b)); h *= 1099511628211UL; }
                }
            }
            return h.ToString("X16");
        }

        // Tessellation. The game builds a material's permutation from TessellationType, DisplacementMapTexCoord and
        // DisplacementObjectScale, and for a displacement permutation dereferences DisplacementMap without a null check
        // (MeshVariationManager: ta->getResource(); HOTH_M2 crashed at game+0x15AAAF3 on a tessellated corridor floor whose map
        // the port had left null). So: the three permutation keys follow the donor surface the sections are drawn by (sections
        // drawn as triangle lists: no tessellation at all), the continuous settings stay the source's, and the map is the
        // source's own texture (the donor's only when the source's cannot be resolved -- reported).
        private static string ApplyTessellation(EbxAsset asset, object mat, int i, PortMaterial pm, Dictionary<string, EbxAssetEntry> texTarget, List<string> report)
        {
            string src;
            pm.Fields.TryGetValue("TessellationType", out src);
            bool srcTess = src != null && src != "ShaderTessellationType_None";
            PortGeo.TessInfo ti;
            PortGeo.LastTessellation.TryGetValue(i, out ti);
            if (ti == null || !ti.Patch)
            {
                PropertyPathWriter.SetValue(mat, "TessellationType", "ShaderTessellationType_None");
                SetNullRef(mat, "DisplacementMap");
                if (srcTess) report.Add("material " + i + ": source tessellation " + src + " NOT reproduced -- its sections are drawn by BFII surfaces compiled for triangle lists only");
                return null;
            }
            if (ti.List) report.Add("material " + i + ": WARNING has both tessellated and triangle-list sections; one of them has no permutation");
            object dmat = ti.DonorMaterial;
            if (dmat == null) return "material " + i + ": tessellated sections but the donor material could not be read";
            foreach (string f in new[] { "TessellationType", "DisplacementMapTexCoord", "DisplacementObjectScale" })
            {
                object dv = ReadMember(dmat, f);
                string sv;
                pm.Fields.TryGetValue(f, out sv);
                string dvs = dv == null ? null : Convert.ToString(dv, CultureInfo.InvariantCulture);
                if (dvs != null && !string.Equals(dvs, sv, StringComparison.OrdinalIgnoreCase))
                {
                    report.Add("material " + i + ": " + f + " " + (sv ?? "(unset)") + " -> " + dvs + " (the permutation of the donor surface, " + ti.DonorMesh + ")");
                    PropertyPathWriter.SetValue(mat, f, dvs);
                }
            }
            EbxAssetEntry te = null;
            if (pm.DisplacementMap != null && pm.DisplacementMap.Guid != null) texTarget.TryGetValue(pm.DisplacementMap.Guid, out te);
            if (te == null)
            {
                object dref = ReadMember(dmat, "DisplacementMap");
                object ext = dref != null ? ReadMember(dref, "External") : null;
                object fg = ext != null ? ReadMember(ext, "FileGuid") : null;
                if (fg is Guid && (Guid)fg != Guid.Empty) te = App.AssetManager.GetEbxEntry((Guid)fg);
                if (te == null) return "material " + i + ": tessellated, but neither the source's displacement map (" + (pm.DisplacementMap != null ? pm.DisplacementMap.Path ?? pm.DisplacementMap.Guid : "not in the package") + ") nor the donor's resolves";
                report.Add("material " + i + ": displacement map " + (pm.DisplacementMap == null ? "not in the package (exported before displacement maps were)" : (pm.DisplacementMap.Path ?? pm.DisplacementMap.Guid) + " unresolved") +
                           "; using the donor surface's " + te.Name);
                texTarget["displacement:" + te.Guid.ToString("D")] = te;
            }
            Guid texClass;
            try { texClass = InstanceGuidOf(App.AssetManager.GetEbx(te).RootObject); } catch { texClass = Guid.Empty; }
            EbxRefWriter.SetRefByPath(mat, "DisplacementMap", te.Guid, texClass);
            asset.AddDependency(te.Guid);
            report.Add("material " + i + ": tessellated (" + Convert.ToString(ReadMember(mat, "TessellationType"), CultureInfo.InvariantCulture) + "), displacement map " + te.Name);
            return null;
        }

        private static void SetNullRef(object obj, string field)
        {
            PropertyInfo pi = obj.GetType().GetProperty(field);
            if (pi != null && pi.CanWrite) pi.SetValue(obj, Activator.CreateInstance(pi.PropertyType));
        }

        private static Guid ExternalFileGuid(object pointerRef)
        {
            object ext = pointerRef != null ? ReadMember(pointerRef, "External") : null;
            object fg = ext != null ? ReadMember(ext, "FileGuid") : null;
            return fg is Guid ? (Guid)fg : Guid.Empty;
        }

        // ------------------------------------------------------------------ helpers

        private static IList ReadList(object value, string name) { return ReadMember(value, name) as IList; }

        private static object ReadMember(object value, string name)
        {
            if (value == null) return null;
            Type type = value.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(value);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(value) : null;
        }

        private static object ResolveInternal(object value)
        {
            if (value == null) return null;
            if (value.GetType().Name != "PointerRef") return value;
            return ReadMember(value, "Internal");
        }

        internal static Guid InstanceGuidOf(object obj)
        {
            if (obj == null) return Guid.Empty;
            try
            {
                MethodInfo get = obj.GetType().GetMethod("GetInstanceGuid");
                object guid = get != null ? get.Invoke(obj, null) : null;
                object exported = guid != null ? ReadMember(guid, "ExportedGuid") : null;
                return exported is Guid ? (Guid)exported : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }
    }
}
