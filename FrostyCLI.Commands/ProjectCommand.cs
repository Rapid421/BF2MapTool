using System;
using System.Collections.Generic;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public class ProjectCommand : ICommand
    {
        public string Name { get { return "project"; } }

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length == 0)
            {
                logger.LogError("Usage: project <new|load|save> [path]");
                return 2;
            }

            string sub = args[0].ToLowerInvariant();

            switch (sub)
            {
                case "new":
                    return ExecuteNew(logger, formatter);
                case "load":
                    return ExecuteLoad(args, logger, formatter);
                case "save":
                    return ExecuteSave(args, logger, formatter);
                case "manifest":
                case "export-delta":
                case "apply-delta":
                    return ExecuteDelta(sub, args, logger, formatter);
                default:
                    logger.LogError("Unknown project subcommand '" + args[0] + "'. Use: new, load, save, manifest, export-delta, apply-delta");
                    return 2;
            }
        }

        private int ExecuteNew(ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.IsInitialized)
            {
                logger.LogError("Session not initialized. Provide --game-path.");
                return 2;
            }
            Session.Project.New();
            var fields = new Dictionary<string, string>();
            formatter.WriteResult("OK", "project new", fields);
            return 0;
        }

        private int ExecuteLoad(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 2 || string.IsNullOrEmpty(args[1]))
            {
                logger.LogError("Usage: project load <file.fbproject>");
                return 2;
            }
            string path = args[1];
            if (!System.IO.File.Exists(path))
            {
                logger.LogError("Project file not found: " + path);
                return 2;
            }
            if (!Session.IsInitialized)
            {
                logger.LogError("Session not initialized. Provide --game-path.");
                return 2;
            }
            try
            {
                Session.Project.Load(path, logger);
                var fields = new Dictionary<string, string>();
                fields["file"] = path;
                formatter.WriteResult("OK", "project load", fields);
                return 0;
            }
            catch (Exception ex)
            {
                var fields = new Dictionary<string, string>();
                fields["file"]  = path;
                fields["error"] = ex.Message;
                formatter.WriteResult("FAIL", "project load", fields);
                return 1;
            }
        }

        private int ExecuteSave(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.Project.IsLoaded)
            {
                logger.LogError("No project loaded. Run 'project load' or 'project new' first.");
                return 2;
            }
            string path = null;
            bool allowIncomplete = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--allow-incomplete") allowIncomplete = true;
                else if (!args[i].StartsWith("--")) path = args[i];
            }
            if (DryRun.Active) { DryRun.Report(logger, "save project to {0}", path ?? Session.Project.FilePath ?? "(loaded file)"); return 0; }
            bool ok = Session.Project.Save(path, logger, allowIncomplete);
            var fields = new Dictionary<string, string>();
            fields["file"] = path ?? Session.Project.FilePath ?? "(unknown)";
            formatter.WriteResult(ok ? "OK" : "FAIL", "project save", fields);
            return ok ? 0 : 1;
        }
    

        // Carry one project's change into another (FrostyCLI.Core.ProjectDelta):
        //   project manifest --out <dir>                                   (on the common base: hashes + bytes)
        //   project export-delta --base <base dir> --out <dir>             (on theirs: what differs from base)
        //   project apply-delta <delta dir> --base <base dir>              (on the target; then save)
        private int ExecuteDelta(string sub, string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.Project.IsLoaded) { logger.LogError("project " + sub + ": no project loaded (use --project)."); return 2; }
            string outDir = null, baseDir = null, deltaDir = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (args[i] == "--base" && i + 1 < args.Length) baseDir = args[++i];
                else if (!args[i].StartsWith("--")) deltaDir = args[i];
            }
            var am = Frosty.Core.App.AssetManager;
            Action<string> log = s => logger.Log("project " + sub + ": " + s);
            var fields = new Dictionary<string, string>();
            try
            {
                if (sub == "manifest")
                {
                    if (outDir == null) { logger.LogError("project manifest --out <dir>"); return 2; }
                    var m = ProjectDelta.Manifest(am, outDir, log);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "manifest.json"), m.ToString());
                    fields["ebx"] = ((Newtonsoft.Json.Linq.JObject)m["ebx"]).Count.ToString();
                    fields["res"] = ((Newtonsoft.Json.Linq.JObject)m["res"]).Count.ToString();
                    fields["chunks"] = ((Newtonsoft.Json.Linq.JObject)m["chunks"]).Count.ToString();
                }
                else if (sub == "export-delta")
                {
                    if (outDir == null || baseDir == null) { logger.LogError("project export-delta --base <base dir> --out <dir>"); return 2; }
                    var bm = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(baseDir, "manifest.json")));
                    var d = ProjectDelta.Export(am, bm, outDir, log);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "delta.json"), d.ToString());
                    fields["ebx"] = ((Newtonsoft.Json.Linq.JObject)d["ebx"]).Count.ToString();
                    fields["res"] = ((Newtonsoft.Json.Linq.JObject)d["res"]).Count.ToString();
                    fields["chunks"] = ((Newtonsoft.Json.Linq.JObject)d["chunks"]).Count.ToString();
                    fields["bundles"] = ((Newtonsoft.Json.Linq.JArray)d["bundles"]).Count.ToString();
                }
                else
                {
                    if (deltaDir == null || baseDir == null) { logger.LogError("project apply-delta <delta dir> --base <base dir>"); return 2; }
                    var bm = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(baseDir, "manifest.json")));
                    var d = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(deltaDir, "delta.json")));
                    var r = ProjectDelta.Apply(am, deltaDir, d, bm, baseDir, log);
                    foreach (string c in r.Conflicts) logger.LogWarning("project apply-delta: CONFLICT " + c);
                    foreach (string n in r.Notes) logger.LogWarning("project apply-delta: " + n);
                    fields["ebxAdded"] = r.EbxAdded.ToString(); fields["ebxReplaced"] = r.EbxReplaced.ToString();
                    fields["ebxMerged"] = r.EbxMerged.ToString(); fields["objectsAdded"] = r.ObjectsAdded.ToString();
                    fields["objectsReplaced"] = r.ObjectsReplaced.ToString(); fields["conflicts"] = r.Conflicts.Count.ToString();
                    fields["resAdded"] = r.ResAdded.ToString(); fields["resReplaced"] = r.ResReplaced.ToString();
                    fields["chunksAdded"] = r.ChunksAdded.ToString(); fields["chunksReplaced"] = r.ChunksReplaced.ToString();
                    fields["bundlesAdded"] = r.BundlesAdded.ToString(); fields["bundleLinks"] = r.BundleLinks.ToString();
                }
            }
            catch (Exception ex)
            {
                logger.LogError("project " + sub + ": " + (ex.InnerException ?? ex).Message);
                if (Environment.GetEnvironmentVariable("FROSTYCLI_TRACE") == "1") Console.Error.WriteLine(ex.ToString());
                return 1;
            }
            formatter.WriteResult("OK", "project " + sub, fields);
            return 0;
        }
    }
}
