using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Frosty.Core;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    public class LocCommand : ICommand
    {
        public string Name => "loc";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli loc <get|set|search> ..."); return 1; }
            switch (args[0].ToLowerInvariant())
            {
                case "get":    return ExecuteGet(Sub(args), logger, formatter);
                case "set":    return ExecuteSet(Sub(args), logger, formatter);
                case "search": return ExecuteSearch(Sub(args), logger, formatter);
                default: Console.Error.WriteLine("loc: unknown subcommand '" + args[0] + "'"); return 1;
            }
        }
        private static string[] Sub(string[] a) { var r = new string[a.Length - 1]; Array.Copy(a, 1, r, 0, r.Length); return r; }

        private static ILocalizedStringDatabase GetDb()
        {
            ILocalizedStringDatabase db = LocalizedStringDatabase.Current;
            if (db == null || db.GetType().Name == "DefaultLocalizedStringDatabase")
                return null;
            try { db.Initialize(); } catch { }
            return db;
        }

        private static bool TryParseKey(string s, out uint key)
        {
            if (s != null && (s.StartsWith("0x") || s.StartsWith("0X")))
                return uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key);
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out key);
        }

        public static int ExecuteGet(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string keyStr = null;
            foreach (string a in args) if (!a.StartsWith("--")) { keyStr = a; break; }
            if (keyStr == null) { Console.Error.WriteLine("loc get: missing <key>"); return 1; }
            uint key; if (!TryParseKey(keyStr, out key)) { Console.Error.WriteLine("loc get: invalid key (uint or 0xHEX): " + keyStr); return 1; }
            var db = GetDb();
            if (db == null) { Console.Error.WriteLine("loc get: no localized string database loaded for this game/profile"); return 1; }
            Console.Out.WriteLine(db.GetString(key) ?? "");
            return 0;
        }

        public static int ExecuteSet(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string keyStr = null, value = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--value" && i + 1 < args.Length) value = args[++i];
                else if (!args[i].StartsWith("--")) keyStr = args[i];
            }
            if (keyStr == null) { Console.Error.WriteLine("loc set: missing <key>"); return 1; }
            if (value == null)  { Console.Error.WriteLine("loc set: missing --value"); return 1; }
            uint key; if (!TryParseKey(keyStr, out key)) { Console.Error.WriteLine("loc set: invalid key: " + keyStr); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("loc set: no project loaded."); return 1; }
            var db = GetDb();
            if (db == null) { Console.Error.WriteLine("loc set: no localized string database loaded for this game/profile"); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "set localized string 0x{0:X8} = \"{1}\"", key, value); return 0; }
            try { db.SetString(key, value); }
            catch (Exception ex) { Console.Error.WriteLine("loc set: failed: " + ex.Message); return 1; }
            return 0;
        }

        public static int ExecuteSearch(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string query = null, outFile = null; bool regex = false; int limit = 200;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--regex") regex = true;
                else if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (!args[i].StartsWith("--")) query = args[i];
            }
            if (query == null) { Console.Error.WriteLine("loc search: missing <query>"); return 1; }
            var db = GetDb();
            if (db == null) { Console.Error.WriteLine("loc search: no localized string database loaded for this game/profile"); return 1; }

            Regex rx = regex ? new Regex(query, RegexOptions.IgnoreCase) : null;
            var results = new List<object>();
            foreach (uint key in db.EnumerateStrings())
            {
                string val = db.GetString(key) ?? "";
                bool match = regex ? rx.IsMatch(val) : (val.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!match) continue;
                results.Add(new { key = key, value = val });
                if (results.Count >= limit) break;
            }
            string json = JsonConvert.SerializeObject(results, Formatting.Indented);
            if (outFile != null) { System.IO.File.WriteAllText(outFile, json); Console.Out.WriteLine("wrote " + results.Count + " matches to " + outFile); }
            else Console.Out.WriteLine(json);
            return 0;
        }
    }
}
