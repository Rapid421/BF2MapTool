using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests.Integration
{
    // Foundation for animation IMPORT: prove an existing Raw clip's keyframes can be overwritten
    // in place (same shape) in the antstate bank bytes and that the change round-trips through the
    // decoder. This is the byte-level core that a SEAnim->Raw encoder + `res import`/`mod build`
    // will build on.
    public class AnimImportTests
    {
        private static bool EnsureSwbf2(ExportLogger logger)
        {
            if (!Session.IsInitialized)
            {
                string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                Session.Initialize("swbf2", gp, null, Path.Combine(exeDir, "Adapters"), logger);
            }
            return App.AssetManager != null && App.AssetManager.GetEbxEntry(AnimSkeleton.DefaultSkeletonPath) != null;
        }

        private static byte[] LoadBankBytes()
        {
            ResAssetEntry e = App.AssetManager.GetResEntry(ExportAnimCommand.SharedAnimBankRes);
            MethodInfo m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = m.Invoke(App.AssetManager, new object[] { e });
            if (raw is byte[] b) return b;
            using (var ms = new MemoryStream()) { ((Stream)raw).CopyTo(ms); return ms.ToArray(); }
        }

        private static Guid StrikeGuid(string fullName)
        {
            foreach (var s in ExportAnimCommand.LoadStrikes("Gameplay/SoldierStateMachine/DefaultSoldierStateMachine"))
                if (string.Equals(s.FullName, fullName, StringComparison.OrdinalIgnoreCase)) return s.ControllerAssetGuid;
            return Guid.Empty;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void InPlaceEdit_OverwritesKeyframe_RoundTrips()
        {
            var logger = new ExportLogger(quiet: true);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 not active)"); return; }

            Guid guid = StrikeGuid("Vader_Strike_1");
            Assert.IsTrue(guid != Guid.Empty, "Vader_Strike_1 not found");

            // The MemoryStream shares the byte[] so post-edit decodes read the mutated bytes.
            byte[] data = LoadBankBytes();
            using (var dec = new AntAnimationDecoder(new MemoryStream(data)))
            {
                AntAnimationDecoder.ClipLayout layout;
                Assert.IsTrue(dec.TryGetRawClipLayout(guid, out layout) && layout.Found, "no Raw layout");

                // Pick the first animated rotation channel.
                AntAnimationDecoder.ClipChannel rot = null;
                foreach (var ch in layout.Channels) if (ch.Rotation && ch.Animated) { rot = ch; break; }
                Assert.IsNotNull(rot);

                // Decode the original frame-0 rotation for that bone.
                var before = new SeAnim();
                before.Bones.Add(new SeAnimBone { Name = rot.Bone });
                Assert.IsTrue(dec.TryDecodeClip(guid, before));
                Assert.IsTrue(before.Bones[0].RotKeys.Count > 0, "bone had no rot keys");
                var orig = before.Bones[0].RotKeys[0].Rotation;

                // Overwrite frame 0 with a distinctive quaternion directly in the bank bytes.
                var newQ = new SeQuat(0.1f, 0.2f, 0.3f, 0.9273f);
                WriteFloat(data, rot.ByteOffset + 0, newQ.X);
                WriteFloat(data, rot.ByteOffset + 4, newQ.Y);
                WriteFloat(data, rot.ByteOffset + 8, newQ.Z);
                WriteFloat(data, rot.ByteOffset + 12, newQ.W);

                // Re-decode (same byte[] -> reads the edit) and confirm it changed to our value.
                var after = new SeAnim();
                after.Bones.Add(new SeAnimBone { Name = rot.Bone });
                Assert.IsTrue(dec.TryDecodeClip(guid, after));
                var got = after.Bones[0].RotKeys[0].Rotation;

                Assert.IsTrue(Math.Abs(got.X - 0.1f) < 1e-5 && Math.Abs(got.Y - 0.2f) < 1e-5
                           && Math.Abs(got.Z - 0.3f) < 1e-5 && Math.Abs(got.W - 0.9273f) < 1e-5,
                    "in-place edit did not round-trip: got (" + got.X + "," + got.Y + "," + got.Z + "," + got.W + ")");
                // And it actually differs from the original (sanity).
                Assert.IsTrue(Math.Abs(got.X - orig.X) + Math.Abs(got.W - orig.W) > 1e-4, "value did not change");
            }
        }

        private static void WriteFloat(byte[] buf, long off, float v)
        {
            byte[] b = BitConverter.GetBytes(v);
            for (int i = 0; i < 4; i++) buf[off + i] = b[i];
        }

        private static SeAnim FullSkeletonAnim()
        {
            var a = new SeAnim();
            foreach (string n in AnimSkeleton.ReadBoneNames(AnimSkeleton.DefaultSkeletonPath))
                a.Bones.Add(new SeAnimBone { Name = n });
            return a;
        }
        private static SeAnimBone Find(SeAnim a, string name)
        {
            foreach (var b in a.Bones) if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ImportAnim_BuildMod_ReportSize()
        {
            var logger = new ExportLogger(quiet: true);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 not active)"); return; }
            Guid guid = StrikeGuid("Vader_Strike_1");
            Assert.IsTrue(guid != Guid.Empty);

            Session.Project.New();

            byte[] bank = LoadBankBytes();
            SeAnim anim = FullSkeletonAnim();
            byte[] patched;
            using (var dec = new AntAnimationDecoder(new MemoryStream(bank)))
            {
                Assert.IsTrue(dec.TryDecodeClip(guid, anim));
                patched = dec.EncodeClip(guid, anim, bank).Bank;
            }

            ResAssetEntry e = App.AssetManager.GetResEntry(ExportAnimCommand.SharedAnimBankRes);
            App.AssetManager.ModifyRes(e.Name, patched);
            Assert.IsTrue(App.AssetManager.GetResEntry(e.Name).IsModified, "ModifyRes did not take effect");

            string modFile = Path.Combine(Path.GetTempPath(), "animimport_" + Guid.NewGuid() + ".fbmod");
            try
            {
                var settings = new ModSettings { Name = "AnimImportTest", Author = "test", Version = "1.0.0" };
                bool ok = Session.Project.Build(modFile, settings, logger);
                Assert.IsTrue(ok, "mod build failed");
                Assert.IsTrue(File.Exists(modFile), "no .fbmod produced");
                long size = new FileInfo(modFile).Length;
                Console.WriteLine("RE: .fbmod size = " + size + " bytes (" + (size / 1024.0 / 1024.0).ToString("F1") + " MB)  bankRes=" + bank.Length);
                Assert.IsTrue(size > 0);
            }
            finally { try { File.Delete(modFile); } catch { } }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void EncodeClip_AppendAndRepoint_RoundTrips()
        {
            var logger = new ExportLogger(quiet: true);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 not active)"); return; }
            Guid guid = StrikeGuid("Vader_Strike_1");
            Assert.IsTrue(guid != Guid.Empty);

            byte[] original = LoadBankBytes();

            // Decode the original clip.
            SeAnim animA = FullSkeletonAnim();
            byte[] patched;
            using (var dec = new AntAnimationDecoder(new MemoryStream(original)))
            {
                Assert.IsTrue(dec.TryDecodeClip(guid, animA));
                // Re-encode it (append new arrays + repoint) into a fresh bank.
                var res = dec.EncodeClip(guid, animA, original);
                patched = res.Bank;
                Console.WriteLine("RE: encoded channels=" + res.Channels + " frames=" + res.Frames + " unmapped=" + res.Unmapped
                    + " bankGrewBy=" + (patched.Length - original.Length));
            }

            // Re-decode from the patched bank and compare a known animated bone.
            SeAnim animB = FullSkeletonAnim();
            using (var dec2 = new AntAnimationDecoder(new MemoryStream(patched)))
                Assert.IsTrue(dec2.TryDecodeClip(guid, animB));

            SeAnimBone a = Find(animA, "Spine"), b = Find(animB, "Spine");
            Assert.IsNotNull(a); Assert.IsNotNull(b);
            Assert.IsTrue(a.RotKeys.Count > 1, "original Spine had no animation");
            Assert.AreEqual(a.RotKeys.Count, b.RotKeys.Count);
            for (int i = 0; i < a.RotKeys.Count; i++)
            {
                var qa = a.RotKeys[i].Rotation; var qb = b.RotKeys[i].Rotation;
                double diff = Math.Abs(qa.X - qb.X) + Math.Abs(qa.Y - qb.Y) + Math.Abs(qa.Z - qb.Z) + Math.Abs(qa.W - qb.W);
                Assert.IsTrue(diff < 1e-4, "Spine rot mismatch at key " + i + " diff=" + diff);
            }
        }
    }
}
