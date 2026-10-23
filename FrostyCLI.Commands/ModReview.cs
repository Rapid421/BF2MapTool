using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // mod review <mod.fbmod>|--vanilla [--against <other.fbmod>|vanilla] [--level <path>] [--mode <GameMode>]
    //            [--shots <shots.json>] [--play-seconds 60] [--out <dir>]
    //
    // One build (or two, side by side) put through what a tester would do: load it, deploy, play while
    // recording the logic (PlayTest: mode start, stalled flows), then shoot a fixed list of viewpoints
    // (GameCommand.Shot - flicker-free, deploys and freezes time itself). With --against the second
    // build gets the same viewpoints and each pair is put on one image with a difference score, so a
    // change that loses meshes, lighting or colour shows at a glance. Without --shots, viewpoints are
    // made around the first build's spawn (an overview and four compass views) and saved for reuse.
    // Writes <out>/review.md; everything is driven through the game's own logic, never PC input.
    internal static class ModReview
    {
        internal static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var list = args.Skip(1).ToList();
            string Opt(string name) { int i = list.IndexOf(name); if (i < 0 || i + 1 >= list.Count) return null; string v = list[i + 1]; list.RemoveRange(i, 2); return v; }
            string against = Opt("--against"), level = Opt("--level") ?? "S2/Levels/CloudCity_01/CloudCity_01",
                   mode = Opt("--mode") ?? "SkirmishBlast", shots = Opt("--shots"),
                   outDir = Opt("--out") ?? Path.Combine(Path.GetTempPath(), "frostycli-review-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            double playSeconds = double.Parse(Opt("--play-seconds") ?? "60", CultureInfo.InvariantCulture);
            bool vanilla = list.Remove("--vanilla");
            string mod = list.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (mod == null && !vanilla)
            {
                logger.LogError("Usage: mod review <mod.fbmod>|--vanilla [--against <other.fbmod>|vanilla] [--level <path>] [--mode <GameMode>] [--shots <shots.json>] [--play-seconds 60] [--out <dir>] [--close-game]");
                return 2;
            }
            Directory.CreateDirectory(outDir);
            // Never close a game the user has open: refuse unless asked, and afterwards close only games
            // this review started.
            bool closeGame = list.Remove("--close-game");
            if (Process.GetProcessesByName("starwarsbattlefrontii").Length > 0)
            {
                if (!closeGame) { logger.LogError("mod review: Battlefront II is running - close it first (or pass --close-game)"); return 2; }
                foreach (Process p in Process.GetProcessesByName("starwarsbattlefrontii")) { try { p.Kill(); p.WaitForExit(15000); } catch { } }
            }
            started = DateTime.Now;

            var builds = new List<Tuple<string, string>> { Tuple.Create(vanilla ? "vanilla" : mod, Label(vanilla ? "vanilla" : mod)) };
            if (against != null) builds.Add(Tuple.Create(against, Label(against)));
            if (builds.Count == 2 && builds[0].Item2 == builds[1].Item2)
                builds[1] = Tuple.Create(builds[1].Item1, builds[1].Item2 + "_b");

            var md = new StringBuilder();
            md.AppendLine("# Review: " + string.Join(" vs ", builds.Select(b => b.Item2)));
            md.AppendLine();
            md.AppendLine("Level `" + level + "`, mode `" + mode + "`, " + playSeconds + " s of play per build.");
            md.AppendLine();
            int worst = 0;

            foreach (var build in builds)
            {
                string dir = Path.Combine(outDir, build.Item2);
                Directory.CreateDirectory(dir);
                string recording = Path.Combine(dir, "logic.txt");
                Console.Out.WriteLine();
                Console.Out.WriteLine("==== " + build.Item2 + " ====");
                md.AppendLine("## " + build.Item2);
                md.AppendLine();

                var testArgs = new List<string> { "test" };
                if (build.Item1 == "vanilla") testArgs.Add("--vanilla"); else testArgs.Add(build.Item1);
                testArgs.AddRange(new[] { "--level", level, "--mode", mode, "--keep", "--record", recording, "--timeout", "600" });
                int loaded = ModBuildCommand.ExecuteTest(testArgs.ToArray(), logger, formatter);
                if (loaded != 0)
                {
                    md.AppendLine("**Did not load** (mod test exit " + loaded + ") - see the crash report in the console / gamelink.log.");
                    md.AppendLine();
                    worst = Math.Max(worst, 1);
                    KillGame();
                    continue;
                }
                md.AppendLine("Loaded.");
                md.AppendLine();
                try
                {

                int played = PlayTest.Run(31500, recording, playSeconds, logger);
                if (played != 0) worst = Math.Max(worst, 1);
                string playReport = recording + ".report.md";
                if (File.Exists(playReport)) { md.AppendLine(File.ReadAllText(playReport).Replace("## Play test", "### Play test")); md.AppendLine(); }

                if (shots == null)
                {
                    GameCommand.Deploy(31500, new List<string> { "--timeout", "60" }, logger);   // the camera must be on the player
                    shots = Path.Combine(outDir, "shots.json");
                    if (!MakeShots(shots)) { md.AppendLine("No viewpoints: could not read the player's position."); KillGame(); continue; }
                    md.AppendLine("Viewpoints made around this build's spawn: `" + shots + "` (reused for every build).");
                    md.AppendLine();
                }
                var shotArgs = new List<string> { "--list", shots, "--out", Path.Combine(dir, "shots") };
                if (GameCommand.ShotFor(31500, shotArgs, logger) != 0) { md.AppendLine("Some viewpoints could not be shot (see the console)."); md.AppendLine(); }
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException || ex is IOException)
                {
                    // The game went away mid-run: say so, with the crash report, and go on to the next build.
                    worst = Math.Max(worst, 1);
                    md.AppendLine("**The game stopped answering** (" + ex.Message + "). Crash report:");
                    md.AppendLine();
                    string crash;
                    try { crash = CrashReport.Build(CrashReport.DefaultLog, CrashReport.FindKnownCrashes() ?? ""); }
                    catch (Exception inner) { crash = "crash report failed: " + inner.Message; }
                    md.AppendLine("```");
                    md.AppendLine(crash.Trim());
                    md.AppendLine("```");
                    md.AppendLine();
                    Console.Out.WriteLine("review: " + build.Item2 + " - the game stopped answering; continuing");
                }
                KillGame();
            }

            // Pictures: one per viewpoint, or A | B with a difference score.
            md.AppendLine("## Viewpoints");
            md.AppendLine();
            string[] names = File.Exists(shots ?? "") ? Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(shots)).Select(t => (string)t["name"]).ToArray() : new string[0];
            foreach (string name in names)
            {
                var paths = builds.Select(b => Path.Combine(outDir, b.Item2, "shots", name + ".png")).ToList();
                if (!paths.All(File.Exists)) { md.AppendLine("- " + name + ": missing in " + string.Join(", ", builds.Where((b, i) => !File.Exists(paths[i])).Select(b => b.Item2))); continue; }
                if (paths.Count == 1) { md.AppendLine("### " + name); md.AppendLine("![" + name + "](" + Rel(outDir, paths[0]) + ")"); md.AppendLine(); continue; }
                string sheet = Path.Combine(outDir, "compare_" + name + ".png");
                double diff = SideBySide(paths[0], paths[1], builds[0].Item2, builds[1].Item2, sheet);
                md.AppendLine(string.Format(CultureInfo.InvariantCulture, "### {0} - difference {1:F1}/255{2}", name, diff,
                              diff > 25 ? " (large: look)" : diff > 10 ? " (noticeable)" : ""));
                md.AppendLine("![" + name + "](" + Rel(outDir, sheet) + ")");
                md.AppendLine();
            }

            string reportPath = Path.Combine(outDir, "review.md");
            File.WriteAllText(reportPath, md.ToString());
            Console.Out.WriteLine();
            Console.Out.WriteLine("review: " + reportPath);
            return worst;
        }

        private static string Label(string mod) => mod == "vanilla" ? "vanilla" : Path.GetFileNameWithoutExtension(mod);

        private static string Rel(string root, string path) => path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');

        private static DateTime started = DateTime.MaxValue;

        // The game this review started: stop it before the next build (the harness can't load two).
        // Only processes started after the review began.
        private static void KillGame()
        {
            foreach (Process p in Process.GetProcessesByName("starwarsbattlefrontii"))
            {
                try { if (p.StartTime >= started.AddSeconds(-5)) { p.Kill(); p.WaitForExit(15000); } } catch { }
            }
            System.Threading.Thread.Sleep(3000);
        }

        // An overview from 120 m above the spawn and four views from 35 m out and 18 m up, looking at it.
        private static bool MakeShots(string path)
        {
            string where = GameCommand.Send(31500, "camera", 10000);
            var m = System.Text.RegularExpressions.Regex.Match(where, @"camera pos (\S+) (\S+) (\S+)");
            if (!m.Success) return false;
            double x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                   z = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (y < -1000) return false;                        // the spawn screen's parked camera, not the level
            string P(double a, double b, double c) => "[" + string.Join(",", new[] { a, b, c }.Select(v => v.ToString("F2", CultureInfo.InvariantCulture))) + "]";
            var items = new List<string>
            {
                "{\"name\":\"overview\",\"at\":" + P(x, y + 120, z - 30) + ",\"look\":" + P(x, y, z) + "}",
                "{\"name\":\"north\",\"at\":" + P(x, y + 18, z + 35) + ",\"look\":" + P(x, y, z) + "}",
                "{\"name\":\"south\",\"at\":" + P(x, y + 18, z - 35) + ",\"look\":" + P(x, y, z) + "}",
                "{\"name\":\"east\",\"at\":" + P(x + 35, y + 18, z) + ",\"look\":" + P(x, y, z) + "}",
                "{\"name\":\"west\",\"at\":" + P(x - 35, y + 18, z) + ",\"look\":" + P(x, y, z) + "}",
            };
            File.WriteAllText(path, "[\n  " + string.Join(",\n  ", items) + "\n]\n");
            return true;
        }

        // A | B on one image (labels on top), and the mean absolute pixel difference.
        private static double SideBySide(string a, string b, string labelA, string labelB, string outPath)
        {
            using (var ia = new Bitmap(a))
            using (var ib = new Bitmap(b))
            {
                int w = 960, h = 540;
                using (var sa = new Bitmap(ia, w, h))
                using (var sb = new Bitmap(ib, w, h))
                using (var sheet = new Bitmap(w * 2, h + 28))
                using (var g = Graphics.FromImage(sheet))
                {
                    g.Clear(Color.Black);
                    g.DrawImage(sa, 0, 28);
                    g.DrawImage(sb, w, 28);
                    using (var font = new Font("Segoe UI", 12))
                    {
                        g.DrawString(labelA, font, Brushes.White, 8, 4);
                        g.DrawString(labelB, font, Brushes.White, w + 8, 4);
                    }
                    sheet.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);

                    long total = 0;
                    int samples = 0;
                    for (int yy = 0; yy < h; yy += 4)
                        for (int xx = 0; xx < w; xx += 4)
                        {
                            Color ca = sa.GetPixel(xx, yy), cb = sb.GetPixel(xx, yy);
                            total += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                            samples += 3;
                        }
                    return samples == 0 ? 0 : (double)total / samples;
                }
            }
        }
    }
}
