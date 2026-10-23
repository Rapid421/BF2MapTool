using System.Collections.Generic;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class ModApplyOnlyTests
    {
        [Test]
        public void OnlyMatches_NoFilter_MatchesEverything()
        {
            Assert.IsTrue(ModBuildCommand.OnlyMatches(null, "anything/at/all"));
        }

        [Test]
        public void OnlyMatches_ExactName_IsCaseInsensitive()
        {
            var only = new List<string> { "gameplay/kits/specials/flametrooper/gp_special_flametrooper" };
            Assert.IsTrue(ModBuildCommand.OnlyMatches(only, "Gameplay/Kits/Specials/Flametrooper/GP_Special_Flametrooper"));
            Assert.IsFalse(ModBuildCommand.OnlyMatches(only, "Gameplay/Kits/Specials/Flametrooper/GP_Special_Flametrooper_AI"));
        }

        [Test]
        public void OnlyMatches_StarIsAPrefix()
        {
            var only = new List<string> { "a0_bgr_reinforcements/a0_enhanced_enforcers/flametrooper/stickyfire/*" };
            Assert.IsTrue(ModBuildCommand.OnlyMatches(only, "A0_BGR_Reinforcements/A0_Enhanced_Enforcers/Flametrooper/StickyFire/W_Flametrooper_StickyFire"));
            Assert.IsFalse(ModBuildCommand.OnlyMatches(only, "a0_bgr_reinforcements/a0_enhanced_enforcers/flametrooper/regen/pf_flametrooper_regen"));
            Assert.IsFalse(ModBuildCommand.OnlyMatches(only, "gameplay/bundles/sharedbundles/common/weapons/sharedbundleweapons_common_networkregistry_win32"));
        }
    }
}
