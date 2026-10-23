using System;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class LightPortTests
    {
        private static LinearTransformData Translation(float x, float y, float z)
        {
            LinearTransformData t = LightPort.Identity();
            t.Trans = new[] { x, y, z };
            return t;
        }

        private static void AssertVec(float[] expected, float[] actual)
        {
            Assert.IsNotNull(actual);
            for (int i = 0; i < 3; i++)
                Assert.IsTrue(Math.Abs(expected[i] - actual[i]) < 1e-5f,
                    "component " + i + ": expected " + expected[i] + " got " + actual[i]);
        }

        [Test]
        public void Compose_WithIdentityParent_ReturnsLocal()
        {
            LinearTransformData local = Translation(1, 2, 3);
            LinearTransformData world = LightPort.Compose(local, LightPort.Identity());
            AssertVec(new float[] { 1, 2, 3 }, world.Trans);
            AssertVec(new float[] { 1, 0, 0 }, world.Right);
            AssertVec(new float[] { 0, 0, 1 }, world.Forward);
        }

        [Test]
        public void Compose_TranslationOnlyParent_AddsTranslation()
        {
            LinearTransformData world = LightPort.Compose(Translation(1, 2, 3), Translation(10, 20, 30));
            AssertVec(new float[] { 11, 22, 33 }, world.Trans);
        }

        [Test]
        public void Compose_RotatedParent_RotatesLocalTranslationAndBasis()
        {
            // Parent rotated 90 degrees about Y: local +X maps to world -Z, local +Z maps to world +X.
            LinearTransformData parent = new LinearTransformData
            {
                Right = new[] { 0f, 0f, -1f },
                Up = new[] { 0f, 1f, 0f },
                Forward = new[] { 1f, 0f, 0f },
                Trans = new[] { 5f, 0f, 0f }
            };
            LinearTransformData world = LightPort.Compose(Translation(1, 0, 0), parent);
            AssertVec(new float[] { 5, 0, -1 }, world.Trans);
            AssertVec(new float[] { 0, 0, -1 }, world.Right);
            AssertVec(new float[] { 1, 0, 0 }, world.Forward);
        }

        [Test]
        public void Compose_ScaledParent_ScalesBasisAndTranslation()
        {
            LinearTransformData parent = LightPort.Identity();
            parent.Right = new[] { 2f, 0f, 0f };
            parent.Up = new[] { 0f, 2f, 0f };
            parent.Forward = new[] { 0f, 0f, 2f };
            LinearTransformData world = LightPort.Compose(Translation(1, 1, 1), parent);
            AssertVec(new float[] { 2, 2, 2 }, world.Trans);
            AssertVec(new float[] { 2, 0, 0 }, world.Right);
        }

        [Test]
        public void Compose_NullRows_TreatedAsIdentity()
        {
            LinearTransformData local = new LinearTransformData { Trans = new[] { 1f, 2f, 3f } };
            LinearTransformData world = LightPort.Compose(local, new LinearTransformData());
            AssertVec(new float[] { 1, 2, 3 }, world.Trans);
            AssertVec(new float[] { 0, 1, 0 }, world.Up);
        }

        [Test]
        public void IsLightEntityType_MatchesPbrLightsOnly()
        {
            Assert.IsTrue(LightPort.IsLightEntityType("PbrSphereLightEntityData"));
            Assert.IsTrue(LightPort.IsLightEntityType("PbrSpotLightEntityData"));
            Assert.IsFalse(LightPort.IsLightEntityType("StaticModelEntityData"));
            Assert.IsFalse(LightPort.IsLightEntityType("LightProbeVolumeEntityData"));
            Assert.IsFalse(LightPort.IsLightEntityType(null));
        }

        [Test]
        public void Vec3_RoundTripsThroughText()
        {
            float[] v;
            Assert.IsTrue(LightPort.TryParseVec3(LightPort.FormatVec3(new[] { 0.25f, -1.5f, 3f }), out v));
            AssertVec(new float[] { 0.25f, -1.5f, 3f }, v);
            Assert.IsFalse(LightPort.TryParseVec3("1,2", out v));
            Assert.IsFalse(LightPort.TryParseVec3("a,b,c", out v));
        }

        [Test]
        public void Vector_RoundTripsTwoToFourComponents()
        {
            float[] v;
            Assert.IsTrue(LightPort.TryParseVector(LightPort.FormatVector(new[] { 0f, 0f, -3.56675148f, 1.02069271f }), out v));
            Assert.IsTrue(v.Length == 4, "expected 4 components, got " + v.Length);
            Assert.IsTrue(Math.Abs(v[2] + 3.56675148f) < 1e-5f);
            Assert.IsTrue(LightPort.TryParseVector("0.2,0.4", out v));
            Assert.IsTrue(v.Length == 2, "expected 2 components, got " + v.Length);
            Assert.IsFalse(LightPort.TryParseVector("1", out v));
            Assert.IsFalse(LightPort.TryParseVector("1,2,3,4,5", out v));
            Assert.IsFalse(LightPort.TryParseVector("a,b", out v));
        }

        [Test]
        public void ScaleValue_ScalesNumbersAndLeavesTextAlone()
        {
            Assert.AreEqual("3000", LightPort.ScaleValue("1500", 2f));
            Assert.AreEqual("LightUnitType_LuminousPower", LightPort.ScaleValue("LightUnitType_LuminousPower", 2f));
        }

        [Test]
        public void SkipOnImport_ExcludesEngineManagedFields()
        {
            Assert.IsTrue(LightPort.SkipOnImport.Contains("Flags"));
            Assert.IsTrue(LightPort.SkipOnImport.Contains("Transform"));
            Assert.IsFalse(LightPort.SkipOnImport.Contains("Intensity"));
        }
    }
}
