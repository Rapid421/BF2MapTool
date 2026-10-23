using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostyCLI.Core;
using FrostyCLI.Commands;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests.Integration
{
    public class EbxDumpIntegrationTests
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
        public void EbxDump_KnownAsset_HasExpectedType()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            // Pick a deterministic, reliably-dumpable asset (a TextureAsset) rather than whatever
            // entry happens to enumerate first -- the latter made this test order-dependent.
            FrostySdk.Managers.EbxAssetEntry entry = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
                if (e.Type == "TextureAsset") { entry = e; break; }
            if (entry == null)
                foreach (var e in App.AssetManager.EnumerateEbx()) { entry = e; break; }
            Assert.IsTrue(entry != null, "No EBX assets found");

            string tmp = Path.Combine(Path.GetTempPath(), "dump_test_" + Guid.NewGuid() + ".json");
            try
            {
                int r = EbxGetCommand.ExecuteDump(new[] { entry.Name, "--out", tmp, "--no-resolve" }, logger, fmt);
                Assert.AreEqual(0, r);
                Assert.IsTrue(File.Exists(tmp));
                JObject jo = JObject.Parse(File.ReadAllText(tmp));
                string gotType = (string)jo["__type"];
                Assert.IsTrue(!string.IsNullOrEmpty(gotType), "__type is empty in dump output");
                Assert.AreEqual(entry.Type, gotType);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        // --deep expands internal-ref PointerRefs (which shallow dump prints as "internal ref") into the
        // target object's fields, so multi-object assets (e.g. hero Abilities) show their full layout.
        [Test]
        [IntegrationTest("swbf2")]
        public void EbxDump_Deep_ExpandsInternalRefs()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);

            // Find an asset whose SHALLOW dump notes at least one internal ref.
            FrostySdk.IO.EbxAsset target = null; int scanned = 0;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (++scanned > 600) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                if (EbxDumper.Dump(a, resolve: false, deep: false).ToString().Contains("\"internal ref\""))
                { target = a; break; }
            }
            if (target == null) { Console.WriteLine("skip: no asset with an internal ref found"); return; }

            string shallow = EbxDumper.Dump(target, resolve: false, deep: false).ToString();
            string deep    = EbxDumper.Dump(target, resolve: false, deep: true).ToString();

            int shallowRefs = CountOccurrences(shallow, "\"internal ref\"");
            int deepRefs    = CountOccurrences(deep, "\"internal ref\"");
            Assert.IsTrue(shallowRefs > 0, "precondition: shallow dump should have an internal ref");
            Assert.IsTrue(deepRefs < shallowRefs, "deep dump should expand internal refs (fewer 'internal ref' notes)");
            Assert.IsTrue(deep.Length > shallow.Length, "deep dump should contain the expanded object's fields");
        }

        // External PointerRefs must resolve to the target asset's path (EbxImportReference.FileGuid is a
        // field, not a property -- reading it via GetProperty made every external ref render bare). Each
        // external ref now emits __fileGuid + __target (+ __path when resolvable), in array order.
        [Test]
        [IntegrationTest("swbf2")]
        public void EbxDump_ExternalRef_ResolvesTargetPath()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);

            FrostySdk.IO.EbxAsset target = null; int scanned = 0;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (++scanned > 600) break;
                FrostySdk.IO.EbxAsset a;
                try { a = App.AssetManager.GetEbx(e); } catch { continue; }
                // deep so refs inside internal objects count too
                if (EbxDumper.Dump(a, resolve: true, deep: true).ToString().Contains("\"__fileGuid\""))
                { target = a; break; }
            }
            if (target == null) { Console.WriteLine("skip: no asset with an external ref found"); return; }

            string dump = EbxDumper.Dump(target, resolve: true, deep: true).ToString();
            Assert.IsTrue(dump.Contains("\"__fileGuid\""), "external ref should emit __fileGuid (not render bare)");
            Assert.IsTrue(dump.Contains("\"__target\""), "external ref should emit a __target");
            Assert.IsTrue(dump.Contains("\"__path\""), "at least one external ref should resolve to a __path");
        }

        private static int CountOccurrences(string s, string sub)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
            return n;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxDump_NoResolve_NoPaths()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            // Pick a deterministic, reliably-dumpable asset (a TextureAsset) rather than whatever
            // entry happens to enumerate first -- the latter made this test order-dependent.
            FrostySdk.Managers.EbxAssetEntry entry = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
                if (e.Type == "TextureAsset") { entry = e; break; }
            if (entry == null)
                foreach (var e in App.AssetManager.EnumerateEbx()) { entry = e; break; }
            Assert.IsNotNull(entry);

            string tmp = Path.Combine(Path.GetTempPath(), "dump_noresolve_" + Guid.NewGuid() + ".json");
            try
            {
                EbxGetCommand.ExecuteDump(new[] { entry.Name, "--out", tmp, "--no-resolve" }, logger, fmt);
                string json = File.ReadAllText(tmp);
                Assert.IsFalse(json.Contains("\"__path\""), "--no-resolve should produce no __path fields");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
