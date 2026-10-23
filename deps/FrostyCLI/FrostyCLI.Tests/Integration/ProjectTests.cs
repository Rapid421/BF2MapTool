using System;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Tests.Integration
{
    public class ProjectTests
    {
        private static readonly string FbprojectPath =
            Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "community-mods", "HvVOnBespinCampaign.fbproject"));

        private void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gamePath = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            Assert.IsTrue(gamePath != null, "Set FROSTYCLI_GAME_PATH_SWBF2 to the SWBF2 install directory.");
            Assert.IsTrue(Directory.Exists(gamePath), "FROSTYCLI_GAME_PATH_SWBF2 does not exist: " + gamePath);
            Session.Initialize("swbf2", gamePath, null, null, logger);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ProjectLoad_HvVCampaign_Succeeds()
        {
            var logger = new ExportLogger(quiet: false);
            EnsureSession(logger);

            if (!File.Exists(FbprojectPath))
            { Console.WriteLine("skip: sample .fbproject fixture not present (" + FbprojectPath + ")"); return; }

            Session.Project.Load(FbprojectPath, logger);
            Assert.IsTrue(Session.Project.IsLoaded, "IsLoaded should be true after Load.");
            Assert.IsTrue(Session.Project.FilePath == FbprojectPath,
                "FilePath should match the loaded path.");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ProjectRoundTrip_SaveAndReload()
        {
            var logger  = new ExportLogger(quiet: false);
            EnsureSession(logger);

            if (!File.Exists(FbprojectPath))
            { Console.WriteLine("skip: sample .fbproject fixture not present (" + FbprojectPath + ")"); return; }

            if (!Session.Project.IsLoaded)
                Session.Project.Load(FbprojectPath, logger);

            string tempPath = Path.Combine(Path.GetTempPath(),
                "FrostyCLI_RoundTrip_" + Guid.NewGuid().ToString() + ".fbproject");
            try
            {
                bool saved = Session.Project.Save(tempPath, logger);
                Assert.IsTrue(saved, "Save() returned false.");
                Assert.IsTrue(File.Exists(tempPath), "Saved file does not exist: " + tempPath);
                Assert.IsTrue(new FileInfo(tempPath).Length > 0, "Saved file is empty.");

                var project2 = new HeadlessProject();
                project2.Load(tempPath, logger);
                Assert.IsTrue(project2.IsLoaded, "Reloaded project IsLoaded should be true.");
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        // Does `project save` (.fbproject) cope with a HANDLER-based modification (ShaderBlockDepot)?
        // mod build needed a scoped PluginManager stub for this; this checks whether the project-save
        // path needs the same. If it throws on null App.PluginManager, the CLI->GUI handoff for
        // material/shader edits has a gap; if it saves + reloads, the hybrid workflow is safe for that case.
        [Test]
        [IntegrationTest("swbf2")]
        public void ProjectSave_WithHandlerModification_RoundTrips()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            ResAssetEntry sbdEntry = null; ShaderBlockDepot sbd = null; int scanned = 0;
            foreach (ResAssetEntry re in App.AssetManager.EnumerateRes((uint)ResourceType.ShaderBlockDepot))
            {
                if (++scanned > 200) break;
                try { sbd = App.AssetManager.GetResAs<ShaderBlockDepot>(re); } catch { continue; }
                if (sbd != null && sbd.GetResource(0) != null) { sbdEntry = re; break; }
            }
            if (sbdEntry == null) { Console.WriteLine("skip: no usable ShaderBlockDepot resource found"); return; }

            string tmp = Path.Combine(Path.GetTempPath(), "FrostyCLI_HandlerSave_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".fbproject");
            try
            {
                sbd.GetResource(0).IsModified = true;
                App.AssetManager.ModifyRes(sbd.ResourceId, sbd);

                bool saved = Session.Project.Save(tmp, logger);
                Assert.IsTrue(saved, "project save with a handler-modified ShaderBlockDepot should succeed");
                Assert.IsTrue(File.Exists(tmp) && new FileInfo(tmp).Length > 0, "saved .fbproject should be non-empty");

                var project2 = new HeadlessProject();
                project2.Load(tmp, logger);
                Assert.IsTrue(project2.IsLoaded, "handler-modified project should reload");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(sbdEntry.Name)); } catch { }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ModBuild_HvVCampaign_ProducesFbmod()
        {
            var logger = new ExportLogger(quiet: false);
            EnsureSession(logger);

            if (!File.Exists(FbprojectPath))
            { Console.WriteLine("skip: sample .fbproject fixture not present (" + FbprojectPath + ")"); return; }

            if (!Session.Project.IsLoaded)
                Session.Project.Load(FbprojectPath, logger);

            string tempMod = Path.Combine(Path.GetTempPath(),
                "FrostyCLI_ModBuild_" + Guid.NewGuid().ToString() + ".fbmod");
            try
            {
                var settings = new ModSettings
                {
                    Name        = "HvVOnBespinCampaign_Test",
                    Author      = "FrostyCLI",
                    Version     = "1.0.0",
                    Description = "Integration test build"
                };
                bool ok = Session.Project.Build(tempMod, settings, logger);
                Assert.IsTrue(ok, "Build() returned false. See log above.");
                Assert.IsTrue(File.Exists(tempMod), "Built .fbmod file does not exist.");
                Assert.IsTrue(new FileInfo(tempMod).Length > 0, "Built .fbmod file is empty.");
                byte[] header = new byte[4];
                using (var fs = File.OpenRead(tempMod))
                    fs.Read(header, 0, 4);
                bool hasContent = header[0] != 0 || header[1] != 0 || header[2] != 0 || header[3] != 0;
                Assert.IsTrue(hasContent, "Built .fbmod starts with 4 zero bytes — likely empty or corrupt.");
            }
            finally
            {
                try { if (File.Exists(tempMod)) File.Delete(tempMod); } catch { }
            }
        }
    }
}
