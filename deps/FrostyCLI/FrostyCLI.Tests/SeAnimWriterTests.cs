using FrostyCLI.Core.Animation;

namespace FrostyCLI.Tests
{
    // SeAnimWriter must emit bytes that FAAE's SeAnimParser (vendored here) reads back exactly.
    public class SeAnimWriterTests
    {
        private static SeAnim MakeRotAnim()
        {
            var anim = new SeAnim
            {
                AnimType = SeAnimType.Absolute,
                Framerate = 30f,
                FrameCount = 2,
            };
            var spine = new SeAnimBone { Name = "Spine" };
            spine.RotKeys.Add(new SeRotKey { Frame = 0, Rotation = new SeQuat(0f, 0f, 0f, 1f) });
            spine.RotKeys.Add(new SeRotKey { Frame = 1, Rotation = new SeQuat(0.1f, 0.2f, 0.3f, 0.927362f) });
            var head = new SeAnimBone { Name = "Head" };
            head.RotKeys.Add(new SeRotKey { Frame = 0, Rotation = new SeQuat(0f, 0f, 0f, 1f) });
            anim.Bones.Add(spine);
            anim.Bones.Add(head);
            return anim;
        }

        [Test]
        public void Write_RotationAnim_RoundTripsHeaderAndBones()
        {
            byte[] bytes = SeAnimWriter.Write(MakeRotAnim());
            Vendored.SeAnimFile parsed = Vendored.SeAnimParser.Parse(bytes);

            Assert.AreEqual(Vendored.SeAnimType.Absolute, parsed.AnimType);
            Assert.AreEqual(30f, parsed.Framerate);
            Assert.AreEqual(2, parsed.FrameCount);
            Assert.AreEqual(2, parsed.Bones.Count);
            Assert.IsTrue(parsed.HasRotation, "rotation presence flag must be set");
            Assert.IsFalse(parsed.HasLocation, "location presence flag must be clear");
            Assert.IsFalse(parsed.HasScale, "scale presence flag must be clear");
            Assert.AreEqual("Spine", parsed.Bones[0].Name);
            Assert.AreEqual("Head", parsed.Bones[1].Name);
        }

        [Test]
        public void Write_RotationAnim_PreservesQuaternionOrderAndFrames()
        {
            byte[] bytes = SeAnimWriter.Write(MakeRotAnim());
            Vendored.SeAnimFile parsed = Vendored.SeAnimParser.Parse(bytes);

            Vendored.SeAnimBone spine = parsed.GetBone("Spine");
            Assert.IsNotNull(spine);
            Assert.AreEqual(2, spine.RotKeys.Count);
            Assert.AreEqual(0, spine.RotKeys[0].Frame);
            Assert.AreEqual(1, spine.RotKeys[1].Frame);
            // Quaternion components written (x, y, z, w)
            Vendored.Quat q = spine.RotKeys[1].Rotation;
            Assert.IsTrue(System.Math.Abs(q.X - 0.1f) < 1e-6, "X");
            Assert.IsTrue(System.Math.Abs(q.Y - 0.2f) < 1e-6, "Y");
            Assert.IsTrue(System.Math.Abs(q.Z - 0.3f) < 1e-6, "Z");
            Assert.IsTrue(System.Math.Abs(q.W - 0.927362f) < 1e-6, "W");
        }

        [Test]
        public void Write_AdditiveLocationAndRotation_RoundTrips()
        {
            var anim = new SeAnim { AnimType = SeAnimType.Additive, Framerate = 60f, FrameCount = 3 };
            var b = new SeAnimBone { Name = "AITrajectory" };
            b.PosKeys.Add(new SePosKey { Frame = 0, Position = new SeVec3(0f, 0f, 0f) });
            b.PosKeys.Add(new SePosKey { Frame = 2, Position = new SeVec3(1.5f, -2.5f, 3.5f) });
            b.RotKeys.Add(new SeRotKey { Frame = 1, Rotation = new SeQuat(0f, 0f, 0f, 1f) });
            anim.Bones.Add(b);

            Vendored.SeAnimFile parsed = Vendored.SeAnimParser.Parse(SeAnimWriter.Write(anim));

            Assert.AreEqual(Vendored.SeAnimType.Additive, parsed.AnimType);
            Assert.IsTrue(parsed.HasLocation, "location present");
            Assert.IsTrue(parsed.HasRotation, "rotation present");
            Vendored.SeAnimBone bone = parsed.GetBone("AITrajectory");
            Assert.AreEqual(2, bone.PosKeys.Count);
            Assert.AreEqual(2, bone.PosKeys[1].Frame);
            Vendored.Vec3 p = bone.PosKeys[1].Position;
            Assert.IsTrue(System.Math.Abs(p.X - 1.5f) < 1e-6, "px");
            Assert.IsTrue(System.Math.Abs(p.Y + 2.5f) < 1e-6, "py");
            Assert.IsTrue(System.Math.Abs(p.Z - 3.5f) < 1e-6, "pz");
            // Bone with no location keys still round-trips an empty location channel.
            Assert.AreEqual(1, bone.RotKeys.Count);
        }
    }
}
