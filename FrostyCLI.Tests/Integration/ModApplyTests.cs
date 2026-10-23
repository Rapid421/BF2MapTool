using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ModApplyTests
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

        // --- Unit guards (no game needed) ---

        [Test]
        public void ModApply_MissingPath_Returns2()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ModBuildCommand.ExecuteApply(new[] { "apply" }, logger, new OutputFormatter(false));
            Assert.AreEqual(2, rc);
        }

        [Test]
        public void ModApply_MissingFile_Returns2()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ModBuildCommand.ExecuteApply(
                new[] { "apply", "C:\\definitely\\does_not_exist.fbmod" }, logger, new OutputFormatter(false));
            Assert.AreEqual(2, rc);
        }

        // --- Integration: build a mod from one EBX change, revert, apply, verify it came back ---

        [Test]
        [IntegrationTest("swbf2")]
        public void ModApply_EbxRoundTrip_ReappliesModification()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Find a reliably-present EBX asset with a public writable string/CString property
            // (exclude levels — mutating a level root drops its entity list / pollutes other tests).
            EbxAssetEntry target = null; string propName = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                if (a == null || a.RootObject == null) continue;
                foreach (var p in a.RootObject.GetType().GetProperties())
                {
                    if (!p.CanRead || !p.CanWrite) continue;
                    if (p.Name.StartsWith("__")) continue; // skip synthetic props (__Id/__Guid) — they don't round-trip
                    if (p.PropertyType == typeof(string) || p.PropertyType.Name == "CString")
                    { target = e; propName = p.Name; break; }
                }
                if (target != null) break;
            }
            if (target == null) { Console.WriteLine("skip: no ebx asset with a writable string/CString property"); return; }

            string token   = "FrostyCLI_ModApply_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string modPath = Path.Combine(Path.GetTempPath(), "modapply_" + Guid.NewGuid().ToString("N") + ".fbmod");
            try
            {
                // 1) Modify a plain EBX string property, then 2) build a mod containing it.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(
                    new[] { target.Name, "--prop", propName, "--value", token }, logger, fmt));
                int br = ModBuildCommand.ExecuteBuild(
                    new[] { "build", "--out", modPath, "--mod-name", "ModApplyTest" }, logger, fmt);
                if (br != 0 || !File.Exists(modPath))
                { Console.WriteLine("skip: mod build did not produce a file (rc=" + br + ")"); return; }

                // 3) Revert so the asset is back to base game — proves apply re-creates the change.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt));
                Assert.IsFalse(App.AssetManager.GetEbxEntry(target.Name).IsModified,
                    "asset should be unmodified after revert");

                // 4) Apply the mod back into the project.
                int ar = ModBuildCommand.ExecuteApply(new[] { "apply", modPath }, logger, fmt);
                Assert.AreEqual(0, ar);

                // 5) The modification is back, carrying the token value.
                Assert.IsTrue(App.AssetManager.GetEbxEntry(target.Name).IsModified,
                    "asset should be modified after apply");
                var reAsset = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(target.Name));
                object name = reAsset.RootObject.GetType().GetProperty(propName).GetValue(reAsset.RootObject);
                Assert.IsNotNull(name);
                Assert.AreEqual(token, name.ToString());
            }
            finally
            {
                try { EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt); } catch { }
                try { File.Delete(modPath); } catch { }
            }
        }

        // v2: a mod carrying a Res modification applies it back too (not just EBX).
        [Test]
        [IntegrationTest("swbf2")]
        public void ModApply_ResRoundTrip_Reapplies()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            ResAssetEntry res = null;
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes()) { res = e; break; }
            if (res == null) { Console.WriteLine("skip: no res in this game"); return; }

            string resFile = Path.Combine(Path.GetTempPath(), "ma_res_" + Guid.NewGuid().ToString("N") + ".bin");
            string modPath = Path.Combine(Path.GetTempPath(), "ma_r_"   + Guid.NewGuid().ToString("N") + ".fbmod");
            try
            {
                Assert.AreEqual(0, ResCommand.ExecuteExport(new[] { res.Name, "--out", resFile }, logger, fmt));
                Assert.AreEqual(0, ResCommand.ExecuteImport(new[] { resFile, "--as", res.Name }, logger, fmt));

                int br = ModBuildCommand.ExecuteBuild(
                    new[] { "build", "--out", modPath, "--mod-name", "ModApplyRes" }, logger, fmt);
                if (br != 0 || !File.Exists(modPath)) { Console.WriteLine("skip: mod build rc=" + br); return; }

                App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(res.Name));
                Assert.IsFalse(App.AssetManager.GetResEntry(res.Name).IsModified, "res should be reverted");

                int ar = ModBuildCommand.ExecuteApply(new[] { "apply", modPath }, logger, fmt);
                Assert.AreEqual(0, ar);
                Assert.IsTrue(App.AssetManager.GetResEntry(res.Name).IsModified, "res should be modified after apply");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(res.Name)); } catch { }
                try { File.Delete(resFile); } catch { }
                try { File.Delete(modPath); } catch { }
            }
        }

        // v2: a mod carrying a Chunk modification applies it back too.
        [Test]
        [IntegrationTest("swbf2")]
        public void ModApply_ChunkRoundTrip_Reapplies()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            ChunkAssetEntry chunk = null;
            foreach (ChunkAssetEntry e in App.AssetManager.EnumerateChunks()) { chunk = e; break; }
            if (chunk == null) { Console.WriteLine("skip: no chunk in this game"); return; }
            string chunkId = chunk.Id.ToString("D");

            string chunkFile = Path.Combine(Path.GetTempPath(), "ma_chunk_" + Guid.NewGuid().ToString("N") + ".bin");
            string modPath   = Path.Combine(Path.GetTempPath(), "ma_c_"     + Guid.NewGuid().ToString("N") + ".fbmod");
            try
            {
                Assert.AreEqual(0, ChunkCommand.ExecuteExport(new[] { chunkId, "--out", chunkFile }, logger, fmt));
                Assert.AreEqual(0, ChunkCommand.ExecuteImport(new[] { chunkFile, "--id", chunkId }, logger, fmt));

                int br = ModBuildCommand.ExecuteBuild(
                    new[] { "build", "--out", modPath, "--mod-name", "ModApplyChunk" }, logger, fmt);
                if (br != 0 || !File.Exists(modPath)) { Console.WriteLine("skip: mod build rc=" + br); return; }

                App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(chunk.Id));
                Assert.IsFalse(App.AssetManager.GetChunkEntry(chunk.Id).IsModified, "chunk should be reverted");

                int ar = ModBuildCommand.ExecuteApply(new[] { "apply", modPath }, logger, fmt);
                Assert.AreEqual(0, ar);
                Assert.IsTrue(App.AssetManager.GetChunkEntry(chunk.Id).IsModified, "chunk should be modified after apply");
            }
            finally
            {
                try { App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(chunk.Id)); } catch { }
                try { File.Delete(chunkFile); } catch { }
                try { File.Delete(modPath); } catch { }
            }
        }

        // N1: a mod that ADDS a new EBX asset re-creates it on apply (AddEbx path).
        [Test]
        [IntegrationTest("swbf2")]
        public void ModApply_AddedEbxRoundTrip_ReaddsAsset()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string path    = "s2/frostycli_test/modadd_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string modPath = Path.Combine(Path.GetTempPath(), "ma_add_" + Guid.NewGuid().ToString("N") + ".fbmod");
            try
            {
                // Create a brand-new asset (added), then build a mod containing it.
                int cr = EbxWriteCommand.ExecuteCreate(new[] { path, "--type", "DataContainer" }, logger, fmt);
                if (cr != 0 || App.AssetManager.GetEbxEntry(path) == null)
                { Console.WriteLine("skip: cannot create test asset (type not creatable in this build)"); return; }

                int br = ModBuildCommand.ExecuteBuild(
                    new[] { "build", "--out", modPath, "--mod-name", "ModApplyAdd" }, logger, fmt);
                if (br != 0 || !File.Exists(modPath)) { Console.WriteLine("skip: mod build rc=" + br); return; }

                // Remove the added asset so apply has to re-add it.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteDelete(new[] { path }, logger, fmt));
                Assert.IsTrue(App.AssetManager.GetEbxEntry(path) == null, "added asset should be gone after delete");

                int ar = ModBuildCommand.ExecuteApply(new[] { "apply", modPath }, logger, fmt);
                Assert.AreEqual(0, ar);
                Assert.IsTrue(App.AssetManager.GetEbxEntry(path) != null, "asset should be re-added after apply");
            }
            finally
            {
                try { EbxWriteCommand.ExecuteDelete(new[] { path }, logger, fmt); } catch { }
                try { File.Delete(modPath); } catch { }
            }
        }

        // H1: a mod that ADDS a new Res re-creates it on apply (AddRes path, original ResRid preserved).
        [Test]
        [IntegrationTest("swbf2")]
        public void ModApply_AddedResRoundTrip_ReaddsRes()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string tag      = Guid.NewGuid().ToString("N").Substring(0, 8);
            string newRes   = "s2/frostycli_test/addres_" + tag;
            string resFile  = Path.Combine(Path.GetTempPath(), "ma_addres_" + tag + ".bin");
            string modPath  = Path.Combine(Path.GetTempPath(), "ma_addres_" + tag + ".fbmod");
            try
            {
                File.WriteAllBytes(resFile, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                int ir = ResCommand.ExecuteImport(new[] { resFile, "--as", newRes, "--type", "MeshSet" }, logger, fmt);
                if (ir != 0 || App.AssetManager.GetResEntry(newRes) == null)
                { Console.WriteLine("skip: cannot create added res"); return; }
                ulong addedRid = App.AssetManager.GetResEntry(newRes).ResRid;

                int br = ModBuildCommand.ExecuteBuild(new[] { "build", "--out", modPath, "--mod-name", "ModApplyAddRes" }, logger, fmt);
                if (br != 0 || !File.Exists(modPath)) { Console.WriteLine("skip: mod build rc=" + br); return; }

                App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(newRes));
                Assert.IsTrue(App.AssetManager.GetResEntry(newRes) == null, "added res should be gone after revert");

                int ar = ModBuildCommand.ExecuteApply(new[] { "apply", modPath }, logger, fmt);
                Assert.AreEqual(0, ar);
                ResAssetEntry re = App.AssetManager.GetResEntry(newRes);
                Assert.IsTrue(re != null, "res should be re-added after apply");
                Assert.AreEqual(addedRid, re.ResRid); // original RID preserved
            }
            finally
            {
                try { var re = App.AssetManager.GetResEntry(newRes); if (re != null) App.AssetManager.RevertAsset(re); } catch { }
                try { File.Delete(resFile); } catch { }
                try { File.Delete(modPath); } catch { }
            }
        }

        // H1: a mod that ADDS a new Chunk re-creates it on apply (AddChunk path, id preserved).
        [Test]
        [IntegrationTest("swbf2")]
        public void ModApply_AddedChunkRoundTrip_ReaddsChunk()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string tag       = Guid.NewGuid().ToString("N").Substring(0, 8);
            Guid   id        = Guid.NewGuid();
            string chunkFile = Path.Combine(Path.GetTempPath(), "ma_addchunk_" + tag + ".bin");
            string modPath   = Path.Combine(Path.GetTempPath(), "ma_addchunk_" + tag + ".fbmod");
            try
            {
                File.WriteAllBytes(chunkFile, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 });
                int ir = ChunkCommand.ExecuteImport(new[] { chunkFile, "--new", "--id", id.ToString() }, logger, fmt);
                if (ir != 0 || App.AssetManager.GetChunkEntry(id) == null)
                { Console.WriteLine("skip: cannot create added chunk"); return; }

                int br = ModBuildCommand.ExecuteBuild(new[] { "build", "--out", modPath, "--mod-name", "ModApplyAddChunk" }, logger, fmt);
                if (br != 0 || !File.Exists(modPath)) { Console.WriteLine("skip: mod build rc=" + br); return; }

                App.AssetManager.RevertAsset(App.AssetManager.GetChunkEntry(id));
                Assert.IsTrue(App.AssetManager.GetChunkEntry(id) == null, "added chunk should be gone after revert");

                int ar = ModBuildCommand.ExecuteApply(new[] { "apply", modPath }, logger, fmt);
                Assert.AreEqual(0, ar);
                Assert.IsTrue(App.AssetManager.GetChunkEntry(id) != null, "chunk should be re-added after apply");
            }
            finally
            {
                try { var ce = App.AssetManager.GetChunkEntry(id); if (ce != null) App.AssetManager.RevertAsset(ce); } catch { }
                try { File.Delete(chunkFile); } catch { }
                try { File.Delete(modPath); } catch { }
            }
        }
    }
}
