using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // A client-realm property connection is only bound by the engine when its TARGET object carries
    // DataContainer.Flags bit 27 (0x08000000). `connection add` sets it; these pin that behaviour.
    public class ConnectionTargetFlagTests
    {
        public class FakeData { public uint Flags { get; set; } }
        public class FakeSignedData { public int Flags { get; set; } }
        public class NoFlags { public string Name { get; set; } }

        [Test]
        public void NeedsBit_ClientAndClientAndServerPropertyConnections()
        {
            Assert.IsTrue(ConnectionCommand.NeedsClientTargetBit("PropertyConnection", 0x1));
            Assert.IsTrue(ConnectionCommand.NeedsClientTargetBit("PropertyConnection", 0x2));
            Assert.IsTrue(ConnectionCommand.NeedsClientTargetBit("PropertyConnection", 0x22));
        }

        [Test]
        public void NoBit_ServerPropertyConnectionsAndOtherKinds()
        {
            Assert.IsFalse(ConnectionCommand.NeedsClientTargetBit("PropertyConnection", 0x3));
            Assert.IsFalse(ConnectionCommand.NeedsClientTargetBit("LinkConnection", 0x2));
            Assert.IsFalse(ConnectionCommand.NeedsClientTargetBit("EventConnection", 0x2));
        }

        [Test]
        public void SetsBit_KeepsLowBits_ReportsPrevious()
        {
            var d = new FakeData { Flags = 0x00f99723u };   // vanilla E-Web camera
            uint before;
            Assert.IsTrue(ConnectionCommand.TrySetClientTargetBit(d, out before));
            Assert.AreEqual(0x00f99723u, before);
            Assert.AreEqual(0x08f99723u, d.Flags);
        }

        [Test]
        public void SetsBit_OnSignedFlags()
        {
            var d = new FakeSignedData { Flags = 0x01deb7fb };
            uint before;
            Assert.IsTrue(ConnectionCommand.TrySetClientTargetBit(d, out before));
            Assert.AreEqual(0x09deb7fb, d.Flags);
        }

        [Test]
        public void AlreadySet_IsUnchanged()
        {
            var d = new FakeData { Flags = 0x089039cbu };   // vanilla 74-Z camera
            uint before;
            Assert.IsTrue(ConnectionCommand.TrySetClientTargetBit(d, out before));
            Assert.AreEqual(0x089039cbu, d.Flags);
        }

        [Test]
        public void NoFlagsProperty_ReturnsFalse()
        {
            uint before;
            Assert.IsFalse(ConnectionCommand.TrySetClientTargetBit(new NoFlags(), out before));
        }
    }
}
