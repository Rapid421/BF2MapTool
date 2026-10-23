using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public class ExportCommand : ICommand
    {
        public string Name => "export";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null;
            string outPath = null;
            string format  = "fbx";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length)
                    outPath = args[++i];
                else if (args[i] == "--format" && i + 1 < args.Length)
                    format = args[++i];
                else if (!args[i].StartsWith("--"))
                    ebxPath = args[i];
            }

            if (ebxPath == null)
            {
                Console.Error.WriteLine("export: missing <ebx-path>");
                return 1;
            }
            if (outPath == null)
            {
                Console.Error.WriteLine("export: missing --out <file>");
                return 1;
            }
            if (App.AssetManager == null)
            {
                Console.Error.WriteLine("export: AssetManager not initialized. Pass --game-path.");
                return 1;
            }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null)
            {
                var failFields1 = new Dictionary<string, string>();
                failFields1["file"]  = ebxPath;
                failFields1["error"] = "EBX entry not found";
                formatter.WriteResult("FAIL", "export", failFields1);
                return 1;
            }

            var handler = Session.Plugins.GetHandler(entry);
            if (handler == null)
            {
                var failFields2 = new Dictionary<string, string>();
                failFields2["file"]  = ebxPath;
                failFields2["type"]  = entry.Type ?? "";
                failFields2["error"] = "No handler registered for type '" + (entry.Type ?? "null") + "'";
                formatter.WriteResult("FAIL", "export", failFields2);
                return 1;
            }

            string outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            try
            {
                // Export(EbxAssetEntry, string path, string ext) -> bool
                bool ok = handler.Export(entry, outPath, format);
                if (ok)
                {
                    var okFields = new Dictionary<string, string>();
                    okFields["file"] = outPath;
                    okFields["ebx"]  = ebxPath;
                    formatter.WriteResult("OK", "export", okFields);
                    return 0;
                }
                else
                {
                    var failFields3 = new Dictionary<string, string>();
                    failFields3["file"]  = outPath;
                    failFields3["ebx"]   = ebxPath;
                    failFields3["error"] = "handler.Export returned false";
                    formatter.WriteResult("FAIL", "export", failFields3);
                    return 1;
                }
            }
            catch (Exception ex)
            {
                var failFields4 = new Dictionary<string, string>();
                failFields4["file"]  = outPath;
                failFields4["ebx"]   = ebxPath;
                failFields4["error"] = ex.Message;
                formatter.WriteResult("FAIL", "export", failFields4);
                logger.LogError("Export exception: {0}", ex);
                return 1;
            }
        }
    }
}
