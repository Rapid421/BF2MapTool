using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FrostySdk.IO;

namespace FrostyCLI.Core.Animation
{
    // Reads the Frostbite "GD" asset-bank container that backs antstate resources
    // (res type 1369688147). Faithful port of the editor's AnimationEditorPlugin Bank/Section
    // reader (which only ever parsed the reflection/schema sections). This reads the STRM header
    // and the REFL/REF2 reflection sections into bank "class" schemas (named, typed field tables) —
    // the vocabulary needed to interpret the DAT2 data sections where clip keyframes live.
    //
    // Container layout: uint32 BE version (==3), uint32 BE offset -> section table at offset+4;
    // each section = 7-char tag ("GD.STRM"/"GD.REFL"/"GD.REF2"/"GD.DATA"/"GD.DAT2") + 1 endian
    // byte ('b'=big) + int32 size. STRM gives the largest section size; REFL/REF2 hold the schema.

    public enum BankType
    {
        Invalid, Boolean, Int8, UInt8, Int16, Uint16, Int32, UInt32, Int64, UInt64,
        Float, Vector2, Vector3, Vector4, Quaternion, Matrix, Guid, String, Reference, Double
    }

    public sealed class AntBankField
    {
        public string Name;
        public int Index;
        public int Size;
        public int Position;
        public int NameOffset;
        public bool IsArray;
        public BankType TypeHash;
        public AntBank Bank; // nested bank (struct field), or null
        public override string ToString() { return (Name ?? "?") + ":" + TypeHash + (IsArray ? "[]" : ""); }
    }

    public sealed class AntBank
    {
        public string Name;
        public BankType Type;
        public int Size;
        public int Alignment;
        public AntBankField[] Fields = new AntBankField[0];
        public override string ToString() { return Name ?? "<unnamed>"; }
    }

    public sealed class AntBankContainer
    {
        public uint Version;
        public string SchemaFormat; // "REFL" or "REF2"
        public Dictionary<BankType, AntBank> Banks = new Dictionary<BankType, AntBank>();
        // Stream position where the data sections (GD.DATA/GD.DAT2 instances) begin,
        // i.e. just past the reflection section.
        public long DataSectionStart;
    }

    public static class AntBankReader
    {
        public static AntBankContainer ParseSchema(Stream stream)
        {
            var reader = new NativeReader(stream);
            var result = new AntBankContainer();

            result.Version = reader.ReadUInt(Endian.Big);
            if (result.Version != 3)
                throw new InvalidDataException("Unexpected GD bank version " + result.Version + " (expected 3).");

            uint tableOffset = reader.ReadUInt(Endian.Big);
            reader.Position = tableOffset + 4;

            SectionHeader header = SectionHeader.Read(reader);
            if (header.Format != SectionFormat.STRM)
                throw new InvalidDataException("Expected GD.STRM first, got " + header.Format);
            // STRM body: largest section size (skip).
            reader.ReadUInt(header.Endian);

            header = SectionHeader.Read(reader);
            if (header.Format == SectionFormat.REFL)
            {
                result.SchemaFormat = "REFL";
                result.Banks = ReadBanks(reader, header, bankVersion: 1);
            }
            else if (header.Format == SectionFormat.REF2)
            {
                result.SchemaFormat = "REF2";
                result.Banks = ReadBanks(reader, header, bankVersion: 2);
            }
            else
            {
                throw new InvalidDataException("Expected GD.REFL/REF2 after STRM, got " + header.Format);
            }
            result.DataSectionStart = reader.Position; // ReadBanks leaves the reader at the section end
            return result;
        }

        private static Dictionary<BankType, AntBank> ReadBanks(NativeReader reader, SectionHeader header, int bankVersion)
        {
            Endian endian = header.Endian;
            long sectionEnd = header.ReaderPosition + (header.Size - 12);

            if (bankVersion == 1) reader.ReadUInt(endian); // REFL: leading size field
            long basePos = reader.Position;
            ulong count = reader.ReadULong(endian);

            var byKey = new Dictionary<long, AntBank>();
            var order = new List<long>();
            for (ulong i = 0; i < count; i++)
            {
                long key;
                if (bankVersion == 1)
                {
                    key = reader.ReadLong(endian);
                }
                else
                {
                    // Frostbite tagged pointer: base offset is the pointer's own position (captured
                    // BEFORE reading the 8-byte value), not the position after.
                    long ptrPos = reader.Position - basePos;
                    long raw = reader.ReadLong(Endian.Little);
                    key = ResolvePointer(ptrPos, raw);
                }
                if (!byKey.ContainsKey(key)) { byKey[key] = new AntBank(); order.Add(key); }
            }

            var byType = new Dictionary<BankType, AntBank>();
            foreach (long key in order)
            {
                reader.Position = basePos + key;
                ReadBank(reader, endian, basePos, byKey, bankVersion, byKey[key]);
                byType[byKey[key].Type] = byKey[key];
            }

            reader.Position = sectionEnd;
            return byType;
        }

        private static void ReadBank(NativeReader reader, Endian endian, long basePos,
            Dictionary<long, AntBank> byKey, int bankVersion, AntBank bank)
        {
            int minEntry = reader.ReadInt(endian);
            int maxEntry = reader.ReadInt(endian);
            bank.Size = reader.ReadInt(endian);
            bank.Alignment = reader.ReadInt(endian);
            reader.ReadUInt(endian); // unknown
            int nameTableSize = reader.ReadInt(endian);
            reader.ReadBoolean();
            reader.ReadBoolean();
            reader.ReadUShort(endian);
            bank.Type = (BankType)reader.ReadUInt(endian);

            int num = maxEntry - minEntry + 1;
            if (num < 0 || num > 100000) num = 0; // guard against a misparse
            var fields = new AntBankField[num];
            for (int i = 0; i < num; i++)
            {
                var f = new AntBankField();
                f.TypeHash = (BankType)reader.ReadUInt(endian);
                f.Size = reader.ReadInt(endian);
                f.Position = reader.ReadInt(endian);
                f.NameOffset = reader.ReadInt(endian);
                f.Index = minEntry + i;
                reader.ReadUShort(endian);
                f.IsArray = reader.ReadUShort(endian) != 0;
                reader.ReadUShort(endian);
                reader.ReadShort(endian);
                fields[i] = f;

                if (bankVersion == 1)
                {
                    long b = reader.ReadLong(endian);
                    if (b != 0 && byKey.ContainsKey(b)) f.Bank = byKey[b];
                }
                else
                {
                    long ptrPos = reader.Position - basePos;
                    long raw = reader.ReadLong(endian);
                    long pos = ResolvePointer(ptrPos, raw);
                    if (pos != 0 && byKey.ContainsKey(pos)) f.Bank = byKey[pos];
                }
            }

            byte[] nameTable = reader.ReadBytes(nameTableSize);
            bank.Name = GetName(nameTable, 1);
            for (int j = 0; j < num; j++)
                fields[j].Name = GetName(nameTable, fields[j].NameOffset);
            bank.Fields = fields;
        }

        // Frostbite tagged pointer: top 4 bits select base (1 = relative to reader pos, 0 = absolute),
        // low 60 bits = offset.
        private static long ResolvePointer(long readerPosition, long offset)
        {
            long low = offset & 0x0FFFFFFFFFFFFFFFL;
            int tag = (int)((ulong)offset >> 60);
            if (tag == 1) return readerPosition + low;
            if (tag == 0) return low;
            return 0;
        }

        private static string GetName(byte[] table, int offset)
        {
            if (table == null || offset < 0 || offset >= table.Length) return "";
            int i = offset;
            while (i < table.Length && table[i] != 0) i++;
            return Encoding.ASCII.GetString(table, offset, i - offset);
        }
    }

    internal enum SectionFormat { None, STRM, REFL, REF2, DATA, DAT2 }

    internal sealed class SectionHeader
    {
        public SectionFormat Format;
        public Endian Endian;
        public int Size;
        public long ReaderPosition;

        public static SectionHeader Read(NativeReader reader)
        {
            var h = new SectionHeader();
            string tag = reader.ReadSizedString(7);
            switch (tag)
            {
                case "GD.STRM": h.Format = SectionFormat.STRM; break;
                case "GD.REFL": h.Format = SectionFormat.REFL; break;
                case "GD.REF2": h.Format = SectionFormat.REF2; break;
                case "GD.DATA": h.Format = SectionFormat.DATA; break;
                case "GD.DAT2": h.Format = SectionFormat.DAT2; break;
                default:        h.Format = SectionFormat.None; break;
            }
            h.Endian = reader.ReadSizedString(1) == "b" ? Endian.Big : Endian.Little;
            h.Size = reader.ReadInt(h.Endian);
            h.ReaderPosition = reader.BaseStream.Position;
            return h;
        }
    }
}
