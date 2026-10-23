using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // `port import-group <group.json> --into <partition> --template <asset>[#<group>] --physics <file>
    //                    [--physics-meta <hex>] [--res <name>] [--health-base <n>] [--bundle <name>|--no-bundle]`
    //
    // A whole SWBF 2015 static model group rebuilt as ONE new BFII group, laid out the way DICE's pipeline lays a group out,
    // instead of appending its placements member by member to a host group. What that buys:
    //  - the group's collision is 2015's own resource (converted by `port convert-physics`: BFII material words, root
    //    record flags), every instance, tree, part and character slot exactly as 2015 built it;
    //  - each member keeps its REAL blueprint as MemberType -- DICE's own members do (Hoth_01 HeroArena:
    //    Corridor_01_DoorSideEntrance carries PartComponentData + StaticModelPhysicsComponentData + health), where the
    //    member-append route swapped in a stripped PartComponentData-only stub;
    //  - part tables, health-state ids, network ranges, per-detail boxes and raycast material lookups come from the
    //    2015 group, not reconstructed per placement.
    // The group, its physics and health components, both GroupRigidBodyData bodies and the GroupHavokAsset are cloned
    // from a shipped BFII group (--template) so every BFII-only field keeps a shipped value, then filled from group.json
    // (written by the port scripts from the 2015 group: see tools/port/build_group_json.py).
    public static class PortImportGroup
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "port import-group";
            string jsonPath = null, into = null, template = null, physicsPath = null, metaHex = null, resName = null, bundle = null, mvdb = null;
            int healthBase = -1, templateIndex = 0; bool noBundle = false, clampParts = false, allowPartMismatch = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i], next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "--into" && next != null) { into = next; i++; }
                else if (a == "--template" && next != null) { template = next; i++; }
                else if (a == "--physics" && next != null) { physicsPath = next; i++; }
                else if (a == "--physics-meta" && next != null) { metaHex = next; i++; }
                else if (a == "--res" && next != null) { resName = next; i++; }
                else if (a == "--health-base" && next != null) { healthBase = int.Parse(next, CultureInfo.InvariantCulture); i++; }
                else if (a == "--bundle" && next != null) { bundle = next; i++; }
                else if (a == "--no-bundle") noBundle = true;
                else if (a == "--mvdb" && next != null) { mvdb = next; i++; }
                else if (a == "--clamp-part-components") clampParts = true;
                else if (a == "--allow-part-mismatch") allowPartMismatch = true;
                else if (!a.StartsWith("--")) jsonPath = a;
            }
            if (jsonPath == null || into == null || template == null)
            { Console.Error.WriteLine(label + ": usage: port import-group <group.json> --into <partition> --template <asset>[#<group>] [--physics <file>] [--mvdb <db>|none] [--clamp-part-components] [--allow-part-mismatch]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": no project loaded"); return 1; }
            int hash = template.LastIndexOf('#');
            if (hash > 0) { templateIndex = int.Parse(template.Substring(hash + 1), CultureInfo.InvariantCulture); template = template.Substring(0, hash); }

            JObject g;
            try { g = JObject.Parse(File.ReadAllText(jsonPath)); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + jsonPath + ": " + ex.Message); return 1; }
            physicsPath = physicsPath ?? (string)g["physicsFile"];
            metaHex = metaHex ?? (string)g["physicsMeta"];
            resName = (resName ?? (string)g["resName"])?.ToLowerInvariant();
            if (physicsPath == null || metaHex == null || resName == null)
            { Console.Error.WriteLine(label + ": group.json (or the options) must give physicsFile, physicsMeta and resName"); return 1; }

            // ---- load everything before touching the project -------------------------------------------------
            EbxAssetEntry intoEntry = App.AssetManager.GetEbxEntry(into);
            if (intoEntry == null) { Console.Error.WriteLine(label + ": partition not found: " + into); return 1; }
            EbxAssetEntry tmplEntry = App.AssetManager.GetEbxEntry(template);
            if (tmplEntry == null) { Console.Error.WriteLine(label + ": template not found: " + template); return 1; }
            EbxAsset level, tmpl;
            try { level = App.AssetManager.GetEbx(intoEntry); tmpl = App.AssetManager.GetEbx(tmplEntry); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": load failed: " + ex.Message); return 1; }
            string err;
            object tmplGroup = LevelCommand.FindStaticGroup(tmpl.RootObject, templateIndex, out err);
            if (tmplGroup == null) { Console.Error.WriteLine(label + ": template: " + err); return 1; }
            IList objects = LevelCommand.GetEntityList(level.RootObject);
            if (objects == null) { Console.Error.WriteLine(label + ": " + into + " has no Objects list"); return 1; }

            byte[] physics = File.ReadAllBytes(physicsPath);
            byte[] meta = HexBytes(metaHex);
            if (meta.Length != 16) { Console.Error.WriteLine(label + ": physics meta must be 16 bytes of hex"); return 1; }
            HavokPhysicsResource parsed;
            try { parsed = HavokPhysicsResource.Parse(physics); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": physics: " + ex.Message); return 1; }

            // members: the BFII blueprint (and the entity instance it wraps) and mesh of each
            var members = (JArray)g["members"];
            var resolved = new List<MemberRefs>();
            var bundleAssets = new List<EbxAssetEntry>();
            // every member is checked before anything is written, and every problem is reported at once (a group of
            // 200 members used to take one run per refusal)
            var problems = new List<string>();
            foreach (JObject m in members)
            {
                var r = new MemberRefs { Json = m };
                string bp = (string)m["blueprint"];
                if (string.IsNullOrEmpty(bp)) { problems.Add(label + ": member without a BFII blueprint: " + (string)m["source"]); continue; }
                r.Blueprint = App.AssetManager.GetEbxEntry(bp);
                if (r.Blueprint == null) { problems.Add(label + ": blueprint not found: " + bp); continue; }
                EbxAsset bpAsset;
                try { bpAsset = App.AssetManager.GetEbx(r.Blueprint); }
                catch (Exception ex) { problems.Add(label + ": " + bp + ": " + ex.Message); continue; }
                object entity = LevelCommand.ResolveInternal(LevelCommand.ReadMember(bpAsset.RootObject, "Object"));
                if (entity == null) { problems.Add(label + ": " + bp + " has no Object"); continue; }
                try
                {
                    dynamic dyn = entity;
                    r.EntityGuid = (Guid)dyn.GetInstanceGuid().ExportedGuid;
                    if (!(bool)dyn.GetInstanceGuid().IsExported || r.EntityGuid == Guid.Empty)
                    { problems.Add(label + ": " + bp + "'s entity is not exported; a member cannot reference it"); continue; }
                }
                catch (Exception ex) { problems.Add(label + ": " + bp + ": " + ex.Message); continue; }
                // Physics parts belong to Havok members only. A member placed by EBX transforms (render-only, no instances
                // in the group's physics) must carry PhysicsPartCountPerInstance 0 -- 135 of 135 shipped Hoth_01 Content
                // EBX members do. 2015's Mesh_Light_Cone_00 claims 2 parts per instance with an unset range
                // (0xFFFFFFFF); the server adds the part index to that range start, wraps, passes its bounds check and
                // indexes off the end of a body table right after load (game+0x143AB34, T6/T7).
                bool ebxMember = m["transforms"] is JArray tj && tj.Count > 0;
                int ppi = m["physicsPartCountPerInstance"] != null ? (int)m["physicsPartCountPerInstance"] : 0;
                JArray pr = m["physicsPartRange"] as JArray;
                bool rangeUnset = pr == null || pr.Count != 2 || (long)pr[0] == 0xFFFFFFFFL || (long)pr[1] == 0xFFFFFFFFL;
                if (ebxMember && ppi > 0)
                {
                    logger.LogWarning(label + ": EBX member " + (string)m["source"] + " claimed " + ppi + " physics part(s) per instance; set to 0 (render-only members carry none)");
                    m["physicsPartCountPerInstance"] = 0;
                    m["physicsPartRange"] = new JArray(0xFFFFFFFFL, 0xFFFFFFFFL);
                }
                else if (!ebxMember && ppi > 0 && (rangeUnset || (long)pr[1] - (long)pr[0] + 1 != (long)ppi * (int)m["instanceCount"]))
                {
                    problems.Add(label + ": Havok member " + (string)m["source"] + " has " + ppi + " part(s) x " + (int)m["instanceCount"] +
                                            " instance(s) but physicsPartRange " + (pr != null ? pr.ToString(Newtonsoft.Json.Formatting.None) : "null") + "; the group's part tables would not line up");
                    continue;
                }
                // A member may not claim more part components than its blueprint has: the server walks the blueprint's
                // PartComponentData per member part component while it creates the group and dereferences a null one
                // (game+0xBC0848, read at +0x80 = PartComponentData.HealthStates) -- T6: a 5-part Atgar tower on a
                // 1-part BoxSmall_01 stand-in.
                int bpParts = bpAsset.Objects.Count(o => o != null && o.GetType().Name == "PartComponentData");
                int wantParts = m["partComponentCount"] != null ? (int)m["partComponentCount"] : 0;
                if (wantParts > bpParts)
                {
                    if (!clampParts)
                    {
                        problems.Add(label + ": member " + (string)m["source"] + " has " + wantParts + " part component(s) but " + bp + " has " + bpParts +
                                                "; the server crashes creating it (game+0xBC0848). Map it to a blueprint with enough parts, or pass --clamp-part-components (stand-in tests only).");
                        continue;
                    }
                    logger.LogWarning(label + ": member " + (string)m["source"] + ": part components clamped " + wantParts + " -> " + bpParts + " (" + bp + ")");
                    m["partComponentCount"] = bpParts;
                }
                // ...and a Havok member's physics parts per instance must equal the blueprint entity's PhysicsPartInfos: a
                // damaged instance gets a ServerStaticModelPhysicsComponent whose per-part table is sized from them while
                // the server walks the body's parts, so extra parts read past the table (game+0x601CD1, Hoth2015 L2/L3:
                // 2015 BoxMedium_02/BoxSmall_03 are 2-part, DICE's same-GUID Box_M_02/03 1-part). Shipped Hoth_01: 146/146.
                if (!ebxMember && ppi > 0)
                {
                    IList infos = LevelCommand.ReadList(entity, "PhysicsPartInfos");
                    int bpInfos = infos != null ? infos.Count : 0;
                    if (bpInfos != ppi)
                    {
                        string msg = label + ": member " + (string)m["source"] + " has " + ppi + " physics part(s) per instance but " + bp + " declares " + bpInfos +
                                     " PhysicsPartInfo(s); the server crashes when an instance is damaged (game+0x601CD1). Port the source blueprint or map the member to one with " + ppi + " part(s)";
                        if (!allowPartMismatch) { problems.Add(msg + ", or pass --allow-part-mismatch (stand-in tests only)."); continue; }
                        logger.LogWarning(msg + " (--allow-part-mismatch)");
                    }
                }
                string meshPath = (string)m["mesh"] ?? LevelCommand.FindBlueprintMesh(bpAsset.RootObject);
                r.Mesh = meshPath != null ? App.AssetManager.GetEbxEntry(meshPath) : null;
                if (r.Mesh == null) { problems.Add(label + ": " + bp + ": no mesh (" + meshPath + ")"); continue; }
                // A composite mesh draws one part per part transform and takes each part's placement from the member's
                // physics parts, so a Havok member using one needs exactly that many parts per instance (see
                // MeshPartInfo). 2015 and BFII sometimes disagree for the same prop (LightGround_03: 2 vs 3).
                if (!ebxMember && r.Mesh.Type == "CompositeMeshAsset")
                {
                    string mtype;
                    int meshParts = FrostyCLI.Adapters.Mesh.MeshPartInfo.CompositePartCount(r.Mesh, out mtype);
                    int havokParts = (int)m["physicsPartCountPerInstance"];
                    if (meshParts > 0 && meshParts != havokParts)
                    {
                        problems.Add(label + ": member " + (string)m["source"] + " has " + havokParts + " physics part(s) per instance but its mesh " + r.Mesh.Name +
                                                " is a composite of " + meshParts + "; the renderer reads past the member's parts after load (game+0x6E6ED6A). " +
                                                "Port the source mesh or map the member to a blueprint whose mesh has " + havokParts + " part(s).");
                        continue;
                    }
                }
                foreach (JToken v in (JArray)m["objectVariation"] ?? new JArray())
                {
                    string vp = v.Type == JTokenType.Null ? null : (string)v;
                    EbxAssetEntry ve = vp != null ? App.AssetManager.GetEbxEntry(vp) : null;
                    r.Variations.Add(ve);
                    if (ve != null) bundleAssets.Add(ve);
                }
                bundleAssets.Add(r.Blueprint); bundleAssets.Add(r.Mesh);
                resolved.Add(r);
            }
            if (problems.Count > 0)
            {
                foreach (string pmsg in problems) Console.Error.WriteLine(pmsg);
                Console.Error.WriteLine(label + ": " + problems.Count + " member problem(s); nothing written");
                return 1;
            }

            // ---- variation database: every member mesh needs an entry in the sub-world's database, and the
            // textures that entry binds must be bundled. A shipped prop's textures are referenced from its entry,
            // not from its mesh, so the blueprint's dependency closure never reaches them: an entry texture missing
            // from the bundle crashes the load at game+0x1522B97 once the member's mesh is loaded (T2: T_EwebBox_CS,
            // T6: the AT-AT wreck's T_ATATTile_01_C), and a mesh without an entry crashes at game+0xF58B89 once drawn.
            // Same rules as port import-layer: keep an existing entry, else copy one from a shipped database that
            // registers the mesh, else derive one from the mesh.
            int mvdbKept = 0, mvdbCopied = 0, mvdbDerived = 0;
            if (mvdb == null)
            {
                string guess = into + "/MeshVariationDb_Win32";
                if (App.AssetManager.GetEbxEntry(guess) != null) mvdb = guess;
            }
            if (mvdb != null && mvdb.Equals("none", StringComparison.OrdinalIgnoreCase)) mvdb = null;
            else if (mvdb == null)
            { Console.Error.WriteLine(label + ": no variation database found next to " + into + "; pass --mvdb <db> (or --mvdb none to skip)"); return 1; }
            else if (App.AssetManager.GetEbxEntry(mvdb) == null)
            { Console.Error.WriteLine(label + ": variation database not found: " + mvdb); return 1; }
            if (mvdb != null && !DryRun.Active)
            {
                EbxAssetEntry db = App.AssetManager.GetEbxEntry(mvdb);
                var seenMesh = new HashSet<Guid>();
                var textures = new List<EbxAssetEntry>();
                foreach (MemberRefs r in resolved)
                {
                    if (!seenMesh.Add(r.Mesh.Guid)) continue;
                    if (PortCommand.MvdbHasMesh(mvdb, r.Mesh.Name)) mvdbKept++;
                    else
                    {
                        string donorDb = PortCommand.FindDatabaseWithMesh(r.Mesh.Name);
                        string cerr = "no shipped database registers it";
                        if (donorDb != null)
                        {
                            int mats;
                            cerr = MvdbCommand.CopyEntry(db, App.AssetManager.GetEbxEntry(donorDb), r.Mesh, 0, logger, out mats);
                        }
                        if (donorDb != null && cerr == null) mvdbCopied++;
                        else if (MvdbCommand.ExecuteAdd(new[] { mvdb, "--mesh", r.Mesh.Name }, logger, formatter) == 0) mvdbDerived++;
                        else
                        {
                            Console.Error.WriteLine(label + ": " + r.Mesh.Name + " (member " + (string)r.Json["source"] + ") has no entry in " + mvdb +
                                                    " and none could be copied (" + cerr + ") or derived; a member mesh without one crashes the game at game+0xF58B89");
                            return 1;
                        }
                    }
                    textures.AddRange(MvdbCommand.EntryTextures(db, r.Mesh.Guid));
                }
                // the textures each member mesh's shader block depot binds by instance guid (see LevelCommand.DepotTextures)
                int depotCount = 0, depotTex = 0;
                var seenMesh2 = new HashSet<Guid>();
                foreach (MemberRefs r in resolved)
                {
                    if (!seenMesh2.Add(r.Mesh.Guid)) continue;
                    int d;
                    List<EbxAssetEntry> dt = LevelCommand.DepotTextures(r.Mesh, out d);
                    depotCount += d; depotTex += dt.Count;
                    textures.AddRange(dt);
                }
                logger.Log("{0}", label + ": " + depotCount + " shader block depot(s) bind " + depotTex + " texture reference(s)");
                var seenTex = new HashSet<Guid>(bundleAssets.Select(b => b.Guid));
                foreach (EbxAssetEntry t in textures)
                    if (seenTex.Add(t.Guid)) bundleAssets.Add(t);
                logger.Log("{0}", label + ": " + mvdb + ": " + mvdbKept + " entr" + (mvdbKept == 1 ? "y" : "ies") + " kept, " + mvdbCopied + " copied, " +
                           mvdbDerived + " derived; " + textures.Select(t => t.Guid).Distinct().Count() + " entry texture(s) to bundle");
            }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "rebuild a {0}-member group ({1} parts, {2} bytes of physics) in {3} from {4}",
                    resolved.Count, parsed.PartCount, physics.Length, into, template);
                return 0;
            }

            // ---- the physics resource ------------------------------------------------------------------------
            ResAssetEntry res = App.AssetManager.GetResEntry(resName);
            if (res == null)
            {
                try { App.AssetManager.AddRes(resName, (ResourceType)2432974693u, meta, physics); }
                catch (Exception ex) { Console.Error.WriteLine(label + ": could not add " + resName + ": " + ex.Message); return 1; }
                res = App.AssetManager.GetResEntry(resName);
                if (res == null) { Console.Error.WriteLine(label + ": AddRes did not create " + resName); return 1; }
            }
            else App.AssetManager.ModifyRes(resName, physics, meta);

            // ---- the group, cloned from the template with its internal graph ----------------------------------
            var copier = new GraphCopier(level, LevelCommand.NextInternalId(level));
            object group = copier.Copy(tmplGroup);
            try
            {
                IList ms = LevelCommand.ReadList(group, "MemberDatas");
                ms.Clear();
                Type memberType = PropertyPathWriter.ListElementType(ms);
                int hb = healthBase >= 0 ? healthBase : (int)LevelCommand.NextHealthStateId(level);
                int memberIndex = 0, normalized = 0;
                foreach (MemberRefs r in resolved)
                {
                    object mm = Activator.CreateInstance(memberType);
                    JObject j = r.Json;
                    EbxRefWriter.SetRefByPath(mm, "MemberType", r.Blueprint.Guid, r.EntityGuid);
                    EbxRefWriter.SetRefByPath(mm, "MeshAsset", r.Mesh.Guid, LevelCommand.ResolveExportedGuid(r.Mesh));
                    LevelCommand.SetNumericMember(mm, "InstanceCount", (int)j["instanceCount"]);
                    SetRange(mm, "PhysicsPartRange", (JArray)j["physicsPartRange"]);
                    LevelCommand.SetNumericMember(mm, "PhysicsPartCountPerInstance", (int)j["physicsPartCountPerInstance"]);
                    SetRange(mm, "NetworkIdRange", (JArray)j["networkIdRange"]);
                    LevelCommand.SetNumericMember(mm, "NetworkIdCountPerInstance", (int)j["networkIdCountPerInstance"]);
                    LevelCommand.SetNumericMember(mm, "PartComponentCount", (int)j["partComponentCount"]);
                    // Contiguous from the level's next free id, one per member in the order written -- NOT the source
                    // group's own id: members dropped from the source (EBX-only ones without a BFII blueprint) leave gaps,
                    // and an id past the level's member total indexes off the end of the health-state manager's entity
                    // table (T6: 2015 ids 0..179 on 153 members -> read of 0xAA fill at game+0x143AB34 right after load).
                    LevelCommand.SetNumericMember(mm, "HealthStateEntityManagerId", hb + memberIndex++);
                    IList xforms = LevelCommand.ReadList(mm, "InstanceTransforms");
                    Type xt = PropertyPathWriter.ListElementType(xforms);
                    foreach (JArray t in (JArray)j["transforms"] ?? new JArray()) xforms.Add(Transform(xt, t));
                    IList rad = LevelCommand.ReadList(mm, "InstanceRadiosityTypeOverride");
                    Type rt = rad != null ? PropertyPathWriter.ListElementType(rad) : null;
                    foreach (JToken v in (JArray)j["radiosity"] ?? new JArray()) rad.Add(Enum.Parse(rt, (string)v));
                    IList tsn = LevelCommand.ReadList(mm, "InstanceTerrainShaderNodesEnable");
                    foreach (JToken v in (JArray)j["terrainShaderNodes"] ?? new JArray()) tsn.Add((bool)v);
                    IList vars = LevelCommand.ReadList(mm, "InstanceObjectVariation");
                    Type vt = vars != null ? PropertyPathWriter.ListElementType(vars) : null;
                    if (r.Variations.Any(v => v != null))
                        foreach (EbxAssetEntry ve in r.Variations)
                            vars.Add(ve == null ? Activator.CreateInstance(vt) : RefTo(vt, ve));
                    // Per-instance lists are empty or exactly InstanceCount long in every shipped BFII group (Hoth_01
                    // Content: 502 of 502 members). 2015 tolerated short ones (StreamedLevel: 266 instances with 255
                    // radiosity overrides, 50 with 47 all-null variations); BFII reads them per instance and runs off the
                    // end once the world is up (game+0x6E6ED6A, T6). Pad with the element default, which is what the short
                    // 2015 lists meant, or trim.
                    int instances = (int)j["instanceCount"];
                    normalized += FitToInstances(rad, rt, instances) + FitToInstances(tsn, typeof(bool), instances) + FitToInstances(vars, vt, instances);
                    ms.Add(mm);
                }

                if (normalized > 0) logger.LogWarning(label + ": " + normalized + " per-instance list(s) padded or trimmed to their member's InstanceCount");
                LevelCommand.SetNumericMember(group, "NetworkIdCount", (int)g["networkIdCount"]);
                IList boxes = LevelCommand.ReadList(group, "PartBoundingBoxes");
                Type bt = PropertyPathWriter.ListElementType(boxes);
                boxes.Clear();
                foreach (JArray b in (JArray)g["partBoundingBoxes"]) boxes.Add(Box(bt, b));
                if (g["transform"] is JArray gt) SetMember(group, "Transform", Transform(LevelCommand.ReadMember(group, "Transform").GetType(), gt));

                // physics component: parts, bodies, the GroupHavokAsset they share
                object physicsComponent = null;
                foreach (object c in LevelCommand.ReadList(group, "Components"))
                {
                    object comp = LevelCommand.ResolveInternal(c);
                    if (comp != null && comp.GetType().Name.IndexOf("Physics", StringComparison.OrdinalIgnoreCase) >= 0) physicsComponent = comp;
                }
                if (physicsComponent == null) throw new InvalidOperationException("the template group has no physics component");
                IList parts = LevelCommand.ReadList(physicsComponent, "Parts");
                object partProto = parts.Count > 0 ? parts[0] : Activator.CreateInstance(PropertyPathWriter.ListElementType(parts));
                parts.Clear();
                int partCount = (int)g["parts"];
                for (int k = 0; k < partCount; k++) parts.Add(LevelCommand.DeepCopyObject(partProto));

                IList bodies = LevelCommand.ReadList(physicsComponent, "PhysicsBodies");
                var bodyJson = (JArray)g["bodies"];
                object havokAsset = null;
                for (int b = 0; b < bodies.Count; b++)
                {
                    object body = LevelCommand.ResolveInternal(bodies[b]);
                    JObject bj = b < bodyJson.Count ? (JObject)bodyJson[b] : (JObject)bodyJson[bodyJson.Count - 1];
                    IList lookups = LevelCommand.ReadList(body, "RaycastMaterialIndicesLookups");
                    Type lt = PropertyPathWriter.ListElementType(lookups);
                    lookups.Clear();
                    foreach (JArray l in (JArray)bj["raycastLookups"])
                    {
                        object lk = Activator.CreateInstance(lt);
                        IList idx = LevelCommand.ReadList(lk, "MaterialIndices");
                        Type it = PropertyPathWriter.ListElementType(idx);
                        foreach (JToken v in l) idx.Add(Convert.ChangeType((int)v, it, CultureInfo.InvariantCulture));
                        lookups.Add(lk);
                    }
                    if (bj["material"] != null) PropertyPathWriter.SetValue(body, "Material.Packed", ((uint)bj["material"]).ToString(CultureInfo.InvariantCulture));
                    object asset = LevelCommand.ResolveInternal(LevelCommand.ReadMember(body, "Asset"));
                    if (asset != null) havokAsset = asset;
                }
                if (havokAsset == null) throw new InvalidOperationException("the template's bodies carry no GroupHavokAsset");

                // bodies the template lacks, e.g. 2015 Arctic_01's third body: a RigidBodyData on
                // RigidBodyCollisionLayer_CharacterCollisionGeometryLayer reading root slot 2 (soldier-only walls)
                for (int b = bodies.Count; b < bodyJson.Count; b++)
                {
                    JObject bj = (JObject)bodyJson[b];
                    string typeName = (string)bj["type"] ?? "RigidBodyData";
                    object body = TypeLibrary.CreateObject(typeName);
                    if (body == null) throw new InvalidOperationException("cannot create a " + typeName);
                    foreach (var kv in (JObject)bj["fields"] ?? new JObject())
                        PropertyPathWriter.SetValue(body, kv.Key, kv.Value.Type == JTokenType.Boolean ? ((bool)kv.Value ? "true" : "false") : kv.Value.ToString());
                    SetMember(body, "Asset", new PointerRef(havokAsset));
                    // the callback handler is an object of its own; share the client proxy body's kind
                    object proto = LevelCommand.ResolveInternal(bodies[bodies.Count - 1]);
                    object handler = proto != null ? LevelCommand.ResolveInternal(LevelCommand.ReadMember(proto, "PhysicsCallbackHandler")) : null;
                    if (handler != null) SetMember(body, "PhysicsCallbackHandler", new PointerRef(copier.Copy(handler)));
                    copier.Register(body);
                    bodies.Add(new PointerRef(body));
                }
                PropertyPathWriter.SetValue(havokAsset, "Name", resName.Replace("_physics_win32", "_Physics_Win32"));
                SetResourceRef(havokAsset, "Resource", res.ResRid);
                IList aabbs = LevelCommand.ReadList(havokAsset, "Aabb");
                Type at = PropertyPathWriter.ListElementType(aabbs);
                aabbs.Clear();
                foreach (JArray partBoxes in (JArray)g["assetAabbs"])
                {
                    object aa = Activator.CreateInstance(at);
                    IList pl = LevelCommand.ReadList(aa, "PartAabb");
                    Type pt = PropertyPathWriter.ListElementType(pl);
                    foreach (JArray pb in partBoxes) pl.Add(Box(pt, pb));
                    aabbs.Add(aa);
                }
                IList ext = LevelCommand.ReadList(havokAsset, "ExternalAssets");
                Type et = PropertyPathWriter.ListElementType(ext);
                ext.Clear();
                int extCount = g["externalAssets"] is JArray ea ? ea.Count : aabbs.Count;
                for (int k = 0; k < extCount; k++) ext.Add(Activator.CreateInstance(et));   // BFII groups keep these null

                objects.Add(new PointerRef(group));
            }
            catch (Exception ex) { Console.Error.WriteLine(label + ": could not build the group: " + LevelCommand.Describe(ex)); return 1; }

            // link the new res to the partition so bundling and the project carry it
            try { intoEntry.LinkAsset(res); } catch { }
            if (!EbxWriteCommand.CallModifyEbx(into, level, logger)) return 1;

            string bundled = "not bundled (--no-bundle)";
            if (!noBundle)
            {
                string berr;
                bundled = LevelCommand.BundleAssets(into, bundle, bundleAssets, out berr);
                if (bundled == null) { Console.Error.WriteLine(label + ": bundling: " + berr); return 1; }
                try
                {
                    BundleEntry be = bundle != null ? BundleCommand.FindBundle(bundle) : App.AssetManager.GetBundleEntry(intoEntry.Bundles.Concat(intoEntry.AddedBundles).First());
                    if (be != null) res.AddToBundle(App.AssetManager.GetBundleId(be));
                }
                catch (Exception ex) { logger.LogWarning(label + ": could not bundle " + resName + ": " + ex.Message); }
            }

            formatter.WriteResult("OK", label, new Dictionary<string, string>
            {
                { "into", into },
                { "members", resolved.Count.ToString(CultureInfo.InvariantCulture) },
                { "instances", resolved.Sum(r => (int)r.Json["instanceCount"]).ToString(CultureInfo.InvariantCulture) },
                { "parts", ((int)g["parts"]).ToString(CultureInfo.InvariantCulture) },
                { "physics", resName + " (" + physics.Length.ToString("N0", CultureInfo.InvariantCulture) + " B, " + parsed.Compounds.Count + " compounds)" },
                { "objects", copier.Created + " internal object(s) cloned from " + template },
                { "bundle", bundled },
                { "mvdb", mvdb == null ? "not touched (--mvdb none)" : mvdb + ": " + mvdbKept + " kept, " + mvdbCopied + " copied, " + mvdbDerived + " derived" },
            });
            return 0;
        }

        // Makes a per-instance list empty or exactly `instances` long; returns 1 when it had to change it.
        private static int FitToInstances(IList list, Type elementType, int instances)
        {
            if (list == null || list.Count == 0 || list.Count == instances) return 0;
            while (list.Count > instances) list.RemoveAt(list.Count - 1);
            while (list.Count < instances) list.Add(elementType == null ? null : Activator.CreateInstance(elementType));
            return 1;
        }

        private sealed class MemberRefs
        {
            public JObject Json;
            public EbxAssetEntry Blueprint, Mesh;
            public Guid EntityGuid;
            public List<EbxAssetEntry> Variations = new List<EbxAssetEntry>();
        }

        /// <summary>
        /// Copies an EBX object and every object it reaches through INTERNAL PointerRefs into <see cref="target"/>,
        /// re-pointing the references at the copies (one copy per source object, so shared targets stay shared: both
        /// group bodies keep pointing at the one GroupHavokAsset). Values and external references copy as they are.
        /// </summary>
        internal sealed class GraphCopier
        {
            private readonly EbxAsset target;
            private readonly Dictionary<object, object> memo = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            private int nextId;
            public int Created;

            public GraphCopier(EbxAsset target, int firstInternalId) { this.target = target; nextId = firstInternalId; }

            /// <summary>Give a freshly created object an internal id and add it to the target asset.</summary>
            public void Register(object obj)
            {
                MethodInfo setGuid = obj.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                if (setGuid != null) setGuid.Invoke(obj, new object[] { new AssetClassGuid(nextId++) });
                target.AddObject(obj, false);
                InProcessObjects.Mark(obj);
                Created++;
            }

            public object Copy(object src)
            {
                if (src == null) return null;
                object existing;
                if (memo.TryGetValue(src, out existing)) return existing;
                Type t = src.GetType();
                object obj = Activator.CreateInstance(t);
                memo[src] = obj;
                CopyMembers(src, obj);
                bool exported = false;
                try { exported = (bool)((dynamic)src).GetInstanceGuid().IsExported; } catch { }
                MethodInfo setGuid = t.GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                if (setGuid != null)
                    setGuid.Invoke(obj, new object[] { exported ? new AssetClassGuid(Guid.NewGuid(), nextId++) : new AssetClassGuid(nextId++) });
                target.AddObject(obj, false);
                InProcessObjects.Mark(obj);
                Created++;
                return obj;
            }

            private void CopyMembers(object src, object dst)
            {
                Type t = src.GetType();
                foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                    pi.SetValue(dst, Value(pi.GetValue(src)));
                }
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    fi.SetValue(dst, Value(fi.GetValue(src)));
            }

            private object Value(object v)
            {
                if (v == null) return null;
                Type t = v.GetType();
                if (t.IsPrimitive || t.IsEnum || v is string || v is Guid || v is decimal) return v;
                if (v is PointerRef pr)
                    return pr.Type == PointerRefType.Internal && pr.Internal != null ? new PointerRef(Copy(pr.Internal)) : v;
                if (t.Name == "CString" || t.Name == "Sha1" || t.Name == "ResourceRef" || t.Name == "AssetClassGuid") return v;
                if (v is IList list)
                {
                    IList copy = (IList)Activator.CreateInstance(t);
                    foreach (object item in list) copy.Add(Value(item));
                    return copy;
                }
                object o = Activator.CreateInstance(t);    // an EBX struct: by value
                CopyMembers(v, o);
                return o;
            }
        }

        /// <summary>
        /// port import-volumes <volumes.json> --into <partition> --obb-template <asset>[#i] --sphere-template <asset>[#i]
        ///                     [--word-map <wordmap.json>]
        /// Invisible collision blockers: 2015 OBBCollisionEntityData / SphereCollisionEntityData (no Blueprint, so
        /// port export-layer never sees them) rebuilt as the same BFII entity types by graph-cloning a shipped volume
        /// (its internal RigidBodyData included) and setting Transform, HalfExtents / Radius, Enabled, CollisionLayer and
        /// the physics material (2015 words re-encoded through the word map, 0x8000 -> 0x800 by default). Flags keep the
        /// template's flag byte over the new instance guid's low 24 bits (memory bfii-entity-flags-field).
        /// volumes.json: [{"type":"OBBCollisionEntityData"|"SphereCollisionEntityData", "halfExtents":[x,y,z],
        ///                 "radius":r, "transform":[[r],[u],[f],[t]], "enabled":true, "material":"0x8000",
        ///                 "collisionLayer":"RigidBodyCollisionLayer_StaticLayer"}, ...]
        /// Occluders (--occluder-template): {"type":"OccluderVolumeEntityData", "transform":[[r],[u],[f],[t]] with the
        /// box size in the axis lengths (as 2015 and BFII store it), "coverageValue":n, "occluderHighPriority":b,
        /// "occluderIsConservative":b, "visible":b} -- render-culling volumes, no physics.
        /// </summary>
        public static int ExecuteVolumes(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "port import-volumes";
            string jsonPath = null, into = null, obbTemplate = null, sphereTemplate = null, occluderTemplate = null, wordMapPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i], next = i + 1 < args.Length ? args[i + 1] : null;
                if (a == "--into" && next != null) { into = next; i++; }
                else if (a == "--obb-template" && next != null) { obbTemplate = next; i++; }
                else if (a == "--occluder-template" && next != null) { occluderTemplate = next; i++; }
                else if (a == "--sphere-template" && next != null) { sphereTemplate = next; i++; }
                else if (a == "--word-map" && next != null) { wordMapPath = next; i++; }
                else if (!a.StartsWith("--")) jsonPath = a;
            }
            if (jsonPath == null || into == null || (obbTemplate == null && sphereTemplate == null && occluderTemplate == null))
            { Console.Error.WriteLine(label + ": usage: port import-volumes <volumes.json> --into <partition> [--obb-template <asset>[#i]] [--sphere-template <asset>[#i]] [--occluder-template <asset>[#i]] [--word-map <json>]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": no project loaded"); return 1; }

            JArray vols;
            try { vols = JArray.Parse(File.ReadAllText(jsonPath)); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + jsonPath + ": " + ex.Message); return 1; }
            var wordMap = new Dictionary<uint, uint>();
            if (wordMapPath != null)
            {
                try { foreach (var kv in HavokPhysicsResource.ReadWordMap(File.ReadAllText(wordMapPath))) wordMap[kv.Key] = kv.Value; }
                catch (Exception ex) { Console.Error.WriteLine(label + ": word map: " + ex.Message); return 1; }
            }

            EbxAssetEntry intoEntry = App.AssetManager.GetEbxEntry(into);
            if (intoEntry == null) { Console.Error.WriteLine(label + ": partition not found: " + into); return 1; }
            EbxAsset level = App.AssetManager.GetEbx(intoEntry);
            IList objects = LevelCommand.GetEntityList(level.RootObject);
            if (objects == null) { Console.Error.WriteLine(label + ": " + into + " has no Objects list"); return 1; }

            var templates = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in new[] { new KeyValuePair<string, string>("OBBCollisionEntityData", obbTemplate), new KeyValuePair<string, string>("SphereCollisionEntityData", sphereTemplate),
                                       new KeyValuePair<string, string>("OccluderVolumeEntityData", occluderTemplate) })
            {
                if (kv.Value == null) continue;
                string path = kv.Value; int index = 0;
                int hash = path.LastIndexOf('#');
                if (hash > 0) { index = int.Parse(path.Substring(hash + 1), CultureInfo.InvariantCulture); path = path.Substring(0, hash); }
                EbxAssetEntry te = App.AssetManager.GetEbxEntry(path);
                if (te == null) { Console.Error.WriteLine(label + ": template not found: " + path); return 1; }
                object found = App.AssetManager.GetEbx(te).Objects.Where(o => o != null && o.GetType().Name == kv.Key).Skip(index).FirstOrDefault();
                if (found == null) { Console.Error.WriteLine(label + ": " + path + " has no " + kv.Key + " #" + index); return 1; }
                templates[kv.Key] = found;
            }

            int added = 0, unmapped = 0, cloned = 0;
            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (JObject v in vols)
            {
                string type = (string)v["type"];
                object tmpl;
                if (type == null || !templates.TryGetValue(type, out tmpl))
                { Console.Error.WriteLine(label + ": no template for volume type " + type); return 1; }
                // a fresh copier per volume: GraphCopier memoises, so one copier would hand every volume the SAME clone
                var copier = new GraphCopier(level, LevelCommand.NextInternalId(level));
                object e = copier.Copy(tmpl);
                cloned += copier.Created;
                try
                {
                    SetMember(e, "Transform", Transform(LevelCommand.ReadMember(e, "Transform").GetType(), (JArray)v["transform"]));
                    if (type == "OccluderVolumeEntityData")
                    {
                        // render-culling volume: the size lives in the transform's axis lengths; no physics fields
                        if (v["coverageValue"] != null) PropertyPathWriter.SetValue(e, "CoverageValue", ((double)v["coverageValue"]).ToString("R", CultureInfo.InvariantCulture));
                        if (v["occluderHighPriority"] != null) PropertyPathWriter.SetValue(e, "OccluderHighPriority", (bool)v["occluderHighPriority"] ? "true" : "false");
                        if (v["occluderIsConservative"] != null) PropertyPathWriter.SetValue(e, "OccluderIsConservative", (bool)v["occluderIsConservative"] ? "true" : "false");
                        if (v["visible"] != null) PropertyPathWriter.SetValue(e, "Visible", (bool)v["visible"] ? "true" : "false");
                    }
                    else if (type == "OBBCollisionEntityData")
                    {
                        object he = LevelCommand.ReadMember(e, "HalfExtents");
                        object nv = Activator.CreateInstance(he.GetType());
                        JArray h = (JArray)v["halfExtents"];
                        SetField(nv, "x", (float)(double)h[0]); SetField(nv, "y", (float)(double)h[1]); SetField(nv, "z", (float)(double)h[2]);
                        SetMember(e, "HalfExtents", nv);
                    }
                    else PropertyPathWriter.SetValue(e, "Radius", ((double)v["radius"]).ToString("R", CultureInfo.InvariantCulture));
                    if (type != "OccluderVolumeEntityData") PropertyPathWriter.SetValue(e, "Enabled", ((bool?)v["enabled"] ?? true) ? "true" : "false");
                    if (v["collisionLayer"] != null) PropertyPathWriter.SetValue(e, "CollisionLayer", (string)v["collisionLayer"]);
                    if (v["material"] != null && type != "OccluderVolumeEntityData")
                    {
                        string ms = ((string)v["material"]).Replace("0x", "");
                        uint word = uint.Parse(ms, NumberStyles.HexNumber, CultureInfo.InvariantCulture), bf2;
                        if (!wordMap.TryGetValue(word, out bf2))
                        {
                            // the bare 2015 marker (no index, no flags) is the bare BFII marker
                            if (word == 0x8000u) bf2 = 0x800u;
                            else { bf2 = word; unmapped++; }
                        }
                        PropertyPathWriter.SetValue(e, "Material.Packed", bf2.ToString(CultureInfo.InvariantCulture));
                    }
                    // Flags: our guid's low 24 bits under the template's flag byte
                    Guid ig = ((AssetClassGuid)e.GetType().GetMethod("GetInstanceGuid").Invoke(e, null)).ExportedGuid;
                    object tf = LevelCommand.ReadMember(e, "Flags");
                    if (tf != null)
                    {
                        uint oldFlags = Convert.ToUInt32(tf, CultureInfo.InvariantCulture);
                        uint data1 = ig != Guid.Empty ? BitConverter.ToUInt32(ig.ToByteArray(), 0) : (uint)((cloned + added) * 2654435761u);
                        PropertyPathWriter.SetValue(e, "Flags", ((data1 & 0x00FFFFFFu) | (oldFlags & 0xFF000000u)).ToString(CultureInfo.InvariantCulture));
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine(label + ": volume " + added + ": " + LevelCommand.Describe(ex)); return 1; }
                objects.Add(new PointerRef(e));
                added++;
                int n; byType.TryGetValue(type, out n); byType[type] = n + 1;
            }
            if (!EbxWriteCommand.CallModifyEbx(into, level, logger)) return 1;
            formatter.WriteResult("OK", label, new Dictionary<string, string>
            {
                { "into", into },
                { "volumes", added.ToString(CultureInfo.InvariantCulture) },
                { "types", string.Join(", ", byType.Select(kv => kv.Key + " x" + kv.Value)) },
                { "objects", cloned + " object(s) cloned" },
                { "unmappedMaterials", unmapped.ToString(CultureInfo.InvariantCulture) },
            });
            return 0;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        private static object RefTo(Type pointerType, EbxAssetEntry entry)
        {
            Guid classGuid = LevelCommand.ResolveExportedGuid(entry);
            return new PointerRef(new EbxImportReference { FileGuid = entry.Guid, ClassGuid = classGuid });
        }

        private static void SetRange(object owner, string field, JArray values)
        {
            object range = LevelCommand.ReadMember(owner, field);
            Type t = range.GetType();
            object r = Activator.CreateInstance(t);
            SetField(r, "First", Convert.ToUInt32((long)values[0] & 0xFFFFFFFF));
            SetField(r, "Last", Convert.ToUInt32((long)values[1] & 0xFFFFFFFF));
            SetMember(owner, field, r);
        }

        private static object Transform(Type t, JArray v)
        {
            // [[rx,ry,rz],[ux,uy,uz],[fx,fy,fz],[tx,ty,tz]]
            object lt = Activator.CreateInstance(t);
            string[] rows = { "right", "up", "forward", "trans" };
            for (int k = 0; k < 4; k++)
            {
                object vec = LevelCommand.ReadMember(lt, rows[k]);
                object nv = Activator.CreateInstance(vec.GetType());
                SetField(nv, "x", (float)(double)v[k][0]); SetField(nv, "y", (float)(double)v[k][1]); SetField(nv, "z", (float)(double)v[k][2]);
                SetMember(lt, rows[k], nv);
            }
            return lt;
        }

        private static object Box(Type t, JArray b)
        {
            // [minx,miny,minz,maxx,maxy,maxz]
            object box = Activator.CreateInstance(t);
            foreach (var pair in new[] { ("min", 0), ("max", 3) })
            {
                object vec = LevelCommand.ReadMember(box, pair.Item1);
                object nv = Activator.CreateInstance(vec.GetType());
                SetField(nv, "x", (float)(double)b[pair.Item2]); SetField(nv, "y", (float)(double)b[pair.Item2 + 1]); SetField(nv, "z", (float)(double)b[pair.Item2 + 2]);
                SetMember(box, pair.Item1, nv);
            }
            return box;
        }

        private static void SetResourceRef(object owner, string field, ulong rid)
        {
            object cur = LevelCommand.ReadMember(owner, field);
            Type t = cur != null ? cur.GetType() : typeof(ResourceRef);
            object rr = Activator.CreateInstance(t, new object[] { rid });
            SetMember(owner, field, rr);
        }

        private static void SetField(object owner, string name, object value)
        {
            Type t = owner.GetType();
            PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi != null && pi.CanWrite) { pi.SetValue(owner, Convert.ChangeType(value, pi.PropertyType, CultureInfo.InvariantCulture)); return; }
            FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fi != null) { fi.SetValue(owner, Convert.ChangeType(value, fi.FieldType, CultureInfo.InvariantCulture)); return; }
            throw new MissingMemberException(t.Name, name);
        }

        private static void SetMember(object owner, string name, object value)
        {
            Type t = owner.GetType();
            PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi != null && pi.CanWrite) { pi.SetValue(owner, value); return; }
            FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fi != null) { fi.SetValue(owner, value); return; }
            throw new MissingMemberException(t.Name, name);
        }

        private static byte[] HexBytes(string hex)
        {
            hex = hex.Replace(" ", "");
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }
    }
}
