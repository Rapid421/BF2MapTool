using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Commands
{
    // Exports SWBF2 hero-strike animations to standard SEAnim (.seanim) files for FAAE.
    //
    // Source: hero strikes are controllers in the soldier state machine EBX
    // (Gameplay/SoldierStateMachine/DefaultSoldierStateMachine, a CharacterStateOwnerData). Each
    // strike's CharacterStateClipControllerData references the packed clip keyframes by AntRef +
    // ClipOffset inside the antstate resource bank. Bone names come from the SkeletonAsset FAAE
    // binds against (Characters/Rigs/Humanoids/Walrus_HumanMale).
    //
    // NOTE: the packed Frostbite channel format is not yet reverse-engineered, so exported files
    // currently carry the correct header + bone-name section with no keyframes. The decode seam is
    // IClipChannelDecoder; swap UnimplementedClipChannelDecoder for a real decoder to fill keyframes.
    public class ExportAnimCommand : ICommand
    {
        public string Name => "export-anim";

        // Antstate bank holding the shared soldier/hero clips (where the strike clips live).
        internal const string SharedAnimBankRes =
            "animations/antanimations/gameplay/bundles/sharedbundles/common/animation/sharedbundleanimation_common_win32_antstate";

        // Builds the real RawAnimationAsset decoder by loading the shared antstate bank; falls back
        // to the keyframe-empty stub if the bank can't be loaded/parsed.
        internal static IClipChannelDecoder BuildDecoder(ExportLogger logger)
        {
            try
            {
                Stream s = OpenRes(SharedAnimBankRes);
                if (s == null) { logger.LogWarning("export-anim: shared anim bank not found; exporting without keyframes."); return new UnimplementedClipChannelDecoder(); }
                var dec = new AntAnimationDecoder(s);
                if (dec.RigDofCount == 0) { logger.LogWarning("export-anim: rig dof map empty; exporting without keyframes."); dec.Dispose(); return new UnimplementedClipChannelDecoder(); }
                return new AntClipChannelDecoder(dec);
            }
            catch (Exception ex)
            {
                logger.LogWarning("export-anim: decoder init failed ({0}); exporting without keyframes.", ex.Message);
                return new UnimplementedClipChannelDecoder();
            }
        }

        // GetRes may return a Stream or byte[] depending on SDK build.
        internal static Stream OpenRes(string resPath)
        {
            ResAssetEntry entry = App.AssetManager.GetResEntry(resPath);
            if (entry == null) return null;
            System.Reflection.MethodInfo m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = m?.Invoke(App.AssetManager, new object[] { entry });
            if (raw is Stream s) return s;
            if (raw is byte[] b) return new MemoryStream(b);
            return null;
        }

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath  = AntStateClipSource_DefaultStateMachine;
            string clipName = null;
            string outPath  = null;
            string skeleton = AnimSkeleton.DefaultSkeletonPath;
            bool   anyClip  = false;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--any") anyClip = true;
                else if (args[i] == "--clip"     && i + 1 < args.Length) clipName = args[++i];
                else if (args[i] == "--out"      && i + 1 < args.Length) outPath  = args[++i];
                else if (args[i] == "--skeleton" && i + 1 < args.Length) skeleton = args[++i];
                else if (args[i] == "--state-machine" && i + 1 < args.Length) ebxPath = args[++i];
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }

            if (clipName == null) { Console.Error.WriteLine("export-anim: missing --clip <name> (use export-anim-batch to export all)"); return 1; }
            if (outPath == null)  { Console.Error.WriteLine("export-anim: missing --out <file.seanim>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("export-anim: AssetManager not initialized. Pass --game-path."); return 1; }

            List<string> boneNames;
            List<StrikeClip> strikes;
            try
            {
                boneNames = AnimSkeleton.ReadBoneNames(skeleton);
                strikes = anyClip ? LoadAllClips(ebxPath) : LoadStrikes(ebxPath);
            }
            catch (Exception ex)
            {
                Fail(formatter, "export-anim", outPath, ebxPath, ex.Message);
                logger.LogError("export-anim load failed: {0}", ex);
                return 1;
            }

            StrikeClip clip = strikes.Find(s =>
                string.Equals(s.FullName, clipName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.ControllerName, clipName, StringComparison.OrdinalIgnoreCase));
            if (clip == null)
            {
                Fail(formatter, "export-anim", outPath, ebxPath, "clip not found: " + clipName);
                return 1;
            }

            try
            {
                bool decoded; string sourceClip = null;
                IClipChannelDecoder decoder = BuildDecoder(logger);
                SeAnim anim;
                try { anim = AnimExporter.BuildAnim(clip, boneNames, decoder, out decoded); sourceClip = decoder.LastClipName; }
                finally { (decoder as IDisposable)?.Dispose(); }
                string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                SeAnimWriter.WriteToFile(anim, outPath);

                var fields = new Dictionary<string, string>();
                fields["file"]     = outPath;
                fields["clip"]     = clip.FullName;
                fields["bones"]    = boneNames.Count.ToString();
                fields["keyframes"] = decoded ? "decoded" : "none (channel decode not implemented)";
                if (!string.IsNullOrEmpty(sourceClip)) fields["source"] = sourceClip;
                formatter.WriteResult("OK", "export-anim", fields);
                return 0;
            }
            catch (Exception ex)
            {
                Fail(formatter, "export-anim", outPath, ebxPath, ex.Message);
                logger.LogError("export-anim write failed: {0}", ex);
                return 1;
            }
        }

        internal const string AntStateClipSource_DefaultStateMachine =
            "Gameplay/SoldierStateMachine/DefaultSoldierStateMachine";

        internal static List<StrikeClip> LoadStrikes(string ebxPath)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) throw new Exception("state machine EBX not found: " + ebxPath);
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            if (asset == null) throw new Exception("failed to load EBX: " + entry.Name);
            return AntStateClipSource.EnumerateStrikes(asset);
        }

        internal static List<StrikeClip> LoadAllClips(string ebxPath)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) throw new Exception("state machine EBX not found: " + ebxPath);
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            if (asset == null) throw new Exception("failed to load EBX: " + entry.Name);
            return AntStateClipSource.EnumerateAllClips(asset);
        }

        private static void Fail(OutputFormatter formatter, string op, string file, string ebx, string error)
        {
            var f = new Dictionary<string, string>();
            f["file"]  = file ?? "";
            f["ebx"]   = ebx ?? "";
            f["error"] = error;
            formatter.WriteResult("FAIL", op, f);
        }
    }

    // Batch form: enumerate every hero strike in the state machine and write "<FullName>.seanim"
    // per clip into --out <dir>.
    public class ExportAnimBatchCommand : ICommand
    {
        public string Name => "export-anim-batch";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath  = ExportAnimCommand.AntStateClipSource_DefaultStateMachine;
            string outDir   = null;
            string skeleton = AnimSkeleton.DefaultSkeletonPath;
            string filter   = null;   // only export clips whose name contains this (per-hero rigs)
            bool   quiet    = false;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--out"      && i + 1 < args.Length) outDir   = args[++i];
                else if (args[i] == "--skeleton" && i + 1 < args.Length) skeleton = args[++i];
                else if (args[i] == "--filter"   && i + 1 < args.Length) filter   = args[++i];
                else if (args[i] == "--quiet") quiet = true;
                else if (!args[i].StartsWith("--")) ebxPath = args[i];
            }

            if (outDir == null) { Console.Error.WriteLine("export-anim-batch: missing required --out <dir>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("export-anim-batch: AssetManager not initialized. Pass --game-path."); return 1; }

            List<string> boneNames;
            List<StrikeClip> strikes;
            try
            {
                boneNames = AnimSkeleton.ReadBoneNames(skeleton);
                strikes = ExportAnimCommand.LoadStrikes(ebxPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("export-anim-batch: " + ex.Message);
                logger.LogError("export-anim-batch load failed: {0}", ex);
                return 1;
            }

            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            IClipChannelDecoder decoder = ExportAnimCommand.BuildDecoder(logger);
            int passed = 0, failed = 0, decodedCount = 0, skipped = 0;
            try
            {
                foreach (StrikeClip clip in strikes)
                {
                    if (filter != null && clip.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        bool decoded;
                        SeAnim anim = AnimExporter.BuildAnim(clip, boneNames, decoder, out decoded);
                        string outPath = Path.Combine(outDir, AnimExporter.SanitizeFileName(clip.FullName) + ".seanim");
                        if (!decoded)
                        {
                            // No usable humanoid keyframes: a compressed codec (DCT) or a clip authored
                            // for a different rig (e.g. Grievous). Skip cleanly — and remove any stale
                            // file from a previous export so the output dir holds only valid SEAnims.
                            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                            if (!quiet) Console.WriteLine("  SKIP " + clip.FullName + " (no humanoid keyframes)");
                            skipped++;
                            continue;
                        }
                        SeAnimWriter.WriteToFile(anim, outPath);
                        decodedCount++;
                        if (!quiet) Console.WriteLine("  PASS " + clip.FullName
                            + (string.IsNullOrEmpty(decoder.LastClipName) ? "" : "  <- " + decoder.LastClipName));
                        passed++;
                    }
                    catch (Exception ex)
                    {
                        if (!quiet) Console.WriteLine("  FAIL " + clip.FullName + ": " + ex.Message);
                        failed++;
                    }
                }
            }
            finally { (decoder as IDisposable)?.Dispose(); }

            Console.WriteLine("Export complete: " + passed + " passed, " + failed + " failed, "
                + skipped + " skipped (no humanoid keyframes), " + decodedCount + " with keyframes");
            return failed == 0 ? 0 : 1;
        }
    }
}
