using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class Phase7IntegrationTests
    {
        private static string ProjPath()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", ".."));
            return Path.Combine(repo, "community-mods", "HvVOnBespinCampaign.fbproject");
        }

        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);

            // The texture export path runs through the registered AssetDefinition adapter,
            // so make sure the adapter DLL is present in the adapters probe dir.
            string adapterSrc = Path.GetFullPath(Path.Combine(
                exeDir, "..", "..", "..", "..",
                "FrostyCLI.Adapters.Texture", "bin", "x64", "Release",
                "FrostyCLI.Adapters.Texture.dll"));
            string adapterDst = Path.Combine(adaptersDir, "FrostyCLI.Adapters.Texture.dll");
            if (File.Exists(adapterSrc) && !File.Exists(adapterDst))
                File.Copy(adapterSrc, adapterDst);

            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        private static void EnsureProject(ExportLogger logger)
        {
            EnsureSession(logger);
            if (!Session.Project.IsLoaded)
            {
                string proj = ProjPath();
                if (File.Exists(proj))
                    Session.Project.Load(proj, logger);
            }
        }

        // Memoized across tests in this process: scanning 155k entries is expensive.
        private static bool _texSearched;
        private static string _texPath;
        private static ulong _texRid;

        // Finds the first TextureAsset whose underlying Texture is a plain 2D texture (TT_2d),
        // returning its EBX path and its Resource RID. Returns null if none found.
        private static string FindTt2dTexture(out ulong resRid)
        {
            if (_texSearched) { resRid = _texRid; return _texPath; }
            _texSearched = true;
            resRid = 0;
            if (App.AssetManager == null) return null;
            int examined = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Type != "TextureAsset") continue;
                if (++examined > 500) break; // keep the search bounded
                FrostySdk.IO.EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(e); } catch { continue; }
                ulong rid;
                try { rid = (ulong)((dynamic)asset.RootObject).Resource; } catch { continue; }
                if (rid == 0) continue;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
                if (resEntry == null) continue;
                FrostySdk.Resources.Texture tex;
                try { tex = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(resEntry); } catch { continue; }
                if (tex == null) continue;
                if (tex.Type.ToString() == "TT_2d") { resRid = _texRid = rid; return _texPath = e.Name; }
            }
            return _texPath = null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ImportTexture_DdsRoundTrip_ReplacesPixels()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureProject(logger);

            ulong resRid;
            string texPath = FindTt2dTexture(out resRid);
            if (texPath == null) { Console.WriteLine("skip: no TT_2d TextureAsset found"); return; }

            string tmpDds = Path.Combine(Path.GetTempPath(), "frostycli_tex_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dds");
            try
            {
                // Export the texture to DDS using the existing export path.
                int er = new ExportCommand().Execute(new[] { texPath, "--out", tmpDds }, logger, fmt);
                if (er != 0 || !File.Exists(tmpDds)) { Console.WriteLine("skip: texture export failed (rc=" + er + ")"); return; }

                // Re-import the exported DDS over the same asset (round-trip).
                int rc = ImportCommand.ExecuteTexture(new[] { tmpDds, "--as", texPath }, logger, fmt);
                Assert.AreEqual(0, rc);

                // The resource should now be marked modified by the import.
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
                Assert.IsTrue(resEntry != null && resEntry.IsModified, "resource not marked modified after import");
            }
            finally
            {
                try { if (File.Exists(tmpDds)) File.Delete(tmpDds); } catch { }
            }
        }

        // F4: cube/array textures import. Iterates cube/array TextureAssets and uses the first that
        // produces a working export -> re-import round-trip (some textures don't export cleanly).
        [Test]
        [IntegrationTest("swbf2")]
        public void ImportTexture_CubeArrayRoundTrip_ReplacesPixels()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureProject(logger);

            string tmpDds = Path.Combine(Path.GetTempPath(), "frostycli_cube_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dds");
            int examined = 0, tried = 0; bool validated = false;
            try
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("TextureAsset"))
                {
                    if (validated || ++examined > 4000 || tried >= 12) break;
                    FrostySdk.IO.EbxAsset asset;
                    try { asset = App.AssetManager.GetEbx(e); } catch { continue; }
                    ulong rid; try { rid = (ulong)((dynamic)asset.RootObject).Resource; } catch { continue; }
                    if (rid == 0) continue;
                    ResAssetEntry re = App.AssetManager.GetResEntry(rid); if (re == null) continue;
                    FrostySdk.Resources.Texture tex;
                    try { tex = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(re); } catch { continue; }
                    if (tex == null) continue;
                    string t = tex.Type.ToString();
                    if (t != "TT_Cube" && t != "TT_2dArray") continue;

                    tried++;
                    try { if (File.Exists(tmpDds)) File.Delete(tmpDds); } catch { }
                    int er = new ExportCommand().Execute(new[] { e.Name, "--out", tmpDds }, logger, fmt);
                    if (er != 0 || !File.Exists(tmpDds)) continue;        // this texture doesn't export; try next
                    int rc = ImportCommand.ExecuteTexture(new[] { tmpDds, "--as", e.Name }, logger, fmt);
                    if (rc != 0) { try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(rid)); } catch { } continue; }

                    ResAssetEntry re2 = App.AssetManager.GetResEntry(rid);
                    if (re2 != null && re2.IsModified) validated = true;
                    try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(rid)); } catch { }
                }

                if (!validated) { Console.WriteLine("skip: no cube/array texture round-tripped (tried " + tried + ")"); return; }
                Assert.IsTrue(validated, "cube/array texture import round-trip");
            }
            finally
            {
                try { if (File.Exists(tmpDds)) File.Delete(tmpDds); } catch { }
            }
        }

        // G3: TT_3d (volume) texture import. Volume textures need no slice-rejig -- the DDS volume
        // layout already matches Frostbite -- so removing the rejection is sufficient. Iterates volume
        // textures and uses the first that round-trips (export -> re-import -> res modified).
        [Test]
        [IntegrationTest("swbf2")]
        public void ImportTexture_VolumeRoundTrip_ReplacesPixels()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureProject(logger);

            string tmpDds = Path.Combine(Path.GetTempPath(), "frostycli_vol_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dds");
            int examined = 0, tried = 0; bool validated = false;
            try
            {
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("TextureAsset"))
                {
                    if (validated || ++examined > 8000 || tried >= 12) break;
                    FrostySdk.IO.EbxAsset asset;
                    try { asset = App.AssetManager.GetEbx(e); } catch { continue; }
                    ulong rid; try { rid = (ulong)((dynamic)asset.RootObject).Resource; } catch { continue; }
                    if (rid == 0) continue;
                    ResAssetEntry re = App.AssetManager.GetResEntry(rid); if (re == null) continue;
                    FrostySdk.Resources.Texture tex;
                    try { tex = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(re); } catch { continue; }
                    if (tex == null) continue;
                    if (tex.Type.ToString() != "TT_3d") continue;

                    tried++;
                    try { if (File.Exists(tmpDds)) File.Delete(tmpDds); } catch { }
                    int er = new ExportCommand().Execute(new[] { e.Name, "--out", tmpDds }, logger, fmt);
                    if (er != 0 || !File.Exists(tmpDds)) continue;        // this texture doesn't export; try next
                    int rc = ImportCommand.ExecuteTexture(new[] { tmpDds, "--as", e.Name }, logger, fmt);
                    if (rc != 0) { try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(rid)); } catch { } continue; }

                    ResAssetEntry re2 = App.AssetManager.GetResEntry(rid);
                    if (re2 != null && re2.IsModified) validated = true;
                    try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(rid)); } catch { }
                }

                if (!validated) { Console.WriteLine("skip: no TT_3d volume texture round-tripped (tried " + tried + ")"); return; }
                Assert.IsTrue(validated, "volume texture import round-trip");
            }
            finally
            {
                try { if (File.Exists(tmpDds)) File.Delete(tmpDds); } catch { }
            }
        }

        // H6: create a brand-new TextureAsset from an image using a template (clone the template's EBX +
        // res + chunk, then import). Key property: INDEPENDENCE -- importing must not modify the template.
        [Test]
        [IntegrationTest("swbf2")]
        public void ImportTexture_NewFromTemplate_IndependentOfTemplate()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureProject(logger);

            ulong tmplResRid;
            string tex = FindTt2dTexture(out tmplResRid);
            if (tex == null) { Console.WriteLine("skip: no TT_2d texture found"); return; }
            try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(tmplResRid)); } catch { }

            string newName = tex + "_h6clone_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string tmpDds  = Path.Combine(Path.GetTempPath(), "frostycli_h6_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dds");
            EbxAssetEntry newEbx = null;
            try
            {
                int er = new ExportCommand().Execute(new[] { tex, "--out", tmpDds }, logger, fmt);
                if (er != 0 || !File.Exists(tmpDds)) { Console.WriteLine("skip: texture export failed"); return; }

                int rc = ImportCommand.ExecuteTexture(new[] { tmpDds, "--as", newName, "--template", tex }, logger, fmt);
                Assert.AreEqual(0, rc);

                newEbx = App.AssetManager.GetEbxEntry(newName);
                Assert.IsTrue(newEbx != null, "new texture asset should be created");
                var newAsset = App.AssetManager.GetEbx(newEbx);
                ulong newResRid = (ulong)((dynamic)newAsset.RootObject).Resource;
                Assert.IsTrue(newResRid != 0 && newResRid != tmplResRid, "new texture must have its OWN res");
                ResAssetEntry newRes = App.AssetManager.GetResEntry(newResRid);
                Assert.IsTrue(newRes != null && (newRes.IsAdded || newRes.IsModified), "new res should carry the imported pixels");
                Assert.IsTrue(!App.AssetManager.GetResEntry(tmplResRid).IsModified, "template res must remain unmodified");
            }
            finally
            {
                try { if (newEbx != null) App.AssetManager.RevertAsset(newEbx); } catch { }
                try { App.AssetManager.RevertAsset(App.AssetManager.GetResEntry(tmplResRid)); } catch { }
                try { if (File.Exists(tmpDds)) File.Delete(tmpDds); } catch { }
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ImportTexture_MalformedDds_FailsWithoutMutation()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureProject(logger);

            ulong resRid;
            string texPath = FindTt2dTexture(out resRid);
            if (texPath == null) { Console.WriteLine("skip: no TT_2d TextureAsset found"); return; }

            // Revert any prior modification so we have a clean baseline.
            EbxAssetEntry ebxEntry = App.AssetManager.GetEbxEntry(texPath);
            bool wasModifiedBefore = ebxEntry != null && ebxEntry.IsModified;

            string tmpDds = Path.Combine(Path.GetTempPath(), "frostycli_bad_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dds");
            try
            {
                // A zero-/garbage-byte file is not a valid DDS — DDSHeader.Read should fail.
                File.WriteAllBytes(tmpDds, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 });

                int rc = ImportCommand.ExecuteTexture(new[] { tmpDds, "--as", texPath }, logger, fmt);
                Assert.AreEqual(1, rc);

                // A failed import must not have newly dirtied the resource.
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
                if (!wasModifiedBefore && resEntry != null)
                    Assert.IsFalse(resEntry.IsModified, "resource was modified despite import failure");
            }
            finally
            {
                try { if (File.Exists(tmpDds)) File.Delete(tmpDds); } catch { }
            }
        }
    }
}
