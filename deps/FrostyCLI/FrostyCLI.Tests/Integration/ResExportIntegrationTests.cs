using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ResExportIntegrationTests
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Session.Initialize("swbf2", gp, null, Path.Combine(exeDir, "Adapters"), logger);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ResExport_KnownEntry_ProducesNonEmptyFile()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            ResAssetEntry entry = null;
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes()) { entry = e; break; }
            Assert.IsTrue(entry != null, "No Res assets found");

            string tmp = Path.Combine(Path.GetTempPath(), "res_export_" + Guid.NewGuid() + ".bin");
            try
            {
                int r = new ResCommand().Execute(new[] { "export", entry.Name, "--out", tmp }, logger, fmt);
                Assert.AreEqual(0, r);
                Assert.IsTrue(File.Exists(tmp));
                Assert.IsTrue(new FileInfo(tmp).Length > 0);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
