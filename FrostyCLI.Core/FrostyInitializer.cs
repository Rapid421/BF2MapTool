using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    public static class FrostyInitializer
    {
        // game: "swbf1" or "swbf2"
        public static void Initialize(string game, string gamePath, ILogger logger)
        {
            // Resolve the game path against the caller's working directory before we change it.
            if (!string.IsNullOrEmpty(gamePath)) gamePath = Path.GetFullPath(gamePath);

            // TypeLibrary.Initialize() loads the prebuilt EBX SDK via File.Exists("Profiles/<SDK>.dll")
            // and the asset/resource managers read "Caches/" -- both RELATIVE TO THE WORKING DIRECTORY.
            // If the exe is launched from any other directory, the prebuilt EbxClasses assembly isn't
            // found and TypeLibrary silently falls back to generating stripped types WITHOUT
            // SetInstanceGuid, so every GetEbx throws a RuntimeBinderException. Pin the CWD to the exe
            // directory for the duration of init, then restore it so user-supplied paths still resolve.
            string originalCwd = Environment.CurrentDirectory;
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
            string profileName = ResolveProfile(game);
            logger.Log("Loading profile: {0} ({1})", profileName, game);

            // Must be registered before ProfilesLibrary.Initialize triggers type loading
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEbxClasses;

            // Load all built-in profiles from Profiles.bin (embedded in FrostySdk.dll).
            // Plugin profiles (StarWarsSDK.dll etc.) are already covered by Profiles.bin for
            // SWBF1/SWBF2; pass an empty list since we're not using the PluginManager.
            ProfilesLibrary.Initialize(new Profile[0]);

            if (!ProfilesLibrary.Initialize(profileName))
                throw new Exception("Profile '" + profileName + "' not found in FrostySdk. Check that FrostySdk.dll is from FE-1063.");

            TypeLibrary.Initialize();
            WarmReflectionOrder(logger);

            // Oodle is optional -- SWBF1 may not need it; SWBF2 does
            string oodlePath = Path.Combine(gamePath, "oo2core_6_win64.dll");
            if (File.Exists(oodlePath))
            {
                BindOodle(oodlePath);
                logger.Log("Oodle bound from game directory.");
            }
            else
            {
                logger.LogWarning("oo2core_6_win64.dll not found in game dir -- skipping Oodle bind.");
            }

            var fs = new FileSystem(gamePath);
            foreach (FileSystemSource source in ProfilesLibrary.Sources)
                fs.AddSource(source.Path, source.SubDirs);

            byte[] key = null;
            if (ProfilesLibrary.RequiresKey)
            {
                string keyPath = Path.Combine(gamePath, ProfilesLibrary.CacheName + ".key");
                if (!File.Exists(keyPath))
                    throw new Exception("Encryption key required but not found: " + keyPath + "\nRun Frosty Editor once with this game to generate it.");
                key = File.ReadAllBytes(keyPath);
                if (key.Length >= 0x10)
                {
                    byte[] k1 = new byte[0x10];
                    Array.Copy(key, k1, 0x10);
                    KeyManager.Instance.AddKey("Key1", k1);
                }
                if (key.Length >= 0x20)
                {
                    byte[] k2 = new byte[0x10];
                    Array.Copy(key, 0x10, k2, 0, 0x10);
                    KeyManager.Instance.AddKey("Key2", k2);
                }
                if (key.Length > 0x20)
                {
                    byte[] k3 = new byte[0x4000];
                    Array.Copy(key, 0x20, k3, 0, Math.Min(key.Length - 0x20, 0x4000));
                    KeyManager.Instance.AddKey("Key3", k3);
                }
            }
            fs.Initialize(KeyManager.Instance.GetKey("Key1"));

            var rm = new ResourceManager(fs);
            rm.SetLogger(logger);
            logger.Log("Initializing resource manager...");
            rm.Initialize();

            // Beta bug #1: a run killed mid cache-write leaves a truncated cache; the next load reads a
            // bogus count and OOMs (~20 min) instead of rebuilding. Guard cheaply with a size sidecar we
            // write after every successful load: if the cache size no longer matches, delete it BEFORE
            // the expensive read so we rebuild fast. (A first-ever cold load that's killed has no sidecar
            // yet -- the catch-and-rebuild below still recovers, just more slowly that one time.)
            string cachePath   = fs.CacheName + ".cache";
            string sidecarPath = cachePath + ".size";
            if (System.IO.File.Exists(cachePath) && System.IO.File.Exists(sidecarPath))
            {
                try
                {
                    if (long.TryParse(System.IO.File.ReadAllText(sidecarPath).Trim(), out long expected)
                        && new System.IO.FileInfo(cachePath).Length != expected)
                    {
                        logger.LogWarning("Asset cache size changed unexpectedly (truncated?); deleting and rebuilding.");
                        DeleteCacheFiles(fs, logger);
                        try { System.IO.File.Delete(sidecarPath); } catch { }
                    }
                }
                catch { /* sidecar unreadable -> ignore, fall through to the load + catch guard */ }
            }

            var am = new AssetManager(fs, rm);
            am.SetLogger(logger);
            logger.Log("Initializing asset manager (this may take 30-60s)...");
            try
            {
                am.Initialize(true);
            }
            catch (Exception ex)
            {
                // Fallback for a corrupt cache with no usable sidecar: delete and rebuild once.
                logger.LogWarning("Asset load failed ({0}); deleting the asset cache and rebuilding from game data.", ex.Message);
                DeleteCacheFiles(fs, logger);
                try { if (System.IO.File.Exists(sidecarPath)) System.IO.File.Delete(sidecarPath); } catch { }
                am = new AssetManager(fs, rm);
                am.SetLogger(logger);
                am.Initialize(true);
            }

            // Record the (now-complete) cache size so the next run can detect truncation cheaply.
            try { if (System.IO.File.Exists(cachePath)) System.IO.File.WriteAllText(sidecarPath, new System.IO.FileInfo(cachePath).Length.ToString()); }
            catch { /* best-effort */ }

            App.AssetManager    = am;
            App.ResourceManager = rm;
            App.FileSystem      = fs;
            logger.Log("SDK initialized. {0} EBX assets loaded.", CountEbx(am));
            }
            finally
            {
                Environment.CurrentDirectory = originalCwd;
            }
        }

        // Frosty's EbxWriter records each field at its SDK offset but writes the values in
        // Type.GetProperties() order, and .NET moves a property that has been looked up by name to the
        // front of later GetProperties() results. EbxReader looks every field up by name, and so do the
        // property writers, so a save could put values into the wrong slots -- the game+0x549B202 load
        // crash (EbxPartitionReader::marshalReference reading a light's float bytes as a pointer index).
        // Enumerating every SDK type's members once, before anything touches them by name, fills the
        // reflection cache in declaration (offset) order and keeps it there.
        private static void WarmReflectionOrder(ILogger logger)
        {
            const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var assemblies = new System.Collections.Generic.List<Assembly> { typeof(TypeLibrary).Assembly };
            Type dataContainer = TypeLibrary.GetType("DataContainer");
            if (dataContainer != null && !assemblies.Contains(dataContainer.Assembly)) assemblies.Add(dataContainer.Assembly);
            int types = 0;
            foreach (Assembly assembly in assemblies)
            {
                Type[] all;
                try { all = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { all = Array.FindAll(ex.Types, t => t != null); }
                foreach (Type t in all)
                {
                    try { t.GetProperties(declared); t.GetProperties(); t.GetFields(declared); types++; }
                    catch { }
                }
            }
            logger.Log("Reflection order pinned for {0} types in {1} assemblies.", types, assemblies.Count);
        }

        public static string ResolveProfile(string game)
        {
            switch (game.ToLowerInvariant())
            {
                case "swbf1": return "starwarsbattlefront";
                case "swbf2": return "starwarsbattlefrontii";
                default: throw new ArgumentException("Unknown game alias '" + game + "'. Use swbf1 or swbf2.");
            }
        }

        private static Assembly ResolveEbxClasses(object sender, ResolveEventArgs args)
        {
            string name = args.Name.Contains(",") ? args.Name.Substring(0, args.Name.IndexOf(',')) : args.Name;
            if (!name.Equals("EbxClasses", StringComparison.OrdinalIgnoreCase)) return null;
            string profilesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles");
            string sdkDll = Path.Combine(profilesDir, ProfilesLibrary.SDKFilename + ".dll");
            return File.Exists(sdkDll) ? Assembly.LoadFrom(sdkDll) : null;
        }

        private static void BindOodle(string dllPath)
        {
            // Load the DLL into the process so FrostySdk's own P/Invoke can find it
            LoadLibraryEx(dllPath, IntPtr.Zero, 0);
        }

        private static int CountEbx(AssetManager am)
        {
            int n = 0;
            foreach (var _ in am.EnumerateEbx()) n++;
            return n;
        }

        // Deletes the asset cache so the next Initialize rebuilds it from game data. Used to recover
        // from a cache truncated by a killed run (beta bug #1).
        private static void DeleteCacheFiles(FileSystem fs, ILogger logger)
        {
            foreach (string path in new[] { fs.CacheName + ".cache", fs.CacheName + "_sbdata.cas" })
            {
                try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                catch (Exception ex) { logger.LogWarning("Could not delete stale cache '{0}': {1}", path, ex.Message); }
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryEx", SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);
    }
}
