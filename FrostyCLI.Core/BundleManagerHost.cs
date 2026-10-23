using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Frosty.Core;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    // Runs FrostyForge's BundleManager plugin headlessly, by reflection, against the session's
    // AssetManager and project.
    //
    // Why this exists: adding an asset to a level and adding a bundle is not enough to make the
    // game stream it. A SubLevel bundle only streams when its *parents* stream, and that parent
    // chain is a precomputed graph that lives outside the EBX -- editing InclusionSettings has no
    // effect on it. BundleManager is the only implementation of that pass, so a headless workflow
    // that stops short of it produces a mod that builds, lints and loads, and silently ships
    // content the engine never reaches. Porting its ~3,800 lines was the alternative; a partial
    // port mis-bundles silently, which is precisely the failure this avoids.
    //
    // The plugin is a WPF editor plugin, so three things have to be arranged:
    //
    //   1. Assembly unification. The installed plugins were compiled against FrostyCore 1.0.0.1;
    //      the CLI ships 1.0.0.0. Ordinary probing fails the version check and would load a
    //      *second* FrostyCore, whose App.AssetManager is null -- the plugin would then quietly
    //      operate on an empty game. The resolver below hands back the already-loaded assembly by
    //      simple name so the plugin binds to the CLI's App.
    //   2. A FrostyTaskWindow. BmCache.ReadingCache and BundleManager.BundleEnumeration both call
    //      task.Update() unconditionally, so null is not an option. FrostyTaskWindow's constructor
    //      is private and touches Application.Current.MainWindow (null here), but Update() only
    //      assigns two plain CLR properties and raises PropertyChanged, which is null with nothing
    //      bound. An uninitialized instance therefore satisfies it without running any WPF ctor.
    //   3. Config.Current, which every BMO_* option read goes through.
    public static class BundleManagerHost
    {
        // Load order matters: BundleManager's dependencies must be resolvable as it JITs.
        private static readonly string[] PluginDlls =
        {
            "RootInstanceEntriesPlugin.dll",
            "AtlasTexturePlugin.dll",
            "MeshSetPlugin.dll",
            "DuplicationPlugin.dll",
            "BundleManager.dll",
        };


        public sealed class Result
        {
            public bool CacheLoaded;
            public int AllowedBundles;
            public string LogPath;
        }

        // The resolver and its probe path now live in EditorPluginHost, shared with
        // PathfindingHost. Behaviour is unchanged: version-agnostic unification against
        // already-loaded assemblies, then probing the plugin directory and its parent.
        private static void EnsureResolver(string dir)
        {
            EditorPluginHost.EnsureDependencyResolver(dir);
        }

        internal static Type FindType(string fullName)
        {
            return EditorPluginHost.FindType(fullName);
        }

        /// <summary>
        /// Loads the BundleManager plugin (and its plugin dependencies) from an installed Frosty
        /// editor's Plugins directory. Returns the BundleManager assembly.
        /// </summary>
        public static Assembly LoadPlugin(string pluginsDir, ILogger logger)
        {
            if (string.IsNullOrEmpty(pluginsDir))
                throw new ArgumentException("A Frosty editor Plugins directory is required.", "pluginsDir");
            if (!Directory.Exists(pluginsDir))
                throw new DirectoryNotFoundException("Plugins directory not found: " + pluginsDir);

            EnsureResolver(pluginsDir);

            Assembly bundleManager = null;
            foreach (string dll in PluginDlls)
            {
                string path = Path.Combine(pluginsDir, dll);
                if (!File.Exists(path))
                    throw new FileNotFoundException("BundleManager dependency not installed: " + path, path);
                Assembly asm = Assembly.LoadFrom(path);
                if (dll == "BundleManager.dll") bundleManager = asm;
                if (logger != null) logger.Log("BundleManager: loaded {0}", dll);
            }
            return bundleManager;
        }

        /// <summary>
        /// An uninitialized FrostyTaskWindow: enough for the plugin's task.Update() progress calls,
        /// without running a WPF constructor that needs a MainWindow we do not have.
        /// </summary>
        internal static object CreateTaskStub()
        {
            Type twType = FindType("Frosty.Core.Windows.FrostyTaskWindow");
            if (twType == null)
                throw new InvalidOperationException("Frosty.Core.Windows.FrostyTaskWindow not found; is FrostyCore loaded?");
            return FormatterServices.GetUninitializedObject(twType);
        }

        /// <summary>
        /// Applies BMO_* plugin options for this run. These are the same switches the editor
        /// exposes in its options dialog; several default to false (FantasyBattles, HvsV,
        /// PlanetaryBattles, Mode3/5/6/7/C), which silently excludes those sub-level bundles from
        /// the pass, so a headless caller has to be able to set them.
        /// </summary>
        public static void SetOptions(IEnumerable<KeyValuePair<string, object>> options)
        {
            if (options == null) return;
            HeadlessProject.EnsureConfig();
            foreach (var kv in options)
                Config.Add(kv.Key, kv.Value);
        }

        /// <summary>
        /// Runs the plugin's bundle-completion pass. <paramref name="levelBundles"/> null completes
        /// every allowed bundle ("Complete Bundles"); a list restricts sub-level bundles to those
        /// parented by the named level bundles ("Complete Level Bundles").
        /// </summary>
        public static Result Run(Assembly bundleManagerAsm, IList<int> levelBundles,
                                 bool blockNetworkRegistries, string cacheHostDir, ILogger logger)
        {
            if (App.AssetManager == null)
                throw new InvalidOperationException("AssetManager not initialized.");

            HeadlessProject.EnsureConfig();

            // The plugin logs through App.Logger throughout, including on its error paths.
            if (App.Logger == null && logger != null) App.Logger = logger;

            // FileSystem.CacheName is the RELATIVE path "Caches/<profile>", so every cache the
            // plugin reads or writes -- the bundle graph, the root-instance db, the prerequisites
            // folder, the CSV log -- resolves against the process working directory. Those caches
            // belong to an installed editor, not to the CLI, so run the pass from the editor's root
            // and put the working directory back afterwards.
            string savedCwd = Environment.CurrentDirectory;
            if (!string.IsNullOrEmpty(cacheHostDir) && Directory.Exists(cacheHostDir))
                Environment.CurrentDirectory = cacheHostDir;
            try { return RunCore(bundleManagerAsm, levelBundles, blockNetworkRegistries); }
            finally { Environment.CurrentDirectory = savedCwd; }
        }

        private static Result RunCore(Assembly bundleManagerAsm, IList<int> levelBundles,
                                      bool blockNetworkRegistries)
        {
            object task = CreateTaskStub();

            Type bmCacheType = bundleManagerAsm.GetType("BundleManager.BmCache");
            if (bmCacheType == null)
                throw new InvalidOperationException("BundleManager.BmCache not found in the loaded plugin.");

            MethodInfo loadCache = null;
            foreach (MethodInfo m in bmCacheType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "LoadCache") continue;
                if (m.GetParameters().Length == 1) { loadCache = m; break; }
            }
            if (loadCache == null)
                throw new InvalidOperationException("BmCache.LoadCache(FrostyTaskWindow) not found.");

            var result = new Result();

            // LoadCache returns false when a required cache is missing. Headlessly the plugin's
            // "generate one?" prompts answer OK (not Yes) via FrostyMessageBoxPatch, so it declines
            // rather than starting a ~20-minute cache build behind a CLI invocation. Report that
            // instead of running the pass over an empty bundle graph, which would clear every
            // bundle edit and recompute nothing.
            result.CacheLoaded = (bool)loadCache.Invoke(null, new object[] { task });
            if (!result.CacheLoaded) return result;

            Type bmType = bundleManagerAsm.GetType("BundleManager.BundleManager");
            if (bmType == null)
                throw new InvalidOperationException("BundleManager.BundleManager not found in the loaded plugin.");

            object bm = Activator.CreateInstance(bmType,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null, new object[] { task, blockNetworkRegistries }, null);

            MethodInfo complete = bmType.GetMethod("CompleteBundleManage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (complete == null)
                throw new InvalidOperationException("BundleManager.CompleteBundleManage not found.");

            object arg = null;
            if (levelBundles != null)
            {
                var list = new List<int>();
                foreach (int id in levelBundles) list.Add(id);
                arg = list;
            }
            complete.Invoke(bm, new object[] { arg });

            result.AllowedBundles = App.WhitelistedBundles != null ? App.WhitelistedBundles.Count : 0;
            if (App.FileSystem != null)
                result.LogPath = Path.GetFullPath(App.FileSystem.CacheName + "_BundleManager_LogList.csv");
            return result;
        }

        /// <summary>
        /// Resolves level bundle paths (e.g. "win32/Levels/MP/Kashyyyk_01/Kashyyyk_01") to bundle ids.
        /// </summary>
        public static List<int> ResolveLevelBundles(IEnumerable<string> names, out List<string> missing)
        {
            missing = new List<string>();
            var ids = new List<int>();
            foreach (string n in names)
            {
                string name = n.Trim();
                if (name.Length == 0) continue;
                BundleEntry found = null;
                foreach (BundleEntry b in App.AssetManager.EnumerateBundles())
                {
                    if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) { found = b; break; }
                }
                if (found == null) { missing.Add(name); continue; }
                ids.Add(App.AssetManager.GetBundleId(found));
            }
            return ids;
        }

        /// <summary>
        /// Loads only the plugin's cache and returns, per shipped chunk id, the FirstMip and H32 BundleManager would
        /// write when it adds that chunk to a bundle: H32 = HashString(h32Name ?? owningEbx.Name, lower). A shipped
        /// chunk's catalog entry carries neither (they live in its own bundle's chunkMeta), so a chunk mirrored into a
        /// new bundle needs them from here.
        /// </summary>
        public static Dictionary<Guid, KeyValuePair<int, uint>> LoadChunkMeta(Assembly bundleManagerAsm, string cacheHostDir, ILogger logger)
        {
            HeadlessProject.EnsureConfig();
            if (App.Logger == null && logger != null) App.Logger = logger;
            string savedCwd = Environment.CurrentDirectory;
            if (!string.IsNullOrEmpty(cacheHostDir) && Directory.Exists(cacheHostDir)) Environment.CurrentDirectory = cacheHostDir;
            try
            {
                Type bmCacheType = bundleManagerAsm.GetType("BundleManager.BmCache");
                MethodInfo loadCache = null;
                foreach (MethodInfo m in bmCacheType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "LoadCache" && m.GetParameters().Length == 1) { loadCache = m; break; }
                if (!(bool)loadCache.Invoke(null, new object[] { CreateTaskStub() })) return null;
                var data = (System.Collections.IDictionary)bmCacheType.GetField("UnmodifiedAssetData", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                var result = new Dictionary<Guid, KeyValuePair<int, uint>>();
                foreach (System.Collections.DictionaryEntry kv in data)
                {
                    var ebx = (EbxAssetEntry)kv.Key;
                    object ad = kv.Value;
                    var chunks = (System.Collections.IEnumerable)ad.GetType().GetField("Chunks").GetValue(ad);
                    if (chunks == null) continue;
                    foreach (object t in chunks)
                    {
                        Type tt = t.GetType();
                        var ce = (ChunkAssetEntry)tt.GetField("Item1").GetValue(t);
                        int firstMip = (int)tt.GetField("Item2").GetValue(t);
                        string h32Name = (string)tt.GetField("Item3").GetValue(t);
                        if (ce == null || result.ContainsKey(ce.Id)) continue;
                        uint h32 = (uint)FrostySdk.Utils.HashString(h32Name ?? ebx.Name, true);
                        result[ce.Id] = new KeyValuePair<int, uint>(firstMip, h32);
                    }
                }
                return result;
            }
            finally { Environment.CurrentDirectory = savedCwd; }
        }
    }
}
