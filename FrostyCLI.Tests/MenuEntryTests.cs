using System.Collections.Generic;
using FrostyLevelTools;

namespace FrostyCLI.Tests
{
    // `level create --menu-name`: the Custom Arcade settings screen finds the picked level's planet and factions by Key
    // (UI/Frontend/Screens/Play/Skirmish/SkirmishSettingsScreen: PlanetLevelAssetLookupEntityData, 23 rows), so the new
    // level gets its host's row under its own key.
    public class MenuEntryTests
    {
        public class Ref { public int Id; }
        public class LevelInfoLookupData
        {
            public Ref PlanetIdData { get; set; }
            public Ref FactionIdCollection { get; set; }
            public string LevelKey { get; set; }
        }
        public class PlanetLevelAssetLookupEntityData
        {
            public List<LevelInfoLookupData> LevelLookupData { get; set; } = new List<LevelInfoLookupData>();
        }

        private static PlanetLevelAssetLookupEntityData Table()
        {
            var t = new PlanetLevelAssetLookupEntityData();
            t.LevelLookupData.Add(new LevelInfoLookupData { LevelKey = "Takodana_01", PlanetIdData = new Ref { Id = 1 }, FactionIdCollection = new Ref { Id = 2 } });
            t.LevelLookupData.Add(new LevelInfoLookupData { LevelKey = "Tatooine_01", PlanetIdData = new Ref { Id = 3 }, FactionIdCollection = new Ref { Id = 4 } });
            return t;
        }

        [Test]
        public void TheNewKeyGetsTheHostsPlanetAndFactions()
        {
            var t = Table();
            Assert.AreEqual(1, LevelDuplicator.AddLookupRow(t, "tatooine_01", "MP2"));
            Assert.AreEqual(3, t.LevelLookupData.Count);
            var row = t.LevelLookupData[2];
            Assert.AreEqual("MP2", row.LevelKey);
            Assert.IsTrue(ReferenceEquals(t.LevelLookupData[1].PlanetIdData, row.PlanetIdData));
            Assert.IsTrue(ReferenceEquals(t.LevelLookupData[1].FactionIdCollection, row.FactionIdCollection));
            Assert.AreEqual("Tatooine_01", t.LevelLookupData[1].LevelKey);
        }

        [Test]
        public void NoRowForAnUnknownHostOrAKeyAlreadyThere()
        {
            var t = Table();
            Assert.AreEqual(0, LevelDuplicator.AddLookupRow(t, "Hoth_01", "MP2"));
            Assert.AreEqual(1, LevelDuplicator.AddLookupRow(t, "Tatooine_01", "MP2"));
            Assert.AreEqual(0, LevelDuplicator.AddLookupRow(t, "Tatooine_01", "mp2"));
            Assert.AreEqual(3, t.LevelLookupData.Count);
        }
    }
}
