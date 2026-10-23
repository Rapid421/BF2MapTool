using System;
using System.Collections.Generic;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // Material/shader-parameter editing for a mesh's ShaderBlockDepot (the Frosty GUI mesh editor's
    // material-param behavior, headless). `material set <mesh> --material <idx> --param <name>
    // --value <v>` sets an existing shader parameter so the game renders the new value.
    public class MaterialCommand : ICommand
    {
        public string Name => "material";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli material set <mesh> --material <idx> --param <name> --value <v> [--add-missing]"); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "set": return ExecuteSet(Sub(args), logger, formatter);
                case "graft": return ExecuteGraft(Sub(args), logger, formatter);
                case "dump": return ExecuteDump(Sub(args), logger, formatter);
                default:
                    Console.Error.WriteLine("material: unknown subcommand '" + args[0] + "'. Use set, graft or dump.");
                    return 1;
            }
        }

        private static string[] Sub(string[] args) { var r = new string[args.Length - 1]; Array.Copy(args, 1, r, 0, r.Length); return r; }

        // material graft <mesh> --material <i> --donor <mesh> --donor-material <j> [--mvdb <db>]
        public static int ExecuteGraft(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mesh = null, donor = null, mvdb = null; int mi = -1, dj = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--material" && i + 1 < args.Length) int.TryParse(args[++i], out mi);
                else if (args[i] == "--donor" && i + 1 < args.Length) donor = args[++i];
                else if (args[i] == "--donor-material" && i + 1 < args.Length) int.TryParse(args[++i], out dj);
                else if (args[i] == "--mvdb" && i + 1 < args.Length) mvdb = args[++i];
                else if (!args[i].StartsWith("--")) mesh = args[i];
            }
            if (mesh == null || donor == null || mi < 0 || dj < 0) { Console.Error.WriteLine("Usage: frostycli material graft <mesh> --material <i> --donor <mesh> --donor-material <j> [--mvdb <db>]"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("material graft: no project loaded."); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "graft material {0} of {1} from {2}[{3}]", mi, mesh, donor, dj); return 0; }
            var info = new Dictionary<string, string> { { "mesh", mesh }, { "material", mi.ToString() }, { "donor", donor + "[" + dj + "]" } };
            string err = MaterialGraft.Execute(mesh, mi, donor, dj, mvdb, logger, info);
            if (err != null) { Console.Error.WriteLine("material graft: " + err); return 1; }
            formatter.WriteResult("OK", "material graft", info);
            return 0;
        }

        // material dump <mesh> [--material <i>] [--names a,b,c] [--out <file.json>]
        // Read-only: reports every parameter slot the mesh's shader block depots bind, so a caller can
        // see the CURRENT texture ids without mutating anything.
        public static int ExecuteDump(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mesh = null, outFile = null; int matIdx = -1;
            List<string> names = new List<string>();
            string variation = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--material" && i + 1 < args.Length) int.TryParse(args[++i], out matIdx);
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--names" && i + 1 < args.Length) names.AddRange(args[++i].Split(','));
                else if (args[i] == "--variation" && i + 1 < args.Length) variation = args[++i];
                else if (!args[i].StartsWith("--")) mesh = args[i];
            }
            if (mesh == null) { Console.Error.WriteLine("Usage: frostycli material dump <mesh> [--material <i>] [--names a,b,c] [--variation <ObjectVariation>] [--out <f.json>]"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("material dump: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(mesh);
            if (entry == null) { Console.Error.WriteLine("material dump: mesh not found: " + mesh); return 1; }

            string json;
            string err = FrostyCLI.Adapters.Mesh.MaterialParamDumper.Dump(entry, matIdx, names, variation, logger, out json);
            if (err != null) { Console.Error.WriteLine("material dump: " + err); return 1; }

            if (outFile != null)
            {
                string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outFile));
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(outFile, json);
                formatter.WriteResult("OK", "material dump", new Dictionary<string, string>
                { { "mesh", mesh }, { "out", outFile } });
            }
            else Console.WriteLine(json);
            return 0;
        }

        public static int ExecuteSet(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string mesh = null, param = null, value = null; int matIdx = -1; bool haveIdx = false;
            // --add-missing creates the parameter when the material's block lacks it (a grafted
            // material only carries the parameters its donor happened to override).
            bool addMissing = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--material" && i + 1 < args.Length) haveIdx = int.TryParse(args[++i], out matIdx);
                else if (args[i] == "--param" && i + 1 < args.Length) param = args[++i];
                else if (args[i] == "--value" && i + 1 < args.Length) value = args[++i];
                else if (args[i] == "--add-missing") addMissing = true;
                else if (!args[i].StartsWith("--")) mesh = args[i];
            }
            if (mesh == null)        { Console.Error.WriteLine("material set: missing <mesh-ebx>"); return 1; }
            if (!haveIdx || matIdx < 0) { Console.Error.WriteLine("material set: missing/invalid --material <index>"); return 1; }
            if (param == null)       { Console.Error.WriteLine("material set: missing --param <name>"); return 1; }
            if (value == null)       { Console.Error.WriteLine("material set: missing --value <v>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("material set: no project loaded. Run 'project new' or 'project load' first."); return 1; }
            if (App.AssetManager == null)  { Console.Error.WriteLine("material set: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(mesh);
            if (entry == null) { Console.Error.WriteLine("material set: mesh not found: " + mesh); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "set material {0} param {1} = {2} on {3}", matIdx, param, value, mesh); return 0; }

            int sections;
            string err = FrostyCLI.Adapters.Mesh.MaterialParamEditor.SetParam(entry, matIdx, param, value, logger, addMissing, out sections);
            if (err != null) { Console.Error.WriteLine("material set: " + err); return 1; }

            formatter.WriteResult("OK", "material set", new Dictionary<string, string>
            { { "mesh", mesh }, { "material", matIdx.ToString() }, { "param", param }, { "sections", sections.ToString() } });
            return 0;
        }
    }
}
