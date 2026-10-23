using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace Spike6
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: Spike6.exe <swbf2-game-path>");
                return 1;
            }
            string gamePath = args[0];
            string outDir   = AppDomain.CurrentDomain.BaseDirectory;
            string outFile  = Path.Combine(outDir, "Spike6_results.md");
            string dumpFile = Path.Combine(outDir, "Spike6_level_dump.json");

            var sb = new StringBuilder();
            sb.AppendLine("# Spike 6 Results — LevelData Entity Types");
            sb.AppendLine();
            sb.AppendLine("Game: " + gamePath);
            sb.AppendLine();

            var logger = new SpikeLogger(sb);
            try { Session.Initialize("swbf2", gamePath, null, null, logger); }
            catch (Exception ex)
            {
                sb.AppendLine("Session FAIL: " + ex.Message);
                File.WriteAllText(outFile, sb.ToString());
                return 1;
            }

            // Pick a level entry
            sb.AppendLine("## Locating SubWorld / level EBX");
            EbxAssetEntry levelEntry = App.AssetManager.GetEbxEntry("s2/levels/cloudcity_01/gameplay");
            if (levelEntry == null)
            {
                foreach (var e in App.AssetManager.EnumerateEbx())
                {
                    if (e.Type != null && (e.Type == "SubWorldData" || e.Type == "LevelData" || e.Type.IndexOf("Level", StringComparison.OrdinalIgnoreCase) >= 0))
                    { levelEntry = e; break; }
                }
            }
            if (levelEntry == null)
            {
                sb.AppendLine("No level EBX found.");
                File.WriteAllText(outFile, sb.ToString());
                return 1;
            }
            sb.AppendLine("- Picked: " + levelEntry.Name + " (type=" + levelEntry.Type + ")");

            EbxAsset asset = App.AssetManager.GetEbx(levelEntry);
            object root = asset.RootObject;
            sb.AppendLine("- Root type: " + root.GetType().FullName);

            // Dump root as JSON
            try
            {
                string json = JsonConvert.SerializeObject(root, Formatting.Indented);
                File.WriteAllText(dumpFile, json);
                sb.AppendLine("- Full dump written to: " + dumpFile);
            }
            catch (Exception ex) { sb.AppendLine("- JSON dump FAILED: " + ex.Message); }

            // Enumerate root properties
            sb.AppendLine();
            sb.AppendLine("## Root Properties");
            PropertyInfo entityListProp = null;
            foreach (var p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object val = null;
                try { val = p.GetValue(root); } catch { }
                bool isList = val is IList;
                sb.AppendLine("- " + p.PropertyType.Name + " " + p.Name + (isList ? " (IList, count=" + ((IList)val).Count + ")" : ""));
                if (isList && entityListProp == null && ((IList)val).Count > 0) entityListProp = p;
            }

            // Inspect entity type
            if (entityListProp != null)
            {
                sb.AppendLine();
                sb.AppendLine("## Entity List: " + entityListProp.Name);
                IList list = (IList)entityListProp.GetValue(root);
                sb.AppendLine("- Count: " + list.Count);
                object first = list[0];
                Type et = first.GetType();
                sb.AppendLine("- Entity type: " + et.FullName);
                sb.AppendLine("### Entity properties");
                foreach (var p in et.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    sb.AppendLine("- " + p.PropertyType.Name + " " + p.Name);
            }

            // Reflect for AssetManager.GetEbxEntry(Guid)
            sb.AppendLine();
            sb.AppendLine("## AssetManager.GetEbxEntry overloads");
            foreach (var m in App.AssetManager.GetType().GetMethods())
                if (m.Name == "GetEbxEntry")
                    sb.AppendLine("- " + m.ReturnType.Name + " GetEbxEntry(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), pp => pp.ParameterType.Name + " " + pp.Name)) + ")");

            File.WriteAllText(outFile, sb.ToString());
            Console.WriteLine("[DONE] Results: " + outFile);
            return 0;
        }
    }

    class SpikeLogger : ILogger
    {
        private readonly StringBuilder _sb;
        public SpikeLogger(StringBuilder sb) { _sb = sb; }
        public void Log(string text, params object[] vars) { string m = vars.Length > 0 ? string.Format(text, vars) : text; Console.WriteLine("[SDK] " + m); _sb.AppendLine("    [SDK] " + m); }
        public void LogWarning(string text, params object[] vars) { string m = vars.Length > 0 ? string.Format(text, vars) : text; Console.WriteLine("[WARN] " + m); _sb.AppendLine("    [WARN] " + m); }
        public void LogError(string text, params object[] vars) { string m = vars.Length > 0 ? string.Format(text, vars) : text; Console.WriteLine("[ERR] " + m); _sb.AppendLine("    [ERR] " + m); }
    }
}
