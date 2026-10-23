using System.Linq;
using System.Text;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests
{
    // `kyber declare`: the JSON Kyber Level Declarator writes (LevelDeclaration.cs), kept as its chunk in the project and
    // shipped as the embedded resource KyberLevelDeclarationData.
    public class KyberDeclarationTests
    {
        [Test]
        public void TheChunkAndResourceNamesAreThePlugins()
        {
            Assert.AreEqual("bebebebe-bebe-bebe-bebe-bebebebebebe", KyberDeclaration.ChunkId.ToString());
            Assert.AreEqual("KyberLevelDeclarationData", KyberDeclaration.EmbeddedName);
        }

        [Test]
        public void TheUninitialisedMarkerReadsAsAnEmptyDeclaration()
        {
            JObject d = KyberDeclaration.Parse(Encoding.UTF8.GetBytes("U"));
            Assert.AreEqual(1, (int)d["version"]);
            Assert.IsTrue(KyberDeclaration.IsEmpty(d));
            Assert.IsTrue(KyberDeclaration.IsEmpty(KyberDeclaration.Parse((byte[])null)));
        }

        [Test]
        public void DeclaringAMapWritesThePluginsFields()
        {
            JObject d = KyberDeclaration.Empty();
            KyberDeclaration.UpsertMap(d, "Dune Arena", "AddedContent/MapPlayable/Levels/MP2/MP2", new[] { "SkirmishBlast", " Blast", "Blast" }, "AAAA");
            var m = (JObject)((JArray)d["maps"]).Single();
            Assert.AreEqual("Dune Arena", (string)m["name"]);
            Assert.AreEqual("AddedContent/MapPlayable/Levels/MP2/MP2", (string)m["id"]);
            Assert.AreEqual("AAAA", (string)m["image"]);
            Assert.AreEqual("SkirmishBlast,Blast", string.Join(",", ((JArray)m["supportedModes"]).Select(x => (string)x)));
            string json = Encoding.UTF8.GetString(KyberDeclaration.ToBytes(d));
            Assert.IsTrue(json.StartsWith("{\"version\":1,\"maps\":[{\"name\":\"Dune Arena\""), json);
            Assert.IsFalse(KyberDeclaration.IsEmpty(d));
        }

        [Test]
        public void RedeclaringAMapReplacesItAndKeepsItsImage()
        {
            JObject d = KyberDeclaration.Empty();
            KyberDeclaration.UpsertMap(d, "A", "Levels/X/X", new[] { "Blast" }, "IMG");
            KyberDeclaration.UpsertMap(d, "B", "levels/x/x", new[] { "HeroesVersusVillains" }, null);
            var maps = (JArray)d["maps"];
            Assert.AreEqual(1, maps.Count);
            Assert.AreEqual("B", (string)maps[0]["name"]);
            Assert.AreEqual("IMG", (string)maps[0]["image"]);
            Assert.IsTrue(KyberDeclaration.RemoveMap(d, "LEVELS/X/X"));
            Assert.IsTrue(KyberDeclaration.IsEmpty(d));
        }

        [Test]
        public void ExistingModesAndMappingsSurviveARoundTrip()
        {
            string json = "{\"version\":1,\"maps\":[],\"modes\":[{\"name\":\"Test\",\"id\":\"Mode10\",\"maxPlayers\":40,\"image\":\"\"}]," +
                          "\"modeMappings\":{\"Added_HeroesVersusVillains\":\"HeroesVersusVillains\"},\"modeNameOverrides\":{\"Mode1\":\"CONQUEST\"}}";
            JObject d = KyberDeclaration.Parse(Encoding.UTF8.GetBytes(json));
            KyberDeclaration.UpsertMap(d, "M", "Levels/M/M", new[] { "Mode10" }, null);
            JObject back = KyberDeclaration.Parse(KyberDeclaration.ToBytes(d));
            Assert.AreEqual("Mode10", (string)back["modes"][0]["id"]);
            Assert.AreEqual("HeroesVersusVillains", (string)back["modeMappings"]["Added_HeroesVersusVillains"]);
            Assert.AreEqual("CONQUEST", (string)back["modeNameOverrides"]["Mode1"]);
            Assert.AreEqual(1, ((JArray)back["maps"]).Count);
        }

        [Test]
        public void CustomArcadePickersMapToTheirModes()
        {
            // UI/Metadata/OptionsEnumUIMetaData, shipped TelemetryIds
            Assert.AreEqual("SkirmishBlast", FrostyLevelTools.LevelDuplicator.PickerMode("BlastLevelReferencesToPlay"));
            Assert.AreEqual("SkirmishOnslaught", FrostyLevelTools.LevelDuplicator.PickerMode("OnSlaughtReferencesLevelToPlay"));
            Assert.AreEqual("SkirmishDuel", FrostyLevelTools.LevelDuplicator.PickerMode("DuelReferencesLevelToPlay"));
            Assert.AreEqual("Mode8", FrostyLevelTools.LevelDuplicator.PickerMode("InstantActionLevelReference"));
            Assert.AreEqual("ModeE", FrostyLevelTools.LevelDuplicator.PickerMode("ModeELevelRef"));
            Assert.AreEqual("ModeF", FrostyLevelTools.LevelDuplicator.PickerMode("ModeFLevelRef"));
            Assert.IsNull(FrostyLevelTools.LevelDuplicator.PickerMode("SomethingElse"));
        }

        [Test]
        public void AMapNeedsTheLevelItLaunches()
        {
            Assert.Throws<System.ArgumentException>(() => KyberDeclaration.UpsertMap(KyberDeclaration.Empty(), "x", " ", new[] { "Blast" }, null));
        }
    }
}
