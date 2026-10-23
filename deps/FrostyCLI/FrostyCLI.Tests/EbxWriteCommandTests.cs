using System;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class EbxWriteCommandTests
    {
        private static ExportLogger Logger() { return new ExportLogger(quiet: true); }
        private static OutputFormatter Fmt() { return new OutputFormatter(json: false); }

        [Test]
        public void EbxSet_MissingPath_Returns1()
        {
            int r = EbxWriteCommand.ExecuteSet(
                new[] { "--prop", "Name", "--value", "foo" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void EbxSet_MissingProp_Returns1()
        {
            int r = EbxWriteCommand.ExecuteSet(
                new[] { "xp2/foo", "--value", "foo" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void EbxSet_MissingValue_Returns1()
        {
            int r = EbxWriteCommand.ExecuteSet(
                new[] { "xp2/foo", "--prop", "Name" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void EbxClone_MissingPath_Returns1()
        {
            int r = EbxWriteCommand.ExecuteClone(
                new[] { "--as", "xp2/dest" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void EbxClone_MissingAs_Returns1()
        {
            int r = EbxWriteCommand.ExecuteClone(
                new[] { "xp2/source" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void EbxGet_UnknownSubcommand_Returns1()
        {
            var cmd = new EbxGetCommand();
            int r = cmd.Execute(new[] { "move", "xp2/foo" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        [Test]
        public void EbxSet_NoProjectLoaded_Returns1()
        {
            if (Session.Project.IsLoaded)
            {
                Console.WriteLine("  SKIP (session already has project loaded)");
                return;
            }
            int r = EbxWriteCommand.ExecuteSet(
                new[] { "xp2/foo", "--prop", "Name", "--value", "x" }, Logger(), Fmt());
            Assert.AreEqual(1, r);
        }

        // The save round-trip check may only fail a write when the object it read back is certainly the written one.
        [Test]
        public void UncertainCounterpart_CertainOnlyForAnIdentifiedObjectInAFaithfulTrip()
        {
            object root = new object(), sibling = new object();
            var objects = new[] { root, sibling };
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(objects, 2, root, root, true) == null);
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(objects, 2, root, new object(), true) == null);   // a struct inside it
        }

        [Test]
        public void UncertainCounterpart_FoundByPosition_IsUncertain()
        {
            object root = new object(), written = new object();
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(new[] { root, written }, 2, written, written, false) != null);
        }

        [Test]
        public void UncertainCounterpart_TripLostObjects_IsUncertain()
        {
            object root = new object(), other = new object();
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(new[] { root, other, null }, 1, root, root, true) != null);
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(new[] { root, other, null }, 2, root, root, true) == null);   // nulls don't count
        }

        // PF_Ability_OfficerTurret: SimpleIntStateData made by set-ref new: earlier in the process, and the third
        // State.Key read back as the second's after the in-memory trip while the saved project kept all three.
        [Test]
        public void UncertainCounterpart_PartitionHoldsInProcessObjects_IsUncertain()
        {
            object root = new object(), created = new object();
            var objects = new[] { root, created };
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(objects, 2, root, root, true) == null);
            InProcessObjects.Mark(created);
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(objects, 2, root, root, true) != null);
        }

        [Test]
        public void UncertainCounterpart_MemberOnAReferencedObject_IsUncertain()
        {
            object root = new object(), state = new object();
            Assert.IsTrue(EbxWriteCommand.UncertainCounterpart(new[] { root, state }, 2, root, state, true) != null);
        }

        [Test]
        public void InProcessObjects_MarksByIdentity()
        {
            object a = new object(), b = new object();
            Assert.IsFalse(InProcessObjects.IsCreated(a));
            InProcessObjects.Mark(a);
            InProcessObjects.Mark(a);   // idempotent
            Assert.IsTrue(InProcessObjects.IsCreated(a));
            Assert.IsFalse(InProcessObjects.IsCreated(b));
            Assert.IsFalse(InProcessObjects.IsCreated(null));
            Assert.IsTrue(InProcessObjects.AnyCreated(new[] { b, a }));
            Assert.IsFalse(InProcessObjects.AnyCreated(new[] { b, null }));
            Assert.IsFalse(InProcessObjects.AnyCreated(null));
        }
    }
}
