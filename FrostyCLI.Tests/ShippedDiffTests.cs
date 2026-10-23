using System;
using System.Collections.Generic;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // Comparing one object against the shipped population of its own type.
    //
    // Every real defect in the battle-royale work was found by hand-diffing an object against the
    // shipped instances of its type: BundleHeap.AllowGrow false where all nine shipped ones are
    // true; a Flags value copied from a duplication source instead of derived from the object's own
    // GUID; a BundleName inherited from whatever was cloned. None of those error, and all of them
    // are decidable from shipped data.
    //
    // These drive the comparison over plain objects, so they run without the game.
    public class ShippedDiffTests
    {
        public struct HeapInfo
        {
            public int HeapType { get; set; }
            public int InitialSize { get; set; }
            public bool AllowGrow { get; set; }
        }

        public class SubWorldRef
        {
            private Guid _guid;
            public SubWorldRef(Guid g) { _guid = g; }
            public Guid GetInstanceGuid() { return _guid; }

            public bool AutoLoad { get; set; }
            public string BundleName { get; set; }
            public uint Flags { get; set; }
            public HeapInfo BundleHeap { get; set; }
        }

        // Shipped values for Flags: 0x04000000 | (Data1 & 0x01FFFFFF) is Kashyyyk's local form of the
        // relation; what actually holds across the shipped population is the low 25 bits alone.
        private static SubWorldRef Shipped(string guid, string bundle, uint topBits)
        {
            var g = new Guid(guid);
            uint d1 = uint.Parse(guid.Substring(0, 8), System.Globalization.NumberStyles.HexNumber);
            return new SubWorldRef(g)
            {
                AutoLoad = false,
                BundleName = bundle,
                Flags = (topBits << 25) | (d1 & 0x01FFFFFFu),
                BundleHeap = new HeapInfo { HeapType = 3, InitialSize = 0, AllowGrow = true },
            };
        }

        private static List<object> NineShipped()
        {
            string[] guids =
            {
                "e2da4a10-94cb-4bab-9bd9-d319c872c5fa", "999e5d27-584f-42d8-924f-8832c850c9f2",
                "08f72774-46c8-4a49-adef-f07bd1de22a1", "d094b474-b00f-46f8-be63-6b4cc6db0277",
                "f99dbaa2-03bc-4eb8-b101-845285427988", "d6df09a5-9e1e-4457-a700-72e4fd4d062b",
                "71249df1-b5aa-467e-a60e-ae0b5f0ff118", "51967ff2-b3a8-4c0a-a13e-68c08c59e5ce",
                "c7b50cf6-2a35-4daf-873e-e88ff8510bbe",
            };
            var list = new List<object>();
            for (int i = 0; i < guids.Length; i++)
                list.Add(Shipped(guids[i], "Levels/MP/Kashyyyk_01/Bundle" + i, 2u));
            return list;
        }

        private static FieldFinding Find(List<FieldFinding> findings, string field)
        {
            foreach (FieldFinding f in findings) if (f.Field == field) return f;
            return null;
        }

        // The acceptance case from the prompt: AllowGrow false against nine shipped trues must be
        // named without being told what to look for, and must sort above everything else.
        [Test]
        public void Compare_UnanimousDisagreement_IsNamedAndRanksFirst()
        {
            var ours = Shipped("11111111-2222-3333-4444-555555555555", "Levels/MP/Kashyyyk_01/Mine", 2u);
            var heap = ours.BundleHeap; heap.AllowGrow = false; ours.BundleHeap = heap;

            List<FieldFinding> findings = ShippedDiff.Compare(ours, NineShipped(), 4);

            Assert.IsTrue(findings.Count > 0, "expected at least one finding");
            Assert.AreEqual("BundleHeap.AllowGrow", findings[0].Field);
            Assert.AreEqual("unanimous-disagreement", findings[0].Verdict);
            Assert.AreEqual(9, findings[0].ShippedCount);
        }

        // A field we agree with must not be reported as a problem, or the outliers drown.
        [Test]
        public void Compare_AgreeingField_IsNotAnOutlier()
        {
            var ours = Shipped("11111111-2222-3333-4444-555555555555", "Levels/MP/Kashyyyk_01/Mine", 2u);

            List<FieldFinding> findings = ShippedDiff.Compare(ours, NineShipped(), 4);

            FieldFinding autoLoad = Find(findings, "AutoLoad");
            Assert.IsNotNull(autoLoad);
            Assert.AreEqual("matches", autoLoad.Verdict);
        }

        // A field whose shipped values are all different carries no majority to disagree with.
        // Reporting BundleName as an outlier because ours is unique would bury the real findings.
        [Test]
        public void Compare_PerInstanceUniqueField_IsNotADisagreement()
        {
            var ours = Shipped("11111111-2222-3333-4444-555555555555", "Levels/MP/Kashyyyk_01/Mine", 2u);

            List<FieldFinding> findings = ShippedDiff.Compare(ours, NineShipped(), 4);

            FieldFinding bundle = Find(findings, "BundleName");
            Assert.IsNotNull(bundle);
            Assert.AreEqual("per-instance", bundle.Verdict);
        }

        // Flags is per-instance unique too, but it is not free: across the shipped population its low
        // 25 bits equal the object's own exported GUID. A duplicated object that keeps its source's
        // Flags therefore shares a sub-level identity with it, and nothing errors. So a field whose
        // values look unique still has to be checked against the relation the shipped data satisfies.
        [Test]
        public void Compare_DerivedIdentityField_ReportsAViolation()
        {
            // Ours carries a SIBLING's Flags -- exactly what a duplication plugin produces.
            var ours = Shipped("11111111-2222-3333-4444-555555555555", "Levels/MP/Kashyyyk_01/Mine", 2u);
            ours.Flags = ((SubWorldRef)NineShipped()[0]).Flags;

            List<FieldFinding> findings = ShippedDiff.Compare(ours, NineShipped(), 4);

            FieldFinding flags = Find(findings, "Flags");
            Assert.IsNotNull(flags);
            Assert.AreEqual("derived-violation", flags.Verdict);
            Assert.IsTrue(flags.Note != null && flags.Note.IndexOf("GUID", StringComparison.OrdinalIgnoreCase) >= 0,
                "the note should say the field is derived from the instance GUID; got: " + flags.Note);
        }

        // The same field, derived correctly, must NOT be reported -- otherwise every object trips it
        // and the check is noise.
        [Test]
        public void Compare_DerivedIdentityField_SatisfiedIsNotAViolation()
        {
            var ours = Shipped("11111111-2222-3333-4444-555555555555", "Levels/MP/Kashyyyk_01/Mine", 2u);

            List<FieldFinding> findings = ShippedDiff.Compare(ours, NineShipped(), 4);

            FieldFinding flags = Find(findings, "Flags");
            Assert.IsNotNull(flags);
            Assert.IsTrue(flags.Verdict != "derived-violation",
                "a correctly derived Flags must not be reported; got: " + flags.Verdict);
        }

        // The relation only holds for some types (100% on SubWorldReferenceObjectData and
        // StaticModelEntityData, 0% on ObjectBlueprint and LayerData). It must be established from
        // the shipped population, never assumed -- asserting it where it does not hold would flag
        // every correct object.
        [Test]
        public void Compare_FieldWithNoDerivation_IsNotCheckedAgainstOne()
        {
            var shipped = new List<object>();
            string[] guids =
            {
                "e2da4a10-94cb-4bab-9bd9-d319c872c5fa", "999e5d27-584f-42d8-924f-8832c850c9f2",
                "08f72774-46c8-4a49-adef-f07bd1de22a1", "d094b474-b00f-46f8-be63-6b4cc6db0277",
                "f99dbaa2-03bc-4eb8-b101-845285427988",
            };
            // Flags unrelated to the GUID: a plain counter, as a blueprint-ish type carries.
            for (int i = 0; i < guids.Length; i++)
                shipped.Add(new SubWorldRef(new Guid(guids[i])) { Flags = (uint)(1000 + i), BundleName = "b" + i });

            var ours = new SubWorldRef(new Guid("11111111-2222-3333-4444-555555555555")) { Flags = 4242, BundleName = "mine" };

            List<FieldFinding> findings = ShippedDiff.Compare(ours, shipped, 4);

            FieldFinding flags = Find(findings, "Flags");
            Assert.IsNotNull(flags);
            Assert.IsTrue(flags.Verdict != "derived-violation",
                "no derivation holds across this population, so none may be asserted; got: " + flags.Verdict);
        }

        [Test]
        public void Compare_NoShippedInstances_ReturnsNoFindings()
        {
            var ours = Shipped("11111111-2222-3333-4444-555555555555", "x", 2u);
            List<FieldFinding> findings = ShippedDiff.Compare(ours, new List<object>(), 4);
            Assert.AreEqual(0, findings.Count);
        }
    }
}
