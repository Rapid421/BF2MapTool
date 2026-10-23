using System;
using FrostyLevelTools;

namespace FrostyCLI.Tests
{
    // `level create --bots-team1/2` adds an AutoPlayerManagerEntityData and a self-starting DelayEntityData that fires its
    // ApplySettings; both carry Flags the way the shipped entities do.
    public class AutoPlayerTests
    {
        [Test]
        public void AnEventTargetCarriesTheGuidLowBitsAndTheTargetBit()
        {
            // shipped auto-player managers (GameModes_Flow): FOSD 78d5c95b, Tatooine_02 1c9b260a, Jakku_02 3ede46be
            Assert.AreEqual(0x04d5c95bu, LevelCreator.EntityFlags(new Guid("78d5c95b-4fbb-4693-9e1c-a0bc188daab4"), true));
            Assert.AreEqual(0x049b260au, LevelCreator.EntityFlags(new Guid("1c9b260a-2b77-48a8-8611-4e0b99ffa064"), true));
            Assert.AreEqual(0x04de46beu, LevelCreator.EntityFlags(new Guid("3ede46be-0947-4fe7-8dda-23fd1ae370af"), true));
        }

        [Test]
        public void ASourceOnlyCarriesTheGuidLowBits()
        {
            // Tatooine_02 GameModes_Flow InclusionSettingEntityData 8e5d0c02 (a property source only): Flags 0x005d0c02
            Assert.AreEqual(0x005d0c02u, LevelCreator.EntityFlags(new Guid("8e5d0c02-5abd-4e5c-a19b-aa193caf5d09"), false));
            Assert.AreEqual(0u, LevelCreator.EntityFlags(Guid.Empty, false));
        }
    }
}
