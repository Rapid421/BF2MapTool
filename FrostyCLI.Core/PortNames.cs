using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Reverse lookup from a connection port id to the port NAME(s) that hash to it.
    ///
    /// Port ids are one-way DJB2-XOR hashes, so a graph dump can only show `0x0b87df4b` unless
    /// something supplies the vocabulary. A wordlist harvested from EBX string sections turns most
    /// of those back into "Out" / "Enabled" / "SetTrue", which is the difference between reading a
    /// logic graph and staring at hex.
    ///
    /// Collisions are real and are NOT resolved silently: a 19,777-word list already contains 452
    /// colliding hashes (e.g. "2CP" and "gp_vehicle_uwing_gunship_MaxCountTestCaseName"). Every
    /// candidate is returned and the caller shows them all; picking the first would quietly invent
    /// a wrong name.
    ///
    /// Sources, in precedence order:
    ///   1. an explicit path (`--names &lt;file&gt;`)
    ///   2. the FROSTYCLI_PORT_NAMES environment variable
    ///   3. portnames.txt beside the executable
    ///   4. data/portnames.txt in the repository/working directory
    /// </summary>
    public static class PortNames
    {
        private static Dictionary<uint, List<string>> _map;
        private static string _loadedFrom;
        private static int _wordCount;

        public static string LoadedFrom { get { return _loadedFrom; } }
        public static int WordCount     { get { return _wordCount; } }
        public static bool IsLoaded     { get { return _map != null && _map.Count > 0; } }

        public static uint Hash(string text)
        {
            uint h = 5381;
            foreach (byte b in Encoding.ASCII.GetBytes(text ?? string.Empty))
                h = unchecked((h * 33) ^ b);
            return h;
        }

        private static IEnumerable<string> CandidatePaths(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath)) yield return explicitPath;

            string env = null;
            try { env = Environment.GetEnvironmentVariable("FROSTYCLI_PORT_NAMES"); } catch { }
            if (!string.IsNullOrEmpty(env)) yield return env;

            string exeDir = null;
            try { exeDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location); } catch { }
            if (!string.IsNullOrEmpty(exeDir))
            {
                yield return Path.Combine(exeDir, "portnames.txt");
                yield return Path.Combine(exeDir, "data", "portnames.txt");
            }

            yield return Path.Combine("data", "portnames.txt");
            yield return "portnames.txt";
        }

        /// <summary>
        /// Loads the catalog once. Returns false when no wordlist was found -- callers then fall back
        /// to printing raw ids, which is a degraded display, never a wrong one.
        /// </summary>
        public static bool Load(string explicitPath = null, bool force = false)
        {
            if (_map != null && !force) return _map.Count > 0;

            foreach (string path in CandidatePaths(explicitPath))
            {
                if (string.IsNullOrEmpty(path)) continue;
                string full;
                try { full = Path.GetFullPath(path); } catch { continue; }
                if (!File.Exists(full)) continue;

                var map = new Dictionary<uint, List<string>>();
                int words = 0;
                try
                {
                    foreach (string raw in File.ReadLines(full))
                    {
                        string w = raw?.Trim();
                        if (string.IsNullOrEmpty(w)) continue;
                        uint h = Hash(w);
                        List<string> bucket;
                        if (!map.TryGetValue(h, out bucket)) { bucket = new List<string>(1); map[h] = bucket; }
                        if (!bucket.Contains(w)) bucket.Add(w);
                        words++;
                    }
                }
                catch { continue; }

                _map = map; _loadedFrom = full; _wordCount = words;
                return _map.Count > 0;
            }

            _map = new Dictionary<uint, List<string>>();
            _loadedFrom = null; _wordCount = 0;
            return false;
        }

        /// <summary>All names hashing to <paramref name="id"/>; empty when unknown.</summary>
        public static IList<string> Resolve(uint id)
        {
            if (_map == null) Load();
            List<string> names;
            return _map != null && _map.TryGetValue(id, out names) ? names : new List<string>();
        }

        public static IList<string> Resolve(int id) { return Resolve(unchecked((uint)id)); }

        /// <summary>
        /// Renders an id for display: the name when unambiguous, "a|b" when several candidates
        /// collide, and the bare hex when the vocabulary does not contain it. Never guesses.
        /// </summary>
        public static string Describe(int id)
        {
            uint u = unchecked((uint)id);
            IList<string> names = Resolve(u);
            if (names.Count == 1) return names[0];
            if (names.Count > 1)  return string.Join("|", names);
            return "0x" + u.ToString("x8");
        }
    }
}
