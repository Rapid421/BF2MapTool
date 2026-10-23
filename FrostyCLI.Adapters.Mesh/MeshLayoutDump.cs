using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // Read-only diagnostic: everything that decides whether (and with which surface) the engine draws each
    // section of a mesh -- per LOD the section table (both geometry declarations, primitive type, stride, the
    // per-section fields the plugin keeps private), the subset categories, and the shader block depot as the
    // runtime pairs it with the sections: per section the mesh-parameter block, the parameter/texture holder
    // and the RVM surface reference of the LOD's variation entry. Plus the EBX materials' shader paths.
    public static class MeshLayoutDump
    {
        // Parameter names seen in mesh-parameter blocks and material holders; names are stored as
        // Fnv1(lower(name)) so a name is only shown when it is in this list.
        public static readonly string[] KnownParamNames = new[]
        {
            "!primitiveCount", "!startIndex", "!vertexStreamOffsets0", "!vertexStreamOffsets1", "!vertexStreamOffsets2",
            "!vertexStreamOffsets3", "!primitiveType", "!instancingMethod", "!lodIndex", "!levelOfDetail", "!vertexCount",
            "!vertexStride", "!indexBufferFormat", "!baseVertexIndex", "!vertexOffset", "!indexOffset", "!meshLodIndex",
        };

        public static string Describe(EbxAssetEntry entry, IEnumerable<string> extraNames, out List<string> lines)
        {
            lines = new List<string>();
            if (entry == null) return "no mesh entry.";
            var names = new Dictionary<uint, string>();
            foreach (string n in KnownParamNames) AddName(names, n);
            foreach (string n in MaterialParamDumper.DefaultNames) AddName(names, n);
            if (extraNames != null) foreach (string n in extraNames) AddName(names, n);
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                if (re == null) return "MeshSet res not found.";
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load the MeshSet.";
                lines.Add("mesh " + entry.Name + " type=" + ms.Type + " lods=" + ms.Lods.Count + " flags=0x" + ((ulong)ms.Flags).ToString("X") + " nameHash=" + ms.NameHash);
                // EBX materials
                IList mats = ReadMember(asset.RootObject, "Materials") as IList;
                for (int i = 0; mats != null && i < mats.Count; i++)
                {
                    object mat = ResolveInternal(mats[i]);
                    object shader = ReadMember(mat, "Shader");
                    string sp = RefPath(ReadMember(shader, "Shader"));
                    lines.Add("  material " + i + ": shader=" + sp + " bool=" + Count(shader, "BoolParameters") + " vec=" + Count(shader, "VectorParameters") +
                              " tex=" + Count(shader, "TextureParameters") + " cond=" + Count(shader, "ConditionalParameters") + " instGuid=" + InstanceGuidOf(mat));
                }
                // depot
                ResAssetEntry nb = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
                ShaderBlockDepot depot = nb != null ? App.AssetManager.GetResAs<ShaderBlockDepot>(nb) : null;
                List<ShaderBlockResource> all = null; List<ShaderBlockEntry> entries = null;
                if (depot != null)
                {
                    all = (List<ShaderBlockResource>)typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot);
                    entries = (List<ShaderBlockEntry>)typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot);
                    lines.Add("  depot " + nb.Name + ": resources=" + all.Count + " lodEntries=" + entries.Count + " meta=" + (nb.ResMeta != null ? BitConverter.ToString(nb.ResMeta).Replace("-", "") : "null"));
                    if (Environment.GetEnvironmentVariable("FROSTYCLI_LAYOUT_RESOURCES") == "1")
                        for (int ri = 0; ri < all.Count; ri++)
                        {
                            ShaderBlockResource r = all[ri];
                            var kids = new List<string>();
                            var sp = r as ShaderStaticParamDbBlock;
                            if (sp != null) foreach (ShaderBlockResource c in sp.Resources) kids.Add(c.Index.ToString());
                            string extra = "";
                            var mv0 = r as ShaderBlockMeshVariationEntry;
                            if (mv0 != null) extra = " refs=" + mv0.RvmShaderRefGuids.Count;
                            var mp0 = r as MeshParamDbBlock;
                            if (mp0 != null) extra = " lod=" + mp0.LodIndex;
                            lines.Add("    res#" + ri + " idx=" + r.Index + " " + r.GetType().Name + " h=" + r.Hash.ToString("X16") + (kids.Count > 0 ? " kids=[" + string.Join(",", kids) + "]" : "") + extra);
                        }
                }
                else lines.Add("  depot: none");
                for (int l = 0; l < ms.Lods.Count; l++)
                {
                    MeshSetLod lod = ms.Lods[l];
                    var cats = new List<string>();
                    if (lod.CategorySubsetIndices != null)
                        for (int c = 0; c < lod.CategorySubsetIndices.Count; c++)
                            cats.Add(c + ":[" + string.Join(",", lod.CategorySubsetIndices[c]) + "]");
                    lines.Add("  lod" + l + " flags=0x" + ((uint)lod.Flags).ToString("X") + " vb=" + lod.VertexBufferSize + " ib=" + lod.IndexBufferSize + " unit=" + lod.IndexUnitSize +
                              " chunk=" + (lod.ChunkId == Guid.Empty ? "inline" : lod.ChunkId.ToString()) + " adj=" + lod.AdjacencyBufferSize + " cats " + string.Join(" ", cats));
                    ShaderBlockEntry sbe = entries != null && l < entries.Count ? entries[l] : null;
                    ShaderBlockMeshVariationEntry mv = null;
                    if (sbe != null) foreach (ShaderBlockResource r in all) if (r is ShaderBlockMeshVariationEntry && r.Hash == sbe.Hash) { mv = (ShaderBlockMeshVariationEntry)r; break; }
                    if (sbe != null) lines.Add("    depotEntry hash=" + sbe.Hash.ToString("X16") + " sectionBlocks=" + sbe.Resources.Count + " variation=" + (mv != null ? mv.RvmShaderRefGuids.Count + " refs" : "none"));
                    for (int s = 0; s < lod.Sections.Count; s++)
                    {
                        MeshSetSection sec = lod.Sections[s];
                        lines.Add("    sec" + s + " name='" + sec.Name + "' mat=" + sec.MaterialId + " prim=" + sec.PrimitiveType + " v=" + sec.VertexCount + " p=" + sec.PrimitiveCount +
                                  " voff=" + sec.VertexOffset + " sidx=" + sec.StartIndex + " stride=" + sec.VertexStride);
                        GeometryDeclarationDesc[] decls = sec.GeometryDeclDesc;
                        for (int d = 0; decls != null && d < decls.Length; d++)
                            lines.Add("      decl" + d + ": " + DescribeDecl(decls[d]));
                        lines.Add("      fields: " + PrivateFields(sec));
                        if (sbe != null && s < sbe.Resources.Count)
                        {
                            lines.Add("      block " + DescribeBlock(sbe.Resources[s], names, 0));
                            if (mv != null && s < mv.RvmShaderRefGuids.Count)
                                lines.Add("      rvm " + mv.RvmShaderRefGuids[s] + " int=0x" + mv.RvmShaderRefInts[s].ToString("X"));
                        }
                    }
                }
                return null;
            }
            catch (Exception ex) { return "layout failed: " + ex.Message + "\n" + ex.StackTrace; }
        }

        public static string DescribeDecl(GeometryDeclarationDesc decl)
        {
            var sb = new StringBuilder();
            sb.Append("n=").Append(decl.ElementCount).Append(" streams=").Append(decl.StreamCount).Append(" [");
            if (decl.Elements != null)
                for (int i = 0; i < decl.Elements.Length; i++)
                {
                    var e = decl.Elements[i];
                    if (e.Usage == VertexElementUsage.Unknown && i >= decl.ElementCount) continue;
                    sb.Append(' ').Append(e.Usage).Append('@').Append(e.Offset).Append('/').Append(e.Format).Append("/s").Append(e.StreamIndex);
                }
            sb.Append(" ] strides");
            if (decl.Streams != null)
                for (int i = 0; i < decl.Streams.Length; i++)
                    if (decl.Streams[i].VertexStride != 0 || i < decl.StreamCount) sb.Append(' ').Append(i).Append(':').Append(decl.Streams[i].VertexStride).Append('/').Append(decl.Streams[i].Classification);
            return sb.ToString();
        }

        private static string DescribeBlock(ShaderBlockResource r, Dictionary<uint, string> names, int depth)
        {
            if (r == null) return "null";
            var sb = new StringBuilder();
            sb.Append(r.GetType().Name).Append('#').Append(r.Index).Append(" h=").Append(r.Hash.ToString("X16"));
            var mp = r as MeshParamDbBlock;
            if (mp != null)
            {
                sb.Append(" lod=").Append(mp.LodIndex).Append(" mesh=").Append(mp.MeshAssetGuid).Append(" {");
                foreach (ParameterEntry p in mp.Parameters) sb.Append(' ').Append(DescribeParam(p, names));
                sb.Append(" }");
                return sb.ToString();
            }
            var pp = r as ShaderPersistentParamDbBlock;
            if (pp != null)
            {
                sb.Append(" n=").Append(pp.Parameters.Count).Append(" {");
                foreach (ParameterEntry p in pp.Parameters) sb.Append(' ').Append(DescribeParam(p, names));
                sb.Append(" }");
                return sb.ToString();
            }
            var sp = r as ShaderStaticParamDbBlock;
            if (sp != null && depth < 4)
            {
                sb.Append(" [");
                foreach (ShaderBlockResource c in sp.Resources) sb.Append("\n        ").Append(new string(' ', depth * 2)).Append(DescribeBlock(c, names, depth + 1));
                sb.Append(" ]");
            }
            return sb.ToString();
        }

        public static string DescribeParam(ParameterEntry p, Dictionary<uint, string> names)
        {
            string n;
            if (names == null || !names.TryGetValue(p.NameHash, out n)) n = "0x" + p.NameHash.ToString("X8");
            object v = null;
            try { v = p.GetValue(); } catch { }
            string vs;
            if (v == null) vs = "raw:" + (p.Value != null ? BitConverter.ToString(p.Value).Replace("-", "") : "null") + "/t" + p.TypeHash.ToString("X8");
            else if (v is float[]) vs = string.Join(",", Array.ConvertAll((float[])v, f => f.ToString("R", CultureInfo.InvariantCulture)));
            else vs = Convert.ToString(v, CultureInfo.InvariantCulture);
            return n + "=" + vs;
        }

        private static void AddName(Dictionary<uint, string> names, string n)
        {
            if (string.IsNullOrEmpty(n)) return;
            uint h = (uint)Frosty.Hash.Fnv1.HashString(n.ToLowerInvariant());
            if (!names.ContainsKey(h)) names[h] = n;
        }

        private static string PrivateFields(object o)
        {
            var sb = new StringBuilder();
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                string n = f.Name;
                if (n == "m_geometryDeclarationDesc" || n == "m_materialName") continue;
                object v = f.GetValue(o);
                string vs;
                if (v == null) vs = "null";
                else if (v is byte[]) vs = BitConverter.ToString((byte[])v).Replace("-", "");
                else if (v is IEnumerable && !(v is string))
                {
                    var parts = new List<string>();
                    foreach (object x in (IEnumerable)v) parts.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
                    vs = "[" + string.Join(",", parts) + "]";
                }
                else vs = Convert.ToString(v, CultureInfo.InvariantCulture);
                sb.Append(n).Append('=').Append(vs).Append(' ');
            }
            return sb.ToString();
        }

        private static int Count(object o, string name) { var l = ReadMember(o, name) as IList; return l != null ? l.Count : -1; }

        private static string RefPath(object pr)
        {
            try
            {
                dynamic p = pr;
                Guid g = (Guid)p.External.FileGuid;
                if (g == Guid.Empty) return "null";
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(g);
                return e != null ? e.Name : g.ToString();
            }
            catch { return "?"; }
        }

        private static string InstanceGuidOf(object obj)
        {
            try { dynamic d = obj; return ((Guid)d.GetInstanceGuid().ExportedGuid).ToString(); } catch { return "?"; }
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
