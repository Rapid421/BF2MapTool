using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests.Integration
{
    public class EbxSearchIntegrationTests
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
        public void EbxSearch_ByType_ReturnsValidJson()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            string firstType = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
                { if (!string.IsNullOrEmpty(e.Type)) { firstType = e.Type; break; } }
            Assert.IsTrue(firstType != null, "No typed EBX assets found");

            string tmp = Path.Combine(Path.GetTempPath(), "search_test_" + Guid.NewGuid() + ".json");
            try
            {
                int r = EbxGetCommand.ExecuteSearch(
                    new[] { "--prop", "Name", "--value", "x", "--type", firstType,
                            "--limit", "1", "--out", tmp },
                    logger, fmt);
                Assert.AreEqual(0, r);
                JArray results = JArray.Parse(File.ReadAllText(tmp));
                Assert.IsNotNull(results); // valid JSON array (may be empty)
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        // Beta bug #4: `ebx search --prop Name --value <name>` used to always return [] (Name is a
        // synthesized field, not a real root property; and CStrings/enums were JSON-quoted/numeric).
        // Now Name matches the path-derived asset name -- searching a real asset's name finds it.
        [Test]
        [IntegrationTest("swbf2")]
        public void EbxSearch_ByName_FindsAssetByPathSegment()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            // Pick a deterministic asset with a reasonably unique (>=10 char) trailing name segment.
            string targetPath = null, seg = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name == null) continue;
                int slash = e.Name.LastIndexOf('/');
                if (slash < 0) continue;
                string s = e.Name.Substring(slash + 1);
                if (s.Length < 10) continue;
                targetPath = e.Name; seg = s; break;
            }
            if (targetPath == null) { Console.WriteLine("skip: no asset with a >=10 char name segment"); return; }

            string tmp = Path.Combine(Path.GetTempPath(), "search_name_" + Guid.NewGuid() + ".json");
            try
            {
                int r = EbxGetCommand.ExecuteSearch(
                    new[] { "--prop", "Name", "--value", seg, "--limit", "100000", "--out", tmp }, logger, fmt);
                Assert.AreEqual(0, r);
                JArray results = JArray.Parse(File.ReadAllText(tmp));
                Assert.IsTrue(results.Count > 0, "search --prop Name should return results (beta bug #4)");
                bool found = false;
                foreach (var x in results)
                    if (string.Equals((string)x["path"], targetPath, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                Assert.IsTrue(found, "search --prop Name --value " + seg + " should find " + targetPath);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EbxSearch_CatalogCached_AfterFirstSearch()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);
            IndexService.Reset();

            string firstType = null;
            foreach (var e in App.AssetManager.EnumerateEbx())
                { if (!string.IsNullOrEmpty(e.Type)) { firstType = e.Type; break; } }

            string tmp = Path.Combine(Path.GetTempPath(), "search_cache_" + Guid.NewGuid() + ".json");
            try
            {
                EbxGetCommand.ExecuteSearch(
                    new[] { "--prop", "Name", "--value", "x", "--type", firstType ?? "SoldierBlueprint",
                            "--limit", "1", "--out", tmp }, logger, fmt);
                string catalogPath = IndexService.GetCatalogPath();
                Assert.IsTrue(File.Exists(catalogPath), "Catalog not written to " + catalogPath);
                Assert.IsTrue(new System.IO.FileInfo(catalogPath).Length > 0);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
