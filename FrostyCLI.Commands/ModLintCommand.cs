using System;
using System.Collections.Generic;
using System.Globalization;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    // `mod lint` -- pre-flight the loaded project for the faults that produce a mod which builds and
    // applies cleanly but is broken in game. Those faults share a shape: an asset the project adds or
    // edits is not reachable by the runtime, so nothing renders and nothing errors. They are all
    // decidable from the project, without launching the game, which is what makes them worth a command
    // rather than a `mod test` run.
    //
    // Level structure is not re-implemented here: every level the project touches is handed to the
    // same validator `level validate` runs, so the two can never disagree about what a broken level is.
    public static class ModLintCommand
    {
        private class LintIssue
        {
            [JsonProperty("severity")] public string Severity;
            [JsonProperty("code")]     public string Code;
            [JsonProperty("asset")]    public string Asset;
            [JsonProperty("detail")]   public string Detail;

            // Set on issues the level validator produced, whose totals it has already counted.
            // Identity, not the asset name: an added sub-world can raise issues of both kinds.
            [JsonIgnore] public bool FromLevel;
        }

        // What the project can edit that is worth validating as a level. A map mod most often touches
        // a sub-world or a layer rather than a root LevelData -- this game ships 44 LevelData against
        // 1384 SubWorldData and 7630 LayerData -- so matching LevelData alone would skip validation
        // for most real map mods. Validating a non-root on its own is safe: the validator only runs
        // mesh-registration checks when the root is a LevelData, precisely because registration is a
        // whole-level property, so a sub-world yields its structural findings and no false positives.
        private static readonly string[] LevelishTypes = { "LevelData", "SubWorldData", "WorldPartData", "LayerData" };

        private static bool IsLevelish(string type)
        {
            if (type == null) return false;
            foreach (string t in LevelishTypes)
                if (string.Equals(type, t, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string format = "table";
            int maxIssues = 100;
            int maxDepth = 32;
            bool strict = false;
            bool skipLevels = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--max-issues" && i + 1 < args.Length) maxIssues = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--max-depth" && i + 1 < args.Length) maxDepth = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--strict") strict = true;
                else if (args[i] == "--no-levels") skipLevels = true;
            }

            if (App.AssetManager == null) { Console.Error.WriteLine("mod lint: AssetManager not initialized."); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("mod lint: no project loaded."); return 1; }
            if (maxIssues < 0) { Console.Error.WriteLine("mod lint: --max-issues must be zero or greater"); return 1; }

            var issues = new List<LintIssue>();
            int ebxCount = 0, resCount = 0, chunkCount = 0, levelCount = 0;

            // An asset the project ADDS reaches the runtime only through a bundle. A modified asset
            // inherits the bundles it already shipped in, so only added ones can be orphaned this way.
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true))
            {
                ebxCount++;
                if (e.IsAdded && CountBundles(e) == 0)
                    issues.Add(Error("added-ebx-unbundled", e.Name,
                        "added EBX is in no bundle, so the game never loads it -- bundle add <asset> --to <bundle>"));
            }

            foreach (ResAssetEntry r in App.AssetManager.EnumerateRes(modifiedOnly: true))
            {
                resCount++;
                if (r.IsAdded && CountBundles(r) == 0)
                    issues.Add(Error("added-res-unbundled", r.Name,
                        "added res is in no bundle, so its owning asset resolves to nothing at runtime"));
            }

            foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks(modifiedOnly: true))
            {
                chunkCount++;
                if (c.IsAdded && CountBundles(c) == 0)
                    issues.Add(Warning("added-chunk-unbundled", c.Id.ToString(),
                        "added chunk is in no bundle; this is correct only if a res references it by id"));
            }

            // Every level the project touches gets the full `level validate` treatment, so mesh
            // registration, bundling, Havok tables and placement ranges are all covered here.
            var levels = new List<string>();
            if (!skipLevels)
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx(modifiedOnly: true))
                    if (IsLevelish(e.Type)) levels.Add(e.Name);
                levels.Sort(StringComparer.OrdinalIgnoreCase);
                levelCount = levels.Count;
            }

            int levelErrors = 0, levelWarnings = 0;
            foreach (string level in levels)
            {
                int e2, w2;
                List<string> lines = LevelCommand.LintLevel(level, maxDepth, out e2, out w2);
                levelErrors += e2;
                levelWarnings += w2;
                foreach (string line in lines)
                {
                    // "severity|code|detail", as LintLevel formats it.
                    string[] parts = line.Split(new[] { '|' }, 3);
                    if (parts.Length == 3) issues.Add(new LintIssue { Severity = parts[0], Code = parts[1], Asset = level, Detail = parts[2], FromLevel = true });
                }
            }

            int errors = levelErrors, warnings = levelWarnings;
            foreach (LintIssue i in issues)
            {
                if (i.FromLevel) continue; // already counted by the level validator
                if (i.Severity == "error") errors++; else warnings++;
            }

            if (format == "json") PrintJson(issues, ebxCount, resCount, chunkCount, levelCount, errors, warnings);
            else PrintTable(issues, ebxCount, resCount, chunkCount, levelCount, maxIssues, errors, warnings);

            if (errors > 0) return 2;
            if (strict && warnings > 0) return 2;
            return 0;
        }

        private static int CountBundles(AssetEntry entry)
        {
            int n = entry.Bundles != null ? entry.Bundles.Count : 0;
            if (entry.AddedBundles != null) n += entry.AddedBundles.Count;
            return n;
        }

        private static LintIssue Error(string code, string asset, string detail)
        {
            return new LintIssue { Severity = "error", Code = code, Asset = asset, Detail = detail };
        }

        private static LintIssue Warning(string code, string asset, string detail)
        {
            return new LintIssue { Severity = "warning", Code = code, Asset = asset, Detail = detail };
        }

        private static void PrintTable(List<LintIssue> issues, int ebx, int res, int chunk, int levels,
                                       int maxIssues, int errors, int warnings)
        {
            Console.Out.WriteLine();
            int shown = 0;
            foreach (LintIssue i in issues)
            {
                if (shown >= maxIssues) break;
                Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-7} {1,-24} {2}",
                    i.Severity.ToUpperInvariant(), i.Code, i.Asset));
                Console.Out.WriteLine("          " + i.Detail);
                shown++;
            }
            if (issues.Count > shown)
                Console.Out.WriteLine("  ... " + (issues.Count - shown) + " more (raise --max-issues, or use --format json)");
            if (issues.Count > 0) Console.Out.WriteLine();

            Console.Out.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Result: {0} ebx, {1} res, {2} chunk, {3} level(s) checked: {4} error(s), {5} warning(s)",
                ebx, res, chunk, levels, errors, warnings));
        }

        private static void PrintJson(List<LintIssue> issues, int ebx, int res, int chunk, int levels,
                                      int errors, int warnings)
        {
            var payload = new Dictionary<string, object>
            {
                { "ebx_checked", ebx },
                { "res_checked", res },
                { "chunk_checked", chunk },
                { "levels_checked", levels },
                { "errors", errors },
                { "warnings", warnings },
                { "issues", issues }
            };
            Console.Out.WriteLine(JsonConvert.SerializeObject(payload, Formatting.Indented));
        }
    }
}
