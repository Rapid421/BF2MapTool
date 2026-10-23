using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // `where <name-or-guid>` resolves an asset to its path/guid/type (exact path, file GUID, or substring).
    public class WhereTests
    {
        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Session.Initialize("swbf2", gp, null, Path.Combine(exeDir, "Adapters"), logger);
        }

        [Test]
        public void Where_MissingQuery_Returns1()
        {
            int rc = new WhereCommand().Execute(new string[0], new ExportLogger(quiet: true), new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void Where_ResolvesByGuidAndName()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            EnsureSession(logger);

            EbxAssetEntry target = null;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                if (e.Name != null && e.Guid != Guid.Empty) { target = e; break; }
            if (target == null) { Console.WriteLine("skip: no ebx with a guid"); return; }

            // Exact path resolves.
            Assert.AreEqual(0, new WhereCommand().Execute(new[] { target.Name }, logger, fmt));

            // GUID resolves to the same path -- capture via a StringWriter-backed formatter.
            var sb = new System.Text.StringBuilder();
            var capFmt = new OutputFormatter(json: false, output: new StringWriter(sb));
            int rc = new WhereCommand().Execute(new[] { target.Guid.ToString() }, logger, capFmt);
            Assert.AreEqual(0, rc);
            Assert.IsTrue(sb.ToString().IndexOf(target.Name, StringComparison.OrdinalIgnoreCase) >= 0,
                "where <guid> should print the asset's path");
        }
    }
}
