using System.IO;
using System.Text;

namespace FrostyCLI.Core.Animation
{
    // Serializes a SeAnim to the standard SEAnim (.seanim) byte layout (DTZxPorter SE Animation
    // spec). The byte layout is verified against FAAE's SeAnimParser, the consumer's source of
    // truth (see SeAnimWriterTests, which round-trips through a vendored copy of that parser).
    //
    // Layout (little-endian): magic "SEAnim", uint16 version, uint16 headerSize, byte animType,
    // byte flags, byte dataPresenceFlags (0x01 loc / 0x02 rot / 0x04 scale), byte dataPropertyFlags
    // (0x01 = double precision), 2 reserved, float framerate, uint32 frameCount, uint32 boneCount,
    // byte boneModCount, 3 reserved, uint32 noteCount, null-terminated bone names, bone-mod entries,
    // then per-bone: byte flags + (if present) loc/rot/scale key lists. Frame- and bone-index integer
    // sizes are dynamic (1/2/4 bytes) based on the frame/bone counts.
    public static class SeAnimWriter
    {
        private const ushort Version    = 0x1;
        private const ushort HeaderSize = 0x1C;

        public static void WriteToFile(SeAnim anim, string path)
        {
            File.WriteAllBytes(path, Write(anim));
        }

        public static byte[] Write(SeAnim anim)
        {
            bool hasLoc = false, hasRot = false, hasScale = false;
            for (int i = 0; i < anim.Bones.Count; i++)
            {
                SeAnimBone b = anim.Bones[i];
                if (b.PosKeys.Count   > 0) hasLoc   = true;
                if (b.RotKeys.Count   > 0) hasRot   = true;
                if (b.ScaleKeys.Count > 0) hasScale = true;
            }

            byte presence = 0;
            if (hasLoc)   presence |= 0x01;
            if (hasRot)   presence |= 0x02;
            if (hasScale) presence |= 0x04;

            byte propertyFlags = (byte)(anim.HighPrecision ? 0x01 : 0x00);

            int boneCount  = anim.Bones.Count;
            int frameCount = anim.FrameCount;
            int frameSize  = DynIntSize(frameCount);
            int boneSize   = DynIntSize(boneCount);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.ASCII))
            {
                w.Write(Encoding.ASCII.GetBytes("SEAnim")); // 6 bytes, no terminator
                w.Write(Version);
                w.Write(HeaderSize);
                w.Write((byte)anim.AnimType);
                w.Write((byte)0);          // flags
                w.Write(presence);
                w.Write(propertyFlags);
                w.Write((byte)0); w.Write((byte)0);            // 2 reserved
                w.Write(anim.Framerate);
                w.Write((uint)frameCount);
                w.Write((uint)boneCount);
                w.Write((byte)0);                              // boneModCount
                w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); // 3 reserved
                w.Write((uint)0);                              // noteCount

                for (int i = 0; i < boneCount; i++)
                {
                    w.Write(Encoding.UTF8.GetBytes(anim.Bones[i].Name ?? ""));
                    w.Write((byte)0);
                }

                // (no bone-mod entries)

                for (int i = 0; i < boneCount; i++)
                {
                    SeAnimBone bone = anim.Bones[i];
                    w.Write((byte)0); // per-bone flags

                    if (hasLoc)
                    {
                        WriteDynInt(w, bone.PosKeys.Count, frameSize);
                        for (int k = 0; k < bone.PosKeys.Count; k++)
                        {
                            SePosKey key = bone.PosKeys[k];
                            WriteDynInt(w, key.Frame, frameSize);
                            WriteVec3(w, key.Position, anim.HighPrecision);
                        }
                    }
                    if (hasRot)
                    {
                        WriteDynInt(w, bone.RotKeys.Count, frameSize);
                        for (int k = 0; k < bone.RotKeys.Count; k++)
                        {
                            SeRotKey key = bone.RotKeys[k];
                            WriteDynInt(w, key.Frame, frameSize);
                            WriteQuat(w, key.Rotation, anim.HighPrecision);
                        }
                    }
                    if (hasScale)
                    {
                        WriteDynInt(w, bone.ScaleKeys.Count, frameSize);
                        for (int k = 0; k < bone.ScaleKeys.Count; k++)
                        {
                            SeScaleKey key = bone.ScaleKeys[k];
                            WriteDynInt(w, key.Frame, frameSize);
                            WriteVec3(w, key.Scale, anim.HighPrecision);
                        }
                    }
                }

                w.Flush();
                return ms.ToArray();
            }
        }

        // SEAnim dynamic integer width: 1 byte for <=255, 2 for <=65535, else 4.
        private static int DynIntSize(int count)
        {
            return count <= 255 ? 1 : count <= 65535 ? 2 : 4;
        }

        private static void WriteDynInt(BinaryWriter w, int value, int size)
        {
            if (size == 1) w.Write((byte)value);
            else if (size == 2) w.Write((ushort)value);
            else w.Write((uint)value);
        }

        private static void WriteVec3(BinaryWriter w, SeVec3 v, bool highPrec)
        {
            if (highPrec) { w.Write((double)v.X); w.Write((double)v.Y); w.Write((double)v.Z); }
            else          { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        }

        // Quaternion components written in (x, y, z, w) order.
        private static void WriteQuat(BinaryWriter w, SeQuat q, bool highPrec)
        {
            if (highPrec) { w.Write((double)q.X); w.Write((double)q.Y); w.Write((double)q.Z); w.Write((double)q.W); }
            else          { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
        }
    }
}
