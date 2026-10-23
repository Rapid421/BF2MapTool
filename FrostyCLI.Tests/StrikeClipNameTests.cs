using FrostyCLI.Core.Animation;

namespace FrostyCLI.Tests
{
    // Locks StrikeClipName to FAAE's StateMachineParser naming so exported .seanim filenames
    // line up with FAAE's expected animation display names.
    public class StrikeClipNameTests
    {
        [Test]
        public void IsStrikeOrCloser_UnderscoreForms_True()
        {
            Assert.IsTrue(StrikeClipName.IsStrikeOrCloser("Vader_Strike_01"));
            Assert.IsTrue(StrikeClipName.IsStrikeOrCloser("Luke_Closer_02"));
        }

        [Test]
        public void IsStrikeOrCloser_StrikeDigitVariant_True()
        {
            Assert.IsTrue(StrikeClipName.IsStrikeOrCloser("Maul_Strike2"));
        }

        [Test]
        public void IsStrikeOrCloser_NonStrike_False()
        {
            Assert.IsFalse(StrikeClipName.IsStrikeOrCloser("Vader_Idle"));
            Assert.IsFalse(StrikeClipName.IsStrikeOrCloser("Some_StrikeForce_Thing")); // "_Strike" not followed by digit
            Assert.IsFalse(StrikeClipName.IsStrikeOrCloser(""));
        }

        [Test]
        public void BuildFullName_UnderscoreStrike_KeepsName()
        {
            Assert.AreEqual("Vader_Strike_01", StrikeClipName.BuildFullName("Vader_Strike_01"));
        }

        [Test]
        public void BuildFullName_DigitVariant_NormalizesSuffix()
        {
            Assert.AreEqual("Maul_Strike_2", StrikeClipName.BuildFullName("Maul_Strike2"));
        }

        [Test]
        public void BuildFullName_Closer_KeepsName()
        {
            Assert.AreEqual("Luke_Closer_02", StrikeClipName.BuildFullName("Luke_Closer_02"));
        }

        [Test]
        public void BuildFullName_NonStrike_Null()
        {
            Assert.IsNull(StrikeClipName.BuildFullName("Vader_Idle"));
        }
    }
}
