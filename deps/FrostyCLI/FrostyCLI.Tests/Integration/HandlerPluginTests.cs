using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using Frosty.Core.Mod;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Tests.Integration
{
    // P4: broader merge handlers. The FE editor's localization / weapon-table / SWBF2-merger plugins
    // carry custom ACTION handlers (LocalizedStringResource, UITextDatabase, DifficultyWeaponTableData,
    // NetworkRegistryAsset, WSTeamData, UIMetaDataAsset, MeshVariationDatabase, ProfileOptionsAsset).
    // These load + instantiate headlessly (no WPF in their ctors). Loading the curated set into the
    // appdomain makes their handlers available to HeadlessProject's [RegisterCustomHandler] scan, so
    // mod build + `mod apply` (P1) merge those types. Gated on FROSTYCLI_HANDLER_PLUGINS pointing at the
    // FE Plugins folder; skips otherwise.
    public class HandlerPluginTests
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void HandlerPlugins_LoadAndRegisterMergeHandlers()
        {
            var logger = new ExportLogger(quiet: true);
            string dir = Environment.GetEnvironmentVariable("FROSTYCLI_HANDLER_PLUGINS");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            { Console.WriteLine("skip: FROSTYCLI_HANDLER_PLUGINS not set to an existing FE Plugins dir"); return; }

            EnsureSession(logger);

            // Load the curated handler-plugin set into the appdomain (what `--handler-plugins <dir>` does).
            int loaded = Session.Plugins.LoadHandlerPlugins(dir, logger);
            if (loaded == 0) { Console.WriteLine("skip: no handler plugins found in " + dir); return; }

            // The headless PluginManager's [RegisterCustomHandler] scan should now resolve the handlers.
            PluginManager pm = HeadlessProject.CreateHeadlessPluginManager();
            Assert.IsNotNull(pm);

            // Res handler (localization) -- registered by ResType, independent of the SDK type library.
            ICustomActionHandler locRes = pm.GetCustomHandler(ResourceType.LocalizedStringResource);
            Assert.IsNotNull(locRes);
            Assert.AreEqual("BiowareLocalizationCustomActionHandler", locRes.GetType().Name);

            // Ebx handlers register by resolving the asset type in the game's type library. The SWBF2-
            // merger's NetworkRegistryAsset + MeshVariationDatabase are real SWBF2 types, so they register.
            ICustomActionHandler netreg = pm.GetCustomHandler("NetworkRegistryAsset");
            Assert.IsNotNull(netreg);
            Assert.AreEqual("NetworkRegistryActionHandler", netreg.GetType().Name);

            ICustomActionHandler mvdb = pm.GetCustomHandler("MeshVariationDatabase");
            Assert.IsNotNull(mvdb);
            Assert.AreEqual("MeshVariationActionHandler", mvdb.GetType().Name);

            // NOTE: DifficultyWeaponTableData is NOT a SWBF2 type (generic Frosty plugin), so its handler
            // loads but does not register here -- expected. It would register for a game whose type
            // library defines that asset type. Confirm the absence is type-driven, not a load failure.
            Assert.IsNull(pm.GetCustomHandler("DifficultyWeaponTableData"));
        }
    }
}
