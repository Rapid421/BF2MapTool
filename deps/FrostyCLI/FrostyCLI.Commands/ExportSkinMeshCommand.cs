using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // Exports a SkinnedMeshAsset's LOD0 to a flat .faemesh (skin weights + bone names + albedo path)
    // for FAAE. Reuses the mesh handler via the "faemesh" filter token.
    public class ExportSkinMeshCommand : ICommand
    {
        public string Name => "export-skinmesh";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null, outPath = null, skeleton = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "--skeleton" && i + 1 < args.Length) skeleton = args[++i];
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }

            if (ebxPath == null) { Console.Error.WriteLine("export-skinmesh: missing <ebx-path>"); return 1; }
            if (outPath == null) { Console.Error.WriteLine("export-skinmesh: missing --out <file.faemesh>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("export-skinmesh: AssetManager not initialized. Pass --game-path."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null)
            {
                Fail(formatter, outPath, ebxPath, "EBX entry not found");
                return 1;
            }
            var handler = Session.Plugins.GetHandler(entry);
            if (handler == null)
            {
                Fail(formatter, outPath, ebxPath, "No handler for type '" + (entry.Type ?? "null") + "'");
                return 1;
            }

            try
            {
                // Thread an explicit skeleton (the mesh's real rig) through the handler's filter token;
                // without it the faemesh export defaults to Walrus_HumanMale (248 bones) for every mesh.
                string filter = string.IsNullOrEmpty(skeleton) ? "faemesh" : "faemesh|" + skeleton;
                bool ok = handler.Export(entry, outPath, filter);
                if (ok && File.Exists(outPath))
                {
                    var f = new Dictionary<string, string>();
                    f["file"] = outPath; f["ebx"] = ebxPath; f["bytes"] = new FileInfo(outPath).Length.ToString();
                    formatter.WriteResult("OK", "export-skinmesh", f);
                    return 0;
                }
                Fail(formatter, outPath, ebxPath, "handler returned false or no file written");
                return 1;
            }
            catch (Exception ex)
            {
                Fail(formatter, outPath, ebxPath, ex.Message);
                logger.LogError("export-skinmesh exception: {0}", ex);
                return 1;
            }
        }

        private static void Fail(OutputFormatter formatter, string file, string ebx, string error)
        {
            var f = new Dictionary<string, string>();
            f["file"] = file ?? ""; f["ebx"] = ebx ?? ""; f["error"] = error;
            formatter.WriteResult("FAIL", "export-skinmesh", f);
        }
    }
}
