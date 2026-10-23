using System;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    public class LevelCommand : ICommand
    {
        public string Name => "level";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: frostycli level <tree|stats|validate|list|add|add-entity|place|remove|move|scaffold|dupe|create|combat-area|loading-screen> ...");
                return 1;
            }
            switch (args[0].ToLowerInvariant())
            {
                case "tree":   return ExecuteTree  (SubArgs(args, 1), logger, formatter);
                case "stats":  return ExecuteStats (SubArgs(args, 1), logger, formatter);
                case "validate": return ExecuteValidate(SubArgs(args, 1), logger, formatter);
                case "roundtrip": return ExecuteRoundtrip(SubArgs(args, 1), logger, formatter);
                case "list":   return ExecuteList  (SubArgs(args, 1), logger, formatter);
                case "add":    return ExecuteAdd   (SubArgs(args, 1), logger, formatter);
                case "place":  return ExecutePlace (SubArgs(args, 1), logger, formatter);
                case "add-entity": return ExecuteAddEntity(SubArgs(args, 1), logger, formatter);
                case "export-lights": return ExecuteExportLights(SubArgs(args, 1), logger, formatter);
                case "import-lights": return ExecuteImportLights(SubArgs(args, 1), logger, formatter);
                case "remove": return ExecuteRemove(SubArgs(args, 1), logger, formatter);
                case "clear": return ExecuteClear(SubArgs(args, 1), logger, formatter);
                case "prune": return LevelPrune.Execute(SubArgs(args, 1), logger, formatter);
                case "repoint-members": return LevelRepointMembers.Execute(SubArgs(args, 1), logger, formatter);
                case "radiosity-override": return LevelRadiosityOverride.Execute(SubArgs(args, 1), logger, formatter);
                case "sync-entity-parts": return LevelSyncEntityParts.Execute(SubArgs(args, 1), logger, formatter);
                case "set-variations": return LevelSetVariations.Execute(SubArgs(args, 1), logger, formatter);
                case "author": return LevelAuthor.Execute(SubArgs(args, 1), logger, formatter);
                case "sink-group": return ExecuteSinkGroup(SubArgs(args, 1), logger, formatter);
                case "transform-group": return ExecuteTransformGroup(SubArgs(args, 1), logger, formatter);
                case "repoint-entities": return ExecuteRepointEntities(SubArgs(args, 1), logger, formatter);
                case "registry-add": return ExecuteRegistryAdd(SubArgs(args, 1), logger, formatter);
                case "registry-copy": return ExecuteRegistryCopy(SubArgs(args, 1), logger, formatter);
                case "add-group": return ExecuteAddGroup(SubArgs(args, 1), logger, formatter);
                case "move":     return ExecuteMove    (SubArgs(args, 1), logger, formatter);
                case "scaffold": return ExecuteScaffold(SubArgs(args, 1), logger, formatter);
                case "dupe": return LevelDupe.Execute(SubArgs(args, 1), logger, formatter);
                case "create": return LevelCreate.Execute(SubArgs(args, 1), logger, formatter);
                case "closure": return LevelCreate.ExecuteClosure(SubArgs(args, 1), logger, formatter);
                case "skirmish-template": return LevelCreate.ExecuteTemplate(SubArgs(args, 1), logger, formatter);
                case "combat-area": return LevelCombatArea.Execute(SubArgs(args, 1), logger, formatter);
                case "loading-screen": return LevelLoadingScreen.Execute(SubArgs(args, 1), logger, formatter);
                default:
                    Console.Error.WriteLine("level: unknown subcommand '" + args[0] + "'");
                    return 1;
            }
        }

        // Root type plus a histogram of what its Objects list holds, for when neither known level
        // layout matches.
        private static string DescribeRoot(object root)
        {
            if (root == null) return "(null)";
            string typeName = root.GetType().Name;
            IList objects = ReadList(root, "Objects");
            if (objects == null) return typeName + " (no Objects list)";

            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int unresolved = 0;
            foreach (object value in objects)
            {
                object obj = ResolveInternal(value);
                if (obj == null) { unresolved++; continue; }
                int n;
                counts.TryGetValue(obj.GetType().Name, out n);
                counts[obj.GetType().Name] = n + 1;
            }
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> item in counts) parts.Add(item.Key + " x" + item.Value);
            if (unresolved > 0) parts.Add("(" + unresolved + " unresolved)");
            return typeName + " with " + objects.Count + " object(s): " +
                (parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "none");
        }

        private static float[] ParseVec3(string value)
        {
            string[] parts = value.Split(',');
            if (parts.Length != 3) throw new ArgumentException("expected \"x,y,z\", got '" + value + "'");
            var result = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
                        CultureInfo.InvariantCulture, out result[i]))
                    throw new ArgumentException("'" + value + "' is not a valid x,y,z position");
            return result;
        }

        private static string[] SubArgs(string[] args, int skip)
        {
            var r = new string[args.Length - skip];
            Array.Copy(args, skip, r, 0, r.Length);
            return r;
        }

        public static int ExecuteTree(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null;
            string format = "table";
            int maxDepth = 32;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--max-depth" && i + 1 < args.Length) maxDepth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }

            if (levelPath == null) { Console.Error.WriteLine("level tree: missing <level-ebx-path>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level tree: AssetManager not initialized."); return 1; }
            if (maxDepth < 0) { Console.Error.WriteLine("level tree: --max-depth must be zero or greater"); return 1; }

            LevelNode root = AnalyzeLevel(levelPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, maxDepth, null);
            if (root.Error != null && root.Type == null) { Console.Error.WriteLine("level tree: " + root.Error); return 1; }
            if (format == "json")
                Console.Out.WriteLine(JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented));
            else
                PrintLevelTree(root, 0);
            return 0;
        }

        // Creates a raw EntityData sub-object (for example AlternateSpawnEntityData), registers it
        // in the EBX partition and appends an internal PointerRef to the layer's Objects list.
        // This is deliberately separate from 'level add', which places an external blueprint ref.
        public static int ExecuteAddEntity(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string layerPath = null, typeName = null, pos = null, rot = null, scale = null;
            // --copy-from <ebx>@<instance guid>: deep-copy a shipped object (and the internal objects it points at, e.g.
            // a SubWorldReference's InclusionSettings) instead of creating a default one. add-entity's defaults are the
            // type's zero values (Mygeeto SHIP6: IsWin32SubLevel false = the sub-world never streamed on PC).
            string copyFrom = null;
            string transformField = "Transform";
            var assignments = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--copy-from" && i + 1 < args.Length) { copyFrom = args[++i]; continue; }
                if (arg == "--type" || arg == "--pos" || arg == "--rot" || arg == "--scale" ||
                    arg == "--transform-field" || arg == "--set")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("level add-entity: " + arg + " requires a value");
                        return 1;
                    }
                    string value = args[++i];
                    if (arg == "--type") typeName = value;
                    else if (arg == "--pos") pos = value;
                    else if (arg == "--rot") rot = value;
                    else if (arg == "--scale") scale = value;
                    else if (arg == "--transform-field") transformField = value;
                    else assignments.Add(value);
                }
                else if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("level add-entity: unknown option '" + arg + "'");
                    return 1;
                }
                else if (layerPath == null) layerPath = arg;
                else
                {
                    Console.Error.WriteLine("level add-entity: unexpected argument '" + arg + "'");
                    return 1;
                }
            }

            if (string.IsNullOrWhiteSpace(layerPath))
            { Console.Error.WriteLine("level add-entity: missing <layer-ebx-path>"); return 1; }
            if (string.IsNullOrWhiteSpace(typeName) && copyFrom == null)
            { Console.Error.WriteLine("level add-entity: --type <EntityType> or --copy-from <ebx>@<guid> is required"); return 1; }
            if (string.IsNullOrWhiteSpace(transformField))
            { Console.Error.WriteLine("level add-entity: --transform-field cannot be empty"); return 1; }

            var parsedAssignments = new List<KeyValuePair<string, string>>();
            foreach (string assignment in assignments)
            {
                int equals = assignment.IndexOf('=');
                if (equals <= 0)
                {
                    Console.Error.WriteLine("level add-entity: --set expects Field=Value, got '" + assignment + "'");
                    return 1;
                }
                string field = assignment.Substring(0, equals).Trim();
                if (field.Length == 0)
                {
                    Console.Error.WriteLine("level add-entity: --set field name cannot be empty");
                    return 1;
                }
                parsedAssignments.Add(new KeyValuePair<string, string>(field, assignment.Substring(equals + 1)));
            }

            LinearTransformData transform = default(LinearTransformData);
            bool hasTransform = pos != null || rot != null || scale != null;
            if (hasTransform)
            {
                try { transform = TransformBuilder.Build(pos, rot, scale); }
                catch (ArgumentException ex) { Console.Error.WriteLine("level add-entity: " + ex.Message); return 1; }
            }

            if (!Session.Project.IsLoaded)
            { Console.Error.WriteLine("level add-entity: no project loaded. Run 'project load' or 'project new' first."); return 1; }
            if (App.AssetManager == null)
            { Console.Error.WriteLine("level add-entity: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(layerPath);
            if (entry == null)
            { Console.Error.WriteLine("level add-entity: layer not found: " + layerPath); return 1; }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level add-entity: load failed: " + Describe(ex)); return 1; }

            IList objects = GetObjectsList(asset.RootObject);
            if (objects == null)
            { Console.Error.WriteLine("level add-entity: root has no Objects list: " + DescribeRoot(asset.RootObject)); return 1; }
            if (objects.IsFixedSize || objects.IsReadOnly)
            { Console.Error.WriteLine("level add-entity: layer Objects list is not writable"); return 1; }

            object entity;
            object copySource = null;
            if (copyFrom != null)
            {
                int at = copyFrom.LastIndexOf('@');
                Guid srcGuid;
                if (at <= 0 || !Guid.TryParse(copyFrom.Substring(at + 1), out srcGuid))
                { Console.Error.WriteLine("level add-entity: --copy-from expects <ebx>@<instance guid>, got '" + copyFrom + "'"); return 1; }
                EbxAssetEntry srcEntry = App.AssetManager.GetEbxEntry(copyFrom.Substring(0, at));
                if (srcEntry == null) { Console.Error.WriteLine("level add-entity: --copy-from asset not found: " + copyFrom.Substring(0, at)); return 1; }
                foreach (object o in App.AssetManager.GetEbx(srcEntry).Objects)
                {
                    try { if ((Guid)((dynamic)o).GetInstanceGuid().ExportedGuid == srcGuid) { copySource = o; break; } } catch { }
                }
                if (copySource == null) { Console.Error.WriteLine("level add-entity: no instance " + srcGuid + " in " + srcEntry.Name); return 1; }
                if (typeName != null && copySource.GetType().Name != typeName)
                { Console.Error.WriteLine("level add-entity: --copy-from object is a " + copySource.GetType().Name + ", not " + typeName); return 1; }
                entity = copySource;    // replaced by the copy below, once the target ids are known
            }
            else
            {
                try { entity = EbxWriteCommand.CreateObjectByType(typeName); }
                catch (Exception ex) { Console.Error.WriteLine("level add-entity: type creation failed: " + Describe(ex)); return 1; }
            }
            if (entity == null)
            { Console.Error.WriteLine("level add-entity: type not found in the loaded SDK: " + typeName); return 1; }
            // Layer objects are EntityData or GameObjectData (ReferenceObjectData, the placed-blueprint
            // object, derives from the latter).
            if (!InheritsFrom(entity.GetType(), "EntityData") && !InheritsFrom(entity.GetType(), "GameObjectData"))
            {
                Console.Error.WriteLine("level add-entity: type '" + entity.GetType().Name + "' is not an EntityData or GameObjectData type");
                return 1;
            }

            int internalId;
            Guid exportedGuid = Guid.NewGuid();
            if (copySource != null && DryRun.Active)
            {
                DryRun.Report(logger, "copy {0} from {1} into layer {2}", copySource.GetType().Name, copyFrom, layerPath);
                return 0;
            }
            if (copySource != null)
            {
                // the copier registers the copy (and every internal object it reaches) in the target partition, with
                // fresh ids; an exported source gets a new exported guid. Flags keep the source's (callers set their own).
                try
                {
                    var copier = new PortImportGroup.GraphCopier(asset, NextInternalId(asset));
                    entity = copier.Copy(copySource);
                    dynamic ig = ((dynamic)entity).GetInstanceGuid();
                    exportedGuid = (Guid)ig.ExportedGuid; internalId = (int)ig.InternalId;
                    logger.Log("level add-entity: copied " + copySource.GetType().Name + " from " + copyFrom + " (" + copier.Created + " object(s))");
                }
                catch (Exception ex) { Console.Error.WriteLine("level add-entity: copy failed: " + Describe(ex)); return 1; }
            }
            else try
            {
                internalId = NextInternalId(asset);
                MethodInfo setGuid = entity.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                if (setGuid == null) throw new InvalidOperationException("type exposes no SetInstanceGuid method");
                setGuid.Invoke(entity, new object[] { new AssetClassGuid(exportedGuid, internalId) });
            }
            catch (Exception ex) { Console.Error.WriteLine("level add-entity: GUID assignment failed: " + Describe(ex)); return 1; }

            if (hasTransform && !WriteTransform(entity, transformField, transform))
            {
                Console.Error.WriteLine("level add-entity: type '" + entity.GetType().Name +
                    "' has no writable transform at '" + transformField + "'");
                return 1;
            }

            foreach (KeyValuePair<string, string> assignment in parsedAssignments)
            {
                string value = assignment.Value;
                if (assignment.Key == "Flags" && value.StartsWith(GuidFlagsToken, StringComparison.Ordinal))
                {
                    uint high = uint.Parse(value.Substring(GuidFlagsToken.Length), CultureInfo.InvariantCulture);
                    uint low = uint.Parse(exportedGuid.ToString("N").Substring(0, 8), NumberStyles.HexNumber) & 0xFFFFFFu;
                    value = ((high << 24) | low).ToString(CultureInfo.InvariantCulture);
                    logger.Log("level add-entity: Flags = 0x{0:X8} (0x{1:X2} << 24 | guid {2})", (high << 24) | low, high, exportedGuid);
                }
                try { PropertyPathWriter.SetValue(entity, assignment.Key, value); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("level add-entity: could not set '" + assignment.Key + "': " + Describe(ex));
                    return 1;
                }
            }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "add {0} to layer {1} (internal id {2})", entity.GetType().Name, layerPath, internalId);
                return 0;
            }

            PointerRef reference;
            try { reference = new PointerRef(entity); }
            catch (Exception ex) { Console.Error.WriteLine("level add-entity: could not create internal reference: " + Describe(ex)); return 1; }

            int listIndex;
            try { listIndex = objects.Add(reference); }
            catch (Exception ex) { Console.Error.WriteLine("level add-entity: could not append to Objects: " + Describe(ex)); return 1; }

            if (copySource == null)
            {
                try { asset.AddObject(entity, false); InProcessObjects.Mark(entity); }
                catch (Exception ex)
                {
                    try { objects.RemoveAt(listIndex); } catch { }
                    Console.Error.WriteLine("level add-entity: could not register the entity in the EBX partition: " + Describe(ex));
                    return 1;
                }
            }

            if (!EbxWriteCommand.CallModifyEbx(layerPath, asset, logger))
            {
                // ModifyEbx can fail after both in-memory collections were changed. Undo those
                // changes so a later command in the same process cannot observe a half-added entity.
                try { objects.RemoveAt(listIndex); } catch { }
                try
                {
                    IList partitionObjects = asset.Objects as IList;
                    if (partitionObjects != null) partitionObjects.Remove(entity);
                }
                catch { }
                return 1;
            }

            formatter.WriteResult("OK", "level add-entity", new Dictionary<string, string>
            {
                { "layer", layerPath },
                { "type", entity.GetType().Name },
                { "guid", exportedGuid.ToString() },
                { "internal_id", internalId.ToString(CultureInfo.InvariantCulture) },
                { "index", listIndex.ToString(CultureInfo.InvariantCulture) },
                { "count", objects.Count.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // A mesh drawn somewhere in a level, in world space.
        public sealed class MeshPlacement
        {
            public string Mesh, Source;
            public float[] Right, Up, Forward, Trans;
        }

        // Every mesh an asset draws -- static model group members (EBX- or Havok-placed), object
        // references, prefabs and what they nest -- composed into world space. The meshes of a
        // blueprint are its model entities' Mesh (StaticModelEntityData and kin) at their own
        // Transform, plus whatever the blueprint itself places; each blueprint is read once.
        public static List<MeshPlacement> CollectMeshes(string ebxPath, int maxDepth, out List<string> skipped, out string error)
        {
            skipped = new List<string>(); error = null;
            if (App.AssetManager == null) { error = "AssetManager not initialized"; return null; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { error = "entry not found: " + ebxPath; return null; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { error = "load failed: " + Describe(ex); return null; }
            var walker = new MeshWalker(maxDepth);
            var result = new List<MeshPlacement>();
            walker.Collect(asset.RootObject, ebxPath, LightPort.Identity(), result);
            skipped = walker.Skipped;
            return result;
        }

        private sealed class MeshWalker
        {
            private readonly int maxDepth;
            private readonly Dictionary<string, List<MeshPlacement>> blueprintMeshes =
                new Dictionary<string, List<MeshPlacement>>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Skipped = new List<string>();

            public MeshWalker(int maxDepth) { this.maxDepth = maxDepth; }

            public void Collect(object root, string source, LinearTransformData parent, List<MeshPlacement> result)
            {
                AddDirect(root, source, parent, result);
                AddPlacements(root, source, parent, 0, result);
            }

            // Model entities held by this asset itself (an object blueprint's root, a prefab's objects).
            private void AddDirect(object root, string source, LinearTransformData parent, List<MeshPlacement> result)
            {
                var candidates = new List<object>();
                IList list = GetEntityList(root);
                if (list != null) foreach (object raw in list) candidates.Add(raw);
                object single = ReadMember(root, "Object");
                if (single != null) candidates.Add(single);
                foreach (object raw in candidates)
                {
                    object obj = ResolveInternal(raw) ?? raw;
                    if (obj == null) continue;
                    object meshRef = ReadMember(obj, "Mesh");
                    if (meshRef == null) continue;
                    string mesh = ResolveBlueprintPath(meshRef);
                    if (string.IsNullOrEmpty(mesh)) continue;
                    Guid dangling;
                    if (Guid.TryParse(mesh, out dangling)) continue;
                    LinearTransformData world = LightPort.Compose(ReadEntityTransform(obj, "Transform"), parent);
                    result.Add(new MeshPlacement { Mesh = mesh, Source = source, Right = world.Right, Up = world.Up, Forward = world.Forward, Trans = world.Trans });
                }
            }

            private void AddPlacements(object root, string source, LinearTransformData parent, int depth, List<MeshPlacement> result)
            {
                var rows = new List<EntityRow>();
                string warning;
                if (!TryCollectBfiiRows(root, rows, null, int.MaxValue, out warning))
                {
                    IList entities = GetEntityList(root);
                    if (entities != null)
                        foreach (object raw in entities)
                        {
                            object ent = ResolveInternal(raw) ?? raw;
                            if (ent == null) continue;
                            EntityRow row = ExtractRow(ent);
                            if (row != null) rows.Add(row);
                        }
                }
                else if (warning != null) Skipped.Add(source + ": " + warning);
                foreach (EntityRow row in rows)
                {
                    if (string.IsNullOrEmpty(row.Blueprint)) continue;
                    Guid dangling;
                    if (Guid.TryParse(row.Blueprint, out dangling)) continue;
                    List<MeshPlacement> inner = MeshesOfBlueprint(row.Blueprint, depth + 1);
                    if (inner.Count == 0) continue;
                    LinearTransformData world = LightPort.Compose(new LinearTransformData { Right = row.Right, Up = row.Up, Forward = row.Forward, Trans = row.Trans }, parent);
                    foreach (MeshPlacement local in inner)
                    {
                        LinearTransformData lt = LightPort.Compose(new LinearTransformData { Right = local.Right, Up = local.Up, Forward = local.Forward, Trans = local.Trans }, world);
                        result.Add(new MeshPlacement { Mesh = local.Mesh, Source = local.Source, Right = lt.Right, Up = lt.Up, Forward = lt.Forward, Trans = lt.Trans });
                    }
                }
            }

            private List<MeshPlacement> MeshesOfBlueprint(string path, int depth)
            {
                List<MeshPlacement> cached;
                if (blueprintMeshes.TryGetValue(path, out cached)) return cached;
                var result = new List<MeshPlacement>();
                if (depth > maxDepth) { Skipped.Add(path + ": --max-depth " + maxDepth + " reached"); return result; }
                if (inProgress.Contains(path)) return result;
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
                if (entry == null) { Skipped.Add(path + ": not found"); blueprintMeshes[path] = result; return result; }
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch (Exception ex) { Skipped.Add(path + ": " + ex.Message); blueprintMeshes[path] = result; return result; }
                inProgress.Add(path);
                AddDirect(asset.RootObject, path, LightPort.Identity(), result);
                AddPlacements(asset.RootObject, path, LightPort.Identity(), depth, result);
                inProgress.Remove(path);
                blueprintMeshes[path] = result;
                return result;
            }
        }

        // The Pbr lights an asset reaches -- its own, its layers' and sub-worlds', and those inside the
        // blueprints it places -- as world-space records (what `level export-lights --types lights` writes).
        public static List<LightRecord> CollectLights(string ebxPath, int maxDepth, out List<string> skipped, out string error)
        {
            skipped = new List<string>(); error = null;
            if (App.AssetManager == null) { error = "AssetManager not initialized"; return null; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { error = "entry not found: " + ebxPath; return null; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { error = "load failed: " + Describe(ex); return null; }
            var walker = new LightWalker(maxDepth, LightPort.IsLightEntityType);
            var records = new List<LightRecord>();
            walker.Collect(asset.RootObject, ebxPath, LightPort.Identity(), records);
            skipped = walker.Skipped;
            return records;
        }

        // Walks a layer, sub-world, level or blueprint and writes every Pbr*LightEntityData it can
        // reach as world-space records. Lights inside placed blueprints (prefabs, light props) are
        // composed through the placement transform, including Havok-backed static group instances.
        public static int ExecuteExportLights(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string sourcePath = null, outFile = null;
            int maxDepth = 4;
            // --types: comma-separated entity type names to capture, "lights" for emitters only.
            // Default is every per-instance lighting entity (lights, reflection volumes, probes, flares).
            string types = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--out" || arg == "--max-depth" || arg == "--types")
                {
                    if (i + 1 >= args.Length)
                    { Console.Error.WriteLine("level export-lights: " + arg + " requires a value"); return 1; }
                    string value = args[++i];
                    if (arg == "--out") outFile = value;
                    else if (arg == "--types") types = value;
                    else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDepth) || maxDepth < 0)
                    { Console.Error.WriteLine("level export-lights: --max-depth must be zero or greater"); return 1; }
                }
                else if (arg.StartsWith("--", StringComparison.Ordinal))
                { Console.Error.WriteLine("level export-lights: unknown option '" + arg + "'"); return 1; }
                else if (sourcePath == null) sourcePath = arg;
                else
                { Console.Error.WriteLine("level export-lights: unexpected argument '" + arg + "'"); return 1; }
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            { Console.Error.WriteLine("level export-lights: missing <ebx-path>"); return 1; }
            if (App.AssetManager == null)
            { Console.Error.WriteLine("level export-lights: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(sourcePath);
            if (entry == null)
            { Console.Error.WriteLine("level export-lights: entry not found: " + sourcePath); return 1; }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level export-lights: load failed: " + Describe(ex)); return 1; }

            var walker = new LightWalker(maxDepth, LightPort.TypeFilter(types));
            var records = new List<LightRecord>();
            walker.Collect(asset.RootObject, sourcePath, LightPort.Identity(), records);

            string json = JsonConvert.SerializeObject(new
            {
                source = sourcePath,
                count = records.Count,
                skipped = walker.Skipped,
                lights = records
            }, Newtonsoft.Json.Formatting.Indented);

            if (outFile == null)
            {
                Console.Out.WriteLine(json);
                return 0;
            }

            try { File.WriteAllText(outFile, json); }
            catch (Exception ex) { Console.Error.WriteLine("level export-lights: could not write " + outFile + ": " + ex.Message); return 1; }

            formatter.WriteResult("OK", "level export-lights", new Dictionary<string, string>
            {
                { "source", sourcePath },
                { "count", records.Count.ToString(CultureInfo.InvariantCulture) },
                { "skipped", walker.Skipped.Count.ToString(CultureInfo.InvariantCulture) },
                { "out", outFile }
            });
            return 0;
        }

        // Recreates exported light records as new entities in a layer. Only fields the target type
        // actually exposes are copied, so a 2015 record lands on a 2017 type with the newer fields
        // left at their defaults. Requires a loaded project; use --save / --save-as to keep it.
        // Values Cloud City's own shadow-casting spot lights ship with (measured 2026-09-06).
        private static readonly KeyValuePair<string, string>[] LightDefaults =
        {
            new KeyValuePair<string, string>("ShadowMaxAngle", "150"),
            new KeyValuePair<string, string>("ShadowNearRadius", "0.2"),
            new KeyValuePair<string, string>("ShadowFadeOutRange", "10"),
            new KeyValuePair<string, string>("CullScreenArea", "0.005"),
            new KeyValuePair<string, string>("FadeScreenArea", "0.01"),
            new KeyValuePair<string, string>("ShadowResolution", "QualityLevel_Medium"),
        };

        public static int ExecuteImportLights(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string blueprintMapFile = null;
            string layerPath = null, fromFile = null, offsetArg = null;
            float intensityScale = 1f;
            bool skipDisabled = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--from" || arg == "--intensity-scale" || arg == "--offset" || arg == "--blueprint-map")
                {
                    if (i + 1 >= args.Length)
                    { Console.Error.WriteLine("level import-lights: " + arg + " requires a value"); return 1; }
                    string value = args[++i];
                    if (arg == "--from") fromFile = value;
                    else if (arg == "--blueprint-map") blueprintMapFile = value;
                    else if (arg == "--offset") offsetArg = value;
                    else if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out intensityScale) || intensityScale <= 0f)
                    { Console.Error.WriteLine("level import-lights: --intensity-scale must be a positive number"); return 1; }
                }
                else if (arg == "--skip-disabled") skipDisabled = true;
                else if (arg.StartsWith("--", StringComparison.Ordinal))
                { Console.Error.WriteLine("level import-lights: unknown option '" + arg + "'"); return 1; }
                else if (layerPath == null) layerPath = arg;
                else
                { Console.Error.WriteLine("level import-lights: unexpected argument '" + arg + "'"); return 1; }
            }

            if (string.IsNullOrWhiteSpace(layerPath))
            { Console.Error.WriteLine("level import-lights: missing <layer-ebx-path>"); return 1; }
            if (string.IsNullOrWhiteSpace(fromFile))
            { Console.Error.WriteLine("level import-lights: --from <lights.json> is required"); return 1; }

            float[] offset = null;
            if (offsetArg != null)
            {
                try { offset = ParseVec3(offsetArg); }
                catch (ArgumentException ex) { Console.Error.WriteLine("level import-lights: --offset " + ex.Message); return 1; }
            }

            if (!File.Exists(fromFile))
            { Console.Error.WriteLine("level import-lights: file not found: " + fromFile); return 1; }

            // --blueprint-map: JSON { "<source path>": "<target path>" } for *ReferenceObjectData
            // records whose blueprint lives at a different path in this game (2015 "XP2/FX/..." vs
            // BFII "FX/..."), or which need substituting with the nearest shipped equivalent.
            var blueprintMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(blueprintMapFile))
            {
                try
                {
                    var jo = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(blueprintMapFile));
                    foreach (var kv in jo) blueprintMap[kv.Key] = (string)kv.Value;
                }
                catch (Exception ex)
                { Console.Error.WriteLine("level import-lights: --blueprint-map: " + ex.Message); return 1; }
            }
            int blueprintsSet = 0, blueprintsMissing = 0;
            List<LightRecord> records;
            try
            {
                JToken parsed = JToken.Parse(File.ReadAllText(fromFile));
                JToken list = parsed.Type == JTokenType.Object ? parsed["lights"] : parsed;
                if (list == null || list.Type != JTokenType.Array)
                    throw new InvalidOperationException("expected a 'lights' array");
                records = list.ToObject<List<LightRecord>>();
            }
            catch (Exception ex) { Console.Error.WriteLine("level import-lights: could not read " + fromFile + ": " + ex.Message); return 1; }

            if (!Session.Project.IsLoaded)
            { Console.Error.WriteLine("level import-lights: no project loaded. Run 'project load' or 'project new' first."); return 1; }
            if (App.AssetManager == null)
            { Console.Error.WriteLine("level import-lights: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(layerPath);
            if (entry == null)
            { Console.Error.WriteLine("level import-lights: layer not found: " + layerPath); return 1; }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level import-lights: load failed: " + Describe(ex)); return 1; }

            IList objects = GetObjectsList(asset.RootObject);
            if (objects == null)
            { Console.Error.WriteLine("level import-lights: root has no Objects list: " + DescribeRoot(asset.RootObject)); return 1; }
            if (objects.IsFixedSize || objects.IsReadOnly)
            { Console.Error.WriteLine("level import-lights: layer Objects list is not writable"); return 1; }

            int added = 0, disabledSkipped = 0, transformFailures = 0, elementsAdded = 0;
            int refsWritten = 0, refsUnresolved = 0;
            var unsupported = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var fieldsSkipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var refsSkipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var addedEntities = new List<object>();
            var addedIndices = new List<int>();

            foreach (LightRecord record in records)
            {
                if (record == null || string.IsNullOrEmpty(record.Type)) continue;
                string enabled;
                if (skipDisabled && record.Fields != null && record.Fields.TryGetValue("Enabled", out enabled) &&
                    string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
                { disabledSkipped++; continue; }

                object entity = null;
                try { entity = EbxWriteCommand.CreateObjectByType(record.Type); }
                catch { entity = null; }
                // Lights and volumes are EntityData; effect/prefab placements are *ReferenceObjectData
                // with a Blueprint pointer, whose base chain differs. Anything the type library can
                // build and that exposes SetInstanceGuid is placeable, so test for that instead of
                // guessing at base-class names.
                if (entity != null && entity.GetType().GetMethod("SetInstanceGuid",
                        BindingFlags.Public | BindingFlags.Instance) == null) entity = null;
                if (entity == null)
                {
                    int n; unsupported.TryGetValue(record.Type, out n); unsupported[record.Type] = n + 1;
                    continue;
                }

                int internalId;
                Guid exportedGuid = Guid.NewGuid();
                try
                {
                    internalId = NextInternalId(asset);
                    MethodInfo setGuid = entity.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                    if (setGuid == null) throw new InvalidOperationException("type exposes no SetInstanceGuid method");
                    setGuid.Invoke(entity, new object[] { new AssetClassGuid(exportedGuid, internalId) });
                }
                catch (Exception ex) { Console.Error.WriteLine("level import-lights: GUID assignment failed: " + Describe(ex)); return 1; }

                LinearTransformData transform = LightPort.Compose(new LinearTransformData
                {
                    Right = record.Right, Up = record.Up, Forward = record.Forward, Trans = record.Trans
                }, LightPort.Identity());
                if (offset != null)
                    transform.Trans = new[] { transform.Trans[0] + offset[0], transform.Trans[1] + offset[1], transform.Trans[2] + offset[2] };
                // EntityData carries "Transform"; a *ReferenceObjectData carries "BlueprintTransform".
                if (!WriteTransform(entity, "Transform", transform)
                    && !WriteTransform(entity, "BlueprintTransform", transform)) transformFailures++;

                // "ebx:<asset path>" values are references (e.g. a reflection volume's BakedTexture); they are set once the
                // entity is in the partition, below.
                var pendingRefs = new List<KeyValuePair<string, string>>();
                string pendingElements = null;
                if (record.Fields != null)
                {
                    foreach (KeyValuePair<string, string> field in record.Fields)
                    {
                        if (LightPort.SkipOnImport.Contains(field.Key)) continue;
                        if (field.Key == "Elements" && field.Value != null && field.Value.StartsWith("[", StringComparison.Ordinal))
                        {
                            pendingElements = field.Value;
                            continue;
                        }
                        if (field.Value != null && field.Value.StartsWith("ebx:", StringComparison.Ordinal))
                        {
                            pendingRefs.Add(new KeyValuePair<string, string>(field.Key, field.Value.Substring(4)));
                            continue;
                        }
                        if (!CopyLightField(entity, field.Key, field.Value, intensityScale))
                        {
                            int n; fieldsSkipped.TryGetValue(field.Key, out n); fieldsSkipped[field.Key] = n + 1;
                        }
                    }
                }
                // BFII-only fields a 2015 light cannot carry default to 0 on the new entity, and
                // ShadowMaxAngle 0 means a spot casts no shadow at all while CullScreenArea 0 never culls.
                // Fill them from Cloud City's own shadow-casting spots unless the source provided a value.
                foreach (KeyValuePair<string, string> def in LightDefaults)
                {
                    if (record.Fields != null && record.Fields.ContainsKey(def.Key)) continue;
                    CopyLightField(entity, def.Key, def.Value, 1f);
                }
                // Shipped entities never carry Flags 0: Flags = (instanceGuid.Data1 & 0xFFFFFF) | (flagBits << 24).
                // Copying the source's Flags verbatim keeps ITS guid bits, so rebuild the low 24 from our new guid
                // and keep only the source's real flag byte (memory bfii-entity-flags-field).
                {
                    uint sourceFlags = 0;
                    string flagText;
                    if (record.Fields != null && record.Fields.TryGetValue("Flags", out flagText))
                        uint.TryParse(flagText, NumberStyles.Integer, CultureInfo.InvariantCulture, out sourceFlags);
                    uint data1 = BitConverter.ToUInt32(exportedGuid.ToByteArray(), 0);
                    uint newFlags = (data1 & 0x00FFFFFFu) | (sourceFlags & 0xFF000000u);
                    CopyLightField(entity, "Flags", newFlags.ToString(CultureInfo.InvariantCulture), 1f);
                }

                if (record.Refs != null)
                {
                    foreach (LightAssetRef assetRef in record.Refs)
                    {
                        if (assetRef == null || string.IsNullOrEmpty(assetRef.Field)) continue;
                        string why;
                        if (WriteLightRef(entity, assetRef, out why)) refsWritten++;
                        else
                        {
                            refsUnresolved++;
                            string key = assetRef.Field + " -> " + (assetRef.Path ?? assetRef.FileGuid) + " (" + why + ")";
                            int n; refsSkipped.TryGetValue(key, out n); refsSkipped[key] = n + 1;
                        }
                    }
                }

                if (DryRun.Active)
                {
                    DryRun.Report(logger, "add {0} to layer {1} (internal id {2})", entity.GetType().Name, layerPath, internalId);
                    added++;
                    continue;
                }

                PointerRef reference;
                try { reference = new PointerRef(entity); }
                catch (Exception ex) { Console.Error.WriteLine("level import-lights: could not create internal reference: " + Describe(ex)); RollbackImport(objects, asset, addedIndices, addedEntities); return 1; }

                int listIndex;
                try { listIndex = objects.Add(reference); }
                catch (Exception ex) { Console.Error.WriteLine("level import-lights: could not append to Objects: " + Describe(ex)); RollbackImport(objects, asset, addedIndices, addedEntities); return 1; }

                foreach (KeyValuePair<string, string> pending in pendingRefs)
                {
                    EbxAssetEntry refEntry = App.AssetManager.GetEbxEntry(pending.Value);
                    if (refEntry == null)
                    {
                        int n; fieldsSkipped.TryGetValue(pending.Key + " (ref not found)", out n); fieldsSkipped[pending.Key + " (ref not found)"] = n + 1;
                        continue;
                    }
                    try
                    {
                        dynamic refRoot = App.AssetManager.GetEbx(refEntry).RootObject;
                        FrostyCLI.Core.EbxRefWriter.SetRefByPath(entity, pending.Key, refEntry.Guid, (Guid)refRoot.GetInstanceGuid().ExportedGuid);
                        asset.AddDependency(refEntry.Guid);
                    }
                    catch
                    {
                        int n; fieldsSkipped.TryGetValue(pending.Key + " (ref failed)", out n); fieldsSkipped[pending.Key + " (ref failed)"] = n + 1;
                    }
                }
                if (pendingElements != null)
                {
                    int built;
                    try { built = ImportElements(entity, "Elements", pendingElements, asset, fieldsSkipped); }
                    catch (Exception ex) { built = -1; Console.Error.WriteLine("level import-lights: Elements: " + Describe(ex)); }
                    if (built < 0) BumpCount(fieldsSkipped, "Elements");
                    else elementsAdded += built;
                }

                // A reference object is inert without its Blueprint: point it at the target game's
                // asset (via --blueprint-map where the path differs) before it goes into the partition.
                if (!string.IsNullOrEmpty(record.Blueprint))
                {
                    string want;
                    if (!blueprintMap.TryGetValue(record.Blueprint, out want)) want = record.Blueprint;
                    EbxAssetEntry bpEntry = string.IsNullOrEmpty(want) ? null : App.AssetManager.GetEbxEntry(want);
                    if (bpEntry == null) { blueprintsMissing++; continue; }
                    try
                    {
                        EbxAsset bpAsset = App.AssetManager.GetEbx(bpEntry);
                        dynamic bpRoot = bpAsset.RootObject;
                        FrostyCLI.Core.EbxRefWriter.SetRefByPath(entity, "Blueprint", bpEntry.Guid,
                            (Guid)bpRoot.GetInstanceGuid().ExportedGuid);
                        asset.AddDependency(bpEntry.Guid);
                        blueprintsSet++;
                    }
                    catch { blueprintsMissing++; continue; }
                }
                try { asset.AddObject(entity, false); InProcessObjects.Mark(entity); }
                catch (Exception ex)
                {
                    try { objects.RemoveAt(listIndex); } catch { }
                    Console.Error.WriteLine("level import-lights: could not register the entity in the EBX partition: " + Describe(ex));
                    RollbackImport(objects, asset, addedIndices, addedEntities);
                    return 1;
                }
                addedIndices.Add(listIndex);
                addedEntities.Add(entity);
                added++;
            }

            if (!DryRun.Active && addedEntities.Count > 0 && !EbxWriteCommand.CallModifyEbx(layerPath, asset, logger))
            {
                RollbackImport(objects, asset, addedIndices, addedEntities);
                return 1;
            }

            var unsupportedText = new List<string>();
            foreach (KeyValuePair<string, int> kv in unsupported) unsupportedText.Add(kv.Key + ":" + kv.Value);
            var skippedText = new List<string>();
            foreach (KeyValuePair<string, int> kv in fieldsSkipped) skippedText.Add(kv.Key + ":" + kv.Value);
            var refsSkippedText = new List<string>();
            foreach (KeyValuePair<string, int> kv in refsSkipped) refsSkippedText.Add(kv.Key + ":" + kv.Value);

            formatter.WriteResult("OK", "level import-lights", new Dictionary<string, string>
            {
                { "layer", layerPath },
                { "from", fromFile },
                { "records", records.Count.ToString(CultureInfo.InvariantCulture) },
                { "blueprints_set", blueprintsSet.ToString(CultureInfo.InvariantCulture) },
                { "blueprints_missing", blueprintsMissing.ToString(CultureInfo.InvariantCulture) },
                { "added", added.ToString(CultureInfo.InvariantCulture) },
                { "disabled_skipped", disabledSkipped.ToString(CultureInfo.InvariantCulture) },
                { "unsupported_types", unsupportedText.Count == 0 ? "none" : string.Join(", ", unsupportedText) },
                { "fields_skipped", skippedText.Count == 0 ? "none" : string.Join(", ", skippedText) },
                { "refs_written", refsWritten.ToString(CultureInfo.InvariantCulture) },
                { "refs_unresolved", refsUnresolved.ToString(CultureInfo.InvariantCulture) },
                { "refs_skipped", refsSkippedText.Count == 0 ? "none" : string.Join(", ", refsSkippedText) },
                { "transform_failures", transformFailures.ToString(CultureInfo.InvariantCulture) },
                { "elements_added", elementsAdded.ToString(CultureInfo.InvariantCulture) },
                { "intensity_scale", intensityScale.ToString("R", CultureInfo.InvariantCulture) },
                { "count", objects.Count.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // Undo the in-memory half of a failed import so a later command in the same process does
        // not see partially added lights. Indices are removed from the end so earlier ones stay valid.
        private static void RollbackImport(IList objects, EbxAsset asset, List<int> indices, List<object> entities)
        {
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                try { objects.RemoveAt(indices[i]); } catch { }
            }
            IList partitionObjects = asset.Objects as IList;
            if (partitionObjects == null) return;
            foreach (object entity in entities)
            {
                try { partitionObjects.Remove(entity); } catch { }
            }
        }

        // Writes one exported field onto a target entity. Returns false when the property does not
        // exist on the target type or the value cannot be coerced, so the caller can report it.
        private static bool CopyLightField(object entity, string name, string value, float intensityScale)
        {
            PropertyInfo prop = entity.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) return false;

            string text = value ?? string.Empty;
            if (string.Equals(name, "Intensity", StringComparison.Ordinal) && intensityScale != 1f)
                text = LightPort.ScaleValue(text, intensityScale);

            float[] vec;
            string vectorType = prop.PropertyType.Name;
            if ((vectorType == "Vec2" || vectorType == "Vec3" || vectorType == "Vec4") && LightPort.TryParseVector(text, out vec))
                return SetFieldText(entity, name, text);

            try { PropertyPathWriter.SetValue(entity, name, text); return true; }
            catch { return false; }
        }

        // Reads a LinearTransform-typed member into the shared transform struct.
        private static LinearTransformData ReadEntityTransform(object entity, string field)
        {
            float[] r = { 1, 0, 0 }, u = { 0, 1, 0 }, f = { 0, 0, 1 }, t = { 0, 0, 0 };
            object tf = ReadMember(entity, field);
            if (tf != null) ReadTransform(tf, ref r, ref u, ref f, ref t);
            return new LinearTransformData { Right = r, Up = u, Forward = f, Trans = t };
        }

        // Captures every scalar, enum, string and Vec2/3/4 property of a light so it can be replayed by
        // name. External refs (a rect light's mask Texture, a volume's BakedTexture) travel as "ebx:<path>":
        // import-lights resolves them by path in the target game and skips the ones that do not exist
        // there, so a 2015-only IES profile costs nothing. A lens flare's Elements list is written as
        // JSON (see ExportElements); other lists and nested structs are still left out.
        private static void ExportScalarFields(object entity, Dictionary<string, string> fields)
        {
            foreach (PropertyInfo prop in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                if (prop.GetIndexParameters().Length != 0) continue;
                if (prop.Name == "Transform" || prop.Name == "BlueprintTransform") continue;
                object value;
                try { value = prop.GetValue(entity); } catch { continue; }
                if (value == null) continue;
                Type type = prop.PropertyType;
                if (type.IsEnum) fields[prop.Name] = value.ToString();
                else if (type == typeof(string)) fields[prop.Name] = (string)value;
                else if (type == typeof(float) || type == typeof(double))
                    fields[prop.Name] = Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                else if (type.IsPrimitive) fields[prop.Name] = Convert.ToString(value, CultureInfo.InvariantCulture);
                else if (type.Name == "Vec3")
                {
                    float[] v = ExtractVec3Components(value);
                    if (v != null) fields[prop.Name] = LightPort.FormatVec3(v);
                }
                else if (type.Name == "Vec2" || type.Name == "Vec4")
                {
                    float[] v = ExtractVectorComponents(value, type.Name == "Vec2" ? 2 : 4);
                    if (v != null) fields[prop.Name] = LightPort.FormatVector(v);
                }
                else if (type.Name == "PointerRef")
                {
                    // Blueprint is carried by LightRecord.Blueprint and remapped by --blueprint-map.
                    if (prop.Name == "Blueprint") continue;
                    string path = ExternalRefPath(value);
                    if (path != null) fields[prop.Name] = "ebx:" + path;
                }
                else if (prop.Name == "Elements" && value is IList)
                {
                    string elements = ExportElements((IList)value);
                    if (elements != null) fields[prop.Name] = elements;
                }
            }
        }

        // The asset path an external PointerRef names, or null for null/internal refs and for guids the
        // asset manager cannot resolve (ResolveBlueprintPath hands those back as the bare guid string).
        private static string ExternalRefPath(object pointerRef)
        {
            string path;
            try { path = ResolveBlueprintPath(pointerRef); } catch { return null; }
            Guid asGuid;
            if (string.IsNullOrEmpty(path) || Guid.TryParse(path, out asGuid)) return null;
            return path;
        }

        // A lens flare's Elements as a JSON array with one { "dot.path": "text" } object per element: numbers,
        // bools and enums by value, Vec2/3/4 as "x,y[,z[,w]]", external refs as "ebx:<path>", and one level of
        // nested struct flattened (the element's SurfaceShaderInstanceDataStruct becomes "Shader.Shader").
        // The shader's parameter arrays are not carried; the Sullust flares have none.
        private static string ExportElements(IList list)
        {
            var array = new JArray();
            foreach (object raw in list)
            {
                object element = ResolveInternal(raw) ?? raw;
                if (element == null) continue;
                var obj = new JObject();
                AppendElementFields(element, "", obj, 0);
                if (obj.Count > 0) array.Add(obj);
            }
            return array.Count == 0 ? null : array.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static void AppendElementFields(object value, string prefix, JObject into, int depth)
        {
            foreach (PropertyInfo prop in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name.StartsWith("__", StringComparison.Ordinal) || prop.GetIndexParameters().Length != 0) continue;
                object v;
                try { v = prop.GetValue(value); } catch { continue; }
                if (v == null) continue;
                Type type = prop.PropertyType;
                string key = prefix + prop.Name;
                if (type.IsEnum) into[key] = v.ToString();
                else if (type == typeof(string) || type.Name == "CString") into[key] = v.ToString();
                else if (type == typeof(bool)) into[key] = (bool)v ? "True" : "False";
                else if (type == typeof(float) || type == typeof(double))
                    into[key] = Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                else if (type.IsPrimitive) into[key] = Convert.ToString(v, CultureInfo.InvariantCulture);
                else if (type.Name == "Vec2" || type.Name == "Vec3" || type.Name == "Vec4")
                {
                    float[] c = ExtractVectorComponents(v, type.Name == "Vec2" ? 2 : type.Name == "Vec3" ? 3 : 4);
                    if (c != null) into[key] = LightPort.FormatVector(c);
                }
                else if (type.Name == "PointerRef")
                {
                    string path = ExternalRefPath(v);
                    if (path != null) into[key] = "ebx:" + path;
                }
                else if (depth == 0 && !(v is IList) && type.Name != "AssetClassGuid")
                    AppendElementFields(v, key + ".", into, depth + 1);
            }
        }

        private static float[] ExtractVectorComponents(object v, int count)
        {
            if (v == null) return null;
            float[] result = new float[count];
            for (int i = 0; i < count; i++)
            {
                object c = ReadMember(v, LightPort.VectorAxes[i]) ?? ReadMember(v, LightPort.VectorAxes[i].ToUpperInvariant());
                if (c == null) return null;
                try { result[i] = Convert.ToSingle(c, CultureInfo.InvariantCulture); } catch { return null; }
            }
            return result;
        }

        // Rebuilds a list written by ExportElements on a new entity and returns how many elements were added,
        // or -1 when the entity has no such list. Refs this game cannot resolve stay null and are counted.
        private static int ImportElements(object entity, string name, string json, EbxAsset asset, SortedDictionary<string, int> skipped)
        {
            PropertyInfo prop = entity.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null) return -1;
            IList list = prop.GetValue(entity) as IList;
            if (list == null)
            {
                if (!prop.CanWrite) return -1;
                list = (IList)Activator.CreateInstance(prop.PropertyType);
                prop.SetValue(entity, list);
            }
            Type elementType = PropertyPathWriter.ListElementType(list);
            int added = 0;
            foreach (JToken token in JArray.Parse(json))
            {
                JObject obj = token as JObject;
                if (obj == null) continue;
                object element = Activator.CreateInstance(elementType);
                foreach (KeyValuePair<string, JToken> kv in obj)
                {
                    string text = kv.Value == null ? null : kv.Value.ToString();
                    if (text == null) continue;
                    if (text.StartsWith("ebx:", StringComparison.Ordinal))
                    {
                        EbxAssetEntry refEntry = App.AssetManager.GetEbxEntry(text.Substring(4));
                        if (refEntry == null) { BumpCount(skipped, name + "." + kv.Key + " (ref not found)"); continue; }
                        try
                        {
                            dynamic refRoot = App.AssetManager.GetEbx(refEntry).RootObject;
                            FrostyCLI.Core.EbxRefWriter.SetRefByPath(element, kv.Key, refEntry.Guid, (Guid)refRoot.GetInstanceGuid().ExportedGuid);
                            asset.AddDependency(refEntry.Guid);
                        }
                        catch { BumpCount(skipped, name + "." + kv.Key + " (ref failed)"); }
                        continue;
                    }
                    if (!SetFieldText(element, kv.Key, text)) BumpCount(skipped, name + "." + kv.Key);
                }
                list.Add(element);
                added++;
            }
            return added;
        }

        // Sets a dot-path member from its exported text; "x,y[,z[,w]]" goes component by component.
        private static bool SetFieldText(object target, string dotPath, string text)
        {
            float[] vec;
            if (text.IndexOf(',') >= 0 && LightPort.TryParseVector(text, out vec))
            {
                for (int i = 0; i < vec.Length; i++)
                {
                    string component = vec[i].ToString("R", CultureInfo.InvariantCulture);
                    bool written = false;
                    foreach (string axis in new[] { LightPort.VectorAxes[i], LightPort.VectorAxes[i].ToUpperInvariant() })
                    {
                        try { PropertyPathWriter.SetValue(target, dotPath + "." + axis, component); written = true; break; }
                        catch { }
                    }
                    if (!written) return false;
                }
                return true;
            }
            try { PropertyPathWriter.SetValue(target, dotPath, text); return true; }
            catch { return false; }
        }

        private static void BumpCount(SortedDictionary<string, int> counts, string key)
        {
            int n; counts.TryGetValue(key, out n); counts[key] = n + 1;
        }

        // Rewires one recorded target onto the new light. Resolution is by file GUID first: the two
        // games ship shared content under identical GUIDs, so this is what recognises an asset the
        // target already has, whatever it is called there. The recorded path is a fallback for an
        // asset that was kept but renamed. An unresolved ref is reported rather than written, since a
        // pointer at an absent asset is worse than a light with none.
        private static bool WriteLightRef(object entity, LightAssetRef assetRef, out string why)
        {
            why = null;
            Guid fileGuid;
            EbxAssetEntry target = null;

            if (Guid.TryParse(assetRef.FileGuid, out fileGuid))
            {
                try { target = App.AssetManager.GetEbxEntry(fileGuid); } catch { }
            }
            if (target == null && !string.IsNullOrEmpty(assetRef.Path))
            {
                try { target = App.AssetManager.GetEbxEntry(assetRef.Path); } catch { }
            }
            if (target == null) { why = "not in this game"; return false; }

            Guid classGuid;
            if (!Guid.TryParse(assetRef.ClassGuid, out classGuid) || classGuid == Guid.Empty)
            {
                try
                {
                    EbxAsset targetAsset = App.AssetManager.GetEbx(target);
                    dynamic root = targetAsset.RootObject;
                    classGuid = (Guid)root.GetInstanceGuid().ExportedGuid;
                }
                catch { why = "could not resolve class guid"; return false; }
            }

            try { FrostyCLI.Core.EbxRefWriter.SetRefByPath(entity, assetRef.Field, target.Guid, classGuid); }
            catch (Exception ex) { why = Describe(ex); return false; }
            return true;
        }

        // The assets a light points at -- its IES profile, a rectangular light's texture. These are
        // PointerRefs, which ExportScalarFields cannot represent, so before this they were dropped
        // silently and every ported light came back with a generic emission shape. Recorded by file
        // GUID, which is what lets the importer recognise an asset the target game already ships.
        private static void ExportPointerFields(object entity, List<LightAssetRef> refs)
        {
            foreach (PropertyInfo prop in entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                if (prop.GetIndexParameters().Length != 0) continue;
                if (prop.Name == "Transform" || prop.Name == "BlueprintTransform") continue;
                if (prop.PropertyType.Name != "PointerRef") continue;

                object value;
                try { value = prop.GetValue(entity); } catch { continue; }
                if (!HasExternalTarget(value)) continue;

                object external = ReadMember(value, "External");
                object fileGuidObj = ReadMember(external, "FileGuid");
                object classGuidObj = ReadMember(external, "ClassGuid");
                if (!(fileGuidObj is Guid)) continue;
                Guid fileGuid = (Guid)fileGuidObj;

                string path = null, targetType = null;
                EbxAssetEntry target = null;
                try { target = App.AssetManager.GetEbxEntry(fileGuid); } catch { }
                if (target != null) { path = target.Name; targetType = target.Type; }

                refs.Add(new LightAssetRef
                {
                    Field = prop.Name,
                    Path = path,
                    FileGuid = fileGuid.ToString(),
                    ClassGuid = classGuidObj is Guid ? ((Guid)classGuidObj).ToString() : null,
                    Type = targetType
                });
            }
        }

        // Finds light entities in an asset and in everything it places. Blueprint contents are
        // gathered once in blueprint space and cached, then composed per placement, so a light prop
        // placed a hundred times costs one asset load.
        private sealed class LightWalker
        {
            private readonly int maxDepth;
            // Which entity types to capture. Defaults to lights so existing callers are unchanged.
            private readonly Func<string, bool> match;
            private readonly Dictionary<string, List<LightRecord>> blueprintLights =
                new Dictionary<string, List<LightRecord>>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Skipped = new List<string>();

            public LightWalker(int maxDepth) : this(maxDepth, null) { }
            public LightWalker(int maxDepth, Func<string, bool> match)
            { this.maxDepth = maxDepth; this.match = match ?? LightPort.IsLightEntityType; }

            public void Collect(object root, string source, LinearTransformData parent, List<LightRecord> records)
            {
                CollectDirect(root, source, parent, records, true);
                CollectPlacements(root, source, parent, 0, records);
            }

            // Light entities held directly by this asset or, through container references, by the
            // layers and sub-worlds it includes. Containers carry no transform of their own.
            private void CollectDirect(object root, string source, LinearTransformData parent, List<LightRecord> records, bool followContainers)
            {
                if (root == null) return;
                var candidates = new List<object>();
                IList list = GetEntityList(root);
                if (list != null) foreach (object raw in list) candidates.Add(raw);
                object single = ReadMember(root, "Object");
                if (single != null) candidates.Add(single);

                foreach (object raw in candidates)
                {
                    object obj = ResolveInternal(raw) ?? raw;
                    if (obj == null) continue;
                    string typeName = obj.GetType().Name;
                    if (match(typeName))
                    {
                        // Entities carry "Transform"; *ReferenceObjectData carry "BlueprintTransform".
                        string tfField = ReadMember(obj, "Transform") != null ? "Transform" : "BlueprintTransform";
                        LinearTransformData world = LightPort.Compose(ReadEntityTransform(obj, tfField), parent);
                        var record = new LightRecord
                        {
                            Type = typeName, Source = source,
                            Blueprint = ResolveBlueprintPath(ReadMember(obj, "Blueprint")),
                            Right = world.Right, Up = world.Up, Forward = world.Forward, Trans = world.Trans
                        };
                        ExportScalarFields(obj, record.Fields);
                        ExportPointerFields(obj, record.Refs);
                        records.Add(record);
                        continue;
                    }
                    if (!followContainers) continue;
                    string childPath = null;
                    if (typeName == "SubWorldReferenceObjectData")
                        childPath = Convert.ToString(ReadMember(obj, "BundleName"), CultureInfo.InvariantCulture);
                    else if (typeName == "LayerReferenceObjectData" || typeName == "WorldPartReferenceObjectData")
                    {
                        // SWBF1 layers: an internal WorldPartData of this partition (identity transform in practice).
                        object part = ResolveInternal(ReadMember(obj, "Blueprint"));
                        if (part != null)
                        {
                            string partKey = source + "#" + Convert.ToString(ReadMember(part, "Name"), CultureInfo.InvariantCulture);
                            if (inProgress.Contains(partKey)) continue;
                            inProgress.Add(partKey);
                            CollectDirect(part, source, parent, records, true);
                            inProgress.Remove(partKey);
                            continue;
                        }
                        childPath = ResolveBlueprintPath(ReadMember(obj, "Blueprint"));
                    }
                    if (string.IsNullOrEmpty(childPath) || inProgress.Contains(childPath)) continue;
                    EbxAsset child = LoadAsset(childPath);
                    if (child == null) continue;
                    inProgress.Add(childPath);
                    CollectDirect(child.RootObject, childPath, parent, records, true);
                    inProgress.Remove(childPath);
                }
            }

            // Every placement the level readers know about (EBX or Havok backed, in any nested layer),
            // expanded through the placed blueprint's own lights.
            private void CollectPlacements(object root, string source, LinearTransformData parent, int depth, List<LightRecord> records)
            {
                var rows = new List<EntityRow>();
                string warning;
                if (!TryCollectBfiiRows(root, rows, null, int.MaxValue, out warning))
                {
                    IList entities = GetEntityList(root);
                    if (entities != null)
                    {
                        foreach (object raw in entities)
                        {
                            object ent = ResolveInternal(raw) ?? raw;
                            if (ent == null) continue;
                            EntityRow row = ExtractRow(ent);
                            if (row != null) rows.Add(row);
                        }
                    }
                }
                else if (warning != null) Skipped.Add(source + ": " + warning);

                foreach (EntityRow row in rows)
                {
                    if (string.IsNullOrEmpty(row.Blueprint)) continue;
                    Guid dangling;
                    if (Guid.TryParse(row.Blueprint, out dangling)) { Skipped.Add(source + ": unknown blueprint " + row.Blueprint); continue; }
                    List<LightRecord> inner = LightsOfBlueprint(row.Blueprint, depth + 1);
                    if (inner.Count == 0) continue;
                    var placement = new LinearTransformData { Right = row.Right, Up = row.Up, Forward = row.Forward, Trans = row.Trans };
                    LinearTransformData world = LightPort.Compose(placement, parent);
                    foreach (LightRecord local in inner)
                    {
                        LinearTransformData lt = LightPort.Compose(new LinearTransformData
                        {
                            Right = local.Right, Up = local.Up, Forward = local.Forward, Trans = local.Trans
                        }, world);
                        records.Add(new LightRecord
                        {
                            Type = local.Type, Source = local.Source,
                            Right = lt.Right, Up = lt.Up, Forward = lt.Forward, Trans = lt.Trans,
                            Fields = new Dictionary<string, string>(local.Fields, StringComparer.Ordinal),
                            // The blueprint's lights are read once and reused per placement, so every
                            // instance shares the same targets; the records must not share the list.
                            Refs = new List<LightAssetRef>(local.Refs)
                        });
                    }
                }
            }

            private List<LightRecord> LightsOfBlueprint(string path, int depth)
            {
                List<LightRecord> cached;
                if (blueprintLights.TryGetValue(path, out cached)) return cached;
                var result = new List<LightRecord>();
                if (depth > maxDepth) { Skipped.Add(path + ": --max-depth " + maxDepth + " reached"); return result; }
                if (inProgress.Contains(path)) return result;
                EbxAsset asset = LoadAsset(path);
                if (asset == null) { blueprintLights[path] = result; return result; }
                inProgress.Add(path);
                CollectDirect(asset.RootObject, path, LightPort.Identity(), result, false);
                CollectPlacements(asset.RootObject, path, LightPort.Identity(), depth, result);
                inProgress.Remove(path);
                blueprintLights[path] = result;
                return result;
            }

            private EbxAsset LoadAsset(string path)
            {
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
                if (entry == null) { Skipped.Add(path + ": not found"); return null; }
                try { return App.AssetManager.GetEbx(entry); }
                catch (Exception ex) { Skipped.Add(path + ": " + ex.Message); return null; }
            }
        }

        // True when the member draws its transforms from the group's physics resource rather than
        // inline InstanceTransforms. Read-only probe used to route 'level add --member'.
        internal static bool IsHavokBackedMember(string assetPath, int groupIndex, int memberIndex)
        {
            if (App.AssetManager == null) return false;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            if (entry == null) return false;
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); } catch { return false; }
            string error;
            object group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) return false;
            IList members;
            object member = FindMember(group, memberIndex, out members, out error);
            if (member == null) return false;
            IList inline = ReadList(member, "InstanceTransforms");
            return (inline == null || inline.Count == 0) && ConvertToInt(ReadMember(member, "InstanceCount")) > 0;
        }

        // Appends an instance to a Havok-backed member: the group's physics resource gains a record
        // in every level compound that holds the member (collision, raycast and, when present,
        // character collision), its AABB trees are rebuilt, and the EBX bookkeeping that indexes
        // physics parts -- the member's ranges, every later member's ranges, the part bounding
        // boxes and the physics component's part list -- is grown to match.  Both writes land in
        // the project; use --save / --save-as.
        internal static int AddHavokGroupedInstance(string assetPath, int groupIndex, int memberIndex, int fromInstance,
            string pos, string rot, string scale, ExportLogger logger)
        {
            const string label = "level add";
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine(label + ": AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            if (entry == null) { Console.Error.WriteLine(label + ": asset not found: " + assetPath); return 1; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": load failed: " + Describe(ex)); return 1; }

            string error;
            object group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) { Console.Error.WriteLine(label + ": " + error); return 1; }
            IList members;
            object member = FindMember(group, memberIndex, out members, out error);
            if (member == null) { Console.Error.WriteLine(label + ": " + error); return 1; }

            int count = ConvertToInt(ReadMember(member, "InstanceCount"));
            int ppc = ConvertToInt(ReadMember(member, "PhysicsPartCountPerInstance"));
            int nic = ConvertToInt(ReadMember(member, "NetworkIdCountPerInstance"));
            if (count <= 0) { Console.Error.WriteLine(label + ": member " + memberIndex + " holds no instances to copy"); return 1; }
            if (fromInstance < 0 || fromInstance >= count)
            { Console.Error.WriteLine(label + ": --from-instance " + fromInstance + " is out of range (member holds " + count + ")"); return 1; }
            if (ppc <= 0)
            {
                Console.Error.WriteLine(label + ": member " + memberIndex + " is Havok-backed but owns no physics parts per instance; " +
                    "its records in the physics resource cannot be identified, so it is not editable.");
                return 1;
            }
            long first, last;
            if (!ReadRange(member, "PhysicsPartRange", out first, out last) || first == NoRange || last == NoRange)
            { Console.Error.WriteLine(label + ": member " + memberIndex + " has no PhysicsPartRange; run 'level validate' on this asset first"); return 1; }
            long netFirst = NoRange, netLast = NoRange;
            if (nic > 0 && (!ReadRange(member, "NetworkIdRange", out netFirst, out netLast) || netFirst == NoRange))
            { Console.Error.WriteLine(label + ": member " + memberIndex + " has network ids per instance but no NetworkIdRange"); return 1; }

            // Instance index in the placement compound: Havok-backed members before this one, in order.
            int havokBase = 0;
            for (int j = 0; j < memberIndex; j++)
            {
                object other = ResolveInternal(members[j]) ?? members[j];
                if (other == null) continue;
                IList otherInline = ReadList(other, "InstanceTransforms");
                if (otherInline == null || otherInline.Count == 0) havokBase += Math.Max(0, ConvertToInt(ReadMember(other, "InstanceCount")));
            }
            int sibling = havokBase + fromInstance;
            int havokTotal = CountHavokPlacements(members);

            string resPath = FindPhysicsResourcePath(group);
            if (resPath == null) { Console.Error.WriteLine(label + ": group " + groupIndex + " names no physics resource"); return 1; }
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resPath);
            if (resEntry == null) { Console.Error.WriteLine(label + ": physics resource not found: " + resPath); return 1; }

            LinearTransformData xform;
            try { xform = TransformBuilder.Build(pos, rot, scale); }
            catch (ArgumentException ex) { Console.Error.WriteLine(label + ": " + ex.Message); return 1; }

            byte[] resBytes;
            if (!TryReadResBytes(resEntry, out resBytes, out error))
            { Console.Error.WriteLine(label + ": could not read " + resPath + ": " + error); return 1; }

            HavokPhysicsResource physics;
            try { physics = HavokPhysicsResource.Parse(resBytes); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + resPath + " could not be decoded: " + Describe(ex)); return 1; }
            HavokPhysicsResource.Compound placement = physics.Placement;
            if (placement == null) { Console.Error.WriteLine(label + ": " + resPath + " has no level compound"); return 1; }
            if (placement.InstanceCount != havokTotal)
            {
                Console.Error.WriteLine(label + ": the group's members need " + havokTotal + " Havok transform(s) but " + resPath +
                    " holds " + placement.InstanceCount + "; run 'level validate' on this asset first");
                return 1;
            }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "add instance {0} to {1} group {2} member {3} at {4}, growing {5} from {6} to {7} instance(s)",
                    count, assetPath, groupIndex, memberIndex, pos, resPath, placement.InstanceCount, placement.InstanceCount + 1);
                return 0;
            }

            string detail;
            int newIndex;
            try { newIndex = physics.AppendInstance(sibling, xform, out detail); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": physics append failed: " + Describe(ex)); return 1; }
            byte[] newBytes, newMeta;
            try { newBytes = physics.Write(); newMeta = physics.Meta; }
            catch (Exception ex) { Console.Error.WriteLine(label + ": physics write failed: " + Describe(ex)); return 1; }

            // EBX bookkeeping. Part indices are global and contiguous in member order, so the new
            // parts go at the end of this member's span and every later span shifts.
            int siblingPart = checked((int)first + fromInstance * ppc);
            int insertPart = checked((int)last + 1);
            try
            {
                SetRange(member, "PhysicsPartRange", first, last + ppc);
                if (nic > 0) SetRange(member, "NetworkIdRange", netFirst, netLast + nic);
                SetNumericMember(member, "InstanceCount", count + 1);
                foreach (string name in PerInstanceArrays)
                {
                    IList side = ReadList(member, name);
                    if (side == null || side.Count != count) continue;
                    side.Add(CloneListElement(side, fromInstance));
                }
                WriteBack(members, memberIndex, member);

                for (int j = memberIndex + 1; j < members.Count; j++)
                {
                    object later = ResolveInternal(members[j]) ?? members[j];
                    if (later == null) continue;
                    long f, l;
                    if (ReadRange(later, "PhysicsPartRange", out f, out l) && f != NoRange && l != NoRange)
                        SetRange(later, "PhysicsPartRange", f + ppc, l + ppc);
                    if (nic > 0 && ReadRange(later, "NetworkIdRange", out f, out l) && f != NoRange && l != NoRange)
                        SetRange(later, "NetworkIdRange", f + nic, l + nic);
                    WriteBack(members, j, later);
                }

                IList boxes = ReadList(group, "PartBoundingBoxes");
                if (boxes != null && boxes.Count >= insertPart)
                    for (int i = 0; i < ppc; i++) boxes.Insert(insertPart + i, CloneListElement(boxes, siblingPart + i));
                if (nic > 0) SetNumericMember(group, "NetworkIdCount", ConvertToInt(ReadMember(group, "NetworkIdCount")) + nic);

                IList components = ReadList(group, "Components");
                if (components != null)
                {
                    foreach (object raw in components)
                    {
                        object comp = ResolveInternal(raw) ?? raw;
                        if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                        IList parts = ReadList(comp, "Parts");
                        if (parts != null && parts.Count >= insertPart)
                            for (int i = 0; i < ppc; i++) parts.Insert(insertPart + i, CloneListElement(parts, siblingPart + i));
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(label + ": could not update the group's part tables: " + Describe(ex)); return 1; }

            if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            try { ResWriter.Modify(resEntry.Name, newBytes, newMeta); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": physics resource write failed: " + Describe(ex)); return 1; }

            logger.Log("level add: {0} group {1} member {2} now holds {3} instance(s); new instance index is {4} (Havok placement {5}); {6} -> {7} bytes ({8})",
                assetPath, groupIndex, memberIndex, count + 1, count, newIndex, resPath, newBytes.Length, detail);
            return 0;
        }

        // Shared preamble for Havok-backed move/remove: resolves the member, checks it really is
        // Havok-backed, maps the member-local instance to its index in the placement compound, and
        // loads + parses the group's physics resource.
        // Appends several instances of a Havok-backed member at once: the resource is parsed and
        // written once and the EBX tables grow by the whole batch, which is what makes importing a
        // few thousand placements tractable (the per-instance path re-reads a 7 MB resource each time).
        internal static int AddHavokGroupedInstances(string assetPath, int groupIndex, int memberIndex, int fromInstance,
            List<LinearTransformData> transforms, ExportLogger logger)
        {
            const string label = "level add";
            if (transforms == null || transforms.Count == 0) return 0;
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine(label + ": AssetManager not initialized."); return 1; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            if (entry == null) { Console.Error.WriteLine(label + ": asset not found: " + assetPath); return 1; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": load failed: " + Describe(ex)); return 1; }
            long tLoad = sw.ElapsedMilliseconds;
            string error;
            object group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) { Console.Error.WriteLine(label + ": " + error); return 1; }
            IList members;
            object member = FindMember(group, memberIndex, out members, out error);
            if (member == null) { Console.Error.WriteLine(label + ": " + error); return 1; }
            int count = ConvertToInt(ReadMember(member, "InstanceCount"));
            int ppc = ConvertToInt(ReadMember(member, "PhysicsPartCountPerInstance"));
            int nic = ConvertToInt(ReadMember(member, "NetworkIdCountPerInstance"));
            if (count <= 0) { Console.Error.WriteLine(label + ": member " + memberIndex + " holds no instances to copy"); return 1; }
            if (fromInstance < 0 || fromInstance >= count) { Console.Error.WriteLine(label + ": --from-instance " + fromInstance + " is out of range (member holds " + count + ")"); return 1; }
            if (ppc <= 0) { Console.Error.WriteLine(label + ": member " + memberIndex + " is Havok-backed but owns no physics parts per instance"); return 1; }
            long first, last;
            if (!ReadRange(member, "PhysicsPartRange", out first, out last) || first == NoRange || last == NoRange)
            { Console.Error.WriteLine(label + ": member " + memberIndex + " has no PhysicsPartRange; run 'level validate' on this asset first"); return 1; }
            long netFirst = NoRange, netLast = NoRange;
            if (nic > 0 && (!ReadRange(member, "NetworkIdRange", out netFirst, out netLast) || netFirst == NoRange))
            { Console.Error.WriteLine(label + ": member " + memberIndex + " has network ids per instance but no NetworkIdRange"); return 1; }
            int havokBase = 0;
            for (int j = 0; j < memberIndex; j++)
            {
                object other = ResolveInternal(members[j]) ?? members[j];
                if (other == null) continue;
                IList otherInline = ReadList(other, "InstanceTransforms");
                if (otherInline == null || otherInline.Count == 0) havokBase += Math.Max(0, ConvertToInt(ReadMember(other, "InstanceCount")));
            }
            int sibling = havokBase + fromInstance;
            int havokTotal = CountHavokPlacements(members);
            string resPath = FindPhysicsResourcePath(group);
            if (resPath == null) { Console.Error.WriteLine(label + ": group " + groupIndex + " names no physics resource"); return 1; }
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resPath);
            if (resEntry == null) { Console.Error.WriteLine(label + ": physics resource not found: " + resPath); return 1; }
            byte[] resBytes;
            if (!TryReadResBytes(resEntry, out resBytes, out error)) { Console.Error.WriteLine(label + ": could not read " + resPath + ": " + error); return 1; }
            HavokPhysicsResource physics;
            try { physics = HavokPhysicsResource.Parse(resBytes); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + resPath + " could not be decoded: " + Describe(ex)); return 1; }
            HavokPhysicsResource.Compound placement = physics.Placement;
            if (placement == null) { Console.Error.WriteLine(label + ": " + resPath + " has no level compound"); return 1; }
            if (placement.InstanceCount != havokTotal)
            {
                Console.Error.WriteLine(label + ": the group's members need " + havokTotal + " Havok transform(s) but " + resPath + " holds " + placement.InstanceCount + "; run 'level validate' on this asset first");
                return 1;
            }
            if (DryRun.Active)
            {
                DryRun.Report(logger, "add {0} instance(s) to {1} group {2} member {3}, growing {4} from {5} to {6} instance(s)",
                    transforms.Count, assetPath, groupIndex, memberIndex, resPath, placement.InstanceCount, placement.InstanceCount + transforms.Count);
                return 0;
            }
            int k = transforms.Count;
            string detail = null;
            long tParseDone = sw.ElapsedMilliseconds;
            try { physics.AppendInstances(sibling, transforms, out detail); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": physics append failed: " + Describe(ex)); return 1; }
            long tAppend = sw.ElapsedMilliseconds - tParseDone;
            byte[] newBytes, newMeta;
            long tW0 = sw.ElapsedMilliseconds;
            try { newBytes = physics.Write(); newMeta = physics.Meta; }
            catch (Exception ex) { Console.Error.WriteLine(label + ": physics write failed: " + Describe(ex)); return 1; }
            long tWrite = sw.ElapsedMilliseconds - tW0;
            int siblingPart = checked((int)first + fromInstance * ppc);
            int insertPart = checked((int)last + 1);
            try
            {
                SetRange(member, "PhysicsPartRange", first, last + (long)ppc * k);
                if (nic > 0) SetRange(member, "NetworkIdRange", netFirst, netLast + (long)nic * k);
                SetNumericMember(member, "InstanceCount", count + k);
                foreach (string name in PerInstanceArrays)
                {
                    IList side = ReadList(member, name);
                    if (side == null || side.Count != count) continue;
                    for (int i = 0; i < k; i++) side.Add(CloneListElement(side, fromInstance));
                }
                WriteBack(members, memberIndex, member);
                for (int j = memberIndex + 1; j < members.Count; j++)
                {
                    object later = ResolveInternal(members[j]) ?? members[j];
                    if (later == null) continue;
                    long f, l;
                    if (ReadRange(later, "PhysicsPartRange", out f, out l) && f != NoRange && l != NoRange) SetRange(later, "PhysicsPartRange", f + (long)ppc * k, l + (long)ppc * k);
                    if (nic > 0 && ReadRange(later, "NetworkIdRange", out f, out l) && f != NoRange && l != NoRange) SetRange(later, "NetworkIdRange", f + (long)nic * k, l + (long)nic * k);
                    WriteBack(members, j, later);
                }
                IList boxes = ReadList(group, "PartBoundingBoxes");
                if (boxes != null && boxes.Count >= insertPart)
                    for (int n = 0; n < k; n++)
                        for (int i = 0; i < ppc; i++) boxes.Insert(insertPart + n * ppc + i, CloneListElement(boxes, siblingPart + i));
                if (nic > 0) SetNumericMember(group, "NetworkIdCount", ConvertToInt(ReadMember(group, "NetworkIdCount")) + nic * k);
                IList components = ReadList(group, "Components");
                if (components != null)
                    foreach (object raw in components)
                    {
                        object comp = ResolveInternal(raw) ?? raw;
                        if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                        IList parts = ReadList(comp, "Parts");
                        if (parts != null && parts.Count >= insertPart)
                            for (int n = 0; n < k; n++)
                                for (int i = 0; i < ppc; i++) parts.Insert(insertPart + n * ppc + i, CloneListElement(parts, siblingPart + i));
                    }
            }
            catch (Exception ex) { Console.Error.WriteLine(label + ": could not update the group's part tables: " + Describe(ex)); return 1; }
            long tEbx0 = sw.ElapsedMilliseconds;
            if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            long tEbx = sw.ElapsedMilliseconds - tEbx0;
            long tRes0 = sw.ElapsedMilliseconds;
            try { ResWriter.Modify(resEntry.Name, newBytes, newMeta); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": physics resource write failed: " + Describe(ex)); return 1; }
            long tRes = sw.ElapsedMilliseconds - tRes0;
            logger.Log("level add: {0} group {1} member {2} now holds {3} instance(s) ({4} added); {5} -> {6} bytes [load {7}, read+parse {8}, append {9}, write {10}, ebx {11}, res {12} ms]",
                assetPath, groupIndex, memberIndex, count + k, k, resPath, newBytes.Length, tLoad, tParseDone - tLoad, tAppend, tWrite, tEbx, tRes);
            return 0;
        }

        private static bool OpenHavokPlacement(string label, string assetPath, int groupIndex, int memberIndex, int instanceIndex,
            out EbxAsset asset, out object group, out object member, out IList members, out int havokIndex,
            out ResAssetEntry resEntry, out HavokPhysicsResource physics)
        {
            asset = null; group = null; member = null; members = null; havokIndex = -1; resEntry = null; physics = null;
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": no project loaded. Run 'project load' first."); return false; }
            if (App.AssetManager == null) { Console.Error.WriteLine(label + ": AssetManager not initialized."); return false; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            if (entry == null) { Console.Error.WriteLine(label + ": asset not found: " + assetPath); return false; }
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": load failed: " + Describe(ex)); return false; }
            string error;
            group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) { Console.Error.WriteLine(label + ": " + error); return false; }
            member = FindMember(group, memberIndex, out members, out error);
            if (member == null) { Console.Error.WriteLine(label + ": " + error); return false; }
            int count = ConvertToInt(ReadMember(member, "InstanceCount"));
            if (instanceIndex < 0 || instanceIndex >= count)
            { Console.Error.WriteLine(label + ": instance index " + instanceIndex + " is out of range (member holds " + count + ")"); return false; }
            int havokBase = 0;
            for (int j = 0; j < memberIndex; j++)
            {
                object other = ResolveInternal(members[j]) ?? members[j];
                if (other == null) continue;
                IList otherInline = ReadList(other, "InstanceTransforms");
                if (otherInline == null || otherInline.Count == 0) havokBase += Math.Max(0, ConvertToInt(ReadMember(other, "InstanceCount")));
            }
            havokIndex = havokBase + instanceIndex;
            string resPath = FindPhysicsResourcePath(group);
            if (resPath == null) { Console.Error.WriteLine(label + ": group " + groupIndex + " names no physics resource"); return false; }
            resEntry = App.AssetManager.GetResEntry(resPath);
            if (resEntry == null) { Console.Error.WriteLine(label + ": physics resource not found: " + resPath); return false; }
            byte[] bytes;
            if (!TryReadResBytes(resEntry, out bytes, out error)) { Console.Error.WriteLine(label + ": could not read " + resPath + ": " + error); return false; }
            try { physics = HavokPhysicsResource.Parse(bytes); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": " + resPath + " could not be decoded: " + Describe(ex)); return false; }
            int havokTotal = CountHavokPlacements(members);
            if (physics.Placement == null || physics.Placement.InstanceCount != havokTotal)
            {
                Console.Error.WriteLine(label + ": the group's members need " + havokTotal + " Havok transform(s) but " + resPath + " holds " +
                    (physics.Placement == null ? 0 : physics.Placement.InstanceCount) + "; run 'level validate' on this asset first");
                return false;
            }
            return true;
        }

        // Moves a Havok-backed placement: the record's transform is rewritten in every collision slot
        // and the trees rebuilt. Nothing on the EBX side changes, since the transform lives in the res.
        private static int MoveHavokGroupedInstance(string assetPath, int groupIndex, int memberIndex, int instanceIndex,
            string pos, string rot, string scale, ExportLogger logger)
        {
            EbxAsset asset; object group, member; IList members; int havokIndex; ResAssetEntry resEntry; HavokPhysicsResource physics;
            if (!OpenHavokPlacement("level move", assetPath, groupIndex, memberIndex, instanceIndex,
                    out asset, out group, out member, out members, out havokIndex, out resEntry, out physics)) return 1;
            LinearTransformData xform;
            try { xform = TransformBuilder.Build(pos, rot, scale); }
            catch (ArgumentException ex) { Console.Error.WriteLine("level move: " + ex.Message); return 1; }
            if (rot == null)
            {
                // Keep the current rotation and scale, as the EBX-backed path does.
                List<List<HavokTransformReader.Transform>> arrays; string err;
                if (TryLoadPhysicsArrays(resEntry.Name, out arrays, out err))
                {
                    List<HavokTransformReader.Transform> placements = HavokTransformReader.SelectPlacements(arrays, physics.Placement.InstanceCount);
                    if (placements != null && havokIndex < placements.Count)
                    {
                        HavokTransformReader.Transform t = placements[havokIndex];
                        xform.Right = t.Right; xform.Up = t.Up; xform.Forward = t.Forward;
                    }
                }
            }
            if (DryRun.Active)
            {
                DryRun.Report(logger, "move Havok placement {0} (member {1} instance {2}) of {3} to {4}", havokIndex, memberIndex, instanceIndex, assetPath, pos);
                return 0;
            }
            string detail;
            try { physics.MoveInstance(havokIndex, xform, out detail); }
            catch (Exception ex) { Console.Error.WriteLine("level move: physics update failed: " + Describe(ex)); return 1; }
            byte[] bytes, meta;
            try { bytes = physics.Write(); meta = physics.Meta; }
            catch (Exception ex) { Console.Error.WriteLine("level move: physics write failed: " + Describe(ex)); return 1; }
            try { App.AssetManager.ModifyRes(resEntry.Name, bytes, meta); }
            catch (Exception ex) { Console.Error.WriteLine("level move: physics resource write failed: " + Describe(ex)); return 1; }
            logger.Log("level move: {0} group {1} member {2} instance {3} (Havok placement {4}) moved to {5} in {6} ({7})",
                assetPath, groupIndex, memberIndex, instanceIndex, havokIndex, pos, resEntry.Name, detail);
            return 0;
        }

        // Removes a Havok-backed placement: the record leaves every collision slot, later part tags
        // and every later member's ranges shift down, and the part tables shrink.
        private static int RemoveHavokGroupedInstance(string assetPath, int groupIndex, int memberIndex, int instanceIndex, ExportLogger logger)
        {
            EbxAsset asset; object group, member; IList members; int havokIndex; ResAssetEntry resEntry; HavokPhysicsResource physics;
            if (!OpenHavokPlacement("level remove", assetPath, groupIndex, memberIndex, instanceIndex,
                    out asset, out group, out member, out members, out havokIndex, out resEntry, out physics)) return 1;
            int count = ConvertToInt(ReadMember(member, "InstanceCount"));
            int ppc = ConvertToInt(ReadMember(member, "PhysicsPartCountPerInstance"));
            int nic = ConvertToInt(ReadMember(member, "NetworkIdCountPerInstance"));
            long first, last;
            if (ppc <= 0 || !ReadRange(member, "PhysicsPartRange", out first, out last) || first == NoRange)
            { Console.Error.WriteLine("level remove: member " + memberIndex + " has no physics part range; run 'level validate' first"); return 1; }
            if (count <= 1)
            { Console.Error.WriteLine("level remove: member " + memberIndex + " holds its last instance; removing whole members is not supported"); return 1; }
            if (DryRun.Active)
            {
                DryRun.Report(logger, "remove Havok placement {0} (member {1} instance {2}) from {3}", havokIndex, memberIndex, instanceIndex, assetPath);
                return 0;
            }
            string detail;
            int parts;
            try { parts = physics.RemoveInstance(havokIndex, out detail); }
            catch (Exception ex) { Console.Error.WriteLine("level remove: physics update failed: " + Describe(ex)); return 1; }
            if (parts != ppc)
            { Console.Error.WriteLine("level remove: the physics record owns " + parts + " part(s) but the member declares " + ppc + " per instance"); return 1; }
            byte[] bytes, meta;
            try { bytes = physics.Write(); meta = physics.Meta; }
            catch (Exception ex) { Console.Error.WriteLine("level remove: physics write failed: " + Describe(ex)); return 1; }

            int removedPart = checked((int)first + instanceIndex * ppc);
            try
            {
                SetNumericMember(member, "InstanceCount", count - 1);
                SetRange(member, "PhysicsPartRange", first, last - ppc);
                long nf, nl;
                if (nic > 0 && ReadRange(member, "NetworkIdRange", out nf, out nl) && nf != NoRange) SetRange(member, "NetworkIdRange", nf, nl - nic);
                foreach (string name in PerInstanceArrays)
                {
                    IList side = ReadList(member, name);
                    if (side != null && side.Count == count) side.RemoveAt(instanceIndex);
                }
                WriteBack(members, memberIndex, member);
                for (int j = memberIndex + 1; j < members.Count; j++)
                {
                    object later = ResolveInternal(members[j]) ?? members[j];
                    if (later == null) continue;
                    long f, l;
                    if (ReadRange(later, "PhysicsPartRange", out f, out l) && f != NoRange && l != NoRange) SetRange(later, "PhysicsPartRange", f - ppc, l - ppc);
                    if (nic > 0 && ReadRange(later, "NetworkIdRange", out f, out l) && f != NoRange && l != NoRange) SetRange(later, "NetworkIdRange", f - nic, l - nic);
                    WriteBack(members, j, later);
                }
                IList boxes = ReadList(group, "PartBoundingBoxes");
                if (boxes != null && boxes.Count >= removedPart + ppc) for (int i = 0; i < ppc; i++) boxes.RemoveAt(removedPart);
                if (nic > 0) SetNumericMember(group, "NetworkIdCount", ConvertToInt(ReadMember(group, "NetworkIdCount")) - nic);
                IList components = ReadList(group, "Components");
                if (components != null)
                    foreach (object raw in components)
                    {
                        object comp = ResolveInternal(raw) ?? raw;
                        if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                        IList partList = ReadList(comp, "Parts");
                        if (partList != null && partList.Count >= removedPart + ppc) for (int i = 0; i < ppc; i++) partList.RemoveAt(removedPart);
                    }
            }
            catch (Exception ex) { Console.Error.WriteLine("level remove: could not update the group's part tables: " + Describe(ex)); return 1; }

            if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            try { App.AssetManager.ModifyRes(resEntry.Name, bytes, meta); }
            catch (Exception ex) { Console.Error.WriteLine("level remove: physics resource write failed: " + Describe(ex)); return 1; }
            logger.Log("level remove: {0} group {1} member {2} instance {3} (Havok placement {4}) removed; member now holds {5}; {6} ({7})",
                assetPath, groupIndex, memberIndex, instanceIndex, havokIndex, count - 1, resEntry.Name, detail);
            return 0;
        }

        private static bool ReadRange(object member, string name, out long first, out long last)
        {
            first = NoRange; last = NoRange;
            object range = ReadMember(member, name);
            if (range == null) return false;
            object f = ReadMember(range, "First"), l = ReadMember(range, "Last");
            if (f == null || l == null) return false;
            first = Convert.ToInt64(f, CultureInfo.InvariantCulture);
            last = Convert.ToInt64(l, CultureInfo.InvariantCulture);
            return true;
        }

        private static void SetRange(object member, string name, long first, long last)
        {
            object range = ReadMember(member, name);
            if (range == null) throw new InvalidOperationException(name + " not found on " + member.GetType().Name);
            SetNumericMember(range, "First", first);
            SetNumericMember(range, "Last", last);
            if (range.GetType().IsValueType) SetPropOrField(member, member.GetType(), name, range);
        }

        internal static bool TryReadResBytes(ResAssetEntry entry, out byte[] data, out string error)
        {
            data = null; error = null;
            try
            {
                MethodInfo method = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
                object raw = method != null ? method.Invoke(App.AssetManager, new object[] { entry }) : null;
                data = raw as byte[];
                if (data == null && raw is Stream)
                {
                    using (Stream input = (Stream)raw)
                    using (var output = new MemoryStream()) { input.CopyTo(output); data = output.ToArray(); }
                }
                if (data == null) { error = "unexpected GetRes return type"; return false; }
                return true;
            }
            catch (Exception ex) { error = ex.InnerException != null ? ex.InnerException.Message : ex.Message; return false; }
        }

        private static bool InheritsFrom(Type type, string baseTypeName)
        {
            for (Type current = type; current != null; current = current.BaseType)
                if (string.Equals(current.Name, baseTypeName, StringComparison.Ordinal)) return true;
            return false;
        }

        private static IList GetObjectsList(object root)
        {
            if (root == null) return null;
            Type type = root.GetType();
            PropertyInfo property = type.GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                object value = property.GetValue(root);
                if (value is IList) return (IList)value;
            }
            FieldInfo field = type.GetField("Objects", BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                object value = field.GetValue(root);
                if (value is IList) return (IList)value;
            }
            return null;
        }

        internal static int NextInternalId(EbxAsset asset)
        {
            int max = -1;
            foreach (object obj in asset.Objects)
            {
                if (obj == null) continue;
                try
                {
                    MethodInfo getGuid = obj.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                    if (getGuid == null) continue;
                    object instanceGuid = getGuid.Invoke(obj, null);
                    if (instanceGuid == null) continue;
                    object value = ReadPropertyOrField(instanceGuid, "InternalId");
                    if (value == null) continue;
                    int id = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    if (id > max) max = id;
                }
                catch { }
            }
            if (max == int.MaxValue) throw new InvalidOperationException("the EBX partition has no free internal IDs");
            return max + 1;
        }

        private static object ReadPropertyOrField(object obj, string name)
        {
            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(obj);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(obj) : null;
        }

        private static bool WriteTransform(object entity, string requestedField, LinearTransformData transform)
        {
            string[] fields = string.Equals(requestedField, "Transform", StringComparison.Ordinal)
                ? new[] { "Transform", "BlueprintTransform" }
                : new[] { requestedField };
            string[] axes = { "right", "up", "forward", "trans" };
            string[] components = { "x", "y", "z" };
            float[][] values = { transform.Right, transform.Up, transform.Forward, transform.Trans };
            int written = 0;

            for (int axis = 0; axis < axes.Length; axis++)
            {
                for (int component = 0; component < components.Length; component++)
                {
                    if (TryWriteTransformComponent(entity, fields, axes[axis], components[component], values[axis][component]))
                        written++;
                }
            }
            return written == 12;
        }

        private static bool TryWriteTransformComponent(object entity, string[] fields, string axis, string component, float value)
        {
            string upperAxis = char.ToUpperInvariant(axis[0]) + axis.Substring(1);
            string upperComponent = component.ToUpperInvariant();
            string text = value.ToString("G", CultureInfo.InvariantCulture);
            foreach (string field in fields)
            {
                string[] paths =
                {
                    field + "." + axis + "." + component,
                    field + "." + upperAxis + "." + upperComponent,
                    field + "." + upperAxis + "." + component,
                    field + "." + axis + "." + upperComponent
                };
                foreach (string path in paths)
                {
                    try { PropertyPathWriter.SetValue(entity, path, text); return true; }
                    catch { }
                }
            }
            return false;
        }

        public static int ExecuteStats(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null;
            string format = "table";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }

            if (levelPath == null) { Console.Error.WriteLine("level stats: missing <level-ebx-path>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level stats: AssetManager not initialized."); return 1; }

            LevelNode root = AnalyzeLevel(levelPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 32, null);
            if (root.Error != null && root.Type == null) { Console.Error.WriteLine("level stats: " + root.Error); return 1; }
            LevelTotals totals = new LevelTotals();
            AccumulateLevelStats(root, totals);

            if (format == "json")
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(new
                {
                    level = levelPath,
                    rootType = root.Type,
                    assets = totals.Assets,
                    subWorlds = Math.Max(0, totals.Assets - 1),
                    objects = totals.Objects,
                    objectTypes = totals.ObjectTypes,
                    staticGroups = totals.StaticGroups,
                    members = totals.Members,
                    placements = totals.Placements,
                    transforms = new { ebx = totals.EbxPlacements, havok = totals.HavokPlacements, missing = totals.MissingPlacements },
                    errors = totals.Errors
                }, Newtonsoft.Json.Formatting.Indented));
            }
            else
            {
                Console.Out.WriteLine("Level: " + levelPath);
                Console.Out.WriteLine("Assets: " + totals.Assets + " (sub-worlds: " + Math.Max(0, totals.Assets - 1) + ")");
                Console.Out.WriteLine("Objects: " + totals.Objects + " across " + totals.ObjectTypes.Count + " types");
                Console.Out.WriteLine("Static groups: " + totals.StaticGroups + "; members: " + totals.Members);
                Console.Out.WriteLine("Placements: " + totals.Placements + " (EBX: " + totals.EbxPlacements + ", Havok: " + totals.HavokPlacements + ", missing: " + totals.MissingPlacements + ")");
                if (totals.Errors.Count > 0) Console.Out.WriteLine("Warnings: " + totals.Errors.Count);
            }
            return 0;
        }

        // Read-only structural check over a level and everything its sub-world references reach.
        // Reports broken sub-world references, unresolvable blueprints, missing or unreadable Havok
        // physics resources, transform-count mismatches and malformed placement arrays.  Exit code
        // 2 means findings were reported; 1 is reserved for usage and load failures.
        public static int ExecuteValidate(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null;
            string format = "table";
            int maxDepth = 32;
            int maxIssues = 100;
            bool strict = false;
            bool all = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--max-depth" && i + 1 < args.Length) maxDepth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--max-issues" && i + 1 < args.Length) maxIssues = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--strict") strict = true;
                else if (args[i] == "--all") all = true;
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }

            if (App.AssetManager == null) { Console.Error.WriteLine("level validate: AssetManager not initialized."); return 1; }
            if (maxDepth < 0) { Console.Error.WriteLine("level validate: --max-depth must be zero or greater"); return 1; }
            if (maxIssues < 0) { Console.Error.WriteLine("level validate: --max-issues must be zero or greater"); return 1; }
            if (all && levelPath != null) { Console.Error.WriteLine("level validate: --all takes no <level-ebx-path>"); return 1; }
            if (!all && levelPath == null) { Console.Error.WriteLine("level validate: missing <level-ebx-path> (or pass --all)"); return 1; }

            List<string> targets;
            if (all)
            {
                targets = EnumerateLevelPaths();
                if (targets.Count == 0)
                {
                    Console.Error.WriteLine("level validate: no assets of type 'LevelData' were found.");
                    Console.Error.WriteLine("  Closest types in this game: " + DescribeLevelishTypes());
                    return 1;
                }
            }
            else targets = new List<string> { levelPath };

            ResetValidationCaches();
            var reports = new List<LevelReport>();
            foreach (string target in targets)
            {
                LevelReport report = ValidateLevel(target, maxDepth);
                if (report == null)
                {
                    // A single named level that will not even load is a command failure, not a
                    // finding. Under --all it is a finding, so the sweep can keep going.
                    if (!all) { Console.Error.WriteLine("level validate: entry not found: " + target); return 1; }
                    continue;
                }
                reports.Add(report);
            }

            int errors = 0, warnings = 0;
            foreach (LevelReport r in reports) { errors += r.Errors; warnings += r.Warnings; }

            if (format == "json") PrintValidateJson(reports, all, errors, warnings);
            else PrintValidateTable(reports, all, maxIssues, errors, warnings);

            if (errors > 0) return 2;
            if (strict && warnings > 0) return 2;
            return 0;
        }

        private class LevelReport
        {
            public string Level;
            public LevelTotals Totals;
            public List<LevelIssue> Issues;
            public int Errors;
            public int Warnings;
        }

        private static List<string> EnumerateLevelPaths()
        {
            var paths = new List<string>();
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
                if (string.Equals(entry.Type, "LevelData", StringComparison.OrdinalIgnoreCase)) paths.Add(entry.Name);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        // Asset types whose name looks level-shaped, for when 'LevelData' finds nothing -- another
        // game may simply name its level root differently.
        private static string DescribeLevelishTypes()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (entry.Type == null) continue;
                if (entry.Type.IndexOf("Level", StringComparison.OrdinalIgnoreCase) < 0 &&
                    entry.Type.IndexOf("World", StringComparison.OrdinalIgnoreCase) < 0) continue;
                int n;
                counts.TryGetValue(entry.Type, out n);
                counts[entry.Type] = n + 1;
            }
            if (counts.Count == 0) return "none containing 'Level' or 'World'";
            var parts = new List<string>();
            foreach (KeyValuePair<string, int> item in counts) parts.Add(item.Key + " x" + item.Value);
            return string.Join(", ", parts.ToArray());
        }

        // Returns null only when the level root itself does not resolve at all.
        private static LevelReport ValidateLevel(string levelPath, int maxDepth)
        {
            var issues = new List<LevelIssue>();
            s_pendingMeshChecks = new List<PendingMeshCheck>();
            LevelNode root = AnalyzeLevel(levelPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, maxDepth, issues);
            if (root.Error != null && root.Type == null) { s_pendingMeshChecks = null; return null; }

            // Mesh registration can only be judged from a whole level. A sub-world validated on its
            // own is a partial view: its props are routinely registered in the parent's database
            // (Levels/MP/StarKiller_01/Art_Exterior is one), and nothing in the sub-world names its
            // parent, so every such placement would read as unregistered. Root levels only.
            if (string.Equals(root.Type, "LevelData", StringComparison.OrdinalIgnoreCase))
                ResolveMeshChecks(root, issues);
            else
                s_pendingMeshChecks = null;

            var totals = new LevelTotals();
            AccumulateLevelStats(root, totals);

            var report = new LevelReport { Level = levelPath, Totals = totals, Issues = issues };
            foreach (LevelIssue issue in issues)
            {
                if (issue.Severity == "error") report.Errors++;
                else report.Warnings++;
            }
            return report;
        }

        // `mod lint`'s view of a level: the same validation `level validate` runs, flattened to
        // "severity|code|detail" lines so lint does not depend on the report types. Returns an empty
        // list for a level that will not load, reporting it as one error instead.
        internal static List<string> LintLevel(string levelPath, int maxDepth, out int errors, out int warnings)
        {
            var lines = new List<string>();
            errors = 0; warnings = 0;

            ResetValidationCaches();
            LevelReport report = ValidateLevel(levelPath, maxDepth);
            if (report == null)
            {
                errors = 1;
                lines.Add("error|level-unreadable|the project modifies this level but it does not resolve");
                return lines;
            }

            foreach (LevelIssue issue in report.Issues)
                lines.Add(issue.Severity + "|" + issue.Code + "|" + issue.Detail);

            errors = report.Errors;
            warnings = report.Warnings;
            return lines;
        }

        private static object ValidateJsonBody(LevelReport r)
        {
            return new
            {
                level = r.Level,
                assets = r.Totals.Assets,
                staticGroups = r.Totals.StaticGroups,
                members = r.Totals.Members,
                placements = r.Totals.Placements,
                transforms = new { ebx = r.Totals.EbxPlacements, havok = r.Totals.HavokPlacements, unresolved = r.Totals.MissingPlacements },
                errors = r.Errors,
                warnings = r.Warnings,
                issues = r.Issues
            };
        }

        private static void PrintValidateJson(List<LevelReport> reports, bool all, int errors, int warnings)
        {
            if (!all)
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(ValidateJsonBody(reports[0]), Newtonsoft.Json.Formatting.Indented));
                return;
            }
            var bodies = new List<object>();
            int withFindings = 0;
            long placements = 0;
            foreach (LevelReport r in reports)
            {
                bodies.Add(ValidateJsonBody(r));
                if (r.Errors > 0 || r.Warnings > 0) withFindings++;
                placements += r.Totals.Placements;
            }
            Console.Out.WriteLine(JsonConvert.SerializeObject(new
            {
                levels = reports.Count,
                levelsWithFindings = withFindings,
                placements = placements,
                errors = errors,
                warnings = warnings,
                results = bodies
            }, Newtonsoft.Json.Formatting.Indented));
        }

        private static void PrintValidateTable(List<LevelReport> reports, bool all, int maxIssues, int errors, int warnings)
        {
            int shown = 0, suppressed = 0;
            if (!all)
            {
                LevelReport r = reports[0];
                Console.Out.WriteLine("Level: " + r.Level);
                Console.Out.WriteLine("Checked: " + r.Totals.Assets + " assets, " + r.Totals.StaticGroups + " static groups, " +
                    r.Totals.Members + " members, " + r.Totals.Placements + " placements");
                Console.Out.WriteLine("Transforms: EBX " + r.Totals.EbxPlacements + ", Havok " + r.Totals.HavokPlacements +
                    ", unresolved " + r.Totals.MissingPlacements);
                if (r.Issues.Count > 0) Console.Out.WriteLine();
                PrintIssues(r.Issues, maxIssues, ref shown, ref suppressed);
                if (suppressed > 0)
                    Console.Out.WriteLine("... " + suppressed + " more (raise --max-issues, or use --format json)");
                if (r.Issues.Count > 0) Console.Out.WriteLine();
                Console.Out.WriteLine(r.Issues.Count == 0
                    ? "Result: no issues found"
                    : "Result: " + errors + " error(s), " + warnings + " warning(s)");
                return;
            }

            long placements = 0;
            int withFindings = 0;
            foreach (LevelReport r in reports)
            {
                placements += r.Totals.Placements;
                bool any = r.Errors > 0 || r.Warnings > 0;
                if (any) withFindings++;
                Console.Out.WriteLine(string.Format("{0,-56}  {1,7} placements  {2}",
                    r.Level, r.Totals.Placements,
                    any ? r.Errors + " error(s), " + r.Warnings + " warning(s)" : "clean"));
                PrintIssues(r.Issues, maxIssues, ref shown, ref suppressed);
            }
            if (suppressed > 0)
                Console.Out.WriteLine("... " + suppressed + " more issue(s) (raise --max-issues, or use --format json)");
            Console.Out.WriteLine();
            Console.Out.WriteLine("Result: " + reports.Count + " level(s), " + placements + " placements, " +
                withFindings + " with findings: " + errors + " error(s), " + warnings + " warning(s)");
        }

        private static void PrintIssues(List<LevelIssue> issues, int maxIssues, ref int shown, ref int suppressed)
        {
            foreach (LevelIssue issue in issues)
            {
                if (shown >= maxIssues) { suppressed++; continue; }
                Console.Out.WriteLine(string.Format("    {0,-7}  {1,-26}  {2}{3}  {4}",
                    issue.Severity.ToUpperInvariant(), issue.Code, issue.Asset, FormatIssueLocation(issue), issue.Detail));
                shown++;
            }
        }

        private static string FormatIssueLocation(LevelIssue issue)
        {
            if (issue.Group == null) return "";
            return issue.Member == null
                ? " [group " + issue.Group + "]"
                : " [group " + issue.Group + " member " + issue.Member + "]";
        }

        public static int ExecuteList(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null;
            string filter    = null;
            string format    = "table";
            string near      = null;
            float  radius    = 10f;
            int    limit     = -1;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--near" && i + 1 < args.Length) near = args[++i];
                else if (args[i] == "--radius" && i + 1 < args.Length) radius = float.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }

            if (levelPath == null) { Console.Error.WriteLine("level list: missing <level-ebx-path>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level list: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            if (entry == null) { Console.Error.WriteLine("level list: entry not found: " + levelPath); return 1; }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level list: load failed: " + ex.Message); return 1; }

            float[] origin = null;
            if (near != null)
            {
                try { origin = ParseVec3(near); }
                catch (ArgumentException ex) { Console.Error.WriteLine("level list: " + ex.Message); return 1; }
                if (radius < 0) { Console.Error.WriteLine("level list: --radius must be zero or greater"); return 1; }
            }

            var rows = new List<EntityRow>();
            int effectiveLimit = limit > 0 ? limit : (format == "table" ? 100 : int.MaxValue);
            // --near ranks the whole level before narrowing, so the limit must not truncate the
            // candidates first -- the nearest placement is rarely among the first hundred found.
            int collectLimit = origin != null ? int.MaxValue : effectiveLimit;
            string bfiiError;
            bool usedBfiiLayout = TryCollectBfiiRows(asset.RootObject, rows, filter, collectLimit, out bfiiError);
            if (!usedBfiiLayout)
            {
                IList entities = GetEntityList(asset.RootObject);
                if (entities == null)
                {
                    // Neither layout matched. Say what the asset actually holds rather than just
                    // failing -- on an unfamiliar game this is the fastest route to what changed.
                    Console.Error.WriteLine("level list: no placements found on '" + levelPath + "'.");
                    Console.Error.WriteLine("  root type: " + DescribeRoot(asset.RootObject));
                    Console.Error.WriteLine("  Neither a StaticModelGroup layout nor an entity list was recognised.");
                    return 1;
                }
                foreach (object raw in entities)
                {
                    // Entries are usually PointerRefs wrapping the object. Handing the ref straight
                    // to ExtractRow finds no Blueprint on it and silently drops the placement, which
                    // is why layer-referenced props read as "0 entities total".
                    object ent = ResolveInternal(raw) ?? raw;
                    if (ent == null) continue;
                    EntityRow row = ExtractRow(ent);
                    if (row == null) continue;
                    if (filter != null && (row.Blueprint == null || !row.Blueprint.StartsWith(filter, StringComparison.OrdinalIgnoreCase))) continue;
                    rows.Add(row);
                    if (rows.Count >= collectLimit) break;
                }
            }
            else if (bfiiError != null) logger.LogWarning("level list: {0}", bfiiError);

            if (origin != null)
            {
                var near_rows = new List<EntityRow>();
                foreach (EntityRow row in rows)
                {
                    if (row.Trans == null) continue;
                    double dx = row.Trans[0] - origin[0];
                    double dy = row.Trans[1] - origin[1];
                    double dz = row.Trans[2] - origin[2];
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d > radius) continue;
                    row.Distance = d;
                    near_rows.Add(row);
                }
                near_rows.Sort(delegate(EntityRow a, EntityRow b) { return a.Distance.Value.CompareTo(b.Distance.Value); });
                if (near_rows.Count > effectiveLimit) near_rows.RemoveRange(effectiveLimit, near_rows.Count - effectiveLimit);
                rows = near_rows;
            }

            if (format == "json")
            {
                var arr = new List<object>();
                foreach (var r in rows)
                {
                    arr.Add(new
                    {
                        blueprint = r.Blueprint,
                        transform = new
                        {
                            right   = r.Right,
                            up      = r.Up,
                            forward = r.Forward,
                            trans   = r.Trans
                        },
                        group = r.GroupIndex,
                        member = r.MemberIndex,
                        instance = r.InstanceIndex,
                        source = r.TransformSource,
                        distance = r.Distance
                    });
                }
                Console.Out.WriteLine(JsonConvert.SerializeObject(arr, Newtonsoft.Json.Formatting.Indented));
            }
            else
            {
                Console.Out.WriteLine(origin != null
                    ? string.Format("{0,-60}  {1,-28}  {2,8}  {3}", "Blueprint", "Pos (x, y, z)", "Distance", "Address")
                    : string.Format("{0,-60}  {1}", "Blueprint", "Pos (x, y, z)"));
                foreach (var r in rows)
                {
                    string posStr = r.Trans != null
                        ? string.Format(CultureInfo.InvariantCulture, "{0:F1}, {1:F1}, {2:F1}", r.Trans[0], r.Trans[1], r.Trans[2])
                        : "n/a";
                    if (origin == null)
                    {
                        Console.Out.WriteLine(string.Format("{0,-60}  {1}", r.Blueprint ?? "(null)", posStr));
                        continue;
                    }
                    // With --near the address is the point of the listing: it is what level
                    // move/add/remove take.
                    string address = r.GroupIndex.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, "--group {0} --member {1} --instance {2}",
                            r.GroupIndex, r.MemberIndex, r.InstanceIndex)
                        : "";
                    Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,-60}  {1,-28}  {2,8:F2}  {3}", r.Blueprint ?? "(null)", posStr, r.Distance, address));
                }
                Console.Out.WriteLine(rows.Count + " entities total");
                // "0 entities total" is the same answer for an empty sub-world, an asset that is not
                // a level, and a level laid out in a way this build does not recognise. Say which.
                if (rows.Count == 0 && filter == null && origin == null)
                    Console.Error.WriteLine("level list: no placements resolved. Root is " +
                        DescribeRoot(asset.RootObject));
            }
            return 0;
        }

        public static int ExecuteAdd(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, blueprint = null, pos = null, rot = null, scale = null, mesh = null, bundleName = null;
            string entityClass = null, registryPath = null; uint? entityFlags = null; int flagsHigh = -1;
            int group = 0, member = -1, fromInstance = 0;
            bool asGroupMember = false, noBundle = false;
            s_noCollision = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--blueprint" && i + 1 < args.Length) blueprint = args[++i];
                else if (args[i] == "--entity-class" && i + 1 < args.Length) entityClass = args[++i];
                else if (args[i] == "--registry" && i + 1 < args.Length) registryPath = args[++i];
                else if (args[i] == "--flags" && i + 1 < args.Length) entityFlags = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                // --flags-high <byte>: shipped EntityData.Flags = behaviour byte << 24 | low 24 bits of the entity's own
                // instance guid (first group); the new guid is only known inside add-entity, so it is derived there
                else if (args[i] == "--flags-high" && i + 1 < args.Length) flagsHigh = ParseByte(args[++i]);
                else if (args[i] == "--mesh" && i + 1 < args.Length) mesh = args[++i];
                else if (args[i] == "--bundle" && i + 1 < args.Length) bundleName = args[++i];
                else if (args[i] == "--no-bundle") noBundle = true;
                else if (args[i] == "--no-collision") s_noCollision = true;
                else if (args[i] == "--member-stub" && i + 1 < args.Length) s_memberStub = args[++i];
                else if (args[i] == "--as-group-member") asGroupMember = true;
                else if (args[i] == "--pos"   && i + 1 < args.Length) pos      = args[++i];
                else if (args[i] == "--rot"   && i + 1 < args.Length) rot      = args[++i];
                else if (args[i] == "--scale" && i + 1 < args.Length) scale    = args[++i];
                else if (args[i] == "--group"  && i + 1 < args.Length) { group = int.Parse(args[++i], CultureInfo.InvariantCulture); asGroupMember = true; }
                else if (args[i] == "--member" && i + 1 < args.Length) member = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--from-instance" && i + 1 < args.Length) fromInstance = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null) { Console.Error.WriteLine("level add: missing <level-ebx-path>"); return 1; }

            // --member appends another instance of a blueprint the member already carries.
            if (member >= 0)
            {
                if (blueprint != null)
                {
                    Console.Error.WriteLine("level add: --blueprint places a blueprint the group does not hold yet; " +
                        "--member appends an instance of one it already does. Pass one or the other.");
                    return 1;
                }
                if (pos == null) { Console.Error.WriteLine("level add: --pos is required"); return 1; }
                if (IsHavokBackedMember(levelPath, group, member))
                    return AddHavokGroupedInstance(levelPath, group, member, fromInstance, pos, rot, scale, logger);
                return AddGroupedInstance(levelPath, group, member, fromInstance, pos, rot, scale, logger);
            }

            // --blueprint against a static model group creates a NEW member for a blueprint the group
            // has never held. Without --group it stays the flat entity-list path, which is a different
            // level layout entirely.
            if (blueprint == null) { Console.Error.WriteLine("level add: --blueprint is required"); return 1; }
            if (asGroupMember)
            {
                if (pos == null) { Console.Error.WriteLine("level add: --pos is required"); return 1; }
                return AddGroupMember(levelPath, group, blueprint, mesh, pos, rot, scale, bundleName, noBundle, logger);
            }

            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level add: no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level add: AssetManager not initialized."); return 1; }

            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(levelPath);
            if (levelEntry == null) { Console.Error.WriteLine("level add: level not found: " + levelPath); return 1; }
            EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(blueprint);
            if (bpEntry == null) { Console.Error.WriteLine("level add: blueprint not found: " + blueprint); return 1; }

            EbxAsset levelAsset, bpAsset;
            try { levelAsset = App.AssetManager.GetEbx(levelEntry); }
            catch (Exception ex) { Console.Error.WriteLine("level add: level load failed: " + ex.Message); return 1; }
            try { bpAsset = App.AssetManager.GetEbx(bpEntry); }
            catch (Exception ex) { Console.Error.WriteLine("level add: blueprint load failed: " + ex.Message); return 1; }

            Guid fileGuid = bpEntry.Guid;
            Guid classGuid;
            try
            {
                dynamic root = bpAsset.RootObject;
                classGuid = (Guid)root.GetInstanceGuid().ExportedGuid;
            }
            catch (Exception ex) { Console.Error.WriteLine("level add: failed to get class GUID: " + ex.Message); return 1; }

            LinearTransformData xform;
            try { xform = TransformBuilder.Build(pos, rot, scale); }
            catch (ArgumentException ex) { Console.Error.WriteLine("level add: " + ex.Message); return 1; }

            // A flat placement is a ReferenceObjectData in the level's Objects list: create it the way
            // add-entity does (fresh exported GUID, internal id, registered in the partition), then point
            // its Blueprint at the blueprint's root.
            if (DryRun.Active) { DryRun.Report(logger, "add entity for {0} to level {1} at {2}", blueprint, levelPath, pos); return 0; }
            // Shipped levels and community map mods place an ObjectBlueprint through an
            // ObjectReferenceObjectData (a SpatialPrefabBlueprint through SpatialPrefabReferenceObjectData),
            // with lightmap scale 1 and scale-with-size on; the base ReferenceObjectData is never used.
            string refType = string.IsNullOrEmpty(entityClass) ? DefaultEntityClass : entityClass;
            var entityArgs = new List<string> { levelPath, "--type", refType, "--pos", pos ?? "0,0,0", "--transform-field", "BlueprintTransform",
                "--set", "LightmapResolutionScale=1", "--set", "LightmapScaleWithSize=true", "--set", "LocalPlayerId=LocalPlayerId_Invalid",
                "--set", "RadiosityTypeOverride=RadiosityTypeOverride_Dynamic", "--set", "Flags=" + (flagsHigh >= 0 ? GuidFlagsToken + flagsHigh.ToString(CultureInfo.InvariantCulture) : (entityFlags ?? DefaultEntityFlags).ToString(CultureInfo.InvariantCulture)) };
            if (rot != null) { entityArgs.Add("--rot"); entityArgs.Add(rot); }
            if (scale != null) { entityArgs.Add("--scale"); entityArgs.Add(scale); }
            int erc = ExecuteAddEntity(entityArgs.ToArray(), logger, formatter);
            if (erc != 0) return erc;
            EbxAsset written;
            try { written = App.AssetManager.GetEbx(levelEntry); }
            catch (Exception ex) { Console.Error.WriteLine("level add: reload failed: " + Describe(ex)); return 1; }
            IList objects = GetEntityList(written.RootObject);
            object placed = objects != null && objects.Count > 0 ? ResolveInternal(objects[objects.Count - 1]) : null;
            if (placed == null || placed.GetType().Name != refType) { Console.Error.WriteLine("level add: the new entity could not be found after add-entity"); return 1; }
            try { EbxRefWriter.SetRefByPath(placed, "Blueprint", fileGuid, classGuid); written.AddDependency(fileGuid); }
            catch (Exception ex) { Console.Error.WriteLine("level add: failed to set blueprint ref: " + Describe(ex)); return 1; }
            if (!EbxWriteCommand.CallModifyEbx(levelPath, written, logger)) return 1;
            {
                string rerr;
                int registered = RegisterBlueprintsInNetworkRegistry(levelPath, registryPath, new[] { new KeyValuePair<EbxAssetEntry, Guid>(bpEntry, classGuid) }, logger, out rerr);
                if (registered < 0) logger.LogWarning("level add: {0}", rerr);
                else if (registered > 0) logger.Log("level add: {0} registered in {1}", blueprint, registryPath ?? levelPath + "_networkregistry_Win32");
            }
            logger.Log("level add: {0} placed as entity {1} of {2} ({3}) at {4}", blueprint, objects.Count - 1, levelPath, refType, pos);
            return 0;
        }

        // Diagnostic: serialises the (modified) level with the EBX writer, reads it back, and reports
        // which Objects[] entries came back null and whether the writer had their targets at all.
        public static int ExecuteRoundtrip(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null; bool noSort = false;
            foreach (string a in args) { if (a == "--no-sort") noSort = true; else if (!a.StartsWith("--")) levelPath = a; }
            if (levelPath == null) { Console.Error.WriteLine("level roundtrip: missing <level-ebx-path>"); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            if (entry == null) { Console.Error.WriteLine("level roundtrip: not found: " + levelPath); return 1; }
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            IList before = GetEntityList(asset.RootObject);
            var byGuid = new Dictionary<Guid, int>();
            for (int i = 0; i < before.Count; i++)
            {
                object o = ResolveInternal(before[i]);
                Guid g = ResolveExportedGuidOf(o);
                if (o != null && g != Guid.Empty && !byGuid.ContainsKey(g)) byGuid[g] = i;
            }
            EbxWriteFlags flags = noSort ? EbxWriteFlags.DoNotSort : EbxWriteFlags.None;
            byte[] buf; List<object> written = null;
            using (var writer = EbxBaseWriter.CreateWriter(new MemoryStream(), flags))
            {
                writer.WriteAsset(asset);
                buf = writer.ToByteArray();
                try { written = (List<object>)((dynamic)writer).Objects; } catch { }
            }
            EbxAsset back;
            using (var reader = EbxReader.CreateReader(new MemoryStream(buf))) back = reader.ReadAsset<EbxAsset>();
            IList after = GetEntityList(back.RootObject);
            var backGuids = new HashSet<Guid>();
            foreach (object o in back.Objects) { Guid g = ResolveExportedGuidOf(o); if (g != Guid.Empty) backGuids.Add(g); }
            int nulls = 0;
            for (int i = 0; i < after.Count; i++)
            {
                if (ResolveInternal(after[i]) != null || HasExternalTarget(after[i])) continue;
                nulls++;
                object orig = i < before.Count ? ResolveInternal(before[i]) : null;
                Guid g = ResolveExportedGuidOf(orig);
                bool inWriter = written != null && orig != null && written.Contains(orig);
                logger.Log("roundtrip: Objects[{0}] came back null; original {1} guid {2}; writer had it: {3}; guid present after read: {4}",
                    i, orig != null ? orig.GetType().Name : "null", g, inWriter, backGuids.Contains(g));
                if (orig == null) continue;
                MethodInfo eq = orig.GetType().GetMethod("Equals", new[] { typeof(object) });
                MethodInfo hc = orig.GetType().GetMethod("GetHashCode", Type.EmptyTypes);
                logger.Log("roundtrip:   Equals declared by {0}, GetHashCode by {1}", eq != null ? eq.DeclaringType.FullName : "?", hc != null ? hc.DeclaringType.FullName : "?");
                for (int j = 0; j < before.Count; j++)
                {
                    object other = ResolveInternal(before[j]);
                    if (other == null || ReferenceEquals(other, orig) || !other.Equals(orig)) continue;
                    logger.Log("roundtrip:   equals Objects[{0}] (guid {1}); transforms: {2} vs {3}", j, ResolveExportedGuidOf(other),
                        DescribeTransform(ReadMember(other, "BlueprintTransform")), DescribeTransform(ReadMember(orig, "BlueprintTransform")));
                    break;
                }
            }
            int nBefore = 0, nAfter = 0;
            foreach (object o in asset.Objects) nBefore++;
            foreach (object o in back.Objects) nAfter++;
            logger.Log("roundtrip: {0} bytes, {1} objects before, {2} after, {3} exported guids after, {4} null entr(y/ies), flags {5}",
                buf.Length, nBefore, nAfter, backGuids.Count, nulls, flags);
            return 0;
        }

        private static string DescribeTransform(object t)
        {
            if (t == null) return "null";
            var sb = new System.Text.StringBuilder();
            foreach (string axis in new[] { "right", "up", "forward", "trans" })
            {
                object v = ReadMember(t, axis);
                sb.Append(axis[0]).Append('(');
                foreach (string c in new[] { "x", "y", "z" }) { object f = v != null ? ReadMember(v, c) : null; sb.Append(f is float ? ((float)f).ToString("R", CultureInfo.InvariantCulture) : "?").Append(','); }
                sb.Append(") ");
            }
            return sb.ToString();
        }

        // Every blueprint a level places as an entity is listed in the level's network registry
        // (<level>_networkregistry_Win32.Objects, a reference to the blueprint's root object). Shipped
        // map mods add their props' blueprints there; an unregistered blueprint crashes the level on
        // load. Returns the number of references added, or -1 with an error.
        internal static int RegisterBlueprintsInNetworkRegistry(string levelPath, string registryPath, IEnumerable<KeyValuePair<EbxAssetEntry, Guid>> blueprints, ExportLogger logger, out string error)
        {
            error = null;
            // A LayerData (SL_Backdrop and friends) has no registry of its own; the owning sub-world's
            // is used, named explicitly.
            string regName = registryPath ?? (levelPath + "_networkregistry_Win32");
            EbxAssetEntry regEntry = App.AssetManager.GetEbxEntry(regName);
            if (regEntry == null) { error = "no network registry found at " + regName; return -1; }
            EbxAsset reg;
            try { reg = App.AssetManager.GetEbx(regEntry); }
            catch (Exception ex) { error = "could not load " + regName + ": " + Describe(ex); return -1; }
            IList objects = ReadList(reg.RootObject, "Objects");
            if (objects == null) { error = regName + " has no Objects list"; return -1; }
            var present = new HashSet<string>();
            foreach (object o in objects)
            {
                Guid fg, cg;
                if (TryReadExternalRef(o, out fg, out cg)) present.Add(fg + "/" + cg);
            }
            int added = 0;
            foreach (KeyValuePair<EbxAssetEntry, Guid> bp in blueprints)
            {
                if (bp.Key == null || bp.Value == Guid.Empty || !present.Add(bp.Key.Guid + "/" + bp.Value)) continue;
                try
                {
                    objects.Add(new PointerRef(new EbxImportReference { FileGuid = bp.Key.Guid, ClassGuid = bp.Value }));
                    reg.AddDependency(bp.Key.Guid);
                }
                catch (Exception ex) { error = "could not add " + bp.Key.Name + " to " + regName + ": " + Describe(ex); return -1; }
                added++;
            }
            if (added == 0) return 0;
            if (DryRun.Active) { DryRun.Report(logger, "register {0} blueprint(s) in {1}", added, regName); return added; }
            if (!EbxWriteCommand.CallModifyEbx(regEntry.Name, reg, logger)) { error = "write of " + regName + " failed"; return -1; }
            return added;
        }

        // Reflection deep copy of an EBX object graph: classes and structs member by member, lists
        // element by element; PointerRefs, GUIDs, strings and primitives by value. The copy gets no
        // instance GUID of its own until the caller assigns one.
        internal static object DeepCopyObject(object value)
        {
            if (value == null) return null;
            Type t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string || value is Guid || value is decimal) return value;
            if (t.Name == "PointerRef" || t.Name == "CString" || t.Name == "Sha1" || t.Name == "ResourceRef" || t.Name == "AssetClassGuid") return value;
            if (value is IList)
            {
                IList list = (IList)value;
                IList copy = (IList)Activator.CreateInstance(t);
                foreach (object item in list) copy.Add(DeepCopyObject(item));
                return copy;
            }
            object obj = Activator.CreateInstance(t);
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                pi.SetValue(obj, DeepCopyObject(pi.GetValue(value)));
            }
            foreach (FieldInfo fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                fi.SetValue(obj, DeepCopyObject(fi.GetValue(value)));
            return obj;
        }

        internal sealed class EntityPlacement
        {
            public string Blueprint;
            public LinearTransformData Transform;
        }

        private sealed class BlueprintEntityInfo
        {
            public EbxAssetEntry Entry;
            public Guid ClassGuid;
            public string RefType;
            public BundlePlan Plan;
        }

        // Places blueprints as level entities in one pass: the level is loaded once, each entity is
        // created the way `level add-entity` does (fresh exported GUID, next internal id, registered
        // in the partition) with the class and lightmap defaults shipped levels use, and the level is
        // written once. Appending members to a StaticModelGroup crashes the game on load whatever the
        // member carries; this is the route community map mods take, and it loads (verified in game).
        // Defaults copied from the placements shipped community map mods make (a Cloud City mod that
        // adds 130+ props): every blueprint, ObjectBlueprint included, goes in as a
        // SpatialPrefabReferenceObjectData with these Flags and dynamic radiosity.
        internal static bool s_bundleShaders = false;   // --bundle-shaders: carry shader graphs in the closure too
        // --skip-bundle-types: substrings of asset type names to leave out of the closure. Used for
        // asset kinds that already live in always-loaded shared bundles and break the level load when
        // a level bundle carries a partial copy of them.
        internal static List<string> s_bundleSkipTypes = new List<string>();
        internal const string DefaultEntityClass = "SpatialPrefabReferenceObjectData";
        internal const string EffectEntityClass = "EffectReferenceObjectData";

        /// <summary>The reference class a blueprint is placed with. An EffectBlueprint can only be instanced through an
        /// EffectReferenceObjectData (shipped levels: AutoStart true for ambient effects); as a SpatialPrefabReferenceObjectData
        /// it is inert (Carbonite rebuild 2026-09-27: 218 2015 steam effects placed, none played). Everything else keeps the
        /// requested class.</summary>
        internal static string ReferenceClassFor(string blueprintRootType, string requested)
        {
            if (blueprintRootType == "EffectBlueprint") return EffectEntityClass;
            return string.IsNullOrEmpty(requested) ? DefaultEntityClass : requested;
        }
        internal const uint DefaultEntityFlags = 19995309;
        internal const string GuidFlagsToken = "guid24|";
        private static int ParseByte(string v)
        {
            v = v.Trim();
            int b = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(v.Substring(2), NumberStyles.HexNumber) : int.Parse(v, CultureInfo.InvariantCulture);
            if (b < 0 || b > 255) throw new ArgumentException("--flags-high takes a byte (0-255 or 0x00-0xFF)");
            return b;
        }

        internal static int AddBlueprintEntities(string levelPath, IList<EntityPlacement> placements, string bundleName, bool noBundle,
            ExportLogger logger, out int added, out List<string> notes)
        {
            return AddBlueprintEntities(levelPath, placements, bundleName, noBundle, null, null, null, logger, out added, out notes);
        }

        internal static int AddBlueprintEntities(string levelPath, IList<EntityPlacement> placements, string bundleName, bool noBundle,
            string entityClass, uint? flags, string registryPath, ExportLogger logger, out int added, out List<string> notes)
        {
            added = 0; notes = new List<string>();
            if (string.IsNullOrEmpty(entityClass)) entityClass = DefaultEntityClass;
            uint entityFlags = flags ?? DefaultEntityFlags;
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level add: no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level add: AssetManager not initialized."); return 1; }
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(levelPath);
            if (levelEntry == null) { Console.Error.WriteLine("level add: level not found: " + levelPath); return 1; }

            // Resolve every distinct blueprint (root GUID, reference class, bundle plan) before the
            // level is touched: loading assets between ModifyEbx and the save has corrupted levels.
            var infos = new Dictionary<string, BlueprintEntityInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (EntityPlacement pl in placements)
            {
                if (pl == null || pl.Blueprint == null || infos.ContainsKey(pl.Blueprint)) continue;
                EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(pl.Blueprint);
                if (bpEntry == null) { Console.Error.WriteLine("level add: blueprint not found: " + pl.Blueprint); return 1; }
                EbxAsset bpAsset;
                try { bpAsset = App.AssetManager.GetEbx(bpEntry); }
                catch (Exception ex) { Console.Error.WriteLine("level add: blueprint load failed for " + pl.Blueprint + ": " + Describe(ex)); return 1; }
                var info = new BlueprintEntityInfo { Entry = bpEntry };
                info.ClassGuid = ResolveExportedGuidOf(bpAsset.RootObject);
                if (info.ClassGuid == Guid.Empty) { Console.Error.WriteLine("level add: the root of " + pl.Blueprint + " is not an exported instance"); return 1; }
                info.RefType = ReferenceClassFor(bpAsset.RootObject.GetType().Name, entityClass);
                if (!noBundle)
                {
                    string meshPath = FindBlueprintMesh(bpAsset.RootObject);
                    EbxAssetEntry meshEntry = meshPath != null ? App.AssetManager.GetEbxEntry(meshPath) : null;
                    string planError;
                    info.Plan = PlanMemberBundling(levelEntry, bpEntry, meshEntry, pl.Blueprint, bundleName, out planError);
                    if (info.Plan == null) { Console.Error.WriteLine("level add: bundling failed: " + planError + " (pass --bundle <name> or --no-bundle)"); return 1; }
                }
                infos[pl.Blueprint] = info;
            }

            if (DryRun.Active)
            {
                int n = 0;
                foreach (EntityPlacement pl in placements) if (pl != null && pl.Blueprint != null) n++;
                DryRun.Report(logger, "add {0} entit(y/ies) for {1} blueprint(s) to level {2}", n, infos.Count, levelPath);
                added = n;
                return 0;
            }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(levelEntry); }
            catch (Exception ex) { Console.Error.WriteLine("level add: level load failed: " + Describe(ex)); return 1; }
            IList objects = GetEntityList(asset.RootObject);
            if (objects == null) { Console.Error.WriteLine("level add: level has no Objects list: " + DescribeRoot(asset.RootObject)); return 1; }
            if (objects.IsFixedSize || objects.IsReadOnly) { Console.Error.WriteLine("level add: level Objects list is not writable"); return 1; }

            int nextId;
            try { nextId = NextInternalId(asset); }
            catch (Exception ex) { Console.Error.WriteLine("level add: " + Describe(ex)); return 1; }
            // Modders add a placement by duplicating one the level already has, so every default the
            // pipeline set on shipped objects (rendering overrides, persistence, stream realm...) is
            // carried along. Do the same: the last object of the requested class in the list is the
            // template; only when there is none is a bare instance created.
            // one template per reference class in use (effects go in as EffectReferenceObjectData, see ReferenceClassFor)
            var siblings = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (BlueprintEntityInfo bi in infos.Values)
            {
                if (siblings.ContainsKey(bi.RefType)) continue;
                object found = null;
                for (int i = objects.Count - 1; i >= 0 && found == null; i--)
                {
                    object o = ResolveInternal(objects[i]);
                    if (o != null && o.GetType().Name == bi.RefType) found = o;
                }
                siblings[bi.RefType] = found;
                if (found != null) notes.Add("new objects copy the defaults of an existing " + bi.RefType + (flags == null ? " (its Flags included)" : ""));
            }
            var created = new List<object>();
            int firstIndex = objects.Count;
            Action rollback = () =>
            {
                try { while (objects.Count > firstIndex) objects.RemoveAt(objects.Count - 1); } catch { }
                try
                {
                    IList partition = asset.Objects as IList;
                    if (partition != null) foreach (object e in created) partition.Remove(e);
                }
                catch { }
            };
            foreach (EntityPlacement pl in placements)
            {
                if (pl == null || pl.Blueprint == null) continue;
                BlueprintEntityInfo info = infos[pl.Blueprint];
                object entity;
                try
                {
                    object sibling = siblings[info.RefType];
                    entity = sibling != null ? DeepCopyObject(sibling) : EbxWriteCommand.CreateObjectByType(info.RefType);
                    if (info.RefType == EffectEntityClass) { try { PropertyPathWriter.SetValue(entity, "AutoStart", "true"); } catch { } }
                    if (entity == null) throw new InvalidOperationException("type not found in the loaded SDK: " + info.RefType);
                    MethodInfo setGuid = entity.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                    if (setGuid == null) throw new InvalidOperationException(info.RefType + " exposes no SetInstanceGuid method");
                    setGuid.Invoke(entity, new object[] { new AssetClassGuid(Guid.NewGuid(), nextId) });
                    if (!WriteTransform(entity, "BlueprintTransform", pl.Transform)) throw new InvalidOperationException(info.RefType + " has no writable BlueprintTransform");
                    // Always write the fields the proven single-prop route (`level add` -> add-entity) writes,
                    // even on a sibling copy: a copied vanilla object carried Flags 28920436 and
                    // HologramProjectorIndex 1, and every build placed that way with imported clones hung on
                    // the loading screen, while the same clones placed with Flags 19995309 loaded.
                    try { PropertyPathWriter.SetValue(entity, "LightmapResolutionScale", "1"); } catch { }
                    try { PropertyPathWriter.SetValue(entity, "LightmapScaleWithSize", "true"); } catch { }
                    // Mod-placed objects carry LocalPlayerId_Invalid (878 of 984 in the mod corpus), not player 0.
                    try { PropertyPathWriter.SetValue(entity, "LocalPlayerId", "LocalPlayerId_Invalid"); } catch { }
                    try { PropertyPathWriter.SetValue(entity, "RadiosityTypeOverride", "RadiosityTypeOverride_Dynamic"); } catch { }
                    try { PropertyPathWriter.SetValue(entity, "RenderingOverrides.HologramProjectorIndex", "0"); } catch { }
                    try { PropertyPathWriter.SetValue(entity, "Excluded", "false"); } catch { }
                    try { PropertyPathWriter.SetValue(entity, "Flags", entityFlags.ToString(CultureInfo.InvariantCulture)); } catch { }
                    try { PropertyPathWriter.SetValue(entity, "ObjectVariation", null); } catch { try { entity.GetType().GetProperty("ObjectVariation").SetValue(entity, new PointerRef()); } catch { } }
                    EbxRefWriter.SetRefByPath(entity, "Blueprint", info.Entry.Guid, info.ClassGuid);
                    objects.Add(new PointerRef(entity));
                    created.Add(entity);
                    asset.AddObject(entity, false);
                    InProcessObjects.Mark(entity);
                    asset.AddDependency(info.Entry.Guid);
                }
                catch (Exception ex)
                {
                    rollback();
                    Console.Error.WriteLine("level add: could not create the entity for " + pl.Blueprint + ": " + Describe(ex));
                    return 1;
                }
                nextId++;
                added++;
            }
            if (added == 0) return 0;
            if (!EbxWriteCommand.CallModifyEbx(levelPath, asset, logger)) { rollback(); added = 0; return 1; }
            {
                var regs = new List<KeyValuePair<EbxAssetEntry, Guid>>();
                foreach (BlueprintEntityInfo info in infos.Values) regs.Add(new KeyValuePair<EbxAssetEntry, Guid>(info.Entry, info.ClassGuid));
                string rerr;
                int registered = RegisterBlueprintsInNetworkRegistry(levelPath, registryPath, regs, logger, out rerr);
                // The entities are already written; a layer without a network registry (SL_Backdrop)
                // is a warning, not a failed placement.
                if (registered < 0) logger.LogWarning("level add: {0} (entities were written; nothing registered)", rerr);
                if (registered > 0) notes.Add(registered + " blueprint(s) registered in " + (registryPath ?? levelPath + "_networkregistry_Win32"));
            }
            // The asset's update pass drops objects it thinks nothing references and nulls their
            // entries; make sure every placed entity survived it.
            try
            {
                var partition = new HashSet<object>();
                foreach (object o in asset.Objects) partition.Add(o);
                var lost = new List<int>();
                for (int i = firstIndex; i < objects.Count; i++)
                {
                    object target = ResolveInternal(objects[i]);
                    if (target == null || !partition.Contains(target)) lost.Add(i);
                }
                if (lost.Count > 0)
                    logger.LogWarning("level add: {0} placed entit(y/ies) lost their reference in the write: Objects[{1}]{2}", lost.Count, lost[0], lost.Count > 1 ? " ..." : "");
            }
            catch { }
            foreach (BlueprintEntityInfo info in infos.Values)
                if (info.Plan != null) notes.Add(ApplyMemberBundling(info.Plan));
            return 0;
        }

        internal static Guid? GetEntityInstanceGuid(object entity)
        {
            try
            {
                MethodInfo m = entity.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                if (m == null) return null;
                object ig = m.Invoke(entity, null);
                if (ig == null) return null;
                PropertyInfo ep = ig.GetType().GetProperty("ExportedGuid") ?? ig.GetType().GetProperty("Guid");
                if (ep != null) { object g = ep.GetValue(ig); if (g is Guid) return (Guid)g; }
                if (ig is Guid) return (Guid)ig;
            }
            catch { }
            return null;
        }

        // level place <level-ebx> --group G --blueprint <bp> --pos x,y,z [...]
        //
        // Placing a prop takes three separate edits -- the placement itself, a MeshVariationDb
        // registration, and bundle membership -- and skipping either of the last two leaves a
        // placement the runtime silently refuses to draw. 'level add' can only warn about them.
        // This runs all three as one step, reusing each command's own implementation so the rules
        // stay in one place, and skipping the steps that are already satisfied so it can be re-run.
        // 'level add' warns that registration and bundling are still owed. Inside 'level place'
        // they are about to happen, so the warning would be telling the user to do what this
        // command is doing for them.
        private static bool s_inPlace;

        public static int ExecutePlace(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, blueprint = null, pos = null, rot = null, scale = null;
            string mesh = null, mvdbPath = null, bundle = null, like = null, varHash = null;
            string group = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--blueprint" && i + 1 < args.Length) blueprint = args[++i];
                else if (args[i] == "--group" && i + 1 < args.Length) group = args[++i];
                else if (args[i] == "--pos"   && i + 1 < args.Length) pos   = args[++i];
                else if (args[i] == "--rot"   && i + 1 < args.Length) rot   = args[++i];
                else if (args[i] == "--scale" && i + 1 < args.Length) scale = args[++i];
                else if (args[i] == "--mesh"  && i + 1 < args.Length) mesh  = args[++i];
                else if (args[i] == "--mvdb"  && i + 1 < args.Length) mvdbPath = args[++i];
                else if (args[i] == "--bundle" && i + 1 < args.Length) bundle = args[++i];
                else if (args[i] == "--like"  && i + 1 < args.Length) like  = args[++i];
                else if (args[i] == "--variation-hash" && i + 1 < args.Length) varHash = args[++i];
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null) { Console.Error.WriteLine("level place: missing <level-ebx-path>"); return 1; }
            if (blueprint == null) { Console.Error.WriteLine("level place: --blueprint is required"); return 1; }
            if (pos == null)       { Console.Error.WriteLine("level place: --pos is required"); return 1; }
            if (group == null)     { Console.Error.WriteLine("level place: --group is required"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level place: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level place: AssetManager not initialized."); return 1; }

            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(levelPath);
            if (levelEntry == null) { Console.Error.WriteLine("level place: level not found: " + levelPath); return 1; }
            EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(blueprint);
            if (bpEntry == null) { Console.Error.WriteLine("level place: blueprint not found: " + blueprint); return 1; }

            // Everything is resolved before anything is written: a failure discovered halfway
            // through would leave exactly the half-registered placement this command exists to
            // prevent.
            if (mesh == null)
            {
                try { mesh = FindBlueprintMesh(App.AssetManager.GetEbx(bpEntry).RootObject); }
                catch (Exception ex) { Console.Error.WriteLine("level place: blueprint load failed: " + ex.Message); return 1; }
            }
            if (string.IsNullOrEmpty(mesh))
            {
                Console.Error.WriteLine("level place: could not find a mesh on blueprint " + blueprint +
                    "; pass --mesh to name it explicitly");
                return 1;
            }
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(mesh);
            if (meshEntry == null) { Console.Error.WriteLine("level place: mesh not found: " + mesh); return 1; }

            if (mvdbPath == null) mvdbPath = MvdbPathFor(levelPath);
            if (App.AssetManager.GetEbxEntry(mvdbPath) == null)
            {
                Console.Error.WriteLine("level place: no mesh variation database at " + mvdbPath +
                    "; pass --mvdb to name the one this sub-world loads");
                return 1;
            }
            if (bundle == null) bundle = ResolveOwnBundleName(levelEntry, levelPath);
            if (bundle == null)
            {
                Console.Error.WriteLine("level place: could not tell which bundle " + levelPath +
                    " loads from; pass --bundle");
                return 1;
            }

            bool needsMvdb = !MvdbCommand.DatabaseHasMesh(mvdbPath, meshEntry.Guid);
            bool needsBundle = !MeshIsInBundle(meshEntry, bundle);

            var addArgs = new List<string> { levelPath, "--group", group, "--blueprint", blueprint, "--pos", pos };
            if (rot != null)   { addArgs.Add("--rot"); addArgs.Add(rot); }
            if (scale != null) { addArgs.Add("--scale"); addArgs.Add(scale); }
            addArgs.Add("--mesh"); addArgs.Add(mesh);
            int rc;
            s_inPlace = true;
            try { rc = ExecuteAdd(addArgs.ToArray(), logger, formatter); }
            finally { s_inPlace = false; }
            if (rc != 0) { Console.Error.WriteLine("level place: the placement step failed; nothing else was attempted"); return rc; }

            if (needsMvdb)
            {
                var mvdbArgs = new List<string> { mvdbPath, "--mesh", mesh };
                if (like != null)    { mvdbArgs.Add("--like"); mvdbArgs.Add(like); }
                if (varHash != null) { mvdbArgs.Add("--variation-hash"); mvdbArgs.Add(varHash); }
                rc = MvdbCommand.ExecuteAdd(mvdbArgs.ToArray(), logger, formatter);
                if (rc != 0)
                {
                    Console.Error.WriteLine("level place: the placement landed but the mesh variation step failed; " +
                        "the prop will not render until mvdb add succeeds");
                    return rc;
                }
            }
            else logger.Log("level place: {0} is already registered in {1}", mesh, mvdbPath);

            if (needsBundle)
            {
                rc = BundleCommand.ExecuteAdd(new[] { mesh, "--to", bundle, "--with-resources" }, logger, formatter);
                if (rc != 0)
                {
                    Console.Error.WriteLine("level place: the placement and mesh variation landed but bundling failed; " +
                        "the prop will not render until bundle add succeeds");
                    return rc;
                }
            }
            else logger.Log("level place: {0} is already in bundle {1}", mesh, bundle);

            formatter.WriteResult("OK", "level place", new Dictionary<string, string>
            {
                { "level", levelPath }, { "blueprint", blueprint }, { "mesh", mesh },
                { "mvdb", needsMvdb ? mvdbPath : "(already registered)" },
                { "bundle", needsBundle ? bundle : "(already bundled)" }
            });
            return 0;
        }

        // A level's own bundle is the one named after it; that is the bundle its sub-world content
        // is expected to load from, and the one bundle add should target by default.
        private static string ResolveOwnBundleName(EbxAssetEntry levelEntry, string levelPath)
        {
            string best = null;
            foreach (int id in BundleIdsOf(levelEntry))
            {
                BundleEntry b = App.AssetManager.GetBundleEntry(id);
                if (b == null || b.Name == null) continue;
                if (string.Equals(b.Name, levelPath, StringComparison.OrdinalIgnoreCase)) return b.Name;
                if (best == null) best = b.Name;
            }
            return best;
        }

        private static bool MeshIsInBundle(EbxAssetEntry meshEntry, string bundleName)
        {
            foreach (int id in BundleIdsOf(meshEntry))
            {
                BundleEntry b = App.AssetManager.GetBundleEntry(id);
                if (b != null && string.Equals(b.Name, bundleName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // level add-group <layer-or-subworld> --template <asset-with-group> [--group i] [--pos x,y,z]
        //                 [--keep-physics]
        // Appends an EMPTY StaticModelGroupEntityData to the target's Objects list, cloned from a
        // shipped group so every runtime-side field the engine expects is present, then reset: no
        // members, no part boxes, no network ids, identity (or --pos) transform, a fresh instance
        // GUID. The physics component is dropped unless --keep-physics, so the group carries no
        // collision resource and every member added to it afterwards is EBX-backed
        // (InstanceTransforms, PhysicsPartCountPerInstance 0) -- the shape Cloud City itself uses for
        // 1,117 of its own pieces. Thousands of instances then cost one entity instead of thousands.
        // Fill it with `level add --group N --blueprint ...` or `port import-layer --as-members --group N`.
        public static int ExecuteAddGroup(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, templatePath = null, pos = null; int templateGroup = 0; bool keepPhysics = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--template" && i + 1 < args.Length) templatePath = args[++i];
                else if (args[i] == "--group" && i + 1 < args.Length) templateGroup = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--pos" && i + 1 < args.Length) pos = args[++i];
                else if (args[i] == "--keep-physics") keepPhysics = true;
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null) { Console.Error.WriteLine("level add-group: missing <level-ebx-path>"); return 1; }
            if (templatePath == null) { Console.Error.WriteLine("level add-group: missing --template <asset that holds a StaticModelGroupEntityData>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level add-group: no project loaded."); return 1; }
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(levelPath);
            if (levelEntry == null) { Console.Error.WriteLine("level add-group: not found: " + levelPath); return 1; }
            EbxAssetEntry tmplEntry = App.AssetManager.GetEbxEntry(templatePath);
            if (tmplEntry == null) { Console.Error.WriteLine("level add-group: template not found: " + templatePath); return 1; }
            EbxAsset level, tmpl;
            try { level = App.AssetManager.GetEbx(levelEntry); tmpl = App.AssetManager.GetEbx(tmplEntry); }
            catch (Exception ex) { Console.Error.WriteLine("level add-group: load failed: " + Describe(ex)); return 1; }
            string gerr;
            object source = FindStaticGroup(tmpl.RootObject, templateGroup, out gerr);
            if (source == null) { Console.Error.WriteLine("level add-group: " + gerr); return 1; }
            IList objects = GetEntityList(level.RootObject);
            if (objects == null) { Console.Error.WriteLine("level add-group: " + levelPath + " has no Objects list"); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "add an empty static model group to {0} from {1}", levelPath, templatePath); return 0; }

            object group;
            int droppedComponents = 0;
            try
            {
                group = DeepCopyObject(source);
                IList members = ReadList(group, "MemberDatas"); if (members != null) members.Clear();
                IList boxes = ReadList(group, "PartBoundingBoxes"); if (boxes != null) boxes.Clear();
                foreach (string f in new[] { "NetworkIdCount", "ClientRuntimeTransformationCount", "ServerRuntimeTransformationCount" })
                    try { SetNumericMember(group, f, 0); } catch { }
                try { PropertyPathWriter.SetValue(group, "Enabled", "true"); } catch { }
                // The group is being emptied, so the physics component's description of the template's
                // members has to go with them: keeping the template's 4558 Parts against a group with
                // zero PartBoundingBoxes and zero physics members crashed the load in the health-state
                // lookup at game+0x549B22E with an id of 0xFFFFFFFF. Emptying is unconditional; the
                // flag only decides whether the component itself survives.
                IList comps = ReadList(group, "Components");
                if (comps != null)
                    foreach (object slot in comps)
                    {
                        object c = ResolveInternal(slot);
                        if (c == null || c.GetType().Name.IndexOf("Physics", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        foreach (string f in new[] { "Parts", "PhysicsBodies", "PhysicsConstraints" })
                        {
                            IList l = ReadList(c, f);
                            if (l != null) l.Clear();
                        }
                    }
                if (!keepPhysics)
                {
                    if (comps != null)
                        for (int i = comps.Count - 1; i >= 0; i--)
                        {
                            object c = ResolveInternal(comps[i]);
                            if (c != null && c.GetType().Name.IndexOf("Physics", StringComparison.OrdinalIgnoreCase) >= 0) { comps.RemoveAt(i); droppedComponents++; }
                        }
                    // ClientIndex/ServerIndex are slots in the group's runtime component arrays, so
                    // removing a component ahead of another leaves the survivor pointing past the end.
                    if (comps != null)
                        for (int i = 0; i < comps.Count; i++)
                        {
                            object c = ResolveInternal(comps[i]);
                            if (c == null) continue;
                            try { SetNumericMember(c, "ClientIndex", i); } catch { }
                            try { SetNumericMember(c, "ServerIndex", i); } catch { }
                        }
                    try { SetNumericMember(group, "ClientRuntimeComponentCount", Math.Max(0, ConvertToInt(ReadMember(group, "ClientRuntimeComponentCount")) - droppedComponents)); } catch { }
                    try { SetNumericMember(group, "ServerRuntimeComponentCount", Math.Max(0, ConvertToInt(ReadMember(group, "ServerRuntimeComponentCount")) - droppedComponents)); } catch { }
                }
                LinearTransformData xform = TransformBuilder.Build(pos ?? "0,0,0", null, null);
                WriteTransform(group, "Transform", xform);
                int nextId = NextInternalId(level);
                MethodInfo setGuid = group.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                if (setGuid != null) setGuid.Invoke(group, new object[] { new AssetClassGuid(Guid.NewGuid(), nextId) });
                objects.Add(new PointerRef(group));
                level.AddObject(group, false);
                InProcessObjects.Mark(group);
            }
            catch (Exception ex) { Console.Error.WriteLine("level add-group: could not build the group: " + Describe(ex)); return 1; }
            if (!EbxWriteCommand.CallModifyEbx(levelPath, level, logger)) return 1;
            int index = 0;
            foreach (object v in objects) { object o = ResolveInternal(v); if (o != null && o.GetType().Name == "StaticModelGroupEntityData") index++; }
            formatter.WriteResult("OK", "level add-group", new Dictionary<string, string>
            { { "level", levelPath }, { "template", templatePath }, { "group_index", (index - 1).ToString(CultureInfo.InvariantCulture) },
              { "physics", keepPhysics ? "kept" : "dropped (" + droppedComponents + " component(s))" } });
            return 0;
        }

        // level clear <level-or-layer> [--delete-groups] [--remove] [--keep-type <TypeName>]...
        //             [--only-type <TypeName>]... [--dry-run]
        // Empties a shipped layer / sub-world so a mod can replace the map: every object that has an
        // Excluded flag (the *ReferenceObjectData placements, sub-world references, lights, effects)
        // is marked Excluded, and with --delete-groups the StaticModelGroupEntityData objects (the
        // Havok-backed static geometry, thousands of instances each) are dropped from the Objects list.
        // --keep-type leaves objects of that type (e.g. LocalLightEntityData) untouched; --only-type
        // touches objects of the listed types only (a lighting variant's fixture group:
        // --delete-groups --only-type StaticModelGroupEntityData keeps its Enlighten entity). Objects the
        // project added itself (IsAdded assets are not distinguishable here) are treated like the rest,
        // so run this BEFORE placing new content into the same layer.
        public static int ExecuteClear(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null; bool deleteGroups = false, removeMode = false; var keepTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var onlyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--delete-groups") deleteGroups = true;
                else if (args[i] == "--remove") removeMode = true;
                else if (args[i] == "--keep-type" && i + 1 < args.Length) keepTypes.Add(args[++i]);
                else if (args[i] == "--only-type" && i + 1 < args.Length) onlyTypes.Add(args[++i]);
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null) { Console.Error.WriteLine("level clear: missing <level-ebx-path>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level clear: no project loaded. Run 'project load' first."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            if (entry == null) { Console.Error.WriteLine("level clear: not found: " + levelPath); return 1; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level clear: load failed: " + Describe(ex)); return 1; }
            IList objects = GetEntityList(asset.RootObject);
            if (objects == null) { Console.Error.WriteLine("level clear: " + levelPath + " has no Objects list"); return 1; }

            int excluded = 0, alreadyExcluded = 0, dropped = 0, kept = 0, external = 0;
            var byType = new Dictionary<string, int>();
            var toRemove = new List<int>();
            for (int i = 0; i < objects.Count; i++)
            {
                object o = ResolveInternal(objects[i]);
                if (o == null) { external++; continue; }   // external reference: another asset owns it
                string t = o.GetType().Name;
                if (keepTypes.Contains(t) || (onlyTypes.Count > 0 && !onlyTypes.Contains(t))) { kept++; continue; }
                if (t == "StaticModelGroupEntityData" && deleteGroups) { toRemove.Add(i); dropped++; byType[t] = byType.TryGetValue(t, out int c0) ? c0 + 1 : 1; continue; }
                // --remove takes objects out of the Objects list: shipped runtime data ignores Excluded,
                // so an excluded layer reference still loads its layer. Groups stay unless --delete-groups.
                if (removeMode)
                {
                    if (t == "StaticModelGroupEntityData") { kept++; continue; }
                    toRemove.Add(i); excluded++; byType[t] = byType.TryGetValue(t, out int cr) ? cr + 1 : 1; continue;
                }
                PropertyInfo ex = o.GetType().GetProperty("Excluded", BindingFlags.Public | BindingFlags.Instance);
                if (ex == null || ex.PropertyType != typeof(bool)) { kept++; continue; }
                if ((bool)ex.GetValue(o)) { alreadyExcluded++; continue; }
                if (!DryRun.Active) ex.SetValue(o, true);
                excluded++;
                byType[t] = byType.TryGetValue(t, out int c1) ? c1 + 1 : 1;
            }
            if (DryRun.Active)
            {
                DryRun.Report(logger, "clear {0}: exclude {1}, drop {2} static group(s), keep {3}", levelPath, excluded, dropped, kept);
                return 0;
            }
            for (int k = toRemove.Count - 1; k >= 0; k--) objects.RemoveAt(toRemove[k]);
            if (excluded == 0 && dropped == 0)
            {
                logger.Log("level clear: nothing to do in {0} ({1} kept, {2} already excluded, {3} external)", levelPath, kept, alreadyExcluded, external);
                return 0;
            }
            if (!EbxWriteCommand.CallModifyEbx(levelPath, asset, logger)) return 1;
            var fields = new Dictionary<string, string>
            {
                { "level", levelPath }, { "excluded", excluded.ToString(CultureInfo.InvariantCulture) },
                { "dropped_groups", dropped.ToString(CultureInfo.InvariantCulture) }, { "kept", kept.ToString(CultureInfo.InvariantCulture) },
                { "already_excluded", alreadyExcluded.ToString(CultureInfo.InvariantCulture) }, { "external", external.ToString(CultureInfo.InvariantCulture) },
            };
            var parts = new List<string>(); foreach (var kv in byType) parts.Add(kv.Key + "=" + kv.Value);
            fields["by_type"] = string.Join(",", parts);
            formatter.WriteResult("OK", "level clear", fields);
            return 0;
        }

        // level sink-group <asset> --group G [--members A:B] --offset dx,dy,dz
        //
        // Translates every placement of a range of members -- Havok-backed ones in the group's physics
        // resource (all root compounds, trees rebuilt once), EBX-backed ones in InstanceTransforms.
        // Counts, part ranges and health-state ids are unchanged, so it is the safe way to put a
        // shipped group's own props out of reach while keeping the group and its physics resource.
        public static int ExecuteSinkGroup(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, range = null, offsetArg = null; int groupIndex = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--group" && i + 1 < args.Length) groupIndex = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--members" && i + 1 < args.Length) range = args[++i];
                else if (args[i] == "--offset" && i + 1 < args.Length) offsetArg = args[++i];
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null || offsetArg == null) { Console.Error.WriteLine("level sink-group: usage <asset> --group G [--members A:B] --offset dx,dy,dz"); return 1; }
            float[] off;
            try { off = ParseVec3(offsetArg); }
            catch (ArgumentException ex) { Console.Error.WriteLine("level sink-group: --offset " + ex.Message); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level sink-group: no project loaded."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            if (entry == null) { Console.Error.WriteLine("level sink-group: not found: " + levelPath); return 1; }
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            string error;
            object group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) { Console.Error.WriteLine("level sink-group: " + error); return 1; }
            IList members = ReadList(group, "MemberDatas");
            if (members == null) { Console.Error.WriteLine("level sink-group: group has no MemberDatas"); return 1; }
            int from = 0, to = members.Count - 1;
            if (range != null)
            {
                string[] p = range.Split(':');
                if (p.Length != 2 || !int.TryParse(p[0], out from) || !int.TryParse(p[1], out to) || from < 0 || to >= members.Count || from > to)
                { Console.Error.WriteLine("level sink-group: --members wants A:B within 0.." + (members.Count - 1)); return 1; }
            }
            var havokIndices = new List<int>();
            int ebxShifted = 0, havokBase = 0;
            for (int j = 0; j < members.Count; j++)
            {
                object m = ResolveInternal(members[j]) ?? members[j];
                if (m == null) continue;
                IList inline = ReadList(m, "InstanceTransforms");
                int count = Math.Max(0, ConvertToInt(ReadMember(m, "InstanceCount")));
                bool havok = inline == null || inline.Count == 0;
                if (j >= from && j <= to)
                {
                    if (havok) { for (int k = 0; k < count; k++) havokIndices.Add(havokBase + k); }
                    else
                    {
                        foreach (object t in inline)
                        {
                            object tr = ReadMember(t, "trans");
                            if (tr == null) continue;
                            string[] axes = { "x", "y", "z" };
                            for (int a = 0; a < 3; a++)
                            {
                                float v = Convert.ToSingle(ReadMember(tr, axes[a]), CultureInfo.InvariantCulture) + off[a];
                                PropertyPathWriter.SetValue(tr, axes[a], v.ToString("R", CultureInfo.InvariantCulture));
                            }
                            ebxShifted++;
                        }
                        WriteBack(members, j, m);
                    }
                }
                if (havok) havokBase += count;
            }
            int havokMoved = 0;
            if (havokIndices.Count > 0)
            {
                string resPath = FindPhysicsResourcePath(group);
                if (resPath == null) { Console.Error.WriteLine("level sink-group: Havok-backed members but the group names no physics resource"); return 1; }
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resPath);
                byte[] bytes;
                if (resEntry == null || !TryReadResBytes(resEntry, out bytes, out error)) { Console.Error.WriteLine("level sink-group: could not read " + resPath); return 1; }
                HavokPhysicsResource physics = HavokPhysicsResource.Parse(bytes);
                int total = CountHavokPlacements(members);
                if (physics.Placement == null || physics.Placement.InstanceCount != total)
                { Console.Error.WriteLine("level sink-group: members need " + total + " Havok transforms but the resource holds " + (physics.Placement == null ? 0 : physics.Placement.InstanceCount)); return 1; }
                string detail;
                try { havokMoved = physics.ShiftInstances(havokIndices, off[0], off[1], off[2], out detail); }
                catch (Exception ex) { Console.Error.WriteLine("level sink-group: physics update failed: " + Describe(ex)); return 1; }
                byte[] outBytes = physics.Write();
                if (!DryRun.Active) App.AssetManager.ModifyRes(resEntry.Name, outBytes, physics.Meta);
                logger.Log("level sink-group: {0}", detail);
            }
            if (!DryRun.Active && !EbxWriteCommand.CallModifyEbx(levelPath, asset, logger)) return 1;
            formatter.WriteResult("OK", "level sink-group", new Dictionary<string, string>
            {
                { "level", levelPath }, { "members", from + ".." + to }, { "offset", offsetArg },
                { "havok_shifted", havokMoved.ToString(CultureInfo.InvariantCulture) },
                { "ebx_shifted", ebxShifted.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // level transform-group <asset> (--group G | --group-res <substring>) [--members A:B]
        //     --transform rx,ry,rz,ux,uy,uz,fx,fy,fz,tx,ty,tz [--if-centroid-near x,y,z,r]
        //
        // Composes a rigid parent transform onto every placement of a range of members: Havok-backed ones in
        // the group's physics resource (all root compounds, leaf boxes carried, trees rebuilt once), EBX-backed
        // ones in InstanceTransforms (world = parent * local). Counts, part ranges, health ids untouched.
        // Use: a 2015 static group that lived inside a prefab (PF_Arctic_01_Base_02_CombinedforE3) was imported
        // in the prefab's local space; this bakes the prefab's world transform into it, the way BFII stores
        // every shipped group (all indexed shipped groups have an identity group Transform).
        // --if-centroid-near makes it idempotent: it only applies while the selected placements' centroid is
        // within r of (x,y,z) (i.e. still in local space) and otherwise reports "skipped" and succeeds.
        public static int ExecuteTransformGroup(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, range = null, xfArg = null, resFilter = null, nearArg = null; int groupIndex = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--group" && i + 1 < args.Length) groupIndex = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--group-res" && i + 1 < args.Length) resFilter = args[++i];
                else if (args[i] == "--members" && i + 1 < args.Length) range = args[++i];
                else if (args[i] == "--transform" && i + 1 < args.Length) xfArg = args[++i];
                else if (args[i] == "--if-centroid-near" && i + 1 < args.Length) nearArg = args[++i];
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            const string usage = "level transform-group: usage <asset> (--group G | --group-res <substring>) [--members A:B] --transform rx,ry,rz,ux,uy,uz,fx,fy,fz,tx,ty,tz [--if-centroid-near x,y,z,r]";
            if (levelPath == null || xfArg == null || (groupIndex < 0 && resFilter == null)) { Console.Error.WriteLine(usage); return 1; }
            LinearTransformData parent;
            float[] near = null;
            try
            {
                parent = ParseTransform12(xfArg);
                if (nearArg != null)
                {
                    string[] p = nearArg.Split(',');
                    if (p.Length != 4) throw new ArgumentException("--if-centroid-near wants x,y,z,r");
                    near = new float[4];
                    for (int k = 0; k < 4; k++) near[k] = float.Parse(p[k], NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException) { Console.Error.WriteLine("level transform-group: " + ex.Message); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level transform-group: no project loaded."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            if (entry == null) { Console.Error.WriteLine("level transform-group: not found: " + levelPath); return 1; }
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            string error;
            object group = null;
            if (resFilter != null)
            {
                IList objects = ReadList(asset.RootObject, "Objects");
                int seen = 0;
                foreach (object value in objects ?? new ArrayList())
                {
                    object obj = ResolveInternal(value);
                    if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData") continue;
                    string rp = FindPhysicsResourcePath(obj);
                    if (rp != null && rp.IndexOf(resFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (group != null) { Console.Error.WriteLine("level transform-group: --group-res '" + resFilter + "' matches more than one group"); return 1; }
                        group = obj; groupIndex = seen;
                    }
                    seen++;
                }
                if (group == null) { Console.Error.WriteLine("level transform-group: no group's physics resource contains '" + resFilter + "'"); return 1; }
            }
            else
            {
                group = FindStaticGroup(asset.RootObject, groupIndex, out error);
                if (group == null) { Console.Error.WriteLine("level transform-group: " + error); return 1; }
            }
            IList members = ReadList(group, "MemberDatas");
            if (members == null) { Console.Error.WriteLine("level transform-group: group has no MemberDatas"); return 1; }
            int from = 0, to = members.Count - 1;
            if (range != null)
            {
                string[] p = range.Split(':');
                if (p.Length != 2 || !int.TryParse(p[0], out from) || !int.TryParse(p[1], out to) || from < 0 || to >= members.Count || from > to)
                { Console.Error.WriteLine("level transform-group: --members wants A:B within 0.." + (members.Count - 1)); return 1; }
            }

            // Selection + centroid of the current placements.
            var havokIndices = new List<int>();
            var ebxMembers = new List<int>();
            int havokBase = 0;
            for (int j = 0; j < members.Count; j++)
            {
                object m = ResolveInternal(members[j]) ?? members[j];
                if (m == null) continue;
                IList inline = ReadList(m, "InstanceTransforms");
                int count = Math.Max(0, ConvertToInt(ReadMember(m, "InstanceCount")));
                bool havok = inline == null || inline.Count == 0;
                if (j >= from && j <= to)
                {
                    if (havok) { for (int k = 0; k < count; k++) havokIndices.Add(havokBase + k); }
                    else ebxMembers.Add(j);
                }
                if (havok) havokBase += count;
            }
            string resPath = FindPhysicsResourcePath(group);
            List<HavokTransformReader.Transform> havokNow = null;
            if (havokIndices.Count > 0)
            {
                if (resPath == null) { Console.Error.WriteLine("level transform-group: Havok-backed members but the group names no physics resource"); return 1; }
                List<List<HavokTransformReader.Transform>> arrays;
                if (!TryLoadPhysicsArrays(resPath, out arrays, out error)) { Console.Error.WriteLine("level transform-group: could not read " + resPath + ": " + error); return 1; }
                havokNow = HavokTransformReader.SelectPlacements(arrays, CountHavokPlacements(members));
                if (havokNow == null) { Console.Error.WriteLine("level transform-group: " + resPath + " has no placement array for the members"); return 1; }
            }
            double[] sum = new double[3]; int n = 0;
            if (havokNow != null) foreach (int hi in havokIndices) { for (int a = 0; a < 3; a++) sum[a] += havokNow[hi].Translation[a]; n++; }
            foreach (int j in ebxMembers)
            {
                object m = ResolveInternal(members[j]) ?? members[j];
                foreach (object t in ReadList(m, "InstanceTransforms"))
                {
                    object tr = ReadMember(t, "trans");
                    sum[0] += Convert.ToDouble(ReadMember(tr, "x"), CultureInfo.InvariantCulture);
                    sum[1] += Convert.ToDouble(ReadMember(tr, "y"), CultureInfo.InvariantCulture);
                    sum[2] += Convert.ToDouble(ReadMember(tr, "z"), CultureInfo.InvariantCulture);
                    n++;
                }
            }
            if (n == 0) { Console.Error.WriteLine("level transform-group: the selected members hold no placements"); return 1; }
            double[] centroid = { sum[0] / n, sum[1] / n, sum[2] / n };
            string centroidText = string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###}", centroid[0], centroid[1], centroid[2]);
            if (near != null)
            {
                double d = Math.Sqrt(Math.Pow(centroid[0] - near[0], 2) + Math.Pow(centroid[1] - near[1], 2) + Math.Pow(centroid[2] - near[2], 2));
                if (d > near[3])
                {
                    logger.Log("level transform-group: skipped -- centroid {0} is {1:0.##} m from {2} (> {3}); already transformed?", centroidText, d, nearArg, near[3]);
                    formatter.WriteResult("OK", "level transform-group", new Dictionary<string, string>
                    {
                        { "level", levelPath }, { "group", groupIndex.ToString(CultureInfo.InvariantCulture) }, { "skipped", "centroid " + centroidText }
                    });
                    return 0;
                }
            }
            if (DryRun.Active)
            {
                DryRun.Report(logger, "transform group {0} of {1}: {2} Havok + {3} EBX member(s), centroid {4}", groupIndex, levelPath, havokIndices.Count, ebxMembers.Count, centroidText);
                return 0;
            }

            // EBX-backed members: world = parent * local, written as fresh LinearTransform values.
            int ebxComposed = 0;
            foreach (int j in ebxMembers)
            {
                object m = ResolveInternal(members[j]) ?? members[j];
                IList inline = ReadList(m, "InstanceTransforms");
                for (int k = 0; k < inline.Count; k++)
                {
                    object t = inline[k];
                    LinearTransformData local = ReadLinearTransform(t);
                    LinearTransformData world = FrostyCLI.Core.LightPort.Compose(local, parent);
                    inline[k] = BuildLinearTransform(t.GetType(), world);
                    ebxComposed++;
                }
                WriteBack(members, j, m);
            }

            int havokComposed = 0;
            if (havokIndices.Count > 0)
            {
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resPath);
                byte[] bytes;
                if (resEntry == null || !TryReadResBytes(resEntry, out bytes, out error)) { Console.Error.WriteLine("level transform-group: could not read " + resPath); return 1; }
                HavokPhysicsResource physics = HavokPhysicsResource.Parse(bytes);
                int total = CountHavokPlacements(members);
                if (physics.Placement == null || physics.Placement.InstanceCount != total)
                { Console.Error.WriteLine("level transform-group: members need " + total + " Havok transforms but the resource holds " + (physics.Placement == null ? 0 : physics.Placement.InstanceCount)); return 1; }
                string detail;
                try { havokComposed = physics.ComposeInstances(havokIndices, parent, out detail); }
                catch (Exception ex) { Console.Error.WriteLine("level transform-group: physics update failed: " + Describe(ex)); return 1; }
                byte[] outBytes = physics.Write();
                App.AssetManager.ModifyRes(resEntry.Name, outBytes, physics.Meta);
                logger.Log("level transform-group: {0}", detail);
            }
            if (!EbxWriteCommand.CallModifyEbx(levelPath, asset, logger)) return 1;
            formatter.WriteResult("OK", "level transform-group", new Dictionary<string, string>
            {
                { "level", levelPath }, { "group", groupIndex.ToString(CultureInfo.InvariantCulture) }, { "members", from + ".." + to },
                { "centroid_before", centroidText },
                { "havok_composed", havokComposed.ToString(CultureInfo.InvariantCulture) },
                { "ebx_composed", ebxComposed.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // level repoint-entities <asset> --from-blueprint <bp> --to-blueprint <bp> [--registry <networkregistry>]
        //
        // Every entity of the asset's Objects list whose Blueprint is --from-blueprint (ObjectReference / SpatialPrefab
        // reference ...) is pointed at --to-blueprint (file guid + exported root guid, partition dependency added); the new
        // blueprint is registered in the sub-world's network registry (entities must be, members must not). Transforms and
        // every other field stay. Used to swap a BFII same-GUID asset whose geometry differs from 2015 for the 2015 port.
        public static int ExecuteRepointEntities(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, fromBp = null, toBp = null, registry = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from-blueprint" && i + 1 < args.Length) fromBp = args[++i];
                else if (args[i] == "--to-blueprint" && i + 1 < args.Length) toBp = args[++i];
                else if (args[i] == "--registry" && i + 1 < args.Length) registry = args[++i];
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null || fromBp == null || toBp == null)
            { Console.Error.WriteLine("level repoint-entities: usage <asset> --from-blueprint <bp> --to-blueprint <bp> [--registry <networkregistry>]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level repoint-entities: no project loaded."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            EbxAssetEntry to = App.AssetManager.GetEbxEntry(toBp);
            if (entry == null || to == null) { Console.Error.WriteLine("level repoint-entities: not found: " + (entry == null ? levelPath : toBp)); return 1; }
            Guid toClass = ResolveExportedGuid(to);
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            int changed = 0;
            foreach (object value in ReadList(asset.RootObject, "Objects") ?? new ArrayList())
            {
                object o = ResolveInternal(value);
                if (o == null) continue;
                object bpRef = ReadMember(o, "Blueprint");
                if (bpRef == null) continue;
                string bp = ResolveBlueprintPath(bpRef);
                if (bp == null || !string.Equals(bp, fromBp, StringComparison.OrdinalIgnoreCase)) continue;
                FrostyCLI.Core.EbxRefWriter.SetRefByPath(o, "Blueprint", to.Guid, toClass);
                changed++;
            }
            if (changed == 0) { Console.Error.WriteLine("level repoint-entities: no entity of " + fromBp + " in " + levelPath); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "repoint {0} entities of {1} to {2} in {3}", changed, fromBp, toBp, levelPath); return 0; }
            asset.AddDependency(to.Guid);
            if (!EbxWriteCommand.CallModifyEbx(levelPath, asset, logger)) return 1;
            string rerr;
            int registered = RegisterBlueprintsInNetworkRegistry(levelPath, registry, new[] { new KeyValuePair<EbxAssetEntry, Guid>(to, toClass) }, logger, out rerr);
            if (registered < 0) { Console.Error.WriteLine("level repoint-entities: " + rerr); return 1; }
            formatter.WriteResult("OK", "level repoint-entities", new Dictionary<string, string>
            {
                { "level", levelPath }, { "from", fromBp }, { "to", to.Name }, { "entities", changed.ToString(CultureInfo.InvariantCulture) },
                { "registered", registered.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // level registry-add <networkregistry> --refs <file>
        //
        // Appends object references to a network registry's Objects list. The file has one reference per line,
        // "<file guid> <class guid>" (anything after is a comment). Used to carry the registrations another mod made in a
        // different level's registry (vehicle blueprints, their ability / weapon objects, the networked objects inside a
        // spawner prefab) over to a ported level: a spawned object that is not registered cannot be created by the
        // network layer. References already present, or whose file is not known, are skipped and counted.
        public static int ExecuteRegistryAdd(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string registry = null, refsFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--refs" && i + 1 < args.Length) refsFile = args[++i];
                else if (!args[i].StartsWith("--") && registry == null) registry = args[i];
            }
            if (registry == null || refsFile == null || !File.Exists(refsFile))
            { Console.Error.WriteLine("level registry-add: usage <networkregistry> --refs <file with '<file guid> <class guid>' lines>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level registry-add: no project loaded."); return 1; }
            var refs = new List<KeyValuePair<EbxAssetEntry, Guid>>();
            int unknown = 0, lines = 0;
            foreach (string raw in File.ReadAllLines(refsFile))
            {
                string[] p = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Guid fg, cg;
                if (p.Length < 2 || !Guid.TryParse(p[0], out fg) || !Guid.TryParse(p[1], out cg)) continue;
                lines++;
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(fg);
                if (e == null) { unknown++; logger.Log("level registry-add: unknown file " + fg + (p.Length > 2 ? " " + p[2] : "")); continue; }
                refs.Add(new KeyValuePair<EbxAssetEntry, Guid>(e, cg));
            }
            string rerr;
            int added = RegisterBlueprintsInNetworkRegistry(null, registry, refs, logger, out rerr);
            if (added < 0) { Console.Error.WriteLine("level registry-add: " + rerr); return 1; }
            formatter.WriteResult("OK", "level registry-add", new Dictionary<string, string>
            {
                { "registry", registry }, { "refs", lines.ToString(CultureInfo.InvariantCulture) },
                { "added", added.ToString(CultureInfo.InvariantCulture) },
                { "alreadyPresent", (refs.Count - added).ToString(CultureInfo.InvariantCulture) },
                { "unknownFile", unknown.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // level registry-copy <target networkregistry> --from <source networkregistry> --reachable-from <asset>...
        //
        // A network registry serves the sub-world it belongs to. A prefab placed in sub-world A whose child prefabs are
        // only registered in sub-world B's registry (PF_EnterVehicle_Interact: registered by the mode sub-world, used by a
        // vehicle spawner placed in the level root) gets no networked state there: its SyncedBool never reaches the
        // client and the interact prompt never shows. This copies, from the source registry into the target, every
        // object reference whose file is reachable (asset-index dependencies, transitively) from the given assets.
        public static int ExecuteRegistryCopy(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string target = null, from = null; var roots = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--reachable-from" && i + 1 < args.Length) roots.Add(args[++i]);
                else if (!args[i].StartsWith("--") && target == null) target = args[i];
            }
            if (target == null || from == null || roots.Count == 0)
            { Console.Error.WriteLine("level registry-copy: usage <target networkregistry> --from <source networkregistry> --reachable-from <asset>..."); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level registry-copy: no project loaded."); return 1; }
            EbxAssetEntry src = App.AssetManager.GetEbxEntry(from);
            if (src == null) { Console.Error.WriteLine("level registry-copy: not found: " + from); return 1; }
            var reach = new HashSet<Guid>(); var stack = new Stack<EbxAssetEntry>();
            foreach (string r in roots)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(r);
                if (e == null) { Console.Error.WriteLine("level registry-copy: not found: " + r); return 1; }
                if (reach.Add(e.Guid)) stack.Push(e);
            }
            while (stack.Count > 0)
                foreach (Guid d in stack.Pop().EnumerateDependencies())
                {
                    if (!reach.Add(d)) continue;
                    EbxAssetEntry de = App.AssetManager.GetEbxEntry(d);
                    if (de != null) stack.Push(de);
                }
            IList objects = ReadList(App.AssetManager.GetEbx(src).RootObject, "Objects");
            if (objects == null) { Console.Error.WriteLine("level registry-copy: " + from + " has no Objects list"); return 1; }
            var refs = new List<KeyValuePair<EbxAssetEntry, Guid>>(); var files = new HashSet<Guid>();
            foreach (object o in objects)
            {
                Guid fg, cg;
                if (!TryReadExternalRef(o, out fg, out cg) || !reach.Contains(fg)) continue;
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(fg);
                if (e == null) continue;
                refs.Add(new KeyValuePair<EbxAssetEntry, Guid>(e, cg));
                if (files.Add(fg)) logger.Log("level registry-copy: " + e.Name);
            }
            string rerr;
            int added = RegisterBlueprintsInNetworkRegistry(null, target, refs, logger, out rerr);
            if (added < 0) { Console.Error.WriteLine("level registry-copy: " + rerr); return 1; }
            formatter.WriteResult("OK", "level registry-copy", new Dictionary<string, string>
            {
                { "target", target }, { "from", from }, { "reachableFiles", reach.Count.ToString(CultureInfo.InvariantCulture) },
                { "sourceObjects", objects.Count.ToString(CultureInfo.InvariantCulture) }, { "matching", refs.Count.ToString(CultureInfo.InvariantCulture) },
                { "files", files.Count.ToString(CultureInfo.InvariantCulture) }, { "added", added.ToString(CultureInfo.InvariantCulture) }
            });
            return 0;
        }

        // "rx,ry,rz,ux,uy,uz,fx,fy,fz,tx,ty,tz" -> transform
        internal static LinearTransformData ParseTransform12(string text)
        {
            string[] p = (text ?? "").Split(',');
            if (p.Length != 12) throw new ArgumentException("--transform wants 12 numbers: right, up, forward, translation");
            var v = new float[12];
            for (int i = 0; i < 12; i++) v[i] = float.Parse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            return new LinearTransformData
            {
                Right = new[] { v[0], v[1], v[2] }, Up = new[] { v[3], v[4], v[5] },
                Forward = new[] { v[6], v[7], v[8] }, Trans = new[] { v[9], v[10], v[11] }
            };
        }

        private static LinearTransformData ReadLinearTransform(object t)
        {
            Func<string, float[]> vec = name =>
            {
                object v = ReadMember(t, name);
                return new[]
                {
                    Convert.ToSingle(ReadMember(v, "x"), CultureInfo.InvariantCulture),
                    Convert.ToSingle(ReadMember(v, "y"), CultureInfo.InvariantCulture),
                    Convert.ToSingle(ReadMember(v, "z"), CultureInfo.InvariantCulture)
                };
            };
            return new LinearTransformData { Right = vec("right"), Up = vec("up"), Forward = vec("forward"), Trans = vec("trans") };
        }

        private static object BuildLinearTransform(Type type, LinearTransformData x)
        {
            object lt = Activator.CreateInstance(type);
            string[] rows = { "right", "up", "forward", "trans" };
            float[][] vals = { x.Right, x.Up, x.Forward, x.Trans };
            for (int k = 0; k < 4; k++)
            {
                object vec = ReadMember(lt, rows[k]);
                object nv = Activator.CreateInstance(vec.GetType());
                SetPlainMember(nv, "x", vals[k][0]); SetPlainMember(nv, "y", vals[k][1]); SetPlainMember(nv, "z", vals[k][2]);
                SetPlainMember(lt, rows[k], nv);
            }
            return lt;
        }

        private static void SetPlainMember(object owner, string name, object value)
        {
            Type t = owner.GetType();
            PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi != null && pi.CanWrite) { pi.SetValue(owner, value); return; }
            FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fi != null) { fi.SetValue(owner, value); return; }
            throw new MissingMemberException(t.Name, name);
        }

        public static int ExecuteRemove(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, guidStr = null;
            int group = 0, member = -1, instance = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--guid" && i + 1 < args.Length) guidStr = args[++i];
                else if (args[i] == "--group"    && i + 1 < args.Length) group    = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--member"   && i + 1 < args.Length) member   = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--instance" && i + 1 < args.Length) instance = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null) { Console.Error.WriteLine("level remove: missing <level-ebx-path>"); return 1; }

            // --member/--instance delete one Battlefront II grouped static placement, as addressed by
            // 'level list'. --guid deletes an entity from a flat entity list.
            if (member >= 0)
            {
                if (guidStr != null) { Console.Error.WriteLine("level remove: use either --guid or --member/--instance, not both"); return 1; }
                if (instance < 0) { Console.Error.WriteLine("level remove: --member requires --instance"); return 1; }
                if (IsHavokBackedMember(levelPath, group, member))
                    return RemoveHavokGroupedInstance(levelPath, group, member, instance, logger);
                return RemoveGroupedInstance(levelPath, group, member, instance, logger);
            }
            if (instance >= 0)   { Console.Error.WriteLine("level remove: --instance requires --member"); return 1; }
            if (guidStr == null) { Console.Error.WriteLine("level remove: missing --guid"); return 1; }
            Guid target;
            if (!Guid.TryParse(guidStr, out target)) { Console.Error.WriteLine("level remove: invalid --guid"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level remove: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level remove: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            if (entry == null) { Console.Error.WriteLine("level remove: level not found: " + levelPath); return 1; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level remove: load failed: " + ex.Message); return 1; }

            IList entities = GetEntityList(asset.RootObject);
            if (entities == null) { Console.Error.WriteLine("level remove: could not find entity list"); return 1; }

            int found = -1;
            for (int i = 0; i < entities.Count; i++)
            {
                object candidate = ResolveInternal(entities[i]) ?? entities[i];
                if (candidate == null) continue;
                Guid? g = GetEntityInstanceGuid(candidate);
                if (g.HasValue && g.Value == target) { found = i; break; }
            }
            if (found < 0) { Console.Error.WriteLine("level remove: no entity with guid " + target); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "remove entity {0} from level {1}", target, levelPath); return 0; }
            entities.RemoveAt(found);

            return EbxWriteCommand.CallModifyEbx(levelPath, asset, logger) ? 0 : 1;
        }

        public static int ExecuteMove(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string levelPath = null, guidStr = null, pos = null, rot = null, scale = null;
            int group = 0, member = -1, instance = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--guid" && i + 1 < args.Length) guidStr = args[++i];
                else if (args[i] == "--pos"   && i + 1 < args.Length) pos   = args[++i];
                else if (args[i] == "--rot"   && i + 1 < args.Length) rot   = args[++i];
                else if (args[i] == "--scale" && i + 1 < args.Length) scale = args[++i];
                else if (args[i] == "--group"    && i + 1 < args.Length) group    = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--member"   && i + 1 < args.Length) member   = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--instance" && i + 1 < args.Length) instance = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (!args[i].StartsWith("--")) levelPath = args[i];
            }
            if (levelPath == null) { Console.Error.WriteLine("level move: missing <level-ebx-path>"); return 1; }

            // --member/--instance address a Battlefront II grouped static placement, as reported by
            // 'level list'. --guid addresses an entity in a flat entity list.
            if (member >= 0)
            {
                if (guidStr != null) { Console.Error.WriteLine("level move: use either --guid or --member/--instance, not both"); return 1; }
                if (instance < 0) { Console.Error.WriteLine("level move: --member requires --instance"); return 1; }
                if (pos == null)  { Console.Error.WriteLine("level move: --pos is required"); return 1; }
                if (IsHavokBackedMember(levelPath, group, member))
                    return MoveHavokGroupedInstance(levelPath, group, member, instance, pos, rot, scale, logger);
                return MoveGroupedInstance(levelPath, group, member, instance, pos, rot, scale, logger);
            }
            if (instance >= 0)     { Console.Error.WriteLine("level move: --instance requires --member"); return 1; }
            if (guidStr == null)   { Console.Error.WriteLine("level move: missing --guid"); return 1; }
            if (pos == null)       { Console.Error.WriteLine("level move: missing --pos"); return 1; }
            Guid target;
            if (!Guid.TryParse(guidStr, out target)) { Console.Error.WriteLine("level move: invalid --guid"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level move: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level move: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(levelPath);
            if (entry == null) { Console.Error.WriteLine("level move: level not found: " + levelPath); return 1; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level move: load failed: " + ex.Message); return 1; }

            IList entities = GetEntityList(asset.RootObject);
            if (entities == null) { Console.Error.WriteLine("level move: could not find entity list"); return 1; }

            object entity = null;
            bool entityWasWrapped = false;
            for (int i = 0; i < entities.Count; i++)
            {
                object candidate = ResolveInternal(entities[i]) ?? entities[i];
                if (candidate == null) continue;
                Guid? g = GetEntityInstanceGuid(candidate);
                if (g.HasValue && g.Value == target)
                {
                    entity = candidate;
                    entityWasWrapped = !ReferenceEquals(candidate, entities[i]);
                    break;
                }
            }
            // Not in the entity list: look through every exported instance of the partition. A
            // LayerData's spawn points (AlternateSpawnEntityData) live there, reached only by another
            // asset's LinkConnections.
            if (entity == null)
            {
                foreach (object obj in asset.Objects)
                {
                    if (obj == null) continue;
                    Guid? g = GetEntityInstanceGuid(obj);
                    if (g.HasValue && g.Value == target) { entity = obj; entityWasWrapped = true; break; }
                }
            }
            if (entity == null) { Console.Error.WriteLine("level move: no entity with guid " + target); return 1; }

            LinearTransformData xform;
            try { xform = TransformBuilder.Build(pos, rot, scale); }
            catch (ArgumentException ex) { Console.Error.WriteLine("level move: " + ex.Message); return 1; }

            // Spatial entities (spawn points, shapes) carry 'Transform'; references carry
            // 'BlueprintTransform'. Pick whichever this object actually has.
            string xformProp = entity.GetType().GetProperty("BlueprintTransform") != null ? "BlueprintTransform"
                             : entity.GetType().GetProperty("Transform") != null ? "Transform" : "BlueprintTransform";

            // Gate BEFORE mutating the (possibly cached) entity's transform.
            if (DryRun.Active) { DryRun.Report(logger, "move entity {0} in level {1} (pos {2})", target, levelPath, pos); return 0; }

            // Write transform components (same loop as ExecuteAdd).
            // If --rot omitted, preserve existing rotation (skip right/up/forward).
            string[] axisNames = { "right", "up", "forward", "trans" };
            float[][] vecs     = { xform.Right, xform.Up, xform.Forward, xform.Trans };
            string[] compNames = { "x", "y", "z" };
            for (int a = 0; a < 4; a++)
            {
                if (rot == null && a < 3) continue; // preserve existing rotation when --rot omitted
                for (int c = 0; c < 3; c++)
                {
                    string strVal = vecs[a][c].ToString("G", CultureInfo.InvariantCulture);
                    string path = xformProp + "." + axisNames[a] + "." + compNames[c];
                    try { PropertyPathWriter.SetValue(entity, path, strVal); }
                    catch (InvalidOperationException)
                    {
                        string altAxis = char.ToUpper(axisNames[a][0]) + axisNames[a].Substring(1);
                        string altPath = xformProp + "." + altAxis + "." + compNames[c].ToUpper();
                        try { PropertyPathWriter.SetValue(entity, altPath, strVal); } catch { }
                    }
                }
            }

            // A value-type entity edited through its box has to be stored back over the original.
            // Not when it came out of a PointerRef though: there the box is the ref's payload and
            // the list holds refs, so writing the entity into the list would corrupt it.
            if (entity.GetType().IsValueType && !entityWasWrapped)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    Guid? g = entities[i] == null ? (Guid?)null : GetEntityInstanceGuid(entities[i]);
                    if (g.HasValue && g.Value == target) { entities[i] = entity; break; }
                }
            }

            return EbxWriteCommand.CallModifyEbx(levelPath, asset, logger) ? 0 : 1;
        }

        public static int ExecuteScaffold(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string templatePrefix = null, destPrefix = null, typeList = null; bool keepInstanceGuids = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) destPrefix = args[++i];
                else if (args[i] == "--types" && i + 1 < args.Length) typeList = args[++i];
                else if (args[i] == "--keep-instance-guids") keepInstanceGuids = true;
                else if (!args[i].StartsWith("--")) templatePrefix = args[i];
            }
            // --types A,B,C: clone only assets of these EBX types (anything else under the prefix is left
            // shared, and references to it keep pointing at the original). --keep-instance-guids: keep every
            // cloned root's instance GUID. The default re-rolls it, but references are remapped by FILE guid
            // only, so an external PointerRef into a cloned root would keep the old instance guid and dangle.
            HashSet<string> onlyTypes = null;
            if (typeList != null)
            {
                onlyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string t in typeList.Split(',')) if (t.Trim().Length > 0) onlyTypes.Add(t.Trim());
            }
            if (templatePrefix == null) { Console.Error.WriteLine("level scaffold: missing <template-prefix>"); return 1; }
            if (destPrefix == null)     { Console.Error.WriteLine("level scaffold: missing --as"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level scaffold: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level scaffold: AssetManager not initialized."); return 1; }

            // Gather sources from the index catalog.
            // IndexService.AssetCatalog uses .Assets (List<AssetCatalogRecord>) and each record uses .Path
            var catalog = IndexService.GetCatalog();
            var sources = new List<SubWorldScaffolder.Source>();
            foreach (var rec in catalog.Assets)
            {
                if (!rec.Path.StartsWith(templatePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(rec.Path);
                if (e == null) continue;
                if (onlyTypes != null && !onlyTypes.Contains(e.Type ?? "")) continue;
                EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                sources.Add(new SubWorldScaffolder.Source { Path = rec.Path, FileGuid = e.Guid, Root = a.RootObject, Asset = a });
            }
            if (sources.Count == 0) { Console.Error.WriteLine("level scaffold: template prefix matched zero assets: " + templatePrefix); return 1; }

            // Reject if any dest already exists.
            foreach (var s in sources)
            {
                string dest = destPrefix + s.Path.Substring(templatePrefix.Length);
                if (App.AssetManager.GetEbxEntry(dest) != null)
                { Console.Error.WriteLine("level scaffold: dest already exists: " + dest); return 1; }
            }

            List<SubWorldScaffolder.Plan_Result> plan;
            try { plan = SubWorldScaffolder.Plan(sources, templatePrefix, destPrefix); }
            catch (Exception ex) { Console.Error.WriteLine("level scaffold: " + ex.Message); return 1; }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "scaffold {0} SubWorld asset(s) under {1}:", plan.Count, destPrefix);
                foreach (var r in plan) logger.Log("           + " + r.DestPath);
                return 0;
            }

            // Build a real EbxAsset per plan result and add to project.
            foreach (var r in plan)
            {
                EbxAsset asset;
                try
                {
                    Type at = typeof(EbxAsset);
                    ConstructorInfo ctor = at.GetConstructor(new[] { typeof(object[]) });
                    asset = (EbxAsset)ctor.Invoke(new object[] { new object[] { r.NewRoot } });
                    PropertyInfo fgp = at.GetProperty("FileGuid", BindingFlags.Public | BindingFlags.Instance);
                    if (fgp != null && fgp.CanWrite) fgp.SetValue(asset, r.NewFileGuid);
                }
                catch (Exception ex) { Console.Error.WriteLine("level scaffold: build asset failed for " + r.DestPath + ": " + ex.Message); return 1; }

                // Reassign instance guids on the cloned root (best-effort).
                if (!keepInstanceGuids) try
                {
                    MethodInfo setIg = r.NewRoot.GetType().GetMethod("SetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                    MethodInfo getIg = r.NewRoot.GetType().GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
                    if (setIg != null && getIg != null)
                    {
                        object oldIg = getIg.Invoke(r.NewRoot, null);
                        ConstructorInfo ci = oldIg.GetType().GetConstructor(new[] { typeof(Guid) });
                        if (ci != null) setIg.Invoke(r.NewRoot, new[] { ci.Invoke(new object[] { Guid.NewGuid() }) });
                    }
                }
                catch { }

                // AddEbx for the new asset.
                Type amt = App.AssetManager.GetType();
                MethodInfo add = null;
                foreach (MethodInfo c in amt.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (c.Name == "AddEbx" && c.GetParameters().Length >= 2 && c.GetParameters()[0].ParameterType == typeof(string)) { add = c; break; }
                if (add == null) { Console.Error.WriteLine("level scaffold: AddEbx not found"); return 1; }
                try
                {
                    ParameterInfo[] ps = add.GetParameters();
                    object[] call = new object[ps.Length];
                    call[0] = r.DestPath; call[1] = asset;
                    if (ps.Length > 2) call[2] = Array.CreateInstance(typeof(int), 0);
                    add.Invoke(App.AssetManager, call);
                }
                catch (Exception ex)
                {
                    string msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    Console.Error.WriteLine("level scaffold: AddEbx failed for " + r.DestPath + ": " + msg); return 1;
                }
            }
            Console.Out.WriteLine("scaffolded " + plan.Count + " assets from " + templatePrefix + " to " + destPrefix);
            return 0;
        }

        private static void SetPropOrField(object obj, Type t, string name, object value)
        {
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(obj, value); return; }
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { f.SetValue(obj, value); return; }
            throw new InvalidOperationException("level add: member '" + name + "' not found on type '" + t.Name + "'");
        }

        // Loads the asset, resolves group/member and checks writability -- the shared preamble of
        // the grouped move and add paths.
        private static bool OpenWritableMember(string label, string assetPath, int groupIndex, int memberIndex,
            out EbxAsset asset, out object group, out object member, out IList members, out IList inline)
        {
            asset = null; group = null; member = null; members = null; inline = null;

            if (!Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": no project loaded. Run 'project load' first."); return false; }
            if (App.AssetManager == null) { Console.Error.WriteLine(label + ": AssetManager not initialized."); return false; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            if (entry == null) { Console.Error.WriteLine(label + ": asset not found: " + assetPath); return false; }
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine(label + ": load failed: " + Describe(ex)); return false; }

            string error;
            group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) { Console.Error.WriteLine(label + ": " + error); return false; }

            member = FindMember(group, memberIndex, out members, out error);
            if (member == null) { Console.Error.WriteLine(label + ": " + error); return false; }

            string reason;
            if (!IsMemberWritable(member, group, memberIndex, out inline, out reason))
            {
                Console.Error.WriteLine(label + ": " + reason);
                return false;
            }
            return true;
        }

        // level move <asset> --group G --member N --instance I --pos x,y,z [--rot ...] [--scale ...]
        private static int MoveGroupedInstance(string assetPath, int groupIndex, int memberIndex, int instanceIndex,
            string pos, string rot, string scale, ExportLogger logger)
        {
            EbxAsset asset; object group, member; IList members, inline;
            if (!OpenWritableMember("level move", assetPath, groupIndex, memberIndex,
                    out asset, out group, out member, out members, out inline)) return 1;

            if (instanceIndex < 0 || instanceIndex >= inline.Count)
            {
                Console.Error.WriteLine("level move: instance index " + instanceIndex +
                    " is out of range (member holds " + inline.Count + ")");
                return 1;
            }

            LinearTransformData xform;
            try { xform = TransformBuilder.Build(pos, rot, scale); }
            catch (ArgumentException ex) { Console.Error.WriteLine("level move: " + ex.Message); return 1; }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "move {0} group {1} member {2} instance {3} to {4}",
                    assetPath, groupIndex, memberIndex, instanceIndex, pos);
                return 0;
            }

            // Omitting --rot preserves the existing rotation, matching the entity-GUID move path.
            object element = inline[instanceIndex];
            try { element = ApplyTransform(element, xform, rot != null || scale != null); }
            catch (Exception ex) { Console.Error.WriteLine("level move: could not write transform: " + Describe(ex)); return 1; }
            WriteBack(inline, instanceIndex, element);

            return EbxWriteCommand.CallModifyEbx(assetPath, asset, logger) ? 0 : 1;
        }

        // level add <asset> --group G --blueprint <bp> --pos x,y,z [--mesh <mesh>] [--rot] [--scale]
        //
        // Creates a new StaticModelGroupMemberData for a blueprint the group does not hold yet, which
        // is what placing something new in a level needs -- AddGroupedInstance only repeats a
        // blueprint already present.
        //
        // The member is created EBX-backed: its single transform lives in InstanceTransforms and it
        // declares no physics parts and no network ids, exactly like the EBX-backed members shipped
        // levels already carry. That is what keeps this self-contained. Appending a member that owns
        // no part or id range shifts nothing: every existing member keeps its ranges, the group's
        // PartBoundingBoxes and NetworkIdCount stay correct, and no physics resource is touched.
        // The prop therefore renders but has no collision, which is the same deal every EBX-backed
        // member in the game gets.
        // Appends a NEW member (a blueprint the group has never held) with one instance.
        //
        // What the runtime needs, taken from the engine's StaticModelGroupBuilder: MemberType must
        // reference the ENTITY inside the blueprint (ObjectBlueprint.Object, a GamePhysicsEntityData),
        // not the blueprint's root -- the client casts it and reads its components, which is what
        // crashed earlier builds of this command on load. HealthStateEntityManagerId is
        // (group index within the asset << 16) | member index, PartComponentCount counts the
        // entity's PartComponentData components, and a prop that ships its own physics resource
        // is Havok-backed: its shape graph is transplanted into the group's physics resource and its
        // parts appended after the group's current part count.
        internal static bool s_noCollision;

        // --member-stub <blueprint>: point every EBX-backed member's MemberType at ONE shipped
        // blueprint's entity instead of at the prop's own. A member renders its own MeshAsset, so
        // MemberType only has to be a static-model entity carrying a single PartComponentData --
        // which is what a group member needs anyway (the group owns physics and health). This
        // sidesteps cloning the prop: the SDK's binary EBX round-trip corrupts some of the ported
        // CarboniteUL blueprints, and the reader then throws on an out-of-range array index.
        internal static string s_memberStub;

        internal static int AddGroupMember(string assetPath, int groupIndex, string blueprintPath, string meshPath,
            string pos, string rot, string scale, string bundleName, bool noBundle, ExportLogger logger)
        {
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("level add: no project loaded. Run 'project load' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level add: AssetManager not initialized."); return 1; }

            EbxAssetEntry assetEntry = App.AssetManager.GetEbxEntry(assetPath);
            if (assetEntry == null) { Console.Error.WriteLine("level add: asset not found: " + assetPath); return 1; }
            EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(blueprintPath);
            if (bpEntry == null) { Console.Error.WriteLine("level add: blueprint not found: " + blueprintPath); return 1; }

            EbxAsset asset, bpAsset;
            try { asset = App.AssetManager.GetEbx(assetEntry); }
            catch (Exception ex) { Console.Error.WriteLine("level add: load failed: " + Describe(ex)); return 1; }
            try { bpAsset = App.AssetManager.GetEbx(bpEntry); }
            catch (Exception ex) { Console.Error.WriteLine("level add: blueprint load failed: " + Describe(ex)); return 1; }

            // A group member's entity must carry PartComponentData and NOTHING else: the group owns the
            // physics and health components for every member it holds. Every shipped EBX-backed member
            // in Cloud City points at an entity with exactly that one component. Pointing one at a
            // blueprint that keeps its own StaticModelPhysicsComponentData / StaticModelHealthComponentData
            // faulted the load at game+0x549B22E on a health-state id of 0xFFFFFFFF -- the registration
            // never produced an entity. Our ported clones do keep those components, and their standalone
            // placements still need them, so route the member at a stripped sibling rather than editing
            // the blueprint everything else uses. Havok-backed members are left alone: they go through
            // the group's physics resource and are not this shape.
            // Physics keys off the PROP; MemberType may be swapped for a stripped stub below.
            string physicsBlueprintPath = blueprintPath;
            EbxAssetEntry physicsBpEntry = bpEntry;
            EbxAsset physicsBpAsset = bpAsset;
            {
                string groupErr;
                object earlyGroup = FindStaticGroup(asset.RootObject, groupIndex, out groupErr);
                bool havokBacked = !s_noCollision && earlyGroup != null
                    && (App.AssetManager.GetResEntry(blueprintPath + "_physics_win32")
                        ?? App.AssetManager.GetResEntry(blueprintPath.ToLowerInvariant() + "_physics_win32")) != null
                    && FindPhysicsResourcePath(earlyGroup) != null;
                // A member's MemberType must carry PartComponentData and NOTHING else -- being
                // Havok-backed does not exempt it. Carbonite faulted the load at game+0x549B22E on a
                // health-state id of 0xFFFFFFFF exactly this way. The prop's own path is still what
                // names its <bp>_physics_win32 shape resource, so keep it for the transplant below.
                if (EntityCarriesNonPartComponents(bpAsset))
                {
                    // Whatever MemberType ends up being, the member still has to render THIS prop, so
                    // pin the mesh before the blueprint is swapped out from under it.
                    if (meshPath == null) meshPath = FindBlueprintMesh(bpAsset.RootObject);
                    string replacement = s_memberStub ?? EnsureGroupMemberBlueprint(blueprintPath, logger);
                    if (replacement == null) return 1;
                    blueprintPath = replacement;
                    bpEntry = App.AssetManager.GetEbxEntry(blueprintPath);
                    if (bpEntry == null) { Console.Error.WriteLine("level add: member blueprint missing: " + replacement); return 1; }
                    try { bpAsset = App.AssetManager.GetEbx(bpEntry); }
                    catch (Exception ex) { Console.Error.WriteLine("level add: member blueprint load failed: " + Describe(ex)); return 1; }
                }
            }

            object entity = ResolveInternal(ReadMember(bpAsset.RootObject, "Object"));
            if (entity == null)
            {
                Console.Error.WriteLine("level add: blueprint '" + blueprintPath + "' has no Object; only ObjectBlueprints wrapping an entity can become group members");
                return 1;
            }
            if (!InheritsFrom(entity.GetType(), "EntityData"))
            {
                Console.Error.WriteLine("level add: blueprint '" + blueprintPath + "' wraps a " + entity.GetType().Name + ", not an entity");
                return 1;
            }
            Guid entityGuid;
            bool entityExported;
            try { dynamic dyn = entity; entityGuid = (Guid)dyn.GetInstanceGuid().ExportedGuid; entityExported = (bool)dyn.GetInstanceGuid().IsExported; }
            catch (Exception ex) { Console.Error.WriteLine("level add: could not read the entity's instance GUID: " + Describe(ex)); return 1; }
            // A member can only reference an exported instance. Some shipped blueprints keep their entity
            // internal (Starkiller Crate_02); export it under a deterministic GUID and write the blueprint
            // back, since an empty class GUID is a dangling reference the game cannot resolve.
            bool exportEntity = !entityExported || entityGuid == Guid.Empty;
            if (exportEntity)
            {
                try
                {
                    dynamic dyn = entity;
                    int internalId = (int)dyn.GetInstanceGuid().InternalId;
                    entityGuid = Utils.GenerateDeterministicGuid(bpAsset.Objects, entity.GetType(), bpEntry.Guid);
                    dyn.SetInstanceGuid(new AssetClassGuid(entityGuid, internalId));
                }
                catch (Exception ex) { Console.Error.WriteLine("level add: the blueprint's entity is not an exported instance and could not be exported: " + Describe(ex)); return 1; }
            }
            int partComponents = Math.Max(1, CountPartComponents(entity));

            // The group renders the member's mesh, not the blueprint, so MeshAsset has to be filled in.
            EbxAssetEntry meshEntry = null;
            if (meshPath != null)
            {
                meshEntry = App.AssetManager.GetEbxEntry(meshPath);
                if (meshEntry == null) { Console.Error.WriteLine("level add: mesh not found: " + meshPath); return 1; }
            }
            else
            {
                string found = FindBlueprintMesh(bpAsset.RootObject);
                if (found == null)
                {
                    Console.Error.WriteLine("level add: could not find a Mesh reference on blueprint '" + blueprintPath +
                        "'. Pass --mesh <mesh-ebx-path>.");
                    return 1;
                }
                meshEntry = App.AssetManager.GetEbxEntry(found);
                if (meshEntry == null) { Console.Error.WriteLine("level add: blueprint names a mesh that does not exist: " + found); return 1; }
            }

            string error;
            object group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) { Console.Error.WriteLine("level add: " + error); return 1; }
            IList members = ReadList(group, "MemberDatas");
            if (members == null) { Console.Error.WriteLine("level add: group exposes no MemberDatas list"); return 1; }

            Type memberType = PropertyPathWriter.ListElementType(members);
            if (memberType == null) { Console.Error.WriteLine("level add: cannot determine the member type"); return 1; }

            LinearTransformData xform;
            try { xform = TransformBuilder.Build(pos, rot, scale); }
            catch (ArgumentException ex) { Console.Error.WriteLine("level add: " + ex.Message); return 1; }

            // A prop with collision ships <blueprint>_physics_win32; that makes the member Havok-backed.
            // --no-collision places a prop that has a physics resource as an EBX-backed member anyway
            // (no shape transplant); a bisecting aid, the prop then has no collision in-game.
            ResAssetEntry propRes = s_noCollision ? null : App.AssetManager.GetResEntry(physicsBlueprintPath + "_physics_win32")
                ?? App.AssetManager.GetResEntry(physicsBlueprintPath.ToLowerInvariant() + "_physics_win32");
            HavokPhysicsResource physics = null;
            ResAssetEntry groupRes = null;
            byte[] propBytes = null;
            HavokPhysicsResource.PropInfo propInfo = default(HavokPhysicsResource.PropInfo);
            IList boxes = ReadList(group, "PartBoundingBoxes");
            string resPath = propRes != null ? FindPhysicsResourcePath(group) : null;
            if (propRes != null && resPath == null)
            {
                // A group without a physics resource cannot carry collision at all; every member in
                // it is EBX-backed. Place the prop the same way rather than refusing.
                logger.LogWarning("'" + blueprintPath + "' has collision (" + propRes.Name + ") but group " + groupIndex +
                    " has no physics resource; placing it EBX-backed without collision.");
                propRes = null;
            }
            if (propRes != null)
            {
                groupRes = App.AssetManager.GetResEntry(resPath);
                if (groupRes == null) { Console.Error.WriteLine("level add: physics resource not found: " + resPath); return 1; }
                byte[] groupBytes;
                if (!TryReadResBytes(groupRes, out groupBytes, out error)) { Console.Error.WriteLine("level add: could not read " + resPath + ": " + error); return 1; }
                if (!TryReadResBytes(propRes, out propBytes, out error)) { Console.Error.WriteLine("level add: could not read " + propRes.Name + ": " + error); return 1; }
                try
                {
                    physics = HavokPhysicsResource.Parse(groupBytes);
                    propInfo = HavokPhysicsResource.InspectProp(propBytes);
                }
                catch (Exception ex) { Console.Error.WriteLine("level add: physics resource could not be decoded: " + Describe(ex)); return 1; }
                int havokTotal = CountHavokPlacements(members);
                if (physics.Placement == null || physics.Placement.InstanceCount != havokTotal)
                {
                    Console.Error.WriteLine("level add: the group's members need " + havokTotal + " Havok transform(s) but " + resPath + " holds " +
                        (physics.Placement == null ? 0 : physics.Placement.InstanceCount) + "; run 'level validate' on this asset first");
                    return 1;
                }
                if (boxes != null && boxes.Count != physics.PartCount)
                {
                    Console.Error.WriteLine("level add: group " + groupIndex + " lists " + boxes.Count + " part bounding boxes but " + resPath +
                        " declares " + physics.PartCount + " parts; run 'level validate' on this asset first");
                    return 1;
                }
            }

            BundlePlan bundlePlan = null;
            if (!noBundle)
            {
                string planError;
                bundlePlan = PlanMemberBundling(assetEntry, bpEntry, meshEntry, blueprintPath, bundleName, out planError);
                if (bundlePlan == null) { Console.Error.WriteLine("level add: bundling failed: " + planError + " (pass --bundle <name> or --no-bundle)"); return 1; }
            }

            if (DryRun.Active)
            {
                DryRun.Report(logger, "add member for {0} to {1} group {2} at {3}{4}", blueprintPath, assetPath, groupIndex, pos,
                    propRes != null ? " with " + propInfo.PartCount + " physics part(s) transplanted from " + propRes.Name : "");
                return 0;
            }

            if (exportEntity)
            {
                if (!EbxWriteCommand.CallModifyEbx(bpEntry.Name, bpAsset, logger)) return 1;
                logger.LogWarning("level add: exported the entity of {0} (instance {1}) so the member can reference it; the blueprint is now part of the project", bpEntry.Name, entityGuid);
            }

            int memberIndex = members.Count;
            object member;
            int firstPart = -1, parts = 0, detailIndex = -1;
            string physicsDetail = null;
            byte[] newRes = null, newMeta = null;
            EbxAssetEntry havokAssetEntry = null; EbxAsset havokAsset = null;
            try
            {
                member = Activator.CreateInstance(memberType);
                EbxRefWriter.SetRefByPath(member, "MemberType", bpEntry.Guid, entityGuid);
                EbxRefWriter.SetRefByPath(member, "MeshAsset", meshEntry.Guid, ResolveExportedGuid(meshEntry));
                SetNumericMember(member, "InstanceCount", 1);
                SetNumericMember(member, "NetworkIdCountPerInstance", 0);
                SetNumericMember(member, "PartComponentCount", partComponents);
                // HealthStateEntityManagerId is a plain 0-based index, not a composite key. The
                // engine does  mov ecx,eax / mov rax,[mgr+0x2A0] / mov rcx,[rax+rcx*8]  (game+0x549B202),
                // with bit 31 selecting a second table at +0x178 rather than meaning "unset". A group
                // at index 1 therefore made this 0x10000 and the load read 65536 entries past the end:
                // ACCESS_VIOLATION at game+0x549B202 with rcx=0x10000. Every shipped member of Cloud
                // City's 354-member group carries exactly its own member index, 0..353 with no gaps.
                SetNumericMember(member, "HealthStateEntityManagerId", NextHealthStateId(asset));
                SetRangeUnset(member, "NetworkIdRange");

                if (physics == null)
                {
                    IList transforms = ReadList(member, "InstanceTransforms");
                    if (transforms == null) { Console.Error.WriteLine("level add: the member type has no InstanceTransforms list"); return 1; }
                    Type transformType = PropertyPathWriter.ListElementType(transforms);
                    if (transformType == null) { Console.Error.WriteLine("level add: cannot determine the transform type"); return 1; }
                    transforms.Add(ApplyTransform(Activator.CreateInstance(transformType), xform, true));
                    SetNumericMember(member, "PhysicsPartCountPerInstance", 0);
                    SetRangeUnset(member, "PhysicsPartRange");
                }
                else
                {
                    // The resource's per-instance detail index addresses the group HavokAsset's
                    // ExternalAssets (the prop's RigidBodyData) and Aabb (its part boxes) lists; a new
                    // prop needs a slot in both, or the game reads past the tables on load.
                    string derr;
                    bool slotAdded;
                    detailIndex = RegisterPropInGroupPhysicsAsset(group, physicsBpEntry, physicsBpAsset, propInfo, out havokAssetEntry, out havokAsset, out slotAdded, out derr);
                    if (detailIndex < 0) { Console.Error.WriteLine("level add: " + derr); return 1; }
                    firstPart = physics.AppendMember(propBytes, xform, detailIndex, out parts, out physicsDetail);
                    // Each ExternalAssets slot needs its RaycastMaterialIndicesLookups entry on every group
                    // body; without it the first ray to hit the prop reads past that list
                    // (InternalGroupRigidBody::rayCast, game+0x1444ADC, 8-14 s after world-up).
                    if (slotAdded && Environment.GetEnvironmentVariable("FROSTYCLI_NO_RAYCAST_LOOKUPS") != "1" &&
                        !AppendRaycastLookups(group, physics.MapPropMaterialRows(propBytes), detailIndex, out derr))
                    { Console.Error.WriteLine("level add: " + derr); return 1; }
                    newRes = physics.Write();
                    newMeta = physics.Meta;
                    SetNumericMember(member, "PhysicsPartCountPerInstance", parts);
                    SetRange(member, "PhysicsPartRange", firstPart, firstPart + parts - 1);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("level add: could not build the member: " + Describe(ex)); return 1; }

            try { members.Add(member); }
            catch (Exception ex) { Console.Error.WriteLine("level add: append failed: " + Describe(ex)); return 1; }

            if (physics != null)
            {
                try
                {
                    if (boxes != null)
                        for (int i = 0; i < parts; i++)
                        {
                            float[][] pb = propInfo.PartBoxes != null && i < propInfo.PartBoxes.Length ? propInfo.PartBoxes[i] : new[] { propInfo.BoxMin, propInfo.BoxMax };
                            boxes.Add(MakeBox(PropertyPathWriter.ListElementType(boxes), pb[0], pb[1]));
                        }
                    IList components = ReadList(group, "Components");
                    if (components != null)
                    {
                        foreach (object raw in components)
                        {
                            object comp = ResolveInternal(raw) ?? raw;
                            if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                            IList partList = ReadList(comp, "Parts");
                            if (partList == null) continue;
                            Type partType = PropertyPathWriter.ListElementType(partList);
                            for (int i = 0; i < parts; i++)
                            {
                                object part = Activator.CreateInstance(partType);
                                object aabb = ReadMember(part, "Aabb");
                                if (aabb == null) SetPropOrField(part, partType, "Aabb", MakeBox(part.GetType().GetProperty("Aabb").PropertyType, new float[3], new float[3]));
                                partList.Add(part);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { members.RemoveAt(members.Count - 1); } catch { }
                    Console.Error.WriteLine("level add: could not grow the group's part tables: " + Describe(ex));
                    return 1;
                }
            }

            if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            if (havokAsset != null && !EbxWriteCommand.CallModifyEbx(havokAssetEntry.Name, havokAsset, logger)) return 1;
            if (physics != null)
            {
                try { ResWriter.Modify(groupRes.Name, newRes, newMeta); }
                catch (Exception ex) { Console.Error.WriteLine("level add: physics resource write failed: " + Describe(ex)); return 1; }
            }

            logger.Log("level add: {0} group {1} member {2} is a new placement of {3}{4}",
                assetPath, groupIndex, members.Count - 1, blueprintPath,
                physicsDetail != null ? " with collision: parts " + firstPart + ".." + (firstPart + parts - 1) + " (" + physicsDetail + ")" : " (no physics resource; EBX-backed)");
            // The plan was made before the writes; apply it now so the blueprint, mesh, textures and
            // physics asset load with this level (validate's mesh-not-bundled rule checks exactly this).
            if (bundlePlan != null) logger.Log("level add: bundled in {0}", ApplyMemberBundling(bundlePlan));
            else if (noBundle)
                logger.LogWarning("--no-bundle: the blueprint, mesh, textures and physics asset were not added to any bundle; the member will not load unless they already ship with this level.");
            if (!s_inPlace)
                logger.LogWarning("The mesh must also be registered in this sub-world's MeshVariationDb_Win32 or it will not render. See 'mvdb add'.");
            return 0;
        }

        // Appends the prop's MaterialIndicesLookup (group material row per prop material row) to the
        // RaycastMaterialIndicesLookups list of every GroupRigidBodyData the group's physics component
        // carries. Shipped groups keep that list exactly as long as the HavokAsset's ExternalAssets
        // (Endor_01 Shared_Art: 390/390 on both bodies); the engine indexes it by detail resource index
        // without a bounds check.
        private static bool AppendRaycastLookups(object group, int[] rows, int detailIndex, out string error)
        {
            error = null;
            int bodiesTouched = 0;
            IList components = ReadList(group, "Components");
            if (components != null)
                foreach (object raw in components)
                {
                    object comp = ResolveInternal(raw) ?? raw;
                    if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                    IList bodies = ReadList(comp, "PhysicsBodies");
                    if (bodies == null) continue;
                    foreach (object b in bodies)
                    {
                        object body = ResolveInternal(b) ?? b;
                        IList lookups = body != null ? ReadList(body, "RaycastMaterialIndicesLookups") : null;
                        if (lookups == null) continue;
                        if (lookups.Count != detailIndex)
                        {
                            error = "group body lists " + lookups.Count + " raycast material lookup(s) but the new prop takes external asset slot " +
                                    detailIndex + "; the tables are out of step (run 'level validate')";
                            return false;
                        }
                        try
                        {
                            Type lookupType = PropertyPathWriter.ListElementType(lookups);
                            object lookup = Activator.CreateInstance(lookupType);
                            IList indices = ReadList(lookup, "MaterialIndices");
                            if (indices == null) { error = "MaterialIndicesLookup has no MaterialIndices list"; return false; }
                            Type indexType = PropertyPathWriter.ListElementType(indices) ?? typeof(uint);
                            foreach (int row in rows) indices.Add(Convert.ChangeType(row, indexType, CultureInfo.InvariantCulture));
                            lookups.Add(lookup);
                            bodiesTouched++;
                        }
                        catch (Exception ex) { error = "could not grow RaycastMaterialIndicesLookups: " + Describe(ex); return false; }
                    }
                }
            if (bodiesTouched == 0) { error = "the group's physics bodies expose no RaycastMaterialIndicesLookups list"; return false; }
            return true;
        }

        // Finds (or appends) the prop's slot in the group HavokAsset's ExternalAssets/Aabb lists.
        // ExternalAssets entries reference the prop blueprint's first RigidBodyData; Aabb entries carry
        // one AxisAlignedBox per physics part. Returns the slot index, or -1 with an error.
        private static int RegisterPropInGroupPhysicsAsset(object group, EbxAssetEntry bpEntry, EbxAsset bpAsset,
            HavokPhysicsResource.PropInfo propInfo, out EbxAssetEntry havokEntry, out EbxAsset havokAsset, out bool slotAdded, out string error)
        {
            havokEntry = null; havokAsset = null; error = null; slotAdded = false;
            // The group's physics component names the HavokAsset through its rigid bodies. In shipped
            // levels that HavokAsset is an object inside the level asset itself (an internal reference),
            // so editing it rides along with the level write; an external one is loaded and written.
            object havokObject = null;
            IList components = ReadList(group, "Components");
            if (components != null)
                foreach (object raw in components)
                {
                    object comp = ResolveInternal(raw) ?? raw;
                    if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                    IList bodies = ReadList(comp, "PhysicsBodies");
                    if (bodies == null) continue;
                    foreach (object b in bodies)
                    {
                        object assetRef = ReadMember(ResolveInternal(b) ?? b, "Asset");
                        object inline = ResolveInternal(assetRef);
                        if (inline != null) { havokObject = inline; break; }
                        Guid fg, cg;
                        if (TryReadExternalRef(assetRef, out fg, out cg))
                        {
                            havokEntry = App.AssetManager.GetEbxEntry(fg);
                            if (havokEntry == null) { error = "group HavokAsset " + fg + " not found"; return -1; }
                            try { havokAsset = App.AssetManager.GetEbx(havokEntry); }
                            catch (Exception ex) { error = "group HavokAsset load failed: " + Describe(ex); return -1; }
                            havokObject = havokAsset.RootObject;
                            break;
                        }
                    }
                    if (havokObject != null) break;
                }
            if (havokObject == null) { error = "the group's physics component names no HavokAsset"; return -1; }
            IList external = ReadList(havokObject, "ExternalAssets");
            IList aabbs = ReadList(havokObject, "Aabb");
            if (external == null || aabbs == null) { error = "group HavokAsset has no ExternalAssets/Aabb lists"; return -1; }
            if (external.Count != aabbs.Count) { error = "group HavokAsset lists disagree (" + external.Count + " external assets, " + aabbs.Count + " boxes); run 'level validate'"; return -1; }
            for (int i = 0; i < external.Count; i++)
            {
                Guid fg, cg;
                if (TryReadExternalRef(external[i], out fg, out cg) && fg == bpEntry.Guid) { havokAsset = null; return i; }   // already registered: nothing to add
            }
            // The prop blueprint's first rigid body.
            Guid bodyGuid = Guid.Empty;
            object entity = ResolveInternal(ReadMember(bpAsset.RootObject, "Object"));
            IList bpComponents = entity != null ? ReadList(entity, "Components") : null;
            if (bpComponents != null)
                foreach (object raw in bpComponents)
                {
                    object comp = ResolveInternal(raw) ?? raw;
                    if (comp == null || comp.GetType().Name != "StaticModelPhysicsComponentData") continue;
                    IList bodies = ReadList(comp, "PhysicsBodies");
                    if (bodies == null || bodies.Count == 0) continue;
                    object body = ResolveInternal(bodies[0]) ?? bodies[0];
                    bodyGuid = ResolveExportedGuidOf(body);
                    break;
                }
            if (bodyGuid == Guid.Empty) { error = "blueprint " + bpEntry.Name + " has no exported RigidBodyData to register in the group HavokAsset"; return -1; }
            try
            {
                external.Add(new PointerRef(new EbxImportReference { FileGuid = bpEntry.Guid, ClassGuid = bodyGuid }));
                Type aabbType = PropertyPathWriter.ListElementType(aabbs);
                object entry = Activator.CreateInstance(aabbType);
                IList partBoxes = ReadList(entry, "PartAabb");
                if (partBoxes == null) { error = "AssetAabbs has no PartAabb list"; return -1; }
                Type boxType = PropertyPathWriter.ListElementType(partBoxes);
                int parts = Math.Max(1, propInfo.PartCount);
                for (int i = 0; i < parts; i++)
                {
                    float[][] pb = propInfo.PartBoxes != null && i < propInfo.PartBoxes.Length ? propInfo.PartBoxes[i] : new[] { propInfo.BoxMin, propInfo.BoxMax };
                    partBoxes.Add(MakeBox(boxType, pb[0], pb[1]));
                }
                aabbs.Add(entry);
                if (havokAsset != null) havokAsset.AddDependency(bpEntry.Guid);
                slotAdded = true;
            }
            catch (Exception ex) { error = "could not grow the group HavokAsset's tables: " + Describe(ex); return -1; }
            return external.Count - 1;
        }

        private static Guid ResolveExportedGuidOf(object obj)
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

        // ----- bundling for new members -----
        //
        // A member's blueprint, mesh (with its MeshSet res, geometry chunks and shader-block res),
        // material textures and physics asset must ship in a bundle the level loads, or streaming
        // never brings them in and the member is invisible. Shipped levels keep props in a sibling
        // "Shared_Art" bundle next to the sub-world bundle, so that is the default target.

        private static BundleEntry PickMemberBundle(EbxAssetEntry levelEntry, string requested, out string reason)
        {
            reason = null;
            if (!string.IsNullOrEmpty(requested))
            {
                BundleEntry chosen = BundleCommand.FindBundle(requested);
                if (chosen == null) reason = "bundle not found: " + requested;
                return chosen;
            }
            var ids = new List<int>(levelEntry.Bundles);
            foreach (int id in levelEntry.AddedBundles) if (!ids.Contains(id)) ids.Add(id);
            if (ids.Count == 0) { reason = "the level asset is in no bundle"; return null; }
            BundleEntry own = App.AssetManager.GetBundleEntry(ids[0]);
            if (own == null) { reason = "the level asset's bundle could not be resolved"; return null; }
            // Shipped members' blueprints sit in the level asset's own bundle (Endor's LightGround_01 is
            // in win32/Levels/MP/Endor_01/Endor_01, not in Shared_Art), so that is where a new member's
            // assets go unless a bundle is named explicitly.
            return own;
        }

        private sealed class BundlePlan
        {
            public BundleEntry Bundle;
            public int BundleId;
            public List<AssetEntry> Entries = new List<AssetEntry>();
            public List<string> Skipped = new List<string>();
        }

        // Everything a new member needs, resolved BEFORE the level is modified: gathering loads the
        // mesh, its textures and the physics asset, and loading assets between ModifyEbx and the
        // project save has been observed to leave the saved level unreadable.
        private static BundlePlan PlanMemberBundling(EbxAssetEntry levelEntry, EbxAssetEntry bpEntry, EbxAssetEntry meshEntry,
            string blueprintPath, string requestedBundle, out string error)
        {
            error = null;
            string reason;
            BundleEntry bundle = PickMemberBundle(levelEntry, requestedBundle, out reason);
            if (bundle == null) { error = reason; return null; }
            var plan = new BundlePlan { Bundle = bundle, BundleId = App.AssetManager.GetBundleId(bundle) };
            // Everything the blueprint reaches through EBX references goes in, the way the community
            // Bundle Manager (and the shipped map mods built with it) do it: the blueprint, its class
            // schematics and physics asset, the mesh, the mesh's LOD group and shader graphs, and the
            // textures those shaders sample, each with its res and chunks. A missing schematics or
            // LOD group asset crashes the level on load just like a missing mesh does.
            var queue = new Queue<EbxAssetEntry>();
            var seen = new HashSet<Guid>();
            queue.Enqueue(bpEntry); seen.Add(bpEntry.Guid);
            if (meshEntry != null && seen.Add(meshEntry.Guid)) queue.Enqueue(meshEntry);
            EbxAssetEntry physicsAsset = App.AssetManager.GetEbxEntry(blueprintPath + "_Physics_Win32");
            if (physicsAsset != null && seen.Add(physicsAsset.Guid)) queue.Enqueue(physicsAsset);
            int guard = 0;
            while (queue.Count > 0 && guard++ < 512)
            {
                EbxAssetEntry e = queue.Dequeue();
                AddEntryWithResources(e, plan);
                EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); }
                catch (Exception ex) { plan.Skipped.Add(e.Name + " dependencies: " + ex.Message); continue; }
                // A SchematicsAsset names its ClassInfoAsset in a string field, not a PointerRef, so
                // the dependency walk never reaches it. Shipped levels always bundle the pair together
                // (Hoth/Yavin carry both halves of every cryo-steam effect); a level bundle holding
                // only the schematics faults the load at game+0x167FE84 reading a null class payload.
                if (string.Equals(e.Type, "SchematicsAsset", StringComparison.OrdinalIgnoreCase))
                {
                    string classPath = null;
                    try { classPath = (string)((dynamic)a.RootObject).InstanceType; } catch { }
                    if (string.IsNullOrEmpty(classPath) && e.Name.EndsWith("_schematics", StringComparison.OrdinalIgnoreCase))
                        classPath = e.Name.Substring(0, e.Name.Length - "_schematics".Length);
                    EbxAssetEntry ci = classPath != null ? App.AssetManager.GetEbxEntry(classPath) : null;
                    if (ci != null && seen.Add(ci.Guid)) queue.Enqueue(ci);
                }
                foreach (Guid dep in a.Dependencies)
                {
                    if (!seen.Add(dep)) continue;
                    EbxAssetEntry d = App.AssetManager.GetEbxEntry(dep);
                    if (d == null) continue;
                    // Levels and sub-worlds are never a prop's dependency worth carrying; stop there.
                    if (d.Type == "SubWorldData" || d.Type == "LevelData" || d.Type == "WorldData") continue;
                    // Shader graphs live in shared bundles; shipped map mods never add them to a level
                    // bundle (the textures they sample come in through the variation database entry).
                    if (!s_bundleShaders && d.Type != null && d.Type.IndexOf("Shader", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // ...but an effect / screen-space shader's default textures do NOT come through any
                        // variation database entry (CoruscantBF3 CBX16: the starfighter sense-of-speed shader's
                        // FX/Textures/Gradients/T_SenseOfSpeed_Noise_Ramp_C, unresolved GUID crash game+0x1522B97).
                        // FROSTYCLI_SHADER_TEXTURES=1: leave the shader out, carry the textures it references.
                        if (Environment.GetEnvironmentVariable("FROSTYCLI_SHADER_TEXTURES") == "1")
                        {
                            try
                            {
                                EbxAsset sh = App.AssetManager.GetEbx(d);
                                foreach (Guid sd in sh.Dependencies)
                                {
                                    EbxAssetEntry te = App.AssetManager.GetEbxEntry(sd);
                                    if (te == null || te.Type == null || te.Type.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    if (seen.Add(sd)) queue.Enqueue(te);
                                }
                            }
                            catch (Exception ex) { plan.Skipped.Add(d.Name + " shader textures: " + ex.Message); }
                        }
                        continue;
                    }
                    if (d.Type != null && s_bundleSkipTypes.Count > 0)
                    {
                        bool skip = false;
                        foreach (string st in s_bundleSkipTypes)
                            if (d.Type.IndexOf(st, StringComparison.OrdinalIgnoreCase) >= 0) { skip = true; break; }
                        if (skip) continue;
                    }
                    queue.Enqueue(d);
                }
            }
            return plan;
        }

        private static void AddEntryWithResources(EbxAssetEntry entry, BundlePlan plan)
        {
            if (entry == null) return;
            plan.Entries.Add(entry);
            List<ulong> resRids; List<Guid> chunkIds;
            try { BundleCommand.GatherSubResources(entry, out resRids, out chunkIds); }
            catch (Exception ex) { plan.Skipped.Add(entry.Name + " resources: " + ex.Message); return; }
            BundleCommand.LinkSubResources(entry, resRids, chunkIds);
            foreach (ulong rid in resRids)
            {
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                if (re != null) plan.Entries.Add(re);
            }
            foreach (Guid cid in chunkIds)
            {
                ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(cid);
                if (ce != null) plan.Entries.Add(ce);
            }
            // Shader block depots ride alongside the mesh as "<mesh>_mesh/blocks".
            ResAssetEntry blocks = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
            if (blocks != null) plan.Entries.Add(blocks);
        }

        // Adds assets (with their res and chunks) to the bundle a level's placements use. Returns a
        // summary line, or null with an error.
        // ---- textures a mesh's shader block depot binds -----------------------------------------------------------
        // A shipped mesh's depot ("<mesh>_mesh/blocks", ShaderBlockDepot res) binds its textures by the texture's ROOT
        // INSTANCE guid (memory bf2-instance-vs-file-guid). Those references appear in no EBX dependency list and often
        // in no variation-database entry either (the 2015 AT-AT wreck's depot binds Gameplay/Vehicles/Ground/AT-AT/
        // texture/T_ATATTile_01_C, which its MVDB entry does not), yet the game resolves them when the mesh loads and
        // crashes at game+0x1522B97 on one that is not bundled. The depot bytes are scanned for any texture's instance
        // guid (16 bytes, bytes_le) -- exact, and independent of the parameter encoding.
        private static Dictionary<Guid, EbxAssetEntry> textureByInstance;
        private static Dictionary<string, List<ResAssetEntry>> depotsByMesh;

        internal static List<EbxAssetEntry> DepotTextures(EbxAssetEntry mesh, out int depots)
        {
            depots = 0;
            var result = new List<EbxAssetEntry>();
            if (mesh == null) return result;
            if (textureByInstance == null)
            {
                var map = new Dictionary<Guid, EbxAssetEntry>();
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (e.Type == null || e.Type.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        object root = App.AssetManager.GetEbx(e).RootObject;
                        Guid ig = ((AssetClassGuid)root.GetType().GetMethod("GetInstanceGuid").Invoke(root, null)).ExportedGuid;
                        if (ig != Guid.Empty && !map.ContainsKey(ig)) map[ig] = e;
                    }
                    catch { }
                }
                textureByInstance = map;
                depotsByMesh = new Dictionary<string, List<ResAssetEntry>>(StringComparer.OrdinalIgnoreCase);
                foreach (ResAssetEntry re in App.AssetManager.EnumerateRes((uint)ResourceType.ShaderBlockDepot))
                {
                    // "<mesh>_mesh/blocks": the owning mesh is the name up to the last "_mesh/"
                    int cut = re.Name.LastIndexOf("_mesh/", StringComparison.OrdinalIgnoreCase);
                    if (cut <= 0) continue;
                    string owner = re.Name.Substring(0, cut);
                    List<ResAssetEntry> l;
                    if (!depotsByMesh.TryGetValue(owner, out l)) depotsByMesh[owner] = l = new List<ResAssetEntry>();
                    l.Add(re);
                }
            }
            List<ResAssetEntry> mine;
            if (!depotsByMesh.TryGetValue(mesh.Name, out mine))
            {
                // a mesh made after the cache was built (port import in the same process): its depot by name
                ResAssetEntry own = App.AssetManager.GetResEntry(mesh.Name.ToLowerInvariant() + "_mesh/blocks");
                if (own == null) return result;
                mine = new List<ResAssetEntry> { own };
            }
            var seen = new HashSet<Guid>();
            foreach (ResAssetEntry re in mine)
            {
                byte[] b;
                try
                {
                    using (Stream st = App.AssetManager.GetRes(re))
                    {
                        if (st == null) continue;
                        var ms = new MemoryStream(); st.CopyTo(ms); b = ms.ToArray();
                    }
                }
                catch { continue; }
                depots++;
                var probe = new byte[16];
                for (int k = 0; k + 16 <= b.Length; k++)
                {
                    Buffer.BlockCopy(b, k, probe, 0, 16);
                    EbxAssetEntry tex;
                    if (textureByInstance.TryGetValue(new Guid(probe), out tex) && seen.Add(tex.Guid)) result.Add(tex);
                }
            }
            return result;
        }

        internal static string BundleAssets(string levelPath, string bundleName, IEnumerable<EbxAssetEntry> assets, out string error)
        {
            error = null;
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry(levelPath);
            if (levelEntry == null) { error = "level not found: " + levelPath; return null; }
            string reason;
            BundleEntry bundle = PickMemberBundle(levelEntry, bundleName, out reason);
            if (bundle == null) { error = reason; return null; }
            var plan = new BundlePlan { Bundle = bundle, BundleId = App.AssetManager.GetBundleId(bundle) };
            foreach (EbxAssetEntry a in assets) AddEntryWithResources(a, plan);
            if (DryRun.Active) return "would add " + plan.Entries.Count + " entries to " + bundle.Name;
            return ApplyMemberBundling(plan);
        }

        // Applies a plan: only AddToBundle calls, no asset loads. Returns a summary line.
        private static string ApplyMemberBundling(BundlePlan plan)
        {
            int ebx = 0, res = 0, chunks = 0, already = 0;
            var seen = new HashSet<AssetEntry>();
            foreach (AssetEntry entry in plan.Entries)
            {
                if (!seen.Add(entry)) continue;
                bool added;
                try { added = entry.AddToBundle(plan.BundleId); }
                catch (Exception ex) { plan.Skipped.Add(entry.Name + ": " + ex.Message); continue; }
                if (!added) { already++; continue; }
                if (entry is EbxAssetEntry) ebx++; else if (entry is ResAssetEntry) res++; else chunks++;
            }
            string summary = plan.Bundle.Name + ": " + ebx + " ebx, " + res + " res, " + chunks + " chunk(s) added, " + already + " already present";
            if (plan.Skipped.Count > 0) summary += "; skipped " + string.Join("; ", plan.Skipped.ToArray());
            return summary;
        }

        // Every texture the mesh's materials reference, resolved through their shader instance data.
        private static List<EbxAssetEntry> CollectMaterialTextures(EbxAsset meshAsset)
        {
            var result = new List<EbxAssetEntry>();
            var seen = new HashSet<Guid>();
            IList materials = ReadList(meshAsset.RootObject, "Materials");
            if (materials == null) return result;
            foreach (object raw in materials)
            {
                object material = ResolveInternal(raw) ?? raw;
                object shader = material != null ? ReadMember(material, "Shader") : null;
                IList textures = shader != null ? ReadList(shader, "TextureParameters") : null;
                if (textures == null) continue;
                foreach (object param in textures)
                {
                    object value = ReadMember(param, "Value");
                    object external = value != null ? ReadMember(value, "External") : null;
                    object fileGuid = external != null ? ReadMember(external, "FileGuid") : null;
                    if (!(fileGuid is Guid) || (Guid)fileGuid == Guid.Empty || !seen.Add((Guid)fileGuid)) continue;
                    MethodInfo getByGuid = App.AssetManager.GetType().GetMethod("GetEbxEntry", new[] { typeof(Guid) });
                    var entry = getByGuid != null ? (EbxAssetEntry)getByGuid.Invoke(App.AssetManager, new object[] { fileGuid }) : null;
                    if (entry != null) result.Add(entry);
                }
            }
            return result;
        }

        private static int CountPartComponents(object entity)
        {
            int count = 0;
            IList components = ReadList(entity, "Components");
            if (components == null) return 0;
            foreach (object raw in components)
            {
                object comp = ResolveInternal(raw) ?? raw;
                if (comp == null) continue;
                if (InheritsFrom(comp.GetType(), "PartComponentData")) count++;
                count += CountPartComponents(comp);
            }
            return count;
        }

        // Builds a fresh AxisAlignedBox-shaped value. Vec3 members are classes in the generated SDK,
        // so a copied element would share them with its source; this allocates its own.
        internal static object MakeBox(Type boxType, float[] min, float[] max)
        {
            object box = Activator.CreateInstance(boxType);
            string[] names = { "min", "max" };
            float[][] values = { min, max };
            for (int k = 0; k < 2; k++)
            {
                PropertyInfo p = boxType.GetProperty(names[k], BindingFlags.Public | BindingFlags.Instance);
                if (p == null) throw new InvalidOperationException(boxType.Name + " has no '" + names[k] + "' member");
                object vec = p.GetValue(box);
                if (vec == null) { vec = Activator.CreateInstance(p.PropertyType); }
                string[] axes = { "x", "y", "z" };
                for (int a = 0; a < 3; a++) SetPropOrField(vec, vec.GetType(), axes[a], Convert.ChangeType(values[k][a], vec.GetType().GetProperty(axes[a]).PropertyType, CultureInfo.InvariantCulture));
                p.SetValue(box, vec);
            }
            return box;
        }

        private static void SetRangeUnset(object member, string name)
        {
            object range = ReadMember(member, name);
            if (range == null) return;
            SetNumericMember(range, "First", NoRange);
            SetNumericMember(range, "Last", NoRange);
            // IndexRange is a value type, so the edited box has to replace the original.
            if (range.GetType().IsValueType) SetPropOrField(member, member.GetType(), name, range);
        }

        // An ObjectBlueprint's Object is the entity it wraps; a static prop's is a
        // StaticModelEntityData carrying the Mesh reference the group needs.
        internal static string FindBlueprintMesh(object blueprintRoot)
        {
            object obj = ResolveInternal(ReadMember(blueprintRoot, "Object"));
            string path = ResolveBlueprintPath(ReadMember(obj, "Mesh"));
            if (!string.IsNullOrEmpty(path)) return path;

            IList objects = ReadList(blueprintRoot, "Objects");
            if (objects == null) return null;
            foreach (object value in objects)
            {
                object candidate = ResolveInternal(value);
                if (candidate == null) continue;
                path = ResolveBlueprintPath(ReadMember(candidate, "Mesh"));
                if (!string.IsNullOrEmpty(path)) return path;
            }
            return null;
        }

        internal static Guid ResolveExportedGuid(EbxAsset asset)
        {
            dynamic root = asset.RootObject;
            return (Guid)root.GetInstanceGuid().ExportedGuid;
        }

        internal static Guid ResolveExportedGuid(EbxAssetEntry entry)
        {
            return ResolveExportedGuid(App.AssetManager.GetEbx(entry));
        }

        // level remove <asset> --group G --member N --instance I
        //
        // Deletes one placement.  The mirror of AddGroupedInstance: same EBX-backed-only rule, and
        // the same "only touch a side array that is exactly as long as the instance count" rule --
        // trimming a deliberately short array would silently re-point the entries after the hole.
        private static int RemoveGroupedInstance(string assetPath, int groupIndex, int memberIndex, int instanceIndex,
            ExportLogger logger)
        {
            EbxAsset asset; object group, member; IList members, inline;
            if (!OpenWritableMember("level remove", assetPath, groupIndex, memberIndex,
                    out asset, out group, out member, out members, out inline)) return 1;

            if (instanceIndex < 0 || instanceIndex >= inline.Count)
            {
                Console.Error.WriteLine("level remove: instance index " + instanceIndex +
                    " is out of range (member holds " + inline.Count + ")");
                return 1;
            }

            int oldCount = inline.Count;
            if (DryRun.Active)
            {
                DryRun.Report(logger, "remove instance {0} from {1} group {2} member {3}",
                    instanceIndex, assetPath, groupIndex, memberIndex);
                return 0;
            }

            try
            {
                inline.RemoveAt(instanceIndex);
                SetNumericMember(member, "InstanceCount", oldCount - 1);
            }
            catch (Exception ex) { Console.Error.WriteLine("level remove: could not remove instance: " + Describe(ex)); return 1; }

            foreach (string name in PerInstanceArrays)
            {
                IList side = ReadList(member, name);
                if (side == null || side.Count != oldCount) continue;
                try { side.RemoveAt(instanceIndex); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("level remove: could not trim " + name + ": " + Describe(ex));
                    return 1;
                }
            }

            WriteBack(members, memberIndex, member);

            if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            // A member kept at zero instances renders nothing but stays in MemberDatas; dropping the
            // member itself would renumber every later member and is deliberately not done here.
            if (oldCount - 1 == 0)
                logger.LogWarning("level remove: {0} group {1} member {2} now has no instances; the member itself remains.",
                    assetPath, groupIndex, memberIndex);
            else
                logger.Log("level remove: {0} group {1} member {2} now holds {3} instance(s)",
                    assetPath, groupIndex, memberIndex, oldCount - 1);
            return 0;
        }

        // level add <asset> --group G --member N --pos x,y,z [--rot ...] [--scale ...] [--from-instance K]
        //
        // Appends one more placement of a blueprint the member already carries.  Adding a blueprint
        // the group does not yet hold is a different, larger operation -- it needs a new member, a
        // mesh-variation database entry and bundle work -- and is not attempted here.
        internal static int AddGroupedInstance(string assetPath, int groupIndex, int memberIndex, int fromInstance,
            string pos, string rot, string scale, ExportLogger logger)
        {
            EbxAsset asset; object group, member; IList members, inline;
            if (!OpenWritableMember("level add", assetPath, groupIndex, memberIndex,
                    out asset, out group, out member, out members, out inline)) return 1;

            if (fromInstance < 0 || fromInstance >= inline.Count)
            {
                Console.Error.WriteLine("level add: --from-instance " + fromInstance +
                    " is out of range (member holds " + inline.Count + ")");
                return 1;
            }

            LinearTransformData xform;
            try { xform = TransformBuilder.Build(pos, rot, scale); }
            catch (ArgumentException ex) { Console.Error.WriteLine("level add: " + ex.Message); return 1; }

            int oldCount = inline.Count;
            if (DryRun.Active)
            {
                DryRun.Report(logger, "add instance {0} to {1} group {2} member {3} at {4}",
                    oldCount, assetPath, groupIndex, memberIndex, pos);
                return 0;
            }

            object element;
            try
            {
                element = CloneListElement(inline, fromInstance);
                if (element == null) { Console.Error.WriteLine("level add: instance " + fromInstance + " is null"); return 1; }
                element = ApplyTransform(element, xform, true);
                inline.Add(element);
                SetNumericMember(member, "InstanceCount", oldCount + 1);
            }
            catch (Exception ex) { Console.Error.WriteLine("level add: could not append instance: " + Describe(ex)); return 1; }

            // Extend only side arrays that are exactly as long as the old instance count.  Shipped
            // levels carry deliberately short ones -- every consumer index-guards its reads -- and
            // appending to a short array would hand its value to the instance at the old tail.
            foreach (string name in PerInstanceArrays)
            {
                IList side = ReadList(member, name);
                if (side == null || side.Count != oldCount) continue;
                try { side.Add(CloneListElement(side, fromInstance)); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("level add: could not extend " + name + ": " + Describe(ex));
                    return 1;
                }
            }

            // StaticModelGroupMemberData is a value type; the InstanceCount edit lives in the box.
            WriteBack(members, memberIndex, member);

            if (!EbxWriteCommand.CallModifyEbx(assetPath, asset, logger)) return 1;
            logger.Log("level add: {0} group {1} member {2} now holds {3} instance(s); new instance index is {4}",
                assetPath, groupIndex, memberIndex, oldCount + 1, oldCount);
            return 0;
        }

        // ----- grouped static placement writes -----
        //
        // Only EBX-backed members are writable here.  A member with inline InstanceTransforms owns
        // its placements outright: measured across every shipped SWBF2 level, such members always
        // carry PhysicsPartCountPerInstance == 0 and NetworkIdCountPerInstance == 0, so they hold no
        // physics parts and no network ids.  That is what makes these edits self-contained -- no
        // part table, bounding-box array, id range or physics resource has to move.  Members backed
        // by the group's Havok resource are refused rather than half-written.

        private const uint NoRange = 0xFFFFFFFF;

        // Pads every member's InstanceRadiosityTypeOverride list of a static group to its InstanceCount
        // with <value> (e.g. RadiosityTypeOverride_Dynamic). Members whose list already covers every
        // instance are left alone. Returns null on success; <changed> counts the entries written.
        internal static string SetGroupInstanceRadiosity(string assetPath, int groupIndex, string value, ExportLogger logger, out int changed)
        {
            changed = 0;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            if (entry == null) return "asset not found: " + assetPath;
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { return "load failed: " + Describe(ex); }
            string error;
            object group = FindStaticGroup(asset.RootObject, groupIndex, out error);
            if (group == null) return error;
            IList members = ReadList(group, "MemberDatas");
            if (members == null) return "group has no MemberDatas";
            for (int i = 0; i < members.Count; i++)
            {
                object member = ResolveInternal(members[i]) ?? members[i];
                if (member == null) continue;
                int count = ConvertToInt(ReadMember(member, "InstanceCount"));
                IList side = ReadList(member, "InstanceRadiosityTypeOverride");
                if (side == null || side.Count >= count) continue;
                Type elemType = PropertyPathWriter.ListElementType(side);
                if (elemType == null) return "cannot determine the InstanceRadiosityTypeOverride element type";
                object v;
                try { v = elemType.IsEnum ? Enum.Parse(elemType, value) : Convert.ChangeType(value, elemType, CultureInfo.InvariantCulture); }
                catch (Exception ex) { return "cannot convert '" + value + "' to " + elemType.Name + ": " + ex.Message; }
                while (side.Count < count) { side.Add(v); changed++; }
                WriteBack(members, i, member);
            }
            if (changed == 0) return null;
            return EbxWriteCommand.CallModifyEbx(assetPath, asset, logger) ? null : "could not write " + assetPath;
        }

        // Per-instance side arrays that run parallel to InstanceTransforms.
        private static readonly string[] PerInstanceArrays =
        {
            "InstanceObjectVariation",
            "InstanceRenderingOverrides",
            "InstanceRadiosityTypeOverride",
            "InstanceTerrainShaderNodesEnable"
        };

        // HealthStateEntityManagerId is a plain 0-based index into the health-state manager's entity
        // table (game+0x549B202 does mov rcx,[table + id*8]; bit 31 selects a second table at +0x178
        // rather than meaning "unset"). The ids are unique across the WORLD, not per group: Cloud
        // City's streamedlevel group numbers its 354 members 0..353 with no gaps, so a second group
        // in the same asset has to continue from there. Reusing 0 made the load fault at
        // game+0x549B22E reading an id of 0xFFFFFFFF -- the registration our member had collided with
        // never produced an entity.
        internal static long NextHealthStateId(EbxAsset asset)
        {
            long next = 0;
            IList objects = ReadList(asset.RootObject, "Objects");
            if (objects == null) return 0;
            foreach (object slot in objects)
            {
                object o = ResolveInternal(slot);
                if (o == null || o.GetType().Name != "StaticModelGroupEntityData") continue;
                IList members = ReadList(o, "MemberDatas");
                if (members == null) continue;
                foreach (object ms in members)
                {
                    object m = ResolveInternal(ms) ?? ms;
                    if (m == null) continue;
                    long id;
                    try { id = Convert.ToInt64(ReadMember(m, "HealthStateEntityManagerId"), CultureInfo.InvariantCulture); }
                    catch { continue; }
                    if (id < 0 || (id & 0x80000000L) != 0) continue;   // the bit-31 space is a different table
                    if (id >= next) next = id + 1;
                }
            }
            return next;
        }

        // True when the blueprint's entity holds a component a group member may not own.
        private static bool EntityCarriesNonPartComponents(EbxAsset blueprint)
        {
            object entity = ResolveInternal(ReadMember(blueprint.RootObject, "Object"));
            IList comps = entity == null ? null : ReadList(entity, "Components");
            if (comps == null) return false;
            foreach (object slot in comps)
            {
                object c = ResolveInternal(slot) ?? slot;
                if (c != null && c.GetType().Name != "PartComponentData") return true;
            }
            return false;
        }

        // Clones <blueprintPath> to a sibling holding only PartComponentData, once per blueprint, and
        // returns its path. Returns null on failure.
        internal static string EnsureGroupMemberBlueprint(string blueprintPath, ExportLogger logger)
        {
            string dest = blueprintPath + "_GroupMember";
            if (App.AssetManager.GetEbxEntry(dest) != null) return dest;
            var formatter = new OutputFormatter(json: false, output: TextWriter.Null);
            // The SDK's binary EBX round-trip intermittently throws "Index was out of range" on a
            // different blueprint every run (EbxWriterV2 hash-code collision). It is not sticky:
            // retrying the same clone in the same process usually succeeds, and a whole 4,500-prop
            // layer import aborts on the first failure otherwise. Retry a few times before giving up.
            int cloneAttempts = 0;
            while (EbxWriteCommand.ExecuteClone(new[] { blueprintPath, "--as", dest }, logger, formatter) != 0)
            {
                if (App.AssetManager.GetEbxEntry(dest) != null) break;   // a partial clone landed; use it
                if (++cloneAttempts >= 4)
                {
                    Console.Error.WriteLine("level add: could not clone " + blueprintPath + " for group-member use after " + cloneAttempts + " attempts");
                    return null;
                }
                logger.LogWarning("level add: clone of {0} failed, retry {1}", blueprintPath, cloneAttempts);
            }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(dest);
            if (entry == null) { Console.Error.WriteLine("level add: clone did not create " + dest); return null; }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level add: could not load " + dest + ": " + Describe(ex)); return null; }
            object entity = ResolveInternal(ReadMember(asset.RootObject, "Object"));
            IList comps = entity == null ? null : ReadList(entity, "Components");
            if (comps == null) { Console.Error.WriteLine("level add: " + dest + " has no entity components list"); return null; }
            for (int i = comps.Count - 1; i >= 0; i--)
            {
                object c = ResolveInternal(comps[i]) ?? comps[i];
                if (c != null && c.GetType().Name != "PartComponentData") comps.RemoveAt(i);
            }
            // ClientIndex/ServerIndex are slots in the entity's runtime component arrays.
            for (int i = 0; i < comps.Count; i++)
            {
                object c = ResolveInternal(comps[i]);
                if (c == null) continue;
                try { SetNumericMember(c, "ClientIndex", i); } catch { }
                try { SetNumericMember(c, "ServerIndex", i); } catch { }
            }
            try { SetNumericMember(entity, "ClientRuntimeComponentCount", comps.Count); } catch { }
            try { SetNumericMember(entity, "ServerRuntimeComponentCount", comps.Count); } catch { }
            if (!EbxWriteCommand.CallModifyEbx(dest, asset, logger)) return null;
            logger.Log("level add: made {0} for group-member use ({1} component(s) kept)", dest, comps.Count);
            return dest;
        }

        internal static object FindStaticGroup(object root, int groupIndex, out string error)
        {
            error = null;
            IList objects = ReadList(root, "Objects");
            if (objects == null) { error = "asset root exposes no Objects list"; return null; }
            int seen = 0;
            foreach (object value in objects)
            {
                object obj = ResolveInternal(value);
                if (obj == null || obj.GetType().Name != "StaticModelGroupEntityData") continue;
                if (seen == groupIndex) return obj;
                seen++;
            }
            error = seen == 0
                ? "asset holds no StaticModelGroupEntityData"
                : "group index " + groupIndex + " is out of range (asset holds " + seen + ")";
            return null;
        }

        // Returns the boxed member and the list it came from; StaticModelGroupMemberData is a value
        // type, so scalar edits must be written back into MemberDatas by index.
        internal static object FindMember(object group, int memberIndex, out IList members, out string error)
        {
            error = null;
            members = ReadList(group, "MemberDatas");
            if (members == null) { error = "group exposes no MemberDatas list"; return null; }
            if (memberIndex < 0 || memberIndex >= members.Count)
            {
                error = "member index " + memberIndex + " is out of range (group holds " + members.Count + ")";
                return null;
            }
            object member = ResolveInternal(members[memberIndex]) ?? members[memberIndex];
            if (member == null) error = "member " + memberIndex + " is null";
            return member;
        }

        // Refuses anything whose placements this command cannot own end to end.
        private static bool IsMemberWritable(object member, object group, int memberIndex, out IList inline, out string reason)
        {
            reason = null;
            inline = ReadList(member, "InstanceTransforms");
            int count = Math.Max(0, ConvertToInt(ReadMember(member, "InstanceCount")));

            if (inline == null || inline.Count == 0)
            {
                string res = FindPhysicsResourcePath(group);
                reason = "member " + memberIndex + " is Havok-backed: its " + count +
                    " transform(s) live in " + (res ?? "the group's physics resource") +
                    ", which this command does not write. Only members with inline InstanceTransforms are editable.";
                return false;
            }
            if (inline.Count != count)
            {
                reason = "member " + memberIndex + " is inconsistent: InstanceCount is " + count +
                    " but InstanceTransforms holds " + inline.Count +
                    " entries. Run 'level validate' on this asset first.";
                return false;
            }

            // Defensive: shipped data never pairs inline transforms with parts or ids, but if some
            // asset does, the part/id tables would need rebalancing and this edit would corrupt it.
            int ppc = ConvertToInt(ReadMember(member, "PhysicsPartCountPerInstance"));
            int nic = ConvertToInt(ReadMember(member, "NetworkIdCountPerInstance"));
            if (ppc != 0 || nic != 0)
            {
                reason = "member " + memberIndex + " has inline transforms but also " + ppc +
                    " physics part(s) and " + nic + " network id(s) per instance; editing it would " +
                    "require rebalancing the group's part and id tables.";
                return false;
            }
            return true;
        }

        // Writes a transform into a LinearTransform-shaped object, returning the (possibly re-boxed)
        // value so a value-type element can be written back into its list.
        private static object ApplyTransform(object transform, LinearTransformData xform, bool includeRotation)
        {
            string[] axes = { "right", "up", "forward", "trans" };
            float[][] vecs = { xform.Right, xform.Up, xform.Forward, xform.Trans };
            string[] comps = { "x", "y", "z" };
            for (int a = includeRotation ? 0 : 3; a < 4; a++)
            {
                for (int c = 0; c < 3; c++)
                {
                    string value = vecs[a][c].ToString("G", CultureInfo.InvariantCulture);
                    try { PropertyPathWriter.SetValue(transform, axes[a] + "." + comps[c], value); }
                    catch (InvalidOperationException)
                    {
                        string altAxis = char.ToUpper(axes[a][0]) + axes[a].Substring(1);
                        PropertyPathWriter.SetValue(transform, altAxis + "." + comps[c].ToUpper(), value);
                    }
                }
            }
            return transform;
        }

        // Assigns a whole number to a member field whose exact numeric type varies (uint/int/ushort).
        internal static void SetNumericMember(object obj, string name, long value)
        {
            Type t = obj.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            FieldInfo f = p == null ? t.GetField(name, BindingFlags.Public | BindingFlags.Instance) : null;
            Type target = p != null ? p.PropertyType : (f != null ? f.FieldType : null);
            if (target == null) throw new InvalidOperationException("member '" + name + "' not found on " + t.Name);
            object converted = Convert.ChangeType(value, Nullable.GetUnderlyingType(target) ?? target, CultureInfo.InvariantCulture);
            if (p != null) p.SetValue(obj, converted); else f.SetValue(obj, converted);
        }

        // A value-type element mutated through its box has to be stored back over the original.
        private static void WriteBack(IList list, int index, object element)
        {
            if (element != null && element.GetType().IsValueType) list[index] = element;
        }

        private static object CloneListElement(IList list, int index)
        {
            return DeepCopyValue(list[index], 0);
        }

        // EBX structs surface as CLASSES in the generated SDK, so a field-by-field copy of a
        // LinearTransform still shares its right/up/forward/trans Vec3 children with the original.
        // ApplyTransform then wrote through those shared children and rewrote every instance that
        // had already been cloned: a member given 82 placements ended up with 82 copies of the last
        // one. Copy the whole subtree instead. Value types are independent once boxed (PointerRef
        // and the enums in the side arrays are structs, and a boxed PointerRef still names the same
        // target, which is what an instance clone wants), and strings are immutable.
        private static object DeepCopyValue(object source, int depth)
        {
            if (source == null) return null;
            Type t = source.GetType();
            if (t.IsValueType || t == typeof(string) || depth > 16) return source;

            IList sourceList = source as IList;
            if (sourceList != null)
            {
                IList listCopy = (IList)Activator.CreateInstance(t);
                for (int i = 0; i < sourceList.Count; i++) listCopy.Add(DeepCopyValue(sourceList[i], depth + 1));
                return listCopy;
            }

            object copy = Activator.CreateInstance(t);
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                f.SetValue(copy, DeepCopyValue(f.GetValue(source), depth + 1));
            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length != 0) continue;
                object value = pi.GetValue(source);
                if (pi.CanWrite) { pi.SetValue(copy, DeepCopyValue(value, depth + 1)); continue; }
                // Get-only list properties are constructed by the SDK type itself; fill the clone's
                // own list rather than dropping the contents on the floor.
                IList src = value as IList;
                IList dst = pi.GetValue(copy) as IList;
                if (src == null || dst == null) continue;
                dst.Clear();
                for (int i = 0; i < src.Count; i++) dst.Add(DeepCopyValue(src[i], depth + 1));
            }
            return copy;
        }

        // ----- helpers -----

        internal class EntityRow
        {
            public string  Blueprint;
            public float[] Right;
            public float[] Up;
            public float[] Forward;
            public float[] Trans;
            public double? Distance;
            public int? GroupIndex;
            public int? MemberIndex;
            public int? InstanceIndex;
            public string TransformSource;
        }

        private class LevelNode
        {
            public string Path;
            public string Type;
            public int Objects;
            public int StaticGroups;
            public int Members;
            public int Placements;
            public int EbxPlacements;
            public int HavokPlacements;
            public int MissingPlacements;
            public bool Repeated;
            public string Error;
            /// <summary>The next HealthStateEntityManagerId the asset's builder hands out (members of all its groups, in order).</summary>
            public long NextHealthId;
            public SortedDictionary<string, int> ObjectTypes = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<LevelNode> Children = new List<LevelNode>();
        }

        private class LevelIssue
        {
            [JsonProperty("severity")] public string Severity;
            [JsonProperty("code")]     public string Code;
            [JsonProperty("asset")]    public string Asset;
            [JsonProperty("group")]    public int? Group;
            [JsonProperty("member")]   public int? Member;
            [JsonProperty("detail")]   public string Detail;
        }

        private class LevelTotals
        {
            public int Assets;
            public int Objects;
            public int StaticGroups;
            public int Members;
            public int Placements;
            public int EbxPlacements;
            public int HavokPlacements;
            public int MissingPlacements;
            public SortedDictionary<string, int> ObjectTypes = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<string> Errors = new List<string>();
        }

        // Shared traversal behind 'level tree', 'level stats' and 'level validate'.  Passing a
        // non-null issue list turns on the deeper checks -- blueprint and resource resolution and
        // Havok transform accounting -- which cost extra asset loads, so tree/stats pass null.
        private static LevelNode AnalyzeLevel(string path, HashSet<string> visited, int depth, int maxDepth,
            List<LevelIssue> issues)
        {
            LevelNode node = new LevelNode { Path = path };
            if (!visited.Add(path)) { node.Repeated = true; return node; }

            EbxAsset asset;
            try
            {
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
                if (entry == null)
                {
                    node.Error = "entry not found: " + path;
                    AddIssue(issues, "error", depth == 0 ? "level-missing" : "subworld-missing", path, null, null,
                        "referenced EBX asset does not exist");
                    return node;
                }
                asset = App.AssetManager.GetEbx(entry);
            }
            catch (Exception ex)
            {
                node.Error = Describe(ex);
                AddIssue(issues, "error", depth == 0 ? "level-unreadable" : "subworld-unreadable", path, null, null,
                    "could not load EBX: " + Describe(ex));
                return node;
            }

            try
            {
                object root = asset.RootObject;
                node.Type = root != null ? root.GetType().Name : null;
                IList objects = ReadList(root, "Objects");
                if (objects == null) return node;
                node.Objects = objects.Count;

                AnalyzeObjects(objects, node, path, visited, depth, maxDepth, issues);
            }
            catch (Exception ex)
            {
                node.Error = Describe(ex);
                AddIssue(issues, "error", "traversal-failed", path, null, null, Describe(ex));
            }
            return node;
        }

        // The Objects[] walk shared by AnalyzeLevel (an EBX asset) and AnalyzeWorldPart (an internal world part).
        private static void AnalyzeObjects(IList objects, LevelNode node, string path, HashSet<string> visited, int depth, int maxDepth,
            List<LevelIssue> issues)
        {
            int groupIndex = 0, objectIndex = -1;
            foreach (object value in objects)
            {
                objectIndex++;
                object obj = ResolveInternal(value);
                if (obj == null)
                {
                    if (!HasExternalTarget(value))
                        AddIssue(issues, "warning", "object-unresolved", path, null, null, "Objects[" + objectIndex + "]: " +
                            "an entry in Objects[] targets neither an internal nor an external object");
                    continue;
                }
                string typeName = obj.GetType().Name;
                int typeCount;
                node.ObjectTypes.TryGetValue(typeName, out typeCount);
                node.ObjectTypes[typeName] = typeCount + 1;

                if (typeName == "StaticModelGroupEntityData") AnalyzeStaticGroup(obj, node, groupIndex++, issues);
                else if ((typeName == "SpatialPrefabReferenceObjectData" || typeName == "ObjectReferenceObjectData") && issues != null)
                    ValidateEntityPhysicsParts(ReadMember(obj, "Blueprint"), path, objectIndex, issues);
                else if (typeName == "SubWorldReferenceObjectData")
                {
                    DescendContainer(node, obj, "BundleName", "subworld", "sub-world", path, visited, depth, maxDepth, issues);
                }
                else if (typeName == "LayerReferenceObjectData" || typeName == "WorldPartReferenceObjectData")
                {
                    // A level is organised into layers as much as sub-worlds -- StarKiller_01
                    // holds 98 layer references against 29 sub-world references -- and each one
                    // names a LayerData asset with its own objects. Stopping here would leave
                    // most of a level unvisited.
                    // SWBF1: the Blueprint is an internal WorldPartData of this partition -- analyse it in place.
                    object part = ResolveInternal(ReadMember(obj, "Blueprint"));
                    if (part != null) node.Children.Add(AnalyzeWorldPart(part, path, visited, depth, maxDepth, issues));
                    else DescendContainer(node, obj, "Blueprint", "layer", "layer", path, visited, depth, maxDepth, issues);
                }
            }
        }

        // Frostbite 2015 (SWBF1) levels keep their layers as WorldPartReferenceObjectData pointing at an INTERNAL
        // WorldPartData of the same partition (Volcanic_01/StreamedLevel holds 22), not a LayerData asset of its own.
        private static LevelNode AnalyzeWorldPart(object part, string parentPath, HashSet<string> visited, int depth, int maxDepth,
            List<LevelIssue> issues)
        {
            string name = Convert.ToString(ReadMember(part, "Name"), CultureInfo.InvariantCulture);
            string path = string.IsNullOrEmpty(name) ? parentPath + "#" + part.GetType().Name : name;
            LevelNode node = new LevelNode { Path = path, Type = part.GetType().Name };
            if (!visited.Add("internal:" + path)) { node.Repeated = true; return node; }
            try
            {
                IList objects = ReadList(part, "Objects");
                if (objects == null) return node;
                node.Objects = objects.Count;
                AnalyzeObjects(objects, node, path, visited, depth + 1, maxDepth, issues);
            }
            catch (Exception ex)
            {
                node.Error = Describe(ex);
                AddIssue(issues, "error", "traversal-failed", path, null, null, Describe(ex));
            }
            return node;
        }

        // Follows one container reference to the asset it names and analyses that too. Sub-worlds
        // name their child by bundle string; layers point at theirs with a blueprint reference.
        private static void DescendContainer(LevelNode node, object obj, string member, string code, string kind,
            string path, HashSet<string> visited, int depth, int maxDepth, List<LevelIssue> issues)
        {
            string childPath = member == "BundleName"
                ? Convert.ToString(ReadMember(obj, member), CultureInfo.InvariantCulture)
                : ResolveBlueprintPath(ReadMember(obj, member));

            // ResolveBlueprintPath hands back the raw GUID when the asset is absent from the catalog.
            Guid dangling;
            if (!string.IsNullOrEmpty(childPath) && Guid.TryParse(childPath, out dangling))
            {
                AddIssue(issues, "error", code + "-missing", path, null, null,
                    kind + " reference points at unknown asset " + childPath);
                return;
            }
            if (string.IsNullOrEmpty(childPath))
            {
                AddIssue(issues, "error", code + "-unnamed", path, null, null,
                    "a " + kind + " reference names no asset");
                return;
            }
            if (depth >= maxDepth)
            {
                AddIssue(issues, "warning", code + "-depth-truncated", path, null, null,
                    kind + " '" + childPath + "' not visited: --max-depth " + maxDepth + " reached");
                return;
            }
            node.Children.Add(AnalyzeLevel(childPath, visited, depth + 1, maxDepth, issues));
        }

        // A member's transform source is chosen per member: any inline InstanceTransforms means the
        // whole member is EBX-backed and consumes no Havok transforms.  Only members without inline
        // transforms draw from the group's physics resource, in member order.
        private static void AnalyzeStaticGroup(object group, LevelNode node, int groupIndex, List<LevelIssue> issues)
        {
            node.StaticGroups++;
            IList members = ReadList(group, "MemberDatas");
            if (members == null)
            {
                AddIssue(issues, "warning", "group-no-members", node.Path, groupIndex, null,
                    "StaticModelGroupEntityData exposes no MemberDatas list");
                return;
            }
            node.Members += members.Count;

            List<string> resPaths = CollectPhysicsResourcePaths(group);
            string resPath = resPaths.Count > 0 ? resPaths[0] : null;
            bool hasPhysics = resPath != null;

            long partCursor = 0, netCursor = 0, totalParts = 0, totalNetIds = 0;
            int havokNeeded = CountHavokPlacements(members);
            if (issues != null && hasPhysics)
            {
                if (resPaths.Count > 1)
                    AddIssue(issues, "warning", "group-multiple-physics", node.Path, groupIndex, null,
                        "group names " + resPaths.Count + " physics resources; only '" + resPath + "' is read");

                if (App.AssetManager.GetResEntry(resPath) == null)
                {
                    AddIssue(issues, "error", "physics-res-missing", node.Path, groupIndex, null,
                        "physics resource '" + resPath + "' does not exist");
                }
                else if (havokNeeded > 0)
                {
                    string readError;
                    List<List<HavokTransformReader.Transform>> arrays;
                    List<HavokTransformReader.CompoundTree> treeNodeCounts;
                    if (!TryLoadPhysicsArrays(resPath, out arrays, out treeNodeCounts, out readError))
                    {
                        AddIssue(issues, "error", "havok-unreadable", node.Path, groupIndex, null,
                            "physics resource '" + resPath + "' could not be decoded: " + readError);
                    }
                    else
                    {
                        if (HavokTransformReader.SelectPlacements(arrays, havokNeeded) == null)
                            AddIssue(issues, "error", "transform-count-mismatch", node.Path, groupIndex, null,
                                "members need " + havokNeeded + " Havok transform(s) but '" + resPath +
                                "' offers instance arrays of " + HavokTransformReader.DescribeSizes(arrays));

                        // Without its AABB tree the runtime cannot reach a compound's geometry, so a
                        // missing or wrong-sized one means collision with holes in it.
                        string treeDetail;
                        if (!HavokTransformReader.TreesMatchInstanceArrays(arrays, treeNodeCounts, out treeDetail))
                            AddIssue(issues, "error", "havok-tree-mismatch", node.Path, groupIndex, null,
                                "in '" + resPath + "': " + treeDetail);
                        ValidatePhysicsAssetTables(group, resPath, node.Path, groupIndex, issues);
                    }
                }
            }

            for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
            {
                object member = ResolveInternal(members[memberIndex]) ?? members[memberIndex];
                if (member == null)
                {
                    AddIssue(issues, "warning", "member-null", node.Path, groupIndex, memberIndex,
                        "MemberDatas entry is null");
                    continue;
                }

                int rawCount = ConvertToInt(ReadMember(member, "InstanceCount"));
                if (rawCount < 0)
                    AddIssue(issues, "error", "instance-count-negative", node.Path, groupIndex, memberIndex,
                        "InstanceCount is " + rawCount);
                int count = Math.Max(0, rawCount);
                node.Placements += count;

                IList inline = ReadList(member, "InstanceTransforms");
                bool useInline = inline != null && inline.Count > 0;
                if (useInline)
                {
                    int usable = Math.Min(count, inline.Count);
                    node.EbxPlacements += usable;
                    node.MissingPlacements += count - usable;
                    if (inline.Count < count)
                        AddIssue(issues, "error", "placement-array-short", node.Path, groupIndex, memberIndex,
                            "InstanceCount is " + count + " but InstanceTransforms holds " + inline.Count + " entries");
                    else if (inline.Count > count)
                        AddIssue(issues, "warning", "placement-array-long", node.Path, groupIndex, memberIndex,
                            "InstanceTransforms holds " + inline.Count + " entries for InstanceCount " + count);
                }
                else if (hasPhysics)
                {
                    node.HavokPlacements += count;
                }
                else
                {
                    node.MissingPlacements += count;
                    if (count > 0)
                        AddIssue(issues, "error", "placement-unresolved", node.Path, groupIndex, memberIndex,
                            count + " instance(s) have neither inline transforms nor a physics resource");
                }

                if (issues != null)
                {
                    ValidateMemberReferences(member, node.Path, groupIndex, memberIndex, count, issues);
                    ValidateMemberTypeTarget(member, node.Path, groupIndex, memberIndex, issues);
                    ValidateHealthStateId(member, node, groupIndex, memberIndex, issues);
                    if (!useInline && count > 0)
                        ValidateMemberPhysicsParts(member, node.Path, groupIndex, memberIndex, issues);
                    ValidateMemberRange(member, "PhysicsPartRange", "PhysicsPartCountPerInstance", count,
                        node.Path, groupIndex, memberIndex, ref partCursor, ref totalParts, issues);
                    ValidateMemberRange(member, "NetworkIdRange", "NetworkIdCountPerInstance", count,
                        node.Path, groupIndex, memberIndex, ref netCursor, ref totalNetIds, issues);
                }
            }

            if (issues != null)
            {
                // PartBoundingBoxes is indexed by physics part, so its length is the group's part total.
                IList boxes = ReadList(group, "PartBoundingBoxes");
                if (boxes != null && boxes.Count != totalParts)
                    AddIssue(issues, "error", "partbbox-count-mismatch", node.Path, groupIndex, null,
                        "members declare " + totalParts + " physics part(s) but PartBoundingBoxes holds " + boxes.Count);

                object declared = ReadMember(group, "NetworkIdCount");
                if (declared != null && ConvertToInt(declared) != totalNetIds)
                    AddIssue(issues, "error", "networkid-count-mismatch", node.Path, groupIndex, null,
                        "members declare " + totalNetIds + " network id(s) but NetworkIdCount is " + ConvertToInt(declared));
            }
        }

        // PhysicsPartRange and NetworkIdRange are inclusive {First,Last} spans that tile the group in
        // member order, each covering InstanceCount * <per-instance count> entries.  A member that
        // owns none of them carries the 0xFFFFFFFF sentinel in both fields instead.  level add and
        // level move rely on this holding, so it is checked rather than assumed.
        private static void ValidateMemberRange(object member, string rangeName, string perInstanceName,
            int instanceCount, string assetPath, int groupIndex, int memberIndex,
            ref long cursor, ref long total, List<LevelIssue> issues)
        {
            object range = ReadMember(member, rangeName);
            if (range == null) return;
            object firstRaw = ReadMember(range, "First");
            object lastRaw = ReadMember(range, "Last");
            if (firstRaw == null || lastRaw == null) return;

            long first = Convert.ToInt64(firstRaw, CultureInfo.InvariantCulture);
            long last = Convert.ToInt64(lastRaw, CultureInfo.InvariantCulture);
            int perInstance = ConvertToInt(ReadMember(member, perInstanceName));
            bool sentinel = first == NoRange && last == NoRange;

            if (perInstance <= 0)
            {
                if (!sentinel)
                    AddIssue(issues, "error", "range-sentinel-mismatch", assetPath, groupIndex, memberIndex,
                        rangeName + " is {" + first + "," + last + "} but " + perInstanceName + " is " + perInstance +
                        ", so the range should be unset");
                return;
            }
            if (sentinel)
            {
                AddIssue(issues, "error", "range-sentinel-mismatch", assetPath, groupIndex, memberIndex,
                    rangeName + " is unset but " + perInstanceName + " is " + perInstance);
                return;
            }

            long width = (long)instanceCount * perInstance;
            if (first != cursor)
                AddIssue(issues, "error", "range-not-contiguous", assetPath, groupIndex, memberIndex,
                    rangeName + " starts at " + first + "; the preceding members end at " + cursor);
            if (last != first + width - 1)
                AddIssue(issues, "error", "range-width-mismatch", assetPath, groupIndex, memberIndex,
                    rangeName + " spans {" + first + "," + last + "} but " + instanceCount + " instance(s) x " +
                    perInstance + " should span " + width);

            cursor = last + 1;
            total += width;
        }

        // Instances that must come from the group's physics resource: a member with any inline
        // InstanceTransforms is EBX-backed and consumes none, so every other member contributes
        // its whole InstanceCount.  This total is what identifies the group's array inside a
        // resource that also carries per-prop compounds and a duplicate collision slot.
        internal static int CountHavokPlacements(IList members)
        {
            int total = 0;
            foreach (object value in members)
            {
                object member = ResolveInternal(value) ?? value;
                if (member == null) continue;
                IList inline = ReadList(member, "InstanceTransforms");
                if (inline != null && inline.Count > 0) continue;
                total += Math.Max(0, ConvertToInt(ReadMember(member, "InstanceCount")));
            }
            return total;
        }

        // MemberType must name the entity object inside the blueprint (a GamePhysicsEntityData), not
        // the blueprint's root: the client casts it to an entity and reads its components, so a
        // root reference crashes the level on load. Per-asset caches keep validate --all affordable.
        private static readonly Dictionary<Guid, Dictionary<Guid, Type>> BlueprintObjectTypes = new Dictionary<Guid, Dictionary<Guid, Type>>();

        private static Type ResolveReferencedObjectType(object pointerRef, out bool blueprintReadable)
        {
            blueprintReadable = false;
            if (pointerRef == null || pointerRef.GetType().Name != "PointerRef") return null;
            object external = ReadMember(pointerRef, "External");
            if (external == null) return null;
            object fileGuidObj = ReadMember(external, "FileGuid"), classGuidObj = ReadMember(external, "ClassGuid");
            if (!(fileGuidObj is Guid) || !(classGuidObj is Guid)) return null;
            Guid fileGuid = (Guid)fileGuidObj, classGuid = (Guid)classGuidObj;
            Dictionary<Guid, Type> map;
            if (!BlueprintObjectTypes.TryGetValue(fileGuid, out map))
            {
                map = null;
                try
                {
                    MethodInfo getByGuid = App.AssetManager.GetType().GetMethod("GetEbxEntry", new[] { typeof(Guid) });
                    var entry = getByGuid != null ? (EbxAssetEntry)getByGuid.Invoke(App.AssetManager, new object[] { fileGuid }) : null;
                    if (entry != null)
                    {
                        EbxAsset bp = App.AssetManager.GetEbx(entry);
                        map = new Dictionary<Guid, Type>();
                        foreach (object obj in bp.Objects)
                        {
                            if (obj == null) continue;
                            try { dynamic dyn = obj; map[(Guid)dyn.GetInstanceGuid().ExportedGuid] = obj.GetType(); }
                            catch { }
                        }
                    }
                }
                catch { map = null; }
                BlueprintObjectTypes[fileGuid] = map;
            }
            if (map == null) return null;
            blueprintReadable = true;
            Type type;
            return map.TryGetValue(classGuid, out type) ? type : null;
        }

        private static void ValidateMemberTypeTarget(object member, string assetPath, int groupIndex, int memberIndex, List<LevelIssue> issues)
        {
            bool readable;
            object memberTypeRef = ReadMember(member, "MemberType");
            object externalRef = memberTypeRef != null ? ReadMember(memberTypeRef, "External") : null;
            object classGuidObj = externalRef != null ? ReadMember(externalRef, "ClassGuid") : null;
            if (classGuidObj is Guid && (Guid)classGuidObj == Guid.Empty)
            {
                AddIssue(issues, "error", "member-type-not-exported", assetPath, groupIndex, memberIndex,
                    "MemberType carries an empty object GUID: the blueprint's entity is not an exported instance, so the reference cannot resolve");
                return;
            }
            Type type = ResolveReferencedObjectType(memberTypeRef, out readable);
            if (!readable) return;
            if (type == null)
                AddIssue(issues, "error", "member-type-object-missing", assetPath, groupIndex, memberIndex,
                    "MemberType names an object GUID that does not exist in the blueprint");
            else if (!InheritsFrom(type, "EntityData"))
                AddIssue(issues, "error", "member-type-not-entity", assetPath, groupIndex, memberIndex,
                    "MemberType references a " + type.Name + "; it must reference the entity inside the blueprint (ObjectBlueprint.Object), or the level crashes on load");
        }

        // HealthStateEntityManagerId indexes the sub-level's health-state entity table. The builder
        // (NextHealthStateId) numbers the members of ALL static groups in an asset consecutively from 0, so a
        // second group continues where the first ended: restarting at 0 collides and faults at load
        // (game+0x549B22E, Endor 2026-09-07), and an id past the member total reads off the table's end
        // (game+0x143AB34, Hoth T6). The first group of every shipped asset starts at 0; Hoth2015_01 Content
        // (six groups, ids 0..311) loads.
        private static void ValidateHealthStateId(object member, LevelNode node, int groupIndex, int memberIndex, List<LevelIssue> issues)
        {
            long expected = node.NextHealthId++;
            object idRaw = ReadMember(member, "HealthStateEntityManagerId");
            if (idRaw == null) return;
            long id = Convert.ToInt64(idRaw, CultureInfo.InvariantCulture);
            if (id != expected)
                AddIssue(issues, "error", "healthstate-id-mismatch", node.Path, groupIndex, memberIndex,
                    "HealthStateEntityManagerId is " + id + " but the builder assigns " + expected +
                    " (members of all the asset's groups are numbered consecutively from 0)");
        }

        // A Havok-backed member's physics parts per instance must equal its blueprint entity's PhysicsPartInfos.
        // When an instance takes damage the server builds a ServerStaticModelPhysicsComponent for it whose per-part
        // table (+0xC0) is sized from PhysicsPartInfos, then walks the body's parts (vtable game+0x2F9BFA0 slot 71):
        // a 2-part member on a 1-part blueprint reads past the table and calls through garbage -- game+0x601CD1,
        // read from 0xFFFFFFFFFFFFFFFF, loop index 1 (Hoth2015 L2/L3, 2026-09-26: 2015 BoxMedium_02/BoxSmall_03 on
        // DICE's 1-part Box_M_02/03, and 2-part stand-ins on BoxSmall_01). Shipped Hoth_01 Content: 146 of 146
        // Havok members match, 35 of 35 EBX members carry 0. PartComponentCount above the blueprint's
        // PartComponentData count is the game+0xBC0848 crash.
        private static void ValidateMemberPhysicsParts(object member, string assetPath, int groupIndex, int memberIndex, List<LevelIssue> issues)
        {
            if (!PartRuleApplies(assetPath, ReadMember(member, "MemberType"))) return;
            int ppc = ConvertToInt(ReadMember(member, "PhysicsPartCountPerInstance"));
            int pcc = ConvertToInt(ReadMember(member, "PartComponentCount"));
            int partInfos, partComponents;
            string blueprint;
            if (!TryCountBlueprintParts(ReadMember(member, "MemberType"), out partInfos, out partComponents, out blueprint)) return;
            if (ppc > 0 && ppc != partInfos)
                AddIssue(issues, "error", "member-physics-parts-mismatch", assetPath, groupIndex, memberIndex,
                    "PhysicsPartCountPerInstance is " + ppc + " but " + blueprint + " declares " + partInfos +
                    " PhysicsPartInfo(s); damaging an instance crashes the server (game+0x601CD1). Give the member a blueprint with that many parts");
            if (pcc > partComponents)
                AddIssue(issues, "error", "member-part-components-mismatch", assetPath, groupIndex, memberIndex,
                    "PartComponentCount is " + pcc + " but " + blueprint + " has " + partComponents + " PartComponentData (game+0xBC0848)");
        }

        // A placed static-model entity walks its physics body's parts against the blueprint's PhysicsPartInfos just as a
        // group member does (game+0x601CD1, loop index 1, when damaged): HOTH_J4 crashed 47 s into a fight on ported
        // rigid blueprints that kept 2015's multi-part lists over a one-part physics component (UplinkBase_01 5/1,
        // SnowSpeeder_Crashed_01 10/1, LightGround_02 / BoxMedium_02C / BoxSmall_03C 2/1). PartBoundingBoxes are
        // indexed by physics part too (the group rule, partbbox-count-mismatch).
        private static readonly Dictionary<Guid, int[]> EntityPartCounts = new Dictionary<Guid, int[]>();

        private static void ValidateEntityPhysicsParts(object blueprintRef, string assetPath, int objectIndex, List<LevelIssue> issues)
        {
            if (!PartRuleApplies(assetPath, blueprintRef)) return;
            object external = blueprintRef != null ? ReadMember(blueprintRef, "External") : null;
            object fileGuidObj = external != null ? ReadMember(external, "FileGuid") : null;
            if (!(fileGuidObj is Guid)) return;
            int[] c;
            if (!EntityPartCounts.TryGetValue((Guid)fileGuidObj, out c))
            {
                c = null;
                try
                {
                    EbxAssetEntry entry = App.AssetManager.GetEbxEntry((Guid)fileGuidObj);
                    if (entry != null) c = LevelSyncEntityParts.Counts(App.AssetManager.GetEbx(entry));
                }
                catch { c = null; }
                EntityPartCounts[(Guid)fileGuidObj] = c;
            }
            if (c == null) return;
            // the body walks the parts its physics RESOURCE declares; every EBX list indexed by part must match it
            int n = c[0] > 0 ? c[0] : c[1];
            string bp = ResolveBlueprintPath(blueprintRef);
            if (c[1] != n || c[2] != n || c[4] != n)
                AddIssue(issues, "error", "entity-physics-parts-mismatch", assetPath, null, null, "Objects[" + objectIndex + "] " + bp +
                    ": physics resource " + (c[0] > 0 ? c[0].ToString(CultureInfo.InvariantCulture) : "?") + " part(s), component Parts " + c[1] +
                    ", PhysicsPartInfos " + c[2] + ", body PartIndices " + c[4] +
                    "; damaging it crashes the server (game+0x601CD1). `level sync-entity-parts` aligns Parts/PartIndices to the resource");
            if (c[3] != n)
                AddIssue(issues, "warning", "entity-partbbox-count-mismatch", assetPath, null, null, "Objects[" + objectIndex + "] " + bp +
                    ": PartBoundingBoxes holds " + c[3] + " for " + n + " physics part(s)");
        }

        // The part rules judge mod-authored placements only: a partition the mod added, or a blueprint it added or edited.
        // Shipped levels carry mismatches that load and play (DeathStar02_01's Computer_Screen_01 members 3/4,
        // MachineCylinder_01_Indestructible 1/2 across the MC85 rooms, ImperialShuttle_Flying 9/19 in the outro
        // cinematics): judged there, `level validate --all` reported them on unmodified data. Both crashes behind the
        // rules came from ported content, which this still covers.
        private static bool PartRuleApplies(string assetPath, object blueprintRef)
        {
            try
            {
                EbxAssetEntry level = App.AssetManager.GetEbxEntry(assetPath);
                if (level != null && level.IsAdded) return true;
                object external = blueprintRef != null ? ReadMember(blueprintRef, "External") : null;
                object fileGuidObj = external != null ? ReadMember(external, "FileGuid") : null;
                if (!(fileGuidObj is Guid)) return false;
                EbxAssetEntry blueprint = App.AssetManager.GetEbxEntry((Guid)fileGuidObj);
                return blueprint != null && (blueprint.IsAdded || blueprint.IsModified);
            }
            catch { return false; }
        }

        private static readonly Dictionary<Guid, int[]> BlueprintPartCounts = new Dictionary<Guid, int[]>();

        /// <summary>PhysicsPartInfos and PartComponentData counts of the entity a MemberType reference names.</summary>
        internal static bool TryCountBlueprintParts(object memberTypeRef, out int partInfos, out int partComponents, out string blueprint)
        {
            partInfos = partComponents = 0;
            blueprint = ResolveBlueprintPath(memberTypeRef);
            object external = memberTypeRef != null ? ReadMember(memberTypeRef, "External") : null;
            object fileGuidObj = external != null ? ReadMember(external, "FileGuid") : null;
            if (!(fileGuidObj is Guid)) return false;
            int[] counts;
            if (!BlueprintPartCounts.TryGetValue((Guid)fileGuidObj, out counts))
            {
                counts = null;
                try
                {
                    EbxAssetEntry entry = App.AssetManager.GetEbxEntry((Guid)fileGuidObj);
                    if (entry != null)
                    {
                        EbxAsset bp = App.AssetManager.GetEbx(entry);
                        object entity = ResolveInternal(ReadMember(bp.RootObject, "Object"));
                        IList infos = entity != null ? ReadList(entity, "PhysicsPartInfos") : null;
                        int parts = 0;
                        foreach (object o in bp.Objects) if (o != null && o.GetType().Name == "PartComponentData") parts++;
                        counts = new[] { infos != null ? infos.Count : 0, parts };
                    }
                }
                catch { counts = null; }
                BlueprintPartCounts[(Guid)fileGuidObj] = counts;
            }
            if (counts == null) return false;
            partInfos = counts[0]; partComponents = counts[1];
            return true;
        }

        private static void ValidateMemberReferences(object member, string assetPath, int groupIndex, int memberIndex,
            int instanceCount, List<LevelIssue> issues)
        {
            string blueprint = ResolveBlueprintPath(ReadMember(member, "MemberType"));
            if (string.IsNullOrEmpty(blueprint))
            {
                AddIssue(issues, "error", "blueprint-unresolved", assetPath, groupIndex, memberIndex,
                    "MemberType does not reference a blueprint");
            }
            else
            {
                // ResolveBlueprintPath falls back to the raw file GUID when the asset is absent from
                // the catalog, so a GUID-shaped result is exactly the dangling-reference case.
                Guid ignored;
                if (Guid.TryParse(blueprint, out ignored))
                    AddIssue(issues, "error", "blueprint-missing", assetPath, groupIndex, memberIndex,
                        "MemberType references unknown blueprint " + blueprint);
            }

            CheckParallelArray(member, "InstanceObjectVariation", assetPath, groupIndex, memberIndex, instanceCount, issues);
            CheckParallelArray(member, "InstanceRenderingOverrides", assetPath, groupIndex, memberIndex, instanceCount, issues);

            // The group draws the member's own MeshAsset (repointed members keep their MemberType); judge that mesh,
            // and the blueprint's only when the member names none.
            string memberMesh = ResolveBlueprintPath(ReadMember(member, "MeshAsset"));
            Guid unresolved;
            if (!string.IsNullOrEmpty(memberMesh) && !Guid.TryParse(memberMesh, out unresolved))
                ValidateMemberMesh(null, assetPath, groupIndex, memberIndex, issues, memberMesh);
            else
                ValidateMemberMesh(blueprint, assetPath, groupIndex, memberIndex, issues);
        }

        // A placement renders only if its mesh is (a) registered in a MeshVariationDb_Win32 and
        // (b) reachable from a bundle the level loads. Both are separate manual steps after
        // 'level add', so getting one wrong is the most common way to end up with an invisible
        // prop -- which is what this pair of rules catches.
        //
        // Neither can be judged from the sub-world alone. Shipped levels routinely place a prop in
        // one sub-world while registering it in the root's database (verified on Kamino_01, whose
        // Content and FantasyBattle placements of box_m_02_mesh resolve through the root's), and
        // the runtime merges every loaded database into one. So the check is deferred until the
        // whole tree is known, and the answer is the union across it.
        private class PendingMeshCheck
        {
            public string Asset;
            public int Group;
            public int Member;
            public string MeshPath;
            public Guid MeshGuid;
        }

        private static void ValidateMemberMesh(string blueprintPath, string assetPath, int groupIndex,
            int memberIndex, List<LevelIssue> issues, string meshPathOverride = null)
        {
            if (s_pendingMeshChecks == null) return;
            if (string.IsNullOrEmpty(blueprintPath) && string.IsNullOrEmpty(meshPathOverride)) return;
            string meshPath = meshPathOverride ?? ResolveBlueprintMeshPath(blueprintPath);
            if (string.IsNullOrEmpty(meshPath)) return;

            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshPath);
            if (meshEntry == null) return;   // a dangling mesh ref is the blueprint's problem, not ours

            s_pendingMeshChecks.Add(new PendingMeshCheck
            {
                Asset = assetPath, Group = groupIndex, Member = memberIndex,
                MeshPath = meshPath, MeshGuid = meshEntry.Guid
            });
        }

        // Called once the level tree is complete, so the databases and bundles of every sub-world
        // are known and a placement can be judged against all of them.
        private static void ResolveMeshChecks(LevelNode root, List<LevelIssue> issues)
        {
            List<PendingMeshCheck> pending = s_pendingMeshChecks;
            s_pendingMeshChecks = null;
            if (pending == null || pending.Count == 0) return;

            var nodePaths = new List<string>();
            CollectNodePaths(root, nodePaths);

            var registered = new HashSet<Guid>();
            var loadedBundles = new HashSet<int>();
            bool anyDatabase = false;
            foreach (string nodePath in nodePaths)
            {
                HashSet<Guid> meshes = GetMvdbMeshGuids(nodePath);
                if (meshes != null) { anyDatabase = true; foreach (Guid g in meshes) registered.Add(g); }
                List<int> bundles = GetBundleIds(nodePath);
                if (bundles != null) foreach (int id in bundles) loadedBundles.Add(id);
            }

            ValidateMemberVariations(nodePaths, issues, loadedBundles);

            if (!anyDatabase)
            {
                AddIssue(issues, "error", "mvdb-missing", root.Path, null, null,
                    "no MeshVariationDb_Win32 anywhere in this level tree, so its " + pending.Count +
                    " mesh placement(s) cannot render");
                return;
            }

            var reportedMvdb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reportedBundle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reportedDepot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PendingMeshCheck check in pending)
            {
                // One finding per mesh, not per placement: a level can place the same unregistered
                // prop hundreds of times, and that is one thing to fix, not hundreds.
                if (!registered.Contains(check.MeshGuid) && reportedMvdb.Add(check.MeshPath))
                    AddIssue(issues, "error", "mesh-not-in-mvdb", check.Asset, check.Group, check.Member,
                        "mesh '" + check.MeshPath + "' is registered in no MeshVariationDb_Win32 in this " +
                        "level tree; see 'mvdb add'");

                if (loadedBundles.Count > 0 && reportedBundle.Contains(check.MeshPath)) continue;
                if (loadedBundles.Count == 0) continue;   // unknown, so do not accuse
                EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(check.MeshPath);
                if (meshEntry == null) continue;
                bool shared = false;
                foreach (int id in BundleIdsOf(meshEntry)) if (loadedBundles.Contains(id)) { shared = true; break; }
                if (!shared && reportedBundle.Add(check.MeshPath))
                    AddIssue(issues, "error", "mesh-not-bundled", check.Asset, check.Group, check.Member,
                        "mesh '" + check.MeshPath + "' is in no bundle this level loads; see " +
                        "'bundle add --with-resources'");
                // A mod-added mesh's shader block depot (<mesh>_mesh/blocks) is found by name, not referenced: in no bundle the
                // level loads, the mesh is created and DRAWS NOTHING -- no crash, no log line (84 of the Hoth 2015 port's 85
                // geometry ports: the ion cannon, the rock walls on the hills, the trench floor boards, the hangar crates).
                if (shared && meshEntry.IsAdded && reportedDepot.Add(check.MeshPath))
                {
                    ResAssetEntry depot = App.AssetManager.GetResEntry(BundleCommand.DepotResName(meshEntry.Name));
                    string depotProblem = DepotBundleProblem(depot, BundleCommand.DepotResName(meshEntry.Name), loadedBundles);
                    if (depotProblem != null)
                        AddIssue(issues, "error", "mesh-depot-not-bundled", check.Asset, check.Group, check.Member,
                            "mesh '" + check.MeshPath + "': " + depotProblem);
                }
            }

            // ...and every other mod-added mesh the level registers: a static-group member repointed to a new MeshAsset keeps its
            // shipped blueprint, so the placement walk above judges the blueprint's mesh, never the one that draws.
            if (loadedBundles.Count > 0)
                foreach (Guid g in registered)
                {
                    EbxAssetEntry me = App.AssetManager.GetEbxEntry(g);
                    if (me == null || !me.IsAdded || !BundleCommand.IsMeshType(me.Type) || !reportedDepot.Add(me.Name)) continue;
                    bool inLevel = false;
                    foreach (int id in BundleIdsOf(me)) if (loadedBundles.Contains(id)) { inLevel = true; break; }
                    if (!inLevel) continue;   // not loaded here at all: mesh-not-bundled's business when something places it
                    string depotName = BundleCommand.DepotResName(me.Name);
                    string depotProblem = DepotBundleProblem(App.AssetManager.GetResEntry(depotName), depotName, loadedBundles);
                    if (depotProblem != null)
                        AddIssue(issues, "error", "mesh-depot-not-bundled", me.Name, null, null, "mesh '" + me.Name + "': " + depotProblem);
                }

            // Textures a ported (mod-added) mesh's shader block depot binds by instance guid: the geometry port copies each
            // material's parameter holder from a donor section, so a parameter the source material does not set keeps the
            // donor's texture -- in no bundle of this level, it crashes the load at game+0x1522B97 (MISS_M6).
            if (loadedBundles.Count > 0)
            {
                var meshes = new List<EbxAssetEntry>();
                var seenMesh = new HashSet<Guid>();
                // every mesh the level registers or places, shipped ones included: a shipped mesh's depot binds textures by
                // instance guid too, and a shipped prop placed outside its home level needs them bundled here
                foreach (Guid g in registered) { EbxAssetEntry me = App.AssetManager.GetEbxEntry(g); if (me != null && seenMesh.Add(me.Guid)) meshes.Add(me); }
                foreach (PendingMeshCheck check in pending) { EbxAssetEntry me = App.AssetManager.GetEbxEntry(check.MeshPath); if (me != null && seenMesh.Add(me.Guid)) meshes.Add(me); }
                foreach (EbxAssetEntry me in meshes)
                {
                    // ported (mod-added) meshes only: shipped meshes' depot textures also resolve through the shared
                    // bundles and superbundles the level description mounts, which this bundle view does not include
                    // (checking them flagged 90 shipped character / FX / prop meshes of a level that loads)
                    if (!me.IsAdded) continue;
                    int depots;
                    var missing = new List<string>();
                    foreach (EbxAssetEntry t in DepotTextures(me, out depots))
                    {
                        bool ok = false;
                        foreach (int id in BundleIdsOf(t)) if (loadedBundles.Contains(id)) { ok = true; break; }
                        if (!ok) missing.Add(t.Name);
                    }
                    if (missing.Count > 0)
                        AddIssue(issues, "error", "mesh-depot-texture-not-bundled", root.Path, null, null,
                            "mesh '" + me.Name + "': its shader block depot binds " + missing.Count + " texture(s) in no bundle this level loads (" +
                            string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? ", ..." : "") + "); the load crashes at game+0x1522B97");
                }
            }

            // A rigid ported mesh drawn through a composite donor's permutation: the section layout carries per-part
            // BoneIndices and the permutation places each vertex by a part matrix a static-group rigid mesh never supplies, so
            // the prop is INVISIBLE at every distance while its collision stays (HOTH_N4: ContainerHuge / BoxMedium_02 /
            // BoxSmall_03 / BoxMedium_01_B / LightGroundLit_03 / ComputerHangar_01 in the hangars). Re-port it (port regeo).
            {
                var seenRigid = new HashSet<Guid>();
                var check = new List<EbxAssetEntry>();
                foreach (Guid g in registered) { EbxAssetEntry me = App.AssetManager.GetEbxEntry(g); if (me != null && me.IsAdded && seenRigid.Add(me.Guid)) check.Add(me); }
                foreach (PendingMeshCheck pc in pending) { EbxAssetEntry me = App.AssetManager.GetEbxEntry(pc.MeshPath); if (me != null && me.IsAdded && seenRigid.Add(me.Guid)) check.Add(me); }
                foreach (EbxAssetEntry me in check)
                {
                    // a tessellated section without the tessellation-type enum draws nothing within its max distance
                    int noType, patches;
                    if (FrostyCLI.Adapters.Mesh.MeshGeoPort.PatchSectionsWithoutTessType(me, out noType, out patches) && noType > 0)
                        AddIssue(issues, "error", "mesh-patch-without-tess-type", me.Name, null, null,
                            "mesh '" + me.Name + "': " + noType + " of " + patches + " tessellated section(s) have no tessellation-type enum (0x5943AD36) in " +
                            "their holder -- drawn plainly far away, NOTHING within TessellationMaxDistance; re-port it with a CLI that keeps holder fields");
                    // a lightmapped mesh whose drawn sections carry no lightmap uvs draws nothing once charted
                    int noUv, opaque;
                    if (FrostyCLI.Adapters.Mesh.MeshGeoPort.StaticWithoutLightmapUvs(me, out noUv, out opaque) && opaque > 0 && noUv == opaque)
                        AddIssue(issues, "error", "mesh-static-without-lightmap-uv", me.Name, null, null,
                            "mesh '" + me.Name + "': EnlightenType_Static but none of its " + opaque + " opaque section(s) has RadiosityTexCoord -- " +
                            "invisible in game (collision intact); make it EnlightenType_Dynamic or re-port it (port import/regeo do this now)");
                    int parted, total;
                    if (!RigidPartLayout(me, out parted, out total) || parted == 0) continue;
                    AddIssue(issues, "error", "mesh-rigid-part-layout", me.Name, null, null,
                        "rigid mesh '" + me.Name + "': " + parted + " of " + total + " section(s) are laid out with part indices (BoneIndices) " +
                        "from a composite donor -- drawn by a part matrix nothing supplies, the prop is invisible (its collision is not); re-port it");
                }
            }

            // The occluder mesh: enabled without a resource, or a resource this level does not load, faults the moment an
            // entity or member using the mesh is created (game+0x6E6ED6A; MISS_M4c's LightGround_01 port, MISS_D1's SP door).
            var reportedOccluder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PendingMeshCheck check in pending)
            {
                if (!reportedOccluder.Add(check.MeshPath)) continue;
                string problem = OccluderProblem(check.MeshPath, loadedBundles);
                if (problem != null)
                    AddIssue(issues, "error", "mesh-occluder", check.Asset, check.Group, check.Member,
                        "mesh '" + check.MeshPath + "': " + problem + " (crashes on creation, game+0x6E6ED6A)");
            }
        }

        // For a RIGID mesh: how many sections (all LODs) carry BoneIndices in their first geometry declaration. False when the
        // mesh is not rigid or cannot be read.
        internal static bool RigidPartLayout(EbxAssetEntry mesh, out int parted, out int total)
        {
            parted = 0; total = 0;
            try
            {
                EbxAsset a = App.AssetManager.GetEbx(mesh);
                ulong rid = (ulong)((dynamic)a.RootObject).MeshSetResource;
                if (rid == 0) return false;
                var ms = App.AssetManager.GetResAs<MeshSetPlugin.Resources.MeshSet>(App.AssetManager.GetResEntry(rid));
                if (ms == null || !ms.Type.ToString().EndsWith("Rigid", StringComparison.OrdinalIgnoreCase)) return false;
                foreach (var lod in ms.Lods)
                    foreach (var sec in lod.Sections)
                    {
                        total++;
                        if (sec.GeometryDeclDesc == null || sec.GeometryDeclDesc.Length == 0 || sec.GeometryDeclDesc[0].Elements == null) continue;
                        for (int i = 0; i < sec.GeometryDeclDesc[0].ElementCount && i < sec.GeometryDeclDesc[0].Elements.Length; i++)
                            if (sec.GeometryDeclDesc[0].Elements[i].Usage == FrostySdk.VertexElementUsage.BoneIndices) { parted++; break; }
                    }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Null when the mesh's occluder settings can load in a level that loads <paramref name="loadedBundles"/>
        /// (empty = unknown, not judged), else what is wrong.</summary>
        internal static string OccluderProblem(string meshPath, ICollection<int> loadedBundles)
        {
            EbxAssetEntry meshEntry = App.AssetManager.GetEbxEntry(meshPath);
            if (meshEntry == null) return null;
            bool enable; ulong rid;
            try
            {
                dynamic root = App.AssetManager.GetEbx(meshEntry).RootObject;
                enable = (bool)root.OccluderMeshEnable;
                rid = (ulong)root.OccluderMeshResource;
            }
            catch { return null; }   // not a mesh type with an occluder
            if (rid == 0) return enable ? "OccluderMeshEnable is set but OccluderMeshResource is empty" : null;
            ResAssetEntry re = App.AssetManager.GetResEntry(rid);
            if (re == null) return "its occluder resource 0x" + rid.ToString("X16") + " is not in the catalog";
            if (loadedBundles == null || loadedBundles.Count == 0) return null;
            foreach (int id in re.Bundles) if (loadedBundles.Contains(id)) return null;
            foreach (int id in re.AddedBundles) if (loadedBundles.Contains(id)) return null;
            return "its occluder resource " + re.Name + " is in no bundle this level loads ('bundle add --with-resources' on the mesh ships it)";
        }

        private static void CollectNodePaths(LevelNode node, List<string> paths)
        {
            if (node == null) return;
            if (!string.IsNullOrEmpty(node.Path) && !paths.Contains(node.Path)) paths.Add(node.Path);
            foreach (LevelNode child in node.Children) CollectNodePaths(child, paths);
        }

        internal static string MvdbPathFor(string subWorldPath)
        {
            return subWorldPath == null ? null : subWorldPath.TrimEnd('/') + "/MeshVariationDb_Win32";
        }

        // Per-run caches: validate re-reads the same handful of databases and blueprints across
        // thousands of members, and '--all' would otherwise reload them for every level it sweeps.
        private static Dictionary<string, HashSet<Guid>> s_mvdbMeshes;
        private static Dictionary<string, string> s_blueprintMeshes;
        private static Dictionary<string, List<int>> s_assetBundles;
        private static List<PendingMeshCheck> s_pendingMeshChecks;

        internal static void ResetValidationCaches()
        {
            s_mvdbMeshes = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
            s_blueprintMeshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            s_assetBundles = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            s_pendingMeshChecks = null;
        }

        // null means "this sub-world has no readable database at all", which is a different finding
        // from "the database exists but omits this mesh".
        // Every static-group member instance with an object variation needs a variation-database record for (the member's
        // MeshAsset, the variation's NameHash) in this level tree; without one the variation cannot be drawn.
        private static void ValidateMemberVariations(List<string> nodePaths, List<LevelIssue> issues, ICollection<int> loadedBundles)
        {
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string nodePath in nodePaths)
            {
                try
                {
                    EbxAssetEntry mvdbEntry = App.AssetManager.GetEbxEntry(MvdbPathFor(nodePath));
                    IList entries = mvdbEntry != null ? MvdbCommand.FindEntries(App.AssetManager.GetEbx(mvdbEntry).RootObject) : null;
                    if (entries == null) continue;
                    foreach (object entry in entries)
                    {
                        Guid fg, cg;
                        if (entry != null && TryReadExternalRef(ReadMember(entry, "Mesh"), out fg, out cg))
                            pairs.Add(fg.ToString("D") + ":" + Convert.ToUInt32(ReadMember(entry, "VariationAssetNameHash"), CultureInfo.InvariantCulture));
                    }
                }
                catch { }
            }
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<uint, EbxAssetEntry> byHash = null;   // NameHash -> ObjectVariation, built on first use
            foreach (string nodePath in nodePaths)
            {
                EbxAssetEntry node = App.AssetManager.GetEbxEntry(nodePath);
                if (node == null) continue;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(node); } catch { continue; }
                for (int g = 0; ; g++)
                {
                    string err;
                    object group = FindStaticGroup(asset.RootObject, g, out err);
                    if (group == null) break;
                    IList members = ReadList(group, "MemberDatas");
                    for (int m = 0; members != null && m < members.Count; m++)
                    {
                        object member = ResolveInternal(members[m]) ?? members[m];
                        IList vars = member != null ? ReadList(member, "InstanceObjectVariation") : null;
                        if (vars == null || vars.Count == 0) continue;
                        Guid meshGuid, mc;
                        if (!TryReadExternalRef(ReadMember(member, "MeshAsset"), out meshGuid, out mc)) continue;
                        foreach (object v in vars)
                        {
                            // BFII: the variation's NameHash (0 = base look); some profiles reference the asset instead
                            EbxAssetEntry ve;
                            uint hash;
                            if (v is uint u)
                            {
                                if (u == 0) continue;
                                if (byHash == null)
                                {
                                    byHash = new Dictionary<uint, EbxAssetEntry>();
                                    foreach (EbxAssetEntry ov in App.AssetManager.EnumerateEbx("ObjectVariation")) byHash[MeshVariationDupe.NameHash(ov.Name)] = ov;
                                }
                                hash = u;
                                if (!byHash.TryGetValue(u, out ve) && !byHash.ContainsKey(0))
                                {
                                    // the name rule did not explain it: read every variation's stored NameHash once (key 0 marks it done)
                                    foreach (EbxAssetEntry ov in App.AssetManager.EnumerateEbx("ObjectVariation"))
                                        try { byHash[Convert.ToUInt32(ReadMember(App.AssetManager.GetEbx(ov).RootObject, "NameHash"), CultureInfo.InvariantCulture)] = ov; } catch { }
                                    byHash[0] = null;
                                    byHash.TryGetValue(u, out ve);
                                }
                                if (ve == null)
                                {
                                    if (reported.Add("hash:" + u))
                                        AddIssue(issues, "error", "member-variation-unknown", nodePath, g, m,
                                            "instance variation hash " + u + " names no ObjectVariation asset");
                                    continue;
                                }
                            }
                            else
                            {
                                Guid vg, vc;
                                if (v == null || !TryReadExternalRef(v, out vg, out vc)) continue;
                                ve = App.AssetManager.GetEbxEntry(vg);
                                if (ve == null) continue;
                                try { hash = Convert.ToUInt32(ReadMember(App.AssetManager.GetEbx(ve).RootObject, "NameHash"), CultureInfo.InvariantCulture); } catch { continue; }
                            }
                            string key = meshGuid.ToString("D") + ":" + hash;
                            if (!reported.Add(key)) continue;
                            EbxAssetEntry me = App.AssetManager.GetEbxEntry(meshGuid);
                            if (!pairs.Contains(key))
                                AddIssue(issues, "error", "member-variation-not-in-mvdb", nodePath, g, m,
                                    "variation '" + ve.Name + "' on mesh '" + (me != null ? me.Name : meshGuid.ToString()) + "' has no record in this level's " +
                                    "MeshVariationDb_Win32 (mvdb copy/add with --variation-hash " + hash + ")");
                            // The variation asset and its depot for this mesh travel in the level's bundles like the mesh's own depot:
                            // the depot is found by name, so one left in the shipped level it came from changes nothing, silently.
                            if (me == null || loadedBundles == null || loadedBundles.Count == 0) continue;
                            bool veLoaded = false;
                            foreach (int id in BundleIdsOf(ve)) if (loadedBundles.Contains(id)) { veLoaded = true; break; }
                            if (!veLoaded)
                                AddIssue(issues, "error", "member-variation-not-bundled", nodePath, g, m,
                                    "variation '" + ve.Name + "' is in no bundle this level loads (level set-variations --register)");
                            string depotName = VariationDepotName(ve.Name, me);
                            string dp = depotName == null ? null : DepotBundleProblem(App.AssetManager.GetResEntry(depotName), depotName, loadedBundles);
                            if (dp != null)
                                AddIssue(issues, "error", "member-variation-depot-not-bundled", nodePath, g, m,
                                    "variation '" + ve.Name + "' on mesh '" + me.Name + "': " + dp.Replace("the mesh", "the variation"));
                        }
                    }
                }
            }
        }

        /// <summary>"&lt;variation&gt;/&lt;mesh file&gt;_&lt;MeshSet name hash&gt;/shaderblocks_variation/blocks", or null when the mesh's MeshSet is unreadable.</summary>
        internal static string VariationDepotName(string variationName, EbxAssetEntry meshEntry)
        {
            uint h;
            if (FrostyCLI.Adapters.Mesh.MeshCloner.ReadMeshSetName(meshEntry, out h) == null) return null;
            return variationName.ToLowerInvariant() + "/" + MeshVariationDupe.MeshFileName(meshEntry.Name) + "_" + h.ToString(CultureInfo.InvariantCulture) + "/shaderblocks_variation/blocks";
        }

        private static HashSet<Guid> GetMvdbMeshGuids(string subWorldPath)
        {
            if (s_mvdbMeshes == null) ResetValidationCaches();
            HashSet<Guid> cached;
            if (s_mvdbMeshes.TryGetValue(subWorldPath, out cached)) return cached;

            HashSet<Guid> guids = null;
            try
            {
                EbxAssetEntry mvdbEntry = App.AssetManager.GetEbxEntry(MvdbPathFor(subWorldPath));
                if (mvdbEntry != null)
                {
                    IList entries = MvdbCommand.FindEntries(App.AssetManager.GetEbx(mvdbEntry).RootObject);
                    if (entries != null)
                    {
                        guids = new HashSet<Guid>();
                        foreach (object entry in entries)
                        {
                            if (entry == null) continue;
                            Guid fileGuid, classGuid;
                            if (TryReadExternalRef(ReadMember(entry, "Mesh"), out fileGuid, out classGuid))
                                guids.Add(fileGuid);
                        }
                    }
                }
            }
            catch { guids = null; }

            s_mvdbMeshes[subWorldPath] = guids;
            return guids;
        }

        private static string ResolveBlueprintMeshPath(string blueprintPath)
        {
            if (s_blueprintMeshes == null) ResetValidationCaches();
            string cached;
            if (s_blueprintMeshes.TryGetValue(blueprintPath, out cached)) return cached;

            string meshPath = null;
            try
            {
                EbxAssetEntry bpEntry = App.AssetManager.GetEbxEntry(blueprintPath);
                if (bpEntry != null) meshPath = FindBlueprintMesh(App.AssetManager.GetEbx(bpEntry).RootObject);
            }
            catch { meshPath = null; }

            s_blueprintMeshes[blueprintPath] = meshPath;
            return meshPath;
        }

        private static List<int> GetBundleIds(string assetPath)
        {
            if (s_assetBundles == null) ResetValidationCaches();
            List<int> cached;
            if (s_assetBundles.TryGetValue(assetPath, out cached)) return cached;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(assetPath);
            List<int> ids = entry == null ? null : BundleIdsOf(entry);
            s_assetBundles[assetPath] = ids;
            return ids;
        }

        /// <summary>Null when a mesh's shader block depot is in a bundle the level loads, else what is wrong. Pure, for the tests:
        /// <paramref name="depotBundles"/> are the depot res entry's bundles (null = the res does not exist).</summary>
        internal static string DepotBundleProblem(ICollection<int> depotBundles, string depotName, ICollection<int> loadedBundles)
        {
            if (depotBundles == null) return "it has no shader block depot (" + depotName + "); the mesh cannot draw";
            foreach (int id in depotBundles) if (loadedBundles.Contains(id)) return null;
            return "its shader block depot '" + depotName + "' is in no bundle this level loads, so the mesh draws nothing " +
                   "(no crash, no warning); see 'bundle add <mesh> --to <bundle> --with-resources'";
        }

        private static string DepotBundleProblem(ResAssetEntry depot, string depotName, ICollection<int> loadedBundles)
        {
            if (depot == null) return DepotBundleProblem((ICollection<int>)null, depotName, loadedBundles);
            var ids = new List<int>(depot.Bundles);
            foreach (int id in depot.AddedBundles) if (!ids.Contains(id)) ids.Add(id);
            return DepotBundleProblem(ids, depotName, loadedBundles);
        }

        private static List<int> BundleIdsOf(EbxAssetEntry entry)
        {
            var ids = new List<int>();
            if (entry == null) return ids;
            ids.AddRange(entry.Bundles);
            foreach (int id in entry.AddedBundles) if (!ids.Contains(id)) ids.Add(id);
            return ids;
        }

        private static bool TryReadExternalRef(object pointerRef, out Guid fileGuid, out Guid classGuid)
        {
            fileGuid = Guid.Empty;
            classGuid = Guid.Empty;
            object external = ReadMember(pointerRef, "External");
            if (external == null) return false;
            object fg = ReadMember(external, "FileGuid");
            object cg = ReadMember(external, "ClassGuid");
            if (!(fg is Guid) || (Guid)fg == Guid.Empty) return false;
            fileGuid = (Guid)fg;
            if (cg is Guid) classGuid = (Guid)cg;
            return true;
        }

        // Per-instance side arrays are optional and are read defensively -- shipped levels routinely
        // carry short ones, and every consumer index-guards them, so a short array is not a defect.
        // An array longer than InstanceCount is: its tail can never be read and the surplus entries
        // are stale relative to the placements they claim to describe.
        private static void CheckParallelArray(object member, string name, string assetPath, int groupIndex,
            int memberIndex, int instanceCount, List<LevelIssue> issues)
        {
            IList list = ReadList(member, name);
            if (list == null || list.Count <= instanceCount) return;
            AddIssue(issues, "warning", "parallel-array-long", assetPath, groupIndex, memberIndex,
                name + " holds " + list.Count + " entries for InstanceCount " + instanceCount);
        }

        // A PointerRef aimed at another asset legitimately carries no Internal object; only a ref
        // with neither target is malformed.
        private static bool HasExternalTarget(object value)
        {
            if (value == null || value.GetType().Name != "PointerRef") return false;
            object external = ReadMember(value, "External");
            if (external == null) return false;
            object fileGuid = ReadMember(external, "FileGuid");
            return fileGuid is Guid && (Guid)fileGuid != Guid.Empty;
        }

        // The resource's per-instance detail indices address the group HavokAsset's ExternalAssets and
        // Aabb lists (one slot per prop). An index past the lists, or lists of different lengths, crashes
        // the level on load (the shape data itself can be perfect). Found by in-game bisect.
        private static void ValidatePhysicsAssetTables(object group, string resPath, string assetPath, int groupIndex, List<LevelIssue> issues)
        {
            object havok = null;
            IList components = ReadList(group, "Components");
            if (components != null)
                foreach (object raw in components)
                {
                    object comp = ResolveInternal(raw) ?? raw;
                    if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                    IList bodies = ReadList(comp, "PhysicsBodies");
                    if (bodies == null) continue;
                    foreach (object b in bodies)
                    {
                        object assetRef = ReadMember(ResolveInternal(b) ?? b, "Asset");
                        havok = ResolveInternal(assetRef);
                        if (havok == null)
                        {
                            Guid fg, cg;
                            if (TryReadExternalRef(assetRef, out fg, out cg))
                            {
                                EbxAssetEntry e = App.AssetManager.GetEbxEntry(fg);
                                try { if (e != null) havok = App.AssetManager.GetEbx(e).RootObject; } catch { havok = null; }
                            }
                        }
                        if (havok != null) break;
                    }
                    if (havok != null) break;
                }
            if (havok == null) return;
            IList external = ReadList(havok, "ExternalAssets");
            IList aabbs = ReadList(havok, "Aabb");
            if (external == null || aabbs == null) return;
            if (external.Count != aabbs.Count)
                AddIssue(issues, "error", "physics-asset-tables-mismatch", assetPath, groupIndex, null,
                    "the group HavokAsset lists " + external.Count + " external asset(s) but " + aabbs.Count + " box set(s)");
            foreach (object raw in components)
            {
                object comp = ResolveInternal(raw) ?? raw;
                if (comp == null || comp.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                IList bodies = ReadList(comp, "PhysicsBodies");
                if (bodies == null) continue;
                for (int bi = 0; bi < bodies.Count; bi++)
                {
                    object body = ResolveInternal(bodies[bi]) ?? bodies[bi];
                    IList lookups = body != null ? ReadList(body, "RaycastMaterialIndicesLookups") : null;
                    if (lookups != null && lookups.Count != external.Count)
                        AddIssue(issues, "error", "raycast-lookups-mismatch", assetPath, groupIndex, null,
                            "group physics body " + bi + " lists " + lookups.Count + " raycast material lookup(s) but the group HavokAsset lists " +
                            external.Count + " external asset(s); the game reads past the list on the first ray that hits a prop beyond it (game+0x1444ADC)");
                }
            }
            ResAssetEntry re = App.AssetManager.GetResEntry(resPath);
            byte[] bytes; string err;
            if (re == null || !TryReadResBytes(re, out bytes, out err)) return;
            int[] detail;
            try { detail = HavokPhysicsResource.Parse(bytes).DetailResourceIndices; }
            catch { return; }
            int max = -1;
            foreach (int d in detail) max = Math.Max(max, d);
            if (max >= external.Count)
                AddIssue(issues, "error", "detail-index-out-of-range", assetPath, groupIndex, null,
                    "'" + resPath + "' uses detail resource index " + max + " but the group HavokAsset lists only " + external.Count +
                    " external asset(s); a member was added without registering its prop, and the level crashes on load");
        }

        private static List<string> CollectPhysicsResourcePaths(object group)
        {
            var paths = new List<string>();
            IList components = ReadList(group, "Components");
            if (components == null) return paths;
            foreach (object componentRef in components)
            {
                object component = ResolveInternal(componentRef);
                if (component == null || component.GetType().Name != "StaticModelGroupPhysicsComponentData") continue;
                IList bodies = ReadList(component, "PhysicsBodies");
                if (bodies == null) continue;
                foreach (object bodyRef in bodies)
                {
                    object body = ResolveInternal(bodyRef);
                    object asset = body != null ? ResolveInternal(ReadMember(body, "Asset")) : null;
                    object name = asset != null ? ReadMember(asset, "Name") : null;
                    string path = name != null ? name.ToString() : null;
                    if (!string.IsNullOrEmpty(path) && !paths.Contains(path)) paths.Add(path);
                }
            }
            return paths;
        }

        private static void AddIssue(List<LevelIssue> issues, string severity, string code, string asset,
            int? group, int? member, string detail)
        {
            if (issues == null) return;
            issues.Add(new LevelIssue
            {
                Severity = severity,
                Code = code,
                Asset = asset,
                Group = group,
                Member = member,
                Detail = detail
            });
        }

        internal static string Describe(Exception ex)
        {
            return ex.InnerException != null ? ex.InnerException.Message : ex.Message;
        }

        private static void AccumulateLevelStats(LevelNode node, LevelTotals totals)
        {
            if (node.Repeated) return;
            if (node.Type != null) totals.Assets++;
            totals.Objects += node.Objects;
            totals.StaticGroups += node.StaticGroups;
            totals.Members += node.Members;
            totals.Placements += node.Placements;
            totals.EbxPlacements += node.EbxPlacements;
            totals.HavokPlacements += node.HavokPlacements;
            totals.MissingPlacements += node.MissingPlacements;
            if (node.Error != null) totals.Errors.Add(node.Path + ": " + node.Error);
            foreach (KeyValuePair<string, int> item in node.ObjectTypes)
            {
                int count;
                totals.ObjectTypes.TryGetValue(item.Key, out count);
                totals.ObjectTypes[item.Key] = count + item.Value;
            }
            foreach (LevelNode child in node.Children) AccumulateLevelStats(child, totals);
        }

        private static void PrintLevelTree(LevelNode node, int depth)
        {
            string indent = new string(' ', depth * 2);
            string suffix = node.Repeated ? " (already shown)" : node.Error != null ? " (warning: " + node.Error + ")" : "";
            Console.Out.WriteLine(indent + node.Path + " [" + (node.Type ?? "unknown") + "] objects=" + node.Objects +
                " groups=" + node.StaticGroups + " placements=" + node.Placements + suffix);
            foreach (LevelNode child in node.Children) PrintLevelTree(child, depth + 1);
        }

        // BFII stores static placements as StaticModelGroup member/instance pairs rather than as a
        // flat entity list.  Instance transforms may be inline EBX values or entries in the group's
        // HavokPhysicsData resource.  Keep this path read-only: the coordinated write bookkeeping is
        // deliberately left to a later change.
        internal static bool TryCollectBfiiRows(object root, List<EntityRow> rows, string filter, int limit, out string warning)
        {
            return TryCollectBfiiRowsRecursive(root, rows, filter, limit,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), out warning);
        }

        private static bool TryCollectBfiiRowsRecursive(object root, List<EntityRow> rows, string filter, int limit,
            HashSet<string> visited, out string warning)
        {
            warning = null;
            IList objects = ReadList(root, "Objects");
            if (objects == null) return false;

            bool foundBfiiLayout = false;
            int groupIndex = 0;
            foreach (object value in objects)
            {
                object obj = ResolveInternal(value);
                if (obj == null) continue;
                if (obj.GetType().Name == "StaticModelGroupEntityData")
                {
                    foundBfiiLayout = true;
                    string groupWarning;
                    CollectStaticModelGroup(obj, groupIndex++, rows, filter, limit, out groupWarning);
                    if (groupWarning != null) warning = groupWarning;
                    if (rows.Count >= limit) break;
                }
                else if (obj.GetType().Name == "SubWorldReferenceObjectData" ||
                         obj.GetType().Name == "LayerReferenceObjectData" ||
                         obj.GetType().Name == "WorldPartReferenceObjectData")
                {
                    bool isLayer = obj.GetType().Name != "SubWorldReferenceObjectData";
                    foundBfiiLayout = true;
                    // Frostbite 2015 (SWBF1) keeps its layers as WorldPartReferenceObjectData whose Blueprint is an
                    // INTERNAL WorldPartData of this same partition, not a LayerData asset. Descend into it in place;
                    // skipping it left every layer of Volcanic_01 -- the Imperial hangar among them -- unexported.
                    object internalPart = isLayer ? ResolveInternal(ReadMember(obj, "Blueprint")) : null;
                    if (internalPart != null)
                    {
                        string partName = Convert.ToString(ReadMember(internalPart, "Name"), CultureInfo.InvariantCulture);
                        string partKey = "internal:" + (string.IsNullOrEmpty(partName)
                            ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(internalPart).ToString(CultureInfo.InvariantCulture)
                            : partName);
                        if (!visited.Add(partKey)) continue;
                        string partWarning;
                        TryCollectBfiiRowsRecursive(internalPart, rows, filter, limit, visited, out partWarning);
                        if (partWarning != null) warning = partWarning;
                        if (rows.Count >= limit) break;
                        continue;
                    }
                    // Layers are containers, not props: descend into one rather than listing the
                    // reference itself as a placement.
                    string childPath = isLayer
                        ? ResolveBlueprintPath(ReadMember(obj, "Blueprint"))
                        : Convert.ToString(ReadMember(obj, "BundleName"), CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(childPath) || !visited.Add(childPath)) continue;
                    try
                    {
                        EbxAssetEntry entry = App.AssetManager.GetEbxEntry(childPath);
                        if (entry == null) continue;
                        EbxAsset child = App.AssetManager.GetEbx(entry);
                        string childWarning;
                        TryCollectBfiiRowsRecursive(child.RootObject, rows, filter, limit, visited, out childWarning);
                        if (childWarning != null) warning = childWarning;
                    }
                    catch (Exception ex)
                    {
                        warning = "could not traverse " + (isLayer ? "layer " : "sub-world ") + childPath + ": " + ex.Message;
                    }
                    if (rows.Count >= limit) break;
                }
                else
                {
                    // Everything else that names a blueprint at a transform is a placement too:
                    // props, spatial prefabs, lights, effects, decals, sound emitters. Only static
                    // model groups were expanded before, which left most of a level unreported.
                    EntityRow row = ExtractRow(obj);
                    if (row == null || row.Blueprint == null) continue;
                    if (filter != null && !row.Blueprint.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    row.TransformSource = "entity";
                    rows.Add(row);
                    foundBfiiLayout = true;
                    if (rows.Count >= limit) break;
                }
            }
            return foundBfiiLayout;
        }

        private static void CollectStaticModelGroup(object group, int groupIndex, List<EntityRow> rows, string filter, int limit, out string warning)
        {
            warning = null;
            IList members = ReadList(group, "MemberDatas");
            if (members == null) return;

            List<HavokTransformReader.Transform> physicsTransforms = null;
            string resPath = FindPhysicsResourcePath(group);
            int havokNeeded = CountHavokPlacements(members);
            if (resPath != null && havokNeeded > 0)
            {
                string readError;
                List<List<HavokTransformReader.Transform>> arrays;
                if (!TryLoadPhysicsArrays(resPath, out arrays, out readError))
                {
                    warning = "could not read " + resPath + ": " + readError;
                }
                else
                {
                    physicsTransforms = HavokTransformReader.SelectPlacements(arrays, havokNeeded);
                    if (physicsTransforms == null)
                        warning = resPath + " has no instance array of " + havokNeeded + " transforms (found " +
                            HavokTransformReader.DescribeSizes(arrays) + ")";
                }
            }

            // The transform source is selected per member, not per instance: a member holding any
            // inline InstanceTransforms reads all of its instances from EBX, and the group's Havok
            // cursor only advances for members that actually consume Havok transforms.  Advancing
            // it for EBX-backed members misaligns every later member in the same group.
            int physicsIndex = 0;
            for (int memberIndex = 0; memberIndex < members.Count && rows.Count < limit; memberIndex++)
            {
                object member = ResolveInternal(members[memberIndex]) ?? members[memberIndex];
                if (member == null) continue;
                string blueprint = ResolveBlueprintPath(ReadMember(member, "MemberType"));
                int instanceCount = Math.Max(0, ConvertToInt(ReadMember(member, "InstanceCount")));
                IList inline = ReadList(member, "InstanceTransforms");
                bool useInline = inline != null && inline.Count > 0;
                int memberPhysicsStart = physicsIndex;
                if (!useInline && physicsTransforms != null) physicsIndex += instanceCount;

                for (int instanceIndex = 0; instanceIndex < instanceCount && rows.Count < limit; instanceIndex++)
                {
                    EntityRow row = null;
                    if (useInline)
                    {
                        if (instanceIndex >= inline.Count) continue;
                        float[] r = { 1, 0, 0 }, u = { 0, 1, 0 }, f = { 0, 0, 1 }, t = { 0, 0, 0 };
                        ReadTransform(inline[instanceIndex], ref r, ref u, ref f, ref t);
                        row = new EntityRow { Blueprint = blueprint, Right = r, Up = u, Forward = f, Trans = t, TransformSource = "ebx" };
                    }
                    else if (physicsTransforms != null)
                    {
                        int sourceIndex = memberPhysicsStart + instanceIndex;
                        if (sourceIndex >= physicsTransforms.Count) continue;
                        HavokTransformReader.Transform tf = physicsTransforms[sourceIndex];
                        row = new EntityRow { Blueprint = blueprint, Right = tf.Right, Up = tf.Up, Forward = tf.Forward, Trans = tf.Translation, TransformSource = "havok" };
                    }

                    if (row == null) continue;
                    if (filter != null && (blueprint == null || !blueprint.StartsWith(filter, StringComparison.OrdinalIgnoreCase))) continue;
                    row.GroupIndex = groupIndex;
                    row.MemberIndex = memberIndex;
                    row.InstanceIndex = instanceIndex;
                    rows.Add(row);
                }
            }
        }

        internal static bool TryLoadPhysicsArrays(string resPath,
            out List<List<HavokTransformReader.Transform>> arrays, out string error)
        {
            List<HavokTransformReader.CompoundTree> ignored;
            return TryLoadPhysicsArrays(resPath, out arrays, out ignored, out error);
        }

        private static bool TryLoadPhysicsArrays(string resPath,
            out List<List<HavokTransformReader.Transform>> arrays, out List<HavokTransformReader.CompoundTree> treeNodeCounts, out string error)
        {
            arrays = null;
            treeNodeCounts = null;
            error = null;
            try
            {
                ResAssetEntry entry = App.AssetManager.GetResEntry(resPath);
                if (entry == null) { error = "resource entry not found"; return false; }
                MethodInfo method = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
                object raw = method != null ? method.Invoke(App.AssetManager, new object[] { entry }) : null;
                byte[] data = raw as byte[];
                if (data == null && raw is Stream)
                {
                    using (Stream input = (Stream)raw)
                    using (var output = new MemoryStream()) { input.CopyTo(output); data = output.ToArray(); }
                }
                if (data == null) { error = "unexpected GetRes return type"; return false; }
                return HavokTransformReader.TryReadCompounds(data, out arrays, out treeNodeCounts, out error);
            }
            catch (Exception ex) { error = ex.InnerException != null ? ex.InnerException.Message : ex.Message; return false; }
        }

        internal static string FindPhysicsResourcePath(object group)
        {
            List<string> paths = CollectPhysicsResourcePaths(group);
            return paths.Count > 0 ? paths[0] : null;
        }

        internal static object ResolveInternal(object value)
        {
            if (value == null) return null;
            if (value.GetType().Name != "PointerRef") return value;
            return ReadMember(value, "Internal");
        }

        internal static IList ReadList(object value, string name)
        {
            return ReadMember(value, name) as IList;
        }

        internal static object ReadMember(object value, string name)
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

        internal static int ConvertToInt(object value)
        {
            try { return value != null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0; }
            catch { return 0; }
        }

        internal static IList GetEntityList(object levelRoot)
        {
            Type rt = levelRoot.GetType();

            // First try the well-known "Objects" property
            PropertyInfo p = rt.GetProperty("Objects", BindingFlags.Public | BindingFlags.Instance);
            if (p != null)
            {
                object v = p.GetValue(levelRoot);
                if (v is IList) return (IList)v;
            }

            // Fallback: prefer a list whose element type name contains "Entity" or "Blueprint"
            IList firstList = null;
            foreach (PropertyInfo cand in rt.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = null;
                try { v = cand.GetValue(levelRoot); } catch { continue; }
                if (!(v is IList)) continue;
                IList list = (IList)v;

                // Check element type name for entity/blueprint hint
                Type elemType = null;
                Type lt = list.GetType();
                if (lt.IsGenericType) elemType = lt.GetGenericArguments()[0];
                else if (list.Count > 0 && list[0] != null) elemType = list[0].GetType();

                if (elemType != null)
                {
                    string eName = elemType.Name;
                    if (eName.IndexOf("Entity",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                        eName.IndexOf("Blueprint", StringComparison.OrdinalIgnoreCase) >= 0)
                        return list;
                }

                // Remember the first IList in case no better candidate is found
                if (firstList == null) firstList = list;
            }
            return firstList;
        }

        private static EntityRow ExtractRow(object entity)
        {
            Type et = entity.GetType();
            PropertyInfo bpProp = et.GetProperty("Blueprint", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo tfProp = et.GetProperty("BlueprintTransform", BindingFlags.Public | BindingFlags.Instance);
            if (bpProp == null) return null;
            object bpRef = bpProp.GetValue(entity);
            string bpPath = ResolveBlueprintPath(bpRef);

            float[] r = { 1, 0, 0 }, u = { 0, 1, 0 }, f = { 0, 0, 1 }, t = { 0, 0, 0 };
            if (tfProp != null)
            {
                object tf = tfProp.GetValue(entity);
                ReadTransform(tf, ref r, ref u, ref f, ref t);
            }
            return new EntityRow { Blueprint = bpPath, Right = r, Up = u, Forward = f, Trans = t };
        }

        internal static string ResolveBlueprintPath(object bpRef)
        {
            if (bpRef == null) return null;
            Type t = bpRef.GetType();
            if (t.Name == "PointerRef")
            {
                object external = ReadMember(bpRef, "External");
                if (external == null) return null;
                bpRef = external;
                t = bpRef.GetType();
            }
            object fileGuidObj = null;
            PropertyInfo fgp = t.GetProperty("FileGuid", BindingFlags.Public | BindingFlags.Instance);
            FieldInfo    fgf = t.GetField   ("FileGuid", BindingFlags.Public | BindingFlags.Instance);
            if (fgp != null) fileGuidObj = fgp.GetValue(bpRef);
            else if (fgf != null) fileGuidObj = fgf.GetValue(bpRef);
            if (!(fileGuidObj is Guid)) return null;
            Guid fileGuid = (Guid)fileGuidObj;

            MethodInfo getByGuid = App.AssetManager.GetType().GetMethod("GetEbxEntry", new[] { typeof(Guid) });
            if (getByGuid != null)
            {
                var bpEntry = (EbxAssetEntry)getByGuid.Invoke(App.AssetManager, new object[] { fileGuid });
                if (bpEntry != null) return bpEntry.Name;
            }
            return fileGuid.ToString();
        }

        private static void ReadTransform(object tf, ref float[] r, ref float[] u, ref float[] f, ref float[] t)
        {
            if (tf == null) return;
            Type tt = tf.GetType();
            r = ReadVec3(tt, tf, "right")   ?? r;
            u = ReadVec3(tt, tf, "up")      ?? u;
            f = ReadVec3(tt, tf, "forward") ?? f;
            t = ReadVec3(tt, tf, "trans")   ?? t;
        }

        private static float[] ReadVec3(Type tt, object tf, string field)
        {
            string[] casings = { field, char.ToUpper(field[0]) + field.Substring(1) };
            foreach (string n in casings)
            {
                PropertyInfo p = tt.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) { object v = p.GetValue(tf); return ExtractVec3Components(v); }
                FieldInfo fi = tt.GetField(n, BindingFlags.Public | BindingFlags.Instance);
                if (fi != null) { object v = fi.GetValue(tf); return ExtractVec3Components(v); }
            }
            return null;
        }

        private static float[] ExtractVec3Components(object v)
        {
            if (v == null) return null;
            Type vt = v.GetType();
            float[] result = new float[3];
            string[] axes = { "x", "y", "z" };
            for (int i = 0; i < 3; i++)
            {
                string[] casings = { axes[i], axes[i].ToUpper() };
                bool got = false;
                foreach (string n in casings)
                {
                    PropertyInfo p = vt.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null) { result[i] = Convert.ToSingle(p.GetValue(v), CultureInfo.InvariantCulture); got = true; break; }
                    FieldInfo fi = vt.GetField(n, BindingFlags.Public | BindingFlags.Instance);
                    if (fi != null) { result[i] = Convert.ToSingle(fi.GetValue(v), CultureInfo.InvariantCulture); got = true; break; }
                }
                if (!got) return null;
            }
            return result;
        }
    }
}
