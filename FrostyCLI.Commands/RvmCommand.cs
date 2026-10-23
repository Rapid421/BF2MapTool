using System;
using System.Collections.Generic;
using System.Globalization;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    // `rvm` -- the compiled shader permutation databases a level carries.
    //
    // Every level bundle ships FOUR of them, one per render backend/vendor path, and a mesh's shader
    // block depot references permutations in them by GUID. A mesh placed in a level whose databases
    // lack its permutations makes the game crash on load -- which is why placing a prop outside its
    // home level is a crash risk even for props the game itself ships.
    //
    // The two ways of supplying missing permutations both operate per variant: `res copy` swaps one
    // database at a time, and the external merge tool handles DX11 only. Both therefore fail the same
    // way -- three of the four variants are left behind, so the level loads on the author's GPU and
    // crashes on someone else's. That asymmetry is invisible in the project and is what `rvm check`
    // exists to catch.
    //
    // This command deliberately does NOT parse database contents: the record format is not known here,
    // so it reports what is decidable from the resources themselves rather than guessing.
    public class RvmCommand : ICommand
    {
        public string Name { get { return "rvm"; } }

        // The four variants every level is expected to carry, as they are named under <level>/.
        private static readonly string[] Variants = { "dx12pc", "dx12nv", "dx11", "dx11nv" };

        private class RvmEntry
        {
            [JsonProperty("variant")]  public string Variant;
            [JsonProperty("res")]      public string Res;
            [JsonProperty("present")]  public bool   Present;
            [JsonProperty("modified")] public bool   Modified;
            [JsonProperty("size")]     public long   Size;
        }

        private class RvmIssue
        {
            [JsonProperty("severity")] public string Severity;
            [JsonProperty("code")]     public string Code;
            [JsonProperty("level")]    public string Level;
            [JsonProperty("detail")]   public string Detail;
        }

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length == 0)
            {
                logger.LogError("Usage: rvm <ls|check> ...");
                return 2;
            }
            var rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
            switch (args[0].ToLowerInvariant())
            {
                case "ls":    return ExecuteLs(rest, logger, formatter);
                case "check": return ExecuteCheck(rest, logger, formatter);
                default:
                    logger.LogError("rvm: unknown subcommand '" + args[0] + "'. Use: ls, check");
                    return 2;
            }
        }

        public static int ExecuteLs(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, format = "table";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (!args[i].StartsWith("--")) level = args[i];
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("rvm ls: AssetManager not initialized."); return 1; }
            if (level == null) { Console.Error.WriteLine("rvm ls: missing <level-ebx-path>"); return 1; }

            List<RvmEntry> entries = Inspect(level);
            if (format == "json")
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "level", level }, { "databases", entries }
                }, Formatting.Indented));
            }
            else
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-8} {1,-9} {2,-9} {3}",
                    "variant", "present", "modified", "res"));
                foreach (RvmEntry e in entries)
                    Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-8} {1,-9} {2,-9} {3}",
                        e.Variant, e.Present ? "yes" : "NO", e.Modified ? "yes" : "-", e.Res));
                Console.Out.WriteLine();
            }
            return 0;
        }

        public static int ExecuteCheck(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string level = null, format = "table";
            bool all = false, strict = false;
            int maxIssues = 100;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--max-issues" && i + 1 < args.Length) maxIssues = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--strict") strict = true;
                else if (args[i] == "--all") all = true;
                else if (!args[i].StartsWith("--")) level = args[i];
            }

            if (App.AssetManager == null) { Console.Error.WriteLine("rvm check: AssetManager not initialized."); return 1; }
            if (all && level != null) { Console.Error.WriteLine("rvm check: --all takes no <level-ebx-path>"); return 1; }
            if (!all && level == null) { Console.Error.WriteLine("rvm check: missing <level-ebx-path> (or pass --all)"); return 1; }

            var targets = new List<string>();
            if (all)
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                    if (string.Equals(e.Type, "LevelData", StringComparison.OrdinalIgnoreCase)) targets.Add(e.Name);
                targets.Sort(StringComparer.OrdinalIgnoreCase);
            }
            else targets.Add(level);

            var issues = new List<RvmIssue>();
            int checkedLevels = 0;

            foreach (string target in targets)
            {
                List<RvmEntry> entries = Inspect(target);
                int present = 0, modified = 0;
                var missing = new List<string>();
                var modifiedNames = new List<string>();
                var unmodifiedNames = new List<string>();

                foreach (RvmEntry e in entries)
                {
                    if (!e.Present) { missing.Add(e.Variant); continue; }
                    present++;
                    if (e.Modified) { modified++; modifiedNames.Add(e.Variant); }
                    else unmodifiedNames.Add(e.Variant);
                }

                // A level with no databases at all is simply not a level that carries them; only say
                // something when it carries some but not the full set.
                if (present == 0) continue;
                checkedLevels++;

                if (missing.Count > 0)
                    issues.Add(new RvmIssue
                    {
                        Severity = "warning", Code = "rvm-variant-missing", Level = target,
                        Detail = "level carries " + present + " of " + Variants.Length +
                                 " shader permutation databases; absent: " + string.Join(", ", missing.ToArray())
                    });

                // The finding this command exists for: an edit applied to some variants and not others.
                if (modified > 0 && modified < present)
                    issues.Add(new RvmIssue
                    {
                        Severity = "error", Code = "rvm-variants-partial", Level = target,
                        Detail = "the project modifies " + modified + " of this level's " + present +
                                 " databases (" + string.Join(", ", modifiedNames.ToArray()) + " edited; " +
                                 string.Join(", ", unmodifiedNames.ToArray()) + " untouched). The untouched " +
                                 "variants keep the permutations they shipped with, so the level loads on " +
                                 "the GPU paths you edited and can crash on the others. Apply the same change " +
                                 "to every variant."
                    });
            }

            int errors = 0, warnings = 0;
            foreach (RvmIssue i in issues) { if (i.Severity == "error") errors++; else warnings++; }

            if (format == "json")
            {
                Console.Out.WriteLine(JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "levels_checked", checkedLevels }, { "errors", errors }, { "warnings", warnings }, { "issues", issues }
                }, Formatting.Indented));
            }
            else
            {
                Console.Out.WriteLine();
                int shown = 0;
                foreach (RvmIssue i in issues)
                {
                    if (shown >= maxIssues) break;
                    Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-7} {1,-22} {2}",
                        i.Severity.ToUpperInvariant(), i.Code, i.Level));
                    Console.Out.WriteLine("          " + i.Detail);
                    shown++;
                }
                if (issues.Count > shown)
                    Console.Out.WriteLine("  ... " + (issues.Count - shown) + " more (raise --max-issues, or use --format json)");
                if (issues.Count > 0) Console.Out.WriteLine();
                Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "Result: {0} level(s) with permutation databases: {1} error(s), {2} warning(s)",
                    checkedLevels, errors, warnings));
            }

            if (errors > 0) return 2;
            if (strict && warnings > 0) return 2;
            return 0;
        }

        // A level's databases sit at "<level>/rvmdatabase_<variant>rvmdatabase". The level path itself
        // repeats its own leaf (s2/levels/cloudcity_01/cloudcity_01), and the resources hang off that.
        private static List<RvmEntry> Inspect(string levelPath)
        {
            var entries = new List<RvmEntry>();
            foreach (string variant in Variants)
            {
                string res = levelPath.ToLowerInvariant() + "/rvmdatabase_" + variant + "rvmdatabase";
                ResAssetEntry entry = App.AssetManager.GetResEntry(res);
                entries.Add(new RvmEntry
                {
                    Variant = variant,
                    Res = res,
                    Present = entry != null,
                    Modified = entry != null && (entry.IsModified || entry.IsAdded),
                    Size = entry != null ? entry.Size : 0
                });
            }
            return entries;
        }
    }
}
