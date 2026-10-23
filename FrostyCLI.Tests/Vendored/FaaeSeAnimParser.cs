using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// VENDORED VERBATIM from FAAE (C:\Users\hartz\FAAE\src\Animation\SeAnimParser.cs and
// SeAnimData.cs) as the byte-layout oracle for SeAnimWriter tests. FAAE is the consumer
// and the source of truth for the SEAnim contract; if FAAE's parser changes, update this
// copy and the writer together. Do not edit to make tests pass — fix the writer instead.
namespace FrostyCLI.Tests.Vendored
{
    public enum SeAnimType : byte { Absolute = 0, Additive = 1, Relative = 2, Delta = 3 }

    public struct Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public struct Quat
    {
        public float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
    }

    public struct PosKey { public int Frame; public Vec3 Position; }
    public struct RotKey { public int Frame; public Quat Rotation; }
    public struct ScaleKey { public int Frame; public Vec3 Scale; }

    public class SeAnimBone
    {
        public string Name { get; set; }
        public byte Flags { get; set; }
        public List<PosKey> PosKeys { get; set; } = new List<PosKey>();
        public List<RotKey> RotKeys { get; set; } = new List<RotKey>();
        public List<ScaleKey> ScaleKeys { get; set; } = new List<ScaleKey>();
    }

    public class SeAnimFile
    {
        public SeAnimType AnimType { get; set; }
        public bool HasLocation { get; set; }
        public bool HasRotation { get; set; }
        public bool HasScale { get; set; }
        public float Framerate { get; set; }
        public int FrameCount { get; set; }
        public List<SeAnimBone> Bones { get; set; } = new List<SeAnimBone>();

        public SeAnimBone GetBone(string name)
        {
            for (int i = 0; i < Bones.Count; i++)
                if (string.Equals(Bones[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return Bones[i];
            return null;
        }
    }

    public static class SeAnimParser
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SEAnim");

        public static SeAnimFile Parse(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            using (var reader = new BinaryReader(stream))
                return Parse(reader);
        }

        public static SeAnimFile Parse(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            using (var reader = new BinaryReader(stream))
                return Parse(reader);
        }

        public static SeAnimFile Parse(BinaryReader reader)
        {
            var anim = new SeAnimFile();
            byte[] magic = reader.ReadBytes(6);
            for (int i = 0; i < 6; i++)
                if (magic[i] != Magic[i]) throw new InvalidDataException("Not a valid SEAnim file");

            reader.ReadUInt16(); // version
            reader.ReadUInt16(); // header size
            byte animType = reader.ReadByte();
            reader.ReadByte(); // flags
            byte dataPresenceFlags = reader.ReadByte();
            byte dataPropertyFlags = reader.ReadByte();
            reader.ReadBytes(2); // reserved

            anim.AnimType = (SeAnimType)animType;
            anim.HasLocation = (dataPresenceFlags & 0x01) != 0;
            anim.HasRotation = (dataPresenceFlags & 0x02) != 0;
            anim.HasScale = (dataPresenceFlags & 0x04) != 0;
            bool highPrec = (dataPropertyFlags & 0x01) != 0;

            anim.Framerate = reader.ReadSingle();
            anim.FrameCount = (int)reader.ReadUInt32();
            int boneCount = (int)reader.ReadUInt32();
            byte boneModCount = reader.ReadByte();
            reader.ReadBytes(3);
            int noteCount = (int)reader.ReadUInt32();

            int frameSize = anim.FrameCount <= 255 ? 1 : anim.FrameCount <= 65535 ? 2 : 4;
            int boneSize = boneCount <= 255 ? 1 : boneCount <= 65535 ? 2 : 4;

            for (int i = 0; i < boneCount; i++)
                anim.Bones.Add(new SeAnimBone { Name = ReadNullTerm(reader) });

            for (int i = 0; i < boneModCount; i++)
            {
                ReadDynInt(reader, boneSize);
                reader.ReadByte(); // mod type
            }

            for (int b = 0; b < boneCount; b++)
            {
                var bone = anim.Bones[b];
                bone.Flags = reader.ReadByte();

                if (anim.HasLocation)
                {
                    int n = ReadDynInt(reader, frameSize);
                    for (int k = 0; k < n; k++)
                    {
                        int frame = ReadDynInt(reader, frameSize);
                        Vec3 pos = highPrec
                            ? new Vec3((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble())
                            : new Vec3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        bone.PosKeys.Add(new PosKey { Frame = frame, Position = pos });
                    }
                }
                if (anim.HasRotation)
                {
                    int n = ReadDynInt(reader, frameSize);
                    for (int k = 0; k < n; k++)
                    {
                        int frame = ReadDynInt(reader, frameSize);
                        Quat rot = highPrec
                            ? new Quat((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble())
                            : new Quat(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        bone.RotKeys.Add(new RotKey { Frame = frame, Rotation = rot });
                    }
                }
                if (anim.HasScale)
                {
                    int n = ReadDynInt(reader, frameSize);
                    for (int k = 0; k < n; k++)
                    {
                        int frame = ReadDynInt(reader, frameSize);
                        Vec3 scale = highPrec
                            ? new Vec3((float)reader.ReadDouble(), (float)reader.ReadDouble(), (float)reader.ReadDouble())
                            : new Vec3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        bone.ScaleKeys.Add(new ScaleKey { Frame = frame, Scale = scale });
                    }
                }
            }
            return anim;
        }

        private static string ReadNullTerm(BinaryReader r)
        {
            var sb = new StringBuilder();
            byte b;
            while ((b = r.ReadByte()) != 0) sb.Append((char)b);
            return sb.ToString();
        }

        private static int ReadDynInt(BinaryReader r, int size) =>
            size == 1 ? r.ReadByte() : size == 2 ? r.ReadUInt16() : (int)r.ReadUInt32();
    }
}
