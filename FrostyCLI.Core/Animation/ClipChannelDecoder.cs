using System;
using System.Collections.Generic;
using System.IO;

namespace FrostyCLI.Core.Animation
{
    // Everything needed to decode one clip's packed per-bone channels into keyframes.
    // The packed Frostbite "Ant" channel format (compiled into the antstate resource bank's
    // GD.DAT2 sections, indexed by ClipOffset) is not yet reverse-engineered, so the only
    // decoder today is UnimplementedClipChannelDecoder.
    public sealed class ClipDecodeRequest
    {
        // Strike clip name (e.g. "Vader_Strike_01"), for diagnostics/filename.
        public string ClipName;
        // _ClipOffset from the CharacterStateClipControllerData — offset into the Ant clip data.
        public uint ClipOffset;
        // _Length from the controller (clip length; units to be confirmed during channel RE).
        public uint ClipLength;
        // AntRef.AssetGuid of the controller — identifies the antstate proxy/bank holding the clip.
        public Guid ControllerAssetGuid;
        // Ordered skeleton bone names (the channel-index -> bone mapping target). The decoder
        // emits tracks whose names match these so FAAE binds them correctly.
        public IReadOnlyList<string> SkeletonBoneNames;
    }

    // Decodes a clip's packed channels into keyframes.
    public interface IClipChannelDecoder
    {
        // Fills `anim` (its Bones are pre-created in skeleton order with matching names) with
        // decoded keyframes and sets FrameCount/Framerate/AnimType. Returns true when keyframe
        // data was produced; false when the packed channel format cannot be decoded yet (the
        // caller still writes a valid, keyframe-empty SEAnim with correct bone names/header).
        bool TryDecode(ClipDecodeRequest request, SeAnim anim);

        // Name of the source clip the last TryDecode resolved to (e.g. "A_Vader_AttackLoop_Strike1"),
        // or null if none/unimplemented. For logging which clip an export picked.
        string LastClipName { get; }
    }

    // Placeholder until the Frostbite Ant clip channel compression is reverse-engineered.
    // Produces no keyframes; the exporter emits header + bone-name section only.
    public sealed class UnimplementedClipChannelDecoder : IClipChannelDecoder
    {
        public bool TryDecode(ClipDecodeRequest request, SeAnim anim)
        {
            return false;
        }
        public string LastClipName { get { return null; } }
    }

    // Real decoder: decodes uncompressed RawAnimationAsset clips from an antstate bank via
    // AntAnimationDecoder, keyed by the strike's controller AntRef guid.
    public sealed class AntClipChannelDecoder : IClipChannelDecoder, IDisposable
    {
        private readonly AntAnimationDecoder _decoder;
        public AntClipChannelDecoder(AntAnimationDecoder decoder) { _decoder = decoder; }
        public int RigDofCount { get { return _decoder.RigDofCount; } }
        public bool TryDecode(ClipDecodeRequest request, SeAnim anim)
        {
            return _decoder.TryDecodeClip(request.ControllerAssetGuid, anim);
        }
        public string LastClipName { get { return _decoder.LastClipName; } }
        public void Dispose() { _decoder.Dispose(); }
    }
}
