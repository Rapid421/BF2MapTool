using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // The last crash in FrostyGameLink's log, explained: fault address, known cause (from
    // tools/gametest/known-crashes.json, matched on the fault and the first stack entries), the stack,
    // and the 'feed:' lines - the last logic events and property writes before it, written by the
    // module's crash handler when a feed was on (`mod test --feeds`).
    //
    // The log (C:\ProgramData\FrostyGameLink\gamelink.log) is shared by every injected process and its
    // lines carry a time of day only, so the report takes the newest crash and prints its time.
    public static class CrashReport
    {
        public const string DefaultLog = @"C:\ProgramData\FrostyGameLink\gamelink.log";

        private static readonly Regex FaultLine = new Regex(@"^\[(?<time>[\d:.]+)\] (?<tag>crash|fault): code (?<code>0x[0-9A-Fa-f]+)(?<name>[^@]*?) at (?<where>\S+)(?: \((?<note>[^)]*)\))? \(thread (?<thread>\d+)");
        private static readonly Regex StackLine = new Regex(@"^\[[\d:.]+\] crash:\s+stack\+0x[0-9A-Fa-f]+ -> (?<where>game\+0x[0-9A-Fa-f]+)");
        private static readonly Regex FeedLine = new Regex(@"^\[[\d:.]+\] feed: (?<text>.*)$");

        public static string Build(string logPath, string knownPath)
        {
            if (!File.Exists(logPath))
                return "crash report: no FrostyGameLink log at " + logPath;
            List<string> lines = ReadTail(logPath, 6000);

            // A terminal crash ('---- process is going down ----' then 'crash:' lines) beats a logged
            // first-chance access violation, which the engine may have handled.
            int start = lines.FindLastIndex(l => l.EndsWith("---- process is going down ----", StringComparison.Ordinal));
            bool terminal = start >= 0;
            if (!terminal)
                start = lines.FindLastIndex(l => FaultLine.IsMatch(l) && l.Contains("fault: code 0xC0000005"));
            if (start < 0)
                return "crash report: no crash or access violation in the last " + lines.Count + " lines of " + logPath;

            Match fault = null;
            var stack = new List<string>();
            var feed = new List<string>();
            for (int i = start; i < lines.Count; i++)
            {
                string line = lines[i];
                if (i > start && terminal && line.EndsWith("---- FrostyGameLink attached ----", StringComparison.Ordinal))
                    break;                                     // the next process's log starts here
                Match m;
                if (fault == null && (m = FaultLine.Match(line)).Success) { fault = m; continue; }
                if ((m = StackLine.Match(line)).Success) { if (!stack.Contains(m.Groups["where"].Value)) stack.Add(m.Groups["where"].Value); continue; }
                if ((m = FeedLine.Match(line)).Success) feed.Add(m.Groups["text"].Value);
                if (!terminal && i > start + 40) break;
            }

            var out_ = new StringBuilder();
            if (fault == null)
            {
                out_.AppendLine("crash report: the process went down but the fault line is missing (log " + logPath + ")");
            }
            else
            {
                out_.AppendLine(string.Format("{0} at {1}: code {2}{3} at {4} (thread {5})",
                    terminal ? "CRASH" : "access violation (logged, may have been handled)",
                    fault.Groups["time"].Value, fault.Groups["code"].Value, fault.Groups["name"].Value.TrimEnd(),
                    fault.Groups["where"].Value, fault.Groups["thread"].Value));
            }

            var known = LoadKnown(knownPath);
            var candidates = new List<string>();
            if (fault != null) candidates.Add(fault.Groups["where"].Value);
            candidates.AddRange(stack.Take(8));
            bool matched = false;
            foreach (string where in candidates)
            {
                JObject hit = Match(known, where);
                if (hit == null) continue;
                matched = true;
                out_.AppendLine();
                out_.AppendLine(string.Format("KNOWN ({0}{1}): {2}", where, where == candidates[0] ? "" : ", on the stack",
                    (string)hit["confidence"] ?? "observed"));
                out_.AppendLine("  cause: " + (string)hit["cause"]);
                out_.AppendLine("  fix:   " + (string)hit["fix"]);
                break;
            }
            if (!matched)
                out_.AppendLine(known.Count == 0 ? "(no known-crashes table found)" : "not in known-crashes.json - once diagnosed, add it there");

            if (stack.Count > 0)
            {
                out_.AppendLine();
                out_.AppendLine("stack (game addresses, top first): " + string.Join("  ", stack.Take(12)));
            }
            out_.AppendLine();
            if (feed.Count > 0)
            {
                out_.AppendLine("last logic before the crash (from the feeds):");
                foreach (string f in feed) out_.AppendLine("  " + f);
            }
            else
            {
                out_.AppendLine("no feed lines - run with `mod test --feeds` (or `game trace on` / `game prop on`) to get the last logic before a crash");
            }
            return out_.ToString().TrimEnd();
        }

        public static string FindKnownCrashes()
        {
            try
            {
                string d = Path.GetDirectoryName(typeof(CrashReport).Assembly.Location);
                for (int i = 0; i < 7 && d != null; i++)
                {
                    string cand = Path.Combine(d, "tools", "gametest", "known-crashes.json");
                    if (File.Exists(cand)) return cand;
                    d = Path.GetDirectoryName(d);
                }
            }
            catch { }
            return null;
        }

        private static List<JObject> LoadKnown(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new List<JObject>();
            try { return ((JArray)JObject.Parse(File.ReadAllText(path))["crashes"]).OfType<JObject>().ToList(); }
            catch { return new List<JObject>(); }
        }

        private static ulong? Offset(string where)
        {
            if (where == null || !where.StartsWith("game+0x", StringComparison.OrdinalIgnoreCase)) return null;
            return ulong.TryParse(where.Substring(7), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? v : (ulong?)null;
        }

        private static JObject Match(List<JObject> known, string where)
        {
            ulong? at = Offset(where);
            if (at == null) return null;
            foreach (JObject k in known)
            {
                var addresses = new List<string>();
                if (k["address"] != null) addresses.Add((string)k["address"]);
                if (k["also"] is JArray also) addresses.AddRange(also.Select(a => (string)a));
                if (addresses.Any(a => Offset(a) == at)) return k;
                if (k["range"] is JArray range && range.Count == 2)
                {
                    ulong? lo = Offset((string)range[0]), hi = Offset((string)range[1]);
                    if (lo != null && hi != null && at >= lo && at <= hi) return k;
                }
            }
            return null;
        }

        // The game holds the log open; read it shared and keep the last `count` lines.
        private static List<string> ReadTail(string path, int count)
        {
            var queue = new Queue<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    queue.Enqueue(line);
                    if (queue.Count > count) queue.Dequeue();
                }
            }
            return queue.ToList();
        }
    }
}
