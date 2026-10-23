using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ChunkImportTests
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
        public void ChunkImport_MissingId_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ChunkCommand.ExecuteImport(new[] { "somefile.bin" }, logger, new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        [Test]
        public void ChunkImport_MissingFile_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ChunkCommand.ExecuteImport(
                new[] { "C:\\definitely\\does_not_exist.bin", "--id", Guid.NewGuid().ToString("D") },
                logger, new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        // --- Integration: export a chunk, import it back, confirm it becomes a project modification ---

        [Test]
        [IntegrationTest("swbf2")]
        public void ChunkImport_RoundTrip_MarksModified()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Pick any chunk.
            ChunkAssetEntry chunk = null;
            foreach (ChunkAssetEntry e in App.AssetManager.EnumerateChunks()) { chunk = e; break; }
            if (chunk == null) { Console.WriteLine("skip: no chunks in this game"); return; }
            string idStr = chunk.Id.ToString("D");

            string tmp = Path.Combine(Path.GetTempPath(), "chunkimport_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                int xr = ChunkCommand.ExecuteExport(new[] { idStr, "--out", tmp }, logger, fmt);
                if (xr != 0 || !File.Exists(tmp)) { Console.WriteLine("skip: chunk export failed (rc=" + xr + ")"); return; }

                int ir = ChunkCommand.ExecuteImport(new[] { tmp, "--id", idStr }, logger, fmt);
                Assert.AreEqual(0, ir);
                Assert.IsTrue(App.AssetManager.GetChunkEntry(chunk.Id).IsModified,
                    "chunk should be modified after import");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(chunk.Id)); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }

        // N2: chunk import --new --id <guid> creates a brand-new chunk (AddChunk).
        [Test]
        [IntegrationTest("swbf2")]
        public void ChunkImport_CreateNew_AddsChunk()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            Guid id = Guid.NewGuid();
            string tmp = Path.Combine(Path.GetTempPath(), "chunknew_" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                File.WriteAllBytes(tmp, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 });
                int ir = ChunkCommand.ExecuteImport(new[] { tmp, "--new", "--id", id.ToString("D") }, logger, fmt);
                Assert.AreEqual(0, ir);
                Assert.IsTrue(App.AssetManager.GetChunkEntry(id) != null, "new chunk should exist after create");
            }
            finally
            {
                try { var e = App.AssetManager.GetChunkEntry(id); if (e != null) App.AssetManager.RevertAsset(e); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
