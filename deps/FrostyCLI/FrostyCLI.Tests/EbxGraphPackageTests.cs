using System;
using System.IO;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests
{
    public class EbxGraphPackageTests
    {
        static JObject Record(string type, JObject fields, bool exported = false) => new JObject { ["type"] = type, ["exported"] = exported, ["fields"] = fields };
        static JObject Ref(int id) => new JObject { ["ref"] = id };

        static EbxGraphPackage Fixture()
        {
            var p = new EbxGraphPackage { Game = "swbf2", Asset = "Tests/Emitter", FileGuid = Guid.NewGuid().ToString(), RootType = "ScalableEmitterDocument" };
            p.Objects.Add(Record("ScalableEmitterDocument", new JObject { ["TemplateDataHigh"] = Ref(1), ["Name"] = "x" }, true));
            p.Objects.Add(Record("EmitterTemplateData", new JObject { ["RootProcessor"] = Ref(2), ["MaxCount"] = 5 }));
            p.Objects.Add(Record("UpdateAgeData", new JObject { ["NextProcessor"] = new JObject { ["null"] = true }, ["Lifetime"] = 2.5,
                ["Tex"] = new JObject { ["external"] = new JObject { ["file_guid"] = Guid.NewGuid().ToString(), ["class_guid"] = Guid.NewGuid().ToString(), ["asset"] = "FX/T", ["type"] = "AtlasTextureAsset" } } }));
            p.Seal();
            return p;
        }

        [Test] public void Package_RoundTripsThroughJsonAndValidates()
        {
            var p = Fixture(); p.Validate();
            var r = JsonConvert.DeserializeObject<EbxGraphPackage>(JsonConvert.SerializeObject(p));
            r.Validate();
            Assert.IsTrue(JToken.DeepEquals(p.Objects, r.Objects));
        }

        [Test] public void Package_RejectsTamperingDanglingRefsAndBadRoot()
        {
            foreach (Action<EbxGraphPackage> change in new Action<EbxGraphPackage>[] {
                g => g.Objects[1]["fields"]["RootProcessor"]["ref"] = 9,
                g => g.RootType = "EffectBlueprint",
                g => g.Game = "swbf3",
                g => g.Objects[2]["fields"]["Tex"]["external"]["file_guid"] = "not-a-guid",
            }) {
                var p = Fixture(); change(p); p.Seal();
                Assert.Throws<InvalidDataException>(() => p.Validate());
            }
            var q = Fixture(); q.Objects[1]["fields"]["MaxCount"] = 6;   // content change without re-sealing
            Assert.Throws<InvalidDataException>(() => q.Validate());
        }

        [Test] public void UnsealedPackage_IsSealedOnlyWhenAllowed()
        {
            var p = Fixture(); p.Sha256 = null;
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, JsonConvert.SerializeObject(p));
                Assert.Throws<InvalidDataException>(() => EbxGraphPackage.Load(file));
                var loaded = EbxGraphPackage.Load(file, allowUnsealed: true);
                Assert.IsTrue(!string.IsNullOrEmpty(loaded.Sha256));
            }
            finally { File.Delete(file); }
        }

        [Test] public void DeterministicGuid_IsStableCaseInsensitiveAndVersion3()
        {
            Guid a = EbxGraphBuilder.DeterministicGuid("S2/FX/Hoth2015/em_a", "object:3");
            Guid b = EbxGraphBuilder.DeterministicGuid("s2/fx/hoth2015/EM_A", "object:3");
            Guid c = EbxGraphBuilder.DeterministicGuid("S2/FX/Hoth2015/em_a", "object:4");
            Assert.AreEqual(a, b);
            Assert.IsTrue(a != c);
            byte[] bytes = a.ToByteArray();
            Assert.AreEqual(0x30, bytes[7] & 0xF0);
            Assert.AreEqual(0x80, bytes[8] & 0xC0);
        }

        [Test] public void IdentityFlags_KeepsFlagByteTakesGuidLowBits()
        {
            var g = new Guid("12abcdef-0000-0000-0000-000000000000");
            Assert.AreEqual(0x07abcdefu, EbxGraphBuilder.IdentityFlags(0x07000000u | 0x00112233u, g));
            Assert.AreEqual(0x00abcdefu, EbxGraphBuilder.IdentityFlags(0u, g));
        }

        [Test] public void Diff_ReportsFieldTypeAndCountDifferences()
        {
            var a = Fixture().Objects; var b = (JArray)a.DeepClone();
            Assert.AreEqual(0, EbxGraphBuilder.Diff(a, b, 10).Count);
            b[1]["fields"]["MaxCount"] = 7;
            Assert.AreEqual(1, EbxGraphBuilder.Diff(a, b, 10).Count);
            b = (JArray)a.DeepClone(); b[2]["type"] = "SpawnRateData";
            Assert.AreEqual(1, EbxGraphBuilder.Diff(a, b, 10).Count);
            b = (JArray)a.DeepClone(); b.RemoveAt(2);
            Assert.AreEqual(1, EbxGraphBuilder.Diff(a, b, 10).Count);
        }
    }
}
