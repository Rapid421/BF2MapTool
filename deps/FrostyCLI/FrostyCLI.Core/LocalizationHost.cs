using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Interfaces;

namespace FrostyCLI.Core
{
    // BFII's UI strings live in the UITextDatabase assets that Frosty Editor edits through FsLocalizationPlugin
    // (FsLocalizationStringDatabase + a custom action handler for the "UITextDatabase" ebx type). Headlessly nothing
    // registers a string database, so `loc set` and menu entries could not name anything. This loads the installed
    // plugin from the editor's Plugins folder and makes its database LocalizedStringDatabase.Current; with the plugin
    // assembly loaded, HeadlessProject's plugin-manager stub also registers its handler, so edited strings survive
    // project save/load and are written into the .fbmod exactly as the editor writes them.
    public static class LocalizationHost
    {
        public const string DefaultPluginsDir = @"F:\Frosty\Frosty Editor\Plugins";
        public const string PluginDll = "FsLocalizationPlugin.dll";

        public static bool IsReal(ILocalizedStringDatabase db) =>
            db != null && db.GetType().Name != "DefaultLocalizedStringDatabase";

        public static bool Ensure(ILogger logger, string pluginsDir = null)
        {
            if (IsReal(LocalizedStringDatabase.Current)) return true;
            if (Environment.GetEnvironmentVariable("FROSTYCLI_NO_LOC") != null) return false;
            pluginsDir = pluginsDir ?? Environment.GetEnvironmentVariable("FROSTYCLI_PLUGINS") ?? DefaultPluginsDir;
            string path = Path.Combine(pluginsDir, PluginDll);
            if (!File.Exists(path)) { logger?.Log("localization: " + path + " not found - string edits unavailable"); return false; }
            try
            {
                EditorPluginHost.EnsureDependencyResolver(pluginsDir);
                Assembly asm = Assembly.LoadFrom(path);
                Type dbType = null;
                foreach (object attr in asm.GetCustomAttributes(false))
                    if (attr.GetType().Name == "RegisterLocalizedStringDatabaseAttribute")
                        dbType = attr.GetType().GetProperties().Select(p => p.GetValue(attr) as Type).FirstOrDefault(t => t != null);
                if (dbType == null) { logger?.Log("localization: " + PluginDll + " registers no string database"); return false; }
                // Initialize reads Config ("Language", game scope) and logs through App.Logger
                HeadlessProject.EnsureConfig();
                // in memory only (Config.AddGame also saves the editor's config file)
                string profile = FrostySdk.ProfilesLibrary.ProfileName;
                var games = Config.Current.Games;
                if (!games.ContainsKey(profile))
                {
                    Type go = typeof(Config).GetProperty("Games").PropertyType.GetGenericArguments()[1];
                    games.Add(profile, (dynamic)Activator.CreateInstance(go, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { App.FileSystem?.BasePath ?? "" }, null));
                }
                if (App.Logger == null && logger != null) App.Logger = logger;
                var db = (ILocalizedStringDatabase)Activator.CreateInstance(dbType);
                PropertyInfo cur = typeof(LocalizedStringDatabase).GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                cur.SetValue(null, db);
                db.Initialize();
                logger?.Log("localization: " + dbType.FullName + " from " + path);
                return true;
            }
            catch (Exception ex)
            {
                logger?.Log("localization: could not load " + path + ": " + (ex.InnerException ?? ex).Message);
                if (Environment.GetEnvironmentVariable("FROSTYCLI_TRACE") == "1") logger?.Log((ex.InnerException ?? ex).ToString());
                return false;
            }
        }
    }
}
