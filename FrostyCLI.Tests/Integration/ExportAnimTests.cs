using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FrostyCLI.Commands;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Tests.Integration
{
    // End-to-end: export real SWBF2 hero strikes to SEAnim and confirm FAAE's parser (vendored)
    // reads them and the bone names line up with the skeleton FAAE binds against.
    public class ExportAnimTests
    {
        // Returns true when SWBF2 assets are reachable. The Session is process-global and
        // initialized once by whichever integration test runs first; in the full suite a SWBF1
        // test can win that race, so these SWBF2-specific tests probe a known SWBF2 asset and
        // skip when it isn't present (they still run fully under `--filter ExportAnim`, which
        // initializes SWBF2 itself).
        private static bool EnsureSwbf2(ExportLogger logger)
        {
            if (!Session.IsInitialized)
            {
                string gp = Environment.GetEnvironmentVariable("FROSTYCLI_GAME_PATH_SWBF2");
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string adaptersDir = Path.Combine(exeDir, "Adapters");
                if (!Directory.Exists(adaptersDir)) Directory.CreateDirectory(adaptersDir);
                Session.Initialize("swbf2", gp, null, adaptersDir, logger);
            }
            return Frosty.Core.App.AssetManager != null
                && Frosty.Core.App.AssetManager.GetEbxEntry(AnimSkeleton.DefaultSkeletonPath) != null;
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ExportAnimBatch_FindsHeroStrikes()
        {
            var logger = new ExportLogger(quiet: true);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 session not active in shared run)"); return; }

            List<StrikeClip> strikes = ExportAnimCommand.LoadStrikes(
                "Gameplay/SoldierStateMachine/DefaultSoldierStateMachine");

            Assert.IsTrue(strikes.Count > 50,
                "Expected many hero strikes, got " + strikes.Count);
            // Names are normalized to FAAE's "<Hero>_<Suffix>" form.
            bool anyStrike = false;
            foreach (var s in strikes)
                if (s.FullName.IndexOf("_Strike_", StringComparison.Ordinal) >= 0 ||
                    s.FullName.IndexOf("_Closer_", StringComparison.Ordinal) >= 0)
                { anyStrike = true; break; }
            Assert.IsTrue(anyStrike, "no strike/closer clip names found");
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ExportAnim_WritesValidSeAnim_BoneNamesMatchSkeleton()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 session not active in shared run)"); return; }

            List<string> boneNames = AnimSkeleton.ReadBoneNames(AnimSkeleton.DefaultSkeletonPath);
            Assert.IsTrue(boneNames.Count > 0, "skeleton has no bones");

            // Pick a real strike to export.
            List<StrikeClip> strikes = ExportAnimCommand.LoadStrikes(
                "Gameplay/SoldierStateMachine/DefaultSoldierStateMachine");
            Assert.IsTrue(strikes.Count > 0, "no strikes to export");
            string clipName = strikes[0].FullName;

            string outFile = Path.Combine(Path.GetTempPath(),
                "animtest_" + Guid.NewGuid() + ".seanim");
            try
            {
                int r = new ExportAnimCommand().Execute(
                    new[] { "--clip", clipName, "--out", outFile }, logger, fmt);
                Assert.AreEqual(0, r);
                Assert.IsTrue(File.Exists(outFile), "no .seanim written");

                // FAAE's parser must read it without throwing, and the bone-name section
                // must match the skeleton in count and order.
                Vendored.SeAnimFile parsed = Vendored.SeAnimParser.Parse(outFile);
                Assert.AreEqual(boneNames.Count, parsed.Bones.Count);
                for (int i = 0; i < boneNames.Count; i++)
                    Assert.AreEqual(boneNames[i], parsed.Bones[i].Name);
            }
            finally
            {
                try { File.Delete(outFile); } catch { }
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ExportAnim_DecodesRealKeyframes_UnitQuaternions()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 session not active in shared run)"); return; }

            string outFile = Path.Combine(Path.GetTempPath(), "animkf_" + Guid.NewGuid() + ".seanim");
            try
            {
                int r = new ExportAnimCommand().Execute(
                    new[] { "--clip", "Vader_Strike_1", "--out", outFile }, logger, fmt);
                Assert.AreEqual(0, r);

                Vendored.SeAnimFile parsed = Vendored.SeAnimParser.Parse(outFile);
                Assert.IsTrue(parsed.HasRotation, "expected rotation keyframes");
                Assert.IsTrue(parsed.FrameCount > 1, "expected multiple frames, got " + parsed.FrameCount);

                // A real melee strike rotates the spine/arms. Confirm a known bone carries multi-frame
                // rotation keys whose quaternions are unit length.
                Vendored.SeAnimBone spine = parsed.GetBone("Spine");
                Assert.IsNotNull(spine);
                Assert.IsTrue(spine.RotKeys.Count > 1,
                    "Spine should have animated rotation keys, got " + spine.RotKeys.Count);
                int checkd = 0;
                foreach (var k in spine.RotKeys)
                {
                    var q = k.Rotation;
                    double len = System.Math.Sqrt((double)q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
                    Assert.IsTrue(System.Math.Abs(len - 1.0) < 0.01,
                        "non-unit quaternion on Spine frame " + k.Frame + " len=" + len);
                    checkd++;
                }
                Assert.IsTrue(checkd > 1, "expected several Spine rot keys");

                // Real motion: the rotation must actually change across the clip (not a stuck pose).
                var first = spine.RotKeys[0].Rotation;
                var last = spine.RotKeys[spine.RotKeys.Count - 1].Rotation;
                double delta = System.Math.Abs(first.X - last.X) + System.Math.Abs(first.Y - last.Y)
                             + System.Math.Abs(first.Z - last.Z) + System.Math.Abs(first.W - last.W);
                Assert.IsTrue(delta > 1e-4, "Spine rotation does not change across the clip (delta=" + delta + ")");

                // Count how many bones actually got rotation keys (sanity: a strike animates many).
                int animatedBones = 0;
                foreach (var b in parsed.Bones) if (b.RotKeys.Count > 0) animatedBones++;
                Assert.IsTrue(animatedBones >= 20,
                    "expected many animated bones in a strike, got " + animatedBones);
            }
            finally
            {
                try { File.Delete(outFile); } catch { }
            }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ExportAnim_EmitsEbxLocalPoseFrame()
        {
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 session not active in shared run)"); return; }

            var bind = AnimSkeleton.ReadBindPose(AnimSkeleton.DefaultSkeletonPath);
            Assert.IsTrue(bind.ContainsKey("Hips"), "no Hips bind");
            float bindHipsY = bind["Hips"].Trans.Y; // ~0.936 standing offset

            string outFile = Path.Combine(Path.GetTempPath(), "animframe_" + Guid.NewGuid() + ".seanim");
            try
            {
                int r = new ExportAnimCommand().Execute(
                    new[] { "--clip", "Anakin_Strike_1", "--out", outFile }, logger, fmt);
                Assert.AreEqual(0, r);
                Vendored.SeAnimFile parsed = Vendored.SeAnimParser.Parse(outFile);

                // Decoded translations are full local values already in the EBX LocalPose frame
                // (constant bones decode to exactly their bind offset), so Hips carries the standing
                // height directly — no bind addition. It should sit near the ~0.93 standing offset.
                Vendored.SeAnimBone hips = parsed.GetBone("Hips");
                Assert.IsNotNull(hips);
                Assert.IsTrue(hips.PosKeys.Count > 0, "Hips has no translation keys");
                float hipsY = hips.PosKeys[0].Position.Y;
                Assert.IsTrue(Math.Abs(hipsY - bindHipsY) < 0.25f,
                    "Hips frame0 Y=" + hipsY + " should be near the EBX LocalPose standing offset " + bindHipsY);
                Assert.IsTrue(bindHipsY > 0.5f, "sanity: bind Hips Y should be the ~0.93 standing offset");

                // Rotations are the decoded local quaternions emitted directly (identity convention);
                // they must be unit. (Anatomical correctness is validated offline with FAAE's exact FK
                // via tools/animsolve, which can't run in this headless test.)
                Vendored.SeAnimBone spine = parsed.GetBone("Spine");
                Assert.IsNotNull(spine);
                Assert.IsTrue(spine.RotKeys.Count > 0, "Spine has no rotation keys");
                var q = spine.RotKeys[0].Rotation;
                double len = Math.Sqrt((double)q.X*q.X + q.Y*q.Y + q.Z*q.Z + q.W*q.W);
                Assert.IsTrue(Math.Abs(len - 1.0) < 0.01, "Spine quat not unit");
            }
            finally { try { File.Delete(outFile); } catch { } }
        }

        [Test]
        [IntegrationTest("swbf2")]
        public void ExportAnim_AdditiveStrike_DecodesViaNamedController()
        {
            // Additive strikes (e.g. Add_ObiWan_DefensiveRush) carry the clip on the named [i+2]
            // controller, not the player [i] ref; this guards that fallback in AntStateClipSource.
            var logger = new ExportLogger(quiet: true);
            var fmt = new OutputFormatter(json: false);
            if (!EnsureSwbf2(logger)) { Console.WriteLine("  (skipped: SWBF2 session not active in shared run)"); return; }

            string outFile = Path.Combine(Path.GetTempPath(), "animadd_" + Guid.NewGuid() + ".seanim");
            try
            {
                int r = new ExportAnimCommand().Execute(
                    new[] { "--clip", "Add_ObiWan_DefensiveRush_Strike_1", "--out", outFile }, logger, fmt);
                Assert.AreEqual(0, r);
                Vendored.SeAnimFile parsed = Vendored.SeAnimParser.Parse(outFile);
                int animatedBones = 0;
                foreach (var b in parsed.Bones) if (b.RotKeys.Count > 0) animatedBones++;
                Assert.IsTrue(animatedBones >= 5,
                    "additive Obi-Wan strike should decode keyframes, got " + animatedBones + " animated bones");
            }
            finally
            {
                try { File.Delete(outFile); } catch { }
            }
        }
    }
}
