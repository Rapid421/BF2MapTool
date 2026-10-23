using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// ebx graph-export / graph-plan / graph-import / schema: whole-asset object-table packages for cross-game EBX ports.
    ///
    ///   ebx graph-export &lt;asset&gt; --out &lt;file.json&gt;
    ///   ebx graph-export --list &lt;assets.txt&gt; --out-dir &lt;dir&gt;          one package per asset (name: path with / -> __)
    ///   ebx graph-plan   &lt;package.json&gt; --as &lt;new asset&gt; [--strict] [--allow-unsealed] [--keep-flags] [--out report.json]
    ///   ebx graph-import &lt;package.json&gt; --as &lt;new asset&gt; --bundle &lt;bundle&gt; [--strict] [--allow-unsealed] [--keep-flags] [--out report.json]
    ///   ebx schema &lt;Type[,Type...]&gt; | --types-file &lt;file&gt;  [--out schema.json]
    /// </summary>
    public static class EbxGraphCommand
    {
        public static int Execute(string op, string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            try
            {
                if (App.AssetManager == null) throw new InvalidOperationException("AssetManager not initialized.");
                var options = AudioCommand.Parse(args.Where(a => a != "--strict" && a != "--allow-unsealed" && a != "--keep-flags").ToArray());
                bool strict = args.Contains("--strict"), allowUnsealed = args.Contains("--allow-unsealed"), keepFlags = args.Contains("--keep-flags");
                switch (op)
                {
                    case "graph-export":
                        {
                            Allow(options, "", "out", "list", "out-dir");
                            if (options.TryGetValue("list", out string listFile))
                            {
                                string dir = Required(options, "out-dir");
                                Directory.CreateDirectory(dir);
                                int ok = 0, failed = 0;
                                foreach (string raw in File.ReadAllLines(listFile))
                                {
                                    string name = raw.Trim();
                                    if (name.Length == 0 || name.StartsWith("#")) continue;
                                    string file = Path.Combine(dir, name.ToLowerInvariant().Replace('/', '_').Replace('\\', '_') + ".json");
                                    try
                                    {
                                        var entry = App.AssetManager.GetEbxEntry(name) ?? throw new ArgumentException("Asset not found: " + name);
                                        File.WriteAllText(file, JsonConvert.SerializeObject(EbxGraphPackage.Capture(entry), Formatting.Indented));
                                        ok++;
                                    }
                                    catch (Exception ex) { Console.Error.WriteLine("graph-export " + name + ": " + ex.Message); failed++; }
                                }
                                formatter.WriteResult(failed == 0 ? "OK" : "PARTIAL", "ebx graph-export", new Dictionary<string, string> { { "exported", ok.ToString() }, { "failed", failed.ToString() }, { "dir", dir } });
                                return failed == 0 ? 0 : 1;
                            }
                            string asset = Required(options, ""), outFile = Required(options, "out");
                            var e = App.AssetManager.GetEbxEntry(asset) ?? throw new ArgumentException("Asset not found: " + asset);
                            File.WriteAllText(outFile, JsonConvert.SerializeObject(EbxGraphPackage.Capture(e), Formatting.Indented));
                            formatter.WriteResult("OK", "ebx graph-export", new Dictionary<string, string> { { "asset", e.Name }, { "out", outFile } });
                            return 0;
                        }
                    case "graph-plan":
                    case "graph-import":
                        {
                            Allow(options, "", "as", "bundle", "out");
                            var package = EbxGraphPackage.Load(Required(options, ""), allowUnsealed);
                            string name = Required(options, "as");
                            var prepared = EbxGraphBuilder.Prepare(package, name, new EbxGraphBuilder.Options { Strict = strict, KeepFlags = keepFlags });
                            if (op == "graph-import" && prepared.Ready)
                            {
                                string bundleName = Required(options, "bundle");
                                var bundle = BundleCommand.FindBundle(bundleName) ?? throw new ArgumentException("Destination bundle not found: " + bundleName);
                                var added = EbxGraphBuilder.Import(prepared, name, App.AssetManager.GetBundleId(bundle), DryRun.Active);
                                prepared.Report["imported"] = added != null;
                                prepared.Report["dry_run"] = DryRun.Active;
                            }
                            Write(prepared.Report, options, logger);
                            if (!prepared.Ready)
                                foreach (var issue in ((JArray)prepared.Report["issues"]).Take(40)) Console.Error.WriteLine("  issue: " + issue);
                            return prepared.Ready ? 0 : 1;
                        }
                    case "schema":
                        {
                            Allow(options, "", "types-file", "out");
                            var names = new List<string>();
                            if (options.TryGetValue("", out string list)) names.AddRange(list.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
                            if (options.TryGetValue("types-file", out string tf)) names.AddRange(File.ReadAllLines(tf).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("#")));
                            if (names.Count == 0) throw new ArgumentException("ebx schema: name at least one type.");
                            Write(EbxGraphBuilder.Schema(names), options, logger);
                            return 0;
                        }
                    default: throw new ArgumentException("Unknown ebx graph operation: " + op);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("ebx " + op + ": " + ex.Message); return 1; }
        }

        private static void Allow(Dictionary<string, string> options, params string[] allowed)
        {
            foreach (string key in options.Keys) if (!allowed.Contains(key)) throw new ArgumentException("Unknown option --" + key);
        }

        private static string Required(Dictionary<string, string> options, string key)
        {
            if (!options.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing " + (key == "" ? "asset/package argument" : "--" + key));
            return value;
        }

        private static void Write(JObject report, Dictionary<string, string> options, ExportLogger logger)
        {
            string text = report.ToString(Formatting.Indented);
            if (options.TryGetValue("out", out string file)) File.WriteAllText(file, text);
            else Console.Out.WriteLine(text);
        }
    }
}
