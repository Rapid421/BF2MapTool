using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Pipeline
{
    public class JsonPipelineRunner : FrostyCLI.Core.IPipelineRunner
    {
        public int Run(string pipelineJsonPath, ExportLogger logger, OutputFormatter formatter)
        {
            if (!File.Exists(pipelineJsonPath))
            {
                logger.LogError("Pipeline file not found: {0}", pipelineJsonPath);
                return 1;
            }

            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(pipelineJsonPath));
            }
            catch (JsonException ex)
            {
                logger.LogError("Pipeline JSON parse error: {0}", ex.Message);
                return 1;
            }

            if (!Session.IsInitialized)
            {
                string game     = root["game"] != null ? root["game"].Value<string>() : "swbf2";
                string gamePath = root["game-path"] != null ? root["game-path"].Value<string>() : null;
                // Honor a handler-plugins dir so a pipeline that loads a .fbproject containing a
                // handler-based ModifiedResource (e.g. a localization asset) can resolve its plugin
                // type during project-load -- otherwise ModifiedResource.Read throws and the load
                // silently drops every asset after it. Falls back to the global --handler-plugins
                // the launcher stashed (Session.DefaultHandlerPluginsDir) when the JSON omits it.
                string handlerPlugins = root["handler-plugins"] != null
                    ? root["handler-plugins"].Value<string>()
                    : Session.DefaultHandlerPluginsDir;
                if (gamePath != null)
                    Session.Initialize(game, gamePath, null, null, logger, handlerPlugins);
            }

            var stepsToken = root["steps"] as JArray;
            if (stepsToken == null)
            {
                logger.LogError("Pipeline JSON missing 'steps' array.");
                return 1;
            }

            var steps = stepsToken.ToObject<List<PipelineStep>>();
            var failed = new List<string>();

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                int result = ExecuteStep(step, i, logger, formatter);
                if (result != 0)
                {
                    failed.Add("step " + i + " (" + step.Op + ")");
                    string onError = step.OnError ?? "fail";
                    if (string.Equals(onError, "fail", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogError("Step {0} failed and on-error=fail; aborting pipeline.", i);
                        return 1;
                    }
                }
            }

            // Under on-error=continue each failure scrolls away beneath the output of the steps after
            // it. Name them where the run ends, so a partial run cannot be read as a complete one.
            if (failed.Count > 0)
            {
                logger.LogError("{0} of {1} steps failed: {2}", failed.Count, steps.Count, string.Join(", ", failed.ToArray()));
                return 1;
            }
            return 0;
        }

        private int ExecuteStep(PipelineStep step, int index, ExportLogger logger, OutputFormatter formatter)
        {
            string op = step.Op != null ? step.Op.ToLowerInvariant() : null;
            switch (op)
            {
                case "export":
                    return ExecuteExport(step, logger, formatter);
                case "project-new":
                    return ExecuteProjectNew(step, logger, formatter);
                case "project-load":
                    return ExecuteProjectLoad(step, logger, formatter);
                case "project-save":
                    return ExecuteProjectSave(step, logger, formatter);
                case "mod-build":
                    return ExecuteModBuild(step, logger, formatter);
                case "ebx-set":    return ExecuteEbxSet(step, logger, formatter);
                case "ebx-clone":  return ExecuteEbxClone(step, logger, formatter);
                case "ebx-delete":  return ExecuteEbxDelete(step, logger, formatter);
                case "ebx-revert":  return ExecuteEbxRevert(step, logger, formatter);
                case "ebx-set-ref": return ExecuteEbxSetRef(step, logger, formatter);
                case "ebx-create":  return ExecuteEbxCreate(step, logger, formatter);
                case "ebx-new-object": return ExecuteEbxNewObject(step, logger, formatter);
                case "ebx-array-add":    return ExecuteEbxArrayAdd(step, logger, formatter);
                case "connection-add":    return ExecuteConnectionAdd(step, logger, formatter);
                case "connection-remove": return ExecuteConnectionRemove(step, logger, formatter);
                case "ebx-array-remove": return ExecuteEbxArrayRemove(step, logger, formatter);
                case "level-add":    return ExecuteLevelAdd(step, logger, formatter);
                case "level-place":  return ExecuteLevelPlace(step, logger, formatter);
                case "level-add-entity": return ExecuteLevelAddEntity(step, logger, formatter);
                case "level-import-lights": return ExecuteLevelImportLights(step, logger, formatter);
                case "level-remove":   return ExecuteLevelRemove(step, logger, formatter);
                case "level-clear":    return ExecuteLevelClear(step, logger, formatter);
                case "level-sink-group": return ExecuteLevelSinkGroup(step, logger, formatter);
                case "level-repoint-members": return ExecuteLevelRepoint(step, "repoint-members", logger, formatter);
                case "level-repoint-entities": return ExecuteLevelRepoint(step, "repoint-entities", logger, formatter);
                case "level-transform-group": return ExecuteLevelRepoint(step, "transform-group", logger, formatter);
                case "level-add-group": return ExecuteLevelAddGroup(step, logger, formatter);
                case "level-move":     return ExecuteLevelMove(step, logger, formatter);
                case "level-scaffold": return ExecuteLevelScaffold(step, logger, formatter);
                case "level-dupe":     return ExecuteLevelDupe(step, logger, formatter);
                case "mvdb-add":    return ExecuteMvdbAdd(step, logger, formatter);
                case "mvdb-remove": return ExecuteMvdbRemove(step, logger, formatter);
                case "loc-set":     return ExecuteLocSet(step, logger, formatter);
                case "import-texture": return ExecuteImportTexture(step, logger, formatter);
                case "import-mesh":    return ExecuteImportMesh(step, logger, formatter);
                case "import-sound":   return ExecuteImportSound(step, logger, formatter);
                case "audio-inspect":
                case "audio-graph-inspect":
                case "audio-graph-validate":
                case "audio-graph-export":
                case "audio-graph-plan":
                case "audio-graph-import":
                case "audio-inventory":
                case "audio-dependencies":
                case "audio-export":
                case "audio-import":
                case "audio-place": return ExecuteAudio(step, logger, formatter);
                case "bundle-new":     return ExecuteBundleNew(step, logger, formatter);
                case "bundle-add":     return ExecuteBundleAdd(step, logger, formatter);
                case "bundle-remove":  return ExecuteBundleRemove(step, logger, formatter);
                case "material-set":   return ExecuteMaterialSet(step, logger, formatter);
                case "material-graft": return ExecuteMaterialGraft(step, logger, formatter);
                case "ebx-dump":     return ExecuteEbxDump(step, logger, formatter);
                case "ebx-graph-export": return ExecuteEbxGraph(step, logger, formatter);
                case "ebx-graph-import": return ExecuteEbxGraph(step, logger, formatter);
                case "material-dump": return ExecuteMaterialDump(step, logger, formatter);
                case "ebx-search":   return ExecuteEbxSearch(step, logger, formatter);
                case "ebx-refs":     return ExecuteEbxRefs(step, logger, formatter);
                case "res-export":   return ExecuteResExport(step, logger, formatter);
                case "chunk-export": return ExecuteChunkExport(step, logger, formatter);
                case "res-import":   return ExecuteResImport(step, logger, formatter);
                case "res-revert":
                    if (string.IsNullOrEmpty(step.Res)) { logger.LogError("res-revert: missing 'res' field."); return 1; }
                    return FrostyCLI.Commands.ResCommand.ExecuteRevert(new[] { step.Res }, logger, formatter);
                case "chunk-import": return ExecuteChunkImport(step, logger, formatter);
                case "port-export": return ExecutePortExport(step, logger, formatter);
                case "port-import": return ExecutePortImport(step, logger, formatter);
                case "port-regeo": return ExecutePortRegeo(step, logger, formatter);
                case "port-repoint-member":
                    {
                        var ra = new List<string> { step.Ebx, "--group", step.Group, "--member", step.Member, "--blueprint", step.Blueprint };
                        if (!string.IsNullOrEmpty(step.Expect)) { ra.Add("--expect"); ra.Add(step.Expect); }
                        return FrostyCLI.Commands.PortRegeo.ExecuteRepointMember(ra.ToArray(), logger, formatter);
                    }
                case "port-prune-refs":
                    if (string.IsNullOrEmpty(step.Ebx) || string.IsNullOrEmpty(step.PrunePrefix)) { logger.LogError("port-prune-refs: needs 'ebx' and 'prune_prefix'."); return 1; }
                    return FrostyCLI.Commands.PortRegeo.ExecutePruneRefs(new[] { step.Ebx, "--prefix", step.PrunePrefix }, logger, formatter);
                case "port-import-layer": return ExecutePortImportLayer(step, logger, formatter);
                case "terrain-retarget":       return ExecuteTerrainRetarget(step, logger, formatter);
                case "terrain-import-heights": return ExecuteTerrainImportHeights(step, logger, formatter);
                case "terrain-export-heights": return ExecuteTerrainExportHeights(step, logger, formatter);
                case "terrain-create":         return ExecuteTerrainCreate(step, logger, formatter);
                case "navmesh-build":          return ExecuteNavmesh(step, "build", logger, formatter);
                case "navmesh-geometry":       return ExecuteNavmesh(step, "geometry", logger, formatter);
                case "mod-diff":        return ExecuteModDiff(step, logger, formatter);
                case "mod-apply":       return ExecuteModApply(step, logger, formatter);
                case "mod-test":        return ExecuteModTest(step, logger, formatter);
                case "mod-lint":        return ExecuteModLint(step, logger, formatter);
                case "level-validate":  return ExecuteLevelValidate(step, logger, formatter);
                case "mvdb-copy":       return ExecuteMvdbCopy(step, logger, formatter);
                case "bundle-complete": return ExecuteBundleComplete(step, logger, formatter);
                case "cli":
                    // Any registered CLI command in-process, on the loaded project: {"op": "cli", "args": ["ve", "apply", "r.json"]}
                    // -- for commands that have no dedicated op (level prune, ve apply, enlighten port-bake ...).
                    if (step.Args == null || step.Args.Length == 0) { logger.LogError("cli: missing 'args'."); return 1; }
                    return FrostyCLI.Core.WriteOpBus.Execute("cli", step.Args, logger, formatter);
                default:
                    // Skipping used to count as success, so a typo -- or an op this runner does not
                    // know -- let the pipeline finish OK without doing the step.
                    logger.LogError("Step {0}: unknown op '{1}'.", index, step.Op);
                    return 1;
            }
        }

        // mod diff/apply/test read their options from index 1, because index 0 is the subcommand word
        // of `mod <sub> ...`; the bus args carry that word too.
        private int ExecuteModDiff(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            var list = new List<string> { "diff" };
            if (!string.IsNullOrEmpty(step.Base))   { list.Add("--base"); list.Add(step.Base); }
            if (!string.IsNullOrEmpty(step.Format)) { list.Add("--format"); list.Add(step.Format); }
            if (!string.IsNullOrEmpty(step.Out))    { list.Add("--out"); list.Add(step.Out); }
            return FrostyCLI.Core.WriteOpBus.Execute("mod-diff", list.ToArray(), logger, formatter);
        }

        private int ExecuteModApply(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File)) { logger.LogError("mod-apply: missing 'file' field."); return 1; }
            // 'only': comma-separated resource names / 'prefix*' patterns to apply (mod apply --only)
            if (!string.IsNullOrEmpty(step.Only))
                return FrostyCLI.Core.WriteOpBus.Execute("mod-apply", new[] { "apply", step.File, "--only", step.Only }, logger, formatter);
            return FrostyCLI.Core.WriteOpBus.Execute("mod-apply", new[] { "apply", step.File }, logger, formatter);
        }

        private int ExecuteModTest(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File)) { logger.LogError("mod-test: missing 'file' field."); return 1; }
            var list = new List<string> { "test", step.File };
            if (!string.IsNullOrEmpty(step.Level)) { list.Add("--level"); list.Add(step.Level); }
            if (!string.IsNullOrEmpty(step.Mode))  { list.Add("--mode"); list.Add(step.Mode); }
            if (step.Timeout.HasValue) { list.Add("--timeout"); list.Add(step.Timeout.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            return FrostyCLI.Core.WriteOpBus.Execute("mod-test", list.ToArray(), logger, formatter);
        }

        // mod lint and level validate exit 2 when they report findings, which fails the step -- that
        // is what makes them usable as gates ahead of mod-build.
        private int ExecuteModLint(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(step.Format)) { list.Add("--format"); list.Add(step.Format); }
            if (step.Strict) list.Add("--strict");
            return FrostyCLI.Core.WriteOpBus.Execute("mod-lint", list.ToArray(), logger, formatter);
        }

        private int ExecuteLevelValidate(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("level-validate: missing 'ebx' (level) field."); return 1; }
            var list = new List<string> { step.Ebx };
            if (!string.IsNullOrEmpty(step.Format)) { list.Add("--format"); list.Add(step.Format); }
            if (step.Strict) list.Add("--strict");
            return FrostyCLI.Core.WriteOpBus.Execute("level-validate", list.ToArray(), logger, formatter);
        }

        private int ExecuteMvdbCopy(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("mvdb-copy: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.Mesh)) { logger.LogError("mvdb-copy: missing 'mesh' field."); return 1; }
            if (string.IsNullOrEmpty(step.From)) { logger.LogError("mvdb-copy: missing 'from' (source database) field."); return 1; }
            var list = new List<string> { step.Ebx, "--mesh", step.Mesh, "--from", step.From };
            if (!string.IsNullOrEmpty(step.VariationHash)) { list.Add("--variation-hash"); list.Add(step.VariationHash); }
            if (!string.IsNullOrEmpty(step.Key)) { list.Add("--from-variation"); list.Add(step.Key); }
            return FrostyCLI.Core.WriteOpBus.Execute("mvdb-copy", list.ToArray(), logger, formatter);
        }

        // `bundle complete` saves the loaded project in place unless told not to. Mid-pipeline that
        // overwrites the input before the pipeline's own project-save runs, so it never saves here.
        private int ExecuteBundleComplete(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(step.Level)) { list.Add("--level"); list.Add(step.Level); }
            if (step.Cosmetic) list.Add("--cosmetic");
            list.Add("--no-save");
            return FrostyCLI.Core.WriteOpBus.Execute("bundle-complete", list.ToArray(), logger, formatter);
        }

        private int ExecutePortImport(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File))
            { logger.LogError("port-import step: missing 'file' field."); return 1; }
            if (string.IsNullOrEmpty(step.AsPath))
            { logger.LogError("port-import step: missing 'as' field."); return 1; }

            var args = new List<string> { step.File, "--as", step.AsPath };
            if (!string.IsNullOrEmpty(step.TemplatePrefix)) { args.Add("--template-prefix"); args.Add(step.TemplatePrefix); }
            if (!string.IsNullOrEmpty(step.TemplateList)) { args.Add("--template-list"); args.Add(step.TemplateList); }
            if (step.MaxLods > 0) { args.Add("--max-lods"); args.Add(step.MaxLods.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(step.MeshTemplate)) { args.Add("--mesh-template"); args.Add(step.MeshTemplate); }
            if (!string.IsNullOrEmpty(step.ShaderFallback)) { args.Add("--shader-fallback"); args.Add(step.ShaderFallback); }
            if (!string.IsNullOrEmpty(step.ShaderMap)) { args.Add("--shader-map"); args.Add(step.ShaderMap); }
            if (!string.IsNullOrEmpty(step.BlueprintTemplate)) { args.Add("--blueprint-template"); args.Add(step.BlueprintTemplate); }
            if (!string.IsNullOrEmpty(step.PhysicsTemplate)) { args.Add("--physics-template"); args.Add(step.PhysicsTemplate); }
            if (!string.IsNullOrEmpty(step.Mvdb)) { args.Add("--mvdb"); args.Add(step.Mvdb); }
            if (!string.IsNullOrEmpty(step.Bundle)) { args.Add("--bundle"); args.Add(step.Bundle); }
            if (step.KeepTemplateMaterials) args.Add("--keep-template-materials");
            if (step.KeepMaterialId) args.Add("--keep-material-id");
            if (step.NoPhysics) args.Add("--no-physics");
            if (!string.IsNullOrEmpty(step.Geo)) { args.Add("--geo"); args.Add(step.Geo); }
            if (!string.IsNullOrEmpty(step.Resident)) { args.Add("--resident"); args.Add(step.Resident); }
            if (step.NoGeo) args.Add("--no-geo");
            if (step.AllowForeignDonors) args.Add("--allow-foreign-donors");
            if (step.KeepComposite) args.Add("--keep-composite");
            return FrostyCLI.Commands.PortCommand.ExecuteImport(args.ToArray(), logger, formatter);
        }

        // port-regeo: the geometry port applied in place to an earlier import ({file: package, mesh: existing mesh, ...}).
        private int ExecutePortRegeo(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File)) { logger.LogError("port-regeo step: missing 'file' (package) field."); return 1; }
            if (string.IsNullOrEmpty(step.Mesh)) { logger.LogError("port-regeo step: missing 'mesh' field."); return 1; }
            var args = new List<string> { step.File, "--mesh", step.Mesh };
            if (!string.IsNullOrEmpty(step.Geo)) { args.Add("--geo"); args.Add(step.Geo); }
            if (!string.IsNullOrEmpty(step.Resident)) { args.Add("--resident"); args.Add(step.Resident); }
            if (!string.IsNullOrEmpty(step.Mvdb)) { args.Add("--mvdb"); args.Add(step.Mvdb); }
            if (!string.IsNullOrEmpty(step.Bundle)) { args.Add("--bundle"); args.Add(step.Bundle); }
            if (!string.IsNullOrEmpty(step.ShaderFallback)) { args.Add("--shader-fallback"); args.Add(step.ShaderFallback); }
            if (!string.IsNullOrEmpty(step.ShaderMap)) { args.Add("--shader-map"); args.Add(step.ShaderMap); }
            if (step.AllowForeignDonors) args.Add("--allow-foreign-donors");
            if (step.LuminanceScale.HasValue) { args.Add("--luminance-scale"); args.Add(step.LuminanceScale.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(step.LuminanceParams)) { args.Add("--luminance-params"); args.Add(step.LuminanceParams); }
            return FrostyCLI.Commands.PortRegeo.Execute(args.ToArray(), logger, formatter);
        }

        private int ExecutePortImportLayer(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File))
            { logger.LogError("port-import-layer step: missing 'file' field."); return 1; }
            if (string.IsNullOrEmpty(step.Into))
            { logger.LogError("port-import-layer step: missing 'into' field."); return 1; }

            var args = new List<string> { step.File, "--into", step.Into };
            if (!string.IsNullOrEmpty(step.Registry)) { args.Add("--registry"); args.Add(step.Registry); }
            if (!string.IsNullOrEmpty(step.Mvdb)) { args.Add("--mvdb"); args.Add(step.Mvdb); }
            if (!string.IsNullOrEmpty(step.PortedPrefix)) { args.Add("--ported-prefix"); args.Add(step.PortedPrefix); }
            if (step.NoBundle) args.Add("--no-bundle");
            if (step.NoRadiosity) args.Add("--no-radiosity");
            if (!string.IsNullOrEmpty(step.SkipBundleTypes)) { args.Add("--skip-bundle-types"); args.Add(step.SkipBundleTypes); }
            if (!string.IsNullOrEmpty(step.EntityClass)) { args.Add("--entity-class"); args.Add(step.EntityClass); }
            if (!string.IsNullOrEmpty(step.BlueprintType)) { args.Add("--blueprint-type"); args.Add(step.BlueprintType); }
            if (step.Flags.HasValue) { args.Add("--flags"); args.Add(step.Flags.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(step.Offset)) { args.Add("--offset"); args.Add(step.Offset); }
            if (!string.IsNullOrEmpty(step.Bundle)) { args.Add("--bundle"); args.Add(step.Bundle); }
            if (step.PreferPorted) args.Add("--prefer-ported");
            if (step.OnlyPorted) args.Add("--only-ported");
            if (step.AsMembers) args.Add("--as-members");
            if (!string.IsNullOrEmpty(step.Group)) { args.Add("--group"); args.Add(step.Group); }
            if (step.NoCollision) args.Add("--no-collision");
            if (!string.IsNullOrEmpty(step.MemberStub)) { args.Add("--member-stub"); args.Add(step.MemberStub); }
            if (step.ExcludeGuid) args.Add("--exclude-guid");
            if (!string.IsNullOrEmpty(step.ExcludeList)) { args.Add("--exclude-list"); args.Add(step.ExcludeList); }
            if (step.Limit.HasValue && step.Limit.Value > 0) { args.Add("--limit"); args.Add(step.Limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            return FrostyCLI.Commands.PortCommand.ExecuteImportLayer(args.ToArray(), logger, formatter);
        }

        private int ExecuteExport(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = step.Ebx;
            string outPath = step.Out;
            string format  = step.Format ?? "fbx";

            if (string.IsNullOrEmpty(ebxPath))
            { logger.LogError("export step: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(outPath))
            { logger.LogError("export step: missing 'out' field."); return 1; }
            if (App.AssetManager == null)
            { logger.LogError("export step: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null)
            {
                var f1 = new Dictionary<string, string>();
                f1["file"]  = ebxPath;
                f1["error"] = "EBX entry not found";
                formatter.WriteResult("FAIL", "export", f1);
                return 1;
            }

            var handler = Session.Plugins.GetHandler(entry);
            if (handler == null)
            {
                var f2 = new Dictionary<string, string>();
                f2["file"]  = ebxPath;
                f2["type"]  = entry.Type ?? "";
                f2["error"] = "No handler for type '" + (entry.Type ?? "null") + "'";
                formatter.WriteResult("FAIL", "export", f2);
                return 1;
            }

            string outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                bool ok = handler.Export(entry, outPath, format);
                var f3 = new Dictionary<string, string>();
                f3["file"] = outPath;
                f3["ebx"]  = ebxPath;
                formatter.WriteResult(ok ? "OK" : "FAIL", "export", f3);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                var f4 = new Dictionary<string, string>();
                f4["file"]  = outPath;
                f4["ebx"]   = ebxPath;
                f4["error"] = ex.Message;
                formatter.WriteResult("FAIL", "export", f4);
                return 1;
            }
        }

        private int ExecuteProjectNew(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.IsInitialized)
            {
                logger.LogError("project-new: Session not initialized.");
                return 1;
            }
            Session.Project.New();
            var fields = new Dictionary<string, string>();
            formatter.WriteResult("OK", "project-new", fields);
            return 0;
        }

        private int ExecuteProjectLoad(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File))
            {
                logger.LogError("project-load: missing 'file' field.");
                return 1;
            }
            if (!File.Exists(step.File))
            {
                logger.LogError("project-load: file not found: " + step.File);
                return 1;
            }
            if (!Session.IsInitialized)
            {
                logger.LogError("project-load: Session not initialized.");
                return 1;
            }
            try
            {
                Session.Project.Load(step.File, logger);
                var fields = new Dictionary<string, string>();
                fields["file"] = step.File;
                formatter.WriteResult("OK", "project-load", fields);
                return 0;
            }
            catch (Exception ex)
            {
                var fields = new Dictionary<string, string>();
                fields["file"]  = step.File;
                fields["error"] = ex.Message;
                formatter.WriteResult("FAIL", "project-load", fields);
                return 1;
            }
        }

        private int ExecuteProjectSave(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.Project.IsLoaded)
            {
                logger.LogError("project-save: no project loaded.");
                return 1;
            }
            bool ok = Session.Project.Save(step.File, logger);
            var fields = new Dictionary<string, string>();
            fields["file"] = step.File ?? Session.Project.FilePath ?? "(unknown)";
            formatter.WriteResult(ok ? "OK" : "FAIL", "project-save", fields);
            return ok ? 0 : 1;
        }

        private int ExecuteModBuild(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.Project.IsLoaded)
            {
                logger.LogError("mod-build: no project loaded.");
                return 1;
            }
            if (string.IsNullOrEmpty(step.Out))
            {
                logger.LogError("mod-build: missing 'out' field.");
                return 1;
            }
            if (string.IsNullOrEmpty(step.ModName))
            {
                logger.LogError("mod-build: missing 'mod-name' field.");
                return 1;
            }
            var settings = new FrostyCLI.Core.ModSettings
            {
                Name        = step.ModName,
                Author      = step.ModAuthor,
                Version     = step.ModVersion,
                Description = step.ModDescription
            };
            bool ok = Session.Project.Build(step.Out, settings, logger);
            var fields = new Dictionary<string, string>();
            fields["out"] = step.Out;
            if (!ok) fields["error"] = "Build failed. See log.";
            formatter.WriteResult(ok ? "OK" : "FAIL", "mod-build", fields);
            return ok ? 0 : 1;
        }

        private int ExecuteEbxSet(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("ebx-set: missing 'ebx' field.");   return 1; }
            if (string.IsNullOrEmpty(step.Prop))  { logger.LogError("ebx-set: missing 'prop' field.");  return 1; }
            // 'res' points a ResourceRef field at a resource by name (its RID is assigned per build).
            var setArgs = new List<string> { step.Ebx, "--prop", step.Prop };
            // 'object' targets a partition object (type name or instance GUID) rather than the root.
            if (!string.IsNullOrEmpty(step.Object)) { setArgs.Add("--object"); setArgs.Add(step.Object); }
            if (!string.IsNullOrEmpty(step.EachType)) { setArgs.Add("--each-type"); setArgs.Add(step.EachType); }
            if (!string.IsNullOrEmpty(step.Res))
            { setArgs.Add("--res"); setArgs.Add(step.Res); return FrostyCLI.Core.WriteOpBus.Execute("ebx-set", setArgs.ToArray(), logger, formatter); }
            if (step.Value == null)               { logger.LogError("ebx-set: missing 'value' field."); return 1; }
            setArgs.Add("--value"); setArgs.Add(step.Value.ToString());
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-set", setArgs.ToArray(), logger, formatter);
        }

        private int ExecuteEbxClone(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Source)) { logger.LogError("ebx-clone: missing 'source' field."); return 1; }
            if (string.IsNullOrEmpty(step.AsPath)) { logger.LogError("ebx-clone: missing 'as' field.");     return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-clone",
                new[] { step.Source, "--as", step.AsPath },
                logger, formatter);
        }

        private int ExecuteEbxDelete(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("ebx-delete: missing 'ebx' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-delete", new[] { step.Ebx }, logger, formatter);
        }

        private int ExecuteEbxRevert(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("ebx-revert: missing 'ebx' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-revert", new[] { step.Ebx }, logger, formatter);
        }

        private int ExecuteEbxSetRef(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))    { logger.LogError("ebx-set-ref: missing 'ebx' field.");    return 1; }
            if (string.IsNullOrEmpty(step.Prop))   { logger.LogError("ebx-set-ref: missing 'prop' field.");   return 1; }
            bool nullRef = string.Equals(step.Target, "null", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(step.Target) && string.IsNullOrEmpty(step.TargetObject))
            { logger.LogError("ebx-set-ref: missing 'target' or 'target-object' field (use \"null\" to clear the reference)."); return 1; }
            var list = new List<string> { step.Ebx, "--prop", step.Prop };
            // 'target-object' points at another object in the SAME partition (entity-to-entity
            // connections inside a level); 'target' points at another asset.
            if (!string.IsNullOrEmpty(step.TargetObject)) { list.Add("--target-object"); list.Add(step.TargetObject); }
            else if (nullRef) list.Add("--null");
            else { list.Add("--target"); list.Add(step.Target); }
            if (!string.IsNullOrEmpty(step.ClassGuid)) { list.Add("--class-guid"); list.Add(step.ClassGuid); }
            // 'object' targets a partition object (type name, "TypeName[N]" or instance GUID).
            if (!string.IsNullOrEmpty(step.Object)) { list.Add("--object"); list.Add(step.Object); }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-set-ref", list.ToArray(), logger, formatter);
        }

        // Wiring a logic graph means hundreds of near-identical connections, which is precisely the
        // shape a pipeline is for -- authoring a shrinking-play-area sequence by hand is a hundred
        // separate invocations, each paying the asset-manager startup cost.


        private int ExecuteEbxCreate(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("ebx-create: missing 'ebx' field.");  return 1; }
            if (string.IsNullOrEmpty(step.Type)) { logger.LogError("ebx-create: missing 'type' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-create", new[] { step.Ebx, "--type", step.Type }, logger, formatter);
        }

        // Creates a partition object WITHOUT listing it as a world object -- shape data and
        // other non-EntityData content is held that way in shipped levels.
        private int ExecuteEbxNewObject(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("ebx-new-object: missing 'ebx' field.");  return 1; }
            if (string.IsNullOrEmpty(step.Type)) { logger.LogError("ebx-new-object: missing 'type' field."); return 1; }
            var list = new List<string> { step.Ebx, "--type", step.Type };
            if (!string.IsNullOrEmpty(step.Field)) { list.Add("--id"); list.Add(step.Field); }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-new-object", list.ToArray(), logger, formatter);
        }

        private int ExecuteEbxArrayAdd(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("ebx-array-add: missing 'ebx' field.");  return 1; }
            if (string.IsNullOrEmpty(step.Prop)) { logger.LogError("ebx-array-add: missing 'prop' field."); return 1; }
            var list = new List<string> { step.Ebx, "--prop", step.Prop };
            if (step.Value != null)              { list.Add("--value"); list.Add(step.Value.ToString()); }
            if (!string.IsNullOrEmpty(step.Type)) { list.Add("--type"); list.Add(step.Type); }
            // 'target' (+ optional 'class_guid') appends an EXTERNAL PointerRef instead of a new object.
            if (!string.IsNullOrEmpty(step.Target)) { list.Add("--target"); list.Add(step.Target); }
            // 'target_file_guid' (+ 'class_guid'): external PointerRef by raw guids, target need not be in the project
            if (!string.IsNullOrEmpty(step.TargetFileGuid)) { list.Add("--target-file-guid"); list.Add(step.TargetFileGuid); }
            if (!string.IsNullOrEmpty(step.ClassGuid)) { list.Add("--class-guid"); list.Add(step.ClassGuid); }
            // 'object' resolves the list against a partition sub-object rather than the root. The
            // command has taken --object for a while; the pipeline silently dropped it, so a step
            // targeting a sub-object's list failed with "not found on type '<root type>'".
            if (!string.IsNullOrEmpty(step.Object)) { list.Add("--object"); list.Add(step.Object); }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-array-add", list.ToArray(), logger, formatter);
        }

        private int ExecuteEbxArrayRemove(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("ebx-array-remove: missing 'ebx' field.");  return 1; }
            if (string.IsNullOrEmpty(step.Prop)) { logger.LogError("ebx-array-remove: missing 'prop' field."); return 1; }
            if (!step.Index.HasValue)            { logger.LogError("ebx-array-remove: missing 'index' field."); return 1; }
            var rmArgs = new List<string> { step.Ebx, "--prop", step.Prop, "--index", step.Index.Value.ToString() };
            if (!string.IsNullOrEmpty(step.Object)) { rmArgs.Add("--object"); rmArgs.Add(step.Object); }
            return FrostyCLI.Core.WriteOpBus.Execute("ebx-array-remove", rmArgs.ToArray(), logger, formatter);
        }

        private int ExecuteLevelAdd(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))       { logger.LogError("level-add: missing 'ebx' field.");       return 1; }
            if (string.IsNullOrEmpty(step.Blueprint))  { logger.LogError("level-add: missing 'blueprint' field."); return 1; }
            var list = new List<string> { step.Ebx, "--blueprint", step.Blueprint };
            if (step.Pos   != null) { list.Add("--pos");   list.Add(step.Pos); }
            if (step.Rot   != null) { list.Add("--rot");   list.Add(step.Rot); }
            if (step.Scale != null) { list.Add("--scale"); list.Add(step.Scale); }
            return FrostyCLI.Core.WriteOpBus.Execute("level-add", list.ToArray(), logger, formatter);
        }

        private int ExecuteLevelPlace(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))       { logger.LogError("level-place: missing 'ebx' field.");       return 1; }
            if (string.IsNullOrEmpty(step.Blueprint)) { logger.LogError("level-place: missing 'blueprint' field."); return 1; }
            if (string.IsNullOrEmpty(step.Pos))       { logger.LogError("level-place: missing 'pos' field.");       return 1; }
            if (string.IsNullOrEmpty(step.Group))     { logger.LogError("level-place: missing 'group' field.");     return 1; }
            var list = new List<string> { step.Ebx, "--blueprint", step.Blueprint, "--pos", step.Pos, "--group", step.Group };
            if (!string.IsNullOrEmpty(step.Rot))           { list.Add("--rot");    list.Add(step.Rot); }
            if (!string.IsNullOrEmpty(step.Scale))         { list.Add("--scale");  list.Add(step.Scale); }
            if (!string.IsNullOrEmpty(step.Mesh))          { list.Add("--mesh");   list.Add(step.Mesh); }
            if (!string.IsNullOrEmpty(step.Mvdb))          { list.Add("--mvdb");   list.Add(step.Mvdb); }
            if (!string.IsNullOrEmpty(step.Bundle))        { list.Add("--bundle"); list.Add(step.Bundle); }
            if (!string.IsNullOrEmpty(step.Like))          { list.Add("--like");   list.Add(step.Like); }
            if (!string.IsNullOrEmpty(step.VariationHash)) { list.Add("--variation-hash"); list.Add(step.VariationHash); }
            return FrostyCLI.Core.WriteOpBus.Execute("level-place", list.ToArray(), logger, formatter);
        }

        private int ExecuteLevelImportLights(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("level-import-lights: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.From)) { logger.LogError("level-import-lights: missing 'from' field."); return 1; }

            var list = new List<string> { step.Ebx, "--from", step.From };
            if (step.IntensityScale.HasValue)
            { list.Add("--intensity-scale"); list.Add(step.IntensityScale.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(step.BlueprintMap)) { list.Add("--blueprint-map"); list.Add(step.BlueprintMap); }
            if (!string.IsNullOrEmpty(step.Offset)) { list.Add("--offset"); list.Add(step.Offset); }
            if (step.SkipDisabled) list.Add("--skip-disabled");
            return FrostyCLI.Core.WriteOpBus.Execute("level-import-lights", list.ToArray(), logger, formatter);
        }

        private int ExecuteLevelAddEntity(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("level-add-entity: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.Type)) { logger.LogError("level-add-entity: missing 'type' field."); return 1; }

            var list = new List<string> { step.Ebx, "--type", step.Type };
            if (!string.IsNullOrEmpty(step.Pos))   { list.Add("--pos"); list.Add(step.Pos); }
            if (!string.IsNullOrEmpty(step.Rot))   { list.Add("--rot"); list.Add(step.Rot); }
            if (!string.IsNullOrEmpty(step.Scale)) { list.Add("--scale"); list.Add(step.Scale); }
            if (!string.IsNullOrEmpty(step.TransformField))
            { list.Add("--transform-field"); list.Add(step.TransformField); }
            if (step.Set != null)
            {
                foreach (KeyValuePair<string, JToken> assignment in step.Set)
                {
                    list.Add("--set");
                    list.Add(assignment.Key + "=" + (assignment.Value == null ? "" : assignment.Value.ToString()));
                }
            }
            return FrostyCLI.Core.WriteOpBus.Execute("level-add-entity", list.ToArray(), logger, formatter);
        }

        private int ExecuteLevelAddGroup(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("level-add-group: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.Template)) { logger.LogError("level-add-group: missing 'template' field."); return 1; }
            var list = new List<string> { step.Ebx, "--template", step.Template, "--group", step.TemplateGroup.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (!string.IsNullOrEmpty(step.Pos)) { list.Add("--pos"); list.Add(step.Pos); }
            if (step.KeepPhysics) list.Add("--keep-physics");
            return FrostyCLI.Core.WriteOpBus.Execute("level-add-group", list.ToArray(), logger, formatter);
        }

        private int ExecuteLevelSinkGroup(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))    { logger.LogError("level-sink-group: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.Offset)) { logger.LogError("level-sink-group: missing 'offset' field."); return 1; }
            var list = new List<string> { "sink-group", step.Ebx, "--offset", step.Offset };
            if (!string.IsNullOrEmpty(step.Group)) { list.Add("--group"); list.Add(step.Group); }
            if (!string.IsNullOrEmpty(step.Members)) { list.Add("--members"); list.Add(step.Members); }
            return new FrostyCLI.Commands.LevelCommand().Execute(list.ToArray(), logger, formatter);
        }

        private int ExecuteLevelClear(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("level-clear: missing 'ebx' field."); return 1; }
            var list = new List<string> { step.Ebx };
            if (step.DeleteGroups) list.Add("--delete-groups");
            if (step.Remove) list.Add("--remove");
            if (step.KeepTypes != null) foreach (string t in step.KeepTypes) { list.Add("--keep-type"); list.Add(t); }
            if (step.OnlyTypes != null) foreach (string t in step.OnlyTypes) { list.Add("--only-type"); list.Add(t); }
            return FrostyCLI.Core.WriteOpBus.Execute("level-clear", list.ToArray(), logger, formatter);
        }

        // level-repoint-members / level-repoint-entities / level-transform-group: thin mappings onto the level commands.
        //   {"op":"level-repoint-members","ebx":<asset>,"from_blueprint":..,"mesh":..,"to_blueprint":..?,"group_res":..?}
        //   {"op":"level-repoint-entities","ebx":<asset>,"from_blueprint":..,"to_blueprint":..,"registry":..?}
        //   {"op":"level-transform-group","ebx":<asset>,"group_res":..,"transform":"12 numbers","if_centroid_near":"x,y,z,r"?}
        private int ExecuteLevelRepoint(PipelineStep step, string sub, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError(sub + ": missing 'ebx' field."); return 1; }
            var a = new List<string> { sub, step.Ebx };
            if (sub == "repoint-members")
            {
                // `level repoint-members <level> --from <bp> --to <bp>[@<mesh>]` (feat/hoth-port 43dea76); to_blueprint
                // defaults to from_blueprint, i.e. only the mesh changes
                if (string.IsNullOrEmpty(step.FromBlueprint)) { logger.LogError("level-repoint-members: missing 'from_blueprint'."); return 1; }
                string to = string.IsNullOrEmpty(step.ToBlueprint) ? step.FromBlueprint : step.ToBlueprint;
                a.Add("--from"); a.Add(step.FromBlueprint); a.Add("--to"); a.Add(string.IsNullOrEmpty(step.Mesh) ? to : to + "@" + step.Mesh);
                return new FrostyCLI.Commands.LevelCommand().Execute(a.ToArray(), logger, formatter);
            }
            if (!string.IsNullOrEmpty(step.FromBlueprint)) { a.Add("--from-blueprint"); a.Add(step.FromBlueprint); }
            if (!string.IsNullOrEmpty(step.ToBlueprint)) { a.Add("--to-blueprint"); a.Add(step.ToBlueprint); }
            if (!string.IsNullOrEmpty(step.Mesh)) { a.Add("--mesh"); a.Add(step.Mesh); }
            if (!string.IsNullOrEmpty(step.GroupRes)) { a.Add("--group-res"); a.Add(step.GroupRes); }
            if (!string.IsNullOrEmpty(step.Registry)) { a.Add("--registry"); a.Add(step.Registry); }
            if (!string.IsNullOrEmpty(step.TransformText)) { a.Add("--transform"); a.Add(step.TransformText); }
            if (!string.IsNullOrEmpty(step.IfCentroidNear)) { a.Add("--if-centroid-near"); a.Add(step.IfCentroidNear); }
            return new FrostyCLI.Commands.LevelCommand().Execute(a.ToArray(), logger, formatter);
        }

        private int ExecuteLevelRemove(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))          { logger.LogError("level-remove: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.InstanceGuid)) { logger.LogError("level-remove: missing 'instance_guid' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("level-remove", new[] { step.Ebx, "--guid", step.InstanceGuid }, logger, formatter);
        }

        private int ExecuteLevelMove(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))          { logger.LogError("level-move: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.InstanceGuid)) { logger.LogError("level-move: missing 'instance_guid' field."); return 1; }
            if (string.IsNullOrEmpty(step.Pos))          { logger.LogError("level-move: missing 'pos' field."); return 1; }
            var list = new List<string> { step.Ebx, "--guid", step.InstanceGuid, "--pos", step.Pos };
            if (!string.IsNullOrEmpty(step.Rot))   { list.Add("--rot");   list.Add(step.Rot); }
            if (!string.IsNullOrEmpty(step.Scale)) { list.Add("--scale"); list.Add(step.Scale); }
            return FrostyCLI.Core.WriteOpBus.Execute("level-move", list.ToArray(), logger, formatter);
        }

        // { "op": "level-dupe", "template": "<shipped LevelData>", "as": "<new level>" }
        private int ExecuteLevelDupe(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Template)) { logger.LogError("level-dupe: missing 'template' field (the shipped LevelData)."); return 1; }
            if (string.IsNullOrEmpty(step.AsPath))   { logger.LogError("level-dupe: missing 'as' field (the new level name)."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("level-dupe", new[] { step.Template, "--as", step.AsPath }, logger, formatter);
        }

        private int ExecuteLevelScaffold(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Template)) { logger.LogError("level-scaffold: missing 'template' field."); return 1; }
            if (string.IsNullOrEmpty(step.AsPath))   { logger.LogError("level-scaffold: missing 'as' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("level-scaffold", new[] { step.Template, "--as", step.AsPath }, logger, formatter);
        }

        private int ExecuteConnectionAdd(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))         { logger.LogError("connection-add: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.Source))      { logger.LogError("connection-add: missing 'source' field."); return 1; }
            if (string.IsNullOrEmpty(step.Target))      { logger.LogError("connection-add: missing 'target' field."); return 1; }
            if (string.IsNullOrEmpty(step.SourceField)) { logger.LogError("connection-add: missing 'source_field' field."); return 1; }
            if (string.IsNullOrEmpty(step.TargetField)) { logger.LogError("connection-add: missing 'target_field' field."); return 1; }
            var list = new List<string>
            {
                step.Ebx, "--source", step.Source, "--source-field", step.SourceField,
                "--target", step.Target, "--target-field", step.TargetField
            };
            if (!string.IsNullOrEmpty(step.Kind))  { list.Add("--kind");  list.Add(step.Kind); }
            if (step.Flags.HasValue) { list.Add("--flags"); list.Add(step.Flags.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            // Prefer naming the realm over a raw bitfield: a connection on the wrong realm wires up
            // in the editor and is silently inert in game.
            if (!string.IsNullOrEmpty(step.Realm))        { list.Add("--realm");         list.Add(step.Realm); }
            if (!string.IsNullOrEmpty(step.PropertyType)) { list.Add("--property-type"); list.Add(step.PropertyType); }
            if (step.NonStaticSource)                       list.Add("--non-static-source");
            return FrostyCLI.Core.WriteOpBus.Execute("connection-add", list.ToArray(), logger, formatter);
        }

        private int ExecuteConnectionRemove(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("connection-remove: missing 'ebx' field."); return 1; }
            if (step.Index == null)             { logger.LogError("connection-remove: missing 'index' field."); return 1; }
            var list = new List<string> { step.Ebx, "--index", step.Index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (!string.IsNullOrEmpty(step.Kind)) { list.Add("--kind"); list.Add(step.Kind); }
            return FrostyCLI.Core.WriteOpBus.Execute("connection-remove", list.ToArray(), logger, formatter);
        }

        private int ExecuteMvdbAdd(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("mvdb-add: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.Mesh)) { logger.LogError("mvdb-add: missing 'mesh' field."); return 1; }
            var list = new List<string> { step.Ebx, "--mesh", step.Mesh };
            if (!string.IsNullOrEmpty(step.Like)) { list.Add("--like"); list.Add(step.Like); }
            if (!string.IsNullOrEmpty(step.VariationHash)) { list.Add("--variation-hash"); list.Add(step.VariationHash); }
            return FrostyCLI.Core.WriteOpBus.Execute("mvdb-add", list.ToArray(), logger, formatter);
        }

        private int ExecuteMvdbRemove(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("mvdb-remove: missing 'ebx' field."); return 1; }
            if (string.IsNullOrEmpty(step.Mesh)) { logger.LogError("mvdb-remove: missing 'mesh' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("mvdb-remove", new[] { step.Ebx, "--mesh", step.Mesh }, logger, formatter);
        }

        private int ExecuteLocSet(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Key)) { logger.LogError("loc-set: missing 'key' field."); return 1; }
            if (step.Value == null)             { logger.LogError("loc-set: missing 'value' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("loc-set", new[] { step.Key, "--value", step.Value.ToString() }, logger, formatter);
        }

        private int ExecuteImportTexture(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File))   { logger.LogError("import-texture: missing 'file' field."); return 1; }
            if (string.IsNullOrEmpty(step.AsPath)) { logger.LogError("import-texture: missing 'as' field."); return 1; }
            // ExecuteTexture expects post-subcommand args (no leading "texture"), matching CommandBus.
            var list = new System.Collections.Generic.List<string> { step.File, "--as", step.AsPath };
            if (!string.IsNullOrEmpty(step.Format)) { list.Add("--format"); list.Add(step.Format); }
            if (!string.IsNullOrEmpty(step.Template)) { list.Add("--template"); list.Add(step.Template); }
            if (step.NoMipmaps) list.Add("--no-mipmaps");
            return FrostyCLI.Core.WriteOpBus.Execute("import-texture", list.ToArray(), logger, formatter);
        }

        private int ExecuteImportMesh(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File))   { logger.LogError("import-mesh: missing 'file' field."); return 1; }
            if (string.IsNullOrEmpty(step.AsPath)) { logger.LogError("import-mesh: missing 'as' field."); return 1; }
            var list = new System.Collections.Generic.List<string> { step.File, "--as", step.AsPath };
            if (!string.IsNullOrEmpty(step.Scale)) { list.Add("--scale"); list.Add(step.Scale); }
            return FrostyCLI.Core.WriteOpBus.Execute("import-mesh", list.ToArray(), logger, formatter);
        }

        // Read-only batch counterpart of material-set: reports the parameter slots a mesh's
        // ShaderBlockDepot actually binds. As a pipeline op, many meshes can be dumped under a
        // single project load instead of one process per mesh.
        private int ExecuteMaterialDump(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("material-dump: missing 'ebx' (mesh) field."); return 1; }
            var list = new System.Collections.Generic.List<string> { step.Ebx };
            if (step.Index.HasValue) { list.Add("--material"); list.Add(step.Index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(step.Out))   { list.Add("--out");   list.Add(step.Out); }
            if (!string.IsNullOrEmpty(step.Names)) { list.Add("--names"); list.Add(step.Names); }
            return FrostyCLI.Commands.MaterialCommand.ExecuteDump(list.ToArray(), logger, formatter);
        }

        private int ExecuteMaterialSet(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))  { logger.LogError("material-set: missing 'ebx' (mesh) field."); return 1; }
            if (!step.Index.HasValue)            { logger.LogError("material-set: missing 'index' (material) field."); return 1; }
            if (string.IsNullOrEmpty(step.Prop)) { logger.LogError("material-set: missing 'prop' (param name) field."); return 1; }
            if (step.Value == null)              { logger.LogError("material-set: missing 'value' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("material-set",
                step.AddMissing
                    ? new[] { step.Ebx, "--material", step.Index.Value.ToString(), "--param", step.Prop, "--value", step.Value.ToString(), "--add-missing" }
                    : new[] { step.Ebx, "--material", step.Index.Value.ToString(), "--param", step.Prop, "--value", step.Value.ToString() },
                logger, formatter);
        }

        private int ExecuteMaterialGraft(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx))   { logger.LogError("material-graft: missing 'ebx' (mesh) field."); return 1; }
            if (!step.Index.HasValue)             { logger.LogError("material-graft: missing 'index' (material) field."); return 1; }
            if (string.IsNullOrEmpty(step.Donor)) { logger.LogError("material-graft: missing 'donor' (mesh) field."); return 1; }
            if (!step.DonorMaterial.HasValue)     { logger.LogError("material-graft: missing 'donor_material' field."); return 1; }
            var list = new List<string> { step.Ebx, "--material", step.Index.Value.ToString(), "--donor", step.Donor, "--donor-material", step.DonorMaterial.Value.ToString() };
            if (!string.IsNullOrEmpty(step.Mvdb)) { list.Add("--mvdb"); list.Add(step.Mvdb); }
            return FrostyCLI.Core.WriteOpBus.Execute("material-graft", list.ToArray(), logger, formatter);
        }

        private int ExecuteBundleNew(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Bundle)) { logger.LogError("bundle-new: missing 'bundle' field."); return 1; }
            var newArgs = new List<string> { step.Bundle };
            if (!string.IsNullOrEmpty(step.Type))        { newArgs.Add("--type"); newArgs.Add(step.Type); }
            if (!string.IsNullOrEmpty(step.SuperBundle)) { newArgs.Add("--superbundle"); newArgs.Add(step.SuperBundle); }
            if (!string.IsNullOrEmpty(step.Template))    { newArgs.Add("--like"); newArgs.Add(step.Template); }
            return FrostyCLI.Core.WriteOpBus.Execute("bundle-new", newArgs.ToArray(), logger, formatter);
        }

        private int ExecuteBundleAdd(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string asset = !string.IsNullOrEmpty(step.Ebx) ? step.Ebx
                         : !string.IsNullOrEmpty(step.Res) ? step.Res : step.ChunkId;
            if (string.IsNullOrEmpty(asset))         { logger.LogError("bundle-add: missing 'ebx' (or 'res'/'chunk_id') field."); return 1; }
            if (string.IsNullOrEmpty(step.Bundle))   { logger.LogError("bundle-add: missing 'bundle' field."); return 1; }
            // the field names the table: a res shares its EBX owner's name, and the EBX lookup ignores case
            var addArgs = new List<string> { asset, "--to", step.Bundle, "--kind", BundleStepKind(step) };
            if (step.WithResources) addArgs.Add("--with-resources");
            return FrostyCLI.Core.WriteOpBus.Execute("bundle-add", addArgs.ToArray(), logger, formatter);
        }

        private int ExecuteBundleRemove(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string asset = !string.IsNullOrEmpty(step.Ebx) ? step.Ebx
                         : !string.IsNullOrEmpty(step.Res) ? step.Res : step.ChunkId;
            if (string.IsNullOrEmpty(asset))       { logger.LogError("bundle-remove: missing 'ebx' (or 'res'/'chunk_id') field."); return 1; }
            if (string.IsNullOrEmpty(step.Bundle)) { logger.LogError("bundle-remove: missing 'bundle' field."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("bundle-remove",
                new[] { asset, "--from", step.Bundle, "--kind", BundleStepKind(step) }, logger, formatter);
        }

        // Which table a bundle-add/-remove step names: its `ebx`, `res` or `chunk_id` field.
        internal static string BundleStepKind(PipelineStep step)
        {
            return !string.IsNullOrEmpty(step.Ebx) ? "ebx" : !string.IsNullOrEmpty(step.Res) ? "res" : "chunk";
        }

        private int ExecuteAudio(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string op = step.Op.ToLowerInvariant();
            var args = new List<string> { op.Substring("audio-".Length) };
            string subject = op == "audio-import" || op == "audio-graph-import" || op == "audio-graph-plan" ? step.File : op == "audio-inventory" ? step.Like : step.Ebx;
            if (subject != null) args.Add(subject);
            foreach (var pair in new[] {
                new[] { "out", step.Out }, new[] { "as", step.AsPath }, new[] { "template", step.Template },
                new[] { "bundle", step.Bundle }, new[] { "into", step.Into }, new[] { "pos", step.Pos }, new[] { "map", step.GraphMap }
            }) if (pair[1] != null) { args.Add("--" + pair[0]); args.Add(pair[1]); }
            if (step.PlayOnCreation.HasValue) { args.Add("--play-on-creation"); args.Add(step.PlayOnCreation.Value ? "true" : "false"); }
            return FrostyCLI.Core.WriteOpBus.Execute("audio", args.ToArray(), logger, formatter);
        }

        private int ExecuteImportSound(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File))   { logger.LogError("import-sound: missing 'file' field."); return 1; }
            if (string.IsNullOrEmpty(step.AsPath)) { logger.LogError("import-sound: missing 'as' field."); return 1; }
            var list = new System.Collections.Generic.List<string> { step.File, "--as", step.AsPath };
            if (step.Variation.HasValue) { list.Add("--variation"); list.Add(step.Variation.Value.ToString()); }
            if (step.Flatten) list.Add("--flatten");
            if (string.Equals(step.Encoding, "ealayer3", StringComparison.OrdinalIgnoreCase)) list.Add("--ealayer3");
            else if (string.Equals(step.Encoding, "pcm", StringComparison.OrdinalIgnoreCase)) list.Add("--pcm");
            else if (step.Encoding != null && !string.Equals(step.Encoding, "auto", StringComparison.OrdinalIgnoreCase))
            { logger.LogError("import-sound: encoding must be auto, pcm or ealayer3."); return 1; }
            return FrostyCLI.Core.WriteOpBus.Execute("import-sound", list.ToArray(), logger, formatter);
        }

        // ebx-graph-export: {ebx, out} or {file: list of assets, out: directory}
        // ebx-graph-import: {file: package, as, bundle, strict, out: report}; "unsealed": true accepts a converter-written package
        private int ExecuteEbxGraph(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string op = step.Op.ToLowerInvariant();
            var args = new List<string>();
            if (op == "ebx-graph-export")
            {
                if (!string.IsNullOrEmpty(step.Ebx)) { args.Add(step.Ebx); if (step.Out != null) { args.Add("--out"); args.Add(step.Out); } }
                else if (!string.IsNullOrEmpty(step.File)) { args.Add("--list"); args.Add(step.File); args.Add("--out-dir"); args.Add(step.Out); }
                else { logger.LogError("ebx-graph-export: needs 'ebx' (+ 'out') or 'file' (list) + 'out' (dir)."); return 1; }
                return FrostyCLI.Commands.EbxGraphCommand.Execute("graph-export", args.ToArray(), logger, formatter);
            }
            if (string.IsNullOrEmpty(step.File) || string.IsNullOrEmpty(step.AsPath)) { logger.LogError("ebx-graph-import: needs 'file' and 'as'."); return 1; }
            args.Add(step.File); args.Add("--as"); args.Add(step.AsPath);
            if (step.Bundle != null) { args.Add("--bundle"); args.Add(step.Bundle); }
            if (step.Out != null) { args.Add("--out"); args.Add(step.Out); }
            if (step.Strict) args.Add("--strict");
            if (step.Unsealed) args.Add("--allow-unsealed");
            return FrostyCLI.Commands.EbxGraphCommand.Execute(step.Bundle != null ? "graph-import" : "graph-plan", args.ToArray(), logger, formatter);
        }

        private int ExecuteEbxDump(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("ebx-dump: missing 'ebx' field."); return 1; }
            var argList = new List<string> { step.Ebx };
            if (!string.IsNullOrEmpty(step.Out)) { argList.Add("--out"); argList.Add(step.Out); }
            if (step.NoResolve) argList.Add("--no-resolve");
            if (step.Deep) argList.Add("--deep");
            if (!string.IsNullOrEmpty(step.Object)) { argList.Add("--object"); argList.Add(step.Object); }
            return FrostyCLI.Commands.EbxGetCommand.ExecuteDump(argList.ToArray(), logger, formatter);
        }

        private int ExecuteEbxSearch(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Prop))  { logger.LogError("ebx-search: missing 'prop' field.");  return 1; }
            if (step.Value == null)               { logger.LogError("ebx-search: missing 'value' field."); return 1; }
            var argList = new List<string> { "--prop", step.Prop, "--value", step.Value.ToString() };
            if (!string.IsNullOrEmpty(step.Type))  { argList.Add("--type");  argList.Add(step.Type); }
            if (!string.IsNullOrEmpty(step.Out))   { argList.Add("--out");   argList.Add(step.Out); }
            if (step.Regex)                          argList.Add("--regex");
            if (step.Limit.HasValue)               { argList.Add("--limit"); argList.Add(step.Limit.Value.ToString()); }
            return FrostyCLI.Commands.EbxGetCommand.ExecuteSearch(argList.ToArray(), logger, formatter);
        }

        private int ExecuteEbxRefs(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            var argList = new List<string>();
            if (!string.IsNullOrEmpty(step.Ebx))  argList.Add(step.Ebx);
            if (!string.IsNullOrEmpty(step.Guid)) { argList.Add("--guid");  argList.Add(step.Guid); }
            if (!string.IsNullOrEmpty(step.Out))  { argList.Add("--out");   argList.Add(step.Out); }
            if (step.Depth.HasValue)              { argList.Add("--depth"); argList.Add(step.Depth.Value.ToString()); }
            if (step.Limit.HasValue)              { argList.Add("--limit"); argList.Add(step.Limit.Value.ToString()); }
            return FrostyCLI.Commands.EbxGetCommand.ExecuteRefs(argList.ToArray(), logger, formatter);
        }

        // port-export: `port export <blueprint> --out <dir> [--mvdb <db>]... [--as-rigid]` inside the pipeline, so a
        // level's worth of packages exports in one process instead of one game load per blueprint. 'mvdb' may list
        // several databases separated by commas.
        private int ExecutePortExport(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string blueprint = step.Blueprint ?? step.Ebx;
            if (string.IsNullOrEmpty(blueprint)) { logger.LogError("port-export: missing 'blueprint' (or 'ebx') field."); return 1; }
            if (string.IsNullOrEmpty(step.Out))  { logger.LogError("port-export: missing 'out' field."); return 1; }
            var list = new System.Collections.Generic.List<string> { blueprint, "--out", step.Out };
            if (!string.IsNullOrEmpty(step.Mvdb))
                foreach (string db in step.Mvdb.Split(','))
                    if (db.Trim().Length > 0) { list.Add("--mvdb"); list.Add(db.Trim()); }
            if (step.AsRigid) list.Add("--as-rigid");
            if (step.AllParts) list.Add("--all-parts");
            return FrostyCLI.Commands.PortCommand.ExecuteExport(list.ToArray(), logger, formatter);
        }

        private int ExecuteResExport(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Res)) { logger.LogError("res-export: missing 'res' field."); return 1; }
            if (string.IsNullOrEmpty(step.Out)) { logger.LogError("res-export: missing 'out' field."); return 1; }
            // "framed": true writes the 16-byte res meta first, for a later res-import with "framed": true
            return new FrostyCLI.Commands.ResCommand().Execute(
                step.Framed ? new[] { "export", step.Res, "--out", step.Out, "--framed" }
                            : new[] { "export", step.Res, "--out", step.Out }, logger, formatter);
        }

        private int ExecuteChunkExport(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.ChunkId)) { logger.LogError("chunk-export: missing 'chunk_id' field."); return 1; }
            if (string.IsNullOrEmpty(step.Out))     { logger.LogError("chunk-export: missing 'out' field.");      return 1; }
            return new FrostyCLI.Commands.ChunkCommand().Execute(
                new[] { "export", step.ChunkId, "--out", step.Out }, logger, formatter);
        }

        private int ExecuteResImport(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File)) { logger.LogError("res-import: missing 'file' field."); return 1; }
            string res = step.Res ?? step.AsPath;
            if (string.IsNullOrEmpty(res)) { logger.LogError("res-import: missing 'res' (or 'as') field."); return 1; }
            var list = new System.Collections.Generic.List<string> { "import", step.File, "--as", res };
            if (!string.IsNullOrEmpty(step.Type)) { list.Add("--type"); list.Add(step.Type); }
            if (!string.IsNullOrEmpty(step.Donor)) { list.Add("--meta-from"); list.Add(step.Donor); }
            // The Enlighten DBs carry their own 16-byte res meta in front of the payload; without
            // --framed the old meta is kept and the game faults at game+0x5498867 on load.
            if (step.Framed) list.Add("--framed");
            return new FrostyCLI.Commands.ResCommand().Execute(list.ToArray(), logger, formatter);
        }

        // terrain-retarget: overwrite a donor terrain's elevations from another terrain (or a flat
        // height). 'terrain' names the donor, 'source' the terrain to sample -- as a res in the
        // loaded game, or as a payload file, which is how a Battlefront (2015) terrain gets in.
        private int ExecuteTerrainRetarget(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string terrain = step.Terrain ?? step.Res ?? step.AsPath;
            if (string.IsNullOrEmpty(terrain)) { logger.LogError("terrain-retarget: missing 'terrain' field."); return 1; }
            if (string.IsNullOrEmpty(step.Source) && string.IsNullOrEmpty(step.Flat))
            { logger.LogError("terrain-retarget: missing 'source' field (or 'flat')."); return 1; }

            var list = new System.Collections.Generic.List<string> { "retarget", terrain };
            if (!string.IsNullOrEmpty(step.Source)) { list.Add("--source"); list.Add(step.Source); }
            if (!string.IsNullOrEmpty(step.Flat)) { list.Add("--flat"); list.Add(step.Flat); }
            if (!string.IsNullOrEmpty(step.Band)) { list.Add("--band"); list.Add(step.Band); }
            if (!string.IsNullOrEmpty(step.Offset)) { list.Add("--offset"); list.Add(step.Offset); }
            if (!string.IsNullOrEmpty(step.MaskFrom)) { list.Add("--mask-from"); list.Add(step.MaskFrom); }
            if (step.HoleLayer.HasValue) { list.Add("--hole-layer"); list.Add(step.HoleLayer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(step.HoleDilate)) { list.Add("--hole-dilate"); list.Add(step.HoleDilate); }
            if (step.KeepDerived) list.Add("--keep-derived");
            if (!string.IsNullOrEmpty(step.Out)) { list.Add("--out"); list.Add(step.Out); }
            return new FrostyCLI.Commands.TerrainCommand().Execute(list.ToArray(), logger, formatter);
        }

        private int ExecuteTerrainImportHeights(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string terrain = step.Terrain ?? step.Res ?? step.AsPath;
            if (string.IsNullOrEmpty(terrain)) { logger.LogError("terrain-import-heights: missing 'terrain' field."); return 1; }
            string from = step.From ?? step.File;
            if (string.IsNullOrEmpty(from)) { logger.LogError("terrain-import-heights: missing 'from' field."); return 1; }

            var list = new System.Collections.Generic.List<string> { "import-heights", terrain, "--from", from };
            if (!string.IsNullOrEmpty(step.Band)) { list.Add("--band"); list.Add(step.Band); }
            if (step.KeepDerived) list.Add("--keep-derived");
            if (!string.IsNullOrEmpty(step.Out)) { list.Add("--out"); list.Add(step.Out); }
            return new FrostyCLI.Commands.TerrainCommand().Execute(list.ToArray(), logger, formatter);
        }

        private int ExecuteTerrainExportHeights(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string terrain = step.Terrain ?? step.Res ?? step.Ebx;
            if (string.IsNullOrEmpty(terrain)) { logger.LogError("terrain-export-heights: missing 'terrain' field."); return 1; }
            if (string.IsNullOrEmpty(step.Out)) { logger.LogError("terrain-export-heights: missing 'out' field."); return 1; }

            var list = new System.Collections.Generic.List<string> { "export-heights", terrain, "--out", step.Out };
            if (step.Size.HasValue) { list.Add("--size"); list.Add(step.Size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            return new FrostyCLI.Commands.TerrainCommand().Execute(list.ToArray(), logger, formatter);
        }

        // terrain-create: a new terrain from a heightmap (+ layers.json, colour map, holes) -- `terrain create`.
        // { "op": "terrain-create", "heights": "h.r32", "host": "levels/mp/tatooine_01/terrain/terrain",
        //   "layers": "layers.json", "as": "levels/mp/tatooine_01/terrain/terrain.streamingtree_win32" }
        private int ExecuteTerrainCreate(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            string heights = step.Heights ?? step.File ?? step.From;
            if (string.IsNullOrEmpty(heights)) { logger.LogError("terrain-create: missing 'heights' field."); return 1; }
            if (string.IsNullOrEmpty(step.Out) && string.IsNullOrEmpty(step.AsPath))
            { logger.LogError("terrain-create: give 'out' (a payload file) and/or 'as' (a res name)."); return 1; }
            var list = new System.Collections.Generic.List<string> { "create", "--heights", heights };
            void Opt(string flag, string value) { if (!string.IsNullOrEmpty(value)) { list.Add(flag); list.Add(value); } }
            Opt("--host", step.Host ?? step.Terrain);
            Opt("--layers", step.Layers);
            Opt("--colormap", step.Colormap);
            Opt("--color", step.Color);
            Opt("--holes", step.Holes);
            Opt("--hole-below", step.HoleBelow);
            Opt("--size", step.WorldSize);
            Opt("--center", step.Center);
            Opt("--origin", step.Origin);
            Opt("--height-range", step.HeightRange ?? step.Band);
            Opt("--rows", step.Rows);
            Opt("--spacing", step.Spacing);
            Opt("--world-y", step.WorldY);
            Opt("--tolerance", step.Tolerance);
            if (step.Budget.HasValue) Opt("--budget", step.Budget.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (step.NoAlign) list.Add("--no-align");
            if (step.ClearDecals) list.Add("--clear-decals");
            Opt("--report", step.Report);
            Opt("--out", step.Out);
            Opt("--as", step.AsPath);
            if (step.Args != null) list.AddRange(step.Args);
            return new FrostyCLI.Commands.TerrainCommand().Execute(list.ToArray(), logger, formatter);
        }

        // { "op": "navmesh-build", "ebx": "<level>", "args": ["--region", "x0,z0,x1,z1", "--collision", ...] } -- the options
        // of `frostycli navmesh build` / `navmesh geometry`, so a pipeline can rebuild a level's navmesh from whatever
        // geometry the project holds at that step
        private int ExecuteNavmesh(PipelineStep step, string sub, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.Ebx)) { logger.LogError("navmesh-" + sub + ": missing 'ebx' field (the level)."); return 1; }
            var list = new System.Collections.Generic.List<string> { sub, step.Ebx };
            if (step.Args != null) list.AddRange(step.Args);
            return new FrostyCLI.Commands.NavmeshCommand().Execute(list.ToArray(), logger, formatter);
        }

        private int ExecuteChunkImport(PipelineStep step, ExportLogger logger, OutputFormatter formatter)
        {
            if (string.IsNullOrEmpty(step.File)) { logger.LogError("chunk-import: missing 'file' field."); return 1; }
            if (string.IsNullOrEmpty(step.ChunkId) && !step.New)
            { logger.LogError("chunk-import: missing 'chunk_id' field (or set 'new': true to create one)."); return 1; }
            var list = new System.Collections.Generic.List<string> { "import", step.File };
            if (step.New) list.Add("--new");
            if (!string.IsNullOrEmpty(step.ChunkId)) { list.Add("--id"); list.Add(step.ChunkId); }
            return new FrostyCLI.Commands.ChunkCommand().Execute(list.ToArray(), logger, formatter);
        }
    }
}
