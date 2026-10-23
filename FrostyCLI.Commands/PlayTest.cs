using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // `mod test --play`: after LOADED, deploy (GameCommand.Deploy - the spawn screen's own START event,
    // no PC input), keep recording the logic feed while the match runs, redeploy after each death, then
    // report what a tester would check first: did the game mode and round start, which blueprints were
    // busy, which game-mode wires never fired, how often the player died. The report is printed and
    // written next to the recording (<recording>.report.md).
    internal static class PlayTest
    {
        // Wires whose firing means the mode is actually running (event or field names, either side).
        private static readonly string[] StartWords =
            { "StartGameMode", "GameModeStarted", "OnGameModeStarted", "RoundStart", "OnRoundStarted", "OnRoundStart", "StartRound", "ForceGameModeStart" };

        // fromLoad: the recording began at level load (mod test --play), so a missing mode start means
        // it never started; `game play` on a running match can't know.
        internal static int Run(int port, string recordPath, double seconds, ExportLogger logger, bool fromLoad = true)
        {
            var report = new StringBuilder();
            Action<string> say = line => { Console.Out.WriteLine(line); report.AppendLine(line); };

            string already = GameCommand.Controlled(GameCommand.Send(port, "players", 10000));
            int deployed = already != null && already != "0x0" ? 0                     // already in the match
                         : GameCommand.Deploy(port, new List<string> { "--timeout", "60" }, logger);
            if (deployed != 0)
            {
                say("play: could not deploy - stopping (see above)");
                return 1;
            }

            // Record while playing: append to the load recording, same clock base (seconds since this run).
            var clock = Stopwatch.StartNew();
            int lines = 0, deaths = 0, redeploys = 0;
            using (var file = new System.IO.StreamWriter(recordPath, true, new UTF8Encoding(false)))
            {
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    System.Threading.Thread.Sleep(3000);
                    lines += GameCommand.DrainFeed(port, "trace", file, clock);
                    file.Flush();
                    string controlled = GameCommand.Controlled(GameCommand.Send(port, "players", 10000));
                    if (controlled == "0x0")
                    {
                        deaths++;
                        if (GameCommand.Deploy(port, new List<string> { "--timeout", "45" }, logger) == 0) redeploys++;
                    }
                }
                lines += GameCommand.DrainFeed(port, "trace", file, clock);
            }

            say("");
            say("## Play test (" + seconds + " s in the match)");
            say("deployed: yes; deaths " + deaths + ", redeployed " + redeploys + "; " + lines + " events recorded while playing");

            // Mode / round start, from the whole recording (load + play).
            var started = new List<string>();
            var perBlueprint = new Dictionary<string, int>();
            foreach (string raw in System.IO.File.ReadLines(recordPath))
            {
                int at = raw.IndexOf("  @", StringComparison.Ordinal);
                if (at > 0)
                {
                    string bp = raw.Substring(at + 3).Trim();
                    perBlueprint[bp] = perBlueprint.TryGetValue(bp, out int n) ? n + 1 : 1;
                }
                string wire = at > 0 ? raw.Substring(0, at) : raw;
                if (started.Count < 8 && StartWords.Any(w => wire.IndexOf("." + w, StringComparison.OrdinalIgnoreCase) >= 0))
                    started.Add(raw.Trim());
            }
            say(started.Count > 0 ? "mode start: seen"
                : fromLoad ? "mode start: NOT SEEN - no StartGameMode / GameModeStarted / RoundStart wire fired"
                : "mode start: unknown - the recording began mid-match (use mod test --play to see the start)");
            foreach (string s in started) say("  " + s);

            say("busiest blueprints:");
            foreach (var kv in perBlueprint.OrderByDescending(kv => kv.Value).Take(8))
                say(string.Format("  {0,7}  {1}", kv.Value, kv.Key));

            // Game-mode wiring that never fired during the whole run.
            string[] listing = GameCommand.Send(port, "wires --events GameModes", 120000)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var fired = new HashSet<string>(GameCommand.RecordedWires(recordPath));
            // Per blueprint: a blueprint none of whose wires fired was not reached (another mode, end of
            // round) and says little; one that fired some and not others is where a flow may have stalled.
            var byBlueprint = new Dictionary<string, Tuple<int, List<string>>>();
            int total = 0, firedTotal = 0;
            for (int i = 1; i < listing.Length; i++)
            {
                string wire = listing[i].TrimEnd('\r');
                if (!wire.StartsWith("E ", StringComparison.Ordinal)) continue;
                int at = wire.IndexOf("  @", StringComparison.Ordinal);
                string bp = at > 0 ? wire.Substring(at + 3).Trim() : "?";
                if (!byBlueprint.TryGetValue(bp, out var slot)) byBlueprint[bp] = slot = Tuple.Create(0, new List<string>());
                total++;
                if (fired.Contains(GameCommand.WireKey(wire))) { firedTotal++; byBlueprint[bp] = slot = Tuple.Create(slot.Item1 + 1, slot.Item2); }
                else slot.Item2.Add(wire);
            }
            var partial = byBlueprint.Where(kv => kv.Value.Item1 > 0 && kv.Value.Item2.Count > 0)
                                     .OrderByDescending(kv => kv.Value.Item2.Count).ToList();
            var untouched = byBlueprint.Where(kv => kv.Value.Item1 == 0).Select(kv => kv.Key).ToList();
            say("game-mode event wires: " + firedTotal + " of " + total + " fired, in " + byBlueprint.Count + " blueprints");
            say("  partly fired (where a flow may have stopped):");
            foreach (var kv in partial.Take(10))
            {
                say(string.Format("    {0,4} of {1,-4} fired  {2}", kv.Value.Item1, kv.Value.Item1 + kv.Value.Item2.Count, kv.Key));
                foreach (string w in kv.Value.Item2.Take(3)) say("           never: " + w.Substring(0, w.IndexOf("  @", StringComparison.Ordinal) > 0 ? w.IndexOf("  @", StringComparison.Ordinal) : w.Length));
            }
            if (partial.Count == 0) say("    none");
            say("  not reached at all (other modes, end of round...): " + untouched.Count + " blueprints" +
                (untouched.Count > 0 ? ", e.g. " + string.Join(", ", untouched.Take(4)) : ""));

            string reportPath = recordPath + ".report.md";
            System.IO.File.WriteAllText(reportPath, report.ToString());
            Console.Out.WriteLine("report: " + reportPath + "   recording: " + recordPath);
            return started.Count > 0 || !fromLoad ? 0 : 1;
        }
    }
}
