using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // Transplants a mesh's RadiosityTexCoord (lightmap UV) channel from one game's mesh onto another's.
    //
    // Why this exists: the ported CarboniteUL meshes are clones of a BF2 template whose geometry was
    // replaced from FBX, and MeshSetPlugin's FBX importer never writes RadiosityTexCoord -- in its
    // per-element switch (FrostyMeshSetEditor.cs:2648) that usage falls into the do-nothing group, and
    // only TexCoord1..7 are taken from the file. So every clone declares the channel and leaves it
    // empty. The baked lightmap addresses a prop as uv_atlas = uv_radiosity * scale + offset, so an
    // empty channel samples the wrong place and the prop renders black.
    //
    // frostycli loads one game profile per process, so this runs as two passes: dump under swbf1,
    // apply under swbf2.
    public static class RadiosityUvPort
    {
        private const string Magic = "RADUV1\0\0";

        public sealed class Vertex
        {
            public float X, Y, Z;      // position
            public float U0, V0;       // TexCoord0
            public float RU, RV;       // RadiosityTexCoord
        }

        // ---- half <-> float, kept local so this does not depend on which SDK build exposes HalfUtils ----
        internal static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 0x1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
            if (exp == 0)
            {
                if (mant == 0) return sign != 0 ? -0.0f : 0.0f;
                float sub = (float)(mant * Math.Pow(2, -24));
                return sign != 0 ? -sub : sub;
            }
            if (exp == 31) return mant == 0 ? (sign != 0 ? float.NegativeInfinity : float.PositiveInfinity) : float.NaN;
            float val = (float)((1.0 + mant / 1024.0) * Math.Pow(2, exp - 15));
            return sign != 0 ? -val : val;
        }

        internal static ushort FloatToHalf(float f)
        {
            if (float.IsNaN(f)) return 0x7E00;
            uint bits = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            uint sign = (bits >> 16) & 0x8000;
            int exp = (int)((bits >> 23) & 0xFF) - 127 + 15;
            uint mant = bits & 0x7FFFFF;
            if (exp <= 0)
            {
                if (exp < -10) return (ushort)sign;
                mant |= 0x800000;
                int shift = 14 - exp;
                return (ushort)(sign | (mant >> shift));
            }
            if (exp >= 31) return (ushort)(sign | 0x7C00);
            return (ushort)(sign | ((uint)exp << 10) | (mant >> 13));
        }

        /// <summary>
        /// LOD choice for Open: the finest LOD whose triangle count is at most this (else the coarsest). int.MaxValue
        /// (the default) is LOD0. `navmesh geometry` lowers it: a navmesh voxelises at decimetres, not LOD0 detail.
        /// </summary>
        public static int MaxTriangles = int.MaxValue;

        public static string Open(string meshPath, out MeshSet meshSet, out ulong resRid, out byte[] buffer, out MeshSetLod lod)
        {
            meshSet = null; resRid = 0; buffer = null; lod = null;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(meshPath);
            if (entry == null) return "mesh not found: " + meshPath;
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { return "mesh load failed: " + ex.Message; }
            try { resRid = (ulong)((dynamic)asset.RootObject).MeshSetResource; }
            catch (Exception ex) { return "no MeshSetResource: " + ex.Message; }
            if (resRid == 0) return "mesh has no geometry resource: " + meshPath;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            if (resEntry == null) return "res not found for " + meshPath;
            meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null || meshSet.Lods.Count == 0) return "no MeshSet/LODs for " + meshPath;
            lod = meshSet.Lods[0];
            if (MaxTriangles != int.MaxValue)
            {
                lod = meshSet.Lods[meshSet.Lods.Count - 1];
                foreach (MeshSetLod l in meshSet.Lods)
                {
                    long tris = 0;
                    foreach (MeshSetSection s in l.Sections) tris += s.PrimitiveCount;
                    if (tris <= MaxTriangles) { lod = l; break; }
                }
            }
            try
            {
                using (var ms = new MemoryStream())
                {
                    if (lod.ChunkId != Guid.Empty)
                        App.AssetManager.GetChunk(App.AssetManager.GetChunkEntry(lod.ChunkId)).CopyTo(ms);
                    else
                        ms.Write(lod.InlineData, 0, lod.InlineData.Length);
                    buffer = ms.ToArray();
                }
            }
            catch (Exception ex) { return "could not read geometry for " + meshPath + ": " + ex.Message; }
            return null;
        }

        private static bool FindElement(GeometryDeclarationDesc decl, VertexElementUsage usage,
                                        out int offset, out VertexElementFormat format)
        {
            offset = 0; format = VertexElementFormat.None;
            foreach (GeometryDeclarationDesc.Element e in decl.Elements)
            {
                if (e.Usage != usage || e.StreamIndex != 0) continue;
                offset = e.Offset; format = e.Format; return true;
            }
            return false;
        }

        private static float[] ReadUv(byte[] buf, int at, VertexElementFormat fmt)
        {
            if (fmt == VertexElementFormat.Float2)
                return new[] { BitConverter.ToSingle(buf, at), BitConverter.ToSingle(buf, at + 4) };
            return new[] { HalfToFloat(BitConverter.ToUInt16(buf, at)), HalfToFloat(BitConverter.ToUInt16(buf, at + 2)) };
        }

        private static void WriteUv(byte[] buf, int at, VertexElementFormat fmt, float u, float v)
        {
            if (fmt == VertexElementFormat.Float2)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(u), 0, buf, at, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(v), 0, buf, at + 4, 4);
                return;
            }
            Buffer.BlockCopy(BitConverter.GetBytes(FloatToHalf(u)), 0, buf, at, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(FloatToHalf(v)), 0, buf, at + 2, 2);
        }

        private static float[] ReadPos(byte[] buf, int at, VertexElementFormat fmt)
        {
            if (fmt == VertexElementFormat.Float3 || fmt == VertexElementFormat.Float4)
                return new[] { BitConverter.ToSingle(buf, at), BitConverter.ToSingle(buf, at + 4), BitConverter.ToSingle(buf, at + 8) };
            return new[] { HalfToFloat(BitConverter.ToUInt16(buf, at)),
                           HalfToFloat(BitConverter.ToUInt16(buf, at + 2)),
                           HalfToFloat(BitConverter.ToUInt16(buf, at + 4)) };
        }

        // Reads every LOD-0 vertex of meshPath into verts.
        public static string Read(string meshPath, List<Vertex> verts, out bool hasRadiosity, out string note)
        {
            hasRadiosity = false; note = null;
            MeshSet meshSet; ulong rid; byte[] buf; MeshSetLod lod;
            string err = Open(meshPath, out meshSet, out rid, out buf, out lod);
            if (err != null) return err;
            int sections = 0;
            var names = new List<string>();
            foreach (MeshSetSection section in lod.Sections)
            {
                if (section.VertexCount == 0) continue;
                GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
                int stride = decl.Streams[0].VertexStride;
                if (stride <= 0) continue;
                int posOff, uvOff, radOff;
                VertexElementFormat posFmt, uvFmt, radFmt;
                if (!FindElement(decl, VertexElementUsage.Pos, out posOff, out posFmt)) continue;
                bool hasUv = FindElement(decl, VertexElementUsage.TexCoord0, out uvOff, out uvFmt);
                bool hasRad = FindElement(decl, VertexElementUsage.RadiosityTexCoord, out radOff, out radFmt);
                if (hasRad) hasRadiosity = true;
                sections++;
                names.Add((section.Name ?? "?") + "[" + section.VertexCount + "]");
                for (int i = 0; i < section.VertexCount; i++)
                {
                    long at = (long)section.VertexOffset + (long)i * stride;
                    if (at + stride > buf.Length) break;
                    float[] p = ReadPos(buf, (int)(at + posOff), posFmt);
                    float[] uv = hasUv ? ReadUv(buf, (int)(at + uvOff), uvFmt) : new[] { 0f, 0f };
                    float[] rad = hasRad ? ReadUv(buf, (int)(at + radOff), radFmt) : new[] { 0f, 0f };
                    verts.Add(new Vertex { X = p[0], Y = p[1], Z = p[2], U0 = uv[0], V0 = uv[1], RU = rad[0], RV = rad[1] });
                }
            }
            note = sections + " section(s), " + verts.Count + " vertices, radiosity channel " + (hasRadiosity ? "present" : "ABSENT")
                 + " | sections: " + string.Join(", ", names.ToArray());
            return null;
        }

        private const string MagicWithTriangles = "RADUV2\0\0";

        public sealed class Section
        {
            public int VertexBase, VertexCount;   // range in the vertex list Read produced
            public bool HasRadiosity;
            public int[] Triangles;               // indices global to that vertex list, 3 per triangle
        }

        // Read, plus each section's triangle list. Moving a baked lightmap between two different
        // lightmap-UV layouts of the same surface needs the triangles, not just the vertices: a texel
        // is mapped through the triangle that covers it. Indices follow the vertex buffer and are
        // section-relative, as MeshSetPlugin's FBX exporter reads them.
        public static string ReadWithTriangles(string meshPath, List<Vertex> verts, List<Section> sections,
                                               out bool hasRadiosity, out string note)
        {
            string err = Read(meshPath, verts, out hasRadiosity, out note);
            if (err != null) return err;
            MeshSet meshSet; ulong rid; byte[] buf; MeshSetLod lod;
            err = Open(meshPath, out meshSet, out rid, out buf, out lod);
            if (err != null) return err;
            int unit = lod.IndexUnitSize / 8;
            int vbase = 0;
            foreach (MeshSetSection section in lod.Sections)
            {
                // the same skips as Read, so the numbering lines up with verts
                if (section.VertexCount == 0) continue;
                GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
                int stride = decl.Streams[0].VertexStride;
                if (stride <= 0) continue;
                int off; VertexElementFormat fmt;
                if (!FindElement(decl, VertexElementUsage.Pos, out off, out fmt)) continue;
                bool hasRad = FindElement(decl, VertexElementUsage.RadiosityTexCoord, out off, out fmt);
                // Read stops a section at the end of the buffer; count exactly what it took
                long fit = Math.Max(0L, ((long)buf.Length - (long)section.VertexOffset) / stride);
                int read = (int)Math.Min((long)section.VertexCount, fit);
                var tris = new List<int>();
                if (section.PrimitiveType == PrimitiveType.PrimitiveType_TriangleList)
                {
                    long at = (long)lod.VertexBufferSize + (long)section.StartIndex * unit;
                    for (long p = 0; p < section.PrimitiveCount; p++)
                    {
                        long o = at + p * 3 * unit;
                        if (o + 3 * unit > buf.Length) break;
                        int a = ReadIndex(buf, o, unit), b = ReadIndex(buf, o + unit, unit), c = ReadIndex(buf, o + 2 * unit, unit);
                        if (a >= read || b >= read || c >= read) continue;
                        tris.Add(vbase + a); tris.Add(vbase + b); tris.Add(vbase + c);
                    }
                }
                sections.Add(new Section { VertexBase = vbase, VertexCount = read, HasRadiosity = hasRad, Triangles = tris.ToArray() });
                vbase += read;
            }
            return null;
        }

        private static int ReadIndex(byte[] buf, long at, int unit)
        {
            return unit == 2 ? BitConverter.ToUInt16(buf, (int)at) : (int)BitConverter.ToUInt32(buf, (int)at);
        }

        // RADUV2 = the RADUV1 vertex block, then {vertexBase, vertexCount, hasRadiosity, triangleCount, indices} per section.
        public static void SaveWithTriangles(List<Vertex> verts, List<Section> sections, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes(MagicWithTriangles));
                w.Write(verts.Count);
                foreach (Vertex v in verts)
                { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.U0); w.Write(v.V0); w.Write(v.RU); w.Write(v.RV); }
                w.Write(sections.Count);
                foreach (Section s in sections)
                {
                    w.Write(s.VertexBase); w.Write(s.VertexCount); w.Write(s.HasRadiosity ? 1 : 0); w.Write(s.Triangles.Length / 3);
                    foreach (int i in s.Triangles) w.Write(i);
                }
            }
        }

        public static void Save(List<Vertex> verts, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
                w.Write(verts.Count);
                foreach (Vertex v in verts)
                { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.U0); w.Write(v.V0); w.Write(v.RU); w.Write(v.RV); }
            }
        }

        public static string Load(string path, List<Vertex> verts)
        {
            if (!File.Exists(path)) return "source file not found: " + path;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                string m = System.Text.Encoding.ASCII.GetString(r.ReadBytes(8));
                if (m != Magic) return "not a RADUV1 file: " + path;
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                    verts.Add(new Vertex { X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle(),
                                           U0 = r.ReadSingle(), V0 = r.ReadSingle(), RU = r.ReadSingle(), RV = r.ReadSingle() });
            }
            return null;
        }

        // Writes the source radiosity UVs onto meshPath, matching vertices by position.
        public static string Apply(string meshPath, List<Vertex> src, float scale, float tolerance,
                                   out int matched, out int total, out string note)
        {
            matched = 0; total = 0; note = null;
            MeshSet meshSet; ulong rid; byte[] buf; MeshSetLod lod;
            string err = Open(meshPath, out meshSet, out rid, out buf, out lod);
            if (err != null) return err;

            var target = new List<Vertex>();
            bool hasRad; string tnote;
            string rerr = Read(meshPath, target, out hasRad, out tnote);
            if (rerr != null) return rerr;
            if (!hasRad) return "target mesh declares no RadiosityTexCoord channel: " + meshPath;

            // The clone's geometry came back through an FBX round trip, so its positions may be uniformly
            // scaled relative to the 2015 mesh. Derive the factor from the two X extents rather than
            // assuming one, unless the caller pinned it.
            if (scale <= 0)
            {
                float sMin = float.MaxValue, sMax = float.MinValue, tMin = float.MaxValue, tMax = float.MinValue;
                foreach (Vertex v in src) { if (v.X < sMin) sMin = v.X; if (v.X > sMax) sMax = v.X; }
                foreach (Vertex v in target) { if (v.X < tMin) tMin = v.X; if (v.X > tMax) tMax = v.X; }
                float se = sMax - sMin, te = tMax - tMin;
                scale = (se > 1e-6f && te > 1e-6f) ? te / se : 1.0f;
            }

            float cell = Math.Max(tolerance * 2.0f, 1e-4f);
            var grid = new Dictionary<long, List<Vertex>>();
            Func<float, float, float, long> keyOf = (x, y, z) =>
                ((long)(int)Math.Floor(x / cell) * 73856093) ^ ((long)(int)Math.Floor(y / cell) * 19349663) ^ ((long)(int)Math.Floor(z / cell) * 83492791);
            foreach (Vertex v in src)
            {
                long k = keyOf(v.X * scale, v.Y * scale, v.Z * scale);
                List<Vertex> l;
                if (!grid.TryGetValue(k, out l)) { l = new List<Vertex>(); grid[k] = l; }
                l.Add(v);
            }

            int vi = 0;
            foreach (MeshSetSection section in lod.Sections)
            {
                if (section.VertexCount == 0) continue;
                GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
                int stride = decl.Streams[0].VertexStride;
                if (stride <= 0) continue;
                int radOff; VertexElementFormat radFmt;
                if (!FindElement(decl, VertexElementUsage.RadiosityTexCoord, out radOff, out radFmt))
                { vi += (int)section.VertexCount; continue; }
                for (int i = 0; i < section.VertexCount; i++, vi++)
                {
                    if (vi >= target.Count) break;
                    Vertex t = target[vi];
                    total++;
                    Vertex best = null; float bestD = float.MaxValue;
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                long k = keyOf(t.X + dx * cell, t.Y + dy * cell, t.Z + dz * cell);
                                List<Vertex> l;
                                if (!grid.TryGetValue(k, out l)) continue;
                                foreach (Vertex c in l)
                                {
                                    float ddx = c.X * scale - t.X, ddy = c.Y * scale - t.Y, ddz = c.Z * scale - t.Z;
                                    float d = ddx * ddx + ddy * ddy + ddz * ddz;
                                    // break position ties with TexCoord0 so split/mirrored vertices pick the right one
                                    d += 0.001f * ((c.U0 - t.U0) * (c.U0 - t.U0) + (c.V0 - t.V0) * (c.V0 - t.V0));
                                    if (d < bestD) { bestD = d; best = c; }
                                }
                            }
                    if (best == null || bestD > tolerance * tolerance) continue;
                    long at = (long)section.VertexOffset + (long)i * stride;
                    if (at + radOff + 4 > buf.Length) continue;
                    WriteUv(buf, (int)(at + radOff), radFmt, best.RU, best.RV);
                    matched++;
                }
            }

            if (matched > 0)
            {
                if (lod.ChunkId != Guid.Empty)
                {
                    if (!App.AssetManager.ModifyChunk(lod.ChunkId, buf)) return "ModifyChunk failed for " + meshPath;
                }
                else
                {
                    lod.SetInlineData(buf);
                    App.AssetManager.ModifyRes(rid, meshSet);
                }
            }
            note = "scale " + scale.ToString("0.####") + ", " + (lod.ChunkId != Guid.Empty ? "chunk-backed" : "inline") + ", " + tnote;
            return null;
        }

        // ---- every LOD, by vertex index (RADUV3) ---------------------------------------------------------------------
        // A lightmap rect is addressed through every LOD's RadiosityTexCoord, not LOD0's alone: a clone whose coarser LODs
        // keep the empty channel samples the corner of its rect as soon as it is far enough to switch LOD. And matching
        // by position is ambiguous on lightmap chart seams (Hoth 2015 HangarSystem_01_CombinedforE3 LOD0 wall: 3.5% of
        // position+TexCoord0 keys carry two different lightmap UVs), while a port-import clone keeps the source mesh's
        // vertex order exactly (same positions, TexCoord0 and triangles by index, every section). So RADUV3 carries all
        // LODs, every section in MeshSet order (empty ones included, so indices line up), and the apply writes by index.
        private const string MagicLods = "RADUV3\0\0";

        public sealed class LodSection
        {
            public string Name = "";
            public int MaterialId;
            public bool HasRadiosity;
            public int VertexCount;
            public float[] Data = new float[0];     // VertexCount x {x, y, z, u0, v0, ru, rv}
            public int[] Triangles = new int[0];     // section-relative, 3 per triangle
        }

        private static string OpenLodIndex(string meshPath, int lodIndex, out MeshSet meshSet, out ulong resRid, out byte[] buffer, out MeshSetLod lod)
        {
            buffer = null; lod = null;
            string err = Open(meshPath, out meshSet, out resRid, out buffer, out lod);
            if (err != null) return err;
            if (lodIndex == 0) return null;
            if (lodIndex >= meshSet.Lods.Count) return "no LOD " + lodIndex + " in " + meshPath;
            lod = meshSet.Lods[lodIndex];
            try
            {
                using (var ms = new MemoryStream())
                {
                    if (lod.ChunkId != Guid.Empty)
                        App.AssetManager.GetChunk(App.AssetManager.GetChunkEntry(lod.ChunkId)).CopyTo(ms);
                    else
                        ms.Write(lod.InlineData, 0, lod.InlineData.Length);
                    buffer = ms.ToArray();
                }
            }
            catch (Exception ex) { return "could not read LOD " + lodIndex + " of " + meshPath + ": " + ex.Message; }
            return null;
        }

        /// <summary>Every LOD's sections (MeshSet order, empty ones included): vertices, lightmap UVs, triangles.</summary>
        public static string ReadAllLods(string meshPath, List<List<LodSection>> lods, out string note)
        {
            note = null;
            MeshSet meshSet; ulong rid; byte[] buf; MeshSetLod lod;
            string err = Open(meshPath, out meshSet, out rid, out buf, out lod);
            if (err != null) return err;
            int count = meshSet.Lods.Count, withRad = 0, verts = 0;
            for (int l = 0; l < count; l++)
            {
                err = OpenLodIndex(meshPath, l, out meshSet, out rid, out buf, out lod);
                if (err != null) return err;
                int unit = lod.IndexUnitSize / 8;
                var list = new List<LodSection>();
                foreach (MeshSetSection section in lod.Sections)
                {
                    var s = new LodSection { Name = section.Name ?? "", MaterialId = section.MaterialId };
                    list.Add(s);
                    if (section.VertexCount == 0) continue;
                    GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
                    int stride = decl.Streams[0].VertexStride;
                    int posOff, uvOff, radOff;
                    VertexElementFormat posFmt, uvFmt, radFmt;
                    if (stride <= 0 || !FindElement(decl, VertexElementUsage.Pos, out posOff, out posFmt)) continue;
                    bool hasUv = FindElement(decl, VertexElementUsage.TexCoord0, out uvOff, out uvFmt);
                    s.HasRadiosity = FindElement(decl, VertexElementUsage.RadiosityTexCoord, out radOff, out radFmt);
                    if (s.HasRadiosity) withRad++;
                    long fit = Math.Max(0L, ((long)buf.Length - (long)section.VertexOffset) / stride);
                    s.VertexCount = (int)Math.Min((long)section.VertexCount, fit);
                    s.Data = new float[s.VertexCount * 7];
                    for (int i = 0; i < s.VertexCount; i++)
                    {
                        long at = (long)section.VertexOffset + (long)i * stride;
                        float[] p = ReadPos(buf, (int)(at + posOff), posFmt);
                        float[] uv = hasUv ? ReadUv(buf, (int)(at + uvOff), uvFmt) : new[] { 0f, 0f };
                        float[] rad = s.HasRadiosity ? ReadUv(buf, (int)(at + radOff), radFmt) : new[] { 0f, 0f };
                        int o = i * 7;
                        s.Data[o] = p[0]; s.Data[o + 1] = p[1]; s.Data[o + 2] = p[2];
                        s.Data[o + 3] = uv[0]; s.Data[o + 4] = uv[1]; s.Data[o + 5] = rad[0]; s.Data[o + 6] = rad[1];
                    }
                    verts += s.VertexCount;
                    var tris = new List<int>();
                    if (section.PrimitiveType == PrimitiveType.PrimitiveType_TriangleList)
                    {
                        long at = (long)lod.VertexBufferSize + (long)section.StartIndex * unit;
                        for (long p = 0; p < section.PrimitiveCount; p++)
                        {
                            long o = at + p * 3 * unit;
                            if (o + 3 * unit > buf.Length) break;
                            int a = ReadIndex(buf, o, unit), b = ReadIndex(buf, o + unit, unit), c = ReadIndex(buf, o + 2 * unit, unit);
                            if (a >= s.VertexCount || b >= s.VertexCount || c >= s.VertexCount) continue;
                            tris.Add(a); tris.Add(b); tris.Add(c);
                        }
                    }
                    s.Triangles = tris.ToArray();
                }
                lods.Add(list);
            }
            note = count + " LOD(s), " + verts + " vertices, " + withRad + " section(s) with a lightmap UV channel";
            return null;
        }

        // RADUV3 = magic, lodCount, per LOD {sectionCount, per section {name (u16 len + ASCII), materialId, hasRad,
        // vertexCount, vertexCount x 7 floats, triangleCount, 3 x triangleCount ints}}.
        public static void SaveLods(List<List<LodSection>> lods, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(System.Text.Encoding.ASCII.GetBytes(MagicLods));
                w.Write(lods.Count);
                foreach (List<LodSection> lod in lods)
                {
                    w.Write(lod.Count);
                    foreach (LodSection s in lod)
                    {
                        byte[] name = System.Text.Encoding.ASCII.GetBytes(s.Name ?? "");
                        w.Write((ushort)name.Length); w.Write(name);
                        w.Write(s.MaterialId); w.Write(s.HasRadiosity ? 1 : 0); w.Write(s.VertexCount);
                        foreach (float f in s.Data) w.Write(f);
                        w.Write(s.Triangles.Length / 3);
                        foreach (int i in s.Triangles) w.Write(i);
                    }
                }
            }
        }

        public static string LoadLods(string path, List<List<LodSection>> lods)
        {
            if (!File.Exists(path)) return "file not found: " + path;
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(8)) != MagicLods) return "not a RADUV3 file: " + path;
                int lodCount = r.ReadInt32();
                for (int l = 0; l < lodCount; l++)
                {
                    int n = r.ReadInt32();
                    var list = new List<LodSection>();
                    for (int k = 0; k < n; k++)
                    {
                        var s = new LodSection();
                        s.Name = System.Text.Encoding.ASCII.GetString(r.ReadBytes(r.ReadUInt16()));
                        s.MaterialId = r.ReadInt32(); s.HasRadiosity = r.ReadInt32() != 0; s.VertexCount = r.ReadInt32();
                        s.Data = new float[s.VertexCount * 7];
                        for (int i = 0; i < s.Data.Length; i++) s.Data[i] = r.ReadSingle();
                        int t = r.ReadInt32();
                        s.Triangles = new int[t * 3];
                        for (int i = 0; i < s.Triangles.Length; i++) s.Triangles[i] = r.ReadInt32();
                        list.Add(s);
                    }
                    lods.Add(list);
                }
            }
            return null;
        }

        /// <summary>
        /// Write src's lightmap UVs onto meshPath by vertex index, every LOD and section that declares the channel. A
        /// section is written only when src has the same vertex count there (and, with checkPositions, the same positions
        /// within 1 mm); anything else is counted as skipped, never guessed.
        /// </summary>
        public static string ApplyLodsByIndex(string meshPath, List<List<LodSection>> src, bool checkPositions,
                                              out int written, out int total, out int skippedSections, out string note,
                                              float tolerance = 1e-3f)
        {
            written = 0; total = 0; skippedSections = 0; note = null;
            MeshSet meshSet; ulong rid; byte[] buf; MeshSetLod lod;
            string err = Open(meshPath, out meshSet, out rid, out buf, out lod);
            if (err != null) return err;
            int lodCount = meshSet.Lods.Count;
            var why = new List<string>();
            bool inlineChanged = false;
            for (int l = 0; l < lodCount && l < src.Count; l++)
            {
                err = OpenLodIndex(meshPath, l, out meshSet, out rid, out buf, out lod);
                if (err != null) return err;
                bool changed = false;
                for (int k = 0; k < lod.Sections.Count; k++)
                {
                    MeshSetSection section = lod.Sections[k];
                    if (section.VertexCount == 0) continue;
                    GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
                    int stride = decl.Streams[0].VertexStride;
                    int radOff, posOff; VertexElementFormat radFmt, posFmt;
                    if (stride <= 0 || !FindElement(decl, VertexElementUsage.RadiosityTexCoord, out radOff, out radFmt)) continue;
                    FindElement(decl, VertexElementUsage.Pos, out posOff, out posFmt);
                    total += (int)section.VertexCount;
                    LodSection s = k < src[l].Count ? src[l][k] : null;
                    if (s == null || s.VertexCount != section.VertexCount)
                    {
                        skippedSections++;
                        why.Add("lod" + l + "/sec" + k + ": " + (s == null ? "no source section" : "source has " + s.VertexCount + " vertices, mesh " + section.VertexCount));
                        continue;
                    }
                    if (checkPositions)
                    {
                        int bad = 0;
                        for (int i = 0; i < s.VertexCount; i++)
                        {
                            float[] p = ReadPos(buf, (int)((long)section.VertexOffset + (long)i * stride + posOff), posFmt);
                            int o = i * 7;
                            if (Math.Abs(p[0] - s.Data[o]) > tolerance || Math.Abs(p[1] - s.Data[o + 1]) > tolerance || Math.Abs(p[2] - s.Data[o + 2]) > tolerance) bad++;
                        }
                        if (bad > 0)
                        {
                            skippedSections++;
                            why.Add("lod" + l + "/sec" + k + ": " + bad + " of " + s.VertexCount + " positions differ from the source");
                            continue;
                        }
                    }
                    for (int i = 0; i < s.VertexCount; i++)
                    {
                        long at = (long)section.VertexOffset + (long)i * stride;
                        WriteUv(buf, (int)(at + radOff), radFmt, s.Data[i * 7 + 5], s.Data[i * 7 + 6]);
                    }
                    written += s.VertexCount;
                    changed = true;
                }
                if (!changed) continue;
                if (lod.ChunkId != Guid.Empty)
                {
                    if (!App.AssetManager.ModifyChunk(lod.ChunkId, buf)) return "ModifyChunk failed for LOD " + l + " of " + meshPath;
                }
                else { lod.SetInlineData(buf); inlineChanged = true; }
            }
            if (inlineChanged) App.AssetManager.ModifyRes(rid, meshSet);
            note = why.Count == 0 ? "all sections by index" : string.Join("; ", why.ToArray());
            return null;
        }

        /// <summary>
        /// RadiosityTexCoord := TexCoord<paramref name="channel"/> on every vertex of every LOD section that declares both,
        /// then (restoreFrom >= 0) TexCoord<channel> := TexCoord<restoreFrom>. The FBX importer always writes
        /// RadiosityTexCoord as (0, 0) (FrostyToolsuite FBXImporter.cs) but fills TexCoord1..7 from the FBX's extra uv
        /// maps, keeping vertices apart where they differ -- so a lightmap uv map imported as TexCoord1 arrives intact and
        /// with its seams split, and this moves it to the channel the lightmap draw reads. Sections without both channels
        /// are counted, not touched. A vertex whose copied uv is outside [0, 1] or a section whose uvs are all equal
        /// (no chart) is reported.
        /// </summary>
        public static string CopyTexCoordToRadiosity(string meshPath, int channel, int restoreFrom, out int written, out int sections,
                                                     out int skippedSections, out int outOfRange, out string note)
        {
            written = 0; sections = 0; skippedSections = 0; outOfRange = 0; note = null;
            if (channel < 1 || channel > 7) return "channel must be 1..7";
            VertexElementUsage src = (VertexElementUsage)((int)VertexElementUsage.TexCoord0 + channel);
            VertexElementUsage rst = restoreFrom >= 0 ? (VertexElementUsage)((int)VertexElementUsage.TexCoord0 + restoreFrom) : VertexElementUsage.Unknown;
            MeshSet meshSet; ulong rid; byte[] buf; MeshSetLod lod;
            string err = Open(meshPath, out meshSet, out rid, out buf, out lod);
            if (err != null) return err;
            int lodCount = meshSet.Lods.Count;
            bool inlineChanged = false;
            var why = new List<string>();
            for (int l = 0; l < lodCount; l++)
            {
                err = OpenLodIndex(meshPath, l, out meshSet, out rid, out buf, out lod);
                if (err != null) return err;
                bool changed = false;
                for (int k = 0; k < lod.Sections.Count; k++)
                {
                    MeshSetSection section = lod.Sections[k];
                    if (section.VertexCount == 0) continue;
                    GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
                    int stride = decl.Streams[0].VertexStride;
                    int radOff = 0, srcOff = 0, rstOff = 0; VertexElementFormat radFmt = VertexElementFormat.None, srcFmt = VertexElementFormat.None, rstFmt = VertexElementFormat.None;
                    bool hasRad = stride > 0 && FindElement(decl, VertexElementUsage.RadiosityTexCoord, out radOff, out radFmt);
                    bool hasSrc = stride > 0 && FindElement(decl, src, out srcOff, out srcFmt);
                    if (!hasRad || !hasSrc)
                    {
                        if (hasRad || hasSrc) { skippedSections++; why.Add("lod" + l + "/sec" + k + ": " + (hasRad ? "no " + src : "no RadiosityTexCoord")); }
                        continue;
                    }
                    bool restore = restoreFrom >= 0 && FindElement(decl, rst, out rstOff, out rstFmt);
                    float u0 = float.NaN, v0 = float.NaN; bool varied = false;
                    for (int i = 0; i < section.VertexCount; i++)
                    {
                        long at = (long)section.VertexOffset + (long)i * stride;
                        if (at + stride > buf.Length) break;
                        float[] uv = ReadUv(buf, (int)(at + srcOff), srcFmt);
                        if (uv[0] < -1e-4f || uv[0] > 1.0001f || uv[1] < -1e-4f || uv[1] > 1.0001f) outOfRange++;
                        if (float.IsNaN(u0)) { u0 = uv[0]; v0 = uv[1]; }
                        else if (Math.Abs(uv[0] - u0) > 1e-6f || Math.Abs(uv[1] - v0) > 1e-6f) varied = true;
                        WriteUv(buf, (int)(at + radOff), radFmt, uv[0], uv[1]);
                        if (restore)
                        {
                            float[] r = ReadUv(buf, (int)(at + rstOff), rstFmt);
                            WriteUv(buf, (int)(at + srcOff), srcFmt, r[0], r[1]);
                        }
                        written++;
                    }
                    if (!varied) why.Add("lod" + l + "/sec" + k + ": every vertex has the same uv (no chart)");
                    sections++;
                    changed = true;
                }
                if (!changed) continue;
                if (lod.ChunkId != Guid.Empty)
                {
                    if (!App.AssetManager.ModifyChunk(lod.ChunkId, buf)) return "ModifyChunk failed for LOD " + l + " of " + meshPath;
                }
                else { lod.SetInlineData(buf); inlineChanged = true; }
            }
            if (inlineChanged) App.AssetManager.ModifyRes(rid, meshSet);
            note = why.Count == 0 ? null : string.Join("; ", why.ToArray());
            return sections == 0 ? "no section declares both RadiosityTexCoord and " + src : null;
        }
    }
}
