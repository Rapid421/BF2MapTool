using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // `--dry-run`: mutating commands validate + report but skip the write. DryRun.Active is a process
    // static, so every test here MUST reset it in finally — a leak would turn later mutation tests into
    // no-ops (their "assert modified" checks would then fail, so a leak is caught, but reset anyway).
    public class DryRunTests
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
        public void DryRun_BundleAdd_ReportsButDoesNotAdd()
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
                if (e.Name != null && e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!e.IsInBundle(bid)) { target = e; break; }
            }
            if (target == null) { Console.WriteLine("skip: no asset outside the chosen bundle"); return; }

            DryRun.Active = true;
            try
            {
                int rc = BundleCommand.ExecuteAdd(new[] { target.Name, "--to", bundle.Name }, logger, fmt);
                Assert.AreEqual(0, rc); // dry-run reports success
                Assert.IsFalse(target.AddedBundles.Contains(bid), "dry-run must NOT actually add the asset to the bundle");
                Assert.IsFalse(target.IsInBundle(bid), "asset should remain outside the bundle after a dry-run add");
            }
            finally
            {
                DryRun.Active = false;
                try { target.AddedBundles.Remove(bid); } catch { }
                try { App.AssetManager.RevertAsset(target); } catch { }
            }
        }

        // REGRESSION (adversarial review): a dry-run `ebx set` on an asset that is ALREADY modified must
        // not change it. GetEbx returns the cached ModifiedEntry.DataObject for a modified asset, so a
        // mutation-then-skip leaks unless the gate is BEFORE the in-place write.
        [Test]
        [IntegrationTest("swbf2")]
        public void DryRun_EbxSet_OnModifiedAsset_DoesNotLeak()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Find an asset with a writable string/CString property (same shape EbxWriteTests uses).
            EbxAssetEntry target = null; string prop = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name == null || e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset a; try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                foreach (var p in a.RootObject.GetType().GetProperties())
                {
                    if (p.Name == "Name") continue;
                    if (!p.CanWrite) continue;
                    if (p.PropertyType == typeof(string) || p.PropertyType.Name == "CString") { prop = p.Name; break; }
                }
                if (prop != null) { target = e; break; }
            }
            if (target == null) { Console.WriteLine("skip: no writable string/CString property found"); return; }

            string tok1 = "FCLI_REAL_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tok2 = "FCLI_DRY_"  + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                // Real set -> asset becomes modified and its DataObject is cached.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(new[] { target.Name, "--prop", prop, "--value", tok1 }, logger, fmt));
                Assert.IsTrue(App.AssetManager.GetEbxEntry(target.Name).IsModified);

                // Dry-run set to a DIFFERENT value -> must NOT change the cached value.
                DryRun.Active = true;
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(new[] { target.Name, "--prop", prop, "--value", tok2 }, logger, fmt));
                DryRun.Active = false;

                var reAsset = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(target.Name));
                object readBack = reAsset.RootObject.GetType().GetProperty(prop).GetValue(reAsset.RootObject);
                Assert.IsTrue(readBack != null && readBack.ToString() == tok1,
                    "dry-run set must NOT mutate the already-modified asset (expected " + tok1 + ", got " + readBack + ")");
            }
            finally
            {
                DryRun.Active = false;
                try { App.AssetManager.RevertAsset(App.AssetManager.GetEbxEntry(target.Name)); } catch { }
            }
        }

        // After dry-run is reset, a real add still works -- proves the gate doesn't permanently disable writes.
        [Test]
        [IntegrationTest("swbf2")]
        public void DryRun_Off_RealBundleAdd_Adds()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
            Assert.IsFalse(DryRun.Active, "DryRun.Active must be false by default (no prior test leaked it)");

            BundleEntry bundle = null;
            foreach (BundleEntry b in App.AssetManager.EnumerateBundles()) { bundle = b; break; }
            if (bundle == null) { Console.WriteLine("skip: no bundles"); return; }
            int bid = App.AssetManager.GetBundleId(bundle);

            EbxAssetEntry target = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name != null && e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!e.IsInBundle(bid)) { target = e; break; }
            }
            if (target == null) { Console.WriteLine("skip: no asset outside the chosen bundle"); return; }

            try
            {
                Assert.AreEqual(0, BundleCommand.ExecuteAdd(new[] { target.Name, "--to", bundle.Name }, logger, fmt));
                Assert.IsTrue(target.AddedBundles.Contains(bid), "a real (non-dry) add should add the asset");
            }
            finally
            {
                try { target.AddedBundles.Remove(bid); } catch { }
                try { App.AssetManager.RevertAsset(target); } catch { }
            }
        }
    }
}
