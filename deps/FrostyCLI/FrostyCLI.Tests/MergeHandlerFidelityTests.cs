using System.Collections.Generic;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // mod build: Swbf2MergerPlugin's MetaDataActionHandler ships only entries appended to a UIMetaDataAsset's root Assets
    // list. `level create`'s Custom Arcade entry edits objects inside UI/Metadata/OptionsEnumUIMetaData, which the handler
    // shipped as an empty delta (every Hoth 2015 build since T11: the level missing from the menus). Such an asset must go
    // out as a plain EBX; an append-only edit may keep the handler.
    public class MergeHandlerFidelityTests
    {
        [Test]
        public void TheHandlerShipsOnlyAppendedEntries()
        {
            var shipped = new List<object> { "A", "B" };
            var edited = new List<object> { "A", "B", null, "C", "A" };
            List<object> added = MergeHandlerFidelity.ShippedAdditions(shipped, edited);
            Assert.AreEqual(1, added.Count);
            Assert.AreEqual("C", added[0]);
        }

        [Test]
        public void AnEditInsideAnObjectShipsNothingThroughTheHandler()
        {
            // the OptionsEnumUIMetaData case: the picker lists changed, the root Assets list did not
            var assets = new List<object> { "pickers" };
            Assert.AreEqual(0, MergeHandlerFidelity.ShippedAdditions(assets, new List<object> { "pickers" }).Count);
            Assert.IsFalse(MergeHandlerFidelity.Reproduces(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }));
        }

        [Test]
        public void OnlyAnExactReplayKeepsTheHandler()
        {
            Assert.IsTrue(MergeHandlerFidelity.Reproduces(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));
            Assert.IsFalse(MergeHandlerFidelity.Reproduces(new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }));
            Assert.IsFalse(MergeHandlerFidelity.Reproduces(null, new byte[] { 1 }));
        }
    }
}
