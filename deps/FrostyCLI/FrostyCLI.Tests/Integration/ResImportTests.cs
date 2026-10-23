using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ResImportTests
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

        // --- Unit guards (no game needed; these checks run before any session use) ---

        [Test]
        public void ResImport_MissingAs_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ResCommand.ExecuteImport(new[] { "somefile.bin" }, logger, new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        [Test]
        public void ResImport_MissingFile_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ResCommand.ExecuteImport(
                new[] { "C:\\definitely\\does_not_exist.bin", "--as", "s2/whatever" }, logger, new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        // --- Integration: export a Res, import it back, confirm it becomes a project modification ---

        [Test]
        [IntegrationTest("swbf2")]
        public void ResImport_RoundTrip_MarksModified()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Pick any Res asset.
            ResAssetEntry res = null;
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes()) { res = e; break; }
            if (res == null) { Console.WriteLine("skip: no Res assets in this game"); return; }

            string tmp = Path.Combine(Path.GetTempPath(), "resimport_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                int xr = ResCommand.ExecuteExport(new[] { res.Name, "--out", tmp }, logger, fmt);
                if (xr != 0 || !File.Exists(tmp)) { Console.WriteLine("skip: res export failed (rc=" + xr + ")"); return; }

                int ir = ResCommand.ExecuteImport(new[] { tmp, "--as", res.Name }, logger, fmt);
                Assert.AreEqual(0, ir);
                Assert.IsTrue(App.AssetManager.GetResEntry(res.Name).IsModified,
                    "res should be modified after import");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(res.Name)); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }

        // N2: res import --as <new> --type <ResType> creates a brand-new Res (AddRes).
        [Test]
        [IntegrationTest("swbf2")]
        public void ResImport_CreateNew_AddsRes()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string newPath = "s2/frostycli_test/newres_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tmp     = Path.Combine(Path.GetTempPath(), "resnew_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                File.WriteAllBytes(tmp, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                int ir = ResCommand.ExecuteImport(new[] { tmp, "--as", newPath, "--type", "Texture" }, logger, fmt);
                Assert.AreEqual(0, ir);
                var created = App.AssetManager.GetResEntry(newPath);
                Assert.IsTrue(created != null, "new res should exist after create");
                Assert.IsTrue(created.IsAdded, "new res should be marked added");
            }
            finally
            {
                try { var e = App.AssetManager.GetResEntry(newPath); if (e != null) App.AssetManager.RevertAsset(e); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
