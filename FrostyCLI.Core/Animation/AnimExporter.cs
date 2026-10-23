using System.Collections.Generic;
using System.IO;

namespace FrostyCLI.Core.Animation
{
    // Orchestrates building a SEAnim for one strike clip: creates one bone track per skeleton bone
    // (names/order matching the skeleton FAAE binds against), then asks the channel decoder to fill
    // keyframes. The decoder already yields local transforms in the EBX skeleton's LocalPose frame
    // (Y-up, meters, Absolute): constant-translation bones decode to exactly their EBX bind offset
    // and the rotation quaternions feed FAAE's QuatToMatrix directly, so no further conversion is
    // applied here. (Earlier builds basis-swapped/retargeted to compensate for a channel-mapping bug
    // that is now fixed in AntAnimationDecoder via the clip's MappingIndices permutation.)
    public static class AnimExporter
    {
        // Builds the in-memory SEAnim. `decoded` reports whether the decoder produced keyframes.
        public static SeAnim BuildAnim(
            StrikeClip clip,
            IList<string> boneNames,
            IClipChannelDecoder decoder,
            out bool decoded)
        {
            var anim = new SeAnim { AnimType = SeAnimType.Absolute, Framerate = 30f };
            for (int i = 0; i < boneNames.Count; i++)
                anim.Bones.Add(new SeAnimBone { Name = boneNames[i] });

            var request = new ClipDecodeRequest
            {
                ClipName = clip.FullName,
                ClipOffset = clip.ClipOffset,
                ClipLength = clip.ClipLength,
                ControllerAssetGuid = clip.ControllerAssetGuid,
                SkeletonBoneNames = AsReadOnly(boneNames),
            };
            decoded = decoder.TryDecode(request, anim);
            return anim;
        }

        // Builds and writes "<outDir>/<FullName>.seanim", returning the path written.
        public static string ExportToDir(
            StrikeClip clip,
            IList<string> boneNames,
            IClipChannelDecoder decoder,
            string outDir,
            out bool decoded)
        {
            SeAnim anim = BuildAnim(clip, boneNames, decoder, out decoded);
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, SanitizeFileName(clip.FullName) + ".seanim");
            SeAnimWriter.WriteToFile(anim, outPath);
            return outPath;
        }

        // FAAE uses the filename (no extension) as the animation's display name, so keep the
        // clip name intact apart from characters illegal in a filename.
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "anim";
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = false;
                for (int i = 0; i < invalid.Length; i++) if (invalid[i] == c) { bad = true; break; }
                sb.Append(bad ? '_' : c);
            }
            return sb.ToString();
        }

        private static IReadOnlyList<string> AsReadOnly(IList<string> list)
        {
            var arr = new string[list.Count];
            list.CopyTo(arr, 0);
            return arr;
        }
    }
}
