using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace FrostyCLI.Core
{
    // Shared machinery for hosting an installed Frosty *editor* plugin inside the headless CLI.
    // Extracted from BundleManagerHost so PathfindingHost can sit on the same resolver rather than
    // carry a second copy of it.
    //
    // The resolver is the whole trick. Installed plugins were compiled against FrostyCore 1.0.0.1
    // and the CLI ships 1.0.0.0, so ordinary probing fails the version check and loads a *second*
    // FrostyCore whose App.AssetManager is null -- the plugin then quietly operates on an empty
    // game. Unifying by simple name against what is already loaded is what makes a plugin bind to
    // the CLI's App instead.
    public static class EditorPluginHost
    {
        private static readonly List<string> s_probeDirs = new List<string>();
        private static bool s_resolverInstalled;

        /// <summary>
        /// Adds <paramref name="dir"/> (and its parent) to the probe path and installs the
        /// version-agnostic assembly resolver once per process. Safe to call repeatedly.
        /// </summary>
        public static void EnsureDependencyResolver(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            lock (s_probeDirs)
            {
                if (!s_probeDirs.Contains(dir)) s_probeDirs.Add(dir);
                string parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent) && !s_probeDirs.Contains(parent)) s_probeDirs.Add(parent);
                if (s_resolverInstalled) return;
                s_resolverInstalled = true;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string simple = new AssemblyName(e.Name).Name;

                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.IsDynamic) continue;
                    if (string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase))
                        return a;
                }

                string[] dirs;
                lock (s_probeDirs) dirs = s_probeDirs.ToArray();
                foreach (string d in dirs)
                {
                    string p = Path.Combine(d, simple + ".dll");
                    if (File.Exists(p))
                    {
                        try { return Assembly.LoadFrom(p); } catch { return null; }
                    }
                }
                return null;
            };
        }

        /// <summary>Finds a type by full name across every loaded assembly, or null.</summary>
        public static Type FindType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;
                Type t;
                try { t = a.GetType(fullName); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// Like <see cref="FindType"/>, but throws an actionable error instead of returning null.
        /// A missing plugin is by far the likeliest cause, and a bare TypeLoadException reads as a
        /// CLI bug rather than a setup problem.
        /// </summary>
        public static Type RequireType(string fullName)
        {
            Type t = FindType(fullName);
            if (t != null) return t;
            throw new InvalidOperationException(
                "Could not find the plugin type '" + fullName + "'. The Frosty editor plugin that "
                + "provides it is not loaded. Pass --frosty-plugins <dir> pointing at an installed "
                + "Frosty editor's Plugins directory, set \"handler-plugins\" in frostycli.json, or "
                + "set FROSTYCLI_FROSTY_PLUGINS.");
        }
    }
}
