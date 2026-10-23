using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Commands;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests.Integration
{
    // `ebx diff-shipped` against real game data.
    //
    // The acceptance case from the brief: break BundleHeap.AllowGrow on one of Kashyyyk's
    // SubWorldReferenceObjectData and have the command name that field as an outlier without being
    // told what to look for. All nine shipped references in Levels/MP/Kashyyyk_01/GameModes carry
    // AllowGrow=true; a false one produces a sub-level that is created and streams nothing, and
    // nothing errors.
    public class DiffShippedTests
    {
        private const string GameModes = "Levels/MP/Kashyyyk_01/GameModes";

        private static void EnsureSession(ExportLogger logger)
        {
            if (Session.IsInitialized) return;
            string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string adaptersDir = Path.Combine(exeDir, "Adapters");
            if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
            Session.Initialize("swbf2", gp, null, adaptersDir, logger);
        }

        private static JObject RunJson(string[] args, out int rc)
        {
            var sb = new StringBuilder();
            var fmt = new OutputFormatter(json: false, output: new StringWriter(sb));
            rc = EbxDiffShippedCommand.Execute(args, new ExportLogger(quiet: true), fmt);
            string text = sb.ToString().Trim();
            return text.Length == 0 ? null : JObject.Parse(text);
        }

        private static string FirstObjectGuidOfType(FrostySdk.IO.EbxAsset asset, string typeName)
        {
            foreach (object o in asset.Objects)
            {
                if (o == null || o.GetType().Name != typeName) continue;
                Guid g = PointerRefUtil.InstanceGuid(o);
                if (g != Guid.Empty) return g.ToString();
            }
            return null;
        }

        // The whole point: a field the shipped population agrees on, where we do not, is named first
        // and without being asked about.
        [Test]
        [IntegrationTest("swbf2")]
        public void DiffShipped_AllowGrowFalse_IsNamedAsAnOutlier()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(GameModes);
            if (entry == null) { Console.WriteLine("skip: Kashyyyk GameModes not found"); return; }
            string guid = FirstObjectGuidOfType(App.AssetManager.GetEbx(entry), "SubWorldReferenceObjectData");
            if (guid == null) { Console.WriteLine("skip: no exported SubWorldReferenceObjectData"); return; }

            int set = EbxWriteCommand.ExecuteSet(new[]
            { GameModes, "--object", guid, "--prop", "BundleHeap.AllowGrow", "--value", "false" }, logger, fmt);
            Assert.AreEqual(0, set);

            try
            {
                int rc;
                JObject report = RunJson(new[]
                { GameModes, "--object", guid, "--format", "json" }, out rc);
                Assert.AreEqual(0, rc);
                Assert.IsNotNull(report);

                JArray findings = (JArray)report["findings"];
                Assert.IsTrue(findings.Count > 0, "expected findings");

                // Ranked first, because a unanimous population disagreeing with us is the strongest
                // signal available without launching the game.
                Assert.AreEqual("BundleHeap.AllowGrow", (string)findings[0]["field"]);
                Assert.AreEqual("unanimous-disagreement", (string)findings[0]["verdict"]);
                Assert.AreEqual("False", (string)findings[0]["ours"]);
                Assert.IsTrue((int)findings[0]["shipped_count"] >= 8,
                    "the shipped population should hold the other Kashyyyk references; got " +
                    findings[0]["shipped_count"]);
            }
            finally
            {
                EbxWriteCommand.ExecuteRevert(new[] { GameModes }, logger, fmt);
            }
        }

        // The population must be SHIPPED data. If it were read through the project, an object
        // compared against assets we had already broken the same way would come back clean --
        // which is exactly how the original defects survived review.
        [Test]
        [IntegrationTest("swbf2")]
        public void DiffShipped_ComparesAgainstUnmodifiedData_NotOurEdits()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(GameModes);
            if (entry == null) { Console.WriteLine("skip: Kashyyyk GameModes not found"); return; }

            // Break AllowGrow on EVERY SubWorldReferenceObjectData in the asset, then ask about one
            // of them. Read through the project, its siblings would now agree with it.
            var guids = new List<string>();
            foreach (object o in App.AssetManager.GetEbx(entry).Objects)
            {
                if (o == null || o.GetType().Name != "SubWorldReferenceObjectData") continue;
                Guid g = PointerRefUtil.InstanceGuid(o);
                if (g != Guid.Empty) guids.Add(g.ToString());
            }
            if (guids.Count < 3) { Console.WriteLine("skip: fewer than three references to work with"); return; }

            try
            {
                foreach (string g in guids)
                    Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(new[]
                    { GameModes, "--object", g, "--prop", "BundleHeap.AllowGrow", "--value", "false" }, logger, fmt));

                int rc;
                JObject report = RunJson(new[] { GameModes, "--object", guids[0], "--format", "json" }, out rc);
                Assert.AreEqual(0, rc);

                JArray findings = (JArray)report["findings"];
                string verdict = null;
                foreach (JToken f in findings)
                    if ((string)f["field"] == "BundleHeap.AllowGrow") { verdict = (string)f["verdict"]; break; }
                Assert.AreEqual("unanimous-disagreement", verdict);
            }
            finally
            {
                EbxWriteCommand.ExecuteRevert(new[] { GameModes }, logger, fmt);
            }
        }

        // An unbroken object should come back with nothing ranked above "per-instance": no
        // disagreements at all. A tool that cries wolf on correct data is worse than none.
        [Test]
        [IntegrationTest("swbf2")]
        public void DiffShipped_UnmodifiedObject_ReportsNoDisagreements()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(GameModes);
            if (entry == null) { Console.WriteLine("skip: Kashyyyk GameModes not found"); return; }
            string guid = FirstObjectGuidOfType(App.AssetManager.GetEbx(entry), "SubWorldReferenceObjectData");
            if (guid == null) { Console.WriteLine("skip: no exported SubWorldReferenceObjectData"); return; }

            int rc;
            JObject report = RunJson(new[] { GameModes, "--object", guid, "--format", "json" }, out rc);
            Assert.AreEqual(0, rc);

            var bad = new List<string>();
            foreach (JToken f in (JArray)report["findings"])
            {
                string v = (string)f["verdict"];
                if (v == "unanimous-disagreement" || v == "derived-violation" || v == "disagreement")
                    bad.Add(f["field"] + "=" + f["ours"] + " (" + v + ")");
            }
            Assert.IsTrue(bad.Count == 0, "a shipped object should not disagree with shipped data: " +
                string.Join(", ", bad.ToArray()));
        }

        // Flags is not a free bitfield: its low 25 bits are the object's own exported GUID. A
        // duplicated object that keeps its source's Flags shares a sub-level identity with it, and
        // that is invisible to a distribution check because every shipped value is already unique.
        [Test]
        [IntegrationTest("swbf2")]
        public void DiffShipped_FlagsCopiedFromASibling_IsReportedAsDerivedViolation()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(GameModes);
            if (entry == null) { Console.WriteLine("skip: Kashyyyk GameModes not found"); return; }

            string mine = null, sibling = null; uint siblingFlags = 0;
            foreach (object o in App.AssetManager.GetEbx(entry).Objects)
            {
                if (o == null || o.GetType().Name != "SubWorldReferenceObjectData") continue;
                Guid g = PointerRefUtil.InstanceGuid(o);
                if (g == Guid.Empty) continue;
                if (mine == null) { mine = g.ToString(); continue; }
                sibling = g.ToString();
                siblingFlags = Convert.ToUInt32(o.GetType().GetProperty("Flags").GetValue(o));
                break;
            }
            if (mine == null || sibling == null) { Console.WriteLine("skip: need two references"); return; }

            try
            {
                Assert.AreEqual(0, EbxWriteCommand.ExecuteSet(new[]
                { GameModes, "--object", mine, "--prop", "Flags",
                  "--value", siblingFlags.ToString() }, logger, fmt));

                int rc;
                JObject report = RunJson(new[] { GameModes, "--object", mine, "--format", "json" }, out rc);
                Assert.AreEqual(0, rc);

                string verdict = null;
                foreach (JToken f in (JArray)report["findings"])
                    if ((string)f["field"] == "Flags") { verdict = (string)f["verdict"]; break; }
                Assert.AreEqual("derived-violation", verdict);
            }
            finally
            {
                EbxWriteCommand.ExecuteRevert(new[] { GameModes }, logger, fmt);
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void DiffShipped_UnknownObject_Returns1()
        {
            var logger = new ExportLogger(quiet: true);
            EnsureSession(logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
            if (App.AssetManager.GetEbxEntry(GameModes) == null) { Console.WriteLine("skip: asset not found"); return; }

            int rc;
            RunJson(new[] { GameModes, "--object", "ffffffff-ffff-ffff-ffff-ffffffffffff", "--format", "json" }, out rc);
            Assert.AreEqual(1, rc);
        }
    }
}
