using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

namespace Spike1
{
    class Program
    {
        static int Main(string[] args)
        {
            string gamePath = args.Length > 0 ? args[0]
                : @"C:\Program Files (x86)\Origin Games\STAR WARS Battlefront II";
            Console.WriteLine("=== Spike1: PluginManager headless init ===");
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles", name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            try { ProfilesLibrary.Initialize(new Profile[0]); ProfilesLibrary.Initialize("starwarsbattlefrontii"); Console.WriteLine("[OK] Profile: " + ProfilesLibrary.ProfileName); }
            catch (Exception ex) { Console.WriteLine("[FAIL] ProfilesLibrary: " + ex.Message); return 1; }
            try { TypeLibrary.Initialize(); Console.WriteLine("[OK] TypeLibrary"); }
            catch (Exception ex) { Console.WriteLine("[FAIL] TypeLibrary: " + ex.Message); return 1; }
            var logger = new SpikeLogger();
            Console.WriteLine("Instantiating PluginManager...");
            PluginManager pm;
            try { pm = new PluginManager(logger, PluginManagerType.Editor); }
            catch (Exception ex) { Console.WriteLine("[FAIL] ctor: " + ex.GetType().Name + ": " + ex.Message + "\n  => WpfStub may be needed"); return 1; }
            try { pm.Initialize(); }
            catch (Exception ex) { Console.WriteLine("[FAIL] Initialize: " + ex.Message); return 1; }
            Console.WriteLine("[PASS] PluginManager works headlessly.");
            foreach (var p in pm.Plugins) Console.WriteLine("  Plugin: " + p.Name);
            return 0;
        }
    }
    class SpikeLogger : ILogger
    {
        public void Log(string text, params object[] vars) { if (text != null && text.StartsWith("progress:", StringComparison.OrdinalIgnoreCase)) return; Console.WriteLine("[SDK] " + (vars.Length > 0 ? string.Format(text, vars) : text)); }
        public void LogWarning(string text, params object[] vars) => Console.WriteLine("[WARN] " + (vars.Length > 0 ? string.Format(text, vars) : text));
        public void LogError(string text, params object[] vars) => Console.WriteLine("[ERR] " + (vars.Length > 0 ? string.Format(text, vars) : text));
    }
}
