using System;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests.Integration
{
    public class SoundGraphIntegrationTests
    {
        private const string Patch = "Sound/SP/Planets/Bespin/Shared/SFX/SW02_SP_Planets_Bespin_Shared_SFX_GasTanks_Loop_Patch";
        private static readonly ExportLogger Logger = new ExportLogger(quiet: true);
        private static void Ensure()
        {
            if (!Session.IsInitialized) Session.Initialize("swbf2", Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2"), null, null, Logger);
            if (!Session.Project.IsLoaded) Session.Project.New();
        }
        private static SoundGraphPackage Capture() { Ensure(); return SoundGraphPackage.Capture(App.AssetManager.GetEbxEntry(Patch)); }

        [Test, IntegrationTest("swbf2")]
        public void RoundTrip_PreservesCompleteGraphAndIndependentIdentities()
        {
            var source = Capture();
            string name = "Tests/Graphs/" + Guid.NewGuid().ToString("N");
            var prepared = SoundGraphPort.Prepare(source, null, name);
            Assert.IsTrue(prepared.Ready, prepared.Report.ToString());
            var expected = (JArray)source.Objects.DeepClone(); expected[0]["fields"]["Name"] = name;
            var actual = SoundGraphPackage.CaptureAsset(prepared.Asset, name);
            Assert.IsTrue(SoundWavePort.EqualFields(expected, actual.Objects), "node/port/compiled graph data changed");
            var donorGuids = App.AssetManager.GetEbx(App.AssetManager.GetEbxEntry(Patch)).Objects.Select(SoundGraphPackage.InstanceGuid).ToList();
            Assert.IsTrue(prepared.Asset.Objects.All(o => !donorGuids.Contains(SoundGraphPackage.InstanceGuid(o))));
            Assert.IsNull(App.AssetManager.GetEbxEntry(name));
        }

        [Test, IntegrationTest("swbf2")]
        public void CrossProfile_RequiresEveryExternalReferenceEvenWhenGuidsMatch()
        {
            var source = Capture(); source.Game = "swbf1"; source.Seal();
            var result = SoundGraphPort.Prepare(source, null, "Tests/Graphs/NotAdded");
            Assert.IsFalse(result.Ready);
            Assert.IsTrue(result.Report["issues"].Any(x => x.ToString().Contains("Map reference")));
            var mappings = new JObject();
            foreach (var external in (JArray)source.Inspect()["references"])
                mappings[SoundGraphPackage.ReferenceKey(external)] = new JObject { ["asset"] = external["asset"], ["class_guid"] = external["class_guid"] };
            result = SoundGraphPort.Prepare(source, new JObject { ["references"] = mappings }, "Tests/Graphs/NotAdded");
            Assert.IsTrue(result.Ready, result.Report.ToString());
            Assert.IsNull(App.AssetManager.GetEbxEntry("Tests/Graphs/NotAdded"));
        }

        [Test, IntegrationTest("swbf2")]
        public void Migration_RequiresExplicitSchemaRulesAndRejectsTypos()
        {
            var source = Capture();
            var generator = source.Objects.OfType<JObject>().First(o => (string)o["type"] == "NumberGeneratorNodeData");
            var fields = (JObject)generator["fields"];
            fields["Min"] = 0.25; fields["OldVersionOnly"] = true; source.Seal();
            Assert.IsFalse(SoundGraphPort.Prepare(source, null, "Tests/Graphs/Migration").Ready);
            var rules = new JObject {
                ["NumberGeneratorNodeData.Min"] = new JObject { ["constant_port"] = true },
                ["NumberGeneratorNodeData.OldVersionOnly"] = new JObject { ["drop"] = true }
            };
            var result = SoundGraphPort.Prepare(source, new JObject { ["fields"] = rules }, "Tests/Graphs/Migration");
            Assert.IsTrue(result.Ready, result.Report.ToString());
            rules["SamplerNodeData.MisspelledField"] = new JObject { ["drop"] = true };
            result = SoundGraphPort.Prepare(source, new JObject { ["fields"] = rules }, "Tests/Graphs/Migration");
            Assert.IsFalse(result.Ready);
            Assert.IsTrue(result.Report["issues"].Any(x => x.ToString().Contains("Unused field rule")));
            rules.Remove("SamplerNodeData.MisspelledField");
            rules["SoundPatchAsset.Loudness"] = new JObject { ["value"] = false };
            result = SoundGraphPort.Prepare(source, new JObject { ["fields"] = rules }, "Tests/Graphs/Migration");
            Assert.IsFalse(result.Ready, "a boolean must not silently become a numeric playback setting");
        }

        [Test, IntegrationTest("swbf2")]
        public void ReferenceMapping_RejectsWrongObjectTypesAndChannelLayouts()
        {
            var source = Capture();
            var external = ((JArray)source.Inspect()["references"]).First(e => (string)e["type"] == "SoundWaveAsset");
            var rules = new JObject { [SoundGraphPackage.ReferenceKey(external)] = new JObject { ["asset"] = Patch } };
            var result = SoundGraphPort.Prepare(source, new JObject { ["references"] = rules }, "Tests/Graphs/BadReference");
            Assert.IsFalse(result.Ready);
            Assert.IsTrue(result.Report["issues"].Any(x => x.ToString().Contains("dependency type differs")));
            foreach (var e in source.Objects.Descendants().OfType<JObject>().Where(o => o["external"]?["type"]?.ToString() == "SoundWaveAsset")) e["external"]["channels"] = 7;
            source.Seal();
            result = SoundGraphPort.Prepare(source, null, "Tests/Graphs/BadChannels");
            Assert.IsFalse(result.Ready);
            Assert.IsTrue(result.Report["issues"].Any(x => x.ToString().Contains("channel count differs")));
        }

        [Test, IntegrationTest("swbf2")]
        public void ImportAndDryRun_LeaveDonorUnchangedAndRefuseOverwrite()
        {
            var source = Capture();
            string name = "Tests/Graphs/" + Guid.NewGuid().ToString("N");
            var result = SoundGraphPort.Prepare(source, null, name);
            var bundle = App.AssetManager.EnumerateBundles().First(); int bid = App.AssetManager.GetBundleId(bundle);
            Assert.IsTrue(result.Ready, result.Report.ToString());
            Assert.IsNull(SoundGraphPort.Import(result, name, bid, true));
            Assert.IsNull(App.AssetManager.GetEbxEntry(name));
            try
            {
                var entry = SoundGraphPort.Import(result, name, bid, false);
                Assert.IsTrue(entry.AddedBundles.Contains(bid));
                Assert.Throws<InvalidDataException>(() => SoundGraphPort.Import(result, name, bid, false));
                var readBack = SoundGraphPackage.Capture(entry);
                Assert.IsTrue(SoundWavePort.EqualFields(SoundGraphPackage.CaptureAsset(result.Asset, name).Objects, readBack.Objects));
                Assert.IsTrue(SoundWavePort.EqualFields(source.Objects, Capture().Objects), "donor changed");
            }
            finally { var entry = App.AssetManager.GetEbxEntry(name); if (entry != null) App.AssetManager.RevertAsset(entry); }
        }
    }
}
