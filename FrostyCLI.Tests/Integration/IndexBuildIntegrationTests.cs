using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class IndexBuildIntegrationTests
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
        public void IndexBuild_CatalogOnly_CreatesFile()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            IndexService.Reset();

            int r = new IndexCommand().Execute(new[] { "build" }, logger, fmt);
            Assert.AreEqual(0, r);

            string catalogPath = IndexService.GetCatalogPath();
            Assert.IsTrue(File.Exists(catalogPath), "Catalog not found at " + catalogPath);
            Assert.IsTrue(new FileInfo(catalogPath).Length > 0);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void IndexBuild_RunTwice_IsIdempotent()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            IndexService.Reset();

            int r1 = new IndexCommand().Execute(new[] { "build" }, logger, fmt);
            int r2 = new IndexCommand().Execute(new[] { "build" }, logger, fmt);
            Assert.AreEqual(0, r1);
            Assert.AreEqual(0, r2);
        }
    }
}
