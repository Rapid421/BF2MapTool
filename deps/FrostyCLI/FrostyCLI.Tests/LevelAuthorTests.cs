using System;
using System.Collections.Generic;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class LevelAuthorTests
    {
        [Test]
        public void DeterministicGuid_IsStableAndDistinct()
        {
            Guid a = LevelAuthor.DeterministicGuid("hoth2015-lightaux", "ap_interior");
            Assert.AreEqual(a, LevelAuthor.DeterministicGuid("hoth2015-lightaux", "ap_interior"));
            Assert.IsFalse(a == LevelAuthor.DeterministicGuid("hoth2015-lightaux", "ap_corridor"), "ids must differ");
            Assert.IsFalse(a == LevelAuthor.DeterministicGuid("other-seed", "ap_interior"), "seeds must differ");
        }

        // 2015 Arctic_01 AreaProximity 4edfce27-f4ff-4934-9fd5-b4da7e5da626 ships Flags 1625280039 = 0x60DFCE27;
        // shipped OBBData shapes carry the flag byte alone (0x02000000), VolumeVectorShapeData 0x06000000.
        [Test]
        public void EntityFlags_MatchesShippedAnchors()
        {
            Guid ap = new Guid("4edfce27-f4ff-4934-9fd5-b4da7e5da626");
            Assert.AreEqual(1625280039u, LevelAuthor.EntityFlags(ap, 0x60, true));
            Assert.AreEqual(0x02000000u, LevelAuthor.EntityFlags(ap, 2, false));
            Assert.AreEqual(0x06000000u, LevelAuthor.EntityFlags(Guid.NewGuid(), 6, false));
            // VisualEnvironmentReferenceObjectData 84317a67-... in Hoth_01 Sunset_Lighting_Schematic: 137460327 = 0x08317A67
            Assert.AreEqual(137460327u, LevelAuthor.EntityFlags(new Guid("84317a67-be61-4787-9770-1a1071f5914e"), 8, true));
        }

        // PF_Arctic_01_Base_02_CombinedforE3 at 70b3d229 (yaw only): its corridor shape's first point (25.138, -5.461, -91.884)
        [Test]
        public void TransformPoint_UsesRowVectorConvention()
        {
            double[] right = { -0.7920084, 0, -0.6105102 }, up = { 0, 1, 0 }, fwd = { 0.6105102, 0, -0.7920084 };
            double[] trans = { 270.8798, 359.68, -1635.79333 };
            double[] w = LevelAuthor.TransformPoint(new[] { 25.1380749, -5.461383, -91.88362 }, right, up, fwd, trans);
            Assert.IsTrue(Math.Abs(w[0] - (25.1380749 * -0.7920084 + -91.88362 * 0.6105102 + 270.8798)) < 1e-9, "x");
            Assert.IsTrue(Math.Abs(w[1] - (359.68 - 5.461383)) < 1e-9, "y");
            Assert.IsTrue(Math.Abs(w[2] - (25.1380749 * -0.6105102 + -91.88362 * -0.7920084 - 1635.79333)) < 1e-9, "z");
        }

        public class FakeComponent
        {
            public List<string> PropertyOverrides { get; set; } = new List<string> { "MinEV", "MaxEV" };
            public uint FieldFlagOverride0 { get; set; } = 6146;
            public uint FieldFlagOverride1 { get; set; } = 1;
            public float MinEV { get; set; } = 8.5f;
        }

        [Test]
        public void ClearOverrides_EmptiesNamesAndBits_KeepsValues()
        {
            var c = new FakeComponent();
            VeCommand.ClearOverrides(c);
            Assert.AreEqual(0, c.PropertyOverrides.Count);
            Assert.AreEqual(0u, c.FieldFlagOverride0);
            Assert.AreEqual(0u, c.FieldFlagOverride1);
            Assert.AreEqual(8.5f, c.MinEV);
        }
    }
}
