using System;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace Spike5
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: Spike5.exe <swbf2-game-path>");
                return 1;
            }
            string gamePath = args[0];
            string outFile  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Spike5_results.md");

            var sb = new StringBuilder();
            sb.AppendLine("# Spike 5 Results — Texture Export + EbxClone");
            sb.AppendLine();
            sb.AppendLine("Game path: " + gamePath);
            sb.AppendLine();

            var logger = new SpikeLogger(sb);
            try { Session.Initialize("swbf2", gamePath, null, null, logger); }
            catch (Exception ex)
            {
                sb.AppendLine("Session.Initialize FAILED: " + ex.Message);
                File.WriteAllText(outFile, sb.ToString());
                return 1;
            }

            // 1. List all loaded types with "Texture" in name
            sb.AppendLine("## Types containing 'Texture'");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types)
                    if (t.Name.IndexOf("Texture", StringComparison.OrdinalIgnoreCase) >= 0 && t.IsPublic)
                        sb.AppendLine("- " + t.FullName + " (asm=" + asm.GetName().Name + ")");
            }

            // 2. Find a TextureAsset entry
            sb.AppendLine();
            sb.AppendLine("## Finding TextureAsset Entry");
            EbxAssetEntry texEntry = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (e.Type == "TextureAsset" || e.Type == "TextureBaseAsset")
                { texEntry = e; break; }
            }
            if (texEntry == null)
            {
                sb.AppendLine("No TextureAsset entry found in EnumerateEbx().");
            }
            else
            {
                sb.AppendLine("- Found: " + texEntry.Name + " (type=" + texEntry.Type + ")");
                EbxAsset asset = App.AssetManager.GetEbx(texEntry);
                object root = asset.RootObject;
                sb.AppendLine("- RootObject type: " + root.GetType().FullName);
                sb.AppendLine();
                sb.AppendLine("### Root properties");
                foreach (var p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    sb.AppendLine("- " + p.PropertyType.Name + " " + p.Name);
                sb.AppendLine();
                ulong rid = 0;
                try { rid = (ulong)((dynamic)root).Resource; sb.AppendLine("- Resource RID = 0x" + rid.ToString("X16")); }
                catch (Exception ex) { sb.AppendLine("- Could not read .Resource: " + ex.Message); }
                if (rid != 0)
                {
                    ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                    sb.AppendLine("- ResEntry: " + (resEntry != null ? resEntry.Name + " (resType=" + resEntry.ResType + ")" : "NULL"));
                }
            }

            // 3. Reflect for TextureUtils / TextureExporter / TextureImporter
            sb.AppendLine();
            sb.AppendLine("## Texture Utility Types");
            string[] candidates = { "TextureUtils", "TextureExporter", "TextureImporter", "Texture", "TextureBase" };
            foreach (string typeName in candidates)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] ts;
                    try { ts = asm.GetTypes(); } catch { continue; }
                    foreach (var t in ts)
                    {
                        if (t.Name == typeName)
                        {
                            sb.AppendLine("- " + t.FullName + " (" + asm.GetName().Name + ")");
                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                                sb.AppendLine("    " + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), pp => pp.ParameterType.Name + " " + pp.Name)) + ")");
                        }
                    }
                }
            }

            // 4. EbxAsset Clone/Copy
            sb.AppendLine();
            sb.AppendLine("## EbxAsset Clone/Copy");
            Type ebxAssetType = typeof(EbxAsset);
            foreach (var m in ebxAssetType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name.IndexOf("Clone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Name.IndexOf("Copy",  StringComparison.OrdinalIgnoreCase) >= 0)
                    sb.AppendLine("- " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), pp => pp.ParameterType.Name + " " + pp.Name)) + ")");
            // Also list all EbxAsset public methods for reference
            sb.AppendLine("### All EbxAsset public methods:");
            foreach (var m in ebxAssetType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                sb.AppendLine("  - " + m.ReturnType.Name + " " + m.Name);

            // 5. AssetManager Add* methods
            sb.AppendLine();
            sb.AppendLine("## AssetManager Add* methods");
            foreach (var m in App.AssetManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name.StartsWith("Add", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("- " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), pp => pp.ParameterType.Name + " " + pp.Name)) + ")");

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
