using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class EbxWriteTests
    {
        private static string Repo()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", ".."));
        }
        private static string ProjPath()
        {
            return Path.Combine(Repo(), "community-mods", "HvVOnBespinCampaign.fbproject");
        }

        private static void EnsureSession(string gp, ExportLogger logger)
        {
            if (!Session.IsInitialized)
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string adaptersDir = Path.Combine(exeDir, "Adapters");
                if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
                Session.Initialize("swbf2", gp, null, adaptersDir, logger);
            }
        }

        // Returns the entry if the named asset's root object has a public writable string/CString
        // property, setting propName to it; otherwise null. Used to pick a deterministic target.
        private static EbxAssetEntry FindStringProp(string name, out string propName)
        {
            propName = null;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(name);
            if (e == null) return null;
            FrostySdk.IO.EbxAsset a;
            try { a = App.AssetManager.GetEbx(e); } catch { return null; }
            if (a == null || a.RootObject == null) return null;
            foreach (var p in a.RootObject.GetType().GetProperties())
            {
                if (!p.CanRead || !p.CanWrite) continue;
                if (p.PropertyType == typeof(string) || p.PropertyType.Name == "CString")
                { propName = p.Name; return e; }
            }
            return null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxSet_RoundTrip_PropertyPersists()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            string gp  = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            // A fresh project gives a writable context without the headless full-load limitation
            // (FrostyProject.Load only partially applies the real .fbproject headlessly).
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Find a writable string-or-CString property to round-trip. SWBF2's generated EBX
            // types expose text as FrostySdk.Ebx.CString (a struct), NOT System.String, so the
            // finder must accept both -- this is exactly the type that 'ebx set' coerces via
            // TypeCoercion (CString has a public (string) constructor).
            //
            // Prefer a known benign data asset (the same one EbxRevert uses); fall back to a
            // search that *excludes level assets* -- mutating a level's root drops its entity list
            // and would pollute later level-reading tests in this shared process.
            EbxAssetEntry target = FindStringProp("s2/gameplay/repository", out string propName);
            if (target == null)
            {
                foreach (var e in App.AssetManager.EnumerateEbx())
                {
                    if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    target = FindStringProp(e.Name, out propName);
                    if (target != null) break;
                }
            }
            if (target == null || propName == null)
            {
                Console.WriteLine("skip: no asset with a public writable string/CString property in this build");
                return;
            }

            string token = "FROSTYCLI_TEST_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string tempPath = Path.Combine(Path.GetTempPath(), "ebxset_test_" + Guid.NewGuid() + ".fbproject");
            try
            {
                int r = EbxWriteCommand.ExecuteSet(
                    new[] { target.Name, "--prop", propName, "--value", token }, logger, fmt);
                Assert.AreEqual(0, r);

                // Read the value back from the (now-modified) in-memory asset and confirm it persisted.
                // CString.ToString() / System.String both round-trip through ToString() here.
                var reAsset = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(target.Name));
                object readBack = reAsset.RootObject.GetType().GetProperty(propName).GetValue(reAsset.RootObject);
                Assert.IsNotNull(readBack);
                Assert.AreEqual(token, readBack.ToString());

                bool saved = Session.Project.Save(tempPath, logger);
                Assert.IsTrue(saved, "Project save returned false");
                Assert.IsTrue(File.Exists(tempPath));
                Assert.IsTrue(new FileInfo(tempPath).Length > 0);
            }
            finally
            {
                // Revert so this base-game mutation doesn't pollute shared AssetManager state for
                // later tests in the same process (e.g. 'level list').
                try { EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt); } catch { }
                try { File.Delete(tempPath); } catch { }
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxClone_NewEntry_CanBeSaved()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            string gp  = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            if (!Session.Project.IsLoaded) Session.Project.Load(ProjPath(), logger);

            EbxAssetEntry source = null;
            foreach (var e in App.AssetManager.EnumerateEbx()) { source = e; break; }
            Assert.IsTrue(source != null);

            string dest = source.Name + "_clone_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            int r = EbxWriteCommand.ExecuteClone(new[] { source.Name, "--as", dest }, logger, fmt);
            Assert.AreEqual(0, r);

            string tempPath = Path.Combine(Path.GetTempPath(), "ebxclone_test_" + Guid.NewGuid() + ".fbproject");
            try
            {
                bool saved = Session.Project.Save(tempPath, logger);
                Assert.IsTrue(saved, "Project save returned false");
                Assert.IsTrue(File.Exists(tempPath));
            }
            finally { try { File.Delete(tempPath); } catch { } }
        }

        // A project-load in a process that already holds a project read the file over the old edits: the clone the
        // file adds was added a second time ("An item with the same key has already been added"), the load stopped
        // partway and saves were refused. The loaded project must be whole, twice over, and keep the clone.
        [Test]
        [IntegrationTest("swbf2")]
        public void ProjectLoad_SameProcessAfterClone_LoadsCompletely()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            string gp  = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            Session.Project.New();

            EbxAssetEntry source = null;
            foreach (var e in App.AssetManager.EnumerateEbx()) { source = e; break; }
            Assert.IsTrue(source != null);
            string dest = source.Name + "_reload_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Assert.AreEqual(0, EbxWriteCommand.ExecuteClone(new[] { source.Name, "--as", dest }, logger, fmt));

            string tempPath = Path.Combine(Path.GetTempPath(), "reload_test_" + Guid.NewGuid() + ".fbproject");
            try
            {
                Assert.IsTrue(Session.Project.Save(tempPath, logger), "Project save returned false");
                for (int i = 0; i < 2; i++)
                {
                    Session.Project.Load(tempPath, logger);
                    Assert.IsTrue(Session.Project.IsComplete, "same-process load #" + (i + 1) + " was partial");
                    Assert.IsTrue(App.AssetManager.GetEbxEntry(dest) != null, "the clone is missing after load #" + (i + 1));
                }
                Assert.IsTrue(Session.Project.Save(tempPath, logger), "save after a same-process reload was refused");
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
                // later tests expect the shared fixture project, not this temp one
                try { if (File.Exists(ProjPath())) Session.Project.Load(ProjPath(), logger); else Session.Project.New(); } catch { }
            }
        }

        // Beta bug #3: ebx clone must (a) produce a FINDABLE asset (was ModifyEbx instead of AddEbx) and
        // (b) PRESERVE data the old JSON clone dropped -- here a non-null CString value survives the
        // binary round-trip.
        [Test]
        [IntegrationTest("swbf2")]
        public void EbxClone_PreservesCStringAndIsFindable()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            string gp  = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            EnsureSession(gp, logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            // Find a non-level asset whose root has a non-empty string/CString property.
            EbxAssetEntry src = null; string prop = null; string origVal = null;
            int scanned = 0;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (++scanned > 2500) break;
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                if (a == null || a.RootObject == null) continue;
                foreach (var p in a.RootObject.GetType().GetProperties())
                {
                    if (!p.CanRead || p.Name.StartsWith("__")) continue;
                    if (p.Name == "Name") continue; // clone overwrites Name with the dest path
                    if (p.PropertyType != typeof(string) && p.PropertyType.Name != "CString") continue;
                    object v; try { v = p.GetValue(a.RootObject); } catch { continue; }
                    if (v == null || v.ToString().Length == 0) continue;
                    src = e; prop = p.Name; origVal = v.ToString(); break;
                }
                if (src != null) break;
            }
            if (src == null) { Console.WriteLine("skip: no asset with a non-empty string/CString root prop"); return; }

            string dest = src.Name + "_cloneok_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try
            {
                int rc = EbxWriteCommand.ExecuteClone(new[] { src.Name, "--as", dest }, logger, fmt);
                Assert.AreEqual(0, rc);

                EbxAssetEntry ce = App.AssetManager.GetEbxEntry(dest);
                Assert.IsTrue(ce != null, "cloned asset must be findable (AddEbx, not ModifyEbx)");

                var ca = App.AssetManager.GetEbx(ce);
                object cval = ca.RootObject.GetType().GetProperty(prop).GetValue(ca.RootObject);
                Assert.IsNotNull(cval);
                Assert.AreEqual(origVal, cval.ToString()); // JSON clone would have nulled this
            }
            finally { try { EbxWriteCommand.ExecuteDelete(new[] { dest }, logger, fmt); } catch { } }
        }
    }
}
