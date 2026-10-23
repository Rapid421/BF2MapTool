using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Read/modify/write model of a Battlefront II HavokPhysicsData resource: the Frostbite reloc
    /// header, the stripped 32-bit and 64-bit HKX 2013 packfiles, and the trailing fixup blob that
    /// Frostbite's pipeline hoists the packfile fixup tables into.
    ///
    /// The layout was taken from the engine pipeline (HavokPipelineUtils::splitHavokResource /
    /// saveHavokResource) and checked against shipped data: a resource parsed and written back with
    /// no edits is byte-identical, and the 16-byte cookie this produces matches the res meta.
    ///
    /// The one edit offered is appending an instance to a static model group's level compounds.
    /// New instance and node arrays are appended to the 64-bit data section and the old ones left
    /// in place, so nothing before them moves; only the four array pointers, the shape-pointer
    /// fixups of the moved instance records and the section/cookie sizes change. The 32-bit
    /// packfile is carried through untouched: the PC runtime loads only the 64-bit one.
    /// </summary>
    public sealed partial class HavokPhysicsResource
    {
        private const int InstanceSize = 0x80;
        private const int NodeSize = 6;
        private static readonly byte[] Magic = { 0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10 };

        private sealed class Section
        {
            public string Name;
            public int AbsStart, FixupTotal, LocalSize, Virt, Exports, Imports, End;
            public int FixupAt;
        }

        private sealed class Pack
        {
            public int Start, End, PointerBytes, SectionCount;
            public List<Section> Sections = new List<Section>();
            public Section Data;
            public int DataBase;
            public Dictionary<int, string> Classes = new Dictionary<int, string>();
            public Section ClassNames;
            public byte[] ClassNameBytes;   // entries only, without the 0xFF tail padding
            public List<KeyValuePair<int, string>> Objects = new List<KeyValuePair<int, string>>();
            public List<int[]> LocalFixups = new List<int[]>();    // data-relative {src, dst}
            public List<int[]> GlobalFixups = new List<int[]>();   // data-relative {src, section, dst}
            public Dictionary<int, int> Pointers = new Dictionary<int, int>(); // absolute field -> absolute target
        }

        public sealed class Compound
        {
            public int Offset;          // absolute object offset in the source bytes
            public int InstanceArray;   // absolute
            public int InstanceCount;
            public int DataObject;      // absolute hknpStaticCompoundShapeData
            public int NodeArray;       // absolute
            public int NodeCount;
            public bool IsRoot;
        }

        private byte[] source;
        private int headerEnd;
        private Pack pack32, pack64;
        private int blobAt, size32, size64, relocAt;

        // Working state for edits (all offsets relative to the 64-bit data section start).
        private List<byte> data;             // object bytes (data section up to the virtual fixups)
        private byte[] virtualTail;          // virtual fixup table + exports/imports (unchanged)
        private List<int[]> local, global;
        private int originalDataLen;         // object bytes that came from the source; blocks live after it
        private List<int[]> blocks = new List<int[]>();   // appended blocks: {start, length, kind}; kind 0 = compound array/nodes, 1 = copied object
        private byte[] header;               // rebuilt reloc header
        private bool dirty;

        public List<Compound> Compounds = new List<Compound>();

        public static HavokPhysicsResource Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 0x100) throw new InvalidDataException("resource is too small");
            var r = new HavokPhysicsResource { source = bytes };
            List<int> headers = FindMagic(bytes);
            if (headers.Count != 2) throw new InvalidDataException("expected a 32-bit and a 64-bit HKX packfile, found " + headers.Count + " header(s)");
            r.headerEnd = headers[0];
            r.pack32 = r.ParsePack(headers[0]);
            r.pack64 = r.ParsePack(headers[1]);
            if (r.pack64.PointerBytes != 8) throw new InvalidDataException("second packfile is not the 64-bit layout");
            r.blobAt = Align16(r.pack64.End);
            if (r.blobAt + 8 > bytes.Length) throw new InvalidDataException("fixup blob is missing");
            r.size32 = ReadInt32(bytes, r.blobAt);
            r.size64 = ReadInt32(bytes, r.blobAt + 4);
            int fix32 = r.blobAt + 8, fix64 = fix32 + r.size32;
            r.relocAt = fix64 + r.size64;
            if (r.size32 < 0 || r.size64 < 0 || r.relocAt > bytes.Length) throw new InvalidDataException("fixup blob sizes are inconsistent");
            r.ReadFixups(r.pack32, fix32);
            r.ReadFixups(r.pack64, fix64);
            r.ReadObjects(r.pack32);
            r.ReadObjects(r.pack64);
            r.ResolvePointers(r.pack64);
            r.ReadCompounds();
            r.BeginEdit();
            return r;
        }

        /// <summary>Number of physics parts recorded in the reloc header.</summary>
        public int PartCount { get { return ReadInt32(header, 0); } }

        /// <summary>Each record's shape memory size lane (col2.w) of a compound.</summary>
        public int[] RecordShapeSizes(Compound C)
        {
            var r = new int[C.InstanceCount];
            for (int i = 0; i < r.Length; i++) r[i] = ReadInt32(source, C.InstanceArray + i * InstanceSize + 0x2c) & 0xffffff;
            return r;
        }

        /// <summary>The packfile size of each record's shape object (from the object table).</summary>
        public int[] RecordShapeObjectSizes(Compound C)
        {
            var r = new int[C.InstanceCount];
            var offsets = new List<int>();
            foreach (KeyValuePair<int, string> kv in pack64.Objects) offsets.Add(kv.Key);
            offsets.Sort();
            for (int i = 0; i < r.Length; i++)
            {
                int target;
                if (!TryGlobalTarget(C.InstanceArray + i * InstanceSize + 0x50, out target)) { r[i] = -1; continue; }
                int idx = offsets.BinarySearch(target);
                if (idx < 0) { r[i] = -1; continue; }
                r[i] = (idx + 1 < offsets.Count ? offsets[idx + 1] : pack64.DataBase + pack64.Data.Virt) - target;
            }
            return r;
        }

        // Resolves the absolute target of the global fixup whose source is the given absolute offset.
        private bool TryGlobalTarget(int absSource, out int absTarget)
        {
            absTarget = 0;
            int rel = absSource - pack64.DataBase;
            foreach (int[] g in pack64.GlobalFixups)
                if (g[0] == rel) { absTarget = pack64.Start + pack64.Sections[g[1]].AbsStart + g[2]; return true; }
            return false;
        }

        /// <summary>The reloc header's per-instance detail resource indices (member order); each
        /// indexes the group HavokAsset's ExternalAssets/Aabb lists.</summary>
        public int[] DetailResourceIndices
        {
            get
            {
                int n = ReadInt32(header, 0x34), off = ReadInt32(header, 0x38);
                var r = new int[Math.Max(0, n)];
                for (int i = 0; i < r.Length; i++) r[i] = ReadUInt16(header, off + 2 * i);
                return r;
            }
        }

        /// <summary>The level placement compound (collision slot), or null.</summary>
        public Compound Placement
        {
            get { foreach (Compound c in Compounds) if (c.IsRoot) return c; return null; }
        }

        /// <summary>Builds the 16-byte res meta (HavokResourceCookie) for the current bytes.</summary>
        public byte[] Meta
        {
            get
            {
                byte[] bytes = Write();
                int pack32Len = pack32.End - pack32.Start;
                int pack64Len = bytes.Length - header.Length - pack32Len - BlobLength();
                var meta = new byte[16];
                WriteUInt16(meta, 0, (ushort)header.Length);
                meta[2] = 0; meta[3] = 1;
                WriteInt32(meta, 4, pack32Len);
                WriteInt32(meta, 8, pack64Len);
                WriteInt32(meta, 12, BlobLength());
                return meta;
            }
        }

        /// <summary>
        /// Appends one instance to every root compound that already holds the sibling's member.
        /// The new record copies the sibling (shape, part count, flags basis) and takes the given
        /// world transform. Returns the new instance index in the placement compound.
        /// </summary>
        public int AppendInstance(int siblingIndex, LinearTransformData transform, out string detail)
        {
            Compound A = Placement;
            if (A == null) throw new InvalidOperationException("resource has no root compound");
            if (siblingIndex < 0 || siblingIndex >= A.InstanceCount)
                throw new ArgumentOutOfRangeException("siblingIndex", "sibling " + siblingIndex + " is out of range (compound holds " + A.InstanceCount + ")");

            float[] right = transform.Right ?? new[] { 1f, 0f, 0f };
            float[] up = transform.Up ?? new[] { 0f, 1f, 0f };
            float[] forward = transform.Forward ?? new[] { 0f, 0f, 1f };
            float[] trans = transform.Trans ?? new float[3];
            float[] scale = { Length(right), Length(up), Length(forward) };
            if (scale[0] <= 0 || scale[1] <= 0 || scale[2] <= 0) throw new ArgumentException("transform has a zero-length axis");
            float[][] cols = { Div(right, scale[0]), Div(up, scale[1]), Div(forward, scale[2]) };
            if (Dot(Cross(cols[0], cols[1]), cols[2]) < 0)
                throw new ArgumentException("mirrored transforms are not supported: the pipeline bakes a mirrored copy of the shape for those");

            // Sibling bookkeeping from the placement compound's current records and the current reloc
            // header (not the parsed bytes: after one append both live in the rebuilt buffers, which is
            // what lets a batch append many instances on one parsed resource).
            byte[] arrA = CurrentInstances(A);
            int sib = siblingIndex * InstanceSize;
            int tagS = ReadUInt16(arrA, sib + 0x58);
            int nextTag = siblingIndex + 1 < A.InstanceCount ? ReadUInt16(arrA, sib + InstanceSize + 0x58) : ReadInt32(header, 0);
            int partsPerInstance = nextTag - tagS;
            if (partsPerInstance <= 0) throw new InvalidDataException("sibling " + siblingIndex + " owns no physics parts");
            int detOff = ReadInt32(header, 0x38), detCount = ReadInt32(header, 0x34);
            if (siblingIndex >= detCount) throw new InvalidDataException("detail resource table is shorter than the instance array");
            int resIndex = ReadUInt16(header, detOff + 2 * siblingIndex);
            int k = siblingIndex + 1;
            while (k < A.InstanceCount && ReadUInt16(header, detOff + 2 * k) == resIndex) k++;
            int firstPart = ReadUInt16(arrA, (k - 1) * InstanceSize + 0x58) + partsPerInstance;

            // Member transforms (rotation + translation lanes only) identify the member in other slots.
            var memberKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int j = 0; j < A.InstanceCount && j < detCount; j++)
                if (ReadUInt16(header, detOff + 2 * j) == resIndex) memberKeys.Add(TransformKey(arrA, j * InstanceSize));
            string sibKey = TransformKey(arrA, sib);

            // Reloc header: grow the per-part and per-instance tables.
            GrowHeader(firstPart, tagS, partsPerInstance, k, siblingIndex);

            var report = new StringBuilder();
            foreach (Compound C in Compounds)
            {
                if (!C.IsRoot) continue;
                int si, ki;
                if (C == A) { si = siblingIndex; ki = k; }
                else
                {
                    si = -1; ki = -1;
                    byte[] cur = CurrentInstances(C);
                    for (int i = 0; i < C.InstanceCount; i++)
                    {
                        string key = TransformKey(cur, i * InstanceSize);
                        if (si < 0 && key == sibKey) si = i;
                        if (memberKeys.Contains(key)) ki = i + 1;
                    }
                    if (si < 0)
                    {
                        report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(" skipped (member absent); ");
                        continue;
                    }
                }
                AppendToCompound(C, si, ki, partsPerInstance, cols, trans, scale);
                report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(": ").Append(C.InstanceCount - 1)
                    .Append("->").Append(C.InstanceCount).Append(" instances, ").Append(C.NodeCount).Append(" nodes; ");
            }
            dirty = true;
            detail = "parts/instance " + partsPerInstance + ", first part " + firstPart + ", " + report.ToString().TrimEnd(' ', ';');
            return k;
        }

        /// <summary>
        /// Appends several instances of the sibling's member in one pass: the reloc header grows once
        /// per instance (cheap) but every compound inserts all the records together and rebuilds its
        /// tree once, which is what makes a few thousand placements take minutes instead of hours.
        /// Byte-for-byte the same result as calling AppendInstance for each transform in order.
        /// </summary>
        public int AppendInstances(int siblingIndex, IList<LinearTransformData> transforms, out string detail)
        {
            if (transforms == null || transforms.Count == 0) { detail = "nothing to append"; return -1; }
            if (transforms.Count == 1) return AppendInstance(siblingIndex, transforms[0], out detail);
            Compound A = Placement;
            if (A == null) throw new InvalidOperationException("resource has no root compound");
            if (siblingIndex < 0 || siblingIndex >= A.InstanceCount)
                throw new ArgumentOutOfRangeException("siblingIndex", "sibling " + siblingIndex + " is out of range (compound holds " + A.InstanceCount + ")");

            var colsList = new List<float[][]>(transforms.Count);
            var transList = new List<float[]>(transforms.Count);
            var scaleList = new List<float[]>(transforms.Count);
            foreach (LinearTransformData transform in transforms)
            {
                float[] right = transform.Right ?? new[] { 1f, 0f, 0f };
                float[] up = transform.Up ?? new[] { 0f, 1f, 0f };
                float[] forward = transform.Forward ?? new[] { 0f, 0f, 1f };
                float[] trans = transform.Trans ?? new float[3];
                float[] scale = { Length(right), Length(up), Length(forward) };
                if (scale[0] <= 0 || scale[1] <= 0 || scale[2] <= 0) throw new ArgumentException("transform has a zero-length axis");
                float[][] cols = { Div(right, scale[0]), Div(up, scale[1]), Div(forward, scale[2]) };
                if (Dot(Cross(cols[0], cols[1]), cols[2]) < 0)
                    throw new ArgumentException("mirrored transforms are not supported: the pipeline bakes a mirrored copy of the shape for those");
                colsList.Add(cols); transList.Add(trans); scaleList.Add(scale);
            }

            byte[] arrA = CurrentInstances(A);
            int sib = siblingIndex * InstanceSize;
            int tagS = ReadUInt16(arrA, sib + 0x58);
            int nextTag = siblingIndex + 1 < A.InstanceCount ? ReadUInt16(arrA, sib + InstanceSize + 0x58) : ReadInt32(header, 0);
            int partsPerInstance = nextTag - tagS;
            if (partsPerInstance <= 0) throw new InvalidDataException("sibling " + siblingIndex + " owns no physics parts");
            int detOff = ReadInt32(header, 0x38), detCount = ReadInt32(header, 0x34);
            if (siblingIndex >= detCount) throw new InvalidDataException("detail resource table is shorter than the instance array");
            int resIndex = ReadUInt16(header, detOff + 2 * siblingIndex);
            int k = siblingIndex + 1;
            while (k < A.InstanceCount && ReadUInt16(header, detOff + 2 * k) == resIndex) k++;
            int firstPart = ReadUInt16(arrA, (k - 1) * InstanceSize + 0x58) + partsPerInstance;
            var memberKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int j = 0; j < A.InstanceCount && j < detCount; j++)
                if (ReadUInt16(header, detOff + 2 * j) == resIndex) memberKeys.Add(TransformKey(arrA, j * InstanceSize));
            string sibKey = TransformKey(arrA, sib);

            // Header: the same growth AppendInstance does, once per new instance (each insertion lands
            // right after the previous one, exactly as sequential appends would place them).
            for (int n = 0; n < transforms.Count; n++) GrowHeader(firstPart + n * partsPerInstance, tagS, partsPerInstance, k + n, siblingIndex);

            var report = new StringBuilder();
            foreach (Compound C in Compounds)
            {
                if (!C.IsRoot) continue;
                int si, ki;
                if (C == A) { si = siblingIndex; ki = k; }
                else
                {
                    si = -1; ki = -1;
                    byte[] cur = CurrentInstances(C);
                    for (int i = 0; i < C.InstanceCount; i++)
                    {
                        string key = TransformKey(cur, i * InstanceSize);
                        if (si < 0 && key == sibKey) si = i;
                        if (memberKeys.Contains(key)) ki = i + 1;
                    }
                    if (si < 0) { report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(" skipped (member absent); "); continue; }
                }
                int before = C.InstanceCount;
                AppendManyToCompound(C, si, ki, partsPerInstance, colsList, transList, scaleList);
                report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(": ").Append(before).Append("->").Append(C.InstanceCount)
                    .Append(" instances, ").Append(C.NodeCount).Append(" nodes; ");
            }
            dirty = true;
            detail = transforms.Count + " instance(s), parts/instance " + partsPerInstance + ", first part " + firstPart + ", " + report.ToString().TrimEnd(' ', ';');
            return k;
        }

        // AppendToCompound for several records inserted consecutively at ki: one leaf-box pass, one
        // tree build, one instance-array block, one fixup remap.
        private void AppendManyToCompound(Compound C, int si, int ki, int parts, List<float[][]> colsList, List<float[]> transList, List<float[]> scaleList)
        {
            int n = C.InstanceCount, m = colsList.Count;
            int rel = C.Offset - pack64.DataBase;
            int dataRel = C.DataObject - pack64.DataBase;
            float[][] domain = { ReadVec4(source, C.DataObject + 0x20), ReadVec4(source, C.DataObject + 0x30) };
            Dictionary<int, LeafBox> leaves = DecodeTree(CurrentNodes(C), 0, C.NodeCount, domain);
            if (leaves.Count != n) throw new InvalidDataException("compound tree yields " + leaves.Count + " leaves for " + n + " instances");
            byte[] curArr = new byte[n * InstanceSize];
            for (int i = 0; i < curArr.Length; i++) curArr[i] = data[C.InstanceArray - pack64.DataBase + i];

            LeafBox sibLeaf = leaves[si];
            float[][] sibCols = { ReadVec4(curArr, si * InstanceSize), ReadVec4(curArr, si * InstanceSize + 0x10), ReadVec4(curArr, si * InstanceSize + 0x20) };
            float[] sibScale = ReadVec4(curArr, si * InstanceSize + 0x40), sibTrans = ReadVec4(curArr, si * InstanceSize + 0x30);
            float[][] localBox = InverseTransformBox(sibLeaf.Min, sibLeaf.Max, sibCols, sibScale, sibTrans);

            var records = new List<byte[]>(n + m);
            for (int i = 0; i < n; i++) records.Add(Slice(curArr, i * InstanceSize, InstanceSize));
            var boxes = new List<LeafBox>(n + m);
            for (int i = 0; i < n; i++) boxes.Add(leaves[i]);
            for (int j = 0; j < m; j++)
            {
                var rec = Slice(curArr, si * InstanceSize, InstanceSize);
                for (int c = 0; c < 3; c++)
                    for (int a = 0; a < 3; a++) WriteSingle(rec, c * 0x10 + a * 4, colsList[j][c][a]);
                for (int a = 0; a < 3; a++) { WriteSingle(rec, 0x30 + a * 4, transList[j][a]); WriteSingle(rec, 0x40 + a * 4, scaleList[j][a]); }
                WriteInt24(rec, 0x0c, FlagsFor(colsList[j], scaleList[j]));
                float[][] newBox = TransformBox(localBox[0], localBox[1], colsList[j], scaleList[j], transList[j]);
                records.Insert(ki + j, rec);
                boxes.Insert(ki + j, new LeafBox { Min = newBox[0], Max = newBox[1] });
            }
            // Part tags: each inserted record continues the previous one's tag; everything after shifts.
            for (int j = 0; j < m; j++) WriteUInt16(records[ki + j], 0x58, (ushort)(ReadUInt16(records[ki + j - 1], 0x58) + parts));
            for (int i = ki + m; i < n + m; i++) WriteUInt16(records[i], 0x58, (ushort)(ReadUInt16(records[i], 0x58) + parts * m));

            float[][] newDomain;
            Dictionary<int, int> leafNode;
            byte[] nodes = BuildTree(boxes, out newDomain, out leafNode);
            for (int i = 0; i < n + m; i++) WriteInt24(records[i], 0x3c, leafNode[i]);

            int newArray = AppendBlock(Concat(records));
            int newNodes = AppendBlock(PadTo16(nodes));
            WriteInt32At(rel + 0x68, n + m);
            data[rel + 0x12] = KeyBitsFor(n + m);   // instance key bits must cover the new count
            WriteInt32At(rel + 0x6c, unchecked((int)((uint)(n + m) | 0x80000000u)));
            bool inlineArray = LocalTarget(rel + 0x60) == rel + 0xd0;
            RetargetLocal(rel + 0x60, newArray);
            // +0xd0 holds instance 0 only while the compound's array is still inline right after the 0xd0-byte
            // object. After a rebuild moved it and CompactData dropped the old array, the NEXT object sits there, and
            // writing a record over it corrupted that object (Hoth F1b crash at game+0x96E51A0; V29e/f/l garbage
            // compounds). FROSTYCLI_OLD_INLINE_COPY=1 restores the unconditional copy for bisects.
            if (inlineArray || Environment.GetEnvironmentVariable("FROSTYCLI_OLD_INLINE_COPY") == "1")
                for (int i = 0; i < 0x50; i++) data[rel + 0xd0 + i] = records[0][i];
            for (int a = 0; a < 3; a++)
            {
                WriteSingleAt(rel + 0x80 + a * 4, newDomain[0][a]); WriteSingleAt(rel + 0x90 + a * 4, newDomain[1][a]);
                WriteSingleAt(dataRel + 0x20 + a * 4, newDomain[0][a]); WriteSingleAt(dataRel + 0x30 + a * 4, newDomain[1][a]);
            }
            int nodeCount = nodes.Length / NodeSize;
            WriteInt32At(dataRel + 0x18, nodeCount);
            WriteInt32At(dataRel + 0x1c, unchecked((int)((uint)nodeCount | 0x80000000u)));
            RetargetLocal(dataRel + 0x10, newNodes);

            int oldLo = C.InstanceArray - pack64.DataBase, oldHi = oldLo + n * InstanceSize;
            int sibShape = -1;
            var moved = new List<int[]>(global.Count + m);
            foreach (int[] g in global)
            {
                if (g[0] >= oldLo && g[0] < oldHi)
                {
                    int i = (g[0] - oldLo) / InstanceSize, off = (g[0] - oldLo) % InstanceSize;
                    if (i == si && off == 0x50) sibShape = g[2];
                    int i2 = i < ki ? i : i + m;
                    moved.Add(new[] { newArray + i2 * InstanceSize + off, g[1], g[2] });
                }
                else moved.Add(g);
            }
            if (sibShape < 0) throw new InvalidDataException("sibling instance has no shape pointer fixup");
            for (int j = 0; j < m; j++) moved.Add(new[] { newArray + (ki + j) * InstanceSize + 0x50, 2, sibShape });
            global = moved;

            C.InstanceArray = pack64.DataBase + newArray;
            C.NodeArray = pack64.DataBase + newNodes;
            C.InstanceCount = n + m;
            C.NodeCount = nodeCount;
            for (int a = 0; a < 3; a++) { WriteSingle(source, C.DataObject + 0x20 + a * 4, newDomain[0][a]); WriteSingle(source, C.DataObject + 0x30 + a * 4, newDomain[1][a]); }
        }

        /// <summary>Serialises the resource. Without edits this reproduces the input exactly.</summary>
        public byte[] Write()
        {
            CompactData();
            // Pad object bytes, then rebuild the 64-bit data section and its headers.
            int objectsLen = Align16(data.Count);
            byte[] hdr = new byte[0x50 + pack64.SectionCount * 0x40];
            Array.Copy(source, pack64.Start, hdr, 0, hdr.Length);
            byte[] lt = FixupTable(local, 2), gt = FixupTable(global, 3);
            int sectionIndex = pack64.Sections.IndexOf(pack64.Data);
            int so = 0x50 + sectionIndex * 0x40;
            int end = objectsLen + virtualTail.Length;
            WriteInt32(hdr, so + 0x18, lt.Length + gt.Length);
            WriteInt32(hdr, so + 0x1c, lt.Length);
            WriteInt32(hdr, so + 0x20, objectsLen);
            WriteInt32(hdr, so + 0x24, end);
            WriteInt32(hdr, so + 0x28, end);
            WriteInt32(hdr, so + 0x2c, end);

            // Class names may have grown: every section after them moves by the padded delta.
            byte[] pre = Slice(source, pack64.Start + hdr.Length, pack64.DataBase - (pack64.Start + hdr.Length));
            if (pack64.ClassNames != null && pack64.ClassNameBytes != null)
            {
                int oldLen = pack64.ClassNames.End;
                int newLen = Align16(pack64.ClassNameBytes.Length);
                if (newLen != oldLen)
                {
                    int delta = newLen - oldLen;
                    int clsRel = pack64.ClassNames.AbsStart - hdr.Length;   // classnames sit right after the headers
                    var grown = new MemoryStream();
                    grown.Write(pre, 0, clsRel);
                    grown.Write(pack64.ClassNameBytes, 0, pack64.ClassNameBytes.Length);
                    for (int i = pack64.ClassNameBytes.Length; i < newLen; i++) grown.WriteByte(0xff);
                    grown.Write(pre, clsRel + oldLen, pre.Length - clsRel - oldLen);
                    pre = grown.ToArray();
                    for (int i = 0; i < pack64.Sections.Count; i++)
                    {
                        Section sec = pack64.Sections[i];
                        int hso = 0x50 + i * 0x40;
                        if (sec == pack64.ClassNames)
                        {
                            WriteInt32(hdr, hso + 0x20, newLen); WriteInt32(hdr, hso + 0x24, newLen);
                            WriteInt32(hdr, hso + 0x28, newLen); WriteInt32(hdr, hso + 0x2c, newLen);
                        }
                        else if (sec.AbsStart > pack64.ClassNames.AbsStart)
                            WriteInt32(hdr, hso + 0x14, sec.AbsStart + delta);
                    }
                }
            }
            var ms = new MemoryStream();
            ms.Write(header, 0, header.Length);
            ms.Write(source, pack32.Start, pack32.End - pack32.Start);
            long p64 = ms.Length;
            ms.Write(hdr, 0, hdr.Length);
            ms.Write(pre, 0, pre.Length);
            byte[] objects = data.ToArray();
            ms.Write(objects, 0, objects.Length);
            Pad(ms, objectsLen - objects.Length);
            ms.Write(virtualTail, 0, virtualTail.Length);
            Pad(ms, (int)((Align16(ms.Length) - ms.Length)));
            long p64Len = ms.Length - p64;

            // Fixup blob: sizes, 32-bit tables verbatim, 64-bit tables (data section rebuilt), reloc fixups.
            var fix64 = new MemoryStream();
            foreach (Section s in pack64.Sections)
            {
                if (s == pack64.Data) { fix64.Write(lt, 0, lt.Length); fix64.Write(gt, 0, gt.Length); }
                else fix64.Write(source, s.FixupAt, s.FixupTotal);
            }
            byte[] f64 = fix64.ToArray();
            var sizes = new byte[8];
            WriteInt32(sizes, 0, size32);
            WriteInt32(sizes, 4, f64.Length);
            ms.Write(sizes, 0, 8);
            ms.Write(source, blobAt + 8, size32);
            ms.Write(f64, 0, f64.Length);
            ms.Write(source, relocAt, source.Length - relocAt);
            return ms.ToArray();
        }

        // ------------------------------------------------------------------ edit internals

        private void BeginEdit()
        {
            Section s = pack64.Data;
            data = new List<byte>(s.Virt + 4096);
            for (int i = 0; i < s.Virt; i++) data.Add(source[pack64.DataBase + i]);
            virtualTail = new byte[s.End - s.Virt];
            Array.Copy(source, pack64.DataBase + s.Virt, virtualTail, 0, virtualTail.Length);
            local = new List<int[]>(pack64.LocalFixups);
            global = new List<int[]>(pack64.GlobalFixups);
            originalDataLen = data.Count;
            blocks = new List<int[]>();
            // The parsed compounds' instance and node arrays are reclaimable too: a previous edit's
            // rebuild left them in the file, and this edit will orphan them again when it rebuilds.
            foreach (Compound C in Compounds)
            {
                if (C.InstanceCount > 0) blocks.Add(new[] { C.InstanceArray - pack64.DataBase, C.InstanceCount * InstanceSize, 0 });
                if (C.NodeCount > 0) blocks.Add(new[] { C.NodeArray - pack64.DataBase, C.NodeCount * NodeSize, 0 });
            }
            blocks.Sort(delegate(int[] x, int[] y) { return x[0].CompareTo(y[0]); });
            header = new byte[headerEnd];
            Array.Copy(source, 0, header, 0, headerEnd);
            RebuildHeader(null, null);
        }

        private void GrowHeader(int firstPart, int siblingPart, int parts, int instanceAt, int siblingInstance)
        {
            int nMat = ReadInt32(header, 0x1c), offMat = ReadInt32(header, 0x20);
            int nDet = ReadInt32(header, 0x34), offDet = ReadInt32(header, 0x38);
            var mat = new List<byte>();
            for (int i = 0; i < nMat; i++) mat.Add(header[offMat + i]);
            var det = new List<byte>();
            for (int i = 0; i < 2 * nDet; i++) det.Add(header[offDet + i]);
            var newParts = mat.GetRange(siblingPart, parts);
            mat.InsertRange(firstPart, newParts);
            det.InsertRange(2 * instanceAt, det.GetRange(2 * siblingInstance, 2));
            WriteInt32(header, 0, ReadInt32(header, 0) + parts);
            RebuildHeader(mat.ToArray(), det.ToArray());
        }

        // Lays the reloc header out the way the pipeline does: fixed 0x50-byte struct, then the
        // material indices, the material flags table and the detail resource indices, 16-aligned.
        private void RebuildHeader(byte[] materialIndices, byte[] detailIndices)
        {
            int nMat = ReadInt32(header, 0x1c), offMat = ReadInt32(header, 0x20);
            int nFlags = ReadInt32(header, 0x28), offFlags = ReadInt32(header, 0x2c);
            int nDet = ReadInt32(header, 0x34), offDet = ReadInt32(header, 0x38);
            byte[] mat = materialIndices ?? Slice(header, offMat, nMat);
            byte[] flags = Slice(header, offFlags, 4 * nFlags);
            byte[] det = detailIndices ?? Slice(header, offDet, 2 * nDet);
            int newMat = 0x50, newFlags = Align16(newMat + mat.Length), newDet = Align16(newFlags + flags.Length);
            int size = Align16(newDet + det.Length);
            var h = new byte[size];
            Array.Copy(header, 0, h, 0, 0x50);
            int[][] arrays = { new[] { 0x4, 0, newMat }, new[] { 0x10, 0, newMat }, new[] { 0x1c, mat.Length, newMat },
                               new[] { 0x28, nFlags, newFlags }, new[] { 0x34, det.Length / 2, newDet } };
            foreach (int[] a in arrays) { WriteInt32(h, a[0], a[1]); WriteInt32(h, a[0] + 4, a[2]); }
            Array.Copy(mat, 0, h, newMat, mat.Length);
            Array.Copy(flags, 0, h, newFlags, flags.Length);
            Array.Copy(det, 0, h, newDet, det.Length);
            header = h;
        }

        private void AppendToCompound(Compound C, int si, int ki, int parts, float[][] cols, float[] trans, float[] scale)
        {
            int n = C.InstanceCount;
            int rel = C.Offset - pack64.DataBase;
            int dataRel = C.DataObject - pack64.DataBase;
            float[][] domain = { ReadVec4(source, C.DataObject + 0x20), ReadVec4(source, C.DataObject + 0x30) };
            Dictionary<int, LeafBox> leaves = DecodeTree(CurrentNodes(C), 0, C.NodeCount, domain);
            if (leaves.Count != n) throw new InvalidDataException("compound tree yields " + leaves.Count + " leaves for " + n + " instances");
            byte[] curArr = new byte[n * InstanceSize];
            for (int i = 0; i < curArr.Length; i++) curArr[i] = data[C.InstanceArray - pack64.DataBase + i];

            // New record: copy of the sibling with the new transform, flags and leaf index.
            int sibAbs = C.InstanceArray + si * InstanceSize;
            var rec = Slice(curArr, si * InstanceSize, InstanceSize);
            for (int c = 0; c < 3; c++)
                for (int a = 0; a < 3; a++) WriteSingle(rec, c * 0x10 + a * 4, cols[c][a]);
            for (int a = 0; a < 3; a++) { WriteSingle(rec, 0x30 + a * 4, trans[a]); WriteSingle(rec, 0x40 + a * 4, scale[a]); }
            WriteInt24(rec, 0x0c, FlagsFor(cols, scale));

            // Leaf box: the sibling's world box taken back into shape space, then out through the
            // new transform. Loose for rotated siblings, never too small.
            LeafBox sibLeaf = leaves[si];
            float[][] sibCols = { ReadVec4(curArr, si * InstanceSize), ReadVec4(curArr, si * InstanceSize + 0x10), ReadVec4(curArr, si * InstanceSize + 0x20) };
            float[] sibScale = ReadVec4(curArr, si * InstanceSize + 0x40), sibTrans = ReadVec4(curArr, si * InstanceSize + 0x30);
            float[][] localBox = InverseTransformBox(sibLeaf.Min, sibLeaf.Max, sibCols, sibScale, sibTrans);
            float[][] newBox = TransformBox(localBox[0], localBox[1], cols, scale, trans);

            var records = new List<byte[]>(n + 1);
            for (int i = 0; i < n; i++) records.Add(Slice(curArr, i * InstanceSize, InstanceSize));
            records.Insert(ki, rec);
            for (int i = ki + 1; i <= n; i++) WriteUInt16(records[i], 0x58, (ushort)(ReadUInt16(records[i], 0x58) + parts));
            WriteUInt16(records[ki], 0x58, (ushort)(ReadUInt16(records[ki - 1], 0x58) + parts));

            var boxes = new List<LeafBox>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                if (i < ki) boxes.Add(leaves[i]);
                else if (i == ki) boxes.Add(new LeafBox { Min = newBox[0], Max = newBox[1] });
                else boxes.Add(leaves[i - 1]);
            }
            float[][] newDomain;
            Dictionary<int, int> leafNode;
            byte[] nodes = BuildTree(boxes, out newDomain, out leafNode);
            for (int i = 0; i <= n; i++) WriteInt24(records[i], 0x3c, leafNode[i]);

            int newArray = AppendBlock(Concat(records));
            int newNodes = AppendBlock(PadTo16(nodes));

            WriteInt32At(rel + 0x68, n + 1);

            data[rel + 0x12] = KeyBitsFor(n + 1);   // instance key bits must cover the new count
            WriteInt32At(rel + 0x6c, unchecked((int)((uint)(n + 1) | 0x80000000u)));
            bool inlineArray = LocalTarget(rel + 0x60) == rel + 0xd0;
            RetargetLocal(rel + 0x60, newArray);
            // +0xd0 holds instance 0 only while the compound's array is still inline right after the 0xd0-byte
            // object. After a rebuild moved it and CompactData dropped the old array, the NEXT object sits there, and
            // writing a record over it corrupted that object (Hoth F1b crash at game+0x96E51A0; V29e/f/l garbage
            // compounds). FROSTYCLI_OLD_INLINE_COPY=1 restores the unconditional copy for bisects.
            if (inlineArray || Environment.GetEnvironmentVariable("FROSTYCLI_OLD_INLINE_COPY") == "1")
                for (int i = 0; i < 0x50; i++) data[rel + 0xd0 + i] = records[0][i];
            for (int a = 0; a < 3; a++)
            {
                WriteSingleAt(rel + 0x80 + a * 4, newDomain[0][a]); WriteSingleAt(rel + 0x90 + a * 4, newDomain[1][a]);
                WriteSingleAt(dataRel + 0x20 + a * 4, newDomain[0][a]); WriteSingleAt(dataRel + 0x30 + a * 4, newDomain[1][a]);
            }
            int nodeCount = nodes.Length / NodeSize;
            WriteInt32At(dataRel + 0x18, nodeCount);
            WriteInt32At(dataRel + 0x1c, unchecked((int)((uint)nodeCount | 0x80000000u)));
            RetargetLocal(dataRel + 0x10, newNodes);

            // Shape pointers live in global fixups keyed by the record field offset: move them.
            int oldLo = C.InstanceArray - pack64.DataBase, oldHi = oldLo + n * InstanceSize;
            int sibShape = -1;
            var moved = new List<int[]>(global.Count + 1);
            foreach (int[] g in global)
            {
                if (g[0] >= oldLo && g[0] < oldHi)
                {
                    int i = (g[0] - oldLo) / InstanceSize, off = (g[0] - oldLo) % InstanceSize;
                    if (i == si && off == 0x50) sibShape = g[2];
                    int i2 = i < ki ? i : i + 1;
                    moved.Add(new[] { newArray + i2 * InstanceSize + off, g[1], g[2] });
                }
                else moved.Add(g);
            }
            if (sibShape < 0) throw new InvalidDataException("sibling instance has no shape pointer fixup");
            moved.Add(new[] { newArray + ki * InstanceSize + 0x50, 2, sibShape });
            global = moved;

            C.InstanceArray = pack64.DataBase + newArray;
            C.NodeArray = pack64.DataBase + newNodes;
            C.InstanceCount = n + 1;
            C.NodeCount = nodeCount;
            for (int a = 0; a < 3; a++) { WriteSingle(source, C.DataObject + 0x20 + a * 4, newDomain[0][a]); WriteSingle(source, C.DataObject + 0x30 + a * 4, newDomain[1][a]); }
        }

        // ------------------------------------------------------------------ tree codec

        public struct LeafBox { public float[] Min; public float[] Max; public int Node; }
        /// <summary>Diagnostic: per placement instance the stored rotation columns, scale, translation and the AABB tree's leaf box.</summary>
        public static List<string> DescribeInstances(byte[] bytes, int from, int count)
        {
            var lines = new List<string>();
            HavokPhysicsResource r = Parse(bytes);
            Compound C = r.Placement;
            if (C == null) { lines.Add("no placement compound"); return lines; }
            byte[] src = r.source;
            float[][] domain = { ReadVec4(src, C.DataObject + 0x20), ReadVec4(src, C.DataObject + 0x30) };
            Dictionary<int, LeafBox> leaves = DecodeTree(r.CurrentNodes(C), 0, C.NodeCount, domain);
            lines.Add("instances=" + C.InstanceCount + " leaves=" + leaves.Count);
            byte[] cur = r.CurrentInstances(C);
            for (int i = Math.Max(0, from); i < C.InstanceCount && i < from + count; i++)
            {
                byte[] rec = Slice(cur, i * InstanceSize, InstanceSize);
                float[] c0 = ReadVec4(rec, 0), c1 = ReadVec4(rec, 0x10), c2 = ReadVec4(rec, 0x20), t = ReadVec4(rec, 0x30), sc = ReadVec4(rec, 0x40);
                LeafBox lb; leaves.TryGetValue(i, out lb);
                string F(float[] v) { return "(" + string.Join(",", Array.ConvertAll(v, x => x.ToString("0.###"))) + ")"; }
                lines.Add("inst " + i + ": right=" + F(c0) + " up=" + F(c1) + " fwd=" + F(c2) + " trans=" + F(t) + " scale=" + F(sc) + " flags=0x" + (ReadInt32(rec, 0x0c) & 0xffffff).ToString("x")
                    + (lb.Min != null ? " leaf=" + F(lb.Min) + ".." + F(lb.Max) : " leaf=?"));
            }
            return lines;
        }


        /// <summary>
        /// Walks a compound tree (hkcdStaticTree, 6-byte Codec6 nodes) and returns each leaf's
        /// decoded box keyed by instance index.
        /// </summary>
        public static Dictionary<int, LeafBox> DecodeTree(byte[] bytes, int nodesAt, int nodeCount, float[][] domain)
        {
            var result = new Dictionary<int, LeafBox>();
            var stack = new Stack<KeyValuePair<int, float[][]>>();
            stack.Push(new KeyValuePair<int, float[][]>(0, new[] { Slice3(domain[0]), Slice3(domain[1]) }));
            while (stack.Count > 0)
            {
                KeyValuePair<int, float[][]> cur = stack.Pop();
                int i = cur.Key;
                if (i < 0 || i >= nodeCount) throw new InvalidDataException("tree node " + i + " is out of range");
                int o = nodesAt + i * NodeSize;
                float[] mn = new float[3], mx = new float[3];
                for (int a = 0; a < 3; a++)
                {
                    int hi = bytes[o + a] >> 4, lo = bytes[o + a] & 15;
                    float s = (cur.Value[1][a] - cur.Value[0][a]) / 226f;
                    mn[a] = cur.Value[0][a] + s * hi * hi;
                    mx[a] = cur.Value[1][a] - s * lo * lo;
                }
                int flags = bytes[o + 3];
                int value = ((flags & 0x7f) << 16) | bytes[o + 4] | (bytes[o + 5] << 8);
                if ((flags & 0x80) != 0)
                {
                    stack.Push(new KeyValuePair<int, float[][]>(i + 1, new[] { mn, mx }));
                    stack.Push(new KeyValuePair<int, float[][]>(i + 2 * value, new[] { mn, mx }));
                }
                else result[value] = new LeafBox { Min = mn, Max = mx, Node = i };
            }
            return result;
        }

        /// <summary>
        /// Builds a depth-first Codec6 tree over the given leaf boxes (index = instance index):
        /// median split on the longest axis, boxes quantised conservatively against their parent.
        /// </summary>
        public static byte[] BuildTree(List<LeafBox> leaves, out float[][] domain, out Dictionary<int, int> leafNode)
        {
            var nodes = new List<byte[]>();
            var map = new Dictionary<int, int>();
            var all = new List<int>();
            for (int i = 0; i < leaves.Count; i++) all.Add(i);
            float[][] dom = Union(leaves, all);
            Emit(leaves, all, dom[0], dom[1], nodes, map);
            domain = dom;
            leafNode = map;
            return Concat(nodes);
        }

        private static int Emit(List<LeafBox> leaves, List<int> idx, float[] pmn, float[] pmx, List<byte[]> nodes, Dictionary<int, int> map)
        {
            float[][] box = Union(leaves, idx);
            var node = new byte[NodeSize];
            float[] dmn = new float[3], dmx = new float[3];
            for (int a = 0; a < 3; a++)
            {
                int hi, lo;
                EncodeAxis(pmn[a], pmx[a], box[0][a], box[1][a], out hi, out lo, out dmn[a], out dmx[a]);
                node[a] = (byte)((hi << 4) | lo);
            }
            int me = nodes.Count;
            nodes.Add(node);
            if (idx.Count == 1)
            {
                int inst = idx[0];
                map[inst] = me;
                node[3] = (byte)((inst >> 16) & 0x7f); node[4] = (byte)(inst & 0xff); node[5] = (byte)((inst >> 8) & 0xff);
                return 1;
            }
            int axis = 0;
            for (int a = 1; a < 3; a++) if (box[1][a] - box[0][a] > box[1][axis] - box[0][axis]) axis = a;
            var sorted = new List<int>(idx);
            sorted.Sort(delegate(int x, int y)
            {
                float cx = leaves[x].Min[axis] + leaves[x].Max[axis], cy = leaves[y].Min[axis] + leaves[y].Max[axis];
                int byCentre = cx.CompareTo(cy);
                return byCentre != 0 ? byCentre : x.CompareTo(y);
            });
            int half = sorted.Count / 2;
            int nl = Emit(leaves, sorted.GetRange(0, half), dmn, dmx, nodes, map);
            int nr = Emit(leaves, sorted.GetRange(half, sorted.Count - half), dmn, dmx, nodes, map);
            if (((1 + nl) & 1) != 0) throw new InvalidOperationException("left subtree has an even node count");
            int value = (1 + nl) / 2;
            node[3] = (byte)(0x80 | ((value >> 16) & 0x7f)); node[4] = (byte)(value & 0xff); node[5] = (byte)((value >> 8) & 0xff);
            return 1 + nl + nr;
        }

        private static void EncodeAxis(float pmn, float pmx, float cmn, float cmx, out int hi, out int lo, out float dmn, out float dmx)
        {
            float s = (pmx - pmn) / 226f;
            if (s <= 0) { hi = 0; lo = 0; dmn = pmn; dmx = pmx; return; }
            hi = Math.Max(0, Math.Min(15, (int)Math.Floor(Math.Sqrt(Math.Max(0.0, (cmn - pmn) / s)))));
            lo = Math.Max(0, Math.Min(15, (int)Math.Floor(Math.Sqrt(Math.Max(0.0, (pmx - cmx) / s)))));
            while (hi > 0 && pmn + s * hi * hi > cmn) hi--;
            while (lo > 0 && pmx - s * lo * lo < cmx) lo--;
            dmn = pmn + s * hi * hi;
            dmx = pmx - s * lo * lo;
        }

        private static float[][] Union(List<LeafBox> leaves, List<int> idx)
        {
            float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
            foreach (int i in idx)
                for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], leaves[i].Min[a]); mx[a] = Math.Max(mx[a], leaves[i].Max[a]); }
            return new[] { mn, mx };
        }

        /// <summary>Instance flag word: base bits plus rotation/scale presence, as the pipeline sets them.</summary>
        public static int FlagsFor(float[][] cols, float[] scale)
        {
            float dev = 0;
            for (int c = 0; c < 3; c++)
                for (int a = 0; a < 3; a++) dev = Math.Max(dev, Math.Abs(cols[c][a] - (c == a ? 1f : 0f)));
            int f = 0x40 | 0x20 | 0x02;
            if (dev >= 1e-3f) f |= 0x04;
            for (int a = 0; a < 3; a++) if (BitConverter.ToInt32(BitConverter.GetBytes(scale[a]), 0) != BitConverter.ToInt32(BitConverter.GetBytes(1f), 0)) { f |= 0x08; break; }
            return f;
        }

        // ------------------------------------------------------------------ parsing

        private Pack ParsePack(int start)
        {
            var p = new Pack { Start = start };
            p.PointerBytes = source[start + 0x10];
            p.SectionCount = ReadInt32(source, start + 0x14);
            if (p.SectionCount <= 0 || p.SectionCount > 8) throw new InvalidDataException("invalid HKX section count");
            int o = start + 0x50;
            for (int i = 0; i < p.SectionCount; i++, o += 0x40)
            {
                var s = new Section
                {
                    Name = ReadAsciiZ(source, o, 19),
                    AbsStart = ReadInt32(source, o + 0x14),
                    FixupTotal = ReadInt32(source, o + 0x18),
                    LocalSize = ReadInt32(source, o + 0x1c),
                    Virt = ReadInt32(source, o + 0x20),
                    Exports = ReadInt32(source, o + 0x24),
                    Imports = ReadInt32(source, o + 0x28),
                    End = ReadInt32(source, o + 0x2c)
                };
                p.Sections.Add(s);
                if (s.Name == "__data__") p.Data = s;
                p.End = Math.Max(p.End, start + s.AbsStart + s.End);
            }
            if (p.Data == null) throw new InvalidDataException("packfile has no __data__ section");
            p.DataBase = start + p.Data.AbsStart;
            Section cls = null;
            foreach (Section s in p.Sections) if (s.Name == "__classnames__") cls = s;
            if (cls != null)
            {
                p.ClassNames = cls;
                int used = cls.End;
                while (used > 0 && source[start + cls.AbsStart + used - 1] == 0xff) used--;
                p.ClassNameBytes = Slice(source, start + cls.AbsStart, used);
                int b = start + cls.AbsStart, c = b;
                while (c + 6 <= b + cls.End && source[c + 4] == 0x09)
                {
                    int n = c + 5;
                    while (n < b + cls.End && source[n] != 0) n++;
                    p.Classes[c + 5 - b] = Encoding.ASCII.GetString(source, c + 5, n - (c + 5));
                    c = n + 1;
                    while (c < b + cls.End && source[c] == 0xff) c++;
                }
            }
            return p;
        }

        private void ReadFixups(Pack p, int at)
        {
            int off = at;
            foreach (Section s in p.Sections)
            {
                s.FixupAt = off;
                if (s == p.Data)
                {
                    for (int i = 0; i + 8 <= s.LocalSize; i += 8)
                    {
                        int a = ReadInt32(source, off + i), b = ReadInt32(source, off + i + 4);
                        if (a == -1) continue;
                        p.LocalFixups.Add(new[] { a, b });
                    }
                    for (int i = s.LocalSize; i + 12 <= s.FixupTotal; i += 12)
                    {
                        int a = ReadInt32(source, off + i), b = ReadInt32(source, off + i + 4), c = ReadInt32(source, off + i + 8);
                        if (a == -1) continue;
                        p.GlobalFixups.Add(new[] { a, b, c });
                    }
                }
                off += s.FixupTotal;
            }
        }

        private void ReadObjects(Pack p)
        {
            Section s = p.Data;
            for (int o = p.DataBase + s.Virt; o + 12 <= p.DataBase + s.Exports; o += 12)
            {
                int a = ReadInt32(source, o), c = ReadInt32(source, o + 8);
                if (a == -1) continue;
                string name;
                p.Objects.Add(new KeyValuePair<int, string>(p.DataBase + a, p.Classes.TryGetValue(c, out name) ? name : "?"));
            }
            p.Objects.Sort(delegate(KeyValuePair<int, string> x, KeyValuePair<int, string> y) { return x.Key.CompareTo(y.Key); });
        }

        private void ResolvePointers(Pack p)
        {
            foreach (int[] f in p.LocalFixups) p.Pointers[p.DataBase + f[0]] = p.DataBase + f[1];
            foreach (int[] f in p.GlobalFixups)
                if (f[1] >= 0 && f[1] < p.Sections.Count) p.Pointers[p.DataBase + f[0]] = p.Start + p.Sections[f[1]].AbsStart + f[2];
        }

        private void ReadCompounds()
        {
            Pack p = pack64;
            var referenced = new HashSet<int>();
            var list = new List<Compound>();
            foreach (KeyValuePair<int, string> obj in p.Objects)
            {
                if (obj.Value != "hknpStaticCompoundShape") continue;
                int arr, dataObj, nodesAt;
                if (!p.Pointers.TryGetValue(obj.Key + 0x60, out arr)) continue;
                var c = new Compound { Offset = obj.Key, InstanceArray = arr, InstanceCount = ReadInt32(source, obj.Key + 0x68) };
                if (p.Pointers.TryGetValue(obj.Key + 0xc0, out dataObj))
                {
                    c.DataObject = dataObj;
                    if (p.Pointers.TryGetValue(dataObj + 0x10, out nodesAt)) { c.NodeArray = nodesAt; c.NodeCount = ReadInt32(source, dataObj + 0x18); }
                }
                for (int i = 0; i < c.InstanceCount; i++)
                {
                    int shape;
                    if (p.Pointers.TryGetValue(arr + i * InstanceSize + 0x50, out shape)) referenced.Add(shape);
                }
                list.Add(c);
            }
            foreach (Compound c in list) c.IsRoot = !referenced.Contains(c.Offset) && c.DataObject != 0 && c.NodeArray != 0;
            Compounds = list;
        }

        // ------------------------------------------------------------------ member transplant

        /// <summary>Local-space box of the prop's collision root, for the group's PartBoundingBoxes.</summary>
        public struct PropInfo
        {
            public int PartCount;
            public float[] BoxMin;
            public float[] BoxMax;
            public int RootCount;
            /// <summary>Per-part local boxes from the prop header's localAabbs, or the root box repeated.</summary>
            public float[][][] PartBoxes;
        }

        /// <summary>Reads what a prop's own physics resource contributes to a group without copying anything.</summary>
        public static PropInfo InspectProp(byte[] propBytes)
        {
            HavokPhysicsResource prop = Parse(propBytes);
            List<int> roots = prop.ContainerRoots(prop.pack64);
            if (roots.Count == 0) throw new InvalidDataException("prop resource lists no root shapes");
            int root = roots[0];
            float[][] box = prop.ShapeLocalBox(prop.pack64, root);
            int parts = ReadInt32(prop.source, 0);
            var partBoxes = new float[Math.Max(1, parts)][][];
            int aabbCount = ReadInt32(prop.source, 0x10), aabbOff = ReadInt32(prop.source, 0x14);
            for (int i = 0; i < partBoxes.Length; i++)
            {
                if (aabbCount == parts && aabbOff > 0 && aabbOff + (i + 1) * 32 <= prop.headerEnd)
                    partBoxes[i] = new[] { Slice3(ReadVec4(prop.source, aabbOff + i * 32)), Slice3(ReadVec4(prop.source, aabbOff + i * 32 + 16)) };
                else partBoxes[i] = new[] { box[0], box[1] };
            }
            return new PropInfo
            {
                PartCount = parts,
                BoxMin = box[0],
                BoxMax = box[1],
                RootCount = roots.Count,
                PartBoxes = partBoxes
            };
        }

        /// <summary>
        /// Copies a prop's shape graph out of its own physics resource into this group resource and
        /// places one instance of it in every level slot the prop provides a root for (collision,
        /// raycast, character). The prop becomes a new, last member: its parts are appended after
        /// the group's current part count, so no existing tag or range moves. Returns the first
        /// part index of the new member; <paramref name="detail"/> describes what was copied.
        /// </summary>
        public int AppendMember(byte[] propBytes, LinearTransformData transform, out int partsPerInstance, out string detail)
        {
            return AppendMember(propBytes, transform, -1, out partsPerInstance, out detail);
        }

        /// <summary>
        /// Transplants a prop's shapes and appends one instance. detailResourceIndex is the new
        /// instance's entry in the reloc header's detail table; it indexes the group HavokAsset's
        /// ExternalAssets/Aabb lists on the EBX side, so the caller that grows those lists passes
        /// the slot it used (-1 = one past the highest index in use).
        /// </summary>
        public int AppendMember(byte[] propBytes, LinearTransformData transform, int detailResourceIndex, out int partsPerInstance, out string detail)
        {
            HavokPhysicsResource prop = Parse(propBytes);
            Pack pp = prop.pack64;
            List<int> proots = prop.ContainerRoots(pp);
            List<int> groots = ContainerRoots(pack64);
            if (proots.Count == 0) throw new InvalidDataException("prop resource lists no root shapes");
            if (groots.Count == 0) throw new InvalidDataException("group resource lists no root shapes");

            float[] right = transform.Right ?? new[] { 1f, 0f, 0f };
            float[] up = transform.Up ?? new[] { 0f, 1f, 0f };
            float[] forward = transform.Forward ?? new[] { 0f, 0f, 1f };
            float[] trans = transform.Trans ?? new float[3];
            float[] scale = { Length(right), Length(up), Length(forward) };
            if (scale[0] <= 0 || scale[1] <= 0 || scale[2] <= 0) throw new ArgumentException("transform has a zero-length axis");
            float[][] cols = { Div(right, scale[0]), Div(up, scale[1]), Div(forward, scale[2]) };
            if (Dot(Cross(cols[0], cols[1]), cols[2]) < 0)
                throw new ArgumentException("mirrored transforms are not supported: the pipeline bakes a mirrored copy of the shape for those");

            // Every class the prop uses must already be named in this packfile.
            var classOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, string> kv in pack64.Classes) classOffsets[kv.Value] = kv.Key;
            var propClassOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (KeyValuePair<int, string> kv in pp.Classes) propClassOffsets[kv.Value] = kv.Key;
            int classesAdded = 0;
            foreach (KeyValuePair<int, string> obj in pp.Objects)
            {
                if (classOffsets.ContainsKey(obj.Value)) continue;
                int propOff;
                if (!propClassOffsets.TryGetValue(obj.Value, out propOff))
                    throw new InvalidDataException("prop packfile names an object of class " + obj.Value + " without a class table entry");
                classOffsets[obj.Value] = AppendClassName(obj.Value, Slice(prop.source, pp.Start + pp.ClassNames.AbsStart + propOff - 5, 5));
                classesAdded++;
            }

            // Objects reachable from the prop's roots, copied verbatim (16-aligned) after the current data.
            var copy = new List<int>();
            foreach (int r in proots) foreach (int o in prop.Reachable(pp, r)) if (!copy.Contains(o)) copy.Add(o);
            copy.Sort();
            var newPos = new Dictionary<int, int>();
            var sizes = new Dictionary<int, int>();
            foreach (int o in copy)
            {
                int size = prop.ObjectSize(pp, o);
                sizes[o] = size;
                newPos[o] = AppendBlock(Slice(prop.source, o, size), 1);
            }
            Func<int, int> remap = delegate(int absTarget)
            {
                foreach (int o in copy)
                    if (absTarget >= o && absTarget < o + sizes[o]) return newPos[o] + (absTarget - o);
                throw new InvalidDataException("prop pointer target 0x" + absTarget.ToString("x") + " lies outside the copied objects");
            };
            Func<int, bool> inCopy = delegate(int abs)
            {
                foreach (int o in copy) if (abs >= o && abs < o + sizes[o]) return true;
                return false;
            };
            foreach (int[] f in pp.LocalFixups)
            {
                int src = pp.DataBase + f[0];
                if (inCopy(src)) local.Add(new[] { remap(src), remap(pp.DataBase + f[1]) });
            }
            foreach (int[] f in pp.GlobalFixups)
            {
                int src = pp.DataBase + f[0];
                if (!inCopy(src)) continue;
                if (f[1] < 0 || f[1] >= pp.Sections.Count) throw new InvalidDataException("prop global fixup names section " + f[1]);
                global.Add(new[] { remap(src), 2, remap(pp.Start + pp.Sections[f[1]].AbsStart + f[2]) });
            }
            var addedVirtual = new List<int[]>();
            foreach (KeyValuePair<int, string> obj in pp.Objects)
                if (newPos.ContainsKey(obj.Key)) addedVirtual.Add(new[] { newPos[obj.Key], 0, classOffsets[obj.Value] });
            AddVirtualFixups(addedVirtual);

            // Reloc header: one detail-resource index for the instance, one material index per part.
            int propParts = ReadInt32(prop.source, 0);
            if (propParts <= 0) throw new InvalidDataException("prop resource declares no physics parts");
            int firstPart = ReadInt32(header, 0);
            byte[] propMaterials = Slice(prop.source, ReadInt32(prop.source, 0x20), ReadInt32(prop.source, 0x1c));
            int[] propTable = ReadInt32Array(prop.source, ReadInt32(prop.source, 0x2c), ReadInt32(prop.source, 0x28));
            var mats = new List<byte>();
            for (int i = 0; i < propParts; i++)
                mats.Add((byte)MapMaterial(propTable, i < propMaterials.Length ? propMaterials[i] : (byte)0));
            GrowHeaderForMember(mats.ToArray(), detailResourceIndex >= 0 ? detailResourceIndex : NextDetailResourceIndex());
            partsPerInstance = propParts;

            // One instance per slot the prop provides a root for.
            int rootMin = 0;
            float[][] localBox = prop.ShapeLocalBox(pp, proots[0]);
            float[][] world = TransformBox(localBox[0], localBox[1], cols, scale, trans);
            var report = new StringBuilder();
            report.Append(copy.Count).Append(" objects copied; ");
            if (classesAdded > 0) report.Append(classesAdded).Append(" class name(s) added; ");
            for (int slot = 0; slot < groots.Count; slot++)
            {
                int propRoot;
                if (slot < proots.Count) propRoot = proots[slot];
                else if (slot == 1) { propRoot = proots[0]; report.Append("slot 1 reuses the collision root; "); }
                else { report.Append("slot ").Append(slot).Append(" skipped (prop has no root); "); continue; }
                Compound C = null;
                foreach (Compound c in Compounds) if (c.Offset == groots[slot]) C = c;
                if (C == null || C.DataObject == 0 || C.NodeArray == 0) throw new InvalidDataException("group root " + slot + " is not a readable compound");
                string shapeClass = prop.ClassOf(pp, propRoot);
                // The record carries the shape's in-memory size. Compounds and meshes keep their
                // arrays out of line (208 / 160 bytes); convex shapes inline their data, so their
                // packfile object size is the memory size (400 for an 8-vertex hull, 128 for a sphere).
                int shapeSize;
                if (!ShapeMemorySizes.TryGetValue(shapeClass, out shapeSize)) shapeSize = prop.ObjectSize(pp, propRoot);
                var rec = new byte[InstanceSize];
                for (int c = 0; c < 3; c++)
                    for (int a = 0; a < 3; a++) WriteSingle(rec, c * 0x10 + a * 4, cols[c][a]);
                for (int a = 0; a < 3; a++) { WriteSingle(rec, 0x30 + a * 4, trans[a]); WriteSingle(rec, 0x40 + a * 4, scale[a]); }
                WriteInt32(rec, 0x0c, unchecked((int)0x3f000000)); WriteInt32(rec, 0x1c, unchecked((int)0x3f000000));
                WriteInt32(rec, 0x2c, unchecked((int)0x3f000000)); WriteInt32(rec, 0x3c, unchecked((int)0x3f000000));
                WriteInt24(rec, 0x0c, FlagsFor(cols, scale));
                WriteInt24(rec, 0x2c, shapeSize);
                WriteUInt16(rec, 0x58, (ushort)firstPart);
                WriteUInt16(rec, 0x5a, 0xffff);
                AppendRecord(C, rec, new LeafBox { Min = world[0], Max = world[1] }, newPos[propRoot]);
                report.Append("slot ").Append(slot).Append(": ").Append(C.InstanceCount - 1).Append("->").Append(C.InstanceCount)
                    .Append(" instances (").Append(shapeClass).Append("); ");
                rootMin++;
            }
            if (rootMin == 0) throw new InvalidDataException("no group slot could take the prop");
            dirty = true;
            detail = report.ToString().TrimEnd(' ', ';');
            return firstPart;
        }

        /// <summary>
        /// Local-space bounding box of a shape object. Compounds carry theirs at +0x80/+0x90; a convex
        /// polytope is bounded by its vertex array (hkRelArray {u16 count, u16 offset} at +0x30,
        /// offset relative to that field, hkVector4 entries). Other classes are refused.
        /// </summary>
        private float[][] ShapeLocalBox(Pack p, int shape)
        {
            string cls = ClassOf(p, shape);
            if (cls == "hknpStaticCompoundShape")
                return new[] { Slice3(ReadVec4(source, shape + 0x80)), Slice3(ReadVec4(source, shape + 0x90)) };
            if (cls == "hknpConvexPolytopeShape" || cls == "hknpConvexShape" || cls == "hknpSphereShape" || cls == "hknpCapsuleShape")
            {
                // hknpConvexShape: vertex hkRelArray at +0x30, expanded by the convex radius at +0x14
                // (a sphere is one repeated vertex plus its radius).
                int count = ReadUInt16(source, shape + 0x30), offset = ReadUInt16(source, shape + 0x32);
                int size = ObjectSize(p, shape);
                if (count <= 0 || count > 4096 || 0x30 + offset + count * 16 > size)
                    throw new InvalidDataException(cls + " at 0x" + shape.ToString("x") + " has an implausible vertex array (" + count + " @ +0x" + offset.ToString("x") + ")");
                float radius = Math.Max(0f, ReadSingle(source, shape + 0x14));
                float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
                for (int i = 0; i < count; i++)
                {
                    float[] v = ReadVec4(source, shape + 0x30 + offset + i * 16);
                    for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], v[a] - radius); mx[a] = Math.Max(mx[a], v[a] + radius); }
                }
                return new[] { mn, mx };
            }
            if (cls == "hknpCompressedMeshShape")
            {
                // A mesh-shaped collision root (walls, wires): its extent is the reloc header's part
                // boxes, which the pipeline wrote from the same geometry.
                int aabbCount = ReadInt32(source, 0x10), aabbOff = ReadInt32(source, 0x14);
                if (aabbCount > 0 && aabbOff > 0 && aabbOff + aabbCount * 32 <= headerEnd)
                {
                    float[] mn = { float.MaxValue, float.MaxValue, float.MaxValue }, mx = { float.MinValue, float.MinValue, float.MinValue };
                    for (int i = 0; i < aabbCount; i++)
                    {
                        float[] lo = ReadVec4(source, aabbOff + i * 32), hi = ReadVec4(source, aabbOff + i * 32 + 16);
                        for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], lo[a]); mx[a] = Math.Max(mx[a], hi[a]); }
                    }
                    return new[] { mn, mx };
                }
                throw new InvalidDataException("prop collision root is a hknpCompressedMeshShape and the header carries no part boxes");
            }
            throw new InvalidDataException("prop collision root is a " + cls + "; only compound, convex, sphere, capsule and mesh roots are supported");
        }

        // Appends a class-name entry (4-byte tag, 0x09, name, NUL) to the 64-bit packfile's
        // __classnames__ section and returns the name's offset, the value virtual fixups carry.
        private int AppendClassName(string name, byte[] tagAndSeparator)
        {
            if (pack64.ClassNames == null || pack64.ClassNameBytes == null)
                throw new InvalidDataException("packfile has no __classnames__ section to grow");
            int nameOffset = pack64.ClassNameBytes.Length + 5;
            var ms = new MemoryStream();
            ms.Write(pack64.ClassNameBytes, 0, pack64.ClassNameBytes.Length);
            ms.Write(tagAndSeparator, 0, 5);
            byte[] ascii = Encoding.ASCII.GetBytes(name);
            ms.Write(ascii, 0, ascii.Length);
            ms.WriteByte(0);
            pack64.ClassNameBytes = ms.ToArray();
            pack64.Classes[nameOffset] = name;
            dirty = true;
            return nameOffset;
        }

        private static readonly Dictionary<string, int> ShapeMemorySizes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // Only shapes whose data lives outside the object carry a fixed runtime size; convex
            // shapes (polytope, sphere, capsule, plain convex) keep vertices inline and the record
            // carries the object's own packfile size (11,177 of 11,177 shipped Bespin records).
            { "hknpStaticCompoundShape", 208 }, { "hknpCompressedMeshShape", 160 }
        };

        // Appends a fully formed record (all lanes but the leaf index set) as the compound's last
        // instance, rebuilds its tree, and wires the record's shape pointer as a global fixup.
        private void AppendRecord(Compound C, byte[] rec, LeafBox box, int shapeDataRel)
        {
            int n = C.InstanceCount;
            int rel = C.Offset - pack64.DataBase, dataRel = C.DataObject - pack64.DataBase;
            float[][] domain = { ReadVec4(source, C.DataObject + 0x20), ReadVec4(source, C.DataObject + 0x30) };
            // The current array may already have been moved by an earlier edit: read through `data`.
            int arrRel = C.InstanceArray - pack64.DataBase;
            byte[] cur = new byte[n * InstanceSize];
            for (int i = 0; i < cur.Length; i++) cur[i] = data[arrRel + i];
            Dictionary<int, LeafBox> leaves = DecodeTree(CurrentNodes(C), 0, C.NodeCount, domain);
            if (leaves.Count != n) throw new InvalidDataException("compound tree yields " + leaves.Count + " leaves for " + n + " instances");
            var records = new List<byte[]>(n + 1);
            for (int i = 0; i < n; i++) records.Add(Slice(cur, i * InstanceSize, InstanceSize));
            records.Add(rec);
            var boxes = new List<LeafBox>(n + 1);
            for (int i = 0; i < n; i++) boxes.Add(leaves[i]);
            boxes.Add(box);
            float[][] newDomain;
            Dictionary<int, int> leafNode;
            byte[] nodes = BuildTree(boxes, out newDomain, out leafNode);
            for (int i = 0; i <= n; i++) WriteInt24(records[i], 0x3c, leafNode[i]);
            int newArray = AppendBlock(Concat(records));
            int newNodes = AppendBlock(PadTo16(nodes));
            WriteInt32At(rel + 0x68, n + 1);
            data[rel + 0x12] = KeyBitsFor(n + 1);   // instance key bits must cover the new count
            WriteInt32At(rel + 0x6c, unchecked((int)((uint)(n + 1) | 0x80000000u)));
            bool inlineArray = LocalTarget(rel + 0x60) == rel + 0xd0;
            RetargetLocal(rel + 0x60, newArray);
            // +0xd0 holds instance 0 only while the compound's array is still inline right after the 0xd0-byte
            // object. After a rebuild moved it and CompactData dropped the old array, the NEXT object sits there, and
            // writing a record over it corrupted that object (Hoth F1b crash at game+0x96E51A0; V29e/f/l garbage
            // compounds). FROSTYCLI_OLD_INLINE_COPY=1 restores the unconditional copy for bisects.
            if (inlineArray || Environment.GetEnvironmentVariable("FROSTYCLI_OLD_INLINE_COPY") == "1")
                for (int i = 0; i < 0x50; i++) data[rel + 0xd0 + i] = records[0][i];
            for (int a = 0; a < 3; a++)
            {
                WriteSingleAt(rel + 0x80 + a * 4, newDomain[0][a]); WriteSingleAt(rel + 0x90 + a * 4, newDomain[1][a]);
                WriteSingleAt(dataRel + 0x20 + a * 4, newDomain[0][a]); WriteSingleAt(dataRel + 0x30 + a * 4, newDomain[1][a]);
            }
            int nodeCount = nodes.Length / NodeSize;
            WriteInt32At(dataRel + 0x18, nodeCount);
            WriteInt32At(dataRel + 0x1c, unchecked((int)((uint)nodeCount | 0x80000000u)));
            RetargetLocal(dataRel + 0x10, newNodes);
            int oldLo = arrRel, oldHi = arrRel + n * InstanceSize;
            var moved = new List<int[]>(global.Count + 1);
            foreach (int[] g in global)
                moved.Add(g[0] >= oldLo && g[0] < oldHi ? new[] { newArray + (g[0] - oldLo), g[1], g[2] } : g);
            moved.Add(new[] { newArray + n * InstanceSize + 0x50, 2, shapeDataRel });
            global = moved;
            C.InstanceArray = pack64.DataBase + newArray;
            C.NodeArray = pack64.DataBase + newNodes;
            C.InstanceCount = n + 1;
            C.NodeCount = nodeCount;
            // Domain/aabb edits above went into `data`; keep the source-backed reads consistent for later slots.
            for (int a = 0; a < 3; a++) { WriteSingle(source, C.DataObject + 0x20 + a * 4, newDomain[0][a]); WriteSingle(source, C.DataObject + 0x30 + a * 4, newDomain[1][a]); }
        }

        private byte[] CurrentNodes(Compound C)
        {
            int rel = C.NodeArray - pack64.DataBase;
            var b = new byte[C.NodeCount * NodeSize];
            for (int i = 0; i < b.Length; i++) b[i] = data[rel + i];
            return b;
        }

        private List<int> ContainerRoots(Pack p)
        {
            var roots = new List<int>();
            foreach (KeyValuePair<int, string> obj in p.Objects)
            {
                if (obj.Value != "HavokPhysicsContainer") continue;
                int arr;
                if (!p.Pointers.TryGetValue(obj.Key + 0x10, out arr)) continue;
                int n = ReadInt32(source, obj.Key + 0x18);
                for (int i = 0; i < n; i++)
                {
                    int shape;
                    if (p.Pointers.TryGetValue(arr + 8 * i, out shape)) roots.Add(shape);
                }
                break;
            }
            return roots;
        }

        private string ClassOf(Pack p, int offset)
        {
            foreach (KeyValuePair<int, string> obj in p.Objects) if (obj.Key == offset) return obj.Value;
            return "?";
        }

        private int ObjectSize(Pack p, int offset)
        {
            int next = p.DataBase + p.Data.Virt;
            foreach (KeyValuePair<int, string> obj in p.Objects) if (obj.Key > offset && obj.Key < next) next = obj.Key;
            return next - offset;
        }

        private List<int> Reachable(Pack p, int start)
        {
            var seen = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int o = stack.Pop();
                if (seen.Contains(o)) continue;
                seen.Add(o);
                int size = ObjectSize(p, o);
                for (int a = o; a < o + size; a += 8)
                {
                    int target;
                    if (!p.Pointers.TryGetValue(a, out target)) continue;
                    int owner = -1;
                    foreach (KeyValuePair<int, string> obj in p.Objects) if (obj.Key <= target && obj.Key > owner) owner = obj.Key;
                    if (owner >= 0 && owner != o && !seen.Contains(owner)) stack.Push(owner);
                }
            }
            return seen;
        }

        private void AddVirtualFixups(List<int[]> entries)
        {
            // virtualTail = existing table (0xFF padded) + exports/imports (empty). Rebuild it sorted by offset.
            var all = new List<int[]>();
            for (int o = 0; o + 12 <= virtualTail.Length; o += 12)
            {
                int a = ReadInt32(virtualTail, o), b = ReadInt32(virtualTail, o + 4), c = ReadInt32(virtualTail, o + 8);
                if (a == -1) continue;
                all.Add(new[] { a, b, c });
            }
            all.AddRange(entries);
            all.Sort(delegate(int[] x, int[] y) { return x[0].CompareTo(y[0]); });
            virtualTail = FixupTable(all, 3);
            var objects = new List<KeyValuePair<int, string>>(pack64.Objects);
            foreach (int[] e in entries)
            {
                string name;
                objects.Add(new KeyValuePair<int, string>(pack64.DataBase + e[0], pack64.Classes.TryGetValue(e[2], out name) ? name : "?"));
            }
            objects.Sort(delegate(KeyValuePair<int, string> x, KeyValuePair<int, string> y) { return x.Key.CompareTo(y.Key); });
            pack64.Objects = objects;
        }

        private int MapMaterial(int[] propTable, byte propIndex)
        {
            int nFlags = ReadInt32(header, 0x28), offFlags = ReadInt32(header, 0x2c);
            int[] table = ReadInt32Array(header, offFlags, nFlags);
            if (propIndex >= propTable.Length) return propIndex < nFlags ? propIndex : 0;
            int value = propTable[propIndex];
            if (Environment.GetEnvironmentVariable("FROSTYCLI_MATERIAL_APPEND") == "1")
            {
                // Bisect switch: the pre-2026-09-14 behaviour (match any row, append after the summary row).
                for (int i = 0; i < table.Length; i++) if (table[i] == value) return i;
                var appended = new List<int>(table) { value };
                RebuildHeaderTables(null, null, appended.ToArray());
                header[0x40] = (byte)Math.Min(255, header[0x40] + 1);
                header[0x41] = (byte)Math.Max(header[0x41], appended.Count - 1);
                return appended.Count - 1;
            }
            int materials = Math.Max(0, table.Length - 1);
            for (int i = 0; i < materials; i++) if (table[i] == value) return i;
            if (IsSwbf1MaterialWord(value) && Environment.GetEnvironmentVariable("FROSTYCLI_RAW_SWBF1_MATERIALS") != "1")
            {
                // A SWBF 2015 word read with BFII's layout has its marker clear, a shifted index and scrambled
                // flags: the character meets the shape but walks through it. Reuse a host row instead.
                int nParts = ReadInt32(header, 0x1c), offParts = ReadInt32(header, 0x20);
                byte[] partRows = new byte[Math.Max(0, Math.Min(nParts, header.Length - offParts))];
                if (partRows.Length > 0) Array.Copy(header, offParts, partRows, 0, partRows.Length);
                int row = PickHostRowForSwbf1Word(value, table, partRows);
                if (row >= 0) return row;
            }
            // Unknown material. The table's last row is not a material (HavokPhysicsData::getMaterialCount()
            // = count - 1): shipped tables end in a fixed trailer (Endor_01 Shared_Art group 0x82b, props
            // 0x800 / 2015-derived 0x8000) that is NOT the OR of the rows, and header byte 0x41 is always
            // count - 2. So the new row goes before the trailer, which is left untouched; appending after it
            // turned the trailer into a material and pushed 0x41 to count - 1.
            var grown = new List<int>(table);
            int at = grown.Count > 0 ? grown.Count - 1 : 0;
            grown.Insert(at, value);
            RebuildHeaderTables(null, null, grown.ToArray());
            header[0x40] = (byte)Math.Min(255, header[0x40] + 1);
            header[0x41] = (byte)Math.Max(header[0x41], at);
            return at;
        }

        /// <summary>
        /// SWBF 2015 material words keep the 0x8 marker nibble at bits 12..15 with the index at 16..23
        /// (0x28118000, 0x8d108000, 0x2ab58009); BFII keeps it at bits 8..11 with the index at 12..19
        /// (0x2100e800, 0x0880f800, 0x09014809). A plain shift reproduces no DICE word, so a 2015 word is
        /// replaced by an existing host row rather than converted.
        /// </summary>
        public sealed class Swbf1ConvertReport
        {
            public int MaterialWords, MaterialWordsMapped, MaterialWordsUnmapped, RootRecords, RootRecordsFlagged, NestedRecords;
            public SortedSet<string> Unmapped = new SortedSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// A SWBF 2015 physics resource (prop or static-model-group) rewritten in place for BFII, same length, so the
        /// source's own 16-byte cookie stays valid:
        /// <list type="bullet">
        /// <item>every material word of the reloc header's materialFlagsAndIndices table goes through
        /// <paramref name="wordMap"/> (2015 word -> BFII word; build it from DICE's own conversions of the props both
        /// games ship -- the index is renumbered per material and the flag byte re-encoded per material, so no bit
        /// transform reproduces DICE's words). A 2015 word read as BFII data has its marker clear and scrambled flags:
        /// the character meets the shape and walks through it.</item>
        /// <item>every record of a ROOT compound (the level placement arrays) gets the 0x20 flag BFII sets on them and
        /// 2015 never does (BFII_EXACT: Hoth_01 Content roots 0x62/0x66/0x6A/0x6E x32,086, nested prop compounds 0x40
        /// x2,689; 2015 Arctic_01 roots 0x46/0x4E, nested 0x40).</item>
        /// </list>
        /// The 32-bit packfile is left as it is: the PC runtime loads only the 64-bit one.
        /// </summary>
        public static byte[] ConvertFromSwbf1(byte[] bytes, IDictionary<uint, uint> wordMap, out Swbf1ConvertReport report)
        {
            var rep = new Swbf1ConvertReport();
            report = rep;
            HavokPhysicsResource r = Parse(bytes);
            byte[] outBytes = (byte[])bytes.Clone();
            int nFlags = ReadInt32(outBytes, 0x28), offFlags = ReadInt32(outBytes, 0x2c);
            for (int i = 0; i < nFlags; i++)
            {
                int at = offFlags + 4 * i;
                uint w = (uint)ReadInt32(outBytes, at);
                rep.MaterialWords++;
                uint mapped;
                if (wordMap != null && wordMap.TryGetValue(w, out mapped))
                {
                    WriteInt32(outBytes, at, unchecked((int)mapped));
                    rep.MaterialWordsMapped++;
                }
                else if (IsSwbf1MaterialWord(unchecked((int)w)) || (w & 0xFFFFF000) == 0x8000)
                {
                    rep.MaterialWordsUnmapped++;
                    rep.Unmapped.Add(w.ToString("x8", CultureInfo.InvariantCulture));
                }
            }
            foreach (Compound c in r.Compounds)
            {
                for (int i = 0; i < c.InstanceCount; i++)
                {
                    int at = c.InstanceArray + i * InstanceSize + 0x0c;
                    int v = ReadInt32(outBytes, at);
                    int flags = v & 0xffffff;
                    if (!c.IsRoot) { rep.NestedRecords++; continue; }
                    rep.RootRecords++;
                    if ((flags & 0x40) != 0 && (flags & 0x20) == 0)
                    {
                        WriteInt32(outBytes, at, (v & ~0xffffff) | flags | 0x20);
                        rep.RootRecordsFlagged++;
                    }
                }
            }
            // the result must still parse as the same object graph
            Parse(outBytes);
            return outBytes;
        }

        /// <summary>A 2015 -> BFII material word map from JSON: { "8d1e8000": {"bf2": "2101c800", ...} } or { "8d1e8000": "2101c800" }.</summary>
        public static Dictionary<uint, uint> ReadWordMap(string json)
        {
            var map = new Dictionary<uint, uint>();
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            foreach (var kv in root)
            {
                string target = kv.Value.Type == Newtonsoft.Json.Linq.JTokenType.Object ? (string)kv.Value["bf2"] : (string)kv.Value;
                if (string.IsNullOrEmpty(target)) continue;
                map[uint.Parse(kv.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] =
                    uint.Parse(target, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return map;
        }

        internal static bool IsSwbf1MaterialWord(int word)
        {
            return ((word >> 8) & 0xF) == 0 && ((word >> 12) & 0xF) == 0x8;
        }

        /// <summary>
        /// The host table row a 2015 word should use: a BFII row with the same index and flag nibble, else the
        /// most-used row with that flag nibble, else the most-used row. The last table row is the trailer and is
        /// never picked. Returns -1 when the table has no BFII material row.
        /// </summary>
        internal static int PickHostRowForSwbf1Word(int word, int[] table, byte[] partRows)
        {
            int index = (word >> 16) & 0xFF, flag = word & 0xF;
            var use = new int[256];
            if (partRows != null) foreach (byte r in partRows) use[r]++;
            int best = -1, bestTier = 0, bestUse = -1;
            for (int r = 0; r < table.Length - 1; r++)
            {
                int w = table[r];
                if (((w >> 8) & 0xF) != 0x8) continue;
                int tier = ((w >> 12) & 0xFF) == index && (w & 0xF) == flag ? 3 : (w & 0xF) == flag ? 2 : 1;
                int u = r < use.Length ? use[r] : 0;
                if (tier > bestTier || (tier == bestTier && u > bestUse)) { best = r; bestTier = tier; bestUse = u; }
            }
            return best;
        }

        /// <summary>
        /// The group table row for each of the prop's own material rows: the contents of the prop's
        /// <c>MaterialIndicesLookup</c> in every GroupRigidBodyData's RaycastMaterialIndicesLookups.
        /// A raycast hit on the prop reports its shape's prop-local material index and the engine reads
        /// <c>lookups[detailIndex].MaterialIndices[thatIndex]</c> unchecked, so the entry covers every prop
        /// row, the summary row included (mapped like row 0). Unknown materials join the group table.
        /// </summary>
        public int[] MapPropMaterialRows(byte[] propBytes)
        {
            HavokPhysicsResource prop = Parse(propBytes);
            int[] propTable = ReadInt32Array(prop.source, ReadInt32(prop.source, 0x2c), ReadInt32(prop.source, 0x28));
            var rows = new int[Math.Max(1, propTable.Length)];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = i < propTable.Length - 1 ? MapMaterial(propTable, (byte)i) : MapMaterial(propTable, 0);
            return rows;
        }

        private int NextDetailResourceIndex()
        {
            int n = ReadInt32(header, 0x34), off = ReadInt32(header, 0x38), max = -1;
            for (int i = 0; i < n; i++) max = Math.Max(max, ReadUInt16(header, off + 2 * i));
            return max + 1;
        }

        private void GrowHeaderForMember(byte[] newMaterials, int resourceIndex)
        {
            int nMat = ReadInt32(header, 0x1c), offMat = ReadInt32(header, 0x20);
            int nDet = ReadInt32(header, 0x34), offDet = ReadInt32(header, 0x38);
            var mat = new List<byte>(Slice(header, offMat, nMat));
            mat.AddRange(newMaterials);
            var det = new List<byte>(Slice(header, offDet, 2 * nDet));
            det.AddRange(BitConverter.GetBytes((ushort)resourceIndex));
            WriteInt32(header, 0, ReadInt32(header, 0) + newMaterials.Length);
            RebuildHeaderTables(mat.ToArray(), det.ToArray(), null);
        }

        private void RebuildHeaderTables(byte[] materialIndices, byte[] detailIndices, int[] flagsTable)
        {
            if (flagsTable != null)
            {
                int nMat = ReadInt32(header, 0x1c), offMat = ReadInt32(header, 0x20);
                int nDet = ReadInt32(header, 0x34), offDet = ReadInt32(header, 0x38);
                byte[] mat = materialIndices ?? Slice(header, offMat, nMat);
                byte[] det = detailIndices ?? Slice(header, offDet, 2 * nDet);
                var flags = new byte[4 * flagsTable.Length];
                for (int i = 0; i < flagsTable.Length; i++) WriteInt32(flags, 4 * i, flagsTable[i]);
                int newMat = 0x50, newFlags = Align16(newMat + mat.Length), newDet = Align16(newFlags + flags.Length);
                var h = new byte[Align16(newDet + det.Length)];
                Array.Copy(header, 0, h, 0, 0x50);
                int[][] arrays = { new[] { 0x4, 0, newMat }, new[] { 0x10, 0, newMat }, new[] { 0x1c, mat.Length, newMat },
                                   new[] { 0x28, flagsTable.Length, newFlags }, new[] { 0x34, det.Length / 2, newDet } };
                foreach (int[] a in arrays) { WriteInt32(h, a[0], a[1]); WriteInt32(h, a[0] + 4, a[2]); }
                Array.Copy(mat, 0, h, newMat, mat.Length);
                Array.Copy(flags, 0, h, newFlags, flags.Length);
                Array.Copy(det, 0, h, newDet, det.Length);
                header = h;
                return;
            }
            RebuildHeader(materialIndices, detailIndices);
        }

        private static int[] ReadInt32Array(byte[] b, int off, int count)
        {
            var r = new int[Math.Max(0, count)];
            for (int i = 0; i < r.Length; i++) r[i] = ReadInt32(b, off + 4 * i);
            return r;
        }

        // ------------------------------------------------------------------ move / remove

        /// <summary>
        /// Rewrites the transform of an existing placement in every root compound that holds it.
        /// The record keeps its shape, part tag and material; its leaf box is the old one carried
        /// through the change of transform, and every affected tree is rebuilt.
        /// </summary>
        /// <summary>
        /// Translates many placements at once by (dx, dy, dz) in every root compound that holds them,
        /// keeping rotation, scale and the w lanes, shifting each leaf box by the same delta and
        /// rebuilding each compound's tree once. Instance counts and part tags are untouched.
        /// Returns the number of records moved in the placement compound.
        /// </summary>
        public int ShiftInstances(ICollection<int> placementIndices, float dx, float dy, float dz, out string detail)
        {
            Compound A = Placement;
            if (A == null) throw new InvalidOperationException("resource has no root compound");
            byte[] arrA = CurrentInstances(A);
            var keys = new HashSet<string>();
            foreach (int index in placementIndices)
            {
                if (index < 0 || index >= A.InstanceCount) throw new ArgumentOutOfRangeException("placementIndices", "instance " + index + " is out of range (compound holds " + A.InstanceCount + ")");
                keys.Add(TransformKey(arrA, index * InstanceSize));
            }
            var selectedA = new HashSet<int>(placementIndices);
            var report = new StringBuilder();
            int movedA = 0;
            foreach (Compound C in Compounds)
            {
                if (!C.IsRoot) continue;
                byte[] cur = CurrentInstances(C);
                float[][] domain = { ReadVec4(source, C.DataObject + 0x20), ReadVec4(source, C.DataObject + 0x30) };
                Dictionary<int, LeafBox> leaves = DecodeTree(CurrentNodes(C), 0, C.NodeCount, domain);
                if (leaves.Count != C.InstanceCount) throw new InvalidDataException("compound tree yields " + leaves.Count + " leaves for " + C.InstanceCount + " instances");
                var records = new List<byte[]>(C.InstanceCount);
                var boxes = new List<LeafBox>(C.InstanceCount);
                var oldToNew = new Dictionary<int, int>();
                int moved = 0;
                for (int i = 0; i < C.InstanceCount; i++)
                {
                    byte[] rec = Slice(cur, i * InstanceSize, InstanceSize);
                    LeafBox box = leaves[i];
                    bool hit = C == A ? selectedA.Contains(i) : keys.Contains(TransformKey(cur, i * InstanceSize));
                    if (hit)
                    {
                        float[] d = { dx, dy, dz };
                        for (int k = 0; k < 3; k++)
                        {
                            float v = BitConverter.ToSingle(rec, 0x30 + 4 * k) + d[k];
                            Buffer.BlockCopy(BitConverter.GetBytes(v), 0, rec, 0x30 + 4 * k, 4);
                        }
                        box = new LeafBox
                        {
                            Min = new[] { box.Min[0] + dx, box.Min[1] + dy, box.Min[2] + dz },
                            Max = new[] { box.Max[0] + dx, box.Max[1] + dy, box.Max[2] + dz }
                        };
                        moved++;
                    }
                    records.Add(rec);
                    boxes.Add(box);
                    oldToNew[i] = i;
                }
                if (moved == 0) { report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(" untouched; "); continue; }
                if (C == A) movedA = moved;
                RebuildCompound(C, records, boxes, oldToNew, null);
                report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(": ").Append(moved).Append(" shifted; ");
            }
            dirty = true;
            detail = report.ToString().TrimEnd(' ', ';');
            return movedA;
        }

        /// <summary>
        /// Composes a rigid parent transform onto many placements at once: every selected record in
        /// every root compound gets world = parent * record (rotation columns and translation rotated
        /// by the parent, translation offset by it; scale and the w lanes kept, flags recomputed). Each
        /// leaf box is carried through the change (record-local box, then the new transform), and each
        /// compound's tree is rebuilt once. This is how a static group authored inside a prefab (2015
        /// Arctic_01's PF_Arctic_01_Base_02_CombinedforE3) is baked into level space, the way every
        /// shipped BFII group is stored (all 1,228 indexed shipped groups have an identity Transform).
        /// Returns the number of records changed in the placement compound.
        /// </summary>
        public int ComposeInstances(ICollection<int> placementIndices, LinearTransformData parent, out string detail)
        {
            Compound A = Placement;
            if (A == null) throw new InvalidOperationException("resource has no root compound");
            float[][] pCols; float[] pScale, pTrans;
            DecomposeTransform(parent, out pCols, out pScale, out pTrans);
            for (int a = 0; a < 3; a++)
                if (Math.Abs(pScale[a] - 1f) > 1e-3f) throw new ArgumentException("the parent transform must be rigid (unit axes); axis " + a + " has length " + pScale[a].ToString("R", CultureInfo.InvariantCulture));
            byte[] arrA = CurrentInstances(A);
            var keys = new HashSet<string>();
            foreach (int index in placementIndices)
            {
                if (index < 0 || index >= A.InstanceCount) throw new ArgumentOutOfRangeException("placementIndices", "instance " + index + " is out of range (compound holds " + A.InstanceCount + ")");
                keys.Add(TransformKey(arrA, index * InstanceSize));
            }
            var selectedA = new HashSet<int>(placementIndices);
            var report = new StringBuilder();
            int movedA = 0;
            foreach (Compound C in Compounds)
            {
                if (!C.IsRoot) continue;
                byte[] cur = CurrentInstances(C);
                float[][] domain = { ReadVec4(source, C.DataObject + 0x20), ReadVec4(source, C.DataObject + 0x30) };
                Dictionary<int, LeafBox> leaves = DecodeTree(CurrentNodes(C), 0, C.NodeCount, domain);
                if (leaves.Count != C.InstanceCount) throw new InvalidDataException("compound tree yields " + leaves.Count + " leaves for " + C.InstanceCount + " instances");
                var records = new List<byte[]>(C.InstanceCount);
                var boxes = new List<LeafBox>(C.InstanceCount);
                var oldToNew = new Dictionary<int, int>();
                int moved = 0;
                for (int i = 0; i < C.InstanceCount; i++)
                {
                    byte[] rec = Slice(cur, i * InstanceSize, InstanceSize);
                    LeafBox box = leaves[i];
                    bool hit = C == A ? selectedA.Contains(i) : keys.Contains(TransformKey(cur, i * InstanceSize));
                    if (hit)
                    {
                        float[][] oldCols = { ReadVec4(rec, 0), ReadVec4(rec, 0x10), ReadVec4(rec, 0x20) };
                        float[] oldScale = ReadVec4(rec, 0x40), oldTrans = ReadVec4(rec, 0x30);
                        float[][] local = InverseTransformBox(box.Min, box.Max, oldCols, oldScale, oldTrans);
                        float[][] newCols; float[] newTrans;
                        ComposeRigid(oldCols, oldTrans, pCols, pTrans, out newCols, out newTrans);
                        float[] scale = Slice3(oldScale);
                        WriteTransformInto(rec, newCols, scale, newTrans);
                        float[][] world = TransformBox(local[0], local[1], newCols, scale, newTrans);
                        box = new LeafBox { Min = world[0], Max = world[1] };
                        moved++;
                    }
                    records.Add(rec);
                    boxes.Add(box);
                    oldToNew[i] = i;
                }
                if (moved == 0) { report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(" untouched; "); continue; }
                if (C == A) movedA = moved;
                RebuildCompound(C, records, boxes, oldToNew, null);
                report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(": ").Append(moved).Append(" composed; ");
            }
            dirty = true;
            detail = report.ToString().TrimEnd(' ', ';');
            return movedA;
        }

        /// <summary>
        /// world = parent * local for a rotation-columns + translation pair: each column and the
        /// translation are rotated by the parent's columns, and the parent's translation is added.
        /// Columns are unit vectors (scale is kept separately by the caller).
        /// </summary>
        public static void ComposeRigid(float[][] cols, float[] trans, float[][] pCols, float[] pTrans, out float[][] outCols, out float[] outTrans)
        {
            outCols = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                outCols[c] = new float[3];
                for (int k = 0; k < 3; k++)
                    outCols[c][k] = (float)((double)pCols[0][k] * cols[c][0] + (double)pCols[1][k] * cols[c][1] + (double)pCols[2][k] * cols[c][2]);
            }
            outTrans = new float[3];
            for (int k = 0; k < 3; k++)
                outTrans[k] = (float)((double)pCols[0][k] * trans[0] + (double)pCols[1][k] * trans[1] + (double)pCols[2][k] * trans[2] + pTrans[k]);
        }

        public void MoveInstance(int index, LinearTransformData transform, out string detail)
        {
            Compound A = Placement;
            if (A == null) throw new InvalidOperationException("resource has no root compound");
            if (index < 0 || index >= A.InstanceCount) throw new ArgumentOutOfRangeException("index", "instance " + index + " is out of range (compound holds " + A.InstanceCount + ")");
            float[][] cols; float[] scale, trans;
            DecomposeTransform(transform, out cols, out scale, out trans);

            byte[] arrA = CurrentInstances(A);
            string key = TransformKey(arrA, index * InstanceSize);
            var report = new StringBuilder();
            foreach (Compound C in Compounds)
            {
                if (!C.IsRoot) continue;
                byte[] cur = CurrentInstances(C);
                int si = C == A ? index : -1;
                if (si < 0)
                    for (int i = 0; i < C.InstanceCount && si < 0; i++)
                        if (TransformKey(cur, i * InstanceSize) == key) si = i;
                if (si < 0) { report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(" skipped (placement absent); "); continue; }

                float[][] domain = { ReadVec4(source, C.DataObject + 0x20), ReadVec4(source, C.DataObject + 0x30) };
                Dictionary<int, LeafBox> leaves = DecodeTree(CurrentNodes(C), 0, C.NodeCount, domain);
                if (leaves.Count != C.InstanceCount) throw new InvalidDataException("compound tree yields " + leaves.Count + " leaves for " + C.InstanceCount + " instances");

                byte[] rec = Slice(cur, si * InstanceSize, InstanceSize);
                float[][] oldCols = { ReadVec4(rec, 0), ReadVec4(rec, 0x10), ReadVec4(rec, 0x20) };
                float[] oldScale = ReadVec4(rec, 0x40), oldTrans = ReadVec4(rec, 0x30);
                float[][] local = InverseTransformBox(leaves[si].Min, leaves[si].Max, oldCols, oldScale, oldTrans);
                float[][] world = TransformBox(local[0], local[1], cols, scale, trans);
                WriteTransformInto(rec, cols, scale, trans);

                var records = new List<byte[]>(C.InstanceCount);
                var boxes = new List<LeafBox>(C.InstanceCount);
                var oldToNew = new Dictionary<int, int>();
                for (int i = 0; i < C.InstanceCount; i++)
                {
                    records.Add(i == si ? rec : Slice(cur, i * InstanceSize, InstanceSize));
                    boxes.Add(i == si ? new LeafBox { Min = world[0], Max = world[1] } : leaves[i]);
                    oldToNew[i] = i;
                }
                RebuildCompound(C, records, boxes, oldToNew, null);
                report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(": instance ").Append(si).Append(" moved; ");
            }
            dirty = true;
            detail = report.ToString().TrimEnd(' ', ';');
        }

        /// <summary>
        /// Deletes a placement from every root compound that holds it, renumbering the part tags of
        /// the records after it and shrinking the reloc header's per-part and per-instance tables.
        /// Returns the number of physics parts the placement owned.
        /// </summary>
        public int RemoveInstance(int index, out string detail)
        {
            Compound A = Placement;
            if (A == null) throw new InvalidOperationException("resource has no root compound");
            if (index < 0 || index >= A.InstanceCount) throw new ArgumentOutOfRangeException("index", "instance " + index + " is out of range (compound holds " + A.InstanceCount + ")");
            byte[] arrA = CurrentInstances(A);
            int tag = ReadUInt16(arrA, index * InstanceSize + 0x58);
            int nextTag = index + 1 < A.InstanceCount ? ReadUInt16(arrA, (index + 1) * InstanceSize + 0x58) : ReadInt32(header, 0);
            int parts = nextTag - tag;
            if (parts <= 0) throw new InvalidDataException("instance " + index + " owns no physics parts");
            string key = TransformKey(arrA, index * InstanceSize);

            var report = new StringBuilder();
            foreach (Compound C in Compounds)
            {
                if (!C.IsRoot) continue;
                byte[] cur = CurrentInstances(C);
                int si = C == A ? index : -1;
                if (si < 0)
                    for (int i = 0; i < C.InstanceCount && si < 0; i++)
                        if (TransformKey(cur, i * InstanceSize) == key) si = i;
                if (si < 0) { report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(" skipped (placement absent); "); continue; }
                if (C.InstanceCount <= 1) throw new InvalidOperationException("compound@0x" + C.Offset.ToString("x") + " would be left empty; removing the last placement is not supported");

                float[][] domain = { ReadVec4(source, C.DataObject + 0x20), ReadVec4(source, C.DataObject + 0x30) };
                Dictionary<int, LeafBox> leaves = DecodeTree(CurrentNodes(C), 0, C.NodeCount, domain);
                if (leaves.Count != C.InstanceCount) throw new InvalidDataException("compound tree yields " + leaves.Count + " leaves for " + C.InstanceCount + " instances");

                var records = new List<byte[]>(C.InstanceCount - 1);
                var boxes = new List<LeafBox>(C.InstanceCount - 1);
                var oldToNew = new Dictionary<int, int>();
                for (int i = 0; i < C.InstanceCount; i++)
                {
                    if (i == si) { oldToNew[i] = -1; continue; }
                    byte[] rec = Slice(cur, i * InstanceSize, InstanceSize);
                    if (i > si) WriteUInt16(rec, 0x58, (ushort)(ReadUInt16(rec, 0x58) - parts));
                    oldToNew[i] = records.Count;
                    records.Add(rec);
                    boxes.Add(leaves[i]);
                }
                RebuildCompound(C, records, boxes, oldToNew, null);
                report.Append("compound@0x").Append(C.Offset.ToString("x")).Append(": instance ").Append(si).Append(" removed; ");
            }

            // Reloc header: drop the placement's parts and its detail-resource index.
            int nMat = ReadInt32(header, 0x1c), offMat = ReadInt32(header, 0x20);
            int nDet = ReadInt32(header, 0x34), offDet = ReadInt32(header, 0x38);
            var mat = new List<byte>(Slice(header, offMat, nMat));
            if (tag + parts <= mat.Count) mat.RemoveRange(tag, parts);
            var det = new List<byte>(Slice(header, offDet, 2 * nDet));
            if (2 * index + 2 <= det.Count) det.RemoveRange(2 * index, 2);
            WriteInt32(header, 0, ReadInt32(header, 0) - parts);
            RebuildHeader(mat.ToArray(), det.ToArray());
            dirty = true;
            detail = "parts " + tag + ".." + (tag + parts - 1) + " dropped; " + report.ToString().TrimEnd(' ', ';');
            return parts;
        }

        private byte[] CurrentInstances(Compound C)
        {
            int rel = C.InstanceArray - pack64.DataBase;
            var b = new byte[C.InstanceCount * InstanceSize];
            for (int i = 0; i < b.Length; i++) b[i] = data[rel + i];
            return b;
        }

        private static void DecomposeTransform(LinearTransformData transform, out float[][] cols, out float[] scale, out float[] trans)
        {
            float[] right = transform.Right ?? new[] { 1f, 0f, 0f };
            float[] up = transform.Up ?? new[] { 0f, 1f, 0f };
            float[] forward = transform.Forward ?? new[] { 0f, 0f, 1f };
            trans = transform.Trans ?? new float[3];
            scale = new[] { Length(right), Length(up), Length(forward) };
            if (scale[0] <= 0 || scale[1] <= 0 || scale[2] <= 0) throw new ArgumentException("transform has a zero-length axis");
            cols = new[] { Div(right, scale[0]), Div(up, scale[1]), Div(forward, scale[2]) };
            if (Dot(Cross(cols[0], cols[1]), cols[2]) < 0)
                throw new ArgumentException("mirrored transforms are not supported: the pipeline bakes a mirrored copy of the shape for those");
        }

        private static void WriteTransformInto(byte[] rec, float[][] cols, float[] scale, float[] trans)
        {
            for (int c = 0; c < 3; c++)
                for (int a = 0; a < 3; a++) WriteSingle(rec, c * 0x10 + a * 4, cols[c][a]);
            for (int a = 0; a < 3; a++) { WriteSingle(rec, 0x30 + a * 4, trans[a]); WriteSingle(rec, 0x40 + a * 4, scale[a]); }
            WriteInt24(rec, 0x0c, FlagsFor(cols, scale));
        }

        // Replaces a compound's instance array and tree with the given records (whose shape pointers
        // are remapped from their old positions via oldToNew; -1 drops one) and boxes. An extra
        // shape target for a brand-new record can be passed as (newIndex -> data-relative shape).
        private void RebuildCompound(Compound C, List<byte[]> records, List<LeafBox> boxes, Dictionary<int, int> oldToNew, Dictionary<int, int> newShapes)
        {
            int rel = C.Offset - pack64.DataBase, dataRel = C.DataObject - pack64.DataBase;
            float[][] newDomain;
            Dictionary<int, int> leafNode;
            byte[] nodes = BuildTree(boxes, out newDomain, out leafNode);
            for (int i = 0; i < records.Count; i++) WriteInt24(records[i], 0x3c, leafNode[i]);
            int newArray = AppendBlock(Concat(records));
            int newNodes = AppendBlock(PadTo16(nodes));
            WriteInt32At(rel + 0x68, records.Count);
            data[rel + 0x12] = KeyBitsFor(records.Count);   // instance key bits must cover the new count
            WriteInt32At(rel + 0x6c, unchecked((int)((uint)records.Count | 0x80000000u)));
            bool inlineArray = LocalTarget(rel + 0x60) == rel + 0xd0;
            RetargetLocal(rel + 0x60, newArray);
            // +0xd0 holds instance 0 only while the compound's array is still inline right after the 0xd0-byte
            // object. After a rebuild moved it and CompactData dropped the old array, the NEXT object sits there, and
            // writing a record over it corrupted that object (Hoth F1b crash at game+0x96E51A0; V29e/f/l garbage
            // compounds). FROSTYCLI_OLD_INLINE_COPY=1 restores the unconditional copy for bisects.
            if (inlineArray || Environment.GetEnvironmentVariable("FROSTYCLI_OLD_INLINE_COPY") == "1")
                for (int i = 0; i < 0x50; i++) data[rel + 0xd0 + i] = records[0][i];
            for (int a = 0; a < 3; a++)
            {
                WriteSingleAt(rel + 0x80 + a * 4, newDomain[0][a]); WriteSingleAt(rel + 0x90 + a * 4, newDomain[1][a]);
                WriteSingleAt(dataRel + 0x20 + a * 4, newDomain[0][a]); WriteSingleAt(dataRel + 0x30 + a * 4, newDomain[1][a]);
                WriteSingle(source, C.DataObject + 0x20 + a * 4, newDomain[0][a]); WriteSingle(source, C.DataObject + 0x30 + a * 4, newDomain[1][a]);
            }
            int nodeCount = nodes.Length / NodeSize;
            WriteInt32At(dataRel + 0x18, nodeCount);
            WriteInt32At(dataRel + 0x1c, unchecked((int)((uint)nodeCount | 0x80000000u)));
            RetargetLocal(dataRel + 0x10, newNodes);
            int oldLo = C.InstanceArray - pack64.DataBase, oldHi = oldLo + C.InstanceCount * InstanceSize;
            var moved = new List<int[]>(global.Count);
            foreach (int[] g in global)
            {
                if (g[0] >= oldLo && g[0] < oldHi)
                {
                    int i = (g[0] - oldLo) / InstanceSize, off = (g[0] - oldLo) % InstanceSize;
                    int i2;
                    if (!oldToNew.TryGetValue(i, out i2) || i2 < 0) continue;
                    moved.Add(new[] { newArray + i2 * InstanceSize + off, g[1], g[2] });
                }
                else moved.Add(g);
            }
            if (newShapes != null)
                foreach (KeyValuePair<int, int> kv in newShapes) moved.Add(new[] { newArray + kv.Key * InstanceSize + 0x50, 2, kv.Value });
            global = moved;
            C.InstanceArray = pack64.DataBase + newArray;
            C.NodeArray = pack64.DataBase + newNodes;
            C.InstanceCount = records.Count;
            C.NodeCount = nodeCount;
        }

        // ------------------------------------------------------------------ small helpers

        // hknpCompoundShape keeps its instance key bits at +0x12: ceil(log2(instances + 1)), as the pipeline's
        // hknpStaticCompoundShape(cinfo) constructor sets them (all 309 compounds in Endor's shipped group resources agree).
        // Appending past 2^bits - 1 instances without raising them overflows the compound's shape keys: Endor's Shared_Art
        // root holds 10,046 instances at 14 bits, and SULLUST_V29b (8,398 appended placements) crashed in Havok on it.
        private static byte KeyBitsFor(int instances)
        {
            int bits = 0;
            for (uint v = (uint)Math.Max(0, instances); v != 0; v >>= 1) bits++;
            return (byte)bits;
        }

        private int BlobLength()
        {
            int f64 = 0;
            foreach (Section s in pack64.Sections) f64 += s == pack64.Data ? FixupTable(local, 2).Length + FixupTable(global, 3).Length : s.FixupTotal;
            return 8 + size32 + f64 + (source.Length - relocAt);
        }

        private int AppendBlock(byte[] block) { return AppendBlock(block, 0); }

        private int AppendBlock(byte[] block, int kind)
        {
            int start = Align16(data.Count);
            while (data.Count < start) data.Add(0);
            data.AddRange(block);
            blocks.Add(new[] { start, block.Length, kind });
            return start;
        }

        // Every compound rebuild appends a fresh instance array and node block and leaves the previous
        // ones behind. Before serialising, drop the blocks no compound points at any more and slide the
        // rest down, remapping the fixups, the compound bookkeeping and the virtual fixup table.
        private void CompactData()
        {
            if (blocks.Count == 0) return;
            var liveStarts = new HashSet<int>();
            foreach (Compound C in Compounds) { liveStarts.Add(C.InstanceArray - pack64.DataBase); liveStarts.Add(C.NodeArray - pack64.DataBase); }
            // Dead regions, trimmed to whole 16-byte lines so nothing after them loses its alignment.
            var dead = new List<int[]>();   // {start, end}
            foreach (int[] b in blocks)
            {
                if (b[2] != 0 || liveStarts.Contains(b[0])) continue;
                int s = Align16(b[0]), e = (b[0] + b[1]) & ~15;
                if (e > s) dead.Add(new[] { s, e });
            }
            if (dead.Count == 0) return;
            dead.Sort(delegate(int[] x, int[] y) { return x[0].CompareTo(y[0]); });
            var merged = new List<int[]>();
            foreach (int[] d in dead)
            {
                if (merged.Count > 0 && d[0] <= merged[merged.Count - 1][1]) merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], d[1]);
                else merged.Add(new[] { d[0], d[1] });
            }
            // New layout: everything except the dead regions, in order. map() subtracts the dead
            // bytes before an offset; offsets inside a dead region are a bug and throw.
            var newData = new List<byte>(data.Count);
            int cursor = 0;
            foreach (int[] d in merged)
            {
                for (int i = cursor; i < d[0]; i++) newData.Add(data[i]);
                cursor = d[1];
            }
            for (int i = cursor; i < data.Count; i++) newData.Add(data[i]);
            Func<int, int> map = delegate(int off)
            {
                int shift = 0;
                foreach (int[] d in merged)
                {
                    if (off >= d[1]) shift += d[1] - d[0];
                    else if (off >= d[0]) throw new InvalidDataException("data offset 0x" + off.ToString("x") + " points into a dropped block");
                    else break;
                }
                return off - shift;
            };
            var kept = new List<int[]>();
            foreach (int[] b in blocks)
            {
                if (b[2] == 0 && !liveStarts.Contains(b[0])) continue;
                kept.Add(new[] { map(b[0]), b[1], b[2] });
            }
            for (int i = 0; i < local.Count; i++) local[i] = new[] { map(local[i][0]), map(local[i][1]) };
            for (int i = 0; i < global.Count; i++) global[i] = new[] { map(global[i][0]), global[i][1], global[i][1] == 2 ? map(global[i][2]) : global[i][2] };
            foreach (Compound C in Compounds)
            {
                C.Offset = pack64.DataBase + map(C.Offset - pack64.DataBase);
                C.DataObject = pack64.DataBase + map(C.DataObject - pack64.DataBase);
                C.InstanceArray = pack64.DataBase + map(C.InstanceArray - pack64.DataBase);
                C.NodeArray = pack64.DataBase + map(C.NodeArray - pack64.DataBase);
            }
            var virt = new List<int[]>();
            for (int o = 0; o + 12 <= virtualTail.Length; o += 12)
            {
                int a = ReadInt32(virtualTail, o), b = ReadInt32(virtualTail, o + 4), c = ReadInt32(virtualTail, o + 8);
                if (a == -1) continue;
                virt.Add(new[] { map(a), b, c });
            }
            virt.Sort(delegate(int[] x, int[] y) { return x[0].CompareTo(y[0]); });
            virtualTail = FixupTable(virt, 3);
            var objects = new List<KeyValuePair<int, string>>(pack64.Objects.Count);
            foreach (KeyValuePair<int, string> kv in pack64.Objects)
                objects.Add(new KeyValuePair<int, string>(pack64.DataBase + map(kv.Key - pack64.DataBase), kv.Value));
            objects.Sort(delegate(KeyValuePair<int, string> x, KeyValuePair<int, string> y) { return x.Key.CompareTo(y.Key); });
            pack64.Objects = objects;
            data = newData;
            blocks = kept;
            originalDataLen = Math.Min(originalDataLen, data.Count);
        }

        private void RetargetLocal(int src, int dst)
        {
            for (int i = 0; i < local.Count; i++)
                if (local[i][0] == src) { local[i] = new[] { src, dst }; return; }
            throw new InvalidDataException("no local fixup at data offset 0x" + src.ToString("x"));
        }

        private int LocalTarget(int src)
        {
            for (int i = 0; i < local.Count; i++)
                if (local[i][0] == src) return local[i][1];
            return -1;
        }

        private void WriteInt32At(int rel, int value) { byte[] b = BitConverter.GetBytes(value); for (int i = 0; i < 4; i++) data[rel + i] = b[i]; }
        private void WriteSingleAt(int rel, float value) { byte[] b = BitConverter.GetBytes(value); for (int i = 0; i < 4; i++) data[rel + i] = b[i]; }

        private static byte[] FixupTable(List<int[]> entries, int ints)
        {
            var ms = new MemoryStream();
            foreach (int[] e in entries) for (int i = 0; i < ints; i++) { byte[] b = BitConverter.GetBytes(e[i]); ms.Write(b, 0, 4); }
            while (ms.Length % 16 != 0) ms.WriteByte(0xff);
            return ms.ToArray();
        }

        private static string TransformKey(byte[] b, int o)
        {
            var sb = new StringBuilder(120);
            foreach (int off in new[] { 0, 0x10, 0x20, 0x30 })
                for (int i = 0; i < 12; i++) sb.Append(b[o + off + i].ToString("x2"));
            return sb.ToString();
        }

        private static float[][] TransformBox(float[] mn, float[] mx, float[][] cols, float[] scale, float[] trans)
        {
            float[] omn = { float.MaxValue, float.MaxValue, float.MaxValue }, omx = { float.MinValue, float.MinValue, float.MinValue };
            for (int corner = 0; corner < 8; corner++)
            {
                float[] l = { (corner & 1) != 0 ? mx[0] : mn[0], (corner & 2) != 0 ? mx[1] : mn[1], (corner & 4) != 0 ? mx[2] : mn[2] };
                for (int k = 0; k < 3; k++)
                {
                    float w = trans[k];
                    for (int c = 0; c < 3; c++) w += cols[c][k] * scale[c] * l[c];
                    omn[k] = Math.Min(omn[k], w); omx[k] = Math.Max(omx[k], w);
                }
            }
            return new[] { omn, omx };
        }

        private static float[][] InverseTransformBox(float[] mn, float[] mx, float[][] cols, float[] scale, float[] trans)
        {
            // Columns are orthonormal, so the inverse rotation is the transpose; scale divides.
            float[] omn = { float.MaxValue, float.MaxValue, float.MaxValue }, omx = { float.MinValue, float.MinValue, float.MinValue };
            for (int corner = 0; corner < 8; corner++)
            {
                float[] w = { ((corner & 1) != 0 ? mx[0] : mn[0]) - trans[0], ((corner & 2) != 0 ? mx[1] : mn[1]) - trans[1], ((corner & 4) != 0 ? mx[2] : mn[2]) - trans[2] };
                for (int c = 0; c < 3; c++)
                {
                    float l = (cols[c][0] * w[0] + cols[c][1] * w[1] + cols[c][2] * w[2]) / (scale[c] == 0 ? 1f : scale[c]);
                    omn[c] = Math.Min(omn[c], l); omx[c] = Math.Max(omx[c], l);
                }
            }
            return new[] { omn, omx };
        }

        private static List<int> FindMagic(byte[] b)
        {
            var r = new List<int>();
            for (int i = 0; i + 8 <= b.Length; i++)
            {
                if (b[i] != 0x57) continue;
                bool ok = true;
                for (int k = 1; k < 8 && ok; k++) ok = b[i + k] == Magic[k];
                if (ok) r.Add(i);
            }
            return r;
        }

        private static int Align16(int x) { return (x + 15) & ~15; }
        private static long Align16(long x) { return (x + 15) & ~15L; }
        private static void Pad(MemoryStream ms, int n) { for (int i = 0; i < n; i++) ms.WriteByte(0); }
        private static byte[] Slice(byte[] b, int o, int n) { var r = new byte[n]; Array.Copy(b, o, r, 0, n); return r; }
        private static float[] Slice3(float[] v) { return new[] { v[0], v[1], v[2] }; }
        private static byte[] PadTo16(byte[] b) { int n = Align16(b.Length); if (n == b.Length) return b; var r = new byte[n]; Array.Copy(b, r, b.Length); return r; }
        private static byte[] Concat(List<byte[]> parts) { var ms = new MemoryStream(); foreach (byte[] p in parts) ms.Write(p, 0, p.Length); return ms.ToArray(); }
        private static int ReadInt32(byte[] b, int o) { return BitConverter.ToInt32(b, o); }
        private static int ReadUInt16(byte[] b, int o) { return BitConverter.ToUInt16(b, o); }
        private static float ReadSingle(byte[] b, int o) { return BitConverter.ToSingle(b, o); }
        private static float[] ReadVec4(byte[] b, int o) { return new[] { ReadSingle(b, o), ReadSingle(b, o + 4), ReadSingle(b, o + 8), ReadSingle(b, o + 12) }; }
        private static void WriteInt32(byte[] b, int o, int v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4); }
        private static void WriteUInt16(byte[] b, int o, ushort v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 2); }
        private static void WriteSingle(byte[] b, int o, float v) { Array.Copy(BitConverter.GetBytes(v), 0, b, o, 4); }
        private static void WriteInt24(byte[] b, int o, int v) { uint old = BitConverter.ToUInt32(b, o); WriteInt32(b, o, unchecked((int)((old & 0xff000000u) | ((uint)v & 0xffffffu)))); }
        private static string ReadAsciiZ(byte[] b, int o, int n) { int e = o; while (e < o + n && b[e] != 0) e++; return Encoding.ASCII.GetString(b, o, e - o); }
        private static float Length(float[] v) { return (float)Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); }
        private static float[] Div(float[] v, float s) { return new[] { v[0] / s, v[1] / s, v[2] / s }; }
        private static float Dot(float[] a, float[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static float[] Cross(float[] a, float[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
    }
}
