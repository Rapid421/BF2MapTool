using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public class ExportBatchCommand : ICommand
    {
        public string Name => "export-batch";

        // Default mesh types matched during enumeration when --type is not supplied.
        // This set is checked together with the SWBF1 _mesh-suffix fallback so that
        // SWBF1 assets (which have Type == "") are included without being explicitly named.
        private static readonly HashSet<string> DefaultMeshTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "RigidMeshAsset", "SkinnedMeshAsset", "CompositeMeshAsset" };

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string   filter       = null;
            string   outDir       = null;
            string   format       = "fbx";
            string   onError      = "continue";
            bool     quiet        = false;
            string[] explicitTypes = null; // null = use DefaultMeshTypes + SWBF1 fallback

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--filter":   filter        = args[++i]; break;
                    case "--type":     explicitTypes = args[++i].Split(','); break;
                    case "--out":      outDir        = args[++i]; break;
                    case "--format":   format        = args[++i]; break;
                    case "--on-error": onError       = args[++i]; break;
                    case "--quiet":    quiet         = true;       break;
                }
            }

            if (outDir == null)
            {
                Console.Error.WriteLine("export-batch: missing required --out <dir>");
                return 1;
            }

            if (App.AssetManager == null)
            {
                Console.Error.WriteLine(
                    "export-batch: AssetManager not initialized. Pass --game-path.");
                return 1;
            }

            bool failOnError     = string.Equals(onError, "fail",
                StringComparison.OrdinalIgnoreCase);
            bool useDefaultTypes = explicitTypes == null;

            int passed = 0, failed = 0, skipped = 0;

            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                // ── Prefix filter ────────────────────────────────────────
                if (filter != null
                    && !entry.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                // ── Type filter ──────────────────────────────────────────
                if (useDefaultTypes)
                {
                    bool isKnownMeshType = DefaultMeshTypes.Contains(entry.Type ?? "");
                    // SWBF1: manifest leaves Type empty; mesh EBX have a _mesh name suffix
                    bool isSwbf1Mesh = string.IsNullOrEmpty(entry.Type)
                        && entry.Name.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase);
                    if (!isKnownMeshType && !isSwbf1Mesh) continue;
                }
                else
                {
                    bool matched = false;
                    foreach (string t in explicitTypes)
                        if (string.Equals(entry.Type, t.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                        { matched = true; break; }
                    if (!matched) continue;
                }

                // ── Handler lookup ───────────────────────────────────────
                var handler = Session.Plugins.GetHandler(entry);
                if (handler == null)
                {
                    if (!quiet)
                        Console.WriteLine("  SKIP (no handler) " + entry.Name);
                    skipped++;
                    continue;
                }

                // ── Export ───────────────────────────────────────────────
                string outPath   = BuildOutputPath(outDir, entry.Name, format);
                string outParent = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(outParent) && !Directory.Exists(outParent))
                    Directory.CreateDirectory(outParent);

                try
                {
                    bool ok = handler.Export(entry, outPath, format);
                    if (ok && File.Exists(outPath))
                    {
                        if (!quiet) Console.WriteLine("  PASS " + entry.Name);
                        passed++;
                    }
                    else
                    {
                        if (!quiet)
                            Console.WriteLine("  FAIL " + entry.Name
                                + " (handler returned false or file missing)");
                        failed++;
                        if (failOnError) return 1;
                    }
                }
                catch (Exception ex)
                {
                    if (!quiet)
                        Console.WriteLine("  FAIL " + entry.Name + ": " + ex.Message + (Environment.GetEnvironmentVariable("FROSTYCLI_TRACE") == "1" ? Environment.NewLine + ex.ToString() : ""));
                    failed++;
                    if (failOnError) return 1;
                }
            }

            Console.WriteLine("Export complete: " + passed + " passed, "
                + failed + " failed, " + skipped + " skipped");
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// Converts an EBX asset name to an output file path.
        /// Forward and back slashes in ebxName become OS directory separators.
        /// Example: ("C:\out", "xp2/objects/foo_mesh", "fbx")
        ///       -> "C:\out\xp2\objects\foo_mesh.fbx"
        /// </summary>
        public static string BuildOutputPath(string outDir, string ebxName, string format)
        {
            string normalized = ebxName
                .Replace('/',  Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            return Path.Combine(outDir, normalized + "." + format);
        }
    }
}
