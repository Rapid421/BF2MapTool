using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using Frosty.Core.Attributes;

namespace Spike2
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("=== Spike2: Plugin DLL load safety with WpfStub ===");

            // Step A: Install WPF Application stub before any WPF-dependent type
            Console.WriteLine("Installing WPF Application stub...");
            InstallWpfStub();
            Console.WriteLine("WPF stub installed OK");

            // Step B: Check FrostyMessageBox.ShowInternal
            Console.WriteLine("Checking FrostyMessageBox.ShowInternal...");
            var fmbType = Type.GetType("Frosty.Core.Controls.FrostyMessageBox, FrostyCore");
            if (fmbType != null)
            {
                var showInternal = fmbType.GetMethod("ShowInternal",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (showInternal != null)
                    Console.WriteLine("  [FOUND] ShowInternal -- will need RuntimeMethodHandle patch");
                else
                    Console.WriteLine("  [NOT FOUND] ShowInternal -- no patch needed or name changed");
            }
            else
            {
                Console.WriteLine("  [NOT FOUND] FrostyMessageBox type -- skipping");
            }

            // Step C: Load MeshSetPlugin.dll
            string pluginPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MeshSetPlugin.dll");
            if (!File.Exists(pluginPath))
            {
                Console.WriteLine("[FAIL] MeshSetPlugin.dll not found at " + pluginPath);
                return 1;
            }
            Console.WriteLine("Loading " + pluginPath + " via Assembly.LoadFrom...");
            Assembly asm;
            try
            {
                asm = Assembly.LoadFrom(pluginPath);
                Console.WriteLine("  [PASS] Assembly loaded");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }

            // Step D: Enumerate RegisterAssetDefinition attributes and try Activator.CreateInstance
            var attrs = asm.GetCustomAttributes(typeof(RegisterAssetDefinitionAttribute), false);
            Console.WriteLine("  Found " + attrs.Length + " RegisterAssetDefinition attributes:");
            int passed = 0, failed = 0;
            foreach (RegisterAssetDefinitionAttribute attr in attrs)
            {
                Console.Write("    [" + attr.LookupType + "] -> " + attr.AssetDefinitionType.Name + " ... ");
                try
                {
                    Activator.CreateInstance(attr.AssetDefinitionType);
                    Console.WriteLine("PASS (instantiated OK)");
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
                    failed++;
                }
            }

            Console.WriteLine("\nResult: " + passed + " passed, " + failed + " failed");
            return failed > 0 ? 1 : 0;
        }

        static void InstallWpfStub()
        {
            if (System.Windows.Application.Current != null) return;
            var ready = new ManualResetEventSlim(false);
            var t = new Thread(() =>
            {
                var app = new Application();
                app.Startup += (s, e) => ready.Set();
                app.Run();
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
