using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class MvdbCommandTests
    {
        [Test]
        public void VariationHash_Missing_DefaultsToZero()
        {
            uint value;
            Assert.IsTrue(MvdbCommand.TryParseVariationHash(null, out value));
            Assert.AreEqual(0u, value);
        }

        [Test]
        public void VariationHash_Decimal_ParsesUInt32()
        {
            uint value;
            Assert.IsTrue(MvdbCommand.TryParseVariationHash("4294967295", out value));
            Assert.AreEqual(uint.MaxValue, value);
        }

        [Test]
        public void VariationHash_Hex_ParsesUInt32()
        {
            uint value;
            Assert.IsTrue(MvdbCommand.TryParseVariationHash("0xF17EAD01", out value));
            Assert.AreEqual(0xF17EAD01u, value);
        }

        [Test]
        public void VariationHash_InvalidOrOutOfRange_IsRejected()
        {
            uint value;
            Assert.IsFalse(MvdbCommand.TryParseVariationHash("-1", out value));
            Assert.IsFalse(MvdbCommand.TryParseVariationHash("4294967296", out value));
            Assert.IsFalse(MvdbCommand.TryParseVariationHash("0x100000000", out value));
            Assert.IsFalse(MvdbCommand.TryParseVariationHash("0x", out value));
            Assert.IsFalse(MvdbCommand.TryParseVariationHash("not-a-hash", out value));
        }

        [Test]
        public void Copy_RejectsMissingArguments()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(false);
            Assert.AreEqual(1, MvdbCommand.ExecuteCopy(new string[0], logger, fmt));
            Assert.AreEqual(1, MvdbCommand.ExecuteCopy(new[] { "db" }, logger, fmt));
            Assert.AreEqual(1, MvdbCommand.ExecuteCopy(new[] { "db", "--mesh", "m" }, logger, fmt));
        }
    }
}
