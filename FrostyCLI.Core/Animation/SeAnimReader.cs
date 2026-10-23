using System.IO;
using System.Text;

namespace FrostyCLI.Core.Animation
{
    // Reads a standard SEAnim (.seanim) file into a SeAnim. Mirrors FAAE's SeAnimParser (the
    // inverse of SeAnimWriter). Used by import-anim to bring an external animation into the game.
    public static class SeAnimReader
    {
        public static SeAnim Read(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var r = new BinaryReader(fs))
                return Read(r);
        }

        public static SeAnim Read(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var r = new BinaryReader(ms))
                return Read(r);
        }

        public static SeAnim Read(BinaryReader r)
        {
            byte[] magic = r.ReadBytes(6);
            string m = Encoding.ASCII.GetString(magic);
            if (m != "SEAnim") throw new InvalidDataException("Not a SEAnim file");

            r.ReadUInt16(); // version
            r.ReadUInt16(); // header size
            byte animType = r.ReadByte();
            r.ReadByte();   // flags
            byte presence = r.ReadByte();
            byte property = r.ReadByte();
            r.ReadBytes(2); // reserved

            var anim = new SeAnim { AnimType = (SeAnimType)animType };
            bool hasLoc = (presence & 0x01) != 0;
            bool hasRot = (presence & 0x02) != 0;
            bool hasScale = (presence & 0x04) != 0;
            bool highPrec = (property & 0x01) != 0;

            anim.Framerate = r.ReadSingle();
            anim.FrameCount = (int)r.ReadUInt32();
            int boneCount = (int)r.ReadUInt32();
            byte boneModCount = r.ReadByte();
            r.ReadBytes(3);
            int noteCount = (int)r.ReadUInt32();
            anim.HighPrecision = highPrec;

            int frameSize = anim.FrameCount <= 255 ? 1 : anim.FrameCount <= 65535 ? 2 : 4;
            int boneSize = boneCount <= 255 ? 1 : boneCount <= 65535 ? 2 : 4;

            for (int i = 0; i < boneCount; i++)
                anim.Bones.Add(new SeAnimBone { Name = ReadNullTerm(r) });

            for (int i = 0; i < boneModCount; i++) { ReadDynInt(r, boneSize); r.ReadByte(); }

            for (int b = 0; b < boneCount; b++)
            {
                SeAnimBone bone = anim.Bones[b];
                r.ReadByte(); // per-bone flags

                if (hasLoc)
                {
                    int n = ReadDynInt(r, frameSize);
                    for (int k = 0; k < n; k++)
                    {
                        int frame = ReadDynInt(r, frameSize);
                        var v = ReadVec3(r, highPrec);
                        bone.PosKeys.Add(new SePosKey { Frame = frame, Position = v });
                    }
                }
                if (hasRot)
                {
                    int n = ReadDynInt(r, frameSize);
                    for (int k = 0; k < n; k++)
                    {
                        int frame = ReadDynInt(r, frameSize);
                        float x = ReadF(r, highPrec), y = ReadF(r, highPrec), z = ReadF(r, highPrec), w = ReadF(r, highPrec);
                        bone.RotKeys.Add(new SeRotKey { Frame = frame, Rotation = new SeQuat(x, y, z, w) });
                    }
                }
                if (hasScale)
                {
                    int n = ReadDynInt(r, frameSize);
                    for (int k = 0; k < n; k++)
                    {
                        int frame = ReadDynInt(r, frameSize);
                        var v = ReadVec3(r, highPrec);
                        bone.ScaleKeys.Add(new SeScaleKey { Frame = frame, Scale = v });
                    }
                }
            }
            return anim;
        }

        private static SeVec3 ReadVec3(BinaryReader r, bool hp) { return new SeVec3(ReadF(r, hp), ReadF(r, hp), ReadF(r, hp)); }
        private static float ReadF(BinaryReader r, bool hp) { return hp ? (float)r.ReadDouble() : r.ReadSingle(); }
        private static int ReadDynInt(BinaryReader r, int size) { return size == 1 ? r.ReadByte() : size == 2 ? r.ReadUInt16() : (int)r.ReadUInt32(); }
        private static string ReadNullTerm(BinaryReader r) { var sb = new StringBuilder(); byte c; while ((c = r.ReadByte()) != 0) sb.Append((char)c); return sb.ToString(); }
    }
}
