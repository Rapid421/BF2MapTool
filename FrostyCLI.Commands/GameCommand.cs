using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // `game` - talk to FrostyGameLink inside a running Battlefront II.
    //
    // The DLL (tools/gamelink, built by tools/gamelink/build.bat) is injected by `mod test` (or the
    // harness directly) and listens on 127.0.0.1:31500 for one text command per connection; the reply
    // ends when the link goes quiet. This command is a thin client for it - no game data is loaded, so
    // it runs instantly against whatever is in the game right now.
    //
    // The two feeds (see tools/gamelink/TRACE.md):
    //   trace - every logic event travelling along a wire:
    //           S EventGate[33].Out -> LogicPrefabReferenceObject#32914a11.TriggerUpdate  @AI/.../PF_..._Logic
    //   prop  - every property value arriving at an entity:
    //           S InterfaceDescriptor[4].DistanceToTarget -> CompareFloat[3].A = 153.515  @AI/.../PF_..._Logic
    // [33] is the instance's index in its EBX file (Frosty's placeholder GUID ...000000000033), #32914a11 an
    // exported instance's real GUID, @ the blueprint that owns the wire. Both only arm with a level loaded:
    // patching the game's code earlier stalls or kills it.
    public sealed class GameCommand : ICommand
    {
        public string Name => "game";

        private const string Usage =
            "game status | game trace <on [filter]|off|[n]|status|filter <f>|clear|pause|resume|dump|guid 0|1|2|where 0|1|addr 0|1> | " +
            "game prop <on [filter]|off|[n]|status|filter <f>|clear> | game overlay <on [filter]|off|filter <f>|feed events|props|both> | " +
            "game follow [--prop|--both] [--seconds <s>] [--keep-on] [filter] | game record <file> [--prop|--both] [--seconds <s>] [filter] | " +
            "game diff <a.txt> <b.txt> [--loose] [--max <n>] | game wires <blueprint filter> [--events|--props] [--against <recording>]... [--never] | " +
            "game budget | game timeline <recording> [--all] [--match <a,b>] | game fire|deliver <Type[tag]>.<Event> @<blueprint> [S|C] [all] | game inspect <Type[tag]> @<blueprint> [S|C] | game deploy [--timeout <s>] | game play [--seconds <s>] [--record <file>] | game shot --at x,y,z [--look x,y,z] [--out <file.png>] [--hud] [--wait <s>] [--keep-cam] | game shot --list <shots.json> --out <dir> | game where | game set <Type[tag]>.<Field> = <value> @<blueprint> [S|C] [all] | game crash [--log <gamelink.log>] [--known <known-crashes.json>] | game log [n] | game send <raw gamelink command> [--port <n>]";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var list = new List<string>(args);
            int port = 31500;
            int portAt = list.IndexOf("--port");
            if (portAt >= 0 && portAt + 1 < list.Count)
            {
                port = int.Parse(list[portAt + 1], System.Globalization.CultureInfo.InvariantCulture);
                list.RemoveRange(portAt, 2);
            }
            if (list.Count == 0)
            {
                logger.LogError("usage: " + Usage);
                return 2;
            }

            string op = list[0];
            string rest = string.Join(" ", list.Skip(1));
            try
            {
                switch (op)
                {
                    case "status":
                        return Status(port, logger);
                    case "trace":
                    case "prop":
                    case "overlay":
                        return Print(Send(port, (op + " " + rest).Trim(), 15000));
                    case "log":
                        return Print(Send(port, ("log " + rest).Trim(), 15000));
                    case "send":
                        if (rest.Length == 0) { logger.LogError("game send: missing the gamelink command"); return 2; }
                        return Print(Send(port, rest, 60000));
                    case "follow":
                        return Follow(port, list.Skip(1).ToList(), logger);
                    case "record":
                        return Record(port, list.Skip(1).ToList(), logger);
                    case "diff":
                        return Diff(list.Skip(1).ToList(), logger);         // files only, no game needed
                    case "wires":
                        return Wires(port, list.Skip(1).ToList(), logger);
                    case "fire":
                    case "deliver":
                        if (rest.Length == 0) { logger.LogError("game " + op + ": usage: game " + op + " <Type[tag]>.<Event> @<blueprint> [S|C] [all]"); return 2; }
                        return Print(Send(port, op + " " + rest, 30000));
                    case "inspect":
                        if (rest.Length == 0) { logger.LogError("game inspect: usage: game inspect <Type[tag]> @<blueprint> [S|C]"); return 2; }
                        return Print(Send(port, "inspect " + rest, 30000));
                    case "set":
                        // gamelink's 'set' writes EBX data by type; the live, wired write is 'write'
                        if (rest.Length == 0) { logger.LogError("game set: usage: game set <Type[tag]>.<Field> = <value> @<blueprint> [S|C] [all]"); return 2; }
                        return Print(Send(port, "write " + rest, 30000));
                    case "deploy":
                        return Deploy(port, list.Skip(1).ToList(), logger);
                    case "play":
                    {
                        // play [--seconds <s>] [--record <file>]: PlayTest on the match that is already running
                        var pa = list.Skip(1).ToList();
                        double secs = double.TryParse(Option(pa, "--seconds") ?? "60", System.Globalization.NumberStyles.Float,
                                                      System.Globalization.CultureInfo.InvariantCulture, out double sv) ? sv : 60;
                        string rec = Option(pa, "--record") ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                     "frostycli-play-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                        if (!FirstLine(Send(port, "trace status", 10000)).EndsWith(", on", StringComparison.Ordinal)) Send(port, "trace on", 15000);
                        return PlayTest.Run(port, rec, secs, logger, fromLoad: false);
                    }
                    case "shot":
                        return Shot(port, list.Skip(1).ToList(), logger);
                    case "where":
                        return Print(Send(port, "camera", 10000));
                    case "budget":
                        return Print(Send(port, "budget", 30000));
                    case "timeline":
                        return Timeline(list.Skip(1).ToList(), logger);         // a recording only, no game needed
                    case "crash":
                        return Crash(list.Skip(1).ToList());             // the log only, no game needed
                    default:
                        logger.LogError("game: unknown subcommand '" + op + "' - usage: " + Usage);
                        return 2;
                }
            }
            catch (SocketException)
            {
                logger.LogError("game: no FrostyGameLink on 127.0.0.1:" + port +
                                " - start the game through `mod test` (e.g. `mod test --vanilla --keep`), which injects it");
                return 1;
            }
        }

        private static int Status(int port, ExportLogger logger)
        {
            string pong = Send(port, "ping", 5000);
            Console.Out.WriteLine("gamelink: " + pong);
            Console.Out.WriteLine(Send(port, "levelname", 10000));
            // first line of each feed's reader: shown / pending / dropped / on|off / filter
            Console.Out.WriteLine("trace: " + FirstLine(Send(port, "trace status", 10000)));
            Console.Out.WriteLine("prop:  " + FirstLine(Send(port, "prop status", 10000)));
            Console.Out.WriteLine("overlay: " + FirstLine(Send(port, "overlay", 10000)));
            return 0;
        }

        // ---- follow / record: stream a feed -------------------------------------------------
        // Turn the feed(s) on, take lines as they fire for --seconds (default 30), then leave each
        // feed as it was found: one that was already on gets its own filter back, one this turned on
        // goes off again (unless --keep-on). Lines already carry names; nothing is cached here.
        private sealed class StreamOptions
        {
            public bool Events = true, Props;
            public int Seconds = 30;
            public bool KeepOn;
            public string Filter = "";
        }

        private static StreamOptions ParseStream(List<string> args)
        {
            var o = new StreamOptions();
            var filter = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "--prop") { o.Events = false; o.Props = true; }
                else if (args[i] == "--both") { o.Events = true; o.Props = true; }
                else if (args[i] == "--keep-on") o.KeepOn = true;
                else if (args[i] == "--seconds" && i + 1 < args.Count) o.Seconds = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                else filter.Add(args[i]);
            }
            o.Filter = string.Join(" ", filter);
            return o;
        }

        private static int Follow(int port, List<string> args, ExportLogger logger)
        {
            Stream(port, ParseStream(args), line => Console.Out.WriteLine(line), logger);
            return 0;
        }

        // record <file> [--prop|--both] [--seconds <s>] [--keep-on] [filter]: the same stream, into a
        // file, each line prefixed with seconds since the start - the input of `diff` and `wires --against`.
        private static int Record(int port, List<string> args, ExportLogger logger)
        {
            if (args.Count == 0) { logger.LogError("game record: missing <file>"); return 2; }
            string path = args[0];
            var options = ParseStream(args.Skip(1).ToList());
            var clock = Stopwatch.StartNew();
            int lines = 0;
            using (var file = new System.IO.StreamWriter(path, false, new UTF8Encoding(false)))
            {
                Stream(port, options, line =>
                {
                    file.WriteLine(clock.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
                    ++lines;
                }, logger);
            }
            logger.Log("game record: {0} lines in {1:F0} s -> {2}", lines, clock.Elapsed.TotalSeconds, path);
            return 0;
        }

        private static void Stream(int port, StreamOptions o, Action<string> sink, ExportLogger logger)
        {
            var feeds = new List<string>();
            if (o.Events) feeds.Add("trace");
            if (o.Props) feeds.Add("prop");
            var restore = new Dictionary<string, string>();          // feed -> its filter, if it was on
            foreach (string feed in feeds)
            {
                string before = FirstLine(Send(port, feed + " status", 10000));
                if (before.Contains(", on"))
                {
                    int at = before.IndexOf(", filter: ", StringComparison.Ordinal);
                    restore[feed] = at >= 0 ? before.Substring(at + ", filter: ".Length) : "";
                }
                string reply = Send(port, (feed + " on " + o.Filter).Trim(), 15000);
                if (reply.StartsWith("deferred", StringComparison.Ordinal))
                    logger.Log("game: " + feed + " arms itself once a level is loaded");
                Send(port, (feed + " filter " + o.Filter).Trim(), 15000);
            }

            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < o.Seconds)
            {
                bool any = false;
                foreach (string feed in feeds)
                {
                    string[] lines = Send(port, feed + " 2000", 15000).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < lines.Length; i++)
                        sink(lines[i].TrimEnd('\r'));
                    any |= lines.Length > 1;
                }
                if (!any)
                    System.Threading.Thread.Sleep(300);
            }

            foreach (string feed in feeds)
            {
                if (restore.TryGetValue(feed, out string previous))
                    Send(port, (feed + " filter " + previous).Trim(), 15000);
                else if (!o.KeepOn)
                    Send(port, feed + " off", 15000);
            }
        }

        // deploy [--timeout <s>]: press the spawn screen's START from the inside - fire the event the
        // button sends (UIElementWidgetReference[22].OnOkAction in UI/InGame/Spawn/Screens/SpawnScreen,
        // whose chain runs the screen's own checks and the kit-limit lock, then OnSpawn to the parent).
        // No keyboard or mouse input reaches the PC. Waits until the local player controls something new.
        internal const string DeployEvent = "UIElementWidgetReference[22].OnOkAction @UI/InGame/Spawn/Screens/SpawnScreen C";

        internal static int Deploy(int port, List<string> args, ExportLogger logger)
        {
            int seconds = 30;
            int at = args.IndexOf("--timeout");
            if (at >= 0 && at + 1 < args.Count && int.TryParse(args[at + 1], out int t)) seconds = t;

            // fire runs inside the event hook, and 'trace on' arms it a moment later (deferred arming)
            if (!FirstLine(Send(port, "trace status", 10000)).EndsWith(", on", StringComparison.Ordinal))
            {
                Send(port, "trace on", 15000);
                for (int i = 0; i < 30 && !FirstLine(Send(port, "trace status", 10000)).EndsWith(", on", StringComparison.Ordinal); i++)
                    System.Threading.Thread.Sleep(1000);
            }

            string before = Controlled(Send(port, "players", 10000));
            if (before != null && before != "0x0")
            {
                Console.Out.WriteLine("already in the match: the local player controls " + before);
                return 0;
            }
            // Right after LOADED the spawn screen may not exist yet: retry while the START button isn't live.
            string fired = FirstLine(Send(port, "fire " + DeployEvent, 30000));
            for (int i = 0; i < seconds && fired.StartsWith("fire: no live", StringComparison.Ordinal); i++)
            {
                System.Threading.Thread.Sleep(1000);
                fired = FirstLine(Send(port, "fire " + DeployEvent, 30000));
            }
            Console.Out.WriteLine(fired);
            if (!fired.StartsWith("fired", StringComparison.Ordinal))
            {
                logger.LogError("game deploy: the START event could not be fired - is the spawn screen up?");
                return 1;
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                System.Threading.Thread.Sleep(1000);
                string now = Controlled(Send(port, "players", 10000));
                if (now != null && now != before && now != "0x0")
                {
                    Console.Out.WriteLine("deployed: the local player controls " + now + " (was " + (before ?? "?") + ")");
                    return 0;
                }
            }
            logger.LogError("game deploy: fired START but the local player still controls " + (before ?? "?") +
                            " after " + seconds + " s (a class/hero limit, or the screen wasn't ready yet - try again)");
            return 1;
        }

        // What the local player controls, from 'players': the entry with localId 0 (bots are 255; the list
        // can also start with a stray non-player record, e.g. 'Gameplay/Settings/PlayerData', seen 2026-10-08).
        internal static string Controlled(string players)
        {
            var m = System.Text.RegularExpressions.Regex.Match(players ?? "",
                @"localId 0 [^\n]*\n\s*controlled (0x[0-9A-Fa-f]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        // shot --at x,y,z [--look x,y,z] [--out <file.png>] [--hud] [--wait <s>] [--keep-cam]
        // shot --list <shots.json> --out <dir>      ([{"name":"spawn_a","at":[x,y,z],"look":[x,y,z]}, ...])
        // The world is only rendered while the local player is in the match, so a dead player is deployed
        // first, and time is frozen for the shots (bots can't kill the player mid-list). The pose goes in
        // through gamelink's viewhook - inside the engine's own RenderView copy, so it never flickers.
        // A picture from anywhere: lock the camera (camlock), put it there (camwarp), face the point
        // (camaim), hide the HUD, give streaming a moment, grab the presented frame, save it as PNG, put
        // the HUD and camera back. `game where` prints the camera's current position to build lists from.
        internal static int ShotFor(int port, List<string> args, ExportLogger logger) => Shot(port, args, logger);

        private static int Shot(int port, List<string> args, ExportLogger logger)
        {
            string at = Option(args, "--at"), look = Option(args, "--look"), outPath = Option(args, "--out"), list = Option(args, "--list");
            double wait = double.TryParse(Option(args, "--wait") ?? "2", System.Globalization.NumberStyles.Float,
                                          System.Globalization.CultureInfo.InvariantCulture, out double w) ? w : 2;
            bool hud = args.Contains("--hud"), keep = args.Contains("--keep-cam");
            var shots = new List<Tuple<string, string, string>>();     // name, at, look
            if (list != null)
            {
                if (outPath == null) { logger.LogError("game shot: --list needs --out <dir>"); return 2; }
                var items = Newtonsoft.Json.Linq.JArray.Parse(System.IO.File.ReadAllText(list));
                foreach (var item in items)
                    shots.Add(Tuple.Create((string)item["name"],
                                           string.Join(",", item["at"].Select(v => ((double)v).ToString(System.Globalization.CultureInfo.InvariantCulture))),
                                           item["look"] == null ? null : string.Join(",", item["look"].Select(v => ((double)v).ToString(System.Globalization.CultureInfo.InvariantCulture)))));
                System.IO.Directory.CreateDirectory(outPath);
            }
            else if (at != null)
                shots.Add(Tuple.Create((string)null, at, look));
            else { logger.LogError("game shot: usage: game shot --at x,y,z [--look x,y,z] [--out <file.png>] [--hud] [--wait <s>] [--keep-cam] | game shot --list <shots.json> --out <dir>"); return 2; }

            string who = Controlled(Send(port, "players", 10000));
            if (who == null || who == "0x0")
                if (Deploy(port, new List<string> { "--timeout", "60" }, logger) != 0) { logger.LogError("game shot: the player is not in the match and could not be deployed"); return 1; }
            Send(port, "timescale 0", 60000);
            string hooked = FirstLine(Send(port, "viewhook 1", 10000));
            if (!hooked.StartsWith("viewhook on", StringComparison.Ordinal)) { Send(port, "timescale 1", 60000); logger.LogError("game shot: " + hooked); return 1; }
            string locked = FirstLine(Send(port, "camlock 1", 10000));
            if (!locked.StartsWith("camlock on", StringComparison.Ordinal)) { Send(port, "viewhook 0", 10000); Send(port, "timescale 1", 60000); logger.LogError("game shot: " + locked); return 1; }
            if (!hud) Send(port, "hud 0", 60000);
            int failed = 0;
            try
            {
                foreach (var shot in shots)
                {
                    string warped = FirstLine(Send(port, "camwarp " + Spaced(shot.Item2), 10000));
                    if (!warped.StartsWith("camera warped", StringComparison.Ordinal)) { logger.LogError("game shot: " + warped); failed++; continue; }
                    if (shot.Item3 != null) Console.Out.WriteLine(FirstLine(Send(port, "camaim " + Spaced(shot.Item3), 10000)));
                    System.Threading.Thread.Sleep((int)(wait * 1000));
                    string png = shot.Item1 != null ? System.IO.Path.Combine(outPath, shot.Item1 + ".png")
                               : outPath ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frostycli-shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                    string bmp = System.IO.Path.ChangeExtension(System.IO.Path.GetFullPath(png), ".bmp");
                    string grabbed = FirstLine(Send(port, "grabframe " + bmp.Replace('\\', '/'), 30000));
                    if (!grabbed.StartsWith("captured", StringComparison.Ordinal) || !System.IO.File.Exists(bmp)) { logger.LogError("game shot: " + grabbed); failed++; continue; }
                    using (var image = new System.Drawing.Bitmap(bmp))
                        image.Save(System.IO.Path.GetFullPath(png), System.Drawing.Imaging.ImageFormat.Png);
                    System.IO.File.Delete(bmp);
                    Console.Out.WriteLine("shot " + (shot.Item1 ?? "") + " -> " + System.IO.Path.GetFullPath(png));
                }
            }
            finally
            {
                if (!hud) Send(port, "hud 1", 60000);
                if (!keep) { Send(port, "camlock 0", 10000); Send(port, "viewhook 0", 10000); }
                Send(port, "timescale 1", 60000);
            }
            return failed == 0 ? 0 : 1;
        }

        private static string Option(List<string> args, string name)
        {
            int at = args.IndexOf(name);
            return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
        }

        // "1,2,3" or "1 2 3" -> "1 2 3"
        internal static string Spaced(string xyz) =>
            string.Join(" ", (xyz ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries));

        // timeline <recording> [--all] [--match <a,b,...>]: a level's start-up at a glance - every
        // stream-in/out, OnStreamedIn and game-mode start in a recording, in order, with times.
        // Repeats are folded (first time and count) unless --all.
        private static readonly string[] TimelineWords =
            { "StreamIn", "StreamOut", "OnStreamedIn", "OnStreamedOut", "StartGameMode", "GameMode", "OnStarted", "RoundStart", "OnRoundStarted" };

        private static int Timeline(List<string> args, ExportLogger logger)
        {
            bool all = args.Remove("--all");
            string[] words = TimelineWords;
            int matchAt = args.IndexOf("--match");
            if (matchAt >= 0 && matchAt + 1 < args.Count)
            {
                words = args[matchAt + 1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                args.RemoveRange(matchAt, 2);
            }
            if (args.Count != 1) { logger.LogError("game timeline: usage: game timeline <recording> [--all] [--match <a,b,...>]"); return 2; }

            var seen = new Dictionary<string, int>();
            var order = new List<KeyValuePair<string, string>>();       // (time, line)
            foreach (string raw in System.IO.File.ReadLines(args[0]))
            {
                string line = raw.Trim();
                int arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow < 0) continue;
                // a stream/mode word on either side of the wire, as an event or field name
                string body = line;
                int where = body.IndexOf("  @", StringComparison.Ordinal);
                string wire = where >= 0 ? body.Substring(0, where) : body;
                if (!words.Any(w => wire.IndexOf("." + w, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                var time = System.Text.RegularExpressions.Regex.Match(line, @"^\d+\.\d+");
                string rest = time.Success ? line.Substring(time.Length).Trim() : line;
                if (all) { order.Add(new KeyValuePair<string, string>(time.Value, rest)); continue; }
                string key = Normalize(line, false);
                if (seen.ContainsKey(key)) { seen[key]++; continue; }
                seen[key] = 1;
                order.Add(new KeyValuePair<string, string>(time.Value, rest));
            }
            foreach (var entry in order)
            {
                string key = Normalize(entry.Value, false);
                int count = all || !seen.ContainsKey(key) ? 1 : seen[key];
                Console.Out.WriteLine(string.Format("{0,9} s  {1}{2}", entry.Key, entry.Value, count > 1 ? "   (x" + count + ")" : ""));
            }
            if (order.Count == 0)
                Console.Out.WriteLine("no stream or game-mode wires in " + args[0] + " - record from load with `mod test --record`");
            return 0;
        }

        // crash [--log <gamelink.log>] [--known <known-crashes.json>]: the newest crash in the module's
        // log explained - fault, known cause, stack, and the last logic before it (see CrashReport).
        private static int Crash(List<string> args)
        {
            string log = CrashReport.DefaultLog, known = CrashReport.FindKnownCrashes();
            for (int i = 0; i + 1 < args.Count; i++)
            {
                if (args[i] == "--log") log = args[++i];
                else if (args[i] == "--known") known = args[++i];
            }
            Console.Out.WriteLine(CrashReport.Build(log, known));
            return 0;
        }

        // ---- diff / wires: compare recordings, find wires that never fired ---------------------

        // One wire in a comparable form: no time, no realm, no value, no "(+n)" / "net". --loose also
        // drops instance tags and the blueprint, so a port can be compared with the vanilla map it
        // copied (different paths, different instance indices).
        private static string Normalize(string line, bool loose)
        {
            string s = line.Trim();
            var time = System.Text.RegularExpressions.Regex.Match(s, @"^\d+\.\d+ ");
            if (time.Success) s = s.Substring(time.Length);
            if (s.Length > 2 && (s[0] == 'S' || s[0] == 'C' || s[0] == 'E' || s[0] == 'P') && s[1] == ' ') s = s.Substring(2);
            int at = s.IndexOf("  @", StringComparison.Ordinal);
            string body = at >= 0 ? s.Substring(0, at) : s;
            string where = at >= 0 ? s.Substring(at) : "";
            int equals = body.IndexOf(" = ", StringComparison.Ordinal);
            if (equals >= 0) body = body.Substring(0, equals);
            body = System.Text.RegularExpressions.Regex.Replace(body, @" \(\+\d+\)", "").Replace(" net", "");
            if (!loose) return body + where;
            return System.Text.RegularExpressions.Regex.Replace(body, @"\[[0-9a-f]+\]|#[0-9a-f]{8}(-[0-9a-f-]{27})?", "");
        }

        private sealed class Seen { public int Count; public string FirstTime; public int Order; }

        // For PlayTest: a wire's comparison key, and every wire key in a recording.
        internal static string WireKey(string line) => Normalize(line, false);
        internal static IEnumerable<string> RecordedWires(string path) => ReadRecording(path, false).Keys;

        private static Dictionary<string, Seen> ReadRecording(string path, bool loose)
        {
            var seen = new Dictionary<string, Seen>();
            foreach (string line in System.IO.File.ReadLines(path))
            {
                if (line.Trim().Length == 0) continue;
                string key = Normalize(line, loose);
                if (!seen.TryGetValue(key, out Seen s))
                {
                    var time = System.Text.RegularExpressions.Regex.Match(line, @"^\d+\.\d+");
                    seen[key] = s = new Seen { FirstTime = time.Success ? time.Value : "", Order = seen.Count };
                }
                s.Count++;
            }
            return seen;
        }

        // diff <a> <b> [--loose] [--max <n>]: wires that fired in one recording and never in the
        // other, in the order the run that has them first fired them; the first of A's is where B
        // went another way (e.g. vanilla vs a port whose game mode never starts).
        private static int Diff(List<string> args, ExportLogger logger)
        {
            bool loose = args.Remove("--loose");
            int max = 40;
            int maxAt = args.IndexOf("--max");
            if (maxAt >= 0 && maxAt + 1 < args.Count) { max = int.Parse(args[maxAt + 1], System.Globalization.CultureInfo.InvariantCulture); args.RemoveRange(maxAt, 2); }
            if (args.Count != 2) { logger.LogError("game diff: usage: game diff <a.txt> <b.txt> [--loose] [--max <n>]"); return 2; }
            var a = ReadRecording(args[0], loose);
            var b = ReadRecording(args[1], loose);
            var onlyA = a.Where(kv => !b.ContainsKey(kv.Key)).OrderBy(kv => kv.Value.Order).ToList();
            var onlyB = b.Where(kv => !a.ContainsKey(kv.Key)).OrderBy(kv => kv.Value.Order).ToList();
            Console.Out.WriteLine(string.Format("A {0}: {1} distinct wires | B {2}: {3} distinct | shared {4}{5}",
                args[0], a.Count, args[1], b.Count, a.Count - onlyA.Count, loose ? " (loose: no instance tags or blueprints)" : ""));
            if (onlyA.Count > 0)
                Console.Out.WriteLine("first divergence - the first wire A fired that B never did (A at " +
                                      onlyA[0].Value.FirstTime + " s):\n  " + onlyA[0].Key);
            Console.Out.WriteLine();
            Console.Out.WriteLine("only in A (" + onlyA.Count + "), in A's order:");
            foreach (var kv in onlyA.Take(max))
                Console.Out.WriteLine(string.Format("  {0,9} s  x{1,-5} {2}", kv.Value.FirstTime, kv.Value.Count, kv.Key));
            Console.Out.WriteLine("only in B (" + onlyB.Count + "), in B's order:");
            foreach (var kv in onlyB.Take(max))
                Console.Out.WriteLine(string.Format("  {0,9} s  x{1,-5} {2}", kv.Value.FirstTime, kv.Value.Count, kv.Key));
            return 0;
        }

        // wires <blueprint filter> [--events|--props] [--against <recording>]... [--never]: every
        // connection of the matching loaded blueprints; with recordings, each is marked fired or never.
        private static int Wires(int port, List<string> args, ExportLogger logger)
        {
            var recordings = new List<string>();
            bool neverOnly = args.Remove("--never");
            string kind = args.Remove("--events") ? "--events " : args.Remove("--props") ? "--props " : "";
            for (int i = args.IndexOf("--against"); i >= 0; i = args.IndexOf("--against"))
            {
                if (i + 1 >= args.Count) { logger.LogError("game wires: --against needs a recording"); return 2; }
                recordings.Add(args[i + 1]);
                args.RemoveRange(i, 2);
            }
            if (args.Count == 0) { logger.LogError("game wires: missing <blueprint filter>"); return 2; }

            string[] listing = Send(port, "wires " + kind + string.Join(" ", args), 120000)
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (recordings.Count == 0)
            {
                foreach (string line in listing) Console.Out.WriteLine(line.TrimEnd('\r'));
                return 0;
            }

            var fired = new HashSet<string>();
            foreach (string path in recordings)
                foreach (string key in ReadRecording(path, false).Keys)
                    fired.Add(key);
            int firedCount = 0, never = 0;
            var output = new List<string>();
            for (int i = 1; i < listing.Length; i++)
            {
                string wire = listing[i].TrimEnd('\r');
                bool hit = fired.Contains(Normalize(wire, false));
                if (hit) firedCount++; else never++;
                if (!hit || !neverOnly)
                    output.Add((hit ? "fired  " : "NEVER  ") + wire);
            }
            Console.Out.WriteLine(listing.Length > 0 ? listing[0].TrimEnd('\r') : "");
            Console.Out.WriteLine(string.Format("{0} fired, {1} never fired in {2}", firedCount, never, string.Join(", ", recordings)));
            foreach (string line in output) Console.Out.WriteLine(line);
            return 0;
        }

        // Empty a feed into a recording: read until a read returns less than a full page, each line
        // prefixed with seconds on `clock` (the `game record` format). Used by `mod test --record`
        // while it waits for the verdict, so the level's own start-up logic lands in the file.
        internal static int DrainFeed(int port, string feed, System.IO.TextWriter file, Stopwatch clock)
        {
            int written = 0;
            for (int page = 0; page < 50; page++)
            {
                string[] lines = Send(port, feed + " 5000", 20000).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0 || !lines[0].Contains(" shown,"))
                    break;                                          // never on yet, or not answering
                string stamp = clock.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 1; i < lines.Length; i++)
                    file.WriteLine(stamp + " " + lines[i].TrimEnd('\r'));
                written += lines.Length - 1;
                if (lines.Length - 1 < 5000)
                    break;
            }
            return written;
        }

        // One command per connection; the link never closes, so the reply ends after a quiet gap -
        // the same framing tools/gamelink/gl.py uses.
        internal static string Send(int port, string command, int timeoutMs)
        {
            using (var client = new TcpClient())
            {
                client.Connect("127.0.0.1", port);
                var stream = client.GetStream();
                byte[] request = Encoding.ASCII.GetBytes(command + "\n");
                stream.Write(request, 0, request.Length);
                stream.ReadTimeout = 400;
                var reply = new StringBuilder();
                var buffer = new byte[65536];
                var clock = Stopwatch.StartNew();
                int quiet = 0;
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    int read;
                    try { read = stream.Read(buffer, 0, buffer.Length); }
                    catch (System.IO.IOException)
                    {
                        if (reply.Length > 0 && ++quiet >= 2) break;
                        continue;
                    }
                    if (read == 0) break;
                    reply.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    quiet = 0;
                }
                return reply.ToString().TrimEnd();
            }
        }

        private static int Print(string reply)
        {
            Console.Out.WriteLine(reply);
            return 0;
        }

        internal static string FirstLine(string text)
        {
            int newline = text.IndexOf('\n');
            return (newline < 0 ? text : text.Substring(0, newline)).TrimEnd('\r');
        }
    }
}
