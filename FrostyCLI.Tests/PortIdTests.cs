using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class PortIdTests
    {
        [Test]
        public void UnsignedDecimal_ReadsAsItself()
        {
            uint id;
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("193453899", out id), "should parse");
            Assert.AreEqual(0x0B87DF4Bu, id);
        }

        [Test]
        public void HexLiteral_ReadsAsItself()
        {
            uint id;
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("0xd27bc17e", out id), "should parse");
            Assert.AreEqual(0xD27BC17Eu, id);
        }

        // Connection records store port ids SIGNED, so a graph dump hands back negatives. These used
        // to fall through to being hashed as text, which produced a wrong-but-plausible id and an
        // unresolvable name (StreamIn read as 0x5564917E instead of 0xD27BC17E).
        [Test]
        public void NegativeDecimal_ReinterpretedAsUnsigned_NotHashed()
        {
            uint id;
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("-763641474", out id), "should parse");
            Assert.AreEqual(0xD27BC17Eu, id);
        }

        [Test]
        public void NegativeDecimal_MatchesHashOfTheName()
        {
            uint fromId, fromName;
            ConnectionCommand.TryResolveFieldId("-763641474", out fromId);
            ConnectionCommand.TryResolveFieldId("StreamIn", out fromName);
            Assert.AreEqual(fromName, fromId);
        }

        [Test]
        public void PlainName_StillHashes()
        {
            uint id;
            Assert.IsTrue(ConnectionCommand.TryResolveFieldId("Out", out id), "should parse");
            Assert.AreEqual(0x0B87DF4Bu, id);
        }
    }
}
