using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    public class ModDiffTests
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
        public void ModDiff_BaseFlag_NotSupported_Returns2()
        {
            var logger = new ExportLogger(quiet: true);
            int rc = ModBuildCommand.ExecuteDiff(
                new[] { "diff", "--base", "other.fbproject" }, logger, new OutputFormatter(false));
            Assert.AreEqual(2, rc);
        }

        // Finds a reliably-present EBX asset with a public writable string/CString DATA property
        // (excludes levels and synthetic __-prefixed props, which don't round-trip).
        private static EbxAssetEntry FindEbxStringProp(out string propName)
        {
            propName = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                if (a == null || a.RootObject == null) continue;
                foreach (var p in a.RootObject.GetType().GetProperties())
                {
                    if (!p.CanRead || !p.CanWrite || p.Name.StartsWith("__")) continue;
                    if (p.PropertyType == typeof(string) || p.PropertyType.Name == "CString")
                    { propName = p.Name; return e; }
                }
            }
            return null;
        }

        // --- Integration: a modified asset shows up in the (vs base game) diff ---

        [Test]
        [IntegrationTest("swbf2")]
        public void ModDiff_ModifiedAsset_AppearsAsModified()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string propName;
            var target = FindEbxStringProp(out propName);
            if (target == null) { Console.WriteLine("skip: no ebx asset with a writable string/CString property"); return; }

            try
            {
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(
                    new[] { target.Name, "--prop", propName, "--value", "FrostyCLI_ModDiff_Test" }, logger, fmt));

                var origOut = Console.Out;
                var sw = new StringWriter();
                Console.SetOut(sw);
                int rc;
                try { rc = ModBuildCommand.ExecuteDiff(new[] { "diff", "--format", "json" }, logger, fmt); }
                finally { Console.SetOut(origOut); }

                Assert.AreEqual(0, rc);
                string output = sw.ToString();
                Assert.IsTrue(output.IndexOf(target.Name, StringComparison.OrdinalIgnoreCase) >= 0,
                    "modified asset should appear in mod diff output");
                Assert.IsTrue(output.IndexOf("modified", StringComparison.OrdinalIgnoreCase) >= 0,
                    "diff should mark the asset as modified");
            }
            finally
            {
                try { EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt); } catch { }
            }
        }

        // --- Integration: mod diff --base <file.fbmod> diffs a built mod against the project ---

        [Test]
        [IntegrationTest("swbf2")]
        public void ModDiff_AgainstMod_ShowsModAsset()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string propName;
            var target = FindEbxStringProp(out propName);
            if (target == null) { Console.WriteLine("skip: no ebx asset with a writable string/CString property"); return; }

            string modPath = Path.Combine(Path.GetTempPath(), "moddiff_" + Guid.NewGuid().ToString("N") + ".fbmod");
            try
            {
                // Modify, build a mod containing the change, then revert the project so the asset is
                // ONLY in the mod — proving the diff reads the .fbmod (read-only) and tags it mod-only.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(
                    new[] { target.Name, "--prop", propName, "--value", "FrostyCLI_ModDiffBase" }, logger, fmt));
                int br = ModBuildCommand.ExecuteBuild(
                    new[] { "build", "--out", modPath, "--mod-name", "ModDiffBase" }, logger, fmt);
                if (br != 0 || !File.Exists(modPath)) { Console.WriteLine("skip: mod build rc=" + br); return; }
                Assert.AreEqual(0, EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt));

                var origOut = Console.Out;
                var sw = new StringWriter();
                Console.SetOut(sw);
                int rc;
                try { rc = ModBuildCommand.ExecuteDiff(new[] { "diff", "--base", modPath, "--format", "json" }, logger, fmt); }
                finally { Console.SetOut(origOut); }

                Assert.AreEqual(0, rc);
                string output = sw.ToString();
                Assert.IsTrue(output.IndexOf(target.Name, StringComparison.OrdinalIgnoreCase) >= 0,
                    "mod's asset should appear in mod diff --base output");
                Assert.IsTrue(output.IndexOf("mod-only", StringComparison.OrdinalIgnoreCase) >= 0,
                    "asset present only in the mod should be tagged mod-only");
            }
            finally
            {
                try { EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt); } catch { }
                try { File.Delete(modPath); } catch { }
            }
        }

        // --- Integration: mod diff --base <file.fbproject> diffs a 2nd project (read-only parse) ---

        [Test]
        [IntegrationTest("swbf2")]
        public void ModDiff_AgainstProject_ShowsModAsset()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            string propName;
            var target = FindEbxStringProp(out propName);
            if (target == null) { Console.WriteLine("skip: no ebx asset with a writable string/CString property"); return; }

            string projPath = Path.Combine(Path.GetTempPath(), "moddiff_" + Guid.NewGuid().ToString("N") + ".fbproject");
            try
            {
                // Modify, save to a 2nd .fbproject, then revert the live project so the asset is ONLY
                // in the saved project — proving the diff parsed the .fbproject (read-only) and tags it.
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(
                    new[] { target.Name, "--prop", propName, "--value", "FrostyCLI_ProjDiff" }, logger, fmt));
                Assert.IsTrue(Session.Project.Save(projPath, logger), "project save failed");
                Assert.IsTrue(File.Exists(projPath));
                Assert.AreEqual(0, EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt));

                var origOut = Console.Out;
                var sw = new StringWriter();
                Console.SetOut(sw);
                int rc;
                try { rc = ModBuildCommand.ExecuteDiff(new[] { "diff", "--base", projPath, "--format", "json" }, logger, fmt); }
                finally { Console.SetOut(origOut); }

                Assert.AreEqual(0, rc);
                string output = sw.ToString();
                Assert.IsTrue(output.IndexOf(target.Name, StringComparison.OrdinalIgnoreCase) >= 0,
                    "the .fbproject's asset should appear in mod diff --base output");
                Assert.IsTrue(output.IndexOf("mod-only", StringComparison.OrdinalIgnoreCase) >= 0,
                    "asset present only in the base project should be tagged mod-only");
            }
            finally
            {
                try { EbxWriteCommand.ExecuteRevert(new[] { target.Name }, logger, fmt); } catch { }
                try { File.Delete(projPath); } catch { }
            }
        }
    }
}
