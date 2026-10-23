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
using FrostyCLI.Core;
using FrostyCLI.Adapters.Mesh;

namespace FrostyCLI.Commands
{
    // Ports a static prop blueprint from one game to another through a package directory, because
    // the asset manager is bound to a single game per process:
    //
    //   frostycli --game swbf1 port export <blueprint> --out <dir>     (2015 side: FBX, DDS, physics bytes, manifest)
    //   frostycli --game swbf2 --project p port import <dir> --as <bp>  (BFII side: textures, mesh, materials, physics, blueprint)
    //
    // Import reuses whatever the target game already ships under the same file GUID (DICE carried the
    // Bespin object set over unchanged), clones templates for the rest, and reports what could not be
    // matched (shaders that do not exist in the target get --shader-fallback).
    public class PortCommand : ICommand
    {
        public string Name => "port";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli port <export|import> ..."); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "export": return ExecuteExport(Sub(args), logger, formatter);
                case "import": return ExecuteImport(Sub(args), logger, formatter);
                case "export-layer": return ExecuteExportLayer(Sub(args), logger, formatter);
                case "import-layer": return ExecuteImportLayer(Sub(args), logger, formatter);
                case "mesh-info": return ExecuteMeshInfo(Sub(args));
                case "import-group": return PortImportGroup.Execute(Sub(args), logger, formatter);
                case "regeo": return PortRegeo.Execute(Sub(args), logger, formatter);
                case "geo-plan": return PortRegeo.ExecutePlan(Sub(args), logger, formatter);
                case "prune-refs": return PortRegeo.ExecutePruneRefs(Sub(args), logger, formatter);
                case "repoint-refs": return PortRegeo.ExecuteRepointRefs(Sub(args), logger, formatter);
                case "repoint-map": return PortRegeo.ExecuteRepointMap(Sub(args), logger, formatter);
                case "mesh-facts": return PortRegeo.ExecuteMeshFacts(Sub(args), logger, formatter);
                case "repoint-member": return PortRegeo.ExecuteRepointMember(Sub(args), logger, formatter);
                case "check-members": return PortRegeo.ExecuteCheckMembers(Sub(args), logger, formatter);
                case "import-volumes": return PortImportGroup.ExecuteVolumes(Sub(args), logger, formatter);
                case "convert-physics": return ExecuteConvertPhysics(Sub(args), logger, formatter);
                case "havok-info": return ExecuteHavokInfo(Sub(args));
                default: Console.Error.WriteLine("port: unknown subcommand '" + args[0] + "'. Use export, import, export-layer, import-layer or mesh-info."); return 1;
            }
        }

        // port havok-info <physics res> [--from N] [--count M]: placement compound instances as stored.
        private static int ExecuteHavokInfo(string[] args)
        {
            string res = null; int from = 0, count = 10;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from" && i + 1 < args.Length) from = int.Parse(args[++i]);
                else if (args[i] == "--count" && i + 1 < args.Length) count = int.Parse(args[++i]);
                else if (!args[i].StartsWith("--")) res = args[i];
            }
            if (res == null) { Console.Error.WriteLine("port havok-info: missing <res>"); return 1; }
            ResAssetEntry re = App.AssetManager.GetResEntry(res);
            if (re == null) { Console.Error.WriteLine("port havok-info: res not found: " + res); return 1; }
            byte[] bytes;
            using (var st = App.AssetManager.GetRes(re)) using (var ms = new MemoryStream()) { st.CopyTo(ms); bytes = ms.ToArray(); }
            foreach (string l in FrostyCLI.Core.HavokPhysicsResource.DescribeInstances(bytes, from, count)) Console.WriteLine(l);
            return 0;
        }

        // port mesh-info <mesh>...: the MeshSet's LOD/chunk/section layout, for checking a clone
        // against a shipped mesh (which LODs stream from a chunk, whether that chunk exists, sizes).
        private static int ExecuteMeshInfo(string[] args)
        {
            if (args.Length == 0) { Console.Error.WriteLine("port mesh-info: missing <mesh>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("port mesh-info: AssetManager not initialized. Pass --game-path."); return 1; }
            int rc = 0;
            foreach (string name in args)
            {
                if (name.StartsWith("--")) continue;
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(name);
                if (e == null) { Console.Error.WriteLine("port mesh-info: mesh not found: " + name); rc = 1; continue; }
                List<string> lines;
                string err = FrostyCLI.Adapters.Mesh.MeshCloner.DescribeLods(e, out lines);
                Console.WriteLine("== " + e.Name);
                if (err != null) { Console.Error.WriteLine("port mesh-info: " + err); rc = 1; continue; }
                foreach (string l in lines) Console.WriteLine(l);
            }
            return rc;
        }

        // Parts a composite shows only after damage: the PartIndex of every non-initial health state of every
        // PartComponentData, minus the parts some component shows at full health (state 0). PartIndex 0xFFFFFFFF = none.
        private static HashSet<int> InitiallyHiddenParts(object entity)
        {
            var shown = new HashSet<int>(); var later = new HashSet<int>();
            IList comps = entity != null ? ReadList(entity, "Components") : null;
            if (comps == null) return later;
            foreach (object c in comps)
            {
                object pcd = ResolveInternal(c) ?? c;
                if (pcd == null || pcd.GetType().Name != "PartComponentData") continue;
                IList states = ReadList(pcd, "HealthStates");
                for (int k = 0; states != null && k < states.Count; k++)
                {
                    object hs = ResolveInternal(states[k]) ?? states[k];
                    object pi = hs != null ? ReadMember(hs, "PartIndex") : null;
                    if (pi == null) continue;
                    long part = Convert.ToInt64(pi, CultureInfo.InvariantCulture);
                    if (part < 0 || part >= 0xFFFF) continue;
                    if (k == 0) shown.Add((int)part); else later.Add((int)part);
                }
            }
            later.ExceptWith(shown);
            return later;
        }

        private static string[] Sub(string[] a) { var r = new string[a.Length - 1]; Array.Copy(a, 1, r, 0, r.Length); return r; }

        // ------------------------------------------------------------------ export

        public static int ExecuteExport(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string blueprint = null, outDir = null;
            bool asRigid = false, allParts = false;
            var mvdbs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (args[i] == "--mvdb" && i + 1 < args.Length) mvdbs.Add(args[++i]);
                else if (args[i] == "--as-rigid") asRigid = true;
                else if (args[i] == "--all-parts") allParts = true;
                else if (!args[i].StartsWith("--")) blueprint = args[i];
            }
            // --as-rigid: a composite mesh is written as plain geometry (no PART skeleton / skins), so
            // `port import` can put it into a rigid template. The parts keep their rest pose.
            FrostyCLI.Adapters.Mesh.HeadlessMeshAssetDefinition.ExportCompositeAsRigid = asRigid;
            if (blueprint == null) { Console.Error.WriteLine("port export: missing <blueprint>"); return 1; }
            if (outDir == null) { Console.Error.WriteLine("port export: missing --out <dir>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("port export: AssetManager not initialized. Pass --game-path."); return 1; }

            EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(blueprint);
            if (bpEntry == null) { Console.Error.WriteLine("port export: blueprint not found: " + blueprint); return 1; }
            EbxAsset bpAsset;
            try { bpAsset = App.AssetManager.GetEbx(bpEntry); }
            catch (Exception ex) { Console.Error.WriteLine("port export: blueprint load failed: " + Describe(ex)); return 1; }

            object entity = ResolveInternal(ReadMember(bpAsset.RootObject, "Object"));
            if (entity == null) { Console.Error.WriteLine("port export: blueprint has no Object entity"); return 1; }

            var manifest = new PortManifest
            {
                SourceGame = FrostyCLI.Core.Session.GameName ?? "",
                Blueprint = bpEntry.Name,
                BlueprintGuid = bpEntry.Guid.ToString("D"),
                EntityType = entity.GetType().Name
            };

            // Mesh reference.
            Guid meshGuid, meshClass;
            if (!TryExternal(ReadMember(entity, "Mesh"), out meshGuid, out meshClass))
            { Console.Error.WriteLine("port export: entity " + manifest.EntityType + " has no external Mesh reference"); return 1; }
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshGuid);
            if (meshEntry == null) { Console.Error.WriteLine("port export: mesh " + meshGuid + " not found"); return 1; }

            // Physics asset: first rigid body's Asset on a physics component, else the naming convention.
            EbxAssetEntry physEntry = null;
            IList components = ReadList(entity, "Components");
            if (components != null)
                foreach (object raw in components)
                {
                    object comp = ResolveInternal(raw);
                    if (comp == null || comp.GetType().Name != "StaticModelPhysicsComponentData") continue;
                    IList bodies = ReadList(comp, "PhysicsBodies");
                    if (bodies == null) continue;
                    foreach (object body in bodies)
                    {
                        Guid pg, pc;
                        if (TryExternal(ReadMember(ResolveInternal(body) ?? body, "Asset"), out pg, out pc)) { physEntry = App.AssetManager.GetEbxEntry(pg); if (physEntry != null) break; }
                    }
                    if (physEntry != null) break;
                }
            if (physEntry == null) physEntry = App.AssetManager.GetEbxEntry(bpEntry.Name + "_Physics_Win32");

            // Part bounding boxes (copied onto the target blueprint).
            IList boxes = ReadList(entity, "PartBoundingBoxes");
            if (boxes != null)
                foreach (object box in boxes)
                {
                    float[] mn = ReadVec3(ReadMember(box, "Min")), mx = ReadVec3(ReadMember(box, "Max"));
                    if (mn != null && mx != null) manifest.PartBoundingBoxes.Add(new PortBox { Min = mn, Max = mx });
                }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "port export {0} (mesh {1}, physics {2}) to {3}", bpEntry.Name, meshEntry.Name, physEntry != null ? physEntry.Name : "none", outDir);
                return 0;
            }
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(Path.Combine(outDir, "textures"));

            // Blueprint dump for reference.
            try { File.WriteAllText(Path.Combine(outDir, "blueprint.json"), EbxDumper.Dump(bpAsset, true, true).ToString()); manifest.BlueprintFile = "blueprint.json"; }
            catch (Exception ex) { manifest.Notes.Add("blueprint dump failed: " + Describe(ex)); }

            // Mesh: FBX + section layout + materials.
            var mesh = new PortMesh { Path = meshEntry.Name, Guid = meshEntry.Guid.ToString("D"), Type = meshEntry.Type, File = "mesh.fbx" };
            // Every LOD and section as the source stores them (MESHGEO1): the geometry port reads this instead of the FBX. Read
            // before the rigid FBX presentation below edits the loaded MeshSet. A destructible composite keeps every health
            // state's geometry as a part and the game shows a part only while its state is active, so the dump carries the
            // intact state only -- per primitive, depth and shadow sections included (--all-parts keeps every part).
            {
                FrostyCLI.Adapters.Mesh.GeoMesh geoDump;
                string gerr = FrostyCLI.Adapters.Mesh.GeoMesh.Read(meshEntry, out geoDump);
                if (gerr == null && !allParts && string.Equals(meshEntry.Type, "CompositeMeshAsset", StringComparison.Ordinal))
                {
                    HashSet<int> hiddenParts = InitiallyHiddenParts(entity);
                    if (hiddenParts.Count > 0)
                    {
                        List<string> dn = geoDump.DropParts(hiddenParts);
                        manifest.Notes.Add("geometry dump: part(s) " + string.Join(",", hiddenParts.OrderBy(x => x)) + " belong to later health states and were removed" +
                                           (dn.Count > 0 ? ": " + string.Join("; ", dn) : " (no geometry used them)"));
                    }
                }
                if (gerr == null) { geoDump.Save(Path.Combine(outDir, "mesh.meshgeo")); mesh.GeoFile = "mesh.meshgeo"; }
                else manifest.Notes.Add("geometry dump failed: " + gerr);
            }
            if (asRigid && !allParts && string.Equals(meshEntry.Type, "CompositeMeshAsset", StringComparison.Ordinal))
            {
                // A destructible composite keeps each health state's geometry as its own part and shows a state's part
                // only while that state is active (BoxMedium_02: crate + "ruin"). As rigid geometry only the parts shown
                // at full health may stay -- the state-0 part of every PartComponentData; --all-parts keeps them all.
                HashSet<int> hiddenParts = InitiallyHiddenParts(entity);
                if (hiddenParts.Count > 0)
                {
                    int hiddenSections; List<string> mixed;
                    string herr = FrostyCLI.Adapters.Mesh.MeshCloner.HideCompositeParts(meshEntry, hiddenParts, out hiddenSections, out mixed);
                    if (herr != null) { Console.Error.WriteLine("port export: " + herr); return 1; }
                    manifest.Notes.Add("composite part(s) " + string.Join(",", hiddenParts) + " belong to later health states: " + hiddenSections +
                                       " section(s) left out of the rigid geometry" + (mixed.Count > 0 ? "; mixed sections kept: " + string.Join(", ", mixed) : ""));
                    if (mixed.Count > 0) logger.LogWarning("port export: sections mixing shown and hidden parts kept: " + string.Join(", ", mixed));
                }
            }
            if (asRigid && string.Equals(meshEntry.Type, "CompositeMeshAsset", StringComparison.Ordinal))
            {
                string rerr = FrostyCLI.Adapters.Mesh.MeshCloner.PresentAsRigid(meshEntry);
                if (rerr != null) { Console.Error.WriteLine("port export: " + rerr); return 1; }
            }
            string err = ExportWithHandler(meshEntry, Path.Combine(outDir, mesh.File), "fbx");
            if (err != null) { Console.Error.WriteLine("port export: mesh export failed: " + err); return 1; }
            if (asRigid && string.Equals(meshEntry.Type, "CompositeMeshAsset", StringComparison.Ordinal))
                manifest.Notes.Add("composite mesh exported as rigid geometry (--as-rigid): no PART skeleton or skins in the FBX");
            int lodCount; List<List<string>> names; List<List<int>> matIds;
            err = FrostyCLI.Adapters.Mesh.MeshCloner.DescribeSections(meshEntry, out lodCount, out names, out matIds);
            if (err != null) { Console.Error.WriteLine("port export: " + err); return 1; }
            mesh.LodCount = lodCount; mesh.SectionNames = names; mesh.SectionMaterials = matIds;

            EbxAsset meshAsset;
            try { meshAsset = App.AssetManager.GetEbx(meshEntry); }
            catch (Exception ex) { Console.Error.WriteLine("port export: mesh load failed: " + Describe(ex)); return 1; }
            IList materials = ReadList(meshAsset.RootObject, "Materials");
            var materialClassGuids = new List<Guid>();
            if (materials != null)
                for (int i = 0; i < materials.Count; i++)
                {
                    object mat = ResolveInternal(materials[i]) ?? materials[i];
                    var pm = new PortMaterial { Index = i };
                    Guid cg = InstanceGuidOf(mat);
                    pm.ClassGuid = cg.ToString("D");
                    materialClassGuids.Add(cg);
                    object shader = ReadMember(mat, "Shader");
                    Guid sg, sc;
                    if (shader != null && TryExternal(ReadMember(shader, "Shader"), out sg, out sc))
                    {
                        pm.ShaderGuid = sg.ToString("D");
                        EbxAssetEntry se = App.AssetManager.GetEbxEntry(sg);
                        pm.ShaderPath = se != null ? se.Name : null;
                    }
                    // Mesh-level texture overrides (BFII keeps them here; 2015 meshes usually leave this empty).
                    IList tp = shader != null ? ReadList(shader, "TextureParameters") : null;
                    if (tp != null) foreach (object p in tp) AddTexture(pm, p);
                    ExportMaterialParams(pm, mat, shader);
                    // a tessellated material's displacement map (the game dereferences it for every displacement permutation)
                    Guid dg, dc;
                    if (TryExternal(ReadMember(mat, "DisplacementMap"), out dg, out dc)) pm.DisplacementMap = new PortTexture { Param = "DisplacementMap", Guid = dg.ToString("D") };
                    mesh.Materials.Add(pm);
                }

            // The mesh root's own settings (LOD group and distances, cull scale, Enlighten type ...), carried to the port as
            // they are where the target type has the same field.
            FlattenFields(meshAsset.RootObject, "", mesh.Fields, 0, MeshFieldsSkipped);
            // The occluder mesh (software occlusion geometry) travels verbatim: OccluderMeshEnable is one of the fields
            // above, and a port that enables it without the resource crashes on creation.
            {
                string oerr = ExportOccluder(meshAsset.RootObject, outDir, mesh);
                if (oerr != null) manifest.Notes.Add("occluder mesh not exported: " + oerr);
            }

            // Texture parameters as the cooked MeshVariationDatabase resolved them. The --mvdb databases are searched
            // first; a mesh none of them registers falls back to every database in the game. A prop placed only by a
            // sub-level keeps its record in THAT sub-level's database: 2015's IonCannon_01 / IonCannonTip_01 are
            // registered by StreamedAssets_BattleBeyond_IonCannon_Default, not by the StreamedLevel database the Hoth
            // export named, and exporting them without their textures made the port draw the ion cannon untextured
            // (a white blob on the base roof).
            string mvdbSource = ScanMvdbs(mvdbs.Count > 0 ? (IEnumerable<string>)mvdbs : AllMvdbNames(), mvdbs.Count > 0,
                                          meshEntry.Guid, materialClassGuids, mesh, manifest);
            if (mvdbSource == null && mvdbs.Count > 0)
            {
                var asked = new HashSet<string>(mvdbs, StringComparer.OrdinalIgnoreCase);
                mvdbSource = ScanMvdbs(AllMvdbNames().Where(n => !asked.Contains(n)), false, meshEntry.Guid, materialClassGuids, mesh, manifest);
                if (mvdbSource != null) manifest.Notes.Add("the --mvdb database(s) do not register the mesh; fell back to " + mvdbSource);
            }
            if (mvdbSource != null) manifest.Notes.Add("texture parameters from " + mvdbSource);
            else manifest.Notes.Add("no MeshVariationDatabase entry found for the mesh; texture parameters come from the mesh only");
            // ...and the textures the shader graph binds itself for parameters neither of those sets. A 2015 graph written
            // for one prop bakes its art in (SS_MetalPlate_Floor_01's BaseColor); without it the port has no texture and a
            // fallback preset draws the prop black.
            AddGraphDefaultTextures(mesh, manifest, logger);

            // Export each distinct texture once.
            var exported = new Dictionary<Guid, string>();
            foreach (PortMaterial pm in mesh.Materials)
                foreach (PortTexture t in TexturesOf(pm))
                {
                    Guid g = Guid.Parse(t.Guid);
                    string file;
                    if (!exported.TryGetValue(g, out file))
                    {
                        EbxAssetEntry te = App.AssetManager.GetEbxEntry(g);
                        if (te == null) { manifest.Notes.Add("texture " + g + " (" + t.Param + ") not found"); continue; }
                        t.Path = te.Name;
                        string baseName = Path.GetFileName(te.Name);
                        file = "textures/" + baseName + ".dds";
                        string texErr = ExportWithHandler(te, Path.Combine(outDir, file), "dds");
                        if (texErr != null) { manifest.Notes.Add("texture export failed for " + te.Name + ": " + texErr); file = null; }
                        exported[g] = file;
                    }
                    t.File = file;
                    if (t.Path == null) { EbxAssetEntry te = App.AssetManager.GetEbxEntry(g); if (te != null) t.Path = te.Name; }
                }
            manifest.Mesh = mesh;

            // Physics resource bytes.
            if (physEntry != null)
            {
                try
                {
                    ulong rid = (ulong)((dynamic)App.AssetManager.GetEbx(physEntry).RootObject).Resource;
                    ResAssetEntry re = rid != 0 ? App.AssetManager.GetResEntry(rid) : null;
                    if (re == null) manifest.Notes.Add("physics asset " + physEntry.Name + " has no resource");
                    else
                    {
                        byte[] bytes; string rerr;
                        if (!LevelCommand.TryReadResBytes(re, out bytes, out rerr)) manifest.Notes.Add("physics res read failed: " + rerr);
                        else
                        {
                            File.WriteAllBytes(Path.Combine(outDir, "physics.bin"), bytes);
                            manifest.Physics = new PortPhysics
                            {
                                AssetPath = physEntry.Name, Guid = physEntry.Guid.ToString("D"), ResName = re.Name, File = "physics.bin",
                                ResType = re.ResType, MetaHex = re.ResMeta != null ? BitConverter.ToString(re.ResMeta).Replace("-", "") : ""
                            };
                        }
                    }
                }
                catch (Exception ex) { manifest.Notes.Add("physics export failed: " + Describe(ex)); }
            }
            else manifest.Notes.Add("blueprint has no physics asset");

            manifest.Save(outDir);
            int texCount = exported.Count;
            formatter.WriteResult("OK", "port export", new Dictionary<string, string>
            {
                { "blueprint", bpEntry.Name }, { "mesh", meshEntry.Name }, { "materials", mesh.Materials.Count.ToString(CultureInfo.InvariantCulture) },
                { "textures", texCount.ToString(CultureInfo.InvariantCulture) }, { "physics", manifest.Physics != null ? manifest.Physics.AssetPath : "" },
                { "out", outDir }
            });
            foreach (string n in manifest.Notes) logger.LogWarning("port export: {0}", n);
            return 0;
        }

        private static IEnumerable<string> AllMvdbNames()
        {
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                if (string.Equals(e.Type, "MeshVariationDatabase", StringComparison.Ordinal)) yield return e.Name;
        }

        /// <summary>The first database in <paramref name="dbs"/> with an entry for the mesh: its texture parameters are
        /// added to the matching materials (by material class guid) and its name returned; null when none has one.</summary>
        private static string ScanMvdbs(IEnumerable<string> dbs, bool noteMissing, Guid meshGuid, List<Guid> materialClassGuids,
                                        PortMesh mesh, PortManifest manifest)
        {
            foreach (string db in dbs)
            {
                EbxAssetEntry dbEntry = App.AssetManager.GetEbxEntry(db);
                if (dbEntry == null) { if (noteMissing) manifest.Notes.Add("mvdb not found: " + db); continue; }
                EbxAsset dbAsset;
                try { dbAsset = App.AssetManager.GetEbx(dbEntry); } catch { continue; }
                IList entries = MvdbCommand.FindEntries(dbAsset.RootObject);
                if (entries == null) continue;
                foreach (object entry in entries)
                {
                    Guid eg, ec;
                    if (!TryExternal(ReadMember(entry, "Mesh"), out eg, out ec) || eg != meshGuid) continue;
                    IList recs = ReadList(entry, "Materials");
                    if (recs != null)
                        foreach (object rec in recs)
                        {
                            Guid mg, mc;
                            int idx = -1;
                            if (TryExternal(ReadMember(rec, "Material"), out mg, out mc)) idx = materialClassGuids.IndexOf(mc);
                            if (idx < 0) continue;
                            IList tp = ReadList(rec, "TextureParameters");
                            if (tp == null) continue;
                            foreach (object p in tp) AddTexture(mesh.Materials[idx], p);
                        }
                    return dbEntry.Name;
                }
            }
            return null;
        }

        private static bool graphDbTried, graphDbLoaded;

        /// <summary>Adds, per material, the shader graph's default texture for every external texture parameter the
        /// material and its database record leave unset (<see cref="ShaderDataPlugin.GraphTextureDefaults"/>), marked
        /// Source = "shader-default". The shader databases load once per process.</summary>
        private static void AddGraphDefaultTextures(PortMesh mesh, PortManifest manifest, ExportLogger logger)
        {
            if (!graphDbTried)
            {
                graphDbTried = true;
                try { graphDbLoaded = ShaderDataPlugin.ShaderDb.LoadAll(logger); }
                catch (Exception ex) { manifest.Notes.Add("shader databases not loaded, graph default textures skipped: " + Describe(ex)); }
            }
            if (!graphDbLoaded) return;
            int added = 0, constants = 0;
            foreach (PortMaterial pm in mesh.Materials)
            {
                if (pm.ShaderPath == null) continue;
                foreach (KeyValuePair<string, string> d in ShaderDataPlugin.GraphTextureDefaults.Of(pm.ShaderPath))
                {
                    if (pm.Textures.Any(t => t.Param == d.Key)) continue;
                    EbxAssetEntry te = App.AssetManager.GetEbxEntry(d.Value);
                    if (te == null) { manifest.Notes.Add("material " + pm.Index + ": graph default " + d.Key + " = " + d.Value + " is not an asset"); continue; }
                    pm.Textures.Add(new PortTexture { Param = d.Key, Guid = te.Guid.ToString("D"), Source = "shader-default" });
                    added++;
                }
                // The graph's own constant textures (its folder's art, no parameter at all: SS_LightGround_01_EmissiveGlow
                // bakes T_LightGround_01_CS / _NW). Param = the packing family; `port import` binds them only on a fallback.
                foreach (string c in ShaderDataPlugin.GraphTextureDefaults.ConstantsOf(pm.ShaderPath))
                {
                    EbxAssetEntry te = App.AssetManager.GetEbxEntry(c);
                    if (te == null) { manifest.Notes.Add("material " + pm.Index + ": graph constant " + c + " is not an asset"); continue; }
                    string guid = te.Guid.ToString("D");
                    if (pm.Textures.Any(t => string.Equals(t.Guid, guid, StringComparison.OrdinalIgnoreCase))) continue;
                    pm.Textures.Add(new PortTexture { Param = ShaderDataPlugin.GraphTextureDefaults.Family(c), Guid = guid, Source = "shader-constant" });
                    constants++;
                }
            }
            if (added > 0) manifest.Notes.Add(added + " texture parameter(s) from the shader graph defaults");
            if (constants > 0) manifest.Notes.Add(constants + " constant texture(s) of the shader graph's own (bound only where the graph falls back)");
        }

        private static void AddTexture(PortMaterial pm, object param)
        {
            Guid g, c;
            if (!TryExternal(ReadMember(param, "Value"), out g, out c)) return;
            string name = Convert.ToString(ReadMember(param, "ParameterName"));
            foreach (PortTexture existing in pm.Textures) if (existing.Param == name) return;
            pm.Textures.Add(new PortTexture { Param = name, Guid = g.ToString("D") });
        }

        /// <summary>
        /// The source blueprint's part wiring onto the new blueprint when the template's PartComponentData are shaped like
        /// the source's (same count, same health-state count each): PhysicsPartInfos (component, health state) as the source
        /// lists them, and each health state's PartIndex / Health / PhysicsEnabled / CanSupportOtherParts /
        /// CopyDamageToBanger and each component's IsSupported / IsFragile / IsNetworkable / IsWindow. The generic rule above
        /// (every physics part on component 0) is right for a one-part template only; on a composite it leaves the ruin
        /// parts of later health states without a state that hides them. Health-state Objects (debris, effects) stay the
        /// template's; differences are reported.
        /// </summary>
        internal static void ApplySourcePartWiring(object entity, string dir, PortManifest m, int physParts, List<string> report, EbxAsset asset = null)
        {
            if (m.BlueprintFile == null) return;
            string file = Path.Combine(dir, m.BlueprintFile);
            if (!File.Exists(file)) return;
            Newtonsoft.Json.Linq.JObject src;
            try { src = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file)); } catch { return; }
            var so = src["Object"] as Newtonsoft.Json.Linq.JObject;
            if (so == null) return;
            var scomps = new List<Newtonsoft.Json.Linq.JObject>();
            var sc = so["Components"] as Newtonsoft.Json.Linq.JArray;
            if (sc != null) foreach (var c in sc) if (c is Newtonsoft.Json.Linq.JObject jo && (string)jo["__type"] == "PartComponentData") scomps.Add(jo);
            var tcomps = new List<object>();
            IList comps = ReadList(entity, "Components");
            if (comps != null) foreach (object c in comps) { object pc = ResolveInternal(c) ?? c; if (pc != null && pc.GetType().Name == "PartComponentData") tcomps.Add(pc); }
            if (scomps.Count == 0) return;
            bool same = tcomps.Count == scomps.Count;
            int grownStates = 0;
            for (int i = 0; same && i < tcomps.Count; i++)
            {
                IList ths = ReadList(tcomps[i], "HealthStates");
                var shs = scomps[i]["HealthStates"] as Newtonsoft.Json.Linq.JArray;
                // a template component with fewer health states than the source's (BFII's one-state Box_M_02 for 2015's
                // intact/ruin BoxMedium_02): the last state is deep-copied to make up the count; its values come from the
                // source below, its spawned objects stay the template's (reported)
                if (ths != null && shs != null && ths.Count > 0 && ths.Count < shs.Count && ths.Count == 1)
                {
                    object last = ResolveInternal(ths[ths.Count - 1]) ?? ths[ths.Count - 1];
                    if (last != null && !(ths[ths.Count - 1] is PointerRef))
                        while (ths.Count < shs.Count) { ths.Add(LevelCommand.DeepCopyObject(last)); grownStates++; }
                    else if (last != null && asset != null)
                        // an internal HealthStateData object: a copy with its own instance guid, registered in the partition
                        while (ths.Count < shs.Count)
                        {
                            object copy = LevelCommand.DeepCopyObject(last);
                            MethodInfo setGuid = copy.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                            if (setGuid == null) break;
                            setGuid.Invoke(copy, new object[] { new AssetClassGuid(Guid.NewGuid(), LevelCommand.NextInternalId(asset)) });
                            asset.AddObject(copy, false);
                            InProcessObjects.Mark(copy);
                            ths.Add(new PointerRef(copy));
                            grownStates++;
                        }
                }
                same = ths != null && shs != null && ths.Count == shs.Count;
            }
            if (grownStates > 0) report.Add(grownStates + " health state(s) added to the template's part component(s) to match the source's");
            if (!same)
            {
                if (tcomps.Count > 1 || scomps.Count > 1)
                    report.Add("the template's " + tcomps.Count + " part component(s) are not shaped like the source's " + scomps.Count +
                               ": part wiring stays generic (pass --blueprint-template <the same object's blueprint> for a composite)");
                return;
            }
            var sppi = so["PhysicsPartInfos"] as Newtonsoft.Json.Linq.JArray;
            IList infos = ReadList(entity, "PhysicsPartInfos");
            if (sppi != null && infos != null && sppi.Count == physParts && physParts > 0)
            {
                Type infoType = PropertyPathWriter.ListElementType(infos);
                infos.Clear();
                foreach (var p in sppi)
                {
                    object info = Activator.CreateInstance(infoType);
                    PropertyPathWriter.SetValue(info, "PartComponentIndex", ((long)p["PartComponentIndex"]).ToString(CultureInfo.InvariantCulture));
                    PropertyPathWriter.SetValue(info, "HealthStateIndex", ((long)p["HealthStateIndex"]).ToString(CultureInfo.InvariantCulture));
                    infos.Add(info);
                }
                report.Add("PhysicsPartInfos: the source's " + sppi.Count + " (component, health state) pair(s)");
            }
            else if (sppi != null) report.Add("the source lists " + sppi.Count + " PhysicsPartInfo(s) but the physics has " + physParts + " part(s): kept the generic wiring");
            int values = 0, objectDiffs = 0;
            var diffs = new List<string>();
            for (int i = 0; i < tcomps.Count; i++)
            {
                foreach (string f in new[] { "IsSupported", "IsFragile", "IsNetworkable", "IsWindow" })
                    values += CopyScalar(tcomps[i], f, scomps[i][f]);
                IList ths = ReadList(tcomps[i], "HealthStates");
                var shs = (Newtonsoft.Json.Linq.JArray)scomps[i]["HealthStates"];
                for (int h = 0; h < ths.Count; h++)
                {
                    object hs = ResolveInternal(ths[h]) ?? ths[h];
                    var sh = shs[h] as Newtonsoft.Json.Linq.JObject;
                    if (hs == null || sh == null) continue;
                    foreach (string f in new[] { "PartIndex", "Health", "PhysicsEnabled", "CanSupportOtherParts", "CopyDamageToBanger" })
                        values += CopyScalar(hs, f, sh[f]);
                    IList tobj = ReadList(hs, "Objects");
                    var sobj = sh["Objects"] as Newtonsoft.Json.Linq.JArray;
                    int tn = tobj != null ? tobj.Count : 0, sn = sobj != null ? sobj.Count : 0;
                    if (tn != sn) { objectDiffs++; if (diffs.Count < 4) diffs.Add("component " + i + " health state " + h + ": " + sn + " object(s) in the source, " + tn + " in the template"); }
                }
            }
            if (values > 0) report.Add(values + " part component / health state value(s) set to the source's");
            if (objectDiffs > 0) report.Add(objectDiffs + " health state(s) spawn other objects than the source (" + string.Join("; ", diffs) + ")");
        }

        private static int CopyScalar(object target, string field, Newtonsoft.Json.Linq.JToken value)
        {
            if (value == null || value.Type == Newtonsoft.Json.Linq.JTokenType.Null || value.Type == Newtonsoft.Json.Linq.JTokenType.Object || value.Type == Newtonsoft.Json.Linq.JTokenType.Array) return 0;
            object cur = ReadMember(target, field);
            if (cur == null) return 0;
            string want = value.Type == Newtonsoft.Json.Linq.JTokenType.Float ? ((double)value).ToString("R", CultureInfo.InvariantCulture)
                        : value.Type == Newtonsoft.Json.Linq.JTokenType.Boolean ? ((bool)value ? "true" : "false")
                        : value.ToString();
            string have = cur is float fl ? ((double)fl).ToString("R", CultureInfo.InvariantCulture) : cur is bool bo ? (bo ? "true" : "false") : Convert.ToString(cur, CultureInfo.InvariantCulture);
            if (cur is float cf && float.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out float wf) && cf == wf) return 0;
            if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return 0;
            try { PropertyPathWriter.SetValue(target, field, want); return 1; } catch { return 0; }
        }

        // The source mesh's OccluderMeshResource into the package as occluder.bin (verbatim; same res type and layout in
        // both games). Returns null when there is nothing to carry or it was written.
        private static string ExportOccluder(object meshRoot, string outDir, PortMesh mesh)
        {
            ulong rid;
            try { rid = (ulong)((dynamic)meshRoot).OccluderMeshResource; }
            catch { return null; }   // a mesh type without an occluder field
            if (rid == 0) return null;
            ResAssetEntry re = App.AssetManager.GetResEntry(rid);
            if (re == null) return "resource 0x" + rid.ToString("X16") + " is not in the catalog";
            byte[] bytes;
            using (var st = App.AssetManager.GetRes(re)) using (var ms = new MemoryStream()) { st.CopyTo(ms); bytes = ms.ToArray(); }
            string herr = OccluderMeshResource.Check(bytes);
            if (herr != null) return re.Name + ": " + herr;
            File.WriteAllBytes(Path.Combine(outDir, "occluder.bin"), bytes);
            mesh.OccluderFile = "occluder.bin";
            mesh.OccluderResMeta = re.ResMeta != null ? BitConverter.ToString(re.ResMeta).Replace("-", "") : null;
            mesh.OccluderResType = re.ResType;
            return null;
        }

        /// <summary>
        /// After the source's mesh fields are on the new mesh: the package's occluder mesh becomes the mesh's own resource
        /// (&lt;mesh&gt;_occludermesh, linked to the mesh so `bundle add --with-resources` ships it). A package without one
        /// (exported before occluders travelled, or a source without an occluder) leaves OccluderMeshEnable false: enabled
        /// with no resource, the mesh faults the moment an entity or group member using it is created (game+0x6E6ED6A,
        /// MISS_M4b/M4c: the LightGround_01 and ArcticBase_RockWall_Large_07 ports).
        /// </summary>
        internal static string ApplyOccluder(EbxAssetEntry newMesh, PortManifest m, string dir, List<string> report, ExportLogger logger)
        {
            EbxAsset asset = App.AssetManager.GetEbx(newMesh);
            dynamic root = asset.RootObject;
            bool enable; ulong rid;
            try { enable = (bool)root.OccluderMeshEnable; rid = (ulong)root.OccluderMeshResource; }
            catch { return null; }
            string file = m.Mesh != null && m.Mesh.OccluderFile != null ? Path.Combine(dir, m.Mesh.OccluderFile) : null;
            if (file != null && File.Exists(file))
            {
                byte[] bytes = File.ReadAllBytes(file);
                string herr = OccluderMeshResource.Check(bytes);
                if (herr != null) return "occluder mesh " + file + ": " + herr;
                string name = newMesh.Name.ToLowerInvariant() + "_occludermesh";
                byte[] meta = OccluderMeshResource.ParseHex(m.Mesh.OccluderResMeta) ?? new byte[16];
                uint type = m.Mesh.OccluderResType != 0 ? m.Mesh.OccluderResType : OccluderMeshResource.ResType;
                if (App.AssetManager.GetResEntry(name) == null) App.AssetManager.AddRes(name, (FrostySdk.Managers.ResourceType)type, meta, bytes);
                else App.AssetManager.ModifyRes(name, bytes, meta);
                ResAssetEntry re = App.AssetManager.GetResEntry(name);
                if (re == null) return "AddRes did not create " + name;
                bool sourceEnable = true;
                string sv;
                if (m.Mesh.Fields != null && m.Mesh.Fields.TryGetValue("OccluderMeshEnable", out sv)) bool.TryParse(sv, out sourceEnable);
                root.OccluderMeshResource = re.ResRid;
                root.OccluderMeshEnable = sourceEnable;
                try { newMesh.LinkAsset(re); } catch { }
                if (!EbxWriteCommand.CallModifyEbx(newMesh.Name, asset, logger)) return "could not save the occluder reference";
                report.Add("occluder mesh carried from the source (" + OccluderMeshResource.Describe(bytes) + "), OccluderMeshEnable " + sourceEnable);
                return null;
            }
            if (enable && rid == 0)
            {
                root.OccluderMeshEnable = false;
                if (!EbxWriteCommand.CallModifyEbx(newMesh.Name, asset, logger)) return "could not clear OccluderMeshEnable";
                report.Add("the source enables an occluder mesh this package does not carry: OccluderMeshEnable cleared " +
                           "(enabled without a resource crashes on creation, game+0x6E6ED6A); re-export the package to carry it");
            }
            return null;
        }

        /// <summary>True when every bundle that ships the asset belongs to the campaign (win32/levels/sp/..., win32/a3/...).</summary>
        internal static bool CampaignOnly(AssetEntry e)
        {
            if (e == null || e.Bundles == null || e.Bundles.Count == 0) return false;
            foreach (int b in e.Bundles)
            {
                BundleEntry be = App.AssetManager.GetBundleEntry(b);
                string n = be != null ? be.Name.ToLowerInvariant() : "";
                if (!(n.StartsWith("win32/levels/sp/") || n.StartsWith("win32/a3/") || n.StartsWith("win32/gameplay/bundles/sp/"))) return false;
            }
            return true;
        }

        // the textures a material names: its texture parameters, then its displacement map
        internal static IEnumerable<PortTexture> TexturesOf(PortMaterial pm)
        {
            foreach (PortTexture t in pm.Textures) yield return t;
            if (pm.DisplacementMap != null) yield return pm.DisplacementMap;
        }

        internal static readonly HashSet<string> MeshFieldsSkipped = new HashSet<string>(StringComparer.Ordinal)
        { "Name", "NameHash", "MeshSetResource", "OccluderMeshResource", "Materials", "ComputeGraph", "ProceduralAnimation" };
        internal static readonly HashSet<string> MaterialFieldsSkipped = new HashSet<string>(StringComparer.Ordinal)
        { "Shader", "ShaderInstance", "DisplacementMap", "TextureSpaceReferenceTexture", "TextureSpaceAnchorDistanceTexture", "TextureSpacePenetrationDistanceTexture" };

        // A material's shader parameters (bools, vectors, conditionals) and its own scalar fields, as the source stores them.
        private static void ExportMaterialParams(PortMaterial pm, object mat, object shader)
        {
            pm.ParamsExported = true;
            IList bools = shader != null ? ReadList(shader, "BoolParameters") : null;
            if (bools != null)
                foreach (object b in bools)
                    pm.Bools.Add(new PortBool { Param = Convert.ToString(ReadMember(b, "ParameterName")), Value = Convert.ToBoolean(ReadMember(b, "Value")) });
            IList vecs = shader != null ? ReadList(shader, "VectorParameters") : null;
            if (vecs != null)
                foreach (object v in vecs)
                {
                    object val = ReadMember(v, "Value");
                    pm.Vectors.Add(new PortVector
                    {
                        Param = Convert.ToString(ReadMember(v, "ParameterName")),
                        Type = Convert.ToString(ReadMember(v, "ParameterType")),
                        Value = new[] { Convert.ToSingle(ReadMember(val, "x")), Convert.ToSingle(ReadMember(val, "y")), Convert.ToSingle(ReadMember(val, "z")), Convert.ToSingle(ReadMember(val, "w")) }
                    });
                }
            IList conds = shader != null ? ReadList(shader, "ConditionalParameters") : null;
            if (conds != null)
                foreach (object c in conds)
                {
                    Guid g, cg;
                    var pc = new PortConditional { Value = Convert.ToString(ReadMember(c, "Value")) };
                    if (TryExternal(ReadMember(c, "ConditionalAsset"), out g, out cg))
                    {
                        pc.AssetGuid = g.ToString("D");
                        EbxAssetEntry ce = App.AssetManager.GetEbxEntry(g);
                        pc.AssetPath = ce != null ? ce.Name : null;
                    }
                    pm.Conditionals.Add(pc);
                }
            FlattenFields(mat, "", pm.Fields, 0, MaterialFieldsSkipped);
        }

        // Scalar fields of an EBX object as strings: primitives, enums, CStrings, nested structs (dotted names, two levels) and
        // external references (as asset paths). Lists and internal references are left out.
        internal static void FlattenFields(object o, string prefix, Dictionary<string, string> into, int depth, HashSet<string> skip)
        {
            if (o == null || depth > 2) return;
            foreach (PropertyInfo pi in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                if (depth == 0 && skip != null && skip.Contains(pi.Name)) continue;
                object v;
                try { v = pi.GetValue(o); } catch { continue; }
                string key = prefix + pi.Name;
                if (v == null) continue;
                Type t = v.GetType();
                if (t.IsPrimitive || t.IsEnum || v is string || t.Name == "CString")
                    into[key] = Convert.ToString(v, CultureInfo.InvariantCulture);
                else if (t.Name == "PointerRef")
                {
                    Guid g, cg;
                    if (TryExternal(v, out g, out cg)) { EbxAssetEntry e = App.AssetManager.GetEbxEntry(g); if (e != null) into[key] = "@" + e.Name; }
                }
                else if (v is IList) continue;
                else if (t.Namespace != null && t.Namespace.StartsWith("FrostySdk.Ebx", StringComparison.Ordinal) && t.GetMethod("GetInstanceGuid") == null)
                    FlattenFields(v, key + ".", into, depth + 1, null);
            }
        }

        // Sets the flattened fields onto a target object where it has the same path; references resolve by asset path.
        // Returns the names set; fields the target lacks are reported in <missing>.
        internal static List<string> ApplyFields(object target, Dictionary<string, string> fields, List<string> missing)
        {
            var set = new List<string>();
            if (fields == null) return set;
            foreach (KeyValuePair<string, string> kv in fields)
            {
                try
                {
                    if (kv.Value.StartsWith("@"))
                    {
                        EbxAssetEntry e = App.AssetManager.GetEbxEntry(kv.Value.Substring(1));
                        if (e == null) { missing.Add(kv.Key + " (" + kv.Value.Substring(1) + " not in this game)"); continue; }
                        Guid cls = InstanceGuidOf(App.AssetManager.GetEbx(e).RootObject);
                        EbxRefWriter.SetRefByPath(target, kv.Key, e.Guid, cls);
                    }
                    else PropertyPathWriter.SetValue(target, kv.Key, kv.Value);
                    set.Add(kv.Key);
                }
                catch (Exception ex) { missing.Add(kv.Key + " (" + Describe(ex) + ")"); }
            }
            return set;
        }

        private static string ExportWithHandler(EbxAssetEntry entry, string path, string format)
        {
            var handler = FrostyCLI.Core.Session.Plugins.GetHandler(entry);
            if (handler == null) return "no export handler for type " + (entry.Type ?? "null");
            try { return handler.Export(entry, path, format) ? null : "handler returned false"; }
            catch (Exception ex) { return Describe(ex); }
        }

        // ------------------------------------------------------------------ import

        // A failed import used to leave what it had already made (the mesh clone, textures, physics) in the project, so a
        // retry with other options -- e.g. --allow-foreign-donors after "geometry port failed: no donor section" -- died on
        // "destination already exists" (Carbonite rebuild, 2026-09-27: 9 prison-room pieces). Now every asset the import
        // created is reverted, newest first, when it fails.
        public static int ExecuteImport(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var created = new List<string>();
            int rc;
            try { rc = ExecuteImportCore(args, logger, formatter, created); }
            catch { RollBackImport(created, logger, formatter); throw; }
            if (rc != 0) RollBackImport(created, logger, formatter);
            return rc;
        }

        internal static void RollBackImport(List<string> created, ExportLogger logger, OutputFormatter formatter)
        {
            if (created == null || created.Count == 0 || DryRun.Active || App.AssetManager == null) return;
            int n = 0;
            for (int i = created.Count - 1; i >= 0; i--)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(created[i]);
                if (e == null || !e.IsAdded) continue;
                if (EbxWriteCommand.ExecuteDelete(new[] { created[i] }, logger, formatter) == 0) n++;
            }
            logger.LogWarning("port import: failed; {0} asset(s) it had created were reverted", n);
        }

        private static int ExecuteImportCore(string[] args, ExportLogger logger, OutputFormatter formatter, List<string> created)
        {
            string dir = null, asPath = null, meshTemplate = null, bpTemplate = null, physTemplate = null, texTemplate = null, shaderFallback = null, mvdb = null, bundle = null;
            bool noPhysics = false, keepComposite = false, keepTemplateMaterials = false, keepMaterialId = false;
            string geoPath = null, residentPath = null, shaderMapFile = null; bool noGeo = false, allowForeignDonors = false;
            s_templatePrefix = null; s_templateList = null; s_wordMapPath = null; int maxLods = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) asPath = args[++i];
                else if (args[i] == "--mesh-template" && i + 1 < args.Length) meshTemplate = args[++i];
                else if (args[i] == "--template-prefix" && i + 1 < args.Length) s_templatePrefix = args[++i];
                else if (args[i] == "--max-lods" && i + 1 < args.Length) maxLods = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--word-map" && i + 1 < args.Length) s_wordMapPath = args[++i];
                else if (args[i] == "--template-list" && i + 1 < args.Length)
                {
                    // One mesh path per line: the only templates the picker may use (e.g. the shipped
                    // meshes whose shader permutations the target level's RVM database already has).
                    s_templateList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string line in File.ReadAllLines(args[++i]))
                    { string t = line.Trim(); if (t.Length > 0 && !t.StartsWith("#")) s_templateList.Add(t); }
                }
                else if (args[i] == "--blueprint-template" && i + 1 < args.Length) bpTemplate = args[++i];
                else if (args[i] == "--physics-template" && i + 1 < args.Length) physTemplate = args[++i];
                else if (args[i] == "--texture-template" && i + 1 < args.Length) texTemplate = args[++i];
                else if (args[i] == "--shader-fallback" && i + 1 < args.Length) shaderFallback = args[++i];
                else if (args[i] == "--shader-map" && i + 1 < args.Length) shaderMapFile = args[++i];
                else if (args[i] == "--mvdb" && i + 1 < args.Length) mvdb = args[++i];
                else if (args[i] == "--bundle" && i + 1 < args.Length) bundle = args[++i];
                else if (args[i] == "--no-physics") noPhysics = true;
                else if (args[i] == "--keep-composite") keepComposite = true;
                else if (args[i] == "--keep-template-materials") keepTemplateMaterials = true;
                else if (args[i] == "--keep-material-id") keepMaterialId = true;
                else if (args[i] == "--geo" && i + 1 < args.Length) geoPath = args[++i];
                else if (args[i] == "--no-geo") noGeo = true;
                else if (args[i] == "--resident" && i + 1 < args.Length) residentPath = args[++i];
                else if (args[i] == "--allow-foreign-donors") allowForeignDonors = true;
                else if (!args[i].StartsWith("--")) dir = args[i];
            }
            if (dir == null) { Console.Error.WriteLine("port import: missing <package-dir>"); return 1; }
            LoadShaderMap(shaderMapFile);
            if (asPath == null) { Console.Error.WriteLine("port import: missing --as <blueprint-path>"); return 1; }
            if (!FrostyCLI.Core.Session.Project.IsLoaded) { Console.Error.WriteLine("port import: no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("port import: AssetManager not initialized."); return 1; }

            PortManifest m;
            try { m = PortManifest.Load(dir); }
            catch (Exception ex) { Console.Error.WriteLine("port import: " + Describe(ex)); return 1; }
            if (m.Mesh == null) { Console.Error.WriteLine("port import: manifest has no mesh"); return 1; }
            // A package with mesh.meshgeo (port export writes it; `mesh geo-dump` makes one for an older package) is imported
            // through the geometry port: the source's own vertex data in donor layouts of each section's own surface, instead
            // of the FBX importer (see MeshGeoPort). --no-geo forces the FBX path.
            GeoMesh geo = null;
            if (geoPath == null && !noGeo && File.Exists(Path.Combine(dir, "mesh.meshgeo"))) geoPath = Path.Combine(dir, "mesh.meshgeo");
            if (geoPath != null && !noGeo)
            {
                string gerr0 = GeoMesh.Load(geoPath, out geo);
                if (gerr0 != null) { Console.Error.WriteLine("port import: " + gerr0); return 1; }
                if (geo.Lods.Count != m.Mesh.LodCount) { Console.Error.WriteLine("port import: the geometry dump has " + geo.Lods.Count + " LOD(s), the manifest " + m.Mesh.LodCount); return 1; }
                if (keepTemplateMaterials) { Console.Error.WriteLine("port import: --keep-template-materials cannot be combined with the geometry port"); return 1; }
            }
            // Frosty's FBX importer only rejects a file with FEWER LODs than the template mesh; extra
            // "section:lodN" nodes are ignored. So a source with more LODs than any usable template can
            // be imported by declaring fewer LODs and letting the picker match on that.
            if (maxLods > 0 && m.Mesh.LodCount > maxLods)
            {
                m.Mesh.LodCount = maxLods;
                if (m.Mesh.SectionNames != null && m.Mesh.SectionNames.Count > maxLods) m.Mesh.SectionNames.RemoveRange(maxLods, m.Mesh.SectionNames.Count - maxLods);
                if (m.Mesh.SectionMaterials != null && m.Mesh.SectionMaterials.Count > maxLods) m.Mesh.SectionMaterials.RemoveRange(maxLods, m.Mesh.SectionMaterials.Count - maxLods);
            }
            if (App.AssetManager.GetEbxEntry(asPath) != null) { Console.Error.WriteLine("port import: destination already exists: " + asPath); return 1; }
            string asDir = asPath.Contains("/") ? asPath.Substring(0, asPath.LastIndexOf('/')) : "";
            string baseName = asPath.Contains("/") ? asPath.Substring(asPath.LastIndexOf('/') + 1) : asPath;
            string meshPath = asDir + "/" + baseName + "_Mesh";
            string physPath = asPath + "_Physics_Win32";
            if (bpTemplate == null) bpTemplate = DefaultBlueprintTemplate;
            if (physTemplate == null) physTemplate = bpTemplate + "_Physics_Win32";
            if (shaderFallback == null) shaderFallback = DefaultShaderFallback;
            var report = new List<string>();

            // 1. Shaders: same GUID in the target game, else the fallback. First, because a graph default texture is only
            //    wanted where the source graph itself is not kept.
            var shaderTarget = new List<EbxAssetEntry>();
            int shaderKept = 0, shaderFallen = 0;
            if (ResolveShaders(m, shaderFallback, report, shaderTarget, ref shaderKept, ref shaderFallen) != 0) return 1;
            DropKeptGraphDefaults(m, shaderTarget, report);

            // 2. Textures: reuse by GUID, else import the DDS as a new texture cloned from a template.
            var texTarget = new Dictionary<string, EbxAssetEntry>(StringComparer.OrdinalIgnoreCase);   // by source guid
            int reusedTex = 0, newTex = 0;
            // With --keep-template-materials the clone renders with the template's textures, so the
            // package's textures would only be dead weight in the bundle (and imported textures were
            // the prime suspect for a level that never finished streaming).
            if (!keepTemplateMaterials && ResolveTextures(m, dir, asDir, texTemplate, report, created, texTarget, ref reusedTex, ref newTex, logger, formatter) != 0) return 1;

            // 3. Mesh template: given, or target-game meshes ranked by how well they fit (same type,
            //    a kept shader, same or more LODs, same material count); the first that clones wins.
            // A composite source goes into a rigid template unless asked otherwise: the FBX importer's
            // composite path expects skin clusters linked to part bones, which a foreign FBX does not
            // carry (it fails in FbxTransformToLinearTransform), while the rigid path reads the same
            // section geometry, already in its part's pose.
            string templateType = m.Mesh.Type;
            if (string.Equals(m.Mesh.Type, "CompositeMeshAsset", StringComparison.Ordinal) && !keepComposite)
            {
                templateType = "RigidMeshAsset";
                report.Add("composite mesh imported as a rigid mesh (pass --keep-composite to use a composite template)");
            }
            var candidates = new List<KeyValuePair<string, string>>();
            if (meshTemplate != null)
            {
                candidates.Add(new KeyValuePair<string, string>(meshTemplate, "given"));
                if (FrostyCLI.Adapters.Mesh.MeshCloner.HasVertexUsage(App.AssetManager.GetEbxEntry(meshTemplate), "SubMaterialIndex") == true)
                    report.Add("mesh template " + meshTemplate + " declares per-vertex SubMaterialIndex, which the FBX cannot supply; the import will likely fail");
            }
            else
            {
                string why;
                candidates = PickMeshTemplates(m.Mesh, templateType, shaderTarget, asPath, out why);
                if (candidates.Count == 0) { Console.Error.WriteLine("port import: could not pick a mesh template (" + why + "); pass --mesh-template <mesh>"); return 1; }
            }
            EbxAssetEntry tmplMesh = null;
            int tLods = 0; List<List<string>> tNames; List<List<int>> tMats;
            foreach (KeyValuePair<string, string> cand in candidates)
            {
                EbxAssetEntry ce = App.AssetManager.GetEbxEntry(cand.Key);
                if (ce == null) { if (meshTemplate != null) { Console.Error.WriteLine("port import: mesh template not found: " + cand.Key); return 1; } continue; }
                int lods;
                string derr = FrostyCLI.Adapters.Mesh.MeshCloner.DescribeSections(ce, out lods, out tNames, out tMats);
                if (derr != null) { if (meshTemplate != null) { Console.Error.WriteLine("port import: " + derr); return 1; } continue; }
                if (lods < m.Mesh.LodCount)
                {
                    if (meshTemplate != null) { Console.Error.WriteLine("port import: mesh template has " + lods + " LOD(s) but the source mesh has " + m.Mesh.LodCount + "; the FBX importer needs an equal count and only trimming is supported. Pass a different --mesh-template."); return 1; }
                    continue;
                }
                tmplMesh = ce; tLods = lods; meshTemplate = ce.Name;
                report.Add("mesh template " + meshTemplate + " (" + cand.Value + ")");
                break;
            }
            if (tmplMesh == null) { Console.Error.WriteLine("port import: none of " + candidates.Count + " candidate mesh template(s) fits; pass --mesh-template <mesh>"); return 1; }
            if (tLods > m.Mesh.LodCount) report.Add("mesh template's " + tLods + " LOD(s) trimmed to " + m.Mesh.LodCount);
            // Only the named sections of a LOD are drawable material sections; the unnamed ones are
            // the shadow/depth sections every LOD carries. Grow (and rename) named sections only.
            int maxSections = 0;
            var namedMaterials = new List<List<int>>();
            for (int li = 0; li < m.Mesh.SectionNames.Count; li++)
            {
                var mats = new List<int>(); int named = 0;
                for (int si = 0; si < m.Mesh.SectionNames[li].Count; si++)
                    if (!string.IsNullOrEmpty(m.Mesh.SectionNames[li][si])) { named++; mats.Add(m.Mesh.SectionMaterials != null && li < m.Mesh.SectionMaterials.Count && si < m.Mesh.SectionMaterials[li].Count ? m.Mesh.SectionMaterials[li][si] : 0); }
                namedMaterials.Add(mats);
                maxSections = Math.Max(maxSections, named);
            }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "port import {0}: mesh {1} from {2} (sections {3}), physics {4}, blueprint {5} from {6}; {7} texture(s) reused, {8} new; shaders kept {9}, fallback {10}",
                    asPath, meshPath, meshTemplate, maxSections, noPhysics || m.Physics == null ? "skipped" : physPath, asPath, bpTemplate, reusedTex, newTex, shaderKept, shaderFallen);
                foreach (string r in report) logger.Log("port import: {0}", r);
                return 0;
            }

            // 4. Mesh: clone, grow, rename sections to the source names, ensure the material count, import the FBX.
            //    A template whose EBX does not survive the clone round-trip is skipped for the next candidate.
            EbxAssetEntry newMesh = null;
            string cerr = null;
            bool started = false;
            foreach (KeyValuePair<string, string> cand in candidates)
            {
                if (!started) { if (cand.Key != meshTemplate) continue; started = true; }
                EbxAssetEntry ce = App.AssetManager.GetEbxEntry(cand.Key);
                int lods; List<List<string>> cn; List<List<int>> cm;
                if (ce == null || FrostyCLI.Adapters.Mesh.MeshCloner.DescribeSections(ce, out lods, out cn, out cm) != null || lods < m.Mesh.LodCount) continue;
                cerr = FrostyCLI.Adapters.Mesh.MeshCloner.Clone(cand.Key, meshPath, out newMesh);
                if (cerr == null)
                {
                    if (cand.Key != meshTemplate) { report.Add("mesh template " + meshTemplate + " did not clone; using " + cand.Key + " (" + cand.Value + ")"); meshTemplate = cand.Key; tLods = lods; }
                    break;
                }
                if (App.AssetManager.GetEbxEntry(meshPath) != null) break;   // a half-made clone cannot be retried
            }
            if (cerr != null || newMesh == null) { Console.Error.WriteLine("port import: mesh clone failed: " + (cerr ?? "no candidate template")); return 1; }
            if (FrostyCLI.Adapters.Mesh.MeshCloner.LastCloneNote != null) report.Add(FrostyCLI.Adapters.Mesh.MeshCloner.LastCloneNote);
            created.Add(meshPath);
            if (tLods > m.Mesh.LodCount)
            {
                string terr = FrostyCLI.Adapters.Mesh.MeshCloner.TrimLods(newMesh, m.Mesh.LodCount);
                if (terr != null) { Console.Error.WriteLine("port import: " + terr); return 1; }
                int depotLodsRemoved;
                string derr2 = FrostyCLI.Adapters.Mesh.MeshCloner.TrimDepotLods(newMesh, m.Mesh.LodCount, out depotLodsRemoved);
                if (derr2 != null) { Console.Error.WriteLine("port import: " + derr2); return 1; }
                if (depotLodsRemoved > 0) report.Add(depotLodsRemoved + " shader block resource(s) of the trimmed LOD(s) removed from the depot");
            }
            if (maxSections > 0 && geo == null)
            {
                // Grows the named sections (and their depot blocks) in lockstep, keeping the shadow
                // sections last; a section without a shader block imports silently and never draws.
                int grownSections;
                string gerr = FrostyCLI.Adapters.Mesh.MeshCloner.GrowRealSections(newMesh, maxSections, namedMaterials, out grownSections);
                if (gerr != null) { Console.Error.WriteLine("port import: " + gerr); return 1; }
                if (grownSections > 0) report.Add(grownSections + " material section(s) added beyond the template's, with depot blocks");
            }
            if (geo == null)
            {
                string rerr = FrostyCLI.Adapters.Mesh.MeshCloner.RenameNamedSections(newMesh, m.Mesh.SectionNames, m.Mesh.SectionMaterials);
                if (rerr != null) { Console.Error.WriteLine("port import: " + rerr); return 1; }
            }
            // Every clone LOD is bundle-resident: say so, or an entity placement streams the higher LODs
            // from a store that does not have them and crashes on first draw.
            int residentLods;
            string lerr = FrostyCLI.Adapters.Mesh.MeshCloner.MarkAllLodsResident(newMesh, out residentLods);
            if (lerr != null) { Console.Error.WriteLine("port import: " + lerr); return 1; }
            if (residentLods > 0) report.Add(residentLods + " LOD(s) marked bundle-resident (IsBaseLod)");
            EbxAsset meshAsset;
            try { meshAsset = App.AssetManager.GetEbx(newMesh); }
            catch (Exception ex) { Console.Error.WriteLine("port import: new mesh load failed: " + Describe(ex)); return 1; }
            IList materials = ReadList(meshAsset.RootObject, "Materials");
            if (materials == null) { Console.Error.WriteLine("port import: new mesh has no Materials list"); return 1; }
            string merr = EnsureMaterialCount(meshAsset, materials, m.Mesh.Materials.Count);
            if (merr != null) { Console.Error.WriteLine("port import: " + merr); return 1; }
            // The template's occluder mesh is its own geometry, not this prop's: drop the reference.
            try { dynamic mroot = meshAsset.RootObject; mroot.OccluderMeshResource = (ulong)0; mroot.OccluderMeshEnable = false; }
            catch (Exception ex) { report.Add("could not clear the template's occluder mesh: " + Describe(ex)); }
            if (!EbxWriteCommand.CallModifyEbx(meshPath, meshAsset, logger)) return 1;

            var geoPlan = new List<string>();
            if (geo != null)
            {
                string perr0 = PortGeo.Run(newMesh, geo, shaderTarget, new PortGeo.Options { ResidentPath = residentPath, AllowForeign = allowForeignDonors, SourceTessellation = PortGeo.Options.TessellationOf(m), SourceFields = PortGeo.Options.FieldsOf(m), FallbackMaterials = PortGeo.Options.FallbacksOf(m, shaderTarget) }, logger, report, geoPlan);
                if (perr0 != null) { Console.Error.WriteLine("port import: geometry port failed: " + perr0); return 1; }
                foreach (string pl in geoPlan) logger.Log("port import: section {0}", pl);
            }
            else
            {
                string fbx = Path.Combine(dir, m.Mesh.File ?? "mesh.fbx");
                if (!File.Exists(fbx)) { Console.Error.WriteLine("port import: FBX not found: " + fbx); return 1; }
                string ierr = ImportCommand.ImportMeshFn(meshPath, fbx, 1.0f, "");
                if (ierr != null) { Console.Error.WriteLine("port import: mesh import failed: " + ierr); return 1; }
            }
            // The importer leaves the template's bounding box in place; the engine culls by it.
            {
                string bboxDetail;
                string bberr = FrostyCLI.Adapters.Mesh.MeshCloner.RecomputeBoundingBox(newMesh, 64f, out bboxDetail);
                if (bberr != null) { Console.Error.WriteLine("port import: " + bberr); return 1; }
                report.Add("bounding " + bboxDetail);
            }

            // 5. Materials: shader reference + texture parameters on the EBX, and the same texture
            //    parameters in the shader block depot where the template's blocks know the name.
            try { meshAsset = App.AssetManager.GetEbx(newMesh); materials = ReadList(meshAsset.RootObject, "Materials"); }
            catch (Exception ex) { Console.Error.WriteLine("port import: mesh reload failed: " + Describe(ex)); return 1; }
            int depotSet = 0, depotMissed = 0;
            if (keepTemplateMaterials)
            {
                report.Add("kept the mesh template's shader and material parameters so its compiled depot permutation remains coherent");
            }
            else
            {
                // The source material exactly: shader, bools, vectors, textures, conditionals and scalar fields -- nothing
                // the template material carried (PortRegeo.ApplyMaterials).
                if (geo == null) PortGeo.LastTessellation.Clear();   // FBX path: template sections, never tessellated (templates with DisplacementMapTexCoord are excluded)
                string aerr = PortRegeo.ApplyMaterials(newMesh, m, shaderTarget, texTarget, report, logger, out depotSet, out depotMissed);
                if (aerr != null) { Console.Error.WriteLine("port import: " + aerr); return 1; }
                // A template with more materials than the prop keeps its extra slots (EnsureMaterialCount only grows),
                // and some shipped templates carry a slot with no shader at all -- the MVDB entry is built from every
                // material, so it fails ("shader is not an external reference": LightGround_03 on a 4-material
                // template, the H8 stand-in failures). No section of the prop draws with those slots; give an empty
                // one the fallback shader so the entry builds.
                EbxAssetEntry fallbackShader = null;
                for (int i = m.Mesh.Materials.Count; i < materials.Count; i++)
                {
                    object mat = ResolveInternal(materials[i]) ?? materials[i];
                    object shaderData = mat != null ? ReadMember(mat, "Shader") : null;
                    Guid fg, cg;
                    if (shaderData == null || TryExternal(ReadMember(shaderData, "Shader"), out fg, out cg)) continue;
                    if (fallbackShader == null) fallbackShader = App.AssetManager.GetEbxEntry(shaderFallback);
                    if (fallbackShader == null) { Console.Error.WriteLine("port import: shader fallback not found: " + shaderFallback); return 1; }
                    Guid fbClass;
                    try { fbClass = InstanceGuidOf(App.AssetManager.GetEbx(fallbackShader).RootObject); }
                    catch (Exception ex) { Console.Error.WriteLine("port import: shader load failed for " + fallbackShader.Name + ": " + Describe(ex)); return 1; }
                    EbxRefWriter.SetRefByPath(mat, "Shader.Shader", fallbackShader.Guid, fbClass);
                    meshAsset.AddDependency(fallbackShader.Guid);
                    report.Add("template material " + i + " (unused by the prop) had no shader; set to " + shaderFallback);
                }
            }
            if (geo != null) PortRegeo.ApplyMeshFields(newMesh, m, report, logger);
            {
                string oerr = ApplyOccluder(newMesh, m, dir, report, logger);
                if (oerr != null) { Console.Error.WriteLine("port import: " + oerr); return 1; }
            }
            try { meshAsset = App.AssetManager.GetEbx(newMesh); materials = ReadList(meshAsset.RootObject, "Materials"); }
            catch (Exception ex) { Console.Error.WriteLine("port import: mesh reload failed: " + Describe(ex)); return 1; }

            // 6. Physics: clone a HavokAsset and replace its bytes with the source resource.
            EbxAssetEntry newPhys = null;
            byte[] physBytes = null;
            if (!noPhysics && m.Physics != null && m.Physics.File != null)
            {
                string pfile = Path.Combine(dir, m.Physics.File);
                if (!File.Exists(pfile)) { Console.Error.WriteLine("port import: physics file not found: " + pfile); return 1; }
                if (App.AssetManager.GetEbxEntry(physTemplate) == null) { Console.Error.WriteLine("port import: physics template not found: " + physTemplate + " (pass --physics-template or --no-physics)"); return 1; }
                string perr = AssetCloner.Clone(physTemplate, physPath, out newPhys);
                if (perr != null) { Console.Error.WriteLine("port import: physics clone failed: " + perr); return 1; }
                created.Add(physPath);
                try
                {
                    byte[] bytes = File.ReadAllBytes(pfile);
                    // A SWBF 2015 package carries 2015 physics material words (0x8 marker at bits 12..15, index at
                    // 16..23). Written verbatim, BFII reads them with its own layout and the character meets the shape
                    // but walks through it -- the walk-through reported on earlier 2015 ports. Re-encode them through
                    // the DICE-pair word map (memory swbf1-physics-material-words) unless explicitly told not to.
                    if (string.Equals(m.SourceGame, "swbf1", StringComparison.OrdinalIgnoreCase) &&
                        Environment.GetEnvironmentVariable("FROSTYCLI_RAW_SWBF1_MATERIALS") != "1")
                    {
                        string mapPath = s_wordMapPath ?? Environment.GetEnvironmentVariable("FROSTYCLI_SWBF1_WORDMAP");
                        if (mapPath == null)
                        {
                            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                            foreach (string cand in new[] { Path.Combine(exeDir, "swbf1_material_wordmap.json"), Path.Combine(exeDir, "data", "swbf1_material_wordmap.json") })
                                if (File.Exists(cand)) { mapPath = cand; break; }
                        }
                        if (mapPath == null || !File.Exists(mapPath))
                        { Console.Error.WriteLine("port import: a SWBF 2015 package needs the 2015 -> BFII material word map (swbf1_material_wordmap.json beside the exe, or --word-map)"); return 1; }
                        HavokPhysicsResource.Swbf1ConvertReport rep;
                        bytes = HavokPhysicsResource.ConvertFromSwbf1(bytes, HavokPhysicsResource.ReadWordMap(File.ReadAllText(mapPath)), out rep);
                        report.Add("physics: " + rep.MaterialWordsMapped + " of " + rep.MaterialWords + " 2015 material word(s) re-encoded" +
                                   (rep.MaterialWordsUnmapped > 0 ? "; UNMAPPED (still 2015, walk-through risk): " + string.Join(" ", rep.Unmapped) : ""));
                    }
                    physBytes = bytes;
                    byte[] meta = ParseHex(m.Physics.MetaHex);
                    // port export captured the source resource's shipping cookie. Preserve it exactly:
                    // HavokPhysicsResource only guarantees regenerated metadata after one of its
                    // supported group-resource edits, and some standalone prop payloads have header
                    // padding/layout that its generic Meta calculation deliberately does not retain.
                    // Falling back to a derived cookie is only for old packages that predate MetaHex.
                    if (meta.Length == 0)
                        try { meta = HavokPhysicsResource.Parse(bytes).Meta; } catch { }
                    ulong rid = (ulong)((dynamic)App.AssetManager.GetEbx(newPhys).RootObject).Resource;
                    ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                    if (re == null) { Console.Error.WriteLine("port import: cloned physics asset has no resource"); return 1; }
                    App.AssetManager.ModifyRes(re.Name, bytes, meta);
                }
                catch (Exception ex) { Console.Error.WriteLine("port import: physics write failed: " + Describe(ex)); return 1; }
            }
            else if (!noPhysics) report.Add("no physics in the package; the blueprint keeps the template's physics reference");

            // 7. Blueprint: clone the template and rewire mesh, physics and part boxes.
            if (App.AssetManager.GetEbxEntry(bpTemplate) == null) { Console.Error.WriteLine("port import: blueprint template not found: " + bpTemplate); return 1; }
            if (EbxWriteCommand.ExecuteClone(new[] { bpTemplate, "--as", asPath }, logger, formatter) != 0) return 1;
            created.Add(asPath);
            EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(asPath);
            EbxAsset bpAsset;
            try { bpAsset = App.AssetManager.GetEbx(bpEntry); }
            catch (Exception ex) { Console.Error.WriteLine("port import: new blueprint load failed: " + Describe(ex)); return 1; }
            object entity = ResolveInternal(ReadMember(bpAsset.RootObject, "Object"));
            if (entity == null) { Console.Error.WriteLine("port import: blueprint template has no Object entity"); return 1; }
            try
            {
                EbxRefWriter.SetRefByPath(entity, "Mesh", newMesh.Guid, InstanceGuidOf(meshAsset.RootObject));
                bpAsset.AddDependency(newMesh.Guid);
                if (newPhys != null)
                {
                    Guid physClass = InstanceGuidOf(App.AssetManager.GetEbx(newPhys).RootObject);
                    IList components = ReadList(entity, "Components");
                    int bodies = 0;
                    if (components != null)
                        foreach (object raw in components)
                        {
                            object comp = ResolveInternal(raw);
                            if (comp == null || comp.GetType().Name != "StaticModelPhysicsComponentData") continue;
                            IList list = ReadList(comp, "PhysicsBodies");
                            if (list == null) continue;
                            for (int b = 0; b < list.Count; b++)
                            {
                                object body = ResolveInternal(list[b]) ?? list[b];
                                EbxRefWriter.SetRefByPath(body, "Asset", newPhys.Guid, physClass);
                                if (body != list[b] && !(list[b] is PointerRef)) list[b] = body;
                                bodies++;
                            }
                        }
                    bpAsset.AddDependency(newPhys.Guid);
                    if (bodies == 0) report.Add("blueprint template has no rigid bodies; physics asset not referenced");
                }
                // Part boxes: the source blueprint's if it had any, else the collision shape's boxes.
                List<PortBox> partBoxes = m.PartBoundingBoxes;
                if ((partBoxes == null || partBoxes.Count == 0) && physBytes != null)
                {
                    try
                    {
                        HavokPhysicsResource.PropInfo info = HavokPhysicsResource.InspectProp(physBytes);
                        partBoxes = new List<PortBox>();
                        if (info.PartBoxes != null) foreach (float[][] pb in info.PartBoxes) partBoxes.Add(new PortBox { Min = pb[0], Max = pb[1] });
                        else partBoxes.Add(new PortBox { Min = info.BoxMin, Max = info.BoxMax });
                    }
                    catch (Exception ex) { report.Add("part boxes not derived from the physics resource: " + Describe(ex)); }
                }
                // One box and one PhysicsPartInfo per physics part, every one in range, every box containing the
                // collision shape. The template's single PhysicsPartInfo on a 5-part prop, or a part box that misses
                // its Havok shape, both resolve to the 16-bit not-found sentinel and InternalPhysicsManager
                // dereferences null at game+0x7BB4435 once anything touches the prop (memory
                // bfii-partboundingboxes-crash; Hoth T9h: UplinkBase_01 5 parts, SnowSpeeder_Crashed_01 10).
                HavokPhysicsResource.PropInfo? shapeInfo = null;
                if (physBytes != null) { try { shapeInfo = HavokPhysicsResource.InspectProp(physBytes); } catch { } }
                int physParts = shapeInfo.HasValue && shapeInfo.Value.PartCount > 0 ? shapeInfo.Value.PartCount : (partBoxes != null ? partBoxes.Count : 0);
                if (shapeInfo.HasValue && shapeInfo.Value.BoxMin != null && shapeInfo.Value.BoxMax != null && physParts > 0)
                {
                    HavokPhysicsResource.PropInfo shape = shapeInfo.Value;
                    if (partBoxes == null) partBoxes = new List<PortBox>();
                    while (partBoxes.Count < physParts) partBoxes.Add(new PortBox { Min = (float[])shape.BoxMin.Clone(), Max = (float[])shape.BoxMax.Clone() });
                    if (partBoxes.Count > physParts) partBoxes.RemoveRange(physParts, partBoxes.Count - physParts);
                    int grown = 0;
                    for (int k = 0; k < partBoxes.Count; k++)
                    {
                        float[] smin = shape.PartBoxes != null && k < shape.PartBoxes.Length ? shape.PartBoxes[k][0] : shape.BoxMin;
                        float[] smax = shape.PartBoxes != null && k < shape.PartBoxes.Length ? shape.PartBoxes[k][1] : shape.BoxMax;
                        PortBox pb = partBoxes[k];
                        bool changed = false;
                        float[] mn = (float[])pb.Min.Clone(), mx = (float[])pb.Max.Clone();
                        for (int a = 0; a < 3; a++)
                        {
                            if (smin[a] < mn[a]) { mn[a] = smin[a]; changed = true; }
                            if (smax[a] > mx[a]) { mx[a] = smax[a]; changed = true; }
                        }
                        if (changed) { partBoxes[k] = new PortBox { Min = mn, Max = mx }; grown++; }
                    }
                    if (grown > 0) report.Add(grown + " part box(es) grown to contain the collision shape");
                }
                IList boxes = ReadList(entity, "PartBoundingBoxes");
                if (boxes != null && partBoxes != null && partBoxes.Count > 0)
                {
                    Type boxType = PropertyPathWriter.ListElementType(boxes);
                    boxes.Clear();
                    foreach (PortBox pb in partBoxes) boxes.Add(LevelCommand.MakeBox(boxType, pb.Min, pb.Max));
                }
                IList infos = ReadList(entity, "PhysicsPartInfos");
                if (infos != null && physParts > 0)
                {
                    // the blueprint's first PartComponentData and how many health states it has
                    int healthStates = 0;
                    IList comps = ReadList(entity, "Components");
                    if (comps != null)
                        foreach (object c in comps)
                        {
                            object pc = ResolveInternal(c) ?? c;
                            if (pc != null && pc.GetType().Name == "PartComponentData")
                            {
                                IList hs = ReadList(pc, "HealthStates");
                                healthStates = hs != null ? hs.Count : 0;
                                break;
                            }
                        }
                    if (healthStates > 0)
                    {
                        Type infoType = PropertyPathWriter.ListElementType(infos);
                        int before = infos.Count;
                        infos.Clear();
                        for (int k = 0; k < physParts; k++)
                        {
                            object info = Activator.CreateInstance(infoType);
                            PropertyPathWriter.SetValue(info, "PartComponentIndex", "0");
                            PropertyPathWriter.SetValue(info, "HealthStateIndex", Math.Min(k, healthStates - 1).ToString(CultureInfo.InvariantCulture));
                            infos.Add(info);
                        }
                        if (before != physParts) report.Add("PhysicsPartInfos " + before + " -> " + physParts + " (one per physics part, component 0, health states in range)");
                    }
                    else report.Add("blueprint has no PartComponentData with health states; PhysicsPartInfos left as the template's (" + infos.Count + ")");
                }
                // A template of the same object (--blueprint-template: BFII's own TransporterPersonel_01 carries 2015's five
                // part components, health state for health state) takes the source's part wiring and health values.
                ApplySourcePartWiring(entity, dir, m, physParts, report, bpAsset);
                // Root-shape slots must exist: 103 2015 props have a single root (no raycast twin), and the template
                // body's RaycastRootShapeIndex 1 then resolves to the not-found sentinel -- the same
                // game+0x7BB4435 fault (Hoth T10: Arctic_01_RebelLastBase_02_Cluster_01). 2015 reuses root 0.
                if (shapeInfo.HasValue && shapeInfo.Value.RootCount > 0)
                {
                    int lastRoot = shapeInfo.Value.RootCount - 1, clamped = 0;
                    IList comps2 = ReadList(entity, "Components");
                    if (comps2 != null)
                        foreach (object c in comps2)
                        {
                            object pc = ResolveInternal(c) ?? c;
                            if (pc == null || !pc.GetType().Name.EndsWith("PhysicsComponentData")) continue;
                            IList bodies2 = ReadList(pc, "PhysicsBodies");
                            if (bodies2 == null) continue;
                            foreach (object b in bodies2)
                            {
                                object body = ResolveInternal(b) ?? b;
                                if (body == null) continue;
                                foreach (string slot in new[] { "CollisionRootShapeIndex", "RaycastRootShapeIndex" })
                                {
                                    object v = ReadMember(body, slot);
                                    if (v == null) continue;
                                    int cur = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                                    if (cur > lastRoot) { PropertyPathWriter.SetValue(body, slot, lastRoot.ToString(CultureInfo.InvariantCulture)); clamped++; }
                                }
                            }
                        }
                    if (clamped > 0) report.Add(clamped + " rigid-body root slot(s) clamped to the " + (lastRoot + 1) + " root(s) the physics resource has");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("port import: blueprint rewire failed: " + Describe(ex)); return 1; }
            if (!EbxWriteCommand.CallModifyEbx(asPath, bpAsset, logger)) return 1;

            // 8. MVDB entry and bundling.
            if (mvdb != null)
            {
                string[] mvdbArgs = keepTemplateMaterials
                    ? (keepMaterialId ? new[] { mvdb, "--mesh", meshPath, "--like", meshTemplate } : new[] { mvdb, "--mesh", meshPath, "--like", meshTemplate, "--zero-material-id" })
                    : new[] { mvdb, "--mesh", meshPath };
                if (MvdbCommand.ExecuteAdd(mvdbArgs, logger, formatter) != 0) return 1;
            }
            if (bundle != null)
            {
                foreach (string name in created)
                    if (BundleCommand.ExecuteAdd(new[] { name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                foreach (EbxAssetEntry te in texTarget.Values)
                    if (!created.Contains(te.Name) && BundleCommand.ExecuteAdd(new[] { te.Name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                if (geo != null)
                {
                    int scannedDepots;
                    foreach (EbxAssetEntry dt in LevelCommand.DepotTextures(newMesh, out scannedDepots))
                        if (BundleCommand.ExecuteAdd(new[] { dt.Name, "--to", bundle, "--with-resources" }, logger, formatter) != 0) return 1;
                }
            }

            formatter.WriteResult("OK", "port import", new Dictionary<string, string>
            {
                { "blueprint", asPath }, { "mesh", meshPath }, { "mesh_template", meshTemplate }, { "physics", newPhys != null ? physPath : "" },
                { "textures_reused", reusedTex.ToString(CultureInfo.InvariantCulture) }, { "textures_new", newTex.ToString(CultureInfo.InvariantCulture) },
                { "shaders_kept", shaderKept.ToString(CultureInfo.InvariantCulture) }, { "shaders_fallback", shaderFallen.ToString(CultureInfo.InvariantCulture) },
                { "depot_params_set", depotSet.ToString(CultureInfo.InvariantCulture) },
                { "geometry", geo != null ? "source dump (" + geo.Lods.Count + " LODs)" : "fbx" }
            });
            foreach (string r in report) logger.LogWarning("port import: {0}", r);
            return 0;
        }

        // ------------------------------------------------------------------ layers

        // Writes every static placement of a level (or one group of it) with its blueprint and transform.
        public static int ExecuteExportLayer(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, outDir = null, filter = null;
            int group = -1;
            // --entities also exports placements outside static model groups (ObjectReferenceObjectData props and
            // prefab references) with Group/Member/Instance -1; by default only group members are written.
            bool entities = false;
            // --flatten-prefabs expands prefab references (SWBF1 levels place whole buildings -- the Sullust Imperial
            // hangar -- as prefabs) into the placements inside them, in world space. Implies --entities.
            bool flatten = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (args[i] == "--entities") entities = true;
                else if (args[i] == "--flatten-prefabs") { flatten = true; entities = true; }
                else if (args[i] == "--group" && i + 1 < args.Length) group = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (level == null) { Console.Error.WriteLine("port export-layer: missing <level>"); return 1; }
            if (outDir == null) { Console.Error.WriteLine("port export-layer: missing --out <dir>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("port export-layer: AssetManager not initialized. Pass --game-path."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(level);
            if (entry == null) { Console.Error.WriteLine("port export-layer: level not found: " + level); return 1; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("port export-layer: load failed: " + Describe(ex)); return 1; }

            var rows = new List<LevelCommand.EntityRow>();
            string warning;
            if (!LevelCommand.TryCollectBfiiRows(asset.RootObject, rows, flatten ? null : filter, int.MaxValue, out warning))
            { Console.Error.WriteLine("port export-layer: no static placements resolved" + (warning != null ? ": " + warning : "")); return 1; }
            if (warning != null) logger.LogWarning("port export-layer: {0}", warning);
            if (flatten)
            {
                var flat = new List<LevelCommand.EntityRow>();
                var prefabCache = new Dictionary<string, List<LevelCommand.EntityRow>>(StringComparer.OrdinalIgnoreCase);
                int expanded = 0;
                foreach (LevelCommand.EntityRow row in rows)
                    FlattenRow(row, FrostyCLI.Core.LightPort.Identity(), 0, flat, prefabCache,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref expanded, logger);
                logger.Log("port export-layer: {0} prefab placement(s) expanded; {1} row(s) -> {2}", expanded, rows.Count, flat.Count);
                rows = flat;
            }

            var layer = new PortLayer { SourceGame = FrostyCLI.Core.Session.GameName ?? "", Level = entry.Name };
            var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (LevelCommand.EntityRow row in rows)
            {
                if (row.Blueprint == null) continue;
                if (flatten && filter != null && !row.Blueprint.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (row.GroupIndex == null && !entities) continue;
                if (group >= 0 && (row.GroupIndex == null || row.GroupIndex.Value != group)) continue;
                string guid;
                if (!guids.TryGetValue(row.Blueprint, out guid))
                {
                    EbxAssetEntry bp = App.AssetManager.GetEbxEntry(row.Blueprint);
                    guid = bp != null ? bp.Guid.ToString("D") : null;
                    guids[row.Blueprint] = guid;
                }
                layer.Placements.Add(new PortPlacement
                {
                    Blueprint = row.Blueprint, Guid = guid, Group = row.GroupIndex ?? -1, Member = row.MemberIndex ?? -1, Instance = row.InstanceIndex ?? -1,
                    Source = row.TransformSource, Right = row.Right, Up = row.Up, Forward = row.Forward, Trans = row.Trans
                });
            }
            if (DryRun.Active) { DryRun.Report(logger, "port export-layer {0}: {1} placement(s) of {2} blueprint(s) to {3}", entry.Name, layer.Placements.Count, guids.Count, outDir); return 0; }
            layer.Save(outDir);
            formatter.WriteResult("OK", "port export-layer", new Dictionary<string, string>
            {
                { "level", entry.Name }, { "placements", layer.Placements.Count.ToString(CultureInfo.InvariantCulture) },
                { "blueprints", guids.Count.ToString(CultureInfo.InvariantCulture) }, { "out", outDir }
            });
            return 0;
        }

        // --flatten-prefabs: a placement whose blueprint has its own Objects[] (a prefab) is replaced by the placements
        // inside it, each composed with the prefab's transform. Nested prefabs recurse (depth 8); ordinary blueprints stay.
        private static void FlattenRow(LevelCommand.EntityRow row, FrostyCLI.Core.LinearTransformData parent, int depth,
            List<LevelCommand.EntityRow> output, Dictionary<string, List<LevelCommand.EntityRow>> cache, HashSet<string> inProgress,
            ref int expanded, ExportLogger logger, string root = null)
        {
            var local = new FrostyCLI.Core.LinearTransformData { Right = row.Right, Up = row.Up, Forward = row.Forward, Trans = row.Trans };
            FrostyCLI.Core.LinearTransformData world = FrostyCLI.Core.LightPort.Compose(local, parent);
            List<LevelCommand.EntityRow> inner = row.GroupIndex == null && row.Blueprint != null && depth < 8 && !inProgress.Contains(row.Blueprint)
                ? PrefabRows(row.Blueprint, cache, logger) : null;
            if (inner == null)
            {
                output.Add(new LevelCommand.EntityRow
                {
                    Blueprint = row.Blueprint, Right = world.Right, Up = world.Up, Forward = world.Forward, Trans = world.Trans,
                    GroupIndex = row.GroupIndex, MemberIndex = row.MemberIndex, InstanceIndex = row.InstanceIndex,
                    TransformSource = depth == 0 ? row.TransformSource : "prefab:" + root + ":" + row.TransformSource
                });
                return;
            }
            expanded++;
            inProgress.Add(row.Blueprint);
            string top = root ?? row.Blueprint.Substring(row.Blueprint.LastIndexOf('/') + 1);
            foreach (LevelCommand.EntityRow child in inner) FlattenRow(child, world, depth + 1, output, cache, inProgress, ref expanded, logger, top);
            inProgress.Remove(row.Blueprint);
        }

        // The placements inside a prefab-like blueprint (root with an Objects list), in its local space; null for ordinary
        // blueprints (an ObjectBlueprint root holds a single Object) and for anything unreadable.
        private static List<LevelCommand.EntityRow> PrefabRows(string blueprint, Dictionary<string, List<LevelCommand.EntityRow>> cache,
            ExportLogger logger)
        {
            List<LevelCommand.EntityRow> rows;
            if (cache.TryGetValue(blueprint, out rows)) return rows;
            rows = null;
            try
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(blueprint);
                if (e != null)
                {
                    EbxAsset a = App.AssetManager.GetEbx(e);
                    if (ReadMember(a.RootObject, "Objects") is System.Collections.IList)
                    {
                        var found = new List<LevelCommand.EntityRow>();
                        string w;
                        if (LevelCommand.TryCollectBfiiRows(a.RootObject, found, null, int.MaxValue, out w)) rows = found;
                        if (w != null) logger.LogWarning("port export-layer: {0}: {1}", blueprint, w);
                    }
                }
            }
            catch (Exception ex) { logger.LogWarning("port export-layer: could not expand {0}: {1}", blueprint, ex.Message); }
            cache[blueprint] = rows;
            return rows;
        }

        // Rebuilds an exported layer inside a target level's static group: each source blueprint is
        // resolved to one the target has (same GUID, same basename, or a ported copy under
        // --ported-prefix), the first placement of a blueprint becomes a new member and later ones
        // append instances to it.
        public static int ExecuteImportLayer(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string dir = null, into = null, offsetArg = null, portedPrefix = null, bundle = null, mvdb = null, entityClass = null, registry = null; uint? entityFlags = null;
            // The type a source blueprint may resolve to. Static props are ObjectBlueprint; an FX
            // layer resolves to EffectBlueprint, and matching by GUID against the wrong type finds nothing.
            string blueprintType = "ObjectBlueprint";
            int group = -1, limit = -1, sourceGroup = -1;
            bool noBundle = false, keepGoing = false, onlyGuid = false, excludeGuid = false, newMembersOnly = false, asMembers = false, onlyPorted = false, preferPorted = false, noRadiosity = false;
            HashSet<string> excludeList = null;
            // Static, so a pipeline run with an earlier --no-collision pass would otherwise leak it
            // into every later pass.
            LevelCommand.s_noCollision = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--into" && i + 1 < args.Length) into = args[++i];
                else if (args[i] == "--as-members") asMembers = true;
                else if (args[i] == "--no-collision") LevelCommand.s_noCollision = true;
                else if (args[i] == "--member-stub" && i + 1 < args.Length) LevelCommand.s_memberStub = args[++i];
                else if (args[i] == "--bundle-shaders") LevelCommand.s_bundleShaders = true;
                else if (args[i] == "--skip-bundle-types" && i + 1 < args.Length) LevelCommand.s_bundleSkipTypes.AddRange(args[++i].Split(','));
                else if (args[i] == "--entity-class" && i + 1 < args.Length) entityClass = args[++i];
                else if (args[i] == "--registry" && i + 1 < args.Length) registry = args[++i];
                else if (args[i] == "--flags" && i + 1 < args.Length) entityFlags = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--blueprint-type" && i + 1 < args.Length) blueprintType = args[++i];
                else if (args[i] == "--group" && i + 1 < args.Length) group = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--source-group" && i + 1 < args.Length) sourceGroup = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--offset" && i + 1 < args.Length) offsetArg = args[++i];
                else if (args[i] == "--ported-prefix" && i + 1 < args.Length) portedPrefix = args[++i].TrimEnd('/');
                else if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--bundle" && i + 1 < args.Length) bundle = args[++i];
                else if (args[i] == "--mvdb" && i + 1 < args.Length) mvdb = args[++i];
                else if (args[i] == "--no-bundle") noBundle = true;
                else if (args[i] == "--continue") keepGoing = true;
                else if (args[i] == "--only-guid") onlyGuid = true;
                else if (args[i] == "--exclude-guid") excludeGuid = true;
                else if (args[i] == "--exclude-list" && i + 1 < args.Length)
                {
                    // Blueprint names (full source path or basename), one per line: placements of these are skipped.
                    excludeList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string line in File.ReadAllLines(args[++i])) { string t = line.Trim(); if (t.Length > 0 && !t.StartsWith("#")) excludeList.Add(t); }
                }
                else if (args[i] == "--only-ported") onlyPorted = true;
                else if (args[i] == "--prefer-ported") preferPorted = true;
                else if (args[i] == "--new-members-only") newMembersOnly = true;
                else if (args[i] == "--no-radiosity") noRadiosity = true;
                else if (!args[i].StartsWith("--")) dir = args[i];
            }
            if (dir == null) { Console.Error.WriteLine("port import-layer: missing <layer-dir>"); return 1; }
            if (into == null) { Console.Error.WriteLine("port import-layer: missing --into <level>"); return 1; }
            if (asMembers && group < 0) { Console.Error.WriteLine("port import-layer: --as-members needs --group <index>"); return 1; }
            if (!FrostyCLI.Core.Session.Project.IsLoaded) { Console.Error.WriteLine("port import-layer: no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("port import-layer: AssetManager not initialized."); return 1; }
            float[] offset = { 0f, 0f, 0f };
            if (offsetArg != null)
            {
                string[] p = offsetArg.Split(',');
                if (p.Length != 3) { Console.Error.WriteLine("port import-layer: --offset needs x,y,z"); return 1; }
                for (int i = 0; i < 3; i++) offset[i] = float.Parse(p[i].Trim(), CultureInfo.InvariantCulture);
            }
            PortLayer layer;
            try { layer = PortLayer.Load(dir); }
            catch (Exception ex) { Console.Error.WriteLine("port import-layer: " + Describe(ex)); return 1; }
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(into);
            if (levelEntry == null) { Console.Error.WriteLine("port import-layer: level not found: " + into); return 1; }
            if (mvdb != null && App.AssetManager.GetEbxEntry(mvdb) == null) { Console.Error.WriteLine("port import-layer: mvdb not found: " + mvdb); return 1; }

            // Resolve every distinct source blueprint once.
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // source path -> target path (null = unresolved)
            var how = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> byName = null;
            foreach (PortPlacement pl in layer.Placements)
            {
                if (pl.Blueprint == null || resolved.ContainsKey(pl.Blueprint)) continue;
                string target = null, method = null;
                string baseName = pl.Blueprint.Substring(pl.Blueprint.LastIndexOf('/') + 1);
                if (excludeList != null && (excludeList.Contains(pl.Blueprint) || excludeList.Contains(baseName)))
                {
                    resolved[pl.Blueprint] = null;
                    how[pl.Blueprint] = "skipped (--exclude-list)";
                    continue;
                }
                if (preferPorted && portedPrefix != null)
                {
                    string candidate = portedPrefix + "/" + baseName + "/" + baseName;
                    if (App.AssetManager.GetEbxEntry(candidate) != null) { target = candidate; method = "ported"; }
                }
                if (target == null && pl.Guid != null)
                {
                    EbxAssetEntry same = App.AssetManager.GetEbxEntry(Guid.Parse(pl.Guid));
                    if (same != null && string.Equals(same.Type, blueprintType, StringComparison.OrdinalIgnoreCase))
                    {
                        if (excludeGuid)
                        {
                            resolved[pl.Blueprint] = null;
                            how[pl.Blueprint] = "skipped (GUID counterpart excluded)";
                            continue;
                        }
                        target = same.Name; method = "guid";
                    }
                }
                if (target == null && portedPrefix != null)
                {
                    string candidate = portedPrefix + "/" + baseName + "/" + baseName;
                    if (App.AssetManager.GetEbxEntry(candidate) != null) { target = candidate; method = "ported"; }
                }
                if (target != null && excludeList != null && (excludeList.Contains(target) || excludeList.Contains(target.Substring(target.LastIndexOf('/') + 1))))
                {
                    // The exclusion may name the TARGET (e.g. a BFII asset a 2015 blueprint resolves to by GUID).
                    resolved[pl.Blueprint] = null;
                    how[pl.Blueprint] = "skipped (--exclude-list: " + target + ")";
                    continue;
                }
                if (target == null && !onlyGuid)
                {
                    if (byName == null)
                    {
                        byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                        {
                            if (!string.Equals(e.Type, blueprintType, StringComparison.OrdinalIgnoreCase)) continue;
                            string n = e.Name.Substring(e.Name.LastIndexOf('/') + 1);
                            List<string> list;
                            if (!byName.TryGetValue(n, out list)) byName[n] = list = new List<string>();
                            list.Add(e.Name);
                        }
                    }
                    List<string> cands;
                    if (byName.TryGetValue(baseName, out cands) && cands.Count > 0)
                    {
                        cands.Sort(StringComparer.OrdinalIgnoreCase);
                        target = cands[0]; method = cands.Count == 1 ? "name" : "name (first of " + cands.Count + ")";
                    }
                }
                resolved[pl.Blueprint] = target;
                how[pl.Blueprint] = method ?? "missing";
            }

            if (onlyPorted)
                foreach (string key in new List<string>(resolved.Keys))
                    if (how[key] != "ported") { resolved[key] = null; how[key] = "skipped (not ported)"; }
            if (!asMembers)
                return ImportLayerAsEntities(layer, into, resolved, how, offset, sourceGroup, limit, bundle, noBundle, mvdb, newMembersOnly, entityClass, entityFlags, registry, logger, formatter);

            // Existing members of the target group, so re-runs append rather than duplicate.
            var memberOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            IList members = null;
            try
            {
                EbxAsset levelAsset = App.AssetManager.GetEbx(levelEntry);
                string gerr;
                object g = LevelCommand.FindStaticGroup(levelAsset.RootObject, group, out gerr);
                if (g == null) { Console.Error.WriteLine("port import-layer: " + gerr); return 1; }
                string merr;
                if (LevelCommand.FindMember(g, 0, out members, out merr) == null && members == null) members = new List<object>();
                for (int i = 0; members != null && i < members.Count; i++)
                {
                    object member = ResolveInternal(members[i]) ?? members[i];
                    string bp = member != null ? LevelCommand.ResolveBlueprintPath(ReadMember(member, "MemberType")) : null;
                    if (bp != null && !memberOf.ContainsKey(bp)) memberOf[bp] = i;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("port import-layer: could not read the target group: " + Describe(ex)); return 1; }
            int memberCount = members != null ? members.Count : 0;
            // With --new-members-only, blueprints the group already holds are left alone: the pass
            // only adds what a previous pass could not resolve (typically after `port import`).
            var existingMembers = new HashSet<string>(memberOf.Keys, StringComparer.OrdinalIgnoreCase);
            int alreadyPlaced = 0;

            int placed = 0, skipped = 0, failed = 0, newMembers = 0, considered = 0, registered = 0, copied = 0;
            var report = new List<string>();
            var missingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // Blueprints whose mesh the variation database could not take: never placed.
            var unregisteredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Placements arrive in source member order, so runs of the same target blueprint are long;
            // each run goes to the target member as one batch (one resource rewrite per run).
            string runTarget = null, runSource = null;
            var runPos = new List<string>();
            var runRot = new List<string>();
            bool aborted = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            ResWriter.Fast = !DryRun.Active;
            Func<bool> flush = () =>
            {
                if (runTarget == null || runPos.Count == 0) return true;
                string target = runTarget;
                int memberIndex;
                int start = 0;
                long t0 = clock.ElapsedMilliseconds, tMember = 0, tMvdb = 0;
                if (!memberOf.TryGetValue(target, out memberIndex))
                {
                    if (unregisteredTargets.Contains(target)) { skipped += runPos.Count; return true; }
                    // A new member's mesh must be in the sub-world's variation database to render, and a PLACED mesh
                    // without an entry crashes the game at game+0xF58B89 as soon as the render job reaches it (Sullust
                    // V29: a half-made `port import` clone whose entry could be neither derived nor copied). Register
                    // before the member exists; a mesh that cannot be registered is left out with all its placements.
                    if (mvdb != null)
                    {
                        string meshPath = BlueprintMeshPath(target);
                        if (meshPath == null) report.Add("could not find the mesh of " + target + "; not registered in " + mvdb);
                        else if (!MvdbHasMesh(mvdb, meshPath))
                        {
                            long m0 = clock.ElapsedMilliseconds;
                            string cerr = null;
                            if (MvdbCommand.ExecuteAdd(new[] { mvdb, "--mesh", meshPath }, logger, formatter) == 0) registered++;
                            else
                            {
                                // Shipped meshes whose records cannot be derived from the mesh: copy the
                                // entry from whichever database already registers them.
                                string donorDb = FindDatabaseWithMesh(meshPath);
                                int mats;
                                cerr = donorDb == null ? "no database registers it" : MvdbCommand.CopyEntry(App.AssetManager.GetEbxEntry(mvdb), App.AssetManager.GetEbxEntry(donorDb), App.AssetManager.GetEbxEntry(meshPath), 0, logger, out mats);
                                if (cerr == null) { registered++; copied++; }
                            }
                            tMvdb = clock.ElapsedMilliseconds - m0;
                            if (cerr != null)
                            {
                                report.Add("mvdb add failed for " + meshPath + " and copying failed (" + cerr + "); " + target + " was NOT placed (an unregistered mesh crashes the game at game+0xF58B89)");
                                unregisteredTargets.Add(target);
                                skipped += runPos.Count;
                                return true;
                            }
                        }
                    }
                    long a0 = clock.ElapsedMilliseconds;
                    int rc0 = LevelCommand.AddGroupMember(into, group, target, null, runPos[0], runRot[0], "1", bundle, noBundle, logger);
                    tMember = clock.ElapsedMilliseconds - a0;
                    if (rc0 != 0) { failed++; Console.Error.WriteLine("port import-layer: new member for " + runSource + " -> " + target + " failed"); return false; }
                    memberIndex = memberCount++; memberOf[target] = memberIndex; newMembers++; placed++; start = 1;
                }
                int rest = runPos.Count - start;
                long b0 = clock.ElapsedMilliseconds;
                if (rest > 0)
                {
                    int rc;
                    if (LevelCommand.IsHavokBackedMember(into, group, memberIndex))
                    {
                        var xforms = new List<LinearTransformData>(rest);
                        for (int i = start; i < runPos.Count; i++) xforms.Add(TransformBuilder.Build(runPos[i], runRot[i], "1"));
                        rc = LevelCommand.AddHavokGroupedInstances(into, group, memberIndex, 0, xforms, logger);
                        if (rc == 0) placed += rest;
                    }
                    else
                    {
                        rc = 0;
                        for (int i = start; i < runPos.Count && rc == 0; i++)
                        {
                            rc = LevelCommand.AddGroupedInstance(into, group, memberIndex, 0, runPos[i], runRot[i], "1", logger);
                            if (rc == 0) placed++;
                        }
                    }
                    if (rc != 0) { failed++; Console.Error.WriteLine("port import-layer: instances of " + runSource + " -> " + target + " (member " + memberIndex + ") failed"); return false; }
                }
                long tBatch = clock.ElapsedMilliseconds - b0;
                logger.Log("port import-layer: {0} placed, {1} skipped, {2} member(s) added; last run {3} x {4}: member {5} ms, mvdb {6} ms, batch {7} ms, total {8} s",
                    placed, skipped, newMembers, runPos.Count, Path.GetFileName(target), tMember, tMvdb, tBatch, clock.ElapsedMilliseconds / 1000);
                return true;
            };
            foreach (PortPlacement pl in layer.Placements)
            {
                if (sourceGroup >= 0 && pl.Group != sourceGroup) continue;
                if (limit > 0 && placed + runPos.Count >= limit) break;
                considered++;
                string target = pl.Blueprint != null && resolved.ContainsKey(pl.Blueprint) ? resolved[pl.Blueprint] : null;
                if (target == null)
                {
                    skipped++;
                    string key = pl.Blueprint ?? "(null)";
                    int c; missingCounts.TryGetValue(key, out c); missingCounts[key] = c + 1;
                    continue;
                }
                if (pl.Trans == null || pl.Right == null || pl.Up == null || pl.Forward == null) { skipped++; continue; }
                if (newMembersOnly && existingMembers.Contains(target)) { alreadyPlaced++; continue; }
                string pos = string.Join(",", new[] { F(pl.Trans[0] + offset[0]), F(pl.Trans[1] + offset[1]), F(pl.Trans[2] + offset[2]) });
                string rot = string.Join(",", new[] { F(pl.Right[0]), F(pl.Right[1]), F(pl.Right[2]), F(pl.Up[0]), F(pl.Up[1]), F(pl.Up[2]), F(pl.Forward[0]), F(pl.Forward[1]), F(pl.Forward[2]) });
                if (DryRun.Active)
                {
                    int existing;
                    DryRun.Report(logger, "place {0} ({1}) at {2}{3}", target, how[pl.Blueprint], pos, memberOf.TryGetValue(target, out existing) ? " as instance of member " + existing : " as a new member");
                    if (!memberOf.ContainsKey(target)) { memberOf[target] = memberCount++; newMembers++; }
                    placed++;
                    continue;
                }
                if (runTarget != null && !string.Equals(runTarget, target, StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = flush();
                    runPos.Clear(); runRot.Clear(); runTarget = null;
                    if (!ok && !keepGoing) { aborted = true; break; }
                }
                runTarget = target; runSource = pl.Blueprint;
                runPos.Add(pos); runRot.Add(rot);
            }
            if (!aborted && !DryRun.Active && runTarget != null) { if (!flush()) failed = Math.Max(failed, 1); runPos.Clear(); runRot.Clear(); }
            // Grouped instances default to lightmapped lighting and this level has no lightmap for
            // them, so they render black; give every instance the dynamic override the entities get.
            if (!aborted && !DryRun.Active && placed > 0 && !noRadiosity)
            {
                int lit;
                string lerr = LevelCommand.SetGroupInstanceRadiosity(into, group, "RadiosityTypeOverride_Dynamic", logger, out lit);
                if (lerr != null) report.Add("could not set the instance lighting override: " + lerr);
                else if (lit > 0) report.Add(lit + " grouped instance(s) set to dynamic lighting");
            }
            if (!DryRun.Active)
            {
                long c0 = clock.ElapsedMilliseconds;
                int recompressed = ResWriter.Finish(delegate(ResAssetEntry e) { byte[] b; string err; return LevelCommand.TryReadResBytes(e, out b, out err) ? b : null; });
                if (recompressed > 0) logger.Log("port import-layer: {0} resource(s) recompressed with the shipping settings in {1} s", recompressed, (clock.ElapsedMilliseconds - c0) / 1000);
            }


            foreach (string r in report) logger.LogWarning("port import-layer: {0}", r);
            var byMethod = new Dictionary<string, int>();
            foreach (KeyValuePair<string, string> kv in how) { int c; byMethod.TryGetValue(kv.Value, out c); byMethod[kv.Value] = c + 1; }
            foreach (KeyValuePair<string, int> kv in byMethod) logger.Log("port import-layer: {0} blueprint(s) resolved by {1}", kv.Value, kv.Key);
            // With --continue a failed placement is reported but the pass still counts as a success
            // when it placed anything, so the project is saved with the work that did land.
            if (failed > 0 && keepGoing && placed > 0)
            {
                logger.LogWarning("port import-layer: {0} placement run(s) failed (see above); the other placements were kept", failed);
                failed = 0;
            }
            formatter.WriteResult(failed == 0 ? "OK" : "FAIL", "port import-layer", new Dictionary<string, string>
            {
                { "level", into }, { "group", group.ToString(CultureInfo.InvariantCulture) },
                { "placed", placed.ToString(CultureInfo.InvariantCulture) }, { "skipped", skipped.ToString(CultureInfo.InvariantCulture) },
                { "failed", failed.ToString(CultureInfo.InvariantCulture) }, { "members_added", newMembers.ToString(CultureInfo.InvariantCulture) },
                { "meshes_registered", registered.ToString(CultureInfo.InvariantCulture) }, { "entries_copied", copied.ToString(CultureInfo.InvariantCulture) },
                { "already_placed", alreadyPlaced.ToString(CultureInfo.InvariantCulture) }
            });
            var missing = new List<KeyValuePair<string, int>>(missingCounts);
            missing.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < missing.Count && i < 15; i++)
                logger.LogWarning("port import-layer: {0} placement(s) skipped: {1} has no counterpart{2}", missing[i].Value, missing[i].Key, portedPrefix == null ? " (pass --ported-prefix after `port import`)" : "");
            if (missing.Count > 15) logger.LogWarning("port import-layer: ... and {0} more blueprint(s) without a counterpart", missing.Count - 15);
            return failed == 0 ? 0 : 1;
        }

        private static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        // A MeshVariationDatabase that references the mesh, via the reverse reference index.
        // The default route: every placement becomes an ObjectReferenceObjectData entity of the level
        // (the way community map mods add props), written in one pass. Group members stay behind
        // --as-members because appending one crashes the game on load.
        private static int ImportLayerAsEntities(PortLayer layer, string into, Dictionary<string, string> resolved, Dictionary<string, string> how,
            float[] offset, int sourceGroup, int limit, string bundle, bool noBundle, string mvdb, bool newOnly, string entityClass, uint? entityFlags, string registry, ExportLogger logger, OutputFormatter formatter)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Blueprints the level already places as entities, for --new-members-only re-runs.
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (newOnly)
            {
                try
                {
                    EbxAsset levelAsset = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(into));
                    IList objects = LevelCommand.GetEntityList(levelAsset.RootObject);
                    for (int i = 0; objects != null && i < objects.Count; i++)
                    {
                        object obj = ResolveInternal(objects[i]) ?? objects[i];
                        object bpRef = obj != null ? ReadMember(obj, "Blueprint") : null;
                        object external = bpRef != null ? ReadMember(bpRef, "External") : null;
                        object fileGuid = external != null ? ReadMember(external, "FileGuid") : null;
                        EbxAssetEntry bp = fileGuid is Guid && (Guid)fileGuid != Guid.Empty ? App.AssetManager.GetEbxEntry((Guid)fileGuid) : null;
                        if (bp != null) existing.Add(bp.Name);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("port import-layer: could not read the level's entities: " + Describe(ex)); return 1; }
            }

            var placements = new List<LevelCommand.EntityPlacement>();
            var report = new List<string>();
            var missingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0, alreadyPlaced = 0, considered = 0;
            foreach (PortPlacement pl in layer.Placements)
            {
                if (sourceGroup >= 0 && pl.Group != sourceGroup) continue;
                if (limit > 0 && placements.Count >= limit) break;
                considered++;
                string target = pl.Blueprint != null && resolved.ContainsKey(pl.Blueprint) ? resolved[pl.Blueprint] : null;
                if (target == null)
                {
                    skipped++;
                    string key = pl.Blueprint ?? "(null)";
                    int c; missingCounts.TryGetValue(key, out c); missingCounts[key] = c + 1;
                    continue;
                }
                if (pl.Trans == null || pl.Right == null || pl.Up == null || pl.Forward == null) { skipped++; continue; }
                if (newOnly && existing.Contains(target)) { alreadyPlaced++; continue; }
                string pos = string.Join(",", new[] { F(pl.Trans[0] + offset[0]), F(pl.Trans[1] + offset[1]), F(pl.Trans[2] + offset[2]) });
                string rot = string.Join(",", new[] { F(pl.Right[0]), F(pl.Right[1]), F(pl.Right[2]), F(pl.Up[0]), F(pl.Up[1]), F(pl.Up[2]), F(pl.Forward[0]), F(pl.Forward[1]), F(pl.Forward[2]) });
                LinearTransformData xform;
                try { xform = TransformBuilder.Build(pos, rot, "1"); }
                catch (ArgumentException ex) { report.Add("placement of " + pl.Blueprint + " skipped: " + ex.Message); skipped++; continue; }
                if (DryRun.Active) DryRun.Report(logger, "place {0} ({1}) at {2} as an entity", target, how[pl.Blueprint], pos);
                placements.Add(new LevelCommand.EntityPlacement { Blueprint = target, Transform = xform });
            }

            // Every distinct mesh goes into the sub-world's variation database once, before the level
            // is written.
            int registered = 0, copied = 0, texturesBundled = 0;
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LevelCommand.EntityPlacement pl in placements) distinct.Add(pl.Blueprint);
            var entryTextures = new List<EbxAssetEntry>();
            var unregisteredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mvdb != null && !DryRun.Active)
                foreach (string target in distinct)
                {
                    string meshPath = BlueprintMeshPath(target);
                    if (meshPath == null) { report.Add("could not find the mesh of " + target + "; not registered in " + mvdb); continue; }
                    if (MvdbHasMesh(mvdb, meshPath))
                    {
                        EbxAssetEntry me0 = App.AssetManager.GetEbxEntry(meshPath);
                        if (me0 != null) entryTextures.AddRange(MvdbCommand.EntryTextures(App.AssetManager.GetEbxEntry(mvdb), me0.Guid));
                        continue;
                    }
                    // A shipped prop mesh keeps its textures in the level's variation database, not in
                    // the mesh (its shader instances carry no texture parameters), so an entry derived
                    // from the mesh has no textures. Copy the entry from a database that already
                    // registers the mesh, the way shipped map mods do; derive only when none does.
                    string donorDb = FindDatabaseWithMesh(meshPath);
                    if (donorDb != null)
                    {
                        int mats;
                        string cerr = MvdbCommand.CopyEntry(App.AssetManager.GetEbxEntry(mvdb), App.AssetManager.GetEbxEntry(donorDb), App.AssetManager.GetEbxEntry(meshPath), 0, logger, out mats);
                        if (cerr == null) { registered++; copied++; continue; }
                        report.Add("copying the entry for " + meshPath + " from " + donorDb + " failed (" + cerr + "); deriving one instead");
                    }
                    if (MvdbCommand.ExecuteAdd(new[] { mvdb, "--mesh", meshPath }, logger, formatter) == 0) { registered++; continue; }
                    report.Add("mvdb add failed for " + meshPath + (donorDb == null ? " and no database registers it" : "") + "; its placements were NOT placed (an unregistered mesh crashes the game at game+0xF58B89)");
                    unregisteredTargets.Add(target);
                }
            // A placed mesh without a variation-database entry crashes the game once rendered: drop those placements
            // before the level is written.
            if (unregisteredTargets.Count > 0)
            {
                int before = placements.Count;
                placements.RemoveAll(p => unregisteredTargets.Contains(p.Blueprint));
                skipped += before - placements.Count;
                distinct.ExceptWith(unregisteredTargets);
            }
            // Textures bound by the entries just written (copied or derived); a shipped prop's
            // textures are referenced from its entry, not from the mesh.
            if (mvdb != null && !DryRun.Active)
                foreach (string target in distinct)
                {
                    string meshPath = BlueprintMeshPath(target);
                    EbxAssetEntry me2 = meshPath != null ? App.AssetManager.GetEbxEntry(meshPath) : null;
                    if (me2 != null) entryTextures.AddRange(MvdbCommand.EntryTextures(App.AssetManager.GetEbxEntry(mvdb), me2.Guid));
                }
            // ... and the textures each placed mesh's shader block depot binds by instance guid (LevelCommand.DepotTextures).
            // A ported mesh's depot starts as its template's, so it can bind textures that neither an EBX dependency nor
            // the variation entry shows (Hoth G1: a failed XWing_01 import kept xwing_01_mesh's depot and its
            // T_XWingCockpitWindowDirt_02_C, unbundled -> game+0x1522B97 while loading).
            int depotTextures = 0;
            if (!DryRun.Active)
                foreach (string target in distinct)
                {
                    string meshPath = BlueprintMeshPath(target);
                    EbxAssetEntry me3 = meshPath != null ? App.AssetManager.GetEbxEntry(meshPath) : null;
                    if (me3 == null) continue;
                    int d;
                    List<EbxAssetEntry> dt = LevelCommand.DepotTextures(me3, out d);
                    depotTextures += dt.Count;
                    entryTextures.AddRange(dt);
                }
            { var seenTex = new HashSet<Guid>(); entryTextures = entryTextures.Where(t => seenTex.Add(t.Guid)).ToList(); }
            long tMvdb = clock.ElapsedMilliseconds;

            int added = 0; List<string> notes = new List<string>();
            int rc = placements.Count > 0 ? LevelCommand.AddBlueprintEntities(into, placements, bundle, noBundle, entityClass, entityFlags, registry, logger, out added, out notes) : 0;
            // Shipped map mods bundle the textures their props' entries bind; the dependency closure
            // of a shipped mesh never reaches them.
            if (rc == 0 && !noBundle && entryTextures.Count > 0 && !DryRun.Active)
            {
                string berr;
                string summary = LevelCommand.BundleAssets(into, bundle, entryTextures, out berr);
                if (summary != null) { notes.Add("entry textures: " + summary); texturesBundled = entryTextures.Count; }
                else report.Add("could not bundle the entry textures: " + berr);
            }
            foreach (string n in notes) logger.Log("port import-layer: bundled {0}", n);
            foreach (string r in report) logger.LogWarning("port import-layer: {0}", r);
            var byMethod = new Dictionary<string, int>();
            foreach (KeyValuePair<string, string> kv in how) { int c; byMethod.TryGetValue(kv.Value, out c); byMethod[kv.Value] = c + 1; }
            foreach (KeyValuePair<string, int> kv in byMethod) logger.Log("port import-layer: {0} blueprint(s) resolved by {1}", kv.Value, kv.Key);
            if (missingCounts.Count > 0)
            {
                var missing = new List<KeyValuePair<string, int>>(missingCounts);
                missing.Sort((a, b) => b.Value.CompareTo(a.Value));
                int shown = 0;
                foreach (KeyValuePair<string, int> kv in missing) { if (shown++ >= 10) break; logger.Log("port import-layer: unresolved {0} x {1}", kv.Value, kv.Key); }
            }
            if (rc != 0) { Console.Error.WriteLine("port import-layer: entity placement failed; nothing was written"); return rc; }
            formatter.WriteResult("OK", "port import-layer", new Dictionary<string, string>
            {
                { "level", into },
                { "route", "entities" },
                { "entity_class", entityClass ?? LevelCommand.DefaultEntityClass },
                { "flags", (entityFlags ?? LevelCommand.DefaultEntityFlags).ToString(CultureInfo.InvariantCulture) },
                { "considered", considered.ToString(CultureInfo.InvariantCulture) },
                { "placed", added.ToString(CultureInfo.InvariantCulture) },
                { "blueprints", distinct.Count.ToString(CultureInfo.InvariantCulture) },
                { "skipped", skipped.ToString(CultureInfo.InvariantCulture) },
                { "already_placed", alreadyPlaced.ToString(CultureInfo.InvariantCulture) },
                { "mvdb_registered", registered.ToString(CultureInfo.InvariantCulture) },
                { "mvdb_copied", copied.ToString(CultureInfo.InvariantCulture) },
                { "entry_textures_bundled", texturesBundled.ToString(CultureInfo.InvariantCulture) },
                { "depot_textures", depotTextures.ToString(CultureInfo.InvariantCulture) },
                { "mvdb_ms", tMvdb.ToString(CultureInfo.InvariantCulture) },
                { "seconds", (clock.ElapsedMilliseconds / 1000).ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        internal static string FindDatabaseWithMesh(string meshPath)
        {
            try
            {
                EbxAssetEntry mesh = App.AssetManager.GetEbxEntry(meshPath);
                if (mesh == null) return null;
                List<string> users;
                if (!IndexService.GetRefIndex().Refs.TryGetValue(mesh.Guid.ToString("D"), out users)) return null;
                foreach (string u in users)
                {
                    EbxAssetEntry e = App.AssetManager.GetEbxEntry(u);
                    if (e != null && e.Type == "MeshVariationDatabase") return e.Name;
                }
            }
            catch { }
            return null;
        }

        // The mesh a static blueprint's entity renders.
        internal static string BlueprintMeshPath(string blueprint)
        {
            try
            {
                EbxAssetEntry bp = App.AssetManager.GetEbxEntry(blueprint);
                if (bp == null) return null;
                object entity = ResolveInternal(ReadMember(App.AssetManager.GetEbx(bp).RootObject, "Object"));
                Guid g, c;
                if (!TryExternal(ReadMember(entity, "Mesh"), out g, out c)) return null;
                EbxAssetEntry mesh = App.AssetManager.GetEbxEntry(g);
                return mesh != null ? mesh.Name : null;
            }
            catch { return null; }
        }

        internal static bool MvdbHasMesh(string mvdb, string meshPath)
        {
            try
            {
                EbxAssetEntry mesh = App.AssetManager.GetEbxEntry(meshPath);
                EbxAssetEntry db = App.AssetManager.GetEbxEntry(mvdb);
                if (mesh == null || db == null) return false;
                IList entries = MvdbCommand.FindEntries(App.AssetManager.GetEbx(db).RootObject);
                return entries != null && MvdbCommand.EntryExists(entries, mesh.Guid, 0);
            }
            catch { return false; }
        }

        internal const string DefaultBlueprintTemplate = "Objects/Props/ObjectSets/Bespin/CryoChamber_Pipe_01/CryoChamber_MetalPipe_S64_01";
        internal const string DefaultShaderFallback = "Shaders/Presets/SS_PropsMetallicPreset";
        internal const string ShadowMeshShader = "Shaders/Presets/SS_Shadowmesh";
        internal const string DefaultTextureTemplate = "Objects/Props/ObjectSets/Bespin/_CommonTextures/T_MetalBase_01_CS";
        internal const string DefaultLinearTextureTemplate = "Objects/Props/ObjectSets/Bespin/_CommonTextures/T_MetalBumpsDetail_01_NS";

        // Picks a target-game mesh referencing one of the resolved shaders, preferring equal LOD and
        // material counts, via the reverse reference index.
        // Ranks target-game meshes as clone templates: same asset type always (a composite FBX does
        // not import into a rigid MeshSet); first meshes using a kept shader with the same LOD and
        // material count, then same LODs, then more LODs (trimmed), then any structural match.
        // --template-prefix: only meshes under this path may serve as templates. A clone carries its
        // template's shader permutations, and those must exist in the target level's RVM database;
        // restricting templates to the level's own content (s2/ for Cloud City) guarantees it.
        private static string s_templatePrefix = null;
        private static HashSet<string> s_templateList = null;
        private static string s_wordMapPath = null;

        private static bool TemplateAllowed(string name)
        {
            // never clone DICE's *_Occluder stand-ins: the Hoth 2015 ContainerHuge_01 port cloned from
            // Container_L_01_Occluder_Mesh drew nothing (71 placements, collision intact) even on drawable donors
            if (FrostyCLI.Adapters.Mesh.SurfaceDonors.IsOccluderStandIn(name)) return false;
            if (s_templateList != null && !s_templateList.Contains(name)) return false;
            return string.IsNullOrEmpty(s_templatePrefix) || name.StartsWith(s_templatePrefix, StringComparison.OrdinalIgnoreCase);
        }

        // Closeness of a loosely matching template: material-count difference first, then extra LODs.
        private static int LooseDistance(string templateName, PortMesh mesh)
        {
            try
            {
                EbxAssetEntry ue = App.AssetManager.GetEbxEntry(templateName);
                int lods; List<List<string>> n; List<List<int>> mm;
                FrostyCLI.Adapters.Mesh.MeshCloner.DescribeSections(ue, out lods, out n, out mm);
                IList mats = ReadList(App.AssetManager.GetEbx(ue).RootObject, "Materials");
                int matCount = mats != null ? mats.Count : 0;
                return Math.Abs(matCount - mesh.Materials.Count) * 10 + Math.Max(0, lods - mesh.LodCount);
            }
            catch { return 1000; }
        }

        private static List<KeyValuePair<string, string>> PickMeshTemplates(PortMesh mesh, string templateType, List<EbxAssetEntry> shaders, string preferUnder, out string why)
        {
            var exact = new List<KeyValuePair<string, string>>();
            var lodOnly = new List<KeyValuePair<string, string>>();
            var moreLods = new List<KeyValuePair<string, string>>();
            var structural = new List<KeyValuePair<string, string>>();
            var structuralLoose = new List<KeyValuePair<string, string>>();
            why = "no reference index";
            RefIndex index = null;
            try { index = IndexService.GetRefIndex(); }
            catch (Exception ex) { why = "reference index unavailable: " + ex.Message; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lodCounts = new List<string>();
            int subMaterial = 0;
            var skippedNames = new List<string>();
            if (index != null)
                foreach (EbxAssetEntry se in shaders)
                {
                    List<string> users;
                    if (se == null || !index.Refs.TryGetValue(se.Guid.ToString("D"), out users)) continue;
                    foreach (string user in users)
                    {
                        if (!seen.Add(user)) continue;
                        EbxAssetEntry ue = App.AssetManager.GetEbxEntry(user);
                        if (ue == null || ue.Type != templateType || !TemplateAllowed(ue.Name)) continue;
                        int lods; List<List<string>> n; List<List<int>> mm;
                        if (FrostyCLI.Adapters.Mesh.MeshCloner.DescribeSections(ue, out lods, out n, out mm) != null) continue;
                        if (FrostyCLI.Adapters.Mesh.MeshCloner.HasVertexUsage(ue, "SubMaterialIndex") == true) { subMaterial++; if (skippedNames.Count < 4) skippedNames.Add(Path.GetFileName(ue.Name)); continue; }
                        // Frosty's FBX importer cannot write DisplacementMapTexCoord, so a template carrying it fails the import.
                        if (FrostyCLI.Adapters.Mesh.MeshCloner.HasVertexUsage(ue, "DisplacementMapTexCoord") == true) continue;
                        int matCount;
                        try { IList mats = ReadList(App.AssetManager.GetEbx(ue).RootObject, "Materials"); matCount = mats != null ? mats.Count : 0; } catch { continue; }
                        if (lodCounts.Count < 6) lodCounts.Add(Path.GetFileName(ue.Name) + ":" + lods + "L/" + matCount + "M");
                        if (lods == mesh.LodCount)
                        {
                            if (matCount == mesh.Materials.Count) exact.Add(new KeyValuePair<string, string>(ue.Name, "uses shader " + se.Name + ", same LOD and material count"));
                            else lodOnly.Add(new KeyValuePair<string, string>(ue.Name, "uses shader " + se.Name + ", same LOD count, materials are added"));
                        }
                        else if (lods > mesh.LodCount)
                            moreLods.Add(new KeyValuePair<string, string>(ue.Name, "uses shader " + se.Name + "; " + lods + " LOD(s) trimmed to " + mesh.LodCount + (matCount == mesh.Materials.Count ? "" : ", materials are added")));
                    }
                }
            // Structural fallbacks: the same LOD and material counts, first under the object set the
            // prop goes into, then anywhere (bounded scan).
            string prefix = FolderOf(preferUnder);
            int budget = 600;
            foreach (int pass in new[] { 0, 1 })
                foreach (EbxAssetEntry ue in App.AssetManager.EnumerateEbx())
                {
                    if (ue.Type != templateType || !TemplateAllowed(ue.Name)) continue;
                    bool under = prefix.Length > 0 && ue.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    if (pass == 0 ? !under : under) continue;
                    if (!seen.Add(ue.Name)) continue;
                    if (--budget < 0) break;
                    int lods; List<List<string>> n; List<List<int>> mm;
                    if (FrostyCLI.Adapters.Mesh.MeshCloner.DescribeSections(ue, out lods, out n, out mm) != null || lods < mesh.LodCount) continue;
                    int matCount;
                    try { IList mats = ReadList(App.AssetManager.GetEbx(ue).RootObject, "Materials"); matCount = mats != null ? mats.Count : 0; } catch { continue; }
                    // Extra template LODs are trimmed and missing material slots are grown; a template
                    // with more materials than the source has also imported fine (explicit
                    // --mesh-template ul_wall_01_128_mesh for a 1-material source, verified in game).
                    if (FrostyCLI.Adapters.Mesh.MeshCloner.HasVertexUsage(ue, "SubMaterialIndex") == true) { subMaterial++; continue; }
                    if (FrostyCLI.Adapters.Mesh.MeshCloner.HasVertexUsage(ue, "DisplacementMapTexCoord") == true) continue;
                    bool exactFit = lods == mesh.LodCount && matCount == mesh.Materials.Count;
                    var kv = new KeyValuePair<string, string>(ue.Name, exactFit ? "same LOD and material count (no shader match)"
                        : "no shader match; " + lods + " LOD(s) -> " + mesh.LodCount + ", " + matCount + " material(s) -> " + mesh.Materials.Count);
                    if (exactFit) structural.Insert(0, kv); else structuralLoose.Add(kv);
                    if (structural.Count >= 3) break;
                }
            // Trimming LODs is the least safe transformation (composite meshes have failed to import
            // after it), so a structural match with the right LOD count outranks a same-shader trim.
            var ranked = new List<KeyValuePair<string, string>>();
            structuralLoose.Sort((a, b2) => LooseDistance(a.Key, mesh).CompareTo(LooseDistance(b2.Key, mesh)));
            ranked.AddRange(exact); ranked.AddRange(lodOnly); ranked.AddRange(structural); ranked.AddRange(moreLods); ranked.AddRange(structuralLoose);
            if (ranked.Count > 0 && ranked[0].Value.IndexOf("no shader match", StringComparison.Ordinal) >= 0 && subMaterial > 0)
                ranked[0] = new KeyValuePair<string, string>(ranked[0].Key, ranked[0].Value + "; " + subMaterial + " same-shader mesh(es) skipped for per-vertex SubMaterialIndex: " + string.Join(", ", skippedNames));
            if (ranked.Count == 0)
                why = (lodCounts.Count > 0 ? "same-shader meshes have too few LODs (" + string.Join(", ", lodCounts) + "); " : "") +
                      (subMaterial > 0 ? subMaterial + " candidate(s) skipped for per-vertex SubMaterialIndex; " : "") +
                      "no " + templateType + " with " + mesh.LodCount + " LOD(s) and " + mesh.Materials.Count + " material(s) found";
            return ranked;
        }

        // DX10 DDS headers name the DXGI format; the sRGB variants are the ones the texture importer
        // will accept into an sRGB template. Legacy headers carry no colour-space flag: treat as linear.
        private static bool DdsIsSrgb(string file)
        {
            try
            {
                byte[] b = new byte[148];
                using (FileStream fs = File.OpenRead(file)) { int n = fs.Read(b, 0, b.Length); if (n < 132) return false; }
                if (b[0] != (byte)'D' || b[1] != (byte)'D' || b[2] != (byte)'S' || b[3] != (byte)' ') return false;
                if (b[84] != (byte)'D' || b[85] != (byte)'X' || b[86] != (byte)'1' || b[87] != (byte)'0') return false;
                uint dxgi = BitConverter.ToUInt32(b, 128);
                switch (dxgi) { case 29: case 72: case 75: case 78: case 91: case 93: case 99: return true; default: return false; }
            }
            catch { return false; }
        }

        private static string FolderOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string[] parts = path.Split('/');
            // Objects/Props/ObjectSets/<set>/... -> keep the object-set root.
            int keep = Math.Min(parts.Length - 1, 4);
            return string.Join("/", parts, 0, keep);
        }


        /// <summary>Whether two texture asset paths name the same texture (their last path element, case-insensitive).</summary>
        internal static bool SameTextureName(string a, string b)
        {
            if (a == null || b == null) return false;
            string la = a.Substring(a.LastIndexOf('/') + 1), lb = b.Substring(b.LastIndexOf('/') + 1);
            return string.Equals(la, lb, StringComparison.OrdinalIgnoreCase);
        }

        // Texture step of `port import` / `port regeo`: every texture a material names, reused by GUID (or by name under the
        // destination folder), else imported from the package's DDS as a clone of a colour-space-matching template.
        internal static int ResolveTextures(PortManifest m, string dir, string asDir, string texTemplate, List<string> report, List<string> created,
            Dictionary<string, EbxAssetEntry> texTarget, ref int reusedTex, ref int newTex, ExportLogger logger, OutputFormatter formatter)
        {
            foreach (PortMaterial pm in m.Mesh.Materials)
                foreach (PortTexture t in TexturesOf(pm))
                {
                    if (t.Guid == null || texTarget.ContainsKey(t.Guid)) continue;
                    EbxAssetEntry existing = App.AssetManager.GetEbxEntry(Guid.Parse(t.Guid));
                    // Same file GUID is the same texture only if DICE kept its name: a renamed one was repacked for BFII's
                    // presets (2015 T_FloorBoards_01_C -> BFII T_FloorBoards_01_CS, _N -> _NAM: colour+smoothness, normal/AO/metal)
                    // and read through the 2015 surface it gave the Hoth 2015 floor boards DICE's dark paint and a misread
                    // normal. Such a texture is imported from the package's DDS instead (reused only if the package has none).
                    if (existing != null && !SameTextureName(existing.Name, t.Path) && t.File != null && File.Exists(Path.Combine(dir, t.File)))
                    {
                        report.Add("texture " + t.Param + ": " + existing.Name + " has the source's GUID but another name (DICE repacked it); importing the source's " + Path.GetFileName(t.Path ?? t.File));
                        existing = null;
                    }
                    if (existing != null && !CampaignOnly(existing)) { texTarget[t.Guid] = existing; reusedTex++; continue; }
                    // a texture BFII ships only in campaign bundles (the 2015 X-wing's landing gear, Xwing/Old): an MP level does not
                    // mount those install chunks, so the package's own DDS is imported as a level texture instead
                    if (existing != null) report.Add("texture " + existing.Name + " ships only in campaign bundles; imported from the package's DDS");
                    string newName = asDir + "/" + Path.GetFileName(t.Path ?? Path.GetFileNameWithoutExtension(t.File ?? "texture"));
                    EbxAssetEntry already = App.AssetManager.GetEbxEntry(newName);
                    if (already != null) { texTarget[t.Guid] = already; reusedTex++; continue; }
                    if (t.File == null || !File.Exists(Path.Combine(dir, t.File))) { report.Add("texture " + t.Param + " (" + t.Path + ") has no DDS in the package; skipped"); continue; }
                    bool srgb = DdsIsSrgb(Path.Combine(dir, t.File));
                    string template = null;
                    if (texTemplate != null)
                    {
                        bool? ts = FrostyCLI.Adapters.Texture.HeadlessTextureImporter.IsSrgb(App.AssetManager.GetEbxEntry(texTemplate));
                        if (ts == null || ts.Value == srgb) template = texTemplate;
                        else report.Add("--texture-template " + texTemplate + " is " + (ts.Value ? "sRGB" : "linear") + " but " + t.Path + " is not; using the built-in template");
                    }
                    if (template == null)
                        foreach (EbxAssetEntry e in texTarget.Values)
                        {
                            bool? es = FrostyCLI.Adapters.Texture.HeadlessTextureImporter.IsSrgb(e);
                            if (es != null && es.Value == srgb) { template = e.Name; break; }
                        }
                    if (template == null) template = srgb ? DefaultTextureTemplate : DefaultLinearTextureTemplate;
                    if (DryRun.Active) { DryRun.Report(logger, "import texture {0} as {1} (template {2}, {3})", t.File, newName, template, srgb ? "sRGB" : "linear"); newTex++; continue; }
                    int rc = ImportCommand.ExecuteTexture(new[] { Path.Combine(dir, t.File), "--as", newName, "--template", template }, logger, formatter);
                    if (rc != 0) { Console.Error.WriteLine("port import: texture import failed for " + t.Path); return 1; }
                    EbxAssetEntry ne = App.AssetManager.GetEbxEntry(newName);
                    if (ne == null) { Console.Error.WriteLine("port import: texture " + newName + " was not created"); return 1; }
                    texTarget[t.Guid] = ne; created.Add(newName); newTex++;
                }
            return 0;
        }

        // A "shader-default" texture (port export: the source graph's own texture for a parameter the material leaves unset)
        // is for a material whose graph the target game lacks: the fallback preset has no art of its own and draws black.
        // Where the source graph is kept (same GUID or same name) it binds that texture itself, so the default is dropped
        // rather than imported as a redundant 2015 copy.
        //
        // "shader-constant" textures (the graph's own parameterless art, Param = packing family) are bound the same way: only
        // on a fallback, by family onto parameters the preset has -- CS / NW on SS_PropsNonMetallicPreset (BFII's preset
        // takes the 2015 families unchanged, so a CS/NW graph falls back to it rather than the metallic default), C / N / MSW
        // as BaseColor / Normal / MSW on the metallic presets; any other constant, or any constant on another fallback, is
        // dropped. An explicit parameter always wins.
        internal const string NonMetallicPreset = "Shaders/Presets/SS_PropsNonMetallicPreset";

        internal static void DropKeptGraphDefaults(PortManifest m, List<EbxAssetEntry> shaderTarget, List<string> report)
        {
            for (int i = 0; i < m.Mesh.Materials.Count && i < shaderTarget.Count; i++)
            {
                PortMaterial pm = m.Mesh.Materials[i];
                if (!pm.Textures.Any(t => t.Source == "shader-default" || t.Source == "shader-constant")) continue;
                if (KeepsSourceGraph(pm, shaderTarget[i]))
                {
                    pm.Textures.RemoveAll(t => t.Source == "shader-default" || t.Source == "shader-constant");
                    continue;
                }
                var constants = pm.Textures.Where(t => t.Source == "shader-constant").ToList();
                pm.Textures.RemoveAll(t => t.Source == "shader-constant");
                if (constants.Any(t => t.Param == "CS") && !string.Equals(shaderTarget[i].Name, NonMetallicPreset, StringComparison.OrdinalIgnoreCase))
                {
                    EbxAssetEntry nm = App.AssetManager != null ? App.AssetManager.GetEbxEntry(NonMetallicPreset) : null;
                    if (nm != null)
                    {
                        report.Add("material " + i + ": the source graph's own art is CS/NW; falling back to " + nm.Name + " instead of " + shaderTarget[i].Name);
                        shaderTarget[i] = nm;
                    }
                }
                Dictionary<string, string> map = FamilyMap(shaderTarget[i].Name);
                int bound = 0;
                foreach (PortTexture c in constants)
                {
                    string param;
                    if (map == null || !map.TryGetValue(c.Param ?? "", out param) || pm.Textures.Any(t => t.Param == param)) continue;
                    pm.Textures.Add(new PortTexture { Param = param, Path = c.Path, Guid = c.Guid, File = c.File, Source = "shader-constant" });
                    bound++;
                }
                int defaults = pm.Textures.Count(t => t.Source == "shader-default");
                if (defaults + bound > 0)
                    report.Add("material " + i + ": " + (defaults + bound) + " texture(s) from the source graph (" + defaults + " parameter default(s), " + bound + " own constant(s)) bound on " + shaderTarget[i].Name);
            }
        }

        // packing family -> parameter, per fallback preset family; null = constants are not bound on that preset
        internal static Dictionary<string, string> FamilyMap(string preset)
        {
            string n = preset.Substring(preset.LastIndexOf('/') + 1);
            if (n.StartsWith("SS_PropsNonMetallicPreset", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "CS", "CS" }, { "NW", "NW" } };
            if (n.StartsWith("SS_PropsMetallicPreset", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "C", "BaseColor" }, { "N", "Normal" }, { "MSW", "MSW" } };
            return null;
        }

        internal static bool KeepsSourceGraph(PortMaterial pm, EbxAssetEntry target)
        {
            if (target == null) return false;
            if (pm.ShaderGuid != null && string.Equals(target.Guid.ToString("D"), pm.ShaderGuid, StringComparison.OrdinalIgnoreCase)) return true;
            if (pm.ShaderPath == null) return false;
            string a = pm.ShaderPath.Substring(pm.ShaderPath.LastIndexOf('/') + 1), b = target.Name.Substring(target.Name.LastIndexOf('/') + 1);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // Shader step: the source shader by GUID, else by basename (DICE moved object-set shaders but kept their names; memory
        // carbonite-material-root-cause), else the fallback.
        /// <summary>
        /// Source shader path -> BFII shader path, consulted before any other rule (--shader-map file.json). For 2015 surfaces BFII
        /// ships only in another form: DICE's own static AT-AT wreck draws the 2015 vehicle shaders SS_ATAT_Legs_01 and
        /// SS_ATATPreset_Weathering_01 through its static-prop copies SS_ATAT_Legs_02 / SS_ATATPreset_Weathering_02.
        /// </summary>
        internal static Dictionary<string, string> ShaderMap;

        /// <summary>Per mapped source shader: the texture parameters DICE's own BFII version of the same model binds (param ->
        /// texture path). A 2015 custom graph BFII does not ship is drawn the way DICE draws that model in BFII: its surface
        /// and its textures (Atgar14FDPTower_01: same topology as DICE's Atgar14FDPTower_Static_01, which draws
        /// SS_PropsMetallicPreset_1UV_TextureArray_01 with T_Atgar14FDPTower_CS / _NAM). The source's own parameters, which name
        /// inputs of the graph BFII lacks, are dropped.</summary>
        internal static Dictionary<string, Dictionary<string, string>> ShaderMapTextures;

        internal static void LoadShaderMap(string file)
        {
            ShaderMap = null; ShaderMapTextures = null;
            if (string.IsNullOrEmpty(file)) return;
            var raw = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file));
            ShaderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ShaderMapTextures = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw)
            {
                if (kv.Value.Type == Newtonsoft.Json.Linq.JTokenType.String) { ShaderMap[kv.Key] = (string)kv.Value; continue; }
                ShaderMap[kv.Key] = (string)kv.Value["shader"];
                var tex = kv.Value["textures"] as Newtonsoft.Json.Linq.JObject;
                if (tex != null)
                {
                    var d = new Dictionary<string, string>();
                    foreach (var t in tex) d[t.Key] = (string)t.Value;
                    ShaderMapTextures[kv.Key] = d;
                }
            }
        }

        internal static int ResolveShaders(PortManifest m, string shaderFallback, List<string> report, List<EbxAssetEntry> shaderTarget, ref int shaderKept, ref int shaderFallen)
        {
            Dictionary<string, List<EbxAssetEntry>> byName = null;
            foreach (PortMaterial pm in m.Mesh.Materials)
            {
                string mapped;
                if (ShaderMap != null && pm.ShaderPath != null && ShaderMap.TryGetValue(pm.ShaderPath, out mapped))
                {
                    EbxAssetEntry me = App.AssetManager.GetEbxEntry(mapped);
                    if (me == null) { Console.Error.WriteLine("port: --shader-map target not in this game: " + mapped); return 1; }
                    shaderKept++; shaderTarget.Add(me);
                    report.Add("material " + pm.Index + ": " + pm.ShaderPath + " -> " + me.Name + " (--shader-map)");
                    Dictionary<string, string> mt;
                    if (ShaderMapTextures != null && ShaderMapTextures.TryGetValue(pm.ShaderPath, out mt))
                    {
                        pm.Textures.Clear(); pm.Vectors.Clear(); pm.Bools.Clear(); pm.Conditionals.Clear();
                        foreach (var t in mt)
                        {
                            EbxAssetEntry te = App.AssetManager.GetEbxEntry(t.Value);
                            if (te == null) { Console.Error.WriteLine("port: --shader-map texture not in this game: " + t.Value); return 1; }
                            pm.Textures.Add(new PortTexture { Param = t.Key, Guid = te.Guid.ToString("D"), Path = te.Name });
                        }
                        report.Add("material " + pm.Index + ": the source graph's parameters replaced by " + mt.Count + " texture(s) of DICE's version of the model (--shader-map)");
                    }
                    continue;
                }
                EbxAssetEntry se = pm.ShaderGuid != null ? App.AssetManager.GetEbxEntry(Guid.Parse(pm.ShaderGuid)) : null;
                if (se == null && pm.ShaderGuid == null && pm.ShaderPath == null)
                {
                    // A 2015 material with no shader at all: the pipeline's shadow-proxy material (every such slot in the Hoth
                    // packages is used only by shadow-only sections or by never-drawn helper stubs). BFII names that material
                    // explicitly: Shaders/Presets/SS_Shadowmesh (memory shadowmesh-sections-lost-in-port).
                    se = App.AssetManager.GetEbxEntry(ShadowMeshShader);
                    if (se != null)
                    {
                        shaderKept++; shaderTarget.Add(se);
                        report.Add("material " + pm.Index + " has no shader in the source (shadow/helper geometry): " + ShadowMeshShader);
                        continue;
                    }
                }
                if (se == null && pm.ShaderPath != null)
                {
                    if (byName == null)
                    {
                        byName = new Dictionary<string, List<EbxAssetEntry>>(StringComparer.OrdinalIgnoreCase);
                        foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                        {
                            if (e.Type != "ShaderGraph") continue;
                            string n = e.Name.Substring(e.Name.LastIndexOf('/') + 1);
                            List<EbxAssetEntry> l;
                            if (!byName.TryGetValue(n, out l)) byName[n] = l = new List<EbxAssetEntry>();
                            l.Add(e);
                        }
                    }
                    List<EbxAssetEntry> cands;
                    string bn = pm.ShaderPath.Substring(pm.ShaderPath.LastIndexOf('/') + 1);
                    if (byName.TryGetValue(bn, out cands) && cands.Count > 0)
                    {
                        cands.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                        se = cands[0];
                        report.Add("material " + pm.Index + ": shader " + pm.ShaderPath + " is not in this game by GUID; same name " + se.Name + (cands.Count > 1 ? " (first of " + cands.Count + ")" : ""));
                    }
                }
                if (se != null) shaderKept++;
                else
                {
                    se = App.AssetManager.GetEbxEntry(shaderFallback);
                    if (se == null) { Console.Error.WriteLine("port import: shader fallback not found: " + shaderFallback); return 1; }
                    shaderFallen++;
                    report.Add("material " + pm.Index + ": shader " + (pm.ShaderPath ?? pm.ShaderGuid) + " is not in this game; using " + shaderFallback);
                }
                shaderTarget.Add(se);
            }
            return 0;
        }

        internal static string EnsureMaterialCountPublic(EbxAsset asset, IList materials, int count) { return EnsureMaterialCount(asset, materials, count); }

        // Grows the mesh's Materials list to <count> by duplicating its last internal material.
        private static string EnsureMaterialCount(EbxAsset asset, IList materials, int count)
        {
            if (materials.Count >= count) return null;
            if (materials.Count == 0) return "template mesh has no materials to duplicate";
            object last = ResolveInternal(materials[materials.Count - 1]);
            if (last == null) return "template material is not an internal object";
            MethodInfo setGuid = last.GetType().GetMethod("SetInstanceGuid");
            while (materials.Count < count)
            {
                object copy;
                try { copy = Activator.CreateInstance(last.GetType()); }
                catch (Exception ex) { return "could not construct " + last.GetType().Name + ": " + ex.Message; }
                // Deep copy: a shallow copy shares the template material's Shader struct (and its parameter lists)
                // with every added material, so setting one material's shader sets them all -- the last one wins
                // (Hoth HangarSystem_01_CombinedforE3: Floor, Alcove and Snowtrim all came out as Snowtrim).
                foreach (PropertyInfo pi in last.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                    try { pi.SetValue(copy, DeepCopyEbxValue(pi.GetValue(last))); } catch { }
                }
                int internalId = asset.Objects is ICollection c ? c.Count : materials.Count;
                try { if (setGuid != null) setGuid.Invoke(copy, new object[] { new AssetClassGuid(Guid.NewGuid(), internalId) }); }
                catch (Exception ex) { return "could not assign an instance GUID: " + ex.Message; }
                try { asset.AddObject(copy, false); InProcessObjects.Mark(copy); }
                catch (Exception ex) { return "could not register the new material: " + ex.Message; }
                materials.Add(new PointerRef(copy));
            }
            return null;
        }

        // ------------------------------------------------------------------ helpers

        // Copies an EBX property value so the copy shares no mutable state with the original: EBX structs
        // (reference types in the SDK) and lists are rebuilt member by member; value types, strings and
        // pointer references (structs) are copied as they are. Object references stay references.
        internal static object DeepCopyEbxValue(object v)
        {
            if (v == null) return null;
            Type t = v.GetType();
            if (t.IsValueType || v is string || t.Name == "CString") return v;
            if (v is IList list)
            {
                IList copy;
                try { copy = (IList)Activator.CreateInstance(t); } catch { return v; }
                foreach (object e in list) copy.Add(DeepCopyEbxValue(e));
                return copy;
            }
            if (t.Namespace == null || !t.Namespace.StartsWith("FrostySdk.Ebx", StringComparison.Ordinal)) return v;
            if (t.GetMethod("GetInstanceGuid") != null) return v;   // an EBX object, not a struct: keep the reference
            object o;
            try { o = Activator.CreateInstance(t); } catch { return v; }
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                try { pi.SetValue(o, DeepCopyEbxValue(pi.GetValue(v))); } catch { }
            }
            return o;
        }


        internal static bool TryExternal(object pointerRef, out Guid fileGuid, out Guid classGuid)
        {
            fileGuid = Guid.Empty; classGuid = Guid.Empty;
            if (pointerRef == null) return false;
            object external = ReadMember(pointerRef, "External");
            if (external == null) return false;
            object fg = ReadMember(external, "FileGuid"), cg = ReadMember(external, "ClassGuid");
            if (!(fg is Guid) || (Guid)fg == Guid.Empty) return false;
            fileGuid = (Guid)fg;
            if (cg is Guid) classGuid = (Guid)cg;
            return true;
        }

        private static Guid InstanceGuidOf(object obj)
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

        private static float[] ReadVec3(object v)
        {
            if (v == null) return null;
            try { return new[] { Convert.ToSingle(ReadMember(v, "x")), Convert.ToSingle(ReadMember(v, "y")), Convert.ToSingle(ReadMember(v, "z")) }; }
            catch { return null; }
        }

        private static byte[] ParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new byte[0];
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static object ResolveInternal(object value)
        {
            if (value == null) return null;
            if (value.GetType().Name != "PointerRef") return value;
            return ReadMember(value, "Internal");
        }

        private static IList ReadList(object value, string name) { return ReadMember(value, name) as IList; }

        private static object ReadMember(object value, string name)
        {
            if (value == null) return null;
            Type type = value.GetType();
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null) return property.GetValue(value);
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(value);
            }
            catch { }
            return null;
        }

        private static string Describe(Exception ex)
        {
            return ex.InnerException != null ? ex.InnerException.Message : ex.Message;
        }
    
        // `port convert-physics <swbf1 physics.bin> --wordmap <map.json> --out <file>`: a SWBF 2015 HavokPhysicsData
        // resource rewritten for BFII in place (same length, so the source's own cookie stays valid): material words
        // through the 2015 -> BFII map, root-compound records given BFII's 0x20 flag. See
        // HavokPhysicsResource.ConvertFromSwbf1.
        private static int ExecuteConvertPhysics(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "port convert-physics";
            string input = null, mapPath = null, outPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--wordmap" && i + 1 < args.Length) mapPath = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (!args[i].StartsWith("--")) input = args[i];
            }
            if (input == null || mapPath == null || outPath == null)
            { Console.Error.WriteLine(label + ": usage: port convert-physics <physics.bin> --wordmap <map.json> --out <file>"); return 1; }
            HavokPhysicsResource.Swbf1ConvertReport rep;
            byte[] converted;
            try
            {
                var map = HavokPhysicsResource.ReadWordMap(System.IO.File.ReadAllText(mapPath));
                converted = HavokPhysicsResource.ConvertFromSwbf1(System.IO.File.ReadAllBytes(input), map, out rep);
            }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + ex.Message); return 1; }
            System.IO.File.WriteAllBytes(outPath, converted);
            formatter.WriteResult(rep.MaterialWordsUnmapped > 0 ? "WARN" : "OK", label, new Dictionary<string, string>
            {
                { "out", outPath },
                { "material words", rep.MaterialWordsMapped + " of " + rep.MaterialWords + " mapped" +
                    (rep.MaterialWordsUnmapped > 0 ? ", UNMAPPED 2015 words: " + string.Join(" ", rep.Unmapped) : "") },
                { "root records", rep.RootRecordsFlagged + " of " + rep.RootRecords + " given the 0x20 flag" },
                { "nested records", rep.NestedRecords + " (left as they are)" },
            });
            return rep.MaterialWordsUnmapped > 0 ? 2 : 0;
        }
    }
}
