using System;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace Spike3
{
    class Program
    {
        static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: Spike3.exe <swbf2-game-path> <fbproject-path>");
                return 1;
            }
            string gamePath    = args[0];
            string projectPath = args[1];
            string outFile     = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Spike3_results.md");

            var sb = new StringBuilder();
            sb.AppendLine("# Spike 3 Results — FrostyProject Headless Compatibility");
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

            // 2. Reflect on FrostyProject
            sb.AppendLine();
            sb.AppendLine("## FrostyProject API (via reflection on FrostyCore.dll)");
            Type fpType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                fpType = asm.GetType("Frosty.Core.FrostyProject");
                if (fpType != null) break;
            }
            if (fpType == null)
            {
                sb.AppendLine("ERROR: FrostyProject type not found in any loaded assembly.");
            }
            else
            {
                sb.AppendLine("Found: " + fpType.FullName + " in " + fpType.Assembly.GetName().Name);
                sb.AppendLine();
                sb.AppendLine("### Constructors");
                foreach (var ctor in fpType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    var ctorParams = ctor.GetParameters();
                    string sig = "(";
                    for (int i = 0; i < ctorParams.Length; i++)
                        sig += ctorParams[i].ParameterType.Name + " " + ctorParams[i].Name + (i < ctorParams.Length - 1 ? ", " : "");
                    sig += ")";
                    sb.AppendLine("- " + sig);
                }
                sb.AppendLine();
                sb.AppendLine("### Public Methods");
                foreach (var m in fpType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var mParams = m.GetParameters();
                    string pStr = "";
                    for (int i = 0; i < mParams.Length; i++)
                        pStr += mParams[i].ParameterType.Name + " " + mParams[i].Name + (i < mParams.Length - 1 ? ", " : "");
                    sb.AppendLine("- " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")");
                }
                sb.AppendLine();
                sb.AppendLine("### Public Static Methods");
                foreach (var m in fpType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    var mParams = m.GetParameters();
                    string pStr = "";
                    for (int i = 0; i < mParams.Length; i++)
                        pStr += mParams[i].ParameterType.Name + " " + mParams[i].Name + (i < mParams.Length - 1 ? ", " : "");
                    sb.AppendLine("- static " + m.ReturnType.Name + " " + m.Name + "(" + pStr + ")");
                }
                sb.AppendLine();
                sb.AppendLine("### Public Properties");
                foreach (var p in fpType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    sb.AppendLine("- " + p.PropertyType.Name + " " + p.Name);
            }

            // 3. Look for mod-build types
            sb.AppendLine();
            sb.AppendLine("## Mod-Build Related Types");
            string[] modTypeNames = new[]
            {
                "Frosty.Core.ModExecuter", "Frosty.Core.FrostyModWriter",
                "Frosty.Core.ModSettings",  "FrostySdk.ModSettings",
                "Frosty.Core.ModuleSettings"
            };
            foreach (string typeName in modTypeNames)
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    t = asm.GetType(typeName);
                    if (t != null) break;
                }
                if (t != null)
                {
                    sb.AppendLine("FOUND: " + typeName);
                    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        sb.AppendLine("  - " + m.MemberType + " " + m.Name);
                }
                else
                {
                    sb.AppendLine("NOT FOUND: " + typeName);
                }
            }

            // 3b. Enumerate ModSettings fields/properties from GetModSettings() return type
            if (fpType != null)
            {
                sb.AppendLine();
                sb.AppendLine("## ModSettings Type Fields");
                MethodInfo getModSettingsMethod = fpType.GetMethod("GetModSettings");
                if (getModSettingsMethod == null)
                {
                    sb.AppendLine("GetModSettings() method NOT FOUND on FrostyProject.");
                }
                else
                {
                    Type modSettingsType = getModSettingsMethod.ReturnType;
                    sb.AppendLine("ModSettings type: " + modSettingsType.FullName);
                    sb.AppendLine("Assembly: " + modSettingsType.Assembly.GetName().Name);
                    sb.AppendLine();
                    sb.AppendLine("### Fields");
                    foreach (FieldInfo fieldInfo in modSettingsType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                        sb.AppendLine("- " + fieldInfo.FieldType.Name + " " + fieldInfo.Name);
                    sb.AppendLine();
                    sb.AppendLine("### Properties");
                    foreach (PropertyInfo propInfo in modSettingsType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        sb.AppendLine("- " + propInfo.PropertyType.Name + " " + propInfo.Name);
                }
            }

            // 4. AssetManager snapshot before load
            sb.AppendLine();
            sb.AppendLine("## AssetManager State");
            int countBefore = 0;
            if (App.AssetManager != null)
            {
                foreach (var e in App.AssetManager.EnumerateEbx()) countBefore++;
                sb.AppendLine("EBX count before project load: " + countBefore);
            }
            else
            {
                sb.AppendLine("AssetManager is null.");
            }

            // 4b. Initialize Config.Current so FrostyProject() ctor can call Config.Get(...)
            sb.AppendLine();
            sb.AppendLine("## Config Initialization");
            Type configType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                configType = asm.GetType("Frosty.Core.Config");
                if (configType != null) break;
            }
            if (configType != null)
            {
                var currentProp = configType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                object existing = currentProp != null ? currentProp.GetValue(null) : null;
                if (existing == null)
                {
                    try
                    {
                        object newConfig = Activator.CreateInstance(configType, true);
                        currentProp.SetValue(null, newConfig, BindingFlags.NonPublic | BindingFlags.Static, null, null, null);
                        sb.AppendLine("- Config.Current initialized via reflection: OK");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("- Config.Current init FAILED: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
                else
                {
                    sb.AppendLine("- Config.Current already set: OK");
                }
            }
            else
            {
                sb.AppendLine("- Config type not found — skipping");
            }

            // 5. Attempt FrostyProject.Load
            sb.AppendLine();
            sb.AppendLine("## FrostyProject.Load Attempt");
            object projectInstance = null;
            if (fpType != null && File.Exists(projectPath))
            {
                try
                {
                    projectInstance = Activator.CreateInstance(fpType);
                    sb.AppendLine("- Activator.CreateInstance(FrostyProject): OK");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("- Activator.CreateInstance FAILED: " + ex.GetType().Name + ": " + ex.Message);
                    if (ex.InnerException != null)
                        sb.AppendLine("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                }

                if (projectInstance != null)
                {
                    var loadMethod = fpType.GetMethod("Load",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(string) }, null);
                    if (loadMethod == null)
                    {
                        sb.AppendLine("- Load(string) method NOT FOUND. Searching all Load overloads...");
                        foreach (var m in fpType.GetMethods())
                            if (m.Name == "Load")
                                sb.AppendLine("  Overload: " + m.ToString());
                    }
                    else
                    {
                        try
                        {
                            loadMethod.Invoke(projectInstance, new object[] { projectPath });
                            sb.AppendLine("- Load(\"" + projectPath + "\"): OK");
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine("- Load FAILED: " + ex.GetType().Name + ": " + ex.Message);
                            if (ex.InnerException != null)
                                sb.AppendLine("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                            if (ex.InnerException != null && ex.InnerException.StackTrace != null)
                                sb.AppendLine("  Stack: " + ex.InnerException.StackTrace.Replace(Environment.NewLine, "\n  "));
                        }
                    }
                }
            }
            else
            {
                if (fpType == null) sb.AppendLine("Skipped — FrostyProject type not found.");
                else sb.AppendLine("Skipped — project file not found at: " + projectPath);
            }

            // 6. AssetManager snapshot after load
            if (App.AssetManager != null && projectInstance != null)
            {
                int countAfter = 0;
                foreach (var e in App.AssetManager.EnumerateEbx()) countAfter++;
                sb.AppendLine("EBX count after project load: " + countAfter);
                sb.AppendLine("AssetManager mutated by Load: " + (countAfter != countBefore ? "YES (count changed)" : "UNKNOWN (count same; may still differ)"));
            }

            // 7. Attempt Save
            sb.AppendLine();
            sb.AppendLine("## FrostyProject.Save Attempt");
            if (fpType != null && projectInstance != null)
            {
                string tempSave = Path.Combine(Path.GetTempPath(), "spike3_save_test.fbproject");
                MethodInfo saveMethod = fpType.GetMethod("Save", new[] { typeof(string), typeof(bool) });
                if (saveMethod == null)
                {
                    sb.AppendLine("- Save(string, bool) NOT FOUND. All Save overloads:");
                    foreach (MethodInfo m in fpType.GetMethods())
                        if (m.Name == "Save")
                            sb.AppendLine("  Overload: " + m.ToString());
                }
                else
                {
                    try
                    {
                        saveMethod.Invoke(projectInstance, new object[] { tempSave, false });
                        sb.AppendLine("- Save(tempPath, false): OK. File size: " +
                            (File.Exists(tempSave) ? new FileInfo(tempSave).Length + " bytes" : "FILE NOT CREATED"));
                        try { File.Delete(tempSave); } catch { }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("- Save FAILED: " + ex.GetType().Name + ": " + ex.Message);
                        if (ex.InnerException != null)
                            sb.AppendLine("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                        if (ex.InnerException != null && ex.InnerException.StackTrace != null)
                            sb.AppendLine("  Stack: " + ex.InnerException.StackTrace.Replace(Environment.NewLine, "\n  "));
                    }
                }
            }
            else sb.AppendLine("Skipped — no project instance.");

            // 8. Attempt WriteToMod / mod-build
            sb.AppendLine();
            sb.AppendLine("## Mod Build Attempt");
            if (fpType != null && projectInstance != null)
            {
                string[] modBuildCandidates = new[] { "WriteToMod", "Build", "Export", "WriteToFile" };
                bool found = false;
                foreach (string candidate in modBuildCandidates)
                {
                    foreach (MethodInfo writeToModMethod in fpType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (writeToModMethod.Name == candidate)
                        {
                            sb.AppendLine("FOUND mod-build candidate: " + writeToModMethod.ReturnType.Name + " " + writeToModMethod.Name + "(" + string.Join(", ", Array.ConvertAll(writeToModMethod.GetParameters(), p => p.ParameterType.Name + " " + p.Name)) + ")");
                            found = true;

                            if (candidate == "WriteToMod" && projectInstance != null)
                            {
                                string tempModPath = Path.Combine(Path.GetTempPath(), "spike3_writetomod_test.fbmod");
                                try
                                {
                                    MethodInfo getModSettingsMethod2 = fpType.GetMethod("GetModSettings");
                                    Type modSettingsType2 = getModSettingsMethod2.ReturnType;
                                    object modSettingsInstance = Activator.CreateInstance(modSettingsType2);
                                    writeToModMethod.Invoke(projectInstance, new object[] { tempModPath, modSettingsInstance, false, System.Threading.CancellationToken.None });
                                    sb.AppendLine("- WriteToMod invocation: OK. File size: " +
                                        (File.Exists(tempModPath) ? new FileInfo(tempModPath).Length + " bytes" : "FILE NOT CREATED"));
                                    try { File.Delete(tempModPath); } catch { }
                                }
                                catch (Exception ex)
                                {
                                    sb.AppendLine("- WriteToMod FAILED: " + ex.GetType().Name + ": " + ex.Message);
                                    if (ex.InnerException != null)
                                        sb.AppendLine("  Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                                    Exception walkEx = ex.InnerException ?? ex;
                                    if (walkEx.StackTrace != null)
                                        sb.AppendLine("  Stack: " + walkEx.StackTrace.Replace(Environment.NewLine, "\n  "));
                                }
                            }
                        }
                    }
                }
                if (!found)
                    sb.AppendLine("No mod-build method found (tried WriteToMod, Build, Export, WriteToFile).");
            }
            else sb.AppendLine("Skipped — no project instance.");

            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine("Review sections above. Key facts to carry into HeadlessProject implementation:");
            sb.AppendLine("1. FrostyProject constructor signature");
            sb.AppendLine("2. Load method name and signature");
            sb.AppendLine("3. Save method name and signature");
            sb.AppendLine("4. Mod-build method name, class, and signature");
            sb.AppendLine("5. Any WPF exceptions and their stack traces");
            sb.AppendLine("6. Whether AssetManager is mutated by Load");

            File.WriteAllText(outFile, sb.ToString());
            Console.WriteLine("[DONE] Results written to: " + outFile);
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
