using System;
using System.Collections;
using System.Collections.Generic;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // Which object inside a target asset a reference should address.
    //
    // `set-ref --target <asset>` used the asset's ROOT, which is right almost always and silently
    // wrong when the member declares what it points at and the root is not that thing. Shipped
    // SubWorldInclusionSetting.Criterion points at the WorldPartInclusionCriterion INSIDE
    // Gameplay/GameModes/GameModeInclusionSettings; that asset's root is a WorldPartInclusion
    // container holding it. Writing the root there produces a reference that loads and addresses a
    // container instead of a criterion -- no error, no gate.
    public class RefTargetPickerTests
    {
        [Test]
        public void DeclaredTargetType_FollowsMetadataAcrossUncreatedPath()
        {
            var root = new PathAutoCreateTests.Root();
            Assert.AreEqual(typeof(PathAutoCreateTests.Setting), EbxRefWriter.DeclaredTargetType(root, "Inclusion.Items[0]"));
            Assert.AreEqual(PathAutoCreateTests.PointerRefType.Null, root.Inclusion.Type);
            Assert.IsNull(EbxRefWriter.DeclaredTargetType(root, "Unknown"));
        }

        public class Criterion { }                       // the declared base
        public class ModeCriterion : Criterion { }       // what shipped data actually points at
        public class Container { }                       // an asset root that is not a criterion

        // The picker works on the objects themselves, so the fakes ARE the objects.
        private sealed class FakeCriterion : Criterion
        {
            private readonly Guid _guid;
            public FakeCriterion(Guid g) { _guid = g; }
            public Guid GetInstanceGuid() { return _guid; }
        }

        private sealed class FakeContainer : Container
        {
            private readonly Guid _guid;
            public FakeContainer(Guid g) { _guid = g; }
            public Guid GetInstanceGuid() { return _guid; }
        }

        private static readonly Guid RootGuid = new Guid("97802864-4303-4c4a-b056-5be74f5fe430");
        private static readonly Guid CritGuid = new Guid("bc56bbb8-db5f-4786-8470-dada94bf9d8e");
        private static readonly Guid Crit2Guid = new Guid("11111111-2222-3333-4444-555555555555");

        // Almost always: the root IS the thing being referenced, and nothing changes.
        [Test]
        public void TryPick_RootIsTheDeclaredType_UsesTheRoot()
        {
            var root = new FakeCriterion(RootGuid);
            Guid picked; string error, note;
            bool ok = RefTargetPicker.TryPick(typeof(Criterion), root, new object[] { root },
                                              out picked, out error, out note);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(RootGuid, picked);
            Assert.IsNull(note);
        }

        // With no declared type there is nothing to check against, so the root stands.
        [Test]
        public void TryPick_NoDeclaredType_UsesTheRoot()
        {
            var root = new FakeContainer(RootGuid);
            Guid picked; string error, note;
            bool ok = RefTargetPicker.TryPick(null, root, new object[] { root }, out picked, out error, out note);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(RootGuid, picked);
        }

        // The shipped criterion case: the root is a container, and exactly one object in the asset is
        // the declared type. Point at that one, and say so.
        [Test]
        public void TryPick_RootIsAContainer_PicksTheSoleMatchingObject()
        {
            var root = new FakeContainer(RootGuid);
            var crit = new FakeCriterion(CritGuid);
            Guid picked; string error, note;
            bool ok = RefTargetPicker.TryPick(typeof(Criterion), root, new object[] { root, crit },
                                              out picked, out error, out note);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(CritGuid, picked);
            Assert.IsTrue(note != null && note.IndexOf("FakeCriterion", StringComparison.Ordinal) >= 0,
                "picking a non-root target should be reported; got: " + note);
        }

        // A subclass of the declared type is what shipped data carries -- WorldPartInclusionCriterion
        // where the member declares SubWorldInclusionCriterion -- so matching must be by assignability,
        // not by name.
        [Test]
        public void TryPick_SubclassOfTheDeclaredType_Matches()
        {
            var root = new FakeContainer(RootGuid);
            var crit = new DerivedCriterion(CritGuid);
            Guid picked; string error, note;
            bool ok = RefTargetPicker.TryPick(typeof(Criterion), root, new object[] { root, crit },
                                              out picked, out error, out note);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(CritGuid, picked);
        }

        private sealed class DerivedCriterion : ModeCriterion
        {
            private readonly Guid _guid;
            public DerivedCriterion(Guid g) { _guid = g; }
            public Guid GetInstanceGuid() { return _guid; }
        }

        // Several candidates and no way to choose: picking one would be a coin flip that writes a
        // reference to the wrong criterion and never errors.
        [Test]
        public void TryPick_SeveralCandidates_Refuses()
        {
            var root = new FakeContainer(RootGuid);
            Guid picked; string error, note;
            bool ok = RefTargetPicker.TryPick(typeof(Criterion), root,
                                              new object[] { root, new FakeCriterion(CritGuid), new FakeCriterion(Crit2Guid) },
                                              out picked, out error, out note);
            Assert.IsFalse(ok, "an ambiguous target must not be guessed");
            Assert.IsTrue(error != null && error.IndexOf("--class-guid", StringComparison.Ordinal) >= 0,
                "the error should point at --class-guid; got: " + error);
            Assert.IsTrue(error.IndexOf(CritGuid.ToString(), StringComparison.OrdinalIgnoreCase) >= 0,
                "the error should list the candidates; got: " + error);
        }

        // Nothing in the asset is the declared type. The write is very likely wrong -- but `set-ref`
        // has always accepted it, and there is no better object to offer, so this warns and leaves
        // the root rather than turning a working command into a failing one. The rule is "refuse or
        // warn"; refusing is reserved for the case where any choice would be a guess.
        [Test]
        public void TryPick_NoCandidate_WarnsAndKeepsTheRoot()
        {
            var root = new FakeContainer(RootGuid);
            Guid picked; string error, note;
            bool ok = RefTargetPicker.TryPick(typeof(Criterion), root, new object[] { root },
                                              out picked, out error, out note);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(RootGuid, picked);
            Assert.IsTrue(note != null && note.IndexOf("Criterion", StringComparison.Ordinal) >= 0,
                "the warning should name the type expected; got: " + note);
        }

        // A non-exported candidate cannot be addressed externally, so it is no better than the root.
        [Test]
        public void TryPick_OnlyCandidateIsNotExported_WarnsAndKeepsTheRoot()
        {
            var root = new FakeContainer(RootGuid);
            Guid picked; string error, note;
            bool ok = RefTargetPicker.TryPick(typeof(Criterion), root,
                                              new object[] { root, new FakeCriterion(Guid.Empty) },
                                              out picked, out error, out note);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(RootGuid, picked);
            Assert.IsNotNull(note);
        }
    }
}
