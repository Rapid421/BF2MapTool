using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Frosty.Core;
using Frosty.Core.Attributes;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    public class PluginRegistry
    {
        private readonly Dictionary<string, AssetDefinition> _handlers
            = new Dictionary<string, AssetDefinition>(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<KeyValuePair<string, AssetDefinition>> All => _handlers;

        public void Discover(string pluginsDir, string adaptersDir, ILogger logger)
        {
            if (pluginsDir != null && Directory.Exists(pluginsDir))
                ScanDir(pluginsDir, logger);

            if (adaptersDir != null && Directory.Exists(adaptersDir))
                ScanDir(adaptersDir, logger);
        }

        // The FE editor plugins that carry merge-relevant custom ACTION handlers (localization,
        // weapon-table, and the SWBF2 merger's network-registry / team-data / UI-metadata / mesh-variation
        // / profile-options handlers). These load + instantiate headlessly (no WPF/UI in their handler
        // ctors). We load only this curated set from the editor's Plugins dir so a user can enable the
        // broader merge handlers without dragging in the ~60 WPF editor plugins. Loading them into the
        // appdomain is enough: HeadlessProject's [RegisterCustomHandler] scan then picks the handlers up
        // for mod build + `mod apply`.
        private static readonly string[] HandlerPluginFiles =
        {
            "BiowareLocalizationPlugin.dll",
            "FsLocalizationPlugin.dll",
            "DifficultyWeaponTableDataPlugin.dll",
            "Swbf2MergerPlugin.dll",
        };

        // Directories probed by the AssemblyResolve handler for handler-plugin dependencies (Prism.Wpf,
        // WindowsAPICodePack, the per-plugin support DLLs, ...) that live next to the plugins but aren't
        // on the exe's normal probe path. Installed once; harmless if the plugin set is never loaded.
        private static readonly HashSet<string> s_probeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_resolverInstalled;

        private static void EnsureDependencyResolver(string dir)
        {
            lock (s_probeDirs)
            {
                s_probeDirs.Add(dir);
                if (s_resolverInstalled) return;
                s_resolverInstalled = true;
            }
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                lock (s_resolving)
                {
                    if (s_resolving.Contains(name)) return null; // break recursion on a truly-missing dep
                    s_resolving.Add(name);
                }
                try
                {
                    string[] dirs;
                    lock (s_probeDirs) { dirs = new string[s_probeDirs.Count]; s_probeDirs.CopyTo(dirs); }
                    foreach (string d in dirs)
                    {
                        string p = Path.Combine(d, name + ".dll");
                        if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { return null; } }
                    }
                    return null;
                }
                finally { lock (s_resolving) { s_resolving.Remove(name); } }
            };
        }

        // Loads the curated handler-plugin set from a directory (typically the FE editor's Plugins
        // folder). Returns the number of handler-plugin DLLs successfully loaded into the appdomain.
        public int LoadHandlerPlugins(string dir, ILogger logger)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return 0;
            EnsureDependencyResolver(dir);
            int loaded = 0;
            foreach (string file in HandlerPluginFiles)
            {
                string path = Path.Combine(dir, file);
                if (!File.Exists(path))
                {
                    // These plugins ship under variant names -- the BFII merge handlers are installed
                    // as Swbf2MergerPlugin.Spartan.dll, for instance. An exact-name match silently
                    // skips them, and the consequence is invisible: the registry/MVDB merge handlers
                    // never register, so `mod build` produces a mod that looks fine and drops the
                    // merges. Accept "<stem>.<variant>.dll" beside the plain name.
                    string stem = Path.GetFileNameWithoutExtension(file);
                    string variant = null;
                    try
                    {
                        foreach (string cand in Directory.GetFiles(dir, stem + ".*.dll"))
                        { variant = cand; break; }
                    }
                    catch { }
                    if (variant == null)
                    { logger?.Log("HandlerPlugins: not present, skipped: {0}", file); continue; }
                    logger?.Log("HandlerPlugins: {0} not found, using variant {1}", file, Path.GetFileName(variant));
                    path = variant;
                }
                try
                {
                    Assembly asm = Assembly.LoadFrom(path);
                    RegisterAssembly(asm, logger);
                    loaded++;
                    logger?.Log("HandlerPlugins: loaded {0}", file);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning("HandlerPlugins: failed to load {0}: {1}", file, ex.Message);
                }
            }
            return loaded;
        }

        private void ScanDir(string dir, ILogger logger)
        {
            foreach (var dllPath in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dllPath);
                    RegisterAssembly(asm, logger);
                }
                catch (Exception ex)
                {
                    if (logger != null)
                        logger.LogWarning("PluginRegistry: failed to load {0}: {1}", dllPath, ex.Message);
                }
            }
        }

        public void RegisterAssembly(Assembly asm, ILogger logger = null)
        {
            var attrs = asm.GetCustomAttributes(typeof(RegisterAssetDefinitionAttribute), false);
            foreach (RegisterAssetDefinitionAttribute attr in attrs)
            {
                try
                {
                    var instance = (AssetDefinition)Activator.CreateInstance(attr.AssetDefinitionType);
                    _handlers[attr.LookupType.ToLower()] = instance;
                    if (logger != null)
                        logger.Log("PluginRegistry: registered [{0}] -> {1}",
                            attr.LookupType, attr.AssetDefinitionType.Name);
                }
                catch (Exception ex)
                {
                    if (logger != null)
                        logger.LogWarning("PluginRegistry: failed to instantiate {0} for [{1}]: {2}",
                            attr.AssetDefinitionType.Name, attr.LookupType, ex.Message);
                }
            }
        }

        public AssetDefinition GetHandler(string typeName)
        {
            if (typeName == null) return null;
            AssetDefinition handler;
            _handlers.TryGetValue(typeName.ToLower(), out handler);
            return handler;
        }

        public AssetDefinition GetHandler(EbxAssetEntry entry)
        {
            if (entry == null) return null;
            var handler = GetHandler(entry.Type);
            // SWBF1: manifest does not populate EBX type; fall back by name convention
            if (handler == null && string.IsNullOrEmpty(entry.Type)
                && entry.Name != null
                && entry.Name.EndsWith("_mesh", StringComparison.OrdinalIgnoreCase))
                handler = GetHandler("RigidMeshAsset");
            return handler;
        }
    }
}
