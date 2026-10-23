using System;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Interfaces;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace Spike4
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: Spike4.exe <swbf2-game-path> <fbproject-path>");
                return 1;
            }
            string gamePath    = args[0];
            string projectPath = args[1];
            string outFile     = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Spike4_results.md");

            var sb = new StringBuilder();
            sb.AppendLine("# Spike 4 Results — AssetManager.ModifyEbx Headless");
            sb.AppendLine();
            sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Game path: " + gamePath);
            sb.AppendLine("Project:   " + projectPath);
            sb.AppendLine();

            var logger = new SpikeLogger(sb);

            // 1. SDK init
            sb.AppendLine("## SDK Initialization");
            try
            {
                Session.Initialize("swbf2", gamePath, null, null, logger);
                sb.AppendLine("- Session.Initialize: OK");
            }
            catch (Exception ex)
            {
                sb.AppendLine("- Session.Initialize FAILED: " + ex.GetType().Name + ": " + ex.Message);
                File.WriteAllText(outFile, sb.ToString());
                return 1;
            }

            // 2. Reflect for Modify* and Add* methods on AssetManager
            sb.AppendLine();
            sb.AppendLine("## AssetManager Modify*/Add* methods");
            if (App.AssetManager == null)
            {
                sb.AppendLine("App.AssetManager is null after Session.Initialize.");
                File.WriteAllText(outFile, sb.ToString());
                return 1;
            }
            Type amType = App.AssetManager.GetType();
            sb.AppendLine("AssetManager type: " + amType.FullName);
            foreach (var m in amType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name.IndexOf("Modify", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Name.IndexOf("Add",    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string sig = m.ReturnType.Name + " " + m.Name + "(";
                    var pps = m.GetParameters();
                    for (int i = 0; i < pps.Length; i++)
                        sig += pps[i].ParameterType.Name + " " + pps[i].Name + (i < pps.Length - 1 ? ", " : "");
                    sig += ")";
                    sb.AppendLine("- " + sig);
                }
            }

            // 3. Load project
            sb.AppendLine();
            sb.AppendLine("## Project Load");
            try
            {
                Session.Project.Load(projectPath, logger);
                sb.AppendLine("- Project loaded: OK");
            }
            catch (Exception ex)
            {
                sb.AppendLine("- Load FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }

            // 4. Pick first EBX entry
            sb.AppendLine();
            sb.AppendLine("## Pick Test EBX");
            EbxAssetEntry testEntry = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                testEntry = e;
                break;
            }
            if (testEntry == null)
            {
                sb.AppendLine("No EBX entries found.");
                File.WriteAllText(outFile, sb.ToString());
                return 1;
            }
            sb.AppendLine("- Picked: " + testEntry.Name + " (type=" + testEntry.Type + ")");

            // 5. Load asset, no-op write of first writable property
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(testEntry); }
            catch (Exception ex)
            {
                sb.AppendLine("- GetEbx FAILED: " + ex.Message);
                File.WriteAllText(outFile, sb.ToString());
                return 1;
            }
            object root = asset.RootObject;
            sb.AppendLine("- RootObject type: " + root.GetType().FullName);
            PropertyInfo firstWritable = null;
            foreach (var p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.CanRead && p.CanWrite && (p.PropertyType.IsValueType || p.PropertyType == typeof(string)))
                { firstWritable = p; break; }
            }
            if (firstWritable != null)
            {
                object cur = firstWritable.GetValue(root);
                try { firstWritable.SetValue(root, cur); sb.AppendLine("- No-op write of '" + firstWritable.Name + "': OK"); }
                catch (Exception ex) { sb.AppendLine("- No-op write FAILED: " + ex.Message); }
            }

            // 6. Call ModifyEbx
            sb.AppendLine();
            sb.AppendLine("## ModifyEbx Invocation");
            MethodInfo modifyEbx = amType.GetMethod("ModifyEbx",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(EbxAsset) }, null);
            if (modifyEbx == null)
            {
                sb.AppendLine("ModifyEbx(string, EbxAsset) NOT FOUND. All 'ModifyEbx' overloads:");
                foreach (var m in amType.GetMethods())
                    if (m.Name == "ModifyEbx") sb.AppendLine("  " + m.ToString());
                sb.AppendLine("Trying to find any method with 'Modify' + 'Ebx' in name:");
                foreach (var m in amType.GetMethods())
                    if (m.Name.IndexOf("Modify", StringComparison.OrdinalIgnoreCase) >= 0
                     && m.Name.IndexOf("Ebx",    StringComparison.OrdinalIgnoreCase) >= 0)
                        sb.AppendLine("  " + m.ToString());
            }
            else
            {
                try
                {
                    modifyEbx.Invoke(App.AssetManager, new object[] { testEntry.Name, asset });
                    sb.AppendLine("- ModifyEbx(string, EbxAsset) invocation: OK");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("- ModifyEbx FAILED: " + ex.GetType().Name + ": " + ex.Message);
                    if (ex.InnerException != null)
                        sb.AppendLine("  Inner: " + ex.InnerException.Message);
                }
            }

            // 7. Save project to temp path, check size
            sb.AppendLine();
            sb.AppendLine("## Save Modified Project");
            string tempSave = Path.Combine(Path.GetTempPath(), "spike4_modified.fbproject");
            bool savedOk = Session.Project.Save(tempSave, logger);
            if (savedOk && File.Exists(tempSave))
                sb.AppendLine("- Saved OK: " + new FileInfo(tempSave).Length + " bytes at " + tempSave);
            else
                sb.AppendLine("- Save FAILED or file missing");
            try { File.Delete(tempSave); } catch { }

            // 8. Summary
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine("Review above sections. Key deliverables:");
            sb.AppendLine("1. Exact ModifyEbx signature (or alternative method name)");
            sb.AppendLine("2. Whether ModifyEbx invocation succeeded headlessly");
            sb.AppendLine("3. Whether project save produced non-zero output");

            File.WriteAllText(outFile, sb.ToString());
            Console.WriteLine("[DONE] Results: " + outFile);
            return 0;
        }
    }

    class SpikeLogger : ILogger
    {
        private readonly StringBuilder _sb;
        public SpikeLogger(StringBuilder sb) { _sb = sb; }
        public void Log(string text, params object[] vars)
        {
            if (text != null && text.StartsWith("progress:", StringComparison.OrdinalIgnoreCase)) return;
            string msg = vars.Length > 0 ? string.Format(text, vars) : text;
            Console.WriteLine("[SDK] " + msg);
            _sb.AppendLine("    [SDK] " + msg);
        }
        public void LogWarning(string text, params object[] vars)
        {
            string msg = vars.Length > 0 ? string.Format(text, vars) : text;
            Console.WriteLine("[WARN] " + msg);
            _sb.AppendLine("    [WARN] " + msg);
        }
        public void LogError(string text, params object[] vars)
        {
            string msg = vars.Length > 0 ? string.Format(text, vars) : text;
            Console.WriteLine("[ERR] " + msg);
            _sb.AppendLine("    [ERR] " + msg);
        }
    }
}
