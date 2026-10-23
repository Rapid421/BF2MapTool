using System;
using System.IO;
using System.Linq;
using System.Reflection;
namespace FrostyCLI.Tests
{
    class Program
    {
        public static int Main(string[] args)
        {
            // Integration tests share one Session, initialized once with an "Adapters" probe dir.
            // Whichever test inits first would otherwise be the only one to stage its adapter, so a
            // later test (e.g. mesh export) silently gets "no handler". Stage ALL adapters here,
            // overwriting (also avoids stale-adapter copies from previous builds), before any test runs.
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string adaptersDir = Path.Combine(exeDir, "Adapters");
                Directory.CreateDirectory(adaptersDir);
                foreach (string dll in Directory.GetFiles(exeDir, "FrostyCLI.Adapters.*.dll"))
                    File.Copy(dll, Path.Combine(adaptersDir, Path.GetFileName(dll)), true);
            }
            catch (Exception ex) { Console.WriteLine("WARN staging adapters: " + ex.Message); }

            // --filter <substr> : run only tests whose "TypeName.MethodName" contains <substr> (case-insensitive)
            // --unit            : skip all integration tests, even when a game-path env var is set (fast feedback loop)
            string filter = null;
            bool unitOnly = false;
            bool runSlow = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--filter" && i + 1 < args.Length) filter = args[++i];
                else if (args[i] == "--unit") unitOnly = true;
                else if (args[i] == "--slow") runSlow = true;
            }

            int passed = 0, failed = 0, skipped = 0;

            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (method.GetCustomAttribute<TestAttribute>() == null) continue;

                    string fullName = type.Name + "." + method.Name;
                    if (filter != null && fullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var integAttr = method.GetCustomAttribute<IntegrationTestAttribute>();
                    if (integAttr != null)
                    {
                        if (unitOnly)
                        {
                            Console.WriteLine("  SKIP " + fullName + " (--unit)");
                            skipped++;
                            continue;
                        }
                        if (integAttr.Slow && !runSlow)
                        {
                            Console.WriteLine("  SKIP " + fullName + " (slow; pass --slow to run)");
                            skipped++;
                            continue;
                        }

                        bool shouldSkip;
                        string skipHint;
                        string game = integAttr.Game ?? "";
                        if (game == "swbf1")
                        {
                            shouldSkip = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1") == null;
                            skipHint = "set FROSTYCLI_GAME_PATH_SWBF1 to enable";
                        }
                        else if (game == "swbf2")
                        {
                            shouldSkip = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2") == null;
                            skipHint = "set FROSTYCLI_GAME_PATH_SWBF2 to enable";
                        }
                        else
                        {
                            shouldSkip = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1") == null
                                      && Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2") == null;
                            skipHint = "set FROSTYCLI_GAME_PATH_SWBF1 or FROSTYCLI_GAME_PATH_SWBF2 to enable";
                        }
                        if (shouldSkip)
                        {
                            Console.WriteLine("  SKIP " + fullName + " (" + skipHint + ")");
                            skipped++;
                            continue;
                        }
                    }

                    try
                    {
                        method.Invoke(Activator.CreateInstance(type), null);
                        Console.WriteLine("  PASS " + fullName);
                        passed++;
                    }
                    catch (TargetInvocationException ex)
                    {
                        Exception inner = ex.InnerException ?? ex;
                        Console.WriteLine("  FAIL " + fullName + ": " + inner.Message);
                        // First few stack frames help pinpoint the failure.
                        if (inner.StackTrace != null)
                            foreach (var line in inner.StackTrace.Split('\n').Take(4))
                                Console.WriteLine("        " + line.Trim());
                        failed++;
                    }
                }
            }

            Console.WriteLine("\n" + passed + " passed, " + failed + " failed, " + skipped + " skipped");
            return failed > 0 ? 1 : 0;
        }
    }
}
