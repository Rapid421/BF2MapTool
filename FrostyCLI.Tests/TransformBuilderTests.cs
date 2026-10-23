using System;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class TransformBuilderTests
    {
        private const float Tol = 1e-5f;

        private static void AssertVec3Close(float[] expected, float[] actual, string what)
        {
            for (int i = 0; i < 3; i++)
            {
                float d = Math.Abs(expected[i] - actual[i]);
                if (d > Tol) throw new Exception(what + ": component " + i + " expected " + expected[i] + " got " + actual[i]);
            }
        }

        [Test]
        public void Build_Identity_ReturnsIdentityMatrix()
        {
            var t = TransformBuilder.Build("0,0,0", "0,0,0", "1");
            AssertVec3Close(new[] { 1f, 0f, 0f }, t.Right,   "right");
            AssertVec3Close(new[] { 0f, 1f, 0f }, t.Up,      "up");
            AssertVec3Close(new[] { 0f, 0f, 1f }, t.Forward, "forward");
            AssertVec3Close(new[] { 0f, 0f, 0f }, t.Trans,   "trans");
        }

        [Test]
        public void Build_PosOnly_TranslationSet()
        {
            var t = TransformBuilder.Build("10,20,30", "0,0,0", "1");
            AssertVec3Close(new[] { 10f, 20f, 30f }, t.Trans,   "trans");
            AssertVec3Close(new[] { 1f,  0f,  0f  }, t.Right,   "right");
            AssertVec3Close(new[] { 0f,  1f,  0f  }, t.Up,      "up");
            AssertVec3Close(new[] { 0f,  0f,  1f  }, t.Forward, "forward");
        }

        [Test]
        public void Build_Yaw90_ClockwiseLH()
        {
            var t = TransformBuilder.Build("0,0,0", "90,0,0", "1");
            // Yaw=90 LH: right→+Z, up unchanged, forward→-X
            AssertVec3Close(new[] { 0f, 0f, 1f }, t.Right,   "right");
            AssertVec3Close(new[] { 0f, 1f, 0f }, t.Up,      "up");
            AssertVec3Close(new[] {-1f, 0f, 0f }, t.Forward, "forward");
        }

        [Test]
        public void Build_Scale2_ScalesColumns()
        {
            var t = TransformBuilder.Build("0,0,0", "0,0,0", "2");
            AssertVec3Close(new[] { 2f, 0f, 0f }, t.Right,   "right");
            AssertVec3Close(new[] { 0f, 2f, 0f }, t.Up,      "up");
            AssertVec3Close(new[] { 0f, 0f, 2f }, t.Forward, "forward");
            AssertVec3Close(new[] { 0f, 0f, 0f }, t.Trans,   "trans");
        }

        [Test]
        public void Build_PerAxisScale_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => TransformBuilder.Build("0,0,0", "0,0,0", "1,2,3"));
        }

        [Test]
        public void Build_NullArgs_TreatedAsDefaults()
        {
            var t = TransformBuilder.Build(null, null, null);
            AssertVec3Close(new[] { 1f, 0f, 0f }, t.Right,   "right");
            AssertVec3Close(new[] { 0f, 1f, 0f }, t.Up,      "up");
            AssertVec3Close(new[] { 0f, 0f, 1f }, t.Forward, "forward");
            AssertVec3Close(new[] { 0f, 0f, 0f }, t.Trans,   "trans");
        }
    }
}
