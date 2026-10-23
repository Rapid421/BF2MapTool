using System.Collections.Generic;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Tests
{
    public class AnimExporterTests
    {
        // A fake decoder that records the request and writes one rotation key on the first bone.
        private sealed class FakeDecoder : IClipChannelDecoder
        {
            public ClipDecodeRequest LastRequest;
            public bool Result;
            public string LastClipName { get { return null; } }
            public bool TryDecode(ClipDecodeRequest request, SeAnim anim)
            {
                LastRequest = request;
                if (Result && anim.Bones.Count > 0)
                {
                    anim.FrameCount = 1;
                    anim.Bones[0].RotKeys.Add(new SeRotKey { Frame = 0, Rotation = SeQuat.Identity });
                }
                return Result;
            }
        }

        private static StrikeClip SampleClip()
        {
            return new StrikeClip
            {
                FullName = "Vader_Strike_01",
                ControllerName = "Vader_Strike_01",
                ClipOffset = 0x1234,
                ClipLength = 60,
            };
        }

        [Test]
        public void BuildAnim_CreatesOneBonePerSkeletonBone_InOrder()
        {
            var bones = new List<string> { "Root", "Spine", "Head" };
            bool decoded;
            SeAnim anim = AnimExporter.BuildAnim(SampleClip(), bones, new UnimplementedClipChannelDecoder(), out decoded);

            Assert.IsFalse(decoded, "stub decoder produces no keyframes");
            Assert.AreEqual(3, anim.Bones.Count);
            Assert.AreEqual("Root", anim.Bones[0].Name);
            Assert.AreEqual("Spine", anim.Bones[1].Name);
            Assert.AreEqual("Head", anim.Bones[2].Name);
        }

        [Test]
        public void BuildAnim_PassesClipMetadataToDecoder()
        {
            var bones = new List<string> { "Root" };
            var fake = new FakeDecoder { Result = false };
            bool decoded;
            AnimExporter.BuildAnim(SampleClip(), bones, fake, out decoded);

            Assert.IsNotNull(fake.LastRequest);
            Assert.AreEqual("Vader_Strike_01", fake.LastRequest.ClipName);
            Assert.AreEqual((uint)0x1234, fake.LastRequest.ClipOffset);
            Assert.AreEqual(1, fake.LastRequest.SkeletonBoneNames.Count);
        }

        [Test]
        public void BuildAnim_DecoderCanFillKeyframes()
        {
            var bones = new List<string> { "Root" };
            var fake = new FakeDecoder { Result = true };
            bool decoded;
            SeAnim anim = AnimExporter.BuildAnim(SampleClip(), bones, fake, out decoded);

            Assert.IsTrue(decoded);
            Assert.AreEqual(1, anim.Bones[0].RotKeys.Count);
        }

        [Test]
        public void SanitizeFileName_KeepsStrikeNameIntact()
        {
            Assert.AreEqual("Vader_Strike_01", AnimExporter.SanitizeFileName("Vader_Strike_01"));
        }
    }
}
