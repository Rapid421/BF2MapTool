using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostyCLI.Core;
using FrostyCLI.Commands;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests.Integration
{
    public class EbxRefsIntegrationTests
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Session.Initialize("swbf2", gp, null, Path.Combine(exeDir, "Adapters"), logger);
        }

        [Test]
        [IntegrationTest("swbf2", Slow = true)]
        public void EbxRefs_ByPath_ReturnsValidJson()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            string testPath = "s2/objects/soldier/common/common_soldier";
            if (App.AssetManager.GetEbxEntry(testPath) == null)
            {
                foreach (var e in App.AssetManager.EnumerateEbx())
                    { if (e.Name != null) { testPath = e.Name; break; } }
            }

            string tmp = Path.Combine(Path.GetTempPath(), "refs_test_" + Guid.NewGuid() + ".json");
            try
            {
                int r = EbxGetCommand.ExecuteRefs(new[] { testPath, "--out", tmp }, logger, fmt);
                Assert.AreEqual(0, r);
                JArray arr = JArray.Parse(File.ReadAllText(tmp));
                Assert.IsNotNull(arr);
                Console.WriteLine("  refs count for " + testPath + ": " + arr.Count);

                // The reverse-reference index must not be empty: a real game has hundreds of
                // thousands of cross-asset references. An empty map means CollectRefs is broken
                // (e.g. only walking RootObject instead of every object in the file).
                RefIndex idx = IndexService.GetRefIndex();
                Assert.IsTrue(idx.Refs.Count > 0, "reference index is empty — CollectRefs is broken");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        [IntegrationTest("swbf2", Slow = true)]
        public void EbxRefs_ByGuid_MatchesPathResult()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            FrostySdk.Managers.EbxAssetEntry entry = null;
            foreach (var e in App.AssetManager.EnumerateEbx()) { entry = e; break; }
            Assert.IsNotNull(entry);

            string tmpPath = Path.Combine(Path.GetTempPath(), "refs_bypath_" + Guid.NewGuid() + ".json");
            string tmpGuid = Path.Combine(Path.GetTempPath(), "refs_byguid_" + Guid.NewGuid() + ".json");
            try
            {
                EbxGetCommand.ExecuteRefs(new[] { entry.Name, "--out", tmpPath }, logger, fmt);
                EbxGetCommand.ExecuteRefs(new[] { "--guid", entry.Guid.ToString("D"), "--out", tmpGuid }, logger, fmt);
                Assert.AreEqual(File.ReadAllText(tmpPath), File.ReadAllText(tmpGuid));
            }
            finally
            {
                if (File.Exists(tmpPath)) File.Delete(tmpPath);
                if (File.Exists(tmpGuid)) File.Delete(tmpGuid);
            }
        }
    }
}
