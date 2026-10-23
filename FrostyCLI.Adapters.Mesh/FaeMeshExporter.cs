using System;
using System.Collections;
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
    // Exports a SkinnedMeshAsset's LOD0 to a flat .faemesh (FAEMESH1) binary that FAAE skins +
    // textures without Assimp: bone names, per-vertex position/normal/uv/boneIndices[4]/weights[4],
    // triangle indices, and the BaseColor texture EBX path. Reads the same vertex stream the FBX/OBJ
    // export uses, but keeps the skin (bone index + weight) channels the FBX path drops.
    //
    // Positions/normals/uv are emitted exactly as the asset stores them (FAAE handles Y-up/handedness,
    // same as today's OBJ). Bone indices are the skeleton's global joint indices (SWBF2 stores them
    // directly); the bone-name table is the skeleton's BoneNames so FAAE binds by name.
    public static class FaeMeshExporter
    {
        public const string DefaultSkeleton = "Characters/Rigs/Humanoids/Walrus_HumanMale";

        private struct Vtx
        {
            public float PX, PY, PZ, NX, NY, NZ, U, V;
            public int B0, B1, B2, B3;
            public float W0, W1, W2, W3;
        }

        public static bool Export(EbxAssetEntry entry, string outPath, string skeletonPath)
        {
            if (App.AssetManager == null) throw new InvalidOperationException("AssetManager not initialized.");
            if (string.IsNullOrEmpty(skeletonPath)) skeletonPath = DefaultSkeleton;

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            if (asset == null) throw new Exception("Failed to load EBX: " + entry.Name);
            dynamic mesh = asset.RootObject;

            ulong resRid = (ulong)mesh.MeshSetResource;
            if (resRid == 0) throw new Exception("MeshSetResource is 0 for " + entry.Name);
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            if (resEntry == null) throw new Exception("ResAssetEntry not found for RID 0x" + resRid.ToString("X16"));
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);
            if (meshSet == null || meshSet.Lods.Count == 0) throw new Exception("No MeshSet/LODs for " + entry.Name);

            List<string> boneNames = ReadBoneNames(skeletonPath);
            MeshSetLod lod = meshSet.Lods[0];
            int indexSize = lod.IndexUnitSize / 8; // bits -> bytes
            if (indexSize != 2 && indexSize != 4) indexSize = 2;

            var verts = new List<Vtx>();
            var indices = new List<uint>();
            bool swbf2 = ProfilesLibrary.DataVersion == 20171117;

            using (NativeReader reader = new NativeReader(lod.ChunkId != Guid.Empty
                ? App.AssetManager.GetChunk(App.AssetManager.GetChunkEntry(lod.ChunkId))
                : new MemoryStream(lod.InlineData)))
            {
                foreach (MeshSetSection section in lod.Sections)
                {
                    if (string.IsNullOrEmpty(section.Name) || section.VertexCount == 0) continue;
                    int baseVertex = verts.Count;
                    ReadSectionVertices(reader, section, boneNames.Count, swbf2, verts);

                    reader.Position = lod.VertexBufferSize + section.StartIndex * indexSize;
                    int idxCount = (int)section.PrimitiveCount * 3;
                    for (int i = 0; i < idxCount; i++)
                    {
                        uint v = indexSize == 2 ? reader.ReadUShort() : reader.ReadUInt();
                        indices.Add((uint)(v + baseVertex));
                    }
                }
            }

            string albedo = ResolveAlbedoPath(entry) ?? "";

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var w = new BinaryWriter(File.Create(outPath)))
            {
                w.Write(Encoding.ASCII.GetBytes("FAEMESH1"));
                w.Write((uint)boneNames.Count);
                foreach (string bn in boneNames) { w.Write(Encoding.UTF8.GetBytes(bn ?? "")); w.Write((byte)0); }
                w.Write((uint)verts.Count);
                foreach (Vtx v in verts)
                {
                    w.Write(v.PX); w.Write(v.PY); w.Write(v.PZ);
                    w.Write(v.NX); w.Write(v.NY); w.Write(v.NZ);
                    w.Write(v.U); w.Write(v.V);
                    w.Write(v.B0); w.Write(v.B1); w.Write(v.B2); w.Write(v.B3);
                    w.Write(v.W0); w.Write(v.W1); w.Write(v.W2); w.Write(v.W3);
                }
                w.Write((uint)indices.Count);
                foreach (uint idx in indices) w.Write(idx);
                byte[] albedoBytes = Encoding.UTF8.GetBytes(albedo);
                w.Write((uint)albedoBytes.Length);
                w.Write(albedoBytes);
            }
            return true;
        }

        private static void ReadSectionVertices(NativeReader reader, MeshSetSection section, int boneNameCount, bool swbf2, List<Vtx> outVerts)
        {
            GeometryDeclarationDesc decl = section.GeometryDeclDesc[0];
            var streams = decl.Streams;
            var elements = decl.Elements;
            // Byte base of each stream within the section's vertex region (stream-major layout).
            int[] streamBase = new int[streams.Length];
            int acc = 0;
            for (int s = 0; s < streams.Length; s++) { streamBase[s] = acc; acc += streams[s].VertexStride; }

            List<ushort> boneList = section.BoneList;
            for (int j = 0; j < section.VertexCount; j++)
            {
                var v = new Vtx { B0 = -1, B1 = -1, B2 = -1, B3 = -1 };
                var idx8 = new int[8]; var w8 = new float[8];
                bool gotUv = false;
                foreach (GeometryDeclarationDesc.Element e in elements)
                {
                    if (e.Usage == VertexElementUsage.Unknown) continue;
                    int s = e.StreamIndex;
                    if (s >= streams.Length || streams[s].VertexStride == 0) continue;
                    long pos = section.VertexOffset + (long)streamBase[s] * section.VertexCount
                             + (long)j * streams[s].VertexStride + e.Offset;
                    reader.Position = pos;

                    int u = (int)e.Usage;
                    if (e.Usage == VertexElementUsage.Pos) { var p = ReadVec3(reader, e.Format); v.PX = p[0]; v.PY = p[1]; v.PZ = p[2]; }
                    else if (e.Usage == VertexElementUsage.Normal) { var n = ReadVec3(reader, e.Format); v.NX = n[0]; v.NY = n[1]; v.NZ = n[2]; }
                    else if (u >= 33 && u <= 40) { if (!gotUv) { var uv = ReadVec2(reader, e.Format); v.U = uv[0]; v.V = uv[1]; gotUv = true; } }
                    else if (e.Usage == VertexElementUsage.BoneIndices) ReadIdx4(reader, e.Format, idx8, 0);
                    else if (e.Usage == VertexElementUsage.BoneIndices2) ReadIdx4(reader, e.Format, idx8, 4);
                    else if (e.Usage == VertexElementUsage.BoneWeights) ReadW4(reader, w8, 0);
                    else if (e.Usage == VertexElementUsage.BoneWeights2) ReadW4(reader, w8, 4);
                }

                // Pick the top 4 influences by weight; remap + validate bone indices.
                ResolveSkin(idx8, w8, boneList, boneNameCount, swbf2, ref v);
                outVerts.Add(v);
            }
        }

        private static void ResolveSkin(int[] idx8, float[] w8, List<ushort> boneList, int boneNameCount, bool swbf2, ref Vtx v)
        {
            // Order influences by descending weight, keep up to 4.
            var order = new int[8]; for (int i = 0; i < 8; i++) order[i] = i;
            Array.Sort(order, (a, b) => w8[b].CompareTo(w8[a]));
            int[] bi = { -1, -1, -1, -1 }; float[] bw = { 0, 0, 0, 0 };
            int n = 0;
            for (int k = 0; k < 8 && n < 4; k++)
            {
                int i = order[k];
                if (w8[i] <= 0f) break;
                int raw = idx8[i];
                if (!swbf2 && boneList != null && raw < boneList.Count) raw = boneList[raw];
                if ((raw & 0x8000) != 0) continue;       // procedural/extra bone — unsupported
                if (raw < 0 || raw >= boneNameCount) continue;
                bi[n] = raw; bw[n] = w8[i]; n++;
            }
            float sum = bw[0] + bw[1] + bw[2] + bw[3];
            if (sum > 0f) { bw[0] /= sum; bw[1] /= sum; bw[2] /= sum; bw[3] /= sum; }
            v.B0 = bi[0]; v.B1 = bi[1]; v.B2 = bi[2]; v.B3 = bi[3];
            v.W0 = bw[0]; v.W1 = bw[1]; v.W2 = bw[2]; v.W3 = bw[3];
        }

        private static float[] ReadVec3(NativeReader r, VertexElementFormat f)
        {
            if (f == VertexElementFormat.Float3 || f == VertexElementFormat.Float4)
                return new[] { r.ReadFloat(), r.ReadFloat(), r.ReadFloat() };
            // Half3 / Half4
            return new[] { HalfUtils.Unpack(r.ReadUShort()), HalfUtils.Unpack(r.ReadUShort()), HalfUtils.Unpack(r.ReadUShort()) };
        }

        private static float[] ReadVec2(NativeReader r, VertexElementFormat f)
        {
            if (f == VertexElementFormat.Float2) return new[] { r.ReadFloat(), r.ReadFloat() };
            return new[] { HalfUtils.Unpack(r.ReadUShort()), HalfUtils.Unpack(r.ReadUShort()) };
        }

        private static void ReadIdx4(NativeReader r, VertexElementFormat f, int[] dst, int off)
        {
            bool ushort4 = f == VertexElementFormat.UShort4 || f == VertexElementFormat.UShort4N;
            for (int i = 0; i < 4; i++) dst[off + i] = ushort4 ? r.ReadUShort() : r.ReadByte();
        }

        private static void ReadW4(NativeReader r, float[] dst, int off)
        {
            for (int i = 0; i < 4; i++) dst[off + i] = r.ReadByte() / 255f;
        }

        private static List<string> ReadBoneNames(string skeletonPath)
        {
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(skeletonPath);
            if (e == null) throw new Exception("Skeleton not found: " + skeletonPath);
            dynamic root = App.AssetManager.GetEbx(e).RootObject;
            var names = new List<string>();
            foreach (var n in (IEnumerable)root.BoneNames) names.Add(n.ToString());
            return names;
        }

        // Find the BaseColor texture EBX path for this mesh from its MeshVariationDatabase. The MVDB
        // is co-located with the mesh (e.g. <meshdir>/vur_<name>_bpb/MeshVariationDb_Win32), so only
        // those are loaded (not all ~3600 DBs). Matches the DB entry whose Mesh ref == this mesh.
        private static string ResolveAlbedoPath(EbxAssetEntry meshEntry)
        {
            try
            {
                string name = meshEntry.Name ?? "";
                int slash = name.LastIndexOf('/');
                string dir = slash > 0 ? name.Substring(0, slash) : name;
                Guid meshGuid = meshEntry.Guid;

                string fallback = null;
                foreach (EbxAssetEntry dbEntry in App.AssetManager.EnumerateEbx("MeshVariationDatabase"))
                {
                    if (dbEntry.Name == null || dbEntry.Name.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    EbxAsset db; try { db = App.AssetManager.GetEbx(dbEntry); } catch { continue; }
                    IList entries = FindMeshVariationList(db.RootObject);
                    if (entries == null) continue;
                    foreach (object me in entries)
                    {
                        if (me == null) continue;
                        string tex = FirstBaseColor(me);
                        if (tex == null) continue;
                        if (RefMatches(me, "Mesh", meshGuid)) return tex; // exact mesh match
                        if (fallback == null) fallback = tex;
                    }
                }
                return fallback;
            }
            catch { }
            return null;
        }

        private static IList FindMeshVariationList(object root)
        {
            foreach (PropertyInfo p in root.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                object val; try { val = p.GetValue(root); } catch { continue; }
                if (val is IList list)
                {
                    Type et = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0]
                            : (list.Count > 0 && list[0] != null ? list[0].GetType() : null);
                    if (et != null && et.Name.IndexOf("MeshVariationDatabaseEntry", StringComparison.OrdinalIgnoreCase) >= 0)
                        return list;
                }
            }
            return null;
        }

        private static string FirstBaseColor(object meshVarEntry)
        {
            object materials = GetProp(meshVarEntry, "Materials");
            if (!(materials is IList matList)) return null;
            foreach (object mat in matList)
            {
                object tps = GetProp(mat, "TextureParameters");
                if (!(tps is IList tpList)) continue;
                foreach (object tp in tpList)
                {
                    string pname = GetProp(tp, "ParameterName")?.ToString();
                    if (string.IsNullOrEmpty(pname)) continue;
                    string lp = pname.ToLowerInvariant();
                    if (lp.Contains("color") || lp.Contains("albedo") || lp.Contains("basecolor") || lp.EndsWith("_c") || lp == "diffuse")
                    {
                        string path = ResolveRefPath(GetProp(tp, "Value"));
                        if (path != null) return path;
                    }
                }
            }
            return null;
        }

        private static bool RefMatches(object obj, string member, Guid fileGuid)
        {
            object pr = GetProp(obj, member);
            return ResolveRefGuid(pr) == fileGuid && fileGuid != Guid.Empty;
        }

        private static Guid ResolveRefGuid(object pr)
        {
            if (pr == null) return Guid.Empty;
            object ext = GetMember(pr, "External");
            if (ext == null) return Guid.Empty;
            object fg = GetMember(ext, "FileGuid");
            return fg is Guid ? (Guid)fg : Guid.Empty;
        }

        private static string ResolveRefPath(object pr)
        {
            Guid g = ResolveRefGuid(pr);
            if (g == Guid.Empty) return null;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(g);
            return e?.Name;
        }

        private static object GetProp(object o, string name)
        {
            if (o == null) return null;
            try { PropertyInfo p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); return p?.GetValue(o); }
            catch { return null; }
        }
        private static object GetMember(object o, string name)
        {
            object p = GetProp(o, name);
            if (p != null) return p;
            try { FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance); return f?.GetValue(o); } catch { return null; }
        }
    }
}
