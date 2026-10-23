using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // Finds DONOR sections for a surface: sections of shipped meshes whose material uses a given shader, with everything
    // that decides how the engine draws them -- the geometry declarations, primitive type, the depot's mesh-parameter
    // block (its 0xBBAFB3AC entry is the vertex declaration's hash; the permutation is looked up by surface + that
    // declaration), the parameter/texture holder and the variation entry's RVM surface reference.
    //
    // A ported section that takes all of that from a donor (instead of from whatever template the mesh was cloned from) is
    // drawn by the donor's surface through a permutation the donor's level compiled -- the missing half of the "grown
    // sections keep the template's surface" bug (memory port-import-material-growth-bugs).
    public static class SurfaceDonors
    {
        /// <summary>Fnv1("!...") name hash of the mesh-parameter entry that carries the vertex declaration hash.</summary>
        public const uint DeclHashParam = 0xBBAFB3AC;

        public sealed class Donor
        {
            public string Mesh;
            public int Lod, Section, Material, LodCount;
            public bool Named;
            public PrimitiveType Prim;
            public uint DeclHash;
            public string Decl;            // decl0, readable
            public int Stride;
            public Guid RvmRef;
            public int RvmInt;
            public ushort Categories;      // bit i = in subset category i
            public bool Resident;          // mesh is in the caller's residency list (its level compiled the permutation)
            public List<GeometryDeclarationDesc.Element> Elements = new List<GeometryDeclarationDesc.Element>();   // decl0, stream 0
            public int LevelOfDetail = -1; // the mesh-parameter block's !levelOfDetail (RvmLevelOfDetail), -1 when absent
            public string Tessellation = "";   // the donor material's permutation key: TessKey(TessellationType, DisplacementMapTexCoord, DisplacementObjectScale)
            public string MeshType = "";   // the donor MeshSet's type (MeshType_Rigid / _Composite / _Skinned)
            public bool Occluder;          // a DICE *_Occluder stand-in mesh: an occlusion proxy that is never drawn
            public override string ToString()
            {
                return Mesh + " lod" + Lod + "/sec" + Section + " mat" + Material + (Named ? " named" : " unnamed") + " " + Prim + " decl=" + DeclHash + " stride=" + Stride +
                       " cats=0x" + Categories.ToString("X") + " rvm=" + RvmRef + "/0x" + RvmInt.ToString("X") + (Resident ? " RESIDENT" : "") + " [" + Decl + "]";
            }
        }

        /// <summary>
        /// Every section of every shipped mesh whose material <c>Shader.Shader</c> is <paramref name="shader"/>. Meshes come from the
        /// reverse reference index (users of the shader). <paramref name="resident"/> (lower-case mesh paths) marks donors whose
        /// permutations the target level is known to carry. <paramref name="maxMeshes"/> bounds the scan.
        /// </summary>
        public static List<Donor> Scan(EbxAssetEntry shader, HashSet<string> resident, int maxMeshes, Func<string, List<string>> users)
        {
            var result = new List<Donor>();
            if (shader == null) return result;
            List<string> meshes = users(shader.Guid.ToString("D")) ?? new List<string>();
            int scanned = 0;
            foreach (string user in meshes)
            {
                if (scanned >= maxMeshes) break;
                EbxAssetEntry me = App.AssetManager.GetEbxEntry(user);
                if (me == null || (me.Type != "RigidMeshAsset" && me.Type != "CompositeMeshAsset" && me.Type != "SkinnedMeshAsset")) continue;
                if (me.IsAdded) continue;   // shipped meshes only: a donor is evidence of what the game compiled
                scanned++;
                try { result.AddRange(ScanMesh(me, shader.Guid, resident)); } catch { }
            }
            return result;
        }

        // DICE ships occlusion proxies of props as ordinary meshes named <prop>_Occluder (Container_L_01_Occluder_Mesh): same
        // surface, a simplified hull, never drawn. A port drawn through one of its sections was invisible in game (Hoth
        // ContainerHuge_01, HOTH_N4), so they are no evidence of a drawable permutation.
        public static bool IsOccluderStandIn(string meshPath)
        {
            string leaf = meshPath.Substring(meshPath.LastIndexOf('/') + 1);
            return leaf.IndexOf("_occluder", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Whether a donor section can draw a section of a target mesh of the given kind. A composite (or skinned) donor's
        // permutation places each vertex by its part (bone) matrix; a rigid mesh placed by a static model group supplies none,
        // so a rigid port drawn through a composite donor is invisible at every distance while its collision stays (HOTH_N4:
        // BoxMedium_02 / BoxSmall_03 on df9_01_cluster_01, BoxMedium_01_B on df9, LightGroundLit_03 on boxsmall_01_cluster).
        // A composite target may use rigid or composite donors (PortGeo prefers part-keeping ones for multi-part sections).
        public static bool Drawable(Donor d, bool compositeTarget)
        {
            if (d.Occluder) return false;
            if (compositeTarget) return !d.MeshType.EndsWith("Skinned", StringComparison.OrdinalIgnoreCase);
            return d.MeshType.Length == 0 || d.MeshType.EndsWith("Rigid", StringComparison.OrdinalIgnoreCase);
        }

        public static List<Donor> ScanMesh(EbxAssetEntry me, Guid shaderFileGuid, HashSet<string> resident)
        {
            var result = new List<Donor>();
            EbxAsset asset = App.AssetManager.GetEbx(me);
            IList mats = ReadMember(asset.RootObject, "Materials") as IList;
            if (mats == null) return result;
            var matUses = new List<int>();
            var tessKeys = new Dictionary<int, string>();
            for (int i = 0; i < mats.Count; i++)
            {
                object mat = ResolveInternal(mats[i]);
                object sh = ReadMember(mat, "Shader");
                Guid g;
                if (TryExternal(ReadMember(sh, "Shader"), out g) && g == shaderFileGuid)
                {
                    matUses.Add(i);
                    tessKeys[i] = TessKey(Str(ReadMember(mat, "TessellationType")), Str(ReadMember(mat, "DisplacementMapTexCoord")), Str(ReadMember(mat, "DisplacementObjectScale")));
                }
            }
            if (matUses.Count == 0) return result;
            ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
            if (rid == 0) return result;
            MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
            ResAssetEntry nb = App.AssetManager.GetResEntry(me.Name.ToLowerInvariant() + "_mesh/blocks");
            if (ms == null || nb == null) return result;
            ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(nb);
            var all = (List<ShaderBlockResource>)typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot);
            var entries = (List<ShaderBlockEntry>)typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot);
            bool isResident = resident != null && resident.Contains(me.Name.ToLowerInvariant());
            for (int l = 0; l < ms.Lods.Count && l < entries.Count; l++)
            {
                MeshSetLod lod = ms.Lods[l];
                ShaderBlockEntry sbe = entries[l];
                ShaderBlockMeshVariationEntry mv = FindVariation(all, sbe);
                for (int s = 0; s < lod.Sections.Count && s < sbe.Resources.Count; s++)
                {
                    MeshSetSection sec = lod.Sections[s];
                    if (!matUses.Contains(sec.MaterialId)) continue;
                    var d = new Donor
                    {
                        Mesh = me.Name, Lod = l, Section = s, Material = sec.MaterialId, LodCount = ms.Lods.Count, Named = !string.IsNullOrEmpty(sec.Name),
                        Prim = sec.PrimitiveType, Stride = (int)sec.VertexStride, Resident = isResident,
                        Decl = sec.GeometryDeclDesc != null && sec.GeometryDeclDesc.Length > 0 ? MeshLayoutDump.DescribeDecl(sec.GeometryDeclDesc[0]) : "",
                        Categories = CategoryBits(lod, s),
                        Tessellation = tessKeys.ContainsKey(sec.MaterialId) ? tessKeys[sec.MaterialId] : "",
                        MeshType = ms.Type.ToString(),
                        Occluder = IsOccluderStandIn(me.Name)
                    };
                    if (sec.GeometryDeclDesc != null && sec.GeometryDeclDesc.Length > 0) d.Elements = MeshGeoPort.Elements(sec.GeometryDeclDesc[0]);
                    MeshParamDbBlock mp = sbe.GetMeshParams(s);
                    if (mp != null)
                    {
                        d.DeclHash = ParamUInt(mp.Parameters, DeclHashParam);
                        uint lodHash = (uint)Frosty.Hash.Fnv1.HashString("!leveloFdetail".ToLowerInvariant());
                        foreach (ParameterEntry p in mp.Parameters) if (p.NameHash == lodHash && p.Value != null && p.Value.Length >= 4) d.LevelOfDetail = BitConverter.ToInt32(p.Value, 0);
                    }
                    if (mv != null && s < mv.RvmShaderRefGuids.Count) { d.RvmRef = mv.RvmShaderRefGuids[s]; d.RvmInt = mv.RvmShaderRefInts[s]; }
                    result.Add(d);
                }
            }
            return result;
        }

        /// <summary>
        /// A material's tessellation permutation key. The game hashes (TessellationType, DisplacementMapTexCoord,
        /// DisplacementObjectScale) into the shader permutation, so a tessellated section can only be drawn through a donor whose
        /// material has the same key. "" for no tessellation.
        /// </summary>
        public static string TessKey(string type, string texCoord, string objectScale)
        {
            if (string.IsNullOrEmpty(type) || type == "ShaderTessellationType_None") return "";
            return type + "|" + (texCoord ?? "0") + "|" + (objectScale ?? "False");
        }

        private static string Str(object v) { return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }

        public static ShaderBlockMeshVariationEntry FindVariation(List<ShaderBlockResource> all, ShaderBlockEntry sbe)
        {
            foreach (ShaderBlockResource r in all) if (r is ShaderBlockMeshVariationEntry && r.Hash == sbe.Hash) return (ShaderBlockMeshVariationEntry)r;
            return null;
        }

        public static ushort CategoryBits(MeshSetLod lod, int sectionIndex)
        {
            ushort bits = 0;
            List<List<byte>> cats = lod.CategorySubsetIndices;
            for (int c = 0; cats != null && c < cats.Count; c++)
                if (cats[c] != null && cats[c].Contains((byte)sectionIndex)) bits |= (ushort)(1 << c);
            return bits;
        }

        public static uint ParamUInt(List<ParameterEntry> ps, uint nameHash)
        {
            foreach (ParameterEntry p in ps)
                if (p.NameHash == nameHash && p.Value != null && p.Value.Length >= 4) return BitConverter.ToUInt32(p.Value, 0);
            return 0;
        }

        private static bool TryExternal(object pointerRef, out Guid fileGuid)
        {
            fileGuid = Guid.Empty;
            object external = ReadMember(pointerRef, "External");
            if (external == null) return false;
            object fg = ReadMember(external, "FileGuid");
            if (!(fg is Guid) || (Guid)fg == Guid.Empty) return false;
            fileGuid = (Guid)fg;
            return true;
        }

        private static object ResolveInternal(object value)
        {
            if (value == null) return null;
            if (value.GetType().Name != "PointerRef") return value;
            return ReadMember(value, "Internal");
        }

        private static object ReadMember(object value, string name)
        {
            if (value == null) return null;
            Type type = value.GetType();
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(value);
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field != null ? field.GetValue(value) : null;
        }
    }
}
