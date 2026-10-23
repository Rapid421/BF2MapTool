using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // G2: `bundle add` / `bundle ls` -- bundle membership management (the Frosty GUI's Bundle Editor
    // capability), via AssetManager.EnumerateBundles / GetBundleId and AssetEntry.AddToBundle.
    public class BundleTests
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
        public void BundleAdd_AddsAssetToBundle()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Pick an EBX asset and a real bundle it is NOT already in.
            BundleEntry bundle = null;
            foreach (BundleEntry b in App.AssetManager.EnumerateBundles()) { bundle = b; break; }
            if (bundle == null) { Console.WriteLine("skip: no bundles enumerated"); return; }
            int bid = App.AssetManager.GetBundleId(bundle);

            EbxAssetEntry target = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!e.IsInBundle(bid)) { target = e; break; }
            }
            if (target == null) { Console.WriteLine("skip: no asset outside the chosen bundle"); return; }

            try
            {
                int rc = BundleCommand.ExecuteAdd(new[] { target.Name, "--to", bundle.Name }, logger, fmt);
                Assert.AreEqual(0, rc);
                Assert.IsTrue(target.IsInBundle(bid), "asset should be in the bundle after add");
                Assert.IsTrue(target.AddedBundles.Contains(bid), "bundle id should be in AddedBundles");
            }
            finally
            {
                try { target.AddedBundles.Remove(bid); } catch { }
                try { App.AssetManager.RevertAsset(target); } catch { }
            }
        }

        // H4: `bundle remove` undoes a project-added bundle membership.
        [Test]
        [IntegrationTest("swbf2")]
        public void BundleRemove_UndoesAdd()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            BundleEntry bundle = null;
            foreach (BundleEntry b in App.AssetManager.EnumerateBundles()) { bundle = b; break; }
            if (bundle == null) { Console.WriteLine("skip: no bundles"); return; }
            int bid = App.AssetManager.GetBundleId(bundle);

            EbxAssetEntry target = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!e.IsInBundle(bid)) { target = e; break; }
            }
            if (target == null) { Console.WriteLine("skip: no asset outside the chosen bundle"); return; }

            try
            {
                Assert.AreEqual(0, BundleCommand.ExecuteAdd(new[] { target.Name, "--to", bundle.Name }, logger, fmt));
                Assert.IsTrue(target.AddedBundles.Contains(bid), "should be added");

                Assert.AreEqual(0, BundleCommand.ExecuteRemove(new[] { target.Name, "--from", bundle.Name }, logger, fmt));
                Assert.IsFalse(target.AddedBundles.Contains(bid), "should be removed after bundle remove");
            }
            finally
            {
                try { target.AddedBundles.Remove(bid); } catch { }
                try { App.AssetManager.RevertAsset(target); } catch { }
            }
        }

        // `bundle add --with-resources` cannot link a vanilla superbundle-resident chunk: FrostySdk's
        // ChunkAssetEntry.AddToBundle refuses any chunk with `Bundles.Count == 0 && !IsAdded`. BFII
        // stores sound data exactly that way, so bundling a shipped SoundWaveAsset puts its EBX in the
        // bundle and leaves the audio out. That has to be reported, not counted as a silent zero.
        [Test]
        [IntegrationTest("swbf2")]
        public void BundleAdd_WithResources_ReportsRefusedSuperbundleChunks()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // A shipped sound wave whose first chunk is superbundle-resident (in no bundle, not added).
            EbxAssetEntry target = null; ChunkAssetEntry chunkEntry = null;
            int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("SoundWaveAsset"))
            {
                if (++scanned > 300) break;
                try
                {
                    dynamic wave = App.AssetManager.GetEbx(e).RootObject;
                    foreach (dynamic c in wave.Chunks)
                    {
                        Guid id = c.ChunkId;
                        ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(id);
                        if (ce == null || ce.IsAdded || ce.Bundles.Count != 0) continue;
                        target = e; chunkEntry = ce; break;
                    }
                }
                catch { continue; }
                if (target != null) break;
            }
            if (target == null) { Console.WriteLine("skip: no shipped sound wave with a superbundle-resident chunk"); return; }

            int bid = -1; BundleEntry bundle = null;
            foreach (BundleEntry b in App.AssetManager.EnumerateBundles())
            {
                int id = App.AssetManager.GetBundleId(b);
                if (!target.IsInBundle(id) && !chunkEntry.IsInBundle(id)) { bid = id; bundle = b; break; }
            }
            if (bundle == null) { Console.WriteLine("skip: no free bundle for the sound wave"); return; }

            TextWriter stderr = Console.Error;
            var captured = new StringWriter();
            try
            {
                Console.SetError(captured);
                int rc = BundleCommand.ExecuteAdd(new[] { target.Name, "--to", bundle.Name, "--with-resources" }, logger, fmt);
                Console.SetError(stderr);

                Assert.AreEqual(0, rc);
                // The EBX lands in the bundle...
                Assert.IsTrue(target.IsInBundle(bid), "the sound wave EBX should be in the bundle");
                // ...but the SDK refuses its payload, and that must be said out loud.
                Assert.IsFalse(chunkEntry.IsInBundle(bid), "a superbundle-resident chunk cannot be bundled");
                Assert.IsTrue(captured.ToString().Contains("were NOT linked"),
                    "refused chunks must be reported, not silently counted as zero; stderr was: " + captured);
            }
            finally
            {
                Console.SetError(stderr);
                try { target.AddedBundles.Remove(bid); } catch { }
                try { chunkEntry.AddedBundles.Remove(bid); } catch { }
                try { App.AssetManager.RevertAsset(target); } catch { }
            }
        }

        // P5: `bundle add --with-resources` pulls a texture's res + pixel chunk into the same bundle as
        // its EBX (porting BundleEditorPlugin's per-type sub-resource linking), so a (new) texture ships
        // everything it needs in that bundle.
        [Test]
        [IntegrationTest("swbf2")]
        public void BundleAdd_WithResources_LinksResAndChunk()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Find a texture EBX with a resolvable res + pixel chunk.
            EbxAssetEntry target = null; ResAssetEntry resEntry = null; ChunkAssetEntry chunkEntry = null;
            int scanned = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx("TextureAsset"))
            {
                if (++scanned > 300) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                try
                {
                    ulong rid = (ulong)((dynamic)a.RootObject).Resource;
                    if (rid == 0) continue;
                    ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                    if (re == null) continue;
                    var tex = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(re);
                    if (tex == null || tex.ChunkId == Guid.Empty) continue;
                    ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(tex.ChunkId);
                    if (ce == null) continue;
                    target = e; resEntry = re; chunkEntry = ce; break;
                }
                catch { continue; }
            }
            if (target == null) { Console.WriteLine("skip: no texture with a resolvable res+chunk found"); return; }

            // A bundle that neither the res nor the chunk is already in, so AddToBundle adds it fresh.
            int bid = -1; BundleEntry bundle = null;
            foreach (BundleEntry b in App.AssetManager.EnumerateBundles())
            {
                int id = App.AssetManager.GetBundleId(b);
                if (!resEntry.IsInBundle(id) && !chunkEntry.IsInBundle(id) && !target.IsInBundle(id)) { bid = id; bundle = b; break; }
            }
            if (bundle == null) { Console.WriteLine("skip: no free bundle for the texture's sub-resources"); return; }

            try
            {
                int rc = BundleCommand.ExecuteAdd(new[] { target.Name, "--to", bundle.Name, "--with-resources" }, logger, fmt);
                Assert.AreEqual(0, rc);
                Assert.IsTrue(resEntry.AddedBundles.Contains(bid), "texture res should be linked into the bundle");
                Assert.IsTrue(chunkEntry.AddedBundles.Contains(bid), "texture pixel chunk should be linked into the bundle");
            }
            finally
            {
                try { target.AddedBundles.Remove(bid); } catch { }
                try { resEntry.AddedBundles.Remove(bid); } catch { }
                try { chunkEntry.AddedBundles.Remove(bid); } catch { }
                try { App.AssetManager.RevertAsset(target); } catch { }
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void BundleAdd_UnknownBundle_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry target = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx()) { target = e; break; }
            if (target == null) { Console.WriteLine("skip: no ebx"); return; }

            int rc = BundleCommand.ExecuteAdd(
                new[] { target.Name, "--to", "no_such_bundle_" + Guid.NewGuid().ToString("N") }, logger, fmt);
            Assert.AreEqual(1, rc);
        }
    }
}
