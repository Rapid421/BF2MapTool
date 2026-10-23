using System.Collections.Generic;

namespace FrostyCLI.Core.Animation
{
    // SEAnim animation type (DTZxPorter SE Animation spec). Matches FAAE's SeAnimType byte values.
    public enum SeAnimType : byte
    {
        Absolute = 0,
        Additive = 1,
        Relative = 2,
        Delta    = 3,
    }

    public struct SeVec3
    {
        public float X, Y, Z;
        public SeVec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    // Quaternion. SEAnim writes components in (x, y, z, w) order.
    public struct SeQuat
    {
        public float X, Y, Z, W;
        public SeQuat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static SeQuat Identity { get { return new SeQuat(0f, 0f, 0f, 1f); } }
    }

    public struct SePosKey   { public int Frame; public SeVec3 Position; }
    public struct SeRotKey   { public int Frame; public SeQuat Rotation; }
    public struct SeScaleKey { public int Frame; public SeVec3 Scale; }

    // One bone track. Name must match the Frostbite skeleton bone name FAAE binds against
    // (the EBX BoneNames entry), case-insensitively.
    public sealed class SeAnimBone
    {
        public string Name;
        public readonly List<SePosKey>   PosKeys   = new List<SePosKey>();
        public readonly List<SeRotKey>   RotKeys   = new List<SeRotKey>();
        public readonly List<SeScaleKey> ScaleKeys = new List<SeScaleKey>();
    }

    // In-memory SEAnim, ready to be serialized by SeAnimWriter. Presence flags
    // (loc/rot/scale) are derived from whether any bone carries keys of that kind.
    public sealed class SeAnim
    {
        public SeAnimType AnimType = SeAnimType.Absolute;
        public float Framerate = 30f;
        public int FrameCount;
        // When true, vectors/quaternions are written as doubles (dataPropertyFlags 0x01).
        public bool HighPrecision;
        public readonly List<SeAnimBone> Bones = new List<SeAnimBone>();
    }
}
