using System;
using System.IO;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests
{
    public class SoundGraphPackageTests
    {
        internal static JObject Record(string type, JObject fields) => new JObject { ["type"] = type, ["fields"] = fields };
        internal static JObject Ref(int id) => new JObject { ["ref"] = id };
        internal static SoundGraphPackage Fixture()
        {
            var package = new SoundGraphPackage { Game = "swbf1", Asset = "Tests/Graph", FileGuid = Guid.NewGuid().ToString() };
            package.Objects.Add(Record("SoundPatchAsset", new JObject { ["Graph"] = Ref(1), ["OutputNodes"] = new JArray(Ref(2)) }));
            package.Objects.Add(Record("SoundGraphData", new JObject {
                ["ValueCount"] = 3, ["PublicValueCount"] = 1, ["Nodes"] = new JArray(Ref(2)),
                ["Info"] = Record("SoundGraphInfo", new JObject {
                    ["Voices"] = new JArray(Record("SoundGraphVoiceInfo", new JObject { ["Plugins"] = new JArray(Record("SoundGraphPluginInfo", new JObject {
                        ["ConnectionIndex"] = 0, ["ConstructParamsIndex"] = 0, ["ConstructParamCount"] = 0
                    })) })),
                    ["Connections"] = new JArray(), ["ConstructParams"] = new JArray(), ["PluginCount"] = 1
                })
            }));
            package.Objects.Add(Record("OutputNodeData", new JObject {
                ["In"] = Record("AudioGraphNodePort", new JObject { ["IsConnected"] = true, ["ValueIndex"] = 0, ["UnconnectedValue"] = 0.0 }),
                ["Plugin"] = Record("SoundGraphPluginRef", new JObject { ["IsValid"] = true, ["VoiceIndex"] = 0, ["PluginIndex"] = 0 }),
                ["BackReference"] = Ref(0)
            }));
            package.Seal(); return package;
        }

        [Test] public void ObjectTable_PreservesSharingCyclesAndOutputIndexZero()
        {
            var package = Fixture(); package.Validate();
            var restored = JsonConvert.DeserializeObject<SoundGraphPackage>(JsonConvert.SerializeObject(package));
            restored.Validate();
            Assert.IsTrue(JToken.DeepEquals(package.Objects, restored.Objects));
            Assert.AreEqual(2, (int)restored.Objects[0]["fields"]["OutputNodes"][0]["ref"]);
            Assert.AreEqual(0, (int)restored.Objects[2]["fields"]["BackReference"]["ref"]);
        }

        [Test] public void Validation_RejectsBrokenPortsCountsAndReferences()
        {
            foreach (Action<SoundGraphPackage> change in new Action<SoundGraphPackage>[] {
                p => p.Objects[0]["fields"]["Graph"]["ref"] = 99,
                p => p.Objects[1]["fields"]["ValueCount"] = 0,
                p => p.Objects[1]["fields"]["PublicValueCount"] = 4,
                p => ((JArray)p.Objects[1]["fields"]["Nodes"]).Add(Ref(2)),
                p => p.Objects[2]["fields"]["In"]["fields"]["ValueIndex"] = 3,
                p => p.Objects[2]["fields"]["Plugin"]["fields"]["VoiceIndex"] = 1,
                p => p.Objects[1]["fields"]["Info"]["fields"]["PluginCount"] = 2,
                p => p.Objects[1]["fields"]["Info"]["fields"]["Voices"][0]["fields"]["Plugins"][0]["fields"]["ConnectionIndex"] = 1,
                p => p.Objects[0]["fields"]["OutputNodes"][0]["ref"] = 1
            }) {
                var package = Fixture(); change(package); package.Seal();
                Assert.Throws<InvalidDataException>(() => package.Validate());
            }
        }

        [Test] public void PackageChecksum_CoversProfileAndGraphData()
        {
            var package = Fixture(); package.Game = "swbf2";
            Assert.Throws<InvalidDataException>(() => package.Validate());
            package = Fixture(); package.Objects[1]["fields"]["ValueCount"] = 5;
            Assert.Throws<InvalidDataException>(() => package.Validate());
            package = Fixture(); package.Version = 2; package.Seal();
            Assert.Throws<InvalidDataException>(() => package.Validate());
        }
    }
}
