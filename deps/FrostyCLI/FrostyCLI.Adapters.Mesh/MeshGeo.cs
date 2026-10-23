using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // MESHGEO1: a mesh's complete geometry as the source game stores it -- every LOD, every section in MeshSet order (unnamed
    // depth/shadow sections included), the section's vertex declaration, its raw vertex bytes and its section-relative
    // indices, plus the LOD's subset categories. Written under the source game's profile (`mesh geo-dump`, one profile per
    // process) and read back under the target's, where MeshGeoPort re-encodes each vertex element into a donor section's
    // declaration. Nothing is decoded on the way out, so nothing is lost: RadiosityTexCoord, extra TexCoords, colours,
    // BinormalSign ... all travel as the source wrote them.
    public sealed class GeoElement
    {
        public VertexElementUsage Usage;
        public VertexElementFormat Format;
        public byte Offset;
        public byte Stream;
        public int Size { get { return FormatSize(Format); } }

        public static int FormatSize(VertexElementFormat f)
        {
            var e = new GeometryDeclarationDesc.Element { Format = f };
            return e.Size;
        }
        public override string ToString() { return Usage + "@" + Offset + "/" + Format + (Stream != 0 ? "/s" + Stream : ""); }
    }

    public sealed class GeoSection
    {
        public string Name = "";
        public int MaterialId;
        public PrimitiveType Prim = PrimitiveType.PrimitiveType_TriangleList;
        public int VertexCount, PrimitiveCount;
        public uint LightMapUvMappingIndex;
        public float[] TexCoordRatios = new float[6];
        public List<GeoElement> Elements = new List<GeoElement>();
        public int Stride;
        public byte[] Vertices = new byte[0];      // stream 0, VertexCount x Stride
        public uint[] Indices = new uint[0];       // section-relative
        public bool Named { get { return !string.IsNullOrEmpty(Name); } }

        public GeoElement Find(VertexElementUsage u)
        {
            foreach (GeoElement e in Elements) if (e.Usage == u && e.Stream == 0) return e;
            return null;
        }
        public string DeclString()
        {
            var parts = new List<string>();
            foreach (GeoElement e in Elements) parts.Add(e.ToString());
            return string.Join(" ", parts) + " stride=" + Stride;
        }
    }

    public sealed class GeoLod
    {
        public uint Flags;
        public List<List<byte>> Categories = new List<List<byte>>();
        public List<GeoSection> Sections = new List<GeoSection>();
        public ushort CategoryBits(int section)
        {
            ushort bits = 0;
            for (int c = 0; c < Categories.Count; c++) if (Categories[c].Contains((byte)section)) bits |= (ushort)(1 << c);
            return bits;
        }
    }

    public sealed class GeoMesh
    {
        public const string Magic = "MESHGEO1";
        public string Path = "";
        public string SourceGame = "";
        public List<GeoLod> Lods = new List<GeoLod>();
        // A composite source's parts (MeshSet m_partTransforms / m_partBoundingBoxes); empty for rigid meshes and for dumps
        // written before the PARTS1 trailer existed. A static-group member of a composite supplies exactly one physics part
        // per mesh part, so a composite port must carry the SOURCE's parts, not its template's (HOTH N5: BoxMedium_01 on
        // Box_M_01_A came out with 3 parts for 4 physics parts, LightGround_03 with 3 for 2).
        public List<LinearTransform> PartTransforms = new List<LinearTransform>();
        public List<AxisAlignedBox> PartBoxes = new List<AxisAlignedBox>();
        public const string PartsMagic = "PARTS1";

        /// <summary>The parts a section's vertices belong to (BoneIndices[0]: 2015 and BFII composites store the part index itself).</summary>
        public static List<int> SectionParts(GeoSection s)
        {
            var parts = new SortedSet<int>();
            GeoElement bi = s.Find(VertexElementUsage.BoneIndices);
            if (bi != null && s.Vertices != null)
                for (int v = 0; v < s.VertexCount; v++)
                {
                    int at = v * s.Stride + bi.Offset;
                    parts.Add(bi.Format == VertexElementFormat.UByte4 ? s.Vertices[at] : (int)Math.Round(VertexCodec.Decode(s.Vertices, at, bi.Format)[0]));
                }
            return new List<int>(parts);
        }

        /// <summary>No section of any LOD carries RadiosityTexCoord: the source never lightmapped this mesh.</summary>
        public bool NeverLightmapped
        {
            get
            {
                foreach (GeoLod l in Lods) foreach (GeoSection s in l.Sections) if (s.Find(VertexElementUsage.RadiosityTexCoord) != null) return false;
                return true;
            }
        }

        /// <summary>
        /// Removes the geometry of composite parts the source shows only in later health states (a destructible prop's ruin,
        /// its damaged glass). A vertex belongs to part BoneIndices[0] -- 2015 composites store the part index itself, not an
        /// index into the section's bone list (BoxMedium_01's M_Ruin section: bone list [3], BoneIndices 3; its depth section:
        /// bone list [0,3], BoneIndices 0 and 3). A primitive goes when all its vertices belong to hidden parts; the vertices no
        /// remaining primitive uses go with it (order kept, indices remapped); a section left without primitives is removed with
        /// its category entries and later sections renumbered. Returns one note per changed section.
        /// </summary>
        public List<string> DropParts(ICollection<int> hidden)
        {
            var notes = new List<string>();
            if (hidden == null || hidden.Count == 0) return notes;
            for (int l = 0; l < Lods.Count; l++)
            {
                GeoLod lod = Lods[l];
                var remove = new List<int>();
                for (int k = 0; k < lod.Sections.Count; k++)
                {
                    GeoSection s = lod.Sections[k];
                    GeoElement bi = s.Find(VertexElementUsage.BoneIndices);
                    int per = IndexCountFor(s.Prim, 1);
                    if (bi == null || per <= 0) continue;
                    var part = new int[s.VertexCount];
                    for (int v = 0; v < s.VertexCount; v++)
                    {
                        int at = v * s.Stride + bi.Offset;
                        part[v] = bi.Format == VertexElementFormat.UByte4 ? s.Vertices[at] : (int)Math.Round(VertexCodec.Decode(s.Vertices, at, bi.Format)[0]);
                    }
                    var keep = new List<uint>(s.Indices.Length);
                    int removed = 0, mixed = 0;
                    for (int p = 0; p + per <= s.Indices.Length; p += per)
                    {
                        int h = 0;
                        for (int c = 0; c < per; c++) if (hidden.Contains(part[s.Indices[p + c]])) h++;
                        if (h == per) { removed++; continue; }
                        if (h > 0) mixed++;
                        for (int c = 0; c < per; c++) keep.Add(s.Indices[p + c]);
                    }
                    if (removed == 0) continue;
                    string what = "lod" + l + "/sec" + k + " '" + s.Name + "'";
                    if (keep.Count == 0) { remove.Add(k); notes.Add(what + ": whole section removed (" + removed + " primitive(s))"); continue; }
                    var map = new int[s.VertexCount];
                    for (int v = 0; v < map.Length; v++) map[v] = -1;
                    foreach (uint ix in keep) map[ix] = 0;
                    int nv = 0;
                    for (int v = 0; v < map.Length; v++) if (map[v] == 0) map[v] = nv++;
                    var vb = new byte[nv * s.Stride];
                    for (int v = 0; v < map.Length; v++) if (map[v] >= 0) Buffer.BlockCopy(s.Vertices, v * s.Stride, vb, map[v] * s.Stride, s.Stride);
                    var ib = new uint[keep.Count];
                    for (int i = 0; i < ib.Length; i++) ib[i] = (uint)map[keep[i]];
                    notes.Add(what + ": " + removed + " of " + (removed + keep.Count / per) + " primitive(s) and " + (s.VertexCount - nv) + " vertice(s) removed" +
                              (mixed > 0 ? ", " + mixed + " primitive(s) straddling a hidden part kept" : ""));
                    s.Vertices = vb; s.Indices = ib; s.VertexCount = nv; s.PrimitiveCount = keep.Count / per;
                }
                for (int r = remove.Count - 1; r >= 0; r--)
                {
                    int k = remove[r];
                    lod.Sections.RemoveAt(k);
                    foreach (List<byte> cat in lod.Categories)
                    {
                        cat.Remove((byte)k);
                        for (int c = 0; c < cat.Count; c++) if (cat[c] > k) cat[c]--;
                    }
                }
            }
            return notes;
        }

        // ------------------------------------------------------------------ read from the loaded game

        public static string Read(EbxAssetEntry entry, out GeoMesh geo)
        {
            geo = null;
            if (entry == null) return "no mesh entry.";
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { return "mesh load failed: " + ex.Message; }
            ulong rid;
            try { rid = (ulong)((dynamic)asset.RootObject).MeshSetResource; }
            catch (Exception ex) { return "no MeshSetResource: " + ex.Message; }
            if (rid == 0) return "mesh has no geometry resource.";
            ResAssetEntry re = App.AssetManager.GetResEntry(rid);
            if (re == null) return "MeshSet res not found.";
            MeshSet ms;
            try { ms = App.AssetManager.GetResAs<MeshSet>(re); }
            catch (Exception ex) { return "could not load the MeshSet: " + ex.Message; }
            if (ms == null || ms.Lods.Count == 0) return "empty MeshSet.";
            geo = new GeoMesh { Path = entry.Name, SourceGame = FrostyCLI.Core.Session.GameName ?? "" };
            if (ms.Type.ToString().EndsWith("Composite", StringComparison.OrdinalIgnoreCase))
            {
                FieldInfo fpt = typeof(MeshSet).GetField("m_partTransforms", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo fpb = typeof(MeshSet).GetField("m_partBoundingBoxes", BindingFlags.Instance | BindingFlags.NonPublic);
                var pt = fpt != null ? fpt.GetValue(ms) as List<LinearTransform> : null;
                var pb = fpb != null ? fpb.GetValue(ms) as List<AxisAlignedBox> : null;
                if (pt != null) geo.PartTransforms.AddRange(pt);
                if (pb != null) geo.PartBoxes.AddRange(pb);
                // SWBF 2015 keeps the lists on the LODs (MeshSetLod.PartTransforms / PartBoundingBoxes), not on the MeshSet
                if (geo.PartBoxes.Count == 0 && ms.Lods[0].PartBoundingBoxes != null && ms.Lods[0].PartBoundingBoxes.Count > 0)
                {
                    geo.PartBoxes.AddRange(ms.Lods[0].PartBoundingBoxes);
                    if (ms.Lods[0].PartTransforms != null) geo.PartTransforms.AddRange(ms.Lods[0].PartTransforms);
                }
            }
            FieldInfo ratiosField = typeof(MeshSetSection).GetField("m_texCoordRatios", BindingFlags.Instance | BindingFlags.NonPublic);
            for (int l = 0; l < ms.Lods.Count; l++)
            {
                MeshSetLod lod = ms.Lods[l];
                byte[] buf;
                try
                {
                    using (var mem = new MemoryStream())
                    {
                        if (lod.ChunkId != Guid.Empty)
                        {
                            ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(lod.ChunkId);
                            if (ce == null) return "LOD " + l + " chunk " + lod.ChunkId + " not found";
                            App.AssetManager.GetChunk(ce).CopyTo(mem);
                        }
                        else if (lod.InlineData != null) mem.Write(lod.InlineData, 0, lod.InlineData.Length);
                        buf = mem.ToArray();
                    }
                }
                catch (Exception ex) { return "could not read LOD " + l + " geometry: " + ex.Message; }
                var gl = new GeoLod { Flags = (uint)lod.Flags };
                if (lod.CategorySubsetIndices != null)
                    foreach (List<byte> c in lod.CategorySubsetIndices) gl.Categories.Add(new List<byte>(c));
                int unit = lod.IndexUnitSize / 8;
                for (int s = 0; s < lod.Sections.Count; s++)
                {
                    MeshSetSection sec = lod.Sections[s];
                    var gs = new GeoSection
                    {
                        Name = sec.Name ?? "", MaterialId = sec.MaterialId, Prim = sec.PrimitiveType,
                        VertexCount = (int)sec.VertexCount, PrimitiveCount = (int)sec.PrimitiveCount,
                        LightMapUvMappingIndex = sec.LightMapUvMappingIndex
                    };
                    if (ratiosField != null)
                    {
                        var r = ratiosField.GetValue(sec) as List<float>;
                        if (r != null) for (int i = 0; i < 6 && i < r.Count; i++) gs.TexCoordRatios[i] = r[i];
                    }
                    GeometryDeclarationDesc decl = sec.GeometryDeclDesc[0];
                    for (int i = 0; i < decl.ElementCount && i < decl.Elements.Length; i++)
                    {
                        var e = decl.Elements[i];
                        if (e.Usage == VertexElementUsage.Unknown) continue;
                        if (e.StreamIndex != 0) return "LOD " + l + " section " + s + " uses vertex stream " + e.StreamIndex + " (only stream 0 is supported)";
                        gs.Elements.Add(new GeoElement { Usage = e.Usage, Format = e.Format, Offset = e.Offset, Stream = e.StreamIndex });
                    }
                    gs.Stride = decl.Streams[0].VertexStride;
                    if (gs.VertexCount > 0)
                    {
                        long need = (long)sec.VertexOffset + (long)gs.VertexCount * gs.Stride;
                        if (need > lod.VertexBufferSize || need > buf.Length) return "LOD " + l + " section " + s + " vertex window exceeds the vertex buffer";
                        gs.Vertices = new byte[gs.VertexCount * gs.Stride];
                        Buffer.BlockCopy(buf, (int)sec.VertexOffset, gs.Vertices, 0, gs.Vertices.Length);
                        int indexCount = IndexCountFor(gs.Prim, gs.PrimitiveCount);
                        if (indexCount < 0) return "LOD " + l + " section " + s + ": primitive type " + gs.Prim + " is not supported";
                        gs.Indices = new uint[indexCount];
                        long at = (long)lod.VertexBufferSize + (long)sec.StartIndex * unit;
                        if (at + (long)indexCount * unit > buf.Length) return "LOD " + l + " section " + s + " index window exceeds the buffer";
                        for (int i = 0; i < indexCount; i++)
                        {
                            long o = at + (long)i * unit;
                            gs.Indices[i] = unit == 2 ? BitConverter.ToUInt16(buf, (int)o) : BitConverter.ToUInt32(buf, (int)o);
                            if (gs.Indices[i] >= gs.VertexCount) return "LOD " + l + " section " + s + " index " + i + " (" + gs.Indices[i] + ") is outside the section's " + gs.VertexCount + " vertices";
                        }
                    }
                    gl.Sections.Add(gs);
                }
                geo.Lods.Add(gl);
            }
            return null;
        }

        /// <summary>Indices a section of <paramref name="prim"/> with <paramref name="prims"/> primitives uses; -1 when unsupported.</summary>
        public static int IndexCountFor(PrimitiveType prim, int prims)
        {
            switch (prim)
            {
                case PrimitiveType.PrimitiveType_TriangleList:
                case PrimitiveType.PrimitiveType_TrianglePatch: return prims * 3;
                case PrimitiveType.PrimitiveType_LineList: return prims * 2;
                case PrimitiveType.PrimitiveType_PointList: return prims;
                default: return -1;
            }
        }

        // ------------------------------------------------------------------ file

        public void Save(string file)
        {
            string dir = System.IO.Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var w = new BinaryWriter(File.Create(file)))
            {
                w.Write(Encoding.ASCII.GetBytes(Magic));
                WriteString(w, Path); WriteString(w, SourceGame);
                w.Write(Lods.Count);
                foreach (GeoLod lod in Lods)
                {
                    w.Write(lod.Flags);
                    w.Write(lod.Categories.Count);
                    foreach (List<byte> c in lod.Categories) { w.Write(c.Count); w.Write(c.ToArray()); }
                    w.Write(lod.Sections.Count);
                    foreach (GeoSection s in lod.Sections)
                    {
                        WriteString(w, s.Name);
                        w.Write(s.MaterialId); w.Write((int)s.Prim); w.Write(s.VertexCount); w.Write(s.PrimitiveCount); w.Write(s.LightMapUvMappingIndex);
                        for (int i = 0; i < 6; i++) w.Write(s.TexCoordRatios[i]);
                        w.Write(s.Elements.Count);
                        foreach (GeoElement e in s.Elements) { w.Write((byte)e.Usage); w.Write((byte)e.Format); w.Write(e.Offset); w.Write(e.Stream); }
                        w.Write(s.Stride);
                        w.Write(s.Vertices.Length); w.Write(s.Vertices);
                        w.Write(s.Indices.Length);
                        foreach (uint i in s.Indices) w.Write(i);
                    }
                }
                // optional trailer (readers of the plain MESHGEO1 layout stop before it)
                if (PartTransforms.Count > 0 || PartBoxes.Count > 0)
                {
                    w.Write(Encoding.ASCII.GetBytes(PartsMagic));
                    w.Write(PartTransforms.Count);
                    foreach (LinearTransform t in PartTransforms) foreach (Vec3 v in new[] { t.right, t.up, t.forward, t.trans }) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
                    w.Write(PartBoxes.Count);
                    foreach (AxisAlignedBox b in PartBoxes) foreach (Vec3 v in new[] { b.min, b.max }) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
                }
            }
        }

        private static Vec3 ReadVec3(BinaryReader r) { return new Vec3 { x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle() }; }

        public static string Load(string file, out GeoMesh geo)
        {
            geo = null;
            if (!File.Exists(file)) return "file not found: " + file;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(file)))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(8)) != Magic) return "not a MESHGEO1 file: " + file;
                    geo = new GeoMesh { Path = ReadString(r), SourceGame = ReadString(r) };
                    int lods = r.ReadInt32();
                    for (int l = 0; l < lods; l++)
                    {
                        var lod = new GeoLod { Flags = r.ReadUInt32() };
                        int cats = r.ReadInt32();
                        for (int c = 0; c < cats; c++) { int n = r.ReadInt32(); lod.Categories.Add(new List<byte>(r.ReadBytes(n))); }
                        int secs = r.ReadInt32();
                        for (int k = 0; k < secs; k++)
                        {
                            var s = new GeoSection { Name = ReadString(r), MaterialId = r.ReadInt32(), Prim = (PrimitiveType)r.ReadInt32(), VertexCount = r.ReadInt32(), PrimitiveCount = r.ReadInt32(), LightMapUvMappingIndex = r.ReadUInt32() };
                            for (int i = 0; i < 6; i++) s.TexCoordRatios[i] = r.ReadSingle();
                            int ne = r.ReadInt32();
                            for (int i = 0; i < ne; i++) s.Elements.Add(new GeoElement { Usage = (VertexElementUsage)r.ReadByte(), Format = (VertexElementFormat)r.ReadByte(), Offset = r.ReadByte(), Stream = r.ReadByte() });
                            s.Stride = r.ReadInt32();
                            s.Vertices = r.ReadBytes(r.ReadInt32());
                            int ni = r.ReadInt32();
                            s.Indices = new uint[ni];
                            for (int i = 0; i < ni; i++) s.Indices[i] = r.ReadUInt32();
                            lod.Sections.Add(s);
                        }
                        geo.Lods.Add(lod);
                    }
                    if (r.BaseStream.Position + PartsMagic.Length <= r.BaseStream.Length &&
                        Encoding.ASCII.GetString(r.ReadBytes(PartsMagic.Length)) == PartsMagic)
                    {
                        int nt = r.ReadInt32();
                        for (int i = 0; i < nt; i++) geo.PartTransforms.Add(new LinearTransform { right = ReadVec3(r), up = ReadVec3(r), forward = ReadVec3(r), trans = ReadVec3(r) });
                        int nb = r.ReadInt32();
                        for (int i = 0; i < nb; i++) geo.PartBoxes.Add(new AxisAlignedBox { min = ReadVec3(r), max = ReadVec3(r) });
                    }
                }
            }
            catch (Exception ex) { geo = null; return "could not read " + file + ": " + ex.Message; }
            return null;
        }

        private static void WriteString(BinaryWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            w.Write((ushort)b.Length); w.Write(b);
        }
        private static string ReadString(BinaryReader r) { return Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16())); }
    }

    // Element value codec: every format a mesh vertex element uses in either game, to and from four floats. Decoding then
    // re-encoding in the same format is the identity (half -> float -> half, n/255 -> round(x*255) ...), so a channel that
    // keeps its format arrives bit for bit; widening (Half3 -> Float3) is exact as well.
    public static class VertexCodec
    {
        public static bool Supported(VertexElementFormat f)
        {
            switch (f)
            {
                case VertexElementFormat.Float: case VertexElementFormat.Float2: case VertexElementFormat.Float3: case VertexElementFormat.Float4:
                case VertexElementFormat.Half: case VertexElementFormat.Half2: case VertexElementFormat.Half3: case VertexElementFormat.Half4:
                case VertexElementFormat.UByte4N: case VertexElementFormat.UByte4: case VertexElementFormat.Byte4N: case VertexElementFormat.Byte4:
                case VertexElementFormat.Short2N: case VertexElementFormat.Short4N: case VertexElementFormat.UShort2N: case VertexElementFormat.UShort4N:
                case VertexElementFormat.Short2: case VertexElementFormat.Short4: case VertexElementFormat.UShort2: case VertexElementFormat.UShort4:
                case VertexElementFormat.UInt: case VertexElementFormat.Int:
                    return true;
                default: return false;
            }
        }

        public static int Components(VertexElementFormat f)
        {
            switch (f)
            {
                case VertexElementFormat.Float: case VertexElementFormat.Half: case VertexElementFormat.UInt: case VertexElementFormat.Int: return 1;
                case VertexElementFormat.Float2: case VertexElementFormat.Half2: case VertexElementFormat.Short2N: case VertexElementFormat.UShort2N:
                case VertexElementFormat.Short2: case VertexElementFormat.UShort2: return 2;
                case VertexElementFormat.Float3: case VertexElementFormat.Half3: return 3;
                default: return 4;
            }
        }

        /// <summary>Decodes one element at <paramref name="at"/> into up to four floats (missing components 0, w 1 for 3-component positions).</summary>
        public static float[] Decode(byte[] b, int at, VertexElementFormat f)
        {
            var v = new float[4];
            switch (f)
            {
                case VertexElementFormat.Float: v[0] = BitConverter.ToSingle(b, at); break;
                case VertexElementFormat.Float2: for (int i = 0; i < 2; i++) v[i] = BitConverter.ToSingle(b, at + 4 * i); break;
                case VertexElementFormat.Float3: for (int i = 0; i < 3; i++) v[i] = BitConverter.ToSingle(b, at + 4 * i); v[3] = 1f; break;
                case VertexElementFormat.Float4: for (int i = 0; i < 4; i++) v[i] = BitConverter.ToSingle(b, at + 4 * i); break;
                case VertexElementFormat.Half: v[0] = HalfToFloat(BitConverter.ToUInt16(b, at)); break;
                case VertexElementFormat.Half2: for (int i = 0; i < 2; i++) v[i] = HalfToFloat(BitConverter.ToUInt16(b, at + 2 * i)); break;
                case VertexElementFormat.Half3: for (int i = 0; i < 3; i++) v[i] = HalfToFloat(BitConverter.ToUInt16(b, at + 2 * i)); v[3] = 1f; break;
                case VertexElementFormat.Half4: for (int i = 0; i < 4; i++) v[i] = HalfToFloat(BitConverter.ToUInt16(b, at + 2 * i)); break;
                case VertexElementFormat.UByte4N: for (int i = 0; i < 4; i++) v[i] = b[at + i] / 255f; break;
                case VertexElementFormat.UByte4: for (int i = 0; i < 4; i++) v[i] = b[at + i]; break;
                case VertexElementFormat.Byte4N: for (int i = 0; i < 4; i++) v[i] = Math.Max((sbyte)b[at + i] / 127f, -1f); break;
                case VertexElementFormat.Byte4: for (int i = 0; i < 4; i++) v[i] = (sbyte)b[at + i]; break;
                case VertexElementFormat.Short2N: for (int i = 0; i < 2; i++) v[i] = Math.Max(BitConverter.ToInt16(b, at + 2 * i) / 32767f, -1f); break;
                case VertexElementFormat.Short4N: for (int i = 0; i < 4; i++) v[i] = Math.Max(BitConverter.ToInt16(b, at + 2 * i) / 32767f, -1f); break;
                case VertexElementFormat.UShort2N: for (int i = 0; i < 2; i++) v[i] = BitConverter.ToUInt16(b, at + 2 * i) / 65535f; break;
                case VertexElementFormat.UShort4N: for (int i = 0; i < 4; i++) v[i] = BitConverter.ToUInt16(b, at + 2 * i) / 65535f; break;
                case VertexElementFormat.Short2: for (int i = 0; i < 2; i++) v[i] = BitConverter.ToInt16(b, at + 2 * i); break;
                case VertexElementFormat.Short4: for (int i = 0; i < 4; i++) v[i] = BitConverter.ToInt16(b, at + 2 * i); break;
                case VertexElementFormat.UShort2: for (int i = 0; i < 2; i++) v[i] = BitConverter.ToUInt16(b, at + 2 * i); break;
                case VertexElementFormat.UShort4: for (int i = 0; i < 4; i++) v[i] = BitConverter.ToUInt16(b, at + 2 * i); break;
                case VertexElementFormat.UInt: v[0] = BitConverter.ToUInt32(b, at); break;
                case VertexElementFormat.Int: v[0] = BitConverter.ToInt32(b, at); break;
                default: throw new NotSupportedException("vertex element format " + f);
            }
            return v;
        }

        public static void Encode(byte[] b, int at, VertexElementFormat f, float[] v)
        {
            switch (f)
            {
                case VertexElementFormat.Float: Put(b, at, v[0]); break;
                case VertexElementFormat.Float2: for (int i = 0; i < 2; i++) Put(b, at + 4 * i, v[i]); break;
                case VertexElementFormat.Float3: for (int i = 0; i < 3; i++) Put(b, at + 4 * i, v[i]); break;
                case VertexElementFormat.Float4: for (int i = 0; i < 4; i++) Put(b, at + 4 * i, v[i]); break;
                case VertexElementFormat.Half: PutU16(b, at, FloatToHalf(v[0])); break;
                case VertexElementFormat.Half2: for (int i = 0; i < 2; i++) PutU16(b, at + 2 * i, FloatToHalf(v[i])); break;
                case VertexElementFormat.Half3: for (int i = 0; i < 3; i++) PutU16(b, at + 2 * i, FloatToHalf(v[i])); break;
                case VertexElementFormat.Half4: for (int i = 0; i < 4; i++) PutU16(b, at + 2 * i, FloatToHalf(v[i])); break;
                case VertexElementFormat.UByte4N: for (int i = 0; i < 4; i++) b[at + i] = (byte)Math.Round(Clamp(v[i], 0f, 1f) * 255f); break;
                case VertexElementFormat.UByte4: for (int i = 0; i < 4; i++) b[at + i] = (byte)Math.Round(Clamp(v[i], 0f, 255f)); break;
                case VertexElementFormat.Byte4N: for (int i = 0; i < 4; i++) b[at + i] = (byte)(sbyte)Math.Round(Clamp(v[i], -1f, 1f) * 127f); break;
                case VertexElementFormat.Byte4: for (int i = 0; i < 4; i++) b[at + i] = (byte)(sbyte)Math.Round(Clamp(v[i], -128f, 127f)); break;
                case VertexElementFormat.Short2N: for (int i = 0; i < 2; i++) PutU16(b, at + 2 * i, (ushort)(short)Math.Round(Clamp(v[i], -1f, 1f) * 32767f)); break;
                case VertexElementFormat.Short4N: for (int i = 0; i < 4; i++) PutU16(b, at + 2 * i, (ushort)(short)Math.Round(Clamp(v[i], -1f, 1f) * 32767f)); break;
                case VertexElementFormat.UShort2N: for (int i = 0; i < 2; i++) PutU16(b, at + 2 * i, (ushort)Math.Round(Clamp(v[i], 0f, 1f) * 65535f)); break;
                case VertexElementFormat.UShort4N: for (int i = 0; i < 4; i++) PutU16(b, at + 2 * i, (ushort)Math.Round(Clamp(v[i], 0f, 1f) * 65535f)); break;
                case VertexElementFormat.Short2: for (int i = 0; i < 2; i++) PutU16(b, at + 2 * i, (ushort)(short)Math.Round(v[i])); break;
                case VertexElementFormat.Short4: for (int i = 0; i < 4; i++) PutU16(b, at + 2 * i, (ushort)(short)Math.Round(v[i])); break;
                case VertexElementFormat.UShort2: for (int i = 0; i < 2; i++) PutU16(b, at + 2 * i, (ushort)Math.Round(v[i])); break;
                case VertexElementFormat.UShort4: for (int i = 0; i < 4; i++) PutU16(b, at + 2 * i, (ushort)Math.Round(v[i])); break;
                case VertexElementFormat.UInt: Buffer.BlockCopy(BitConverter.GetBytes((uint)v[0]), 0, b, at, 4); break;
                case VertexElementFormat.Int: Buffer.BlockCopy(BitConverter.GetBytes((int)v[0]), 0, b, at, 4); break;
                default: throw new NotSupportedException("vertex element format " + f);
            }
        }

        private static float Clamp(float x, float lo, float hi) { return x < lo ? lo : (x > hi ? hi : x); }
        private static void Put(byte[] b, int at, float x) { Buffer.BlockCopy(BitConverter.GetBytes(x), 0, b, at, 4); }
        private static void PutU16(byte[] b, int at, ushort x) { b[at] = (byte)x; b[at + 1] = (byte)(x >> 8); }

        public static float HalfToFloat(ushort h) { return RadiosityUvPort.HalfToFloat(h); }

        // Round-to-nearest-even half conversion (the FBX path truncated). For any float that came from a half the result
        // is that half again, which is what keeps same-format channels bit exact.
        public static ushort FloatToHalf(float f)
        {
            uint x = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            uint sign = (x >> 16) & 0x8000u;
            uint mant = x & 0x007FFFFFu;
            int exp = (int)((x >> 23) & 0xFF);
            if (exp == 255) return (ushort)(sign | 0x7C00u | (mant != 0 ? 0x200u : 0u));
            int e = exp - 127 + 15;
            if (e >= 31) return (ushort)(sign | 0x7C00u);
            if (e <= 0)
            {
                if (e < -10) return (ushort)sign;
                mant |= 0x00800000u;
                int shift = 14 - e;
                uint half = mant >> shift;
                uint rem = mant & ((1u << shift) - 1);
                uint mid = 1u << (shift - 1);
                if (rem > mid || (rem == mid && (half & 1) != 0)) half++;
                return (ushort)(sign | half);
            }
            uint h = (uint)(e << 10) | (mant >> 13);
            uint r = mant & 0x1FFFu;
            if (r > 0x1000u || (r == 0x1000u && (h & 1) != 0)) h++;   // may carry into the exponent: still correct
            return (ushort)(sign | h);
        }
    }
}
