using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Commands
{
    // Exports a facial-layer clip (e.g. Face_Idle_01, Add_Breathing_Face, E_<hero>_*) to SEAnim with
    // real FACIAL_LOD1_* keyframes. The clip stores FACS action-unit weights; this blends the bank's
    // FacePoseLibrary (quantized per-pose joint deltas) onto the skeleton's neutral facial pose.
    // See memory/faceposer-facs-decode.md.
    public class ExportFaceCommand : ICommand
    {
        public string Name => "export-face";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string clipName = null, outPath = null, skeleton = AnimSkeleton.DefaultSkeletonPath, libName = null;
            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--clip"     && i + 1 < args.Length) clipName = args[++i];
                else if (args[i] == "--out"      && i + 1 < args.Length) outPath  = args[++i];
                else if (args[i] == "--skeleton" && i + 1 < args.Length) skeleton = args[++i];
                else if (args[i] == "--library"  && i + 1 < args.Length) libName  = args[++i];
                else if (!args[i].StartsWith("--") && clipName == null) clipName = args[i];
            }
            if (clipName == null) { Console.Error.WriteLine("export-face: missing --clip <facial-clip-name>"); return 1; }
            if (outPath == null)  { Console.Error.WriteLine("export-face: missing --out <file.seanim>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("export-face: AssetManager not initialized. Pass --game-path."); return 1; }

            List<string> boneNames;
            Dictionary<string, AnimSkeleton.BindLocal> bind;
            try { boneNames = AnimSkeleton.ReadBoneNames(skeleton); bind = AnimSkeleton.ReadBindPose(skeleton); }
            catch (Exception ex) { Fail(formatter, outPath, clipName, ex.Message); return 1; }

            var bindRot = new Dictionary<string, SeQuat>(StringComparer.OrdinalIgnoreCase);
            var bindTrans = new Dictionary<string, SeVec3>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in bind) { bindRot[kv.Key] = kv.Value.Rot; bindTrans[kv.Key] = kv.Value.Trans; }

            Stream s = ExportAnimCommand.OpenRes(ExportAnimCommand.SharedAnimBankRes);
            if (s == null) { Fail(formatter, outPath, clipName, "shared anim bank not found"); return 1; }

            try
            {
                using (var dec = new AntAnimationDecoder(s))
                {
                    AntAnimationDecoder.FacePoseLib lib = dec.BuildFacePoseLib(libName);
                    if (lib == null) { Fail(formatter, outPath, clipName, "FacePoseLibrary not found in bank"); return 1; }

                    var anim = new SeAnim { AnimType = SeAnimType.Absolute, Framerate = 30f };
                    foreach (string n in boneNames) anim.Bones.Add(new SeAnimBone { Name = n });
                    bool ok = dec.DecodeFaceClip(clipName, anim, lib, bindRot, bindTrans);
                    if (!ok) { Fail(formatter, outPath, clipName, "facial clip not found or no FACS weights"); return 1; }

                    string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    SeAnimWriter.WriteToFile(anim, outPath);

                    int facialKeyed = 0;
                    foreach (var b in anim.Bones)
                        if (b.Name.StartsWith("FACIAL", StringComparison.OrdinalIgnoreCase) && (b.RotKeys.Count > 0 || b.PosKeys.Count > 0)) facialKeyed++;

                    var f = new Dictionary<string, string>
                    {
                        ["file"] = outPath, ["clip"] = clipName,
                        ["frames"] = anim.FrameCount.ToString(), ["facialBonesKeyed"] = facialKeyed.ToString(),
                    };
                    formatter.WriteResult("OK", "export-face", f);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Fail(formatter, outPath, clipName, ex.Message);
                logger.LogError("export-face failed: {0}", ex);
                return 1;
            }
        }

        private static void Fail(OutputFormatter formatter, string file, string clip, string error)
        {
            formatter.WriteResult("FAIL", "export-face", new Dictionary<string, string>
            { ["file"] = file ?? "", ["clip"] = clip ?? "", ["error"] = error });
        }
    }
}
