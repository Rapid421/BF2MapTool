using System;
using System.IO;
using FrostySdk.Interfaces;

namespace FrostyCLI.Core
{
    public static class Session
    {
        public static PluginRegistry  Plugins       { get; private set; }
        public static bool            IsInitialized { get; private set; }
        public static HeadlessProject Project       { get; private set; } = new HeadlessProject();
        public static string          GameName      { get; private set; }
        /// <summary>The installed Frosty editor's Plugins directory, when one was configured.</summary>
        public static string          EditorPluginsDir { get; private set; }

        // Stashed by the launcher from the global --handler-plugins flag so command paths that
        // self-initialize the Session (notably the `run` pipeline) can still pick up the handler
        // plugins needed to resolve handler-based ModifiedResources during project-load.
        public static string DefaultHandlerPluginsDir { get; set; }

        // Stashed by the launcher from the global --save / --save-as flags. A command that writes
        // the project itself needs to know the launcher is already going to, so it does not write
        // the loaded file on the way past when the user asked for the result somewhere else.
        public static bool   SaveRequested { get; set; }
        public static string SaveAsPath    { get; set; }

        public static void Initialize(
            string game,
            string gamePath,
            string pluginsDir,
            string adaptersDir,
            ILogger logger,
            string handlerPluginsDir = null)
        {
            if (IsInitialized)
                throw new InvalidOperationException("Session.Initialize called more than once.");

            if (string.IsNullOrWhiteSpace(gamePath))
                throw new ArgumentException("gamePath must not be null or empty.", "gamePath");

            GameName = game;
            WpfStub.Install();
            FrostyMessageBoxPatch.Install();
            FrostyInitializer.Initialize(game, gamePath, logger);

            Plugins = new PluginRegistry();
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string defaultPlugins = pluginsDir ?? Path.Combine(exeDir, "Plugins");
            string defaultAdapters = adaptersDir ?? Path.Combine(exeDir, "Adapters");
            Plugins.Discover(defaultPlugins, defaultAdapters, logger);

            // Optionally pull in the broader merge handlers (localization / weapon-table / network-
            // registry, etc.) from the FE editor's Plugins folder. Curated + defensive: only the known
            // handler plugins load, and a failure to load any one is a warning, not fatal.
            EditorPluginsDir = handlerPluginsDir;
            if (!string.IsNullOrEmpty(handlerPluginsDir))
                Plugins.LoadHandlerPlugins(handlerPluginsDir, logger);

            // Battlefront II strings: FsLocalizationPlugin's database (string edits for `loc set` and level menu
            // entries; its UITextDatabase handler then carries them through project save and mod build)
            if (string.Equals(game, "swbf2", StringComparison.OrdinalIgnoreCase))
                LocalizationHost.Ensure(logger, handlerPluginsDir);

            IsInitialized = true;
        }
    }
}
