using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
namespace FrostyCLI.Commands
{
    public struct GlobalOptions
    {
        public string Game;
        public string GamePath;
        public string Project;
        public string PluginsDir;
        public string HandlerPluginsDir;
        public string AdaptersDir;
        public bool   Json;
        public bool   Quiet;
        public bool   DryRun;
        public bool   Save;
        public string SaveAs;
    }

    public static class ArgParser
    {
        public static GlobalOptions ParseGlobal(string[] args, out string[] remaining)
        {
            var opts = new GlobalOptions { Game = "swbf2" };
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    // Next() bounds-checks: a trailing value-flag (e.g. `frostycli --game-path` with no
                    // value) leaves the option unset instead of throwing IndexOutOfRangeException.
                    case "--game":         { string v = Next(args, ref i); if (v != null) opts.Game = v.ToLowerInvariant(); break; }
                    case "--game-path":    opts.GamePath = Next(args, ref i) ?? opts.GamePath; break;
                    case "--project":      opts.Project = Next(args, ref i) ?? opts.Project; break;
                    case "--plugins-dir":  opts.PluginsDir = Next(args, ref i) ?? opts.PluginsDir; break;
                    case "--handler-plugins": opts.HandlerPluginsDir = Next(args, ref i) ?? opts.HandlerPluginsDir; break;
                    case "--adapters-dir": opts.AdaptersDir = Next(args, ref i) ?? opts.AdaptersDir; break;
                    case "--json":         opts.Json = true; break;
                    case "--quiet":        opts.Quiet = true; break;
                    case "--dry-run":      opts.DryRun = true; break;
                    case "--save":         opts.Save = true; break;
                    case "--save-as":      opts.SaveAs = Next(args, ref i) ?? opts.SaveAs; break;
                    default:               rest.Add(args[i]); break;
                }
            }
            remaining = rest.ToArray();
            return opts;
        }

        private static string Next(string[] args, ref int i) => (i + 1 < args.Length) ? args[++i] : null;

        // Fills options the user didn't pass explicitly from a `frostycli.json` (in the working dir, then
        // the exe dir) and then environment variables — so common invocations can drop `--game-path` etc.
        // Precedence: explicit flag > frostycli.json > env var. The selected game itself stays a flag
        // (default swbf2); only game-path/project/handler-plugins/plugins-dir are resolved here.
        //   frostycli.json keys: "game-path", "project", "handler-plugins", "plugins-dir"
        //   env: FROSTYCLI_GAME_PATH_SWBF2 / FROSTYCLI_GAME_PATH_SWBF1
        public static GlobalOptions ResolveDefaults(GlobalOptions opts)
        {
            JObject cfg = LoadConfig();
            Func<string, string> cfgVal = key =>
            {
                JToken t = cfg != null ? cfg[key] : null;
                string s = t != null ? t.ToString() : null;
                return string.IsNullOrWhiteSpace(s) ? null : s;
            };

            if (opts.GamePath == null)
            {
                string env = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_" + (opts.Game ?? "swbf2").ToUpperInvariant());
                opts.GamePath = cfgVal("game-path") ?? (string.IsNullOrWhiteSpace(env) ? null : env);
            }
            if (opts.Project == null)           opts.Project = cfgVal("project");
            if (opts.HandlerPluginsDir == null) opts.HandlerPluginsDir = cfgVal("handler-plugins");
            if (opts.PluginsDir == null)        opts.PluginsDir = cfgVal("plugins-dir");
            return opts;
        }

        private static JObject LoadConfig()
        {
            foreach (string dir in new[] { Directory.GetCurrentDirectory(), AppDomain.CurrentDomain.BaseDirectory })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string path = Path.Combine(dir, "frostycli.json");
                if (!File.Exists(path)) continue;
                try { return JObject.Parse(File.ReadAllText(path)); }
                catch { continue; } // a malformed config shouldn't crash the CLI, nor mask the next dir's
            }
            return null;
        }
    }
}
