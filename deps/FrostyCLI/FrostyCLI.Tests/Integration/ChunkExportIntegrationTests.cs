using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ChunkExportIntegrationTests
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
        public void ChunkExport_KnownChunk_ProducesNonEmptyFile()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            // EnumerateChunks may not exist in all SDK builds — use reflection with graceful skip
            MethodInfo enumMethod = App.AssetManager.GetType()
                .GetMethod("EnumerateChunks", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (enumMethod == null)
            {
                Console.WriteLine("  SKIP (EnumerateChunks not available)");
                return;
            }

            ChunkAssetEntry chunk = null;
            try
            {
                var enumResult = enumMethod.Invoke(App.AssetManager, null) as IEnumerable;
                if (enumResult != null)
                    foreach (ChunkAssetEntry e in enumResult) { chunk = e; break; }
            }
            catch
            {
                Console.WriteLine("  SKIP (EnumerateChunks threw — not supported in this SDK build)");
                return;
            }

            Assert.IsTrue(chunk != null, "No chunks found");

            string tmp = Path.Combine(Path.GetTempPath(), "chunk_export_" + Guid.NewGuid() + ".bin");
            try
            {
                int r = new ChunkCommand().Execute(
                    new[] { "export", chunk.Id.ToString("D"), "--out", tmp }, logger, fmt);
                Assert.AreEqual(0, r);
                Assert.IsTrue(File.Exists(tmp));
                Assert.IsTrue(new FileInfo(tmp).Length > 0);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
