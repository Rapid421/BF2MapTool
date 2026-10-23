using System;
using System.IO;
using System.Reflection;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class TextureExportTests
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

        // UITextureAsset wraps a Texture via the same `.Resource` RID as TextureAsset, so it must map to
        // the same exporter. Before the fix, `export` failed with "No handler registered for type
        // 'UITextureAsset'". This is game-independent (handler registration), so it runs under any session.
        [Test]
        [IntegrationTest("swbf2")]
        public void Export_UITextureAsset_MapsToTextureExporter()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            var h = Session.Plugins.GetHandler("UITextureAsset");
            Assert.IsNotNull(h);
            Assert.IsTrue(h.GetType().Name == "HeadlessTextureAssetDefinition",
                "UITextureAsset should map to HeadlessTextureAssetDefinition, got " + h.GetType().Name);
        }

        [Test]
        [IntegrationTest("swbf1")]
        public void TextureExport_CryoPrefix_DdsFilesOnDisk()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);

            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF1");
            if (!Session.IsInitialized)
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string adaptersDir = Path.Combine(exeDir, "Adapters");
                if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);

                // Copy texture adapter to adapters dir if not already there
                string adapterSrc = Path.GetFullPath(Path.Combine(
                    exeDir, "..", "..", "..", "..",
                    "FrostyCLI.Adapters.Texture", "bin", "x64", "Release",
                    "FrostyCLI.Adapters.Texture.dll"));
                string adapterDst = Path.Combine(adaptersDir, "FrostyCLI.Adapters.Texture.dll");
                if (File.Exists(adapterSrc) && !File.Exists(adapterDst))
                    File.Copy(adapterSrc, adapterDst);

                Session.Initialize("swbf1", gp, null, adaptersDir, logger);
            }

            string outDir = Path.Combine(Path.GetTempPath(), "texexport_test_" + Guid.NewGuid());
            Directory.CreateDirectory(outDir);
            try
            {
                var cmd = new ExportBatchCommand();
                int r = cmd.Execute(new[]
                {
                    "--type",   "TextureAsset",
                    "--filter", "xp2/objects/architecture/_wallscryochamber_01",
                    "--out",    outDir,
                    "--format", "dds"
                }, logger, fmt);
                Assert.IsTrue(r == 0, "export-batch returned non-zero: " + r);
                string[] dds = Directory.GetFiles(outDir, "*.dds", SearchOption.AllDirectories);
                Assert.IsTrue(dds.Length >= 1, "Expected at least 1 DDS file, got " + dds.Length);
            }
            finally
            {
                try { Directory.Delete(outDir, true); } catch { }
            }
        }
    }
}
