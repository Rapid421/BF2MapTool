using System.Collections.Generic;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // `level loading-screen`: UI/Configuration/WSUILoadingScreenSettings.MPLoadLevelScreenOverrides is keyed by level path;
    // a new level's entry copies the host's (mode, widget, timeout, versions) with its own name, screen and videos.
    public class LoadingScreenTests
    {
        public class Override
        {
            public List<int> IncludedInVersions { get; set; } = new List<int>();
            public string GameModeName { get; set; } = "";
            public string LoadingScreenAssetName { get; set; } = "";
            public string WidgetDataName { get; set; } = "";
            public float DisplayTimeout { get; set; }
            public string IntroMovieName { get; set; } = "";
            public string MovieName { get; set; } = "";
            public string LevelName { get; set; } = "";
            public string DetachedSubworldName { get; set; } = "";
        }

        private static Override Tatooine() => new Override
        {
            LoadingScreenAssetName = "UI/LoadingScreens/MPLoadLevel/MPLoadLevelTatooineAsset", DisplayTimeout = 60,
            IntroMovieName = "UI/Video/Loading/Planet_Tatooine_01", MovieName = "UI/Video/Loading/Planet_Tatooine_01",
            LevelName = "Levels/MP/Tatooine_01/Tatooine_01", IncludedInVersions = { 3 },
        };

        [Test]
        public void ANewLevelGetsACopyOfTheHostsEntry()
        {
            var host = Tatooine();
            var list = new List<Override> { host };
            bool added = LevelLoadingScreen.SetOverride(list, host, "AddedContent/M/Levels/MP2/MP2",
                "UI/LoadingScreens/MPLoadLevel/MPLoadLevelMP2Asset", "UI/Video/Loading/MP2", "UI/Video/Loading/MP2");
            Assert.IsTrue(added);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("AddedContent/M/Levels/MP2/MP2", list[1].LevelName);
            Assert.AreEqual("UI/Video/Loading/MP2", list[1].MovieName);
            Assert.AreEqual(60f, list[1].DisplayTimeout);
            Assert.AreEqual("Levels/MP/Tatooine_01/Tatooine_01", host.LevelName);         // the host's entry is untouched
            list[1].IncludedInVersions.Add(9);
            Assert.AreEqual(1, host.IncludedInVersions.Count);                          // lists are not shared
        }

        [Test]
        public void ASecondRunUpdatesTheLevelsEntry()
        {
            var host = Tatooine();
            var list = new List<Override> { host };
            LevelLoadingScreen.SetOverride(list, host, "A/L/L", host.LoadingScreenAssetName, host.IntroMovieName, host.MovieName);
            bool added = LevelLoadingScreen.SetOverride(list, host, "a/l/l/", "S", "I", "M");
            Assert.IsFalse(added);
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("S", list[1].LoadingScreenAssetName);
        }
    }
}
