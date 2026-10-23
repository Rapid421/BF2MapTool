using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // Rebuilds a (cloned) mesh from a MESHGEO1 dump of the source game's mesh so that every section is drawn by the surface
    // of ITS OWN material, through a vertex layout that surface was compiled for, filled with the source's own vertex data:
    //
    //   per section   the DONOR section (a shipped section drawn by that surface, SurfaceDonors) supplies the geometry
    //                 declarations, primitive type and private section fields, the depot's mesh-parameter block (vertex
    //                 declaration hash, primitive type, instancing method) and the variation entry's RVM surface reference;
    //   vertex data   every element of the donor's declaration is encoded from the source vertex (VertexCodec), converting
    //                 formats (Half3 -> Float3 ...) and deriving Binormal <-> BinormalSign where the layouts differ;
    //   indices       the source's, section-relative, unchanged;
    //   categories    the source LOD's subset categories (opaque / z-only / shadow membership), section for section;
    //   depot         rebuilt from scratch in the shipped order: holders first, then per LOD the section blocks, the LOD
    //                 entry and its variation entry.
    //
    // Nothing comes from the clone template except the MeshSet/LOD records themselves (LOD count, names, chunk ids) and the
    // depot entry hashes, which are keyed by the clone's own name. This replaces the FBX import for ports: the FBX path could
    // not write RadiosityTexCoord, DisplacementMapTexCoord or extra UV sets, and grown sections kept the template's surface.
    public static class MeshGeoPort
    {
        public sealed class SectionChoice
        {
            public int Lod, Section;
            public SurfaceDonors.Donor Donor;
            public MeshSetSection DonorSection;           // the donor MeshSet's section object (not modified)
            public MeshParamDbBlock DonorMeshParams;       // the donor depot's mesh-parameter block for that section
            public ShaderStaticParamDbBlock DonorHolder;   // the donor depot's parameter/texture holder for that section
            public Guid RvmRef; public int RvmInt;
            public LayoutMatch Match;
        }

        /// <summary>How a donor layout relates to a source section's layout (pure, see Compare).</summary>
        public sealed class LayoutMatch
        {
            public List<VertexElementUsage> Carried = new List<VertexElementUsage>();     // source usages written (maybe converted)
            public List<VertexElementUsage> Dropped = new List<VertexElementUsage>();     // source usages the donor layout lacks
            public List<VertexElementUsage> DroppedEmpty = new List<VertexElementUsage>(); // ... of those, constant "empty" channels
            public List<VertexElementUsage> Derived = new List<VertexElementUsage>();     // donor usages derived from others (Binormal <-> BinormalSign)
            public List<VertexElementUsage> Filled = new List<VertexElementUsage>();      // donor usages filled with the empty UV default
            public List<VertexElementUsage> FilledColors = new List<VertexElementUsage>(); // donor vertex colours filled (only when asked: see Compare)
            public List<string> Unfillable = new List<string>();                         // donor usages no rule can fill
            public int FormatChanges;                                                    // carried usages whose format differs
            public List<VertexElementUsage> Lossy = new List<VertexElementUsage>();       // carried usages whose source values do not survive the target format
            public bool Usable { get { return Unfillable.Count == 0; } }
            public override string ToString()
            {
                return "carried " + Carried.Count + (Dropped.Count > 0 ? ", dropped " + string.Join("/", Dropped) + (DroppedEmpty.Count > 0 ? " (empty: " + string.Join("/", DroppedEmpty) + ")" : "") : "") +
                       (Derived.Count > 0 ? ", derived " + string.Join("/", Derived) : "") + (Filled.Count > 0 ? ", filled " + string.Join("/", Filled) : "") +
                       (FilledColors.Count > 0 ? ", colour-filled " + string.Join("/", FilledColors) : "") +
                       (FormatChanges > 0 ? ", " + FormatChanges + " format change(s)" : "") + (Lossy.Count > 0 ? ", LOSSY " + string.Join("/", Lossy) : "") + (Unfillable.Count > 0 ? ", UNFILLABLE " + string.Join("/", Unfillable) : "");
            }
        }

        public static bool IsTexCoord(VertexElementUsage u) { return u >= VertexElementUsage.TexCoord0 && u <= VertexElementUsage.TexCoord7; }

        /// <summary>
        /// Compares a donor declaration with a source section. <paramref name="emptyUsages"/> are source channels that carry no
        /// data (constant (0,1) / (0,0) UVs): dropping those loses nothing.
        /// </summary>
        public static LayoutMatch Compare(IList<GeoElement> source, IList<GeometryDeclarationDesc.Element> donor, ICollection<VertexElementUsage> emptyUsages)
        {
            return Compare(source, donor, emptyUsages, false);
        }

        /// <summary>As Compare; <paramref name="zeroRadiosity"/> lets a donor's RadiosityTexCoord be filled with (0,0) when the source
        /// has no lightmap UVs at all (a mesh the source never lightmapped). <paramref name="fillColors"/> also accepts a donor whose
        /// layout has Color0/Color1 the source lacks, filled with the pipeline's neutral values (Color0 white, Color1 zero -- what
        /// DICE's composite users of such surfaces store, e.g. the BFII ComputerPanelLarge breakables); PortGeo asks for it only for
        /// a composite target whose section spans several parts and has no donor that keeps the part indices otherwise.</summary>
        public static LayoutMatch Compare(IList<GeoElement> source, IList<GeometryDeclarationDesc.Element> donor, ICollection<VertexElementUsage> emptyUsages, bool zeroRadiosity, bool fillColors = false)
        {
            var m = new LayoutMatch();
            var src = new Dictionary<VertexElementUsage, GeoElement>();
            foreach (GeoElement e in source) if (e.Stream == 0 && !src.ContainsKey(e.Usage)) src[e.Usage] = e;
            var dst = new HashSet<VertexElementUsage>();
            foreach (var e in donor)
            {
                if (e.Usage == VertexElementUsage.Unknown || e.StreamIndex != 0) continue;
                dst.Add(e.Usage);
                GeoElement s;
                if (src.TryGetValue(e.Usage, out s))
                {
                    if (!VertexCodec.Supported(e.Format) || !VertexCodec.Supported(s.Format)) { m.Unfillable.Add(e.Usage + " (format " + s.Format + " -> " + e.Format + ")"); continue; }
                    m.Carried.Add(e.Usage);
                    if (s.Format != e.Format) m.FormatChanges++;
                }
                else if (e.Usage == VertexElementUsage.Binormal && src.ContainsKey(VertexElementUsage.BinormalSign) && src.ContainsKey(VertexElementUsage.Normal) && src.ContainsKey(VertexElementUsage.Tangent))
                    m.Derived.Add(e.Usage);
                else if (e.Usage == VertexElementUsage.BinormalSign && src.ContainsKey(VertexElementUsage.Binormal) && src.ContainsKey(VertexElementUsage.Normal) && src.ContainsKey(VertexElementUsage.Tangent))
                    m.Derived.Add(e.Usage);
                else if (IsTexCoord(e.Usage) && e.Usage != VertexElementUsage.TexCoord0 && VertexCodec.Supported(e.Format))
                    m.Filled.Add(e.Usage);
                else if (e.Usage == VertexElementUsage.RadiosityTexCoord && zeroRadiosity && VertexCodec.Supported(e.Format))
                    m.Filled.Add(e.Usage);
                else if (fillColors && (e.Usage == VertexElementUsage.Color0 || e.Usage == VertexElementUsage.Color1) && VertexCodec.Supported(e.Format))
                    m.FilledColors.Add(e.Usage);
                else m.Unfillable.Add(e.Usage.ToString());
            }
            foreach (var kv in src)
            {
                if (dst.Contains(kv.Key)) continue;
                // the other half of a derived pair is not lost
                if (kv.Key == VertexElementUsage.BinormalSign && dst.Contains(VertexElementUsage.Binormal)) continue;
                if (kv.Key == VertexElementUsage.Binormal && dst.Contains(VertexElementUsage.BinormalSign)) continue;
                m.Dropped.Add(kv.Key);
                if (emptyUsages != null && emptyUsages.Contains(kv.Key)) m.DroppedEmpty.Add(kv.Key);
            }
            return m;
        }

        /// <summary>
        /// Ranks a usable match: lower is better. Non-empty source channels dropped weigh most, then channels invented (filled),
        /// then format changes; residency (the level compiled the permutation) and the same primitive type are filtered earlier.
        /// </summary>
        public static int Score(LayoutMatch m)
        {
            if (!m.Usable) return int.MaxValue;
            int lostData = m.Dropped.Count - m.DroppedEmpty.Count;
            return lostData * 1000 + m.Lossy.Count * 500 + (m.Filled.Count + m.FilledColors.Count) * 100 + m.DroppedEmpty.Count * 10 + m.FormatChanges;
        }

        /// <summary>
        /// Whether every value of the section's <paramref name="usage"/> channel survives encoding into <paramref name="target"/>
        /// (the components the source format has; a Half3/Half4 position's w is not data). Float -> Half narrows most real values,
        /// Half -> Float never does, so this is decided on the data, not on the formats.
        /// </summary>
        public static bool RoundTrips(GeoSection s, VertexElementUsage usage, VertexElementFormat target)
        {
            GeoElement e = s.Find(usage);
            if (e == null || e.Format == target) return true;
            if (!VertexCodec.Supported(target)) return false;
            int comps = Math.Min(VertexCodec.Components(e.Format), VertexCodec.Components(target));
            if (usage == VertexElementUsage.Pos) comps = Math.Min(comps, 3);
            var tmp = new byte[16];
            for (int v = 0; v < s.VertexCount; v++)
            {
                float[] x = VertexCodec.Decode(s.Vertices, v * s.Stride + e.Offset, e.Format);
                Array.Clear(tmp, 0, tmp.Length);
                VertexCodec.Encode(tmp, 0, target, x);
                float[] y = VertexCodec.Decode(tmp, 0, target);
                for (int c = 0; c < comps; c++) if (x[c] != y[c] && !(float.IsNaN(x[c]) && float.IsNaN(y[c]))) return false;
            }
            return true;
        }

        /// <summary>True when the section's vertices name a part other than 0 (BoneIndices of a composite mesh): its geometry
        /// follows several part transforms, so a layout without BoneIndices would pin all of it to part 0.</summary>
        public static bool SpansParts(GeoSection s)
        {
            GeoElement bi = s.Find(VertexElementUsage.BoneIndices);
            if (bi == null || !VertexCodec.Supported(bi.Format)) return false;
            for (int v = 0; v < s.VertexCount; v++)
            {
                float[] x = VertexCodec.Decode(s.Vertices, v * s.Stride + bi.Offset, bi.Format);
                for (int k = 0; k < x.Length; k++) if (x[k] != 0f) return true;
            }
            return false;
        }

        /// <summary>Source channels that carry no data: TexCoord1..7 constant at (0,1) or (0,0) over the whole section.</summary>
        public static HashSet<VertexElementUsage> EmptyChannels(GeoSection s)
        {
            var empty = new HashSet<VertexElementUsage>();
            if (s.VertexCount == 0) return empty;
            foreach (GeoElement e in s.Elements)
            {
                if (!IsTexCoord(e.Usage) || e.Usage == VertexElementUsage.TexCoord0 || !VertexCodec.Supported(e.Format)) continue;
                float[] first = VertexCodec.Decode(s.Vertices, e.Offset, e.Format);
                if (first[0] != 0f || (first[1] != 0f && first[1] != 1f)) continue;
                bool constant = true;
                for (int v = 1; v < s.VertexCount && constant; v++)
                {
                    float[] x = VertexCodec.Decode(s.Vertices, v * s.Stride + e.Offset, e.Format);
                    if (x[0] != first[0] || x[1] != first[1]) constant = false;
                }
                if (constant) empty.Add(e.Usage);
            }
            return empty;
        }

        // ------------------------------------------------------------------ vertex encoding (pure)

        /// <summary>
        /// Encodes the source section's vertices into <paramref name="decl"/> (stream 0, <paramref name="stride"/> bytes each).
        /// Binormal = normalize(BinormalSign * cross(Tangent, Normal)), w 1; BinormalSign = sign(dot(Binormal, cross(Tangent,
        /// Normal))) -- the convention both games store (measured on shipped kit meshes, tools/binormal_convention.py). Filled
        /// TexCoords get (0,1), the value the source pipeline writes for a UV set a mesh does not have.
        /// </summary>
        public static byte[] EncodeVertices(GeoSection s, IList<GeometryDeclarationDesc.Element> decl, int stride)
        {
            var src = new Dictionary<VertexElementUsage, GeoElement>();
            foreach (GeoElement e in s.Elements) if (e.Stream == 0 && !src.ContainsKey(e.Usage)) src[e.Usage] = e;
            var outb = new byte[(long)s.VertexCount * stride];
            GeoElement eN, eT, eS, eB;
            src.TryGetValue(VertexElementUsage.Normal, out eN); src.TryGetValue(VertexElementUsage.Tangent, out eT);
            src.TryGetValue(VertexElementUsage.BinormalSign, out eS); src.TryGetValue(VertexElementUsage.Binormal, out eB);
            for (int v = 0; v < s.VertexCount; v++)
            {
                int sb = v * s.Stride, db = v * stride;
                foreach (var e in decl)
                {
                    if (e.Usage == VertexElementUsage.Unknown || e.StreamIndex != 0) continue;
                    if (e.Offset + e.Size > stride) throw new InvalidDataException("element " + e.Usage + " at " + e.Offset + " exceeds the stride " + stride);
                    GeoElement se;
                    float[] val;
                    if (src.TryGetValue(e.Usage, out se)) val = VertexCodec.Decode(s.Vertices, sb + se.Offset, se.Format);
                    else if (e.Usage == VertexElementUsage.Binormal && eS != null && eN != null && eT != null)
                    {
                        float sign = VertexCodec.Decode(s.Vertices, sb + eS.Offset, eS.Format)[0] < 0 ? -1f : 1f;
                        float[] n = VertexCodec.Decode(s.Vertices, sb + eN.Offset, eN.Format), t = VertexCodec.Decode(s.Vertices, sb + eT.Offset, eT.Format);
                        float bx = t[1] * n[2] - t[2] * n[1], by = t[2] * n[0] - t[0] * n[2], bz = t[0] * n[1] - t[1] * n[0];
                        float len = (float)Math.Sqrt(bx * bx + by * by + bz * bz);
                        if (len > 1e-12f) { bx /= len; by /= len; bz /= len; }
                        val = new[] { sign * bx, sign * by, sign * bz, 1f };
                    }
                    else if (e.Usage == VertexElementUsage.BinormalSign && eB != null && eN != null && eT != null)
                    {
                        float[] b = VertexCodec.Decode(s.Vertices, sb + eB.Offset, eB.Format), n = VertexCodec.Decode(s.Vertices, sb + eN.Offset, eN.Format), t = VertexCodec.Decode(s.Vertices, sb + eT.Offset, eT.Format);
                        float cx = t[1] * n[2] - t[2] * n[1], cy = t[2] * n[0] - t[0] * n[2], cz = t[0] * n[1] - t[1] * n[0];
                        float d = b[0] * cx + b[1] * cy + b[2] * cz;
                        val = new[] { d < 0 ? -1f : 1f, 0f, 0f, 0f };
                    }
                    else if (IsTexCoord(e.Usage) && e.Usage != VertexElementUsage.TexCoord0) val = new[] { 0f, 1f, 0f, 0f };
                    else if (e.Usage == VertexElementUsage.RadiosityTexCoord) val = new[] { 0f, 0f, 0f, 0f };
                    else if (e.Usage == VertexElementUsage.Color0) val = new[] { 1f, 1f, 1f, 1f };
                    else if (e.Usage == VertexElementUsage.Color1) val = new[] { 0f, 0f, 0f, 0f };
                    else throw new InvalidDataException("no source data for " + e.Usage);
                    VertexCodec.Encode(outb, db + e.Offset, e.Format, val);
                }
            }
            return outb;
        }

        /// <summary>
        /// The vertex layout as an RVM database's Dx11InputElement block describes it: "offset:bytes" per input element, stream 0.
        /// The database packs a Half3 position with the following Half BinormalSign into one 8-byte element and every pair of
        /// adjacent Half2 channels into one Half4 (measured on Hoth_01: TexCoord0+RadiosityTexCoord at 40 is one H4@40).
        /// A (surface, signature) pair present in a loaded database is a permutation the section can be drawn with.
        /// </summary>
        public static string RvmSignature(IList<GeometryDeclarationDesc.Element> els)
        {
            var list = els.Where(e => e.Usage != VertexElementUsage.Unknown && e.StreamIndex == 0).OrderBy(e => e.Offset).ToList();
            var parts = new List<string>();
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                int size = e.Size;
                if (i + 1 < list.Count)
                {
                    var n = list[i + 1];
                    bool posSign = e.Format == VertexElementFormat.Half3 && n.Format == VertexElementFormat.Half && n.Offset == e.Offset + 6;
                    bool halfPair = e.Format == VertexElementFormat.Half2 && n.Format == VertexElementFormat.Half2 && n.Offset == e.Offset + 4;
                    if (posSign || halfPair) { size = 8; i++; }
                }
                parts.Add(e.Offset + ":" + size);
            }
            return string.Join(",", parts);
        }

        /// <summary>Elements of a declaration, as a list (Unknown padding entries dropped).</summary>
        public static List<GeometryDeclarationDesc.Element> Elements(GeometryDeclarationDesc decl)
        {
            var l = new List<GeometryDeclarationDesc.Element>();
            if (decl.Elements == null) return l;
            for (int i = 0; i < decl.Elements.Length && i < decl.ElementCount; i++) if (decl.Elements[i].Usage != VertexElementUsage.Unknown) l.Add(decl.Elements[i]);
            return l;
        }

        // ------------------------------------------------------------------ verify

        /// <summary>
        /// Compares a ported mesh (read back as a GeoMesh) with the source dump: same LOD/section structure, names, materials,
        /// primitive types and counts, identical indices, and every source channel the target declares equal after decoding
        /// (bit exact when the format is unchanged; within half precision when the target narrows). Binormal targets are checked
        /// against BinormalSign*cross(T,N). Returns a list of discrepancies (empty = exact).
        /// </summary>
        public static List<string> Verify(GeoMesh source, GeoMesh target, out int checkedValues)
        {
            var issues = new List<string>();
            checkedValues = 0;
            if (source.Lods.Count != target.Lods.Count) { issues.Add("LOD count " + target.Lods.Count + " != " + source.Lods.Count); return issues; }
            for (int l = 0; l < source.Lods.Count; l++)
            {
                GeoLod a = source.Lods[l], b = target.Lods[l];
                if (a.Sections.Count != b.Sections.Count) { issues.Add("lod" + l + ": " + b.Sections.Count + " sections, source " + a.Sections.Count); continue; }
                for (int c = 0; c < Math.Min(a.Categories.Count, b.Categories.Count); c++)
                    if (!a.Categories[c].OrderBy(x => x).SequenceEqual(b.Categories[c].OrderBy(x => x))) issues.Add("lod" + l + " category " + c + " differs");
                for (int k = 0; k < a.Sections.Count; k++)
                {
                    GeoSection s = a.Sections[k], t = b.Sections[k];
                    string at = "lod" + l + "/sec" + k;
                    if (s.Name != t.Name) issues.Add(at + " name '" + t.Name + "' != '" + s.Name + "'");
                    if (s.MaterialId != t.MaterialId) issues.Add(at + " material " + t.MaterialId + " != " + s.MaterialId);
                    if (s.Prim != t.Prim) issues.Add(at + " primitive " + t.Prim + " != " + s.Prim);
                    if (s.VertexCount != t.VertexCount || s.PrimitiveCount != t.PrimitiveCount) { issues.Add(at + " counts " + t.VertexCount + "/" + t.PrimitiveCount + " != " + s.VertexCount + "/" + s.PrimitiveCount); continue; }
                    if (!s.Indices.SequenceEqual(t.Indices)) issues.Add(at + " indices differ");
                    foreach (GeoElement te in t.Elements)
                    {
                        GeoElement se = s.Find(te.Usage);
                        bool derivedBinormal = se == null && te.Usage == VertexElementUsage.Binormal && s.Find(VertexElementUsage.BinormalSign) != null;
                        if (se == null && !derivedBinormal) continue;
                        int comps = te.Usage == VertexElementUsage.Pos ? 3 : Math.Min(VertexCodec.Components(te.Format), se != null ? VertexCodec.Components(se.Format) : 3);
                        if (te.Usage == VertexElementUsage.BinormalSign) comps = 1;
                        int bad = 0; float worst = 0;
                        for (int v = 0; v < s.VertexCount; v++)
                        {
                            float[] want;
                            if (derivedBinormal)
                            {
                                GeoElement eS = s.Find(VertexElementUsage.BinormalSign), eN = s.Find(VertexElementUsage.Normal), eT = s.Find(VertexElementUsage.Tangent);
                                float sign = VertexCodec.Decode(s.Vertices, v * s.Stride + eS.Offset, eS.Format)[0] < 0 ? -1f : 1f;
                                float[] n = VertexCodec.Decode(s.Vertices, v * s.Stride + eN.Offset, eN.Format), tt = VertexCodec.Decode(s.Vertices, v * s.Stride + eT.Offset, eT.Format);
                                float bx = tt[1] * n[2] - tt[2] * n[1], by = tt[2] * n[0] - tt[0] * n[2], bz = tt[0] * n[1] - tt[1] * n[0];
                                float len = (float)Math.Sqrt(bx * bx + by * by + bz * bz); if (len > 1e-12f) { bx /= len; by /= len; bz /= len; }
                                want = new[] { sign * bx, sign * by, sign * bz, 1f };
                            }
                            else want = VertexCodec.Decode(s.Vertices, v * s.Stride + se.Offset, se.Format);
                            float[] got = VertexCodec.Decode(t.Vertices, v * t.Stride + te.Offset, te.Format);
                            for (int i = 0; i < comps; i++)
                            {
                                checkedValues++;
                                float d = Math.Abs(got[i] - want[i]);
                                // bit exact for every carried channel; only a derived Binormal (a cross product stored as halves)
                                // is compared within half precision
                                float tol = se != null ? 0f : Math.Max(1e-3f, Math.Abs(want[i]) * 1e-3f);
                                if (d > tol) { bad++; if (d > worst) worst = d; }
                            }
                        }
                        if (bad > 0) issues.Add(at + " " + te.Usage + ": " + bad + " value(s) differ (worst " + worst + ")");
                    }
                }
            }
            return issues;
        }

        // ------------------------------------------------------------------ apply

        public sealed class ApplyReport
        {
            public int Lods, Sections, Vertices, Indices, Parts;
            public bool PartsFromSource;   // a composite target took the dump's parts (else the template's stayed)
            public List<string> Notes = new List<string>();
        }

        /// <summary>
        /// Rebuilds <paramref name="mesh"/> (a clone with the same LOD count as <paramref name="geo"/>) from the dump and the
        /// per-section donor choices (<paramref name="choices"/>[lod][section]). <paramref name="holderFor"/> gives each material
        /// index the holder its sections share (a donor holder copy the caller already filled with the source's parameters).
        /// </summary>
        public static string Apply(EbxAssetEntry mesh, GeoMesh geo, List<List<SectionChoice>> choices, Dictionary<int, ShaderStaticParamDbBlock> holderFor, out ApplyReport report)
        {
            report = new ApplyReport();
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(mesh); } catch (Exception ex) { return "mesh load failed: " + ex.Message; }
            ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
            if (rid == 0) return "mesh has no MeshSetResource.";
            ResAssetEntry re = App.AssetManager.GetResEntry(rid);
            MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
            if (ms == null) return "could not load the MeshSet.";
            if (ms.Lods.Count != geo.Lods.Count) return "mesh has " + ms.Lods.Count + " LOD(s), the dump " + geo.Lods.Count + " (trim the clone first)";
            ResAssetEntry nb = App.AssetManager.GetResEntry(mesh.Name.ToLowerInvariant() + "_mesh/blocks");
            if (nb == null) return "mesh has no shader block depot (" + mesh.Name.ToLowerInvariant() + "_mesh/blocks)";
            ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(nb);
            FieldInfo resField = typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo entField = typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic);
            var oldEntries = (List<ShaderBlockEntry>)entField.GetValue(depot);
            if (oldEntries.Count < ms.Lods.Count) return "depot has " + oldEntries.Count + " LOD entr(y/ies) for " + ms.Lods.Count + " LOD(s)";
            Guid meshGuid;
            try { meshGuid = (Guid)((dynamic)asset.RootObject).GetInstanceGuid().ExportedGuid; } catch (Exception ex) { return "no mesh instance guid: " + ex.Message; }

            FieldInfo fName = typeof(MeshSetSection).GetField("m_materialName", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo fMat = typeof(MeshSetSection).GetField("m_materialId", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo fIdx = typeof(MeshSetSection).GetField("m_sectionIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo fLm = typeof(MeshSetSection).GetField("m_lightMapUvMappingIndex", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo fRatios = typeof(MeshSetSection).GetField("m_texCoordRatios", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fName == null || fMat == null || fIdx == null) return "MeshSetSection layout not recognised (m_materialName/m_materialId/m_sectionIndex)";

            var newResources = new List<ShaderBlockResource>();
            var newEntries = new List<ShaderBlockEntry>();
            // holders first (children, then the holder), exactly once each
            var placed = new HashSet<ShaderBlockResource>();
            Action<ShaderBlockResource> place = null;
            place = r =>
            {
                if (r == null || placed.Contains(r)) return;
                var sp = r as ShaderStaticParamDbBlock;
                if (sp != null) foreach (ShaderBlockResource c in sp.Resources) place(c);
                placed.Add(r); newResources.Add(r);
            };
            foreach (var kv in holderFor.OrderBy(k => k.Key)) place(kv.Value);

            int sectionIndex = 0;
            for (int l = 0; l < ms.Lods.Count; l++)
            {
                MeshSetLod lod = ms.Lods[l];
                GeoLod gl = geo.Lods[l];
                if (choices.Count <= l || choices[l].Count != gl.Sections.Count) return "LOD " + l + ": " + gl.Sections.Count + " section(s) but " + (choices.Count > l ? choices[l].Count : 0) + " donor choice(s)";
                var sections = new List<MeshSetSection>();
                var vbuf = new MemoryStream();
                var sectionIdx = new List<uint[]>();
                var startIndex = new List<uint>();
                bool large = false;
                foreach (GeoSection gs in gl.Sections) if (gs.VertexCount > 65535) large = true;
                uint runningIndex = 0;
                for (int k = 0; k < gl.Sections.Count; k++)
                {
                    GeoSection gs = gl.Sections[k];
                    SectionChoice ch = choices[l][k];
                    MeshSetSection sec = CloneSection(ch.DonorSection);
                    GeometryDeclarationDesc decl0 = sec.GeometryDeclDesc[0];
                    int stride = decl0.Streams[0].VertexStride;
                    byte[] verts;
                    try { verts = EncodeVertices(gs, Elements(decl0), stride); }
                    catch (Exception ex) { return "LOD " + l + " section " + k + ": " + ex.Message; }
                    fName.SetValue(sec, gs.Name ?? "");
                    fMat.SetValue(sec, gs.MaterialId);
                    fIdx.SetValue(sec, sectionIndex++);
                    if (fLm != null) fLm.SetValue(sec, gs.LightMapUvMappingIndex);
                    if (fRatios != null)
                    {
                        var ratios = new List<float>();
                        var have = new HashSet<VertexElementUsage>(Elements(decl0).Select(e => e.Usage));
                        for (int i = 0; i < 6; i++)
                            ratios.Add(have.Contains((VertexElementUsage)((int)VertexElementUsage.TexCoord0 + i)) && gs.TexCoordRatios[i] > 0 ? gs.TexCoordRatios[i] : 1f);
                        fRatios.SetValue(sec, ratios);
                    }
                    sec.VertexOffset = (uint)vbuf.Length;
                    sec.VertexCount = (uint)gs.VertexCount;
                    sec.PrimitiveCount = (uint)gs.PrimitiveCount;
                    vbuf.Write(verts, 0, verts.Length);
                    // shipped 16-bit index buffers start each section on an even index (4-byte aligned)
                    if (!large && (runningIndex & 1) != 0) runningIndex++;
                    sec.StartIndex = runningIndex;
                    startIndex.Add(runningIndex);
                    sectionIdx.Add(gs.Indices);
                    runningIndex += (uint)gs.Indices.Length;
                    sections.Add(sec);
                    report.Vertices += gs.VertexCount; report.Indices += gs.Indices.Length;
                }
                // buffers: vertices, pad 16, indices, pad 16 (the importer's layout)
                long vbSize = (vbuf.Length + 15) & ~15L;
                int unit = large ? 4 : 2;
                long ibSize = (((long)runningIndex * unit) + 15) & ~15L;
                var all = new byte[vbSize + ibSize];
                Buffer.BlockCopy(vbuf.ToArray(), 0, all, 0, (int)vbuf.Length);
                for (int k = 0; k < sectionIdx.Count; k++)
                {
                    uint[] idx = sectionIdx[k];
                    long at = vbSize + (long)startIndex[k] * unit;
                    for (int i = 0; i < idx.Length; i++)
                    {
                        if (large) Buffer.BlockCopy(BitConverter.GetBytes(idx[i]), 0, all, (int)(at + (long)i * 4), 4);
                        else { if (idx[i] > 65535) return "LOD " + l + " section " + k + ": index " + idx[i] + " needs 32-bit indices"; all[at + i * 2] = (byte)idx[i]; all[at + i * 2 + 1] = (byte)(idx[i] >> 8); }
                    }
                }
                lod.Sections.Clear();
                lod.Sections.AddRange(sections);
                try { lod.SectionCount = sections.Count; } catch { }
                lod.VertexBufferSize = (uint)vbSize;
                lod.IndexBufferSize = (uint)ibSize;
                lod.SetIndexBufferFormatSize(unit);
                List<List<byte>> cats = lod.CategorySubsetIndices;
                for (int c = 0; c < cats.Count; c++)
                {
                    cats[c].Clear();
                    if (c < gl.Categories.Count) foreach (byte b in gl.Categories[c]) if (b < sections.Count) cats[c].Add(b);
                }
                if (lod.ChunkId != Guid.Empty)
                {
                    if (!App.AssetManager.ModifyChunk(lod.ChunkId, all)) return "ModifyChunk failed for LOD " + l;
                }
                else lod.SetInlineData(all);

                // depot: per section a mesh-parameter block + section block, then the LOD entry and its variation entry
                ShaderBlockEntry oldEntry = oldEntries[l];
                var entry = new ShaderBlockEntry { Hash = oldEntry.Hash, IsModified = true };
                var variation = new ShaderBlockMeshVariationEntry { Hash = oldEntry.Hash, IsModified = true };
                for (int k = 0; k < sections.Count; k++)
                {
                    SectionChoice ch = choices[l][k];
                    MeshSetSection sec = sections[k];
                    var mp = new MeshParamDbBlock { MeshAssetGuid = meshGuid, LodIndex = l, Hash = Derive(meshGuid, l, k, 1), IsModified = true };
                    foreach (ParameterEntry p in ch.DonorMeshParams.Parameters)
                        using (var r = new NativeReader(new MemoryStream(p.ToBytes()))) mp.Parameters.Add(new ParameterEntry(r));
                    mp.SetParameterValue("!primitiveCount", sec.PrimitiveCount);
                    mp.SetParameterValue("!vertexStreamOffsets0", sec.VertexOffset);
                    mp.SetParameterValue("!startIndex", sec.StartIndex);
                    ShaderStaticParamDbBlock holder;
                    if (!holderFor.TryGetValue(sec.MaterialId, out holder)) return "no holder for material " + sec.MaterialId;
                    var sb = new ShaderStaticParamDbBlock { Hash = Derive(meshGuid, l, k, 2), IsModified = true };
                    sb.Resources.Add(mp); sb.Resources.Add(holder);
                    newResources.Add(mp); newResources.Add(sb);
                    entry.Resources.Add(sb);
                    variation.RvmShaderRefGuids.Add(ch.RvmRef); variation.RvmShaderRefInts.Add(ch.RvmInt);
                }
                newResources.Add(entry); newResources.Add(variation);
                newEntries.Add(entry);
                report.Sections += sections.Count;
            }
            // A composite target draws each vertex by its part: carry the source's parts (the member supplies one physics part
            // per mesh part) and give every section the list of parts its vertices use. A dump without parts (written before
            // the PARTS1 trailer) keeps the template's, and the report says so.
            if (ms.Type.ToString().EndsWith("Composite", StringComparison.OrdinalIgnoreCase))
            {
                FieldInfo fpt = typeof(MeshSet).GetField("m_partTransforms", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo fpb = typeof(MeshSet).GetField("m_partBoundingBoxes", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fpt == null || fpb == null) return "MeshSet part fields not recognised (m_partTransforms/m_partBoundingBoxes)";
                List<LinearTransform> pts; List<AxisAlignedBox> pbs;
                if (geo.PartBoxes.Count > 0)
                {
                    pts = new List<LinearTransform>(geo.PartTransforms);
                    pbs = new List<AxisAlignedBox>(geo.PartBoxes);
                    while (pts.Count < pbs.Count) pts.Add(LinearTransform.Identity);
                    fpt.SetValue(ms, pts); fpb.SetValue(ms, pbs);
                    // the header's part count sizes the part lists when the MeshSet is read back (a composite writes
                    // m_bonePartCount, then m_boneCount): left at the template's, the port came back with the template's parts
                    FieldInfo fbpc = typeof(MeshSet).GetField("m_bonePartCount", BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo fbc = typeof(MeshSet).GetField("m_boneCount", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fbpc == null || fbc == null) return "MeshSet part count fields not recognised (m_bonePartCount/m_boneCount)";
                    uint oldParts = (uint)fbpc.GetValue(ms), oldBones = (uint)fbc.GetValue(ms);
                    fbpc.SetValue(ms, (uint)pbs.Count);
                    if (oldBones == oldParts) fbc.SetValue(ms, (uint)pbs.Count);   // DICE's composites: one bone per part
                    report.PartsFromSource = true;
                }
                else { pts = (List<LinearTransform>)fpt.GetValue(ms); pbs = (List<AxisAlignedBox>)fpb.GetValue(ms); }
                report.Parts = pbs.Count;
                for (int l = 0; l < ms.Lods.Count; l++)
                {
                    var perSection = new List<List<int>>();
                    foreach (GeoSection gs in geo.Lods[l].Sections)
                    {
                        List<int> used = GeoMesh.SectionParts(gs);
                        foreach (int pi in used) if (pi >= pbs.Count) return "LOD " + l + " section '" + gs.Name + "' uses part " + pi + " but the mesh has " + pbs.Count + " part(s)";
                        perSection.Add(used.Count > 0 ? used : new List<int> { 0 });
                    }
                    ms.Lods[l].SetParts(pts, pbs, perSection);
                }
            }
            for (int i = 0; i < newResources.Count; i++) newResources[i].Index = i;
            resField.SetValue(depot, newResources);
            entField.SetValue(depot, newEntries);
            string partNote = GrowCompositeParts(ms, geo);
            if (partNote != null) report.Notes.Add(partNote);
            App.AssetManager.ModifyRes(rid, ms);
            App.AssetManager.ModifyRes(nb.ResRid, depot);
            report.Lods = ms.Lods.Count;
            return null;
        }

        /// <summary>The source's part index of each vertex of a section (BoneIndices[0]: 2015 composites store the part itself),
        /// or null when the section has no BoneIndices.</summary>
        internal static List<int> SectionParts(GeoSection s)
        {
            GeoElement bi = s.Find(VertexElementUsage.BoneIndices);
            if (bi == null) return null;
            var parts = new SortedSet<int>();
            for (int v = 0; v < s.VertexCount; v++)
                parts.Add(PartOf(s, v, bi));
            return parts.ToList();
        }

        private static int PartOf(GeoSection s, int v, GeoElement bi)
        {
            int at = v * s.Stride + bi.Offset;
            return bi.Format == VertexElementFormat.UByte4 ? s.Vertices[at] : (int)Math.Round(VertexCodec.Decode(s.Vertices, at, bi.Format)[0]);
        }

        /// <summary>
        /// A composite port takes its part tables from the template, so a source with more parts than the template left the
        /// extra parts undefined: 2015's BoxMedium_01 (Metal, Glass, Glass_Damage, M_Ruin = 4 parts, 4 physics parts per group
        /// instance) on BFII's Box_M_01_A (3) -- import-group refuses such a member (game+0x6E6ED6A). Grows the mesh's and every
        /// LOD's part transforms (identity, as the source's) and part boxes (the box of that part's LOD-0 vertices, the mesh box
        /// when none), sets the bone-part count, and rebuilds each LOD's per-section part lists from the dump.
        /// Returns a note when it grew the mesh, else null.
        /// </summary>
        internal static string GrowCompositeParts(MeshSet ms, GeoMesh geo)
        {
            if (ms == null || geo == null || ms.Type != MeshType.MeshType_Composite) return null;
            int needed = 0;
            foreach (GeoLod gl in geo.Lods)
                foreach (GeoSection gs in gl.Sections)
                {
                    List<int> ps = SectionParts(gs);
                    if (ps != null && ps.Count > 0) needed = Math.Max(needed, ps.Max() + 1);
                }
            FieldInfo mt = typeof(MeshSet).GetField("m_partTransforms", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo mb = typeof(MeshSet).GetField("m_partBoundingBoxes", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo mc = typeof(MeshSet).GetField("m_bonePartCount", BindingFlags.Instance | BindingFlags.NonPublic);
            var meshT = mt != null ? mt.GetValue(ms) as List<LinearTransform> : null;
            var meshB = mb != null ? mb.GetValue(ms) as List<AxisAlignedBox> : null;
            if (meshT == null || meshB == null) return null;
            int have = Math.Max(meshT.Count, meshB.Count);
            // exactly the source's parts: a template with more parts than the source is as wrong as one with fewer (the
            // group's physics parts per instance must equal the mesh's parts -- 2015 BoxMedium_01_B: 2 parts, on a 3-part
            // template)
            if (needed == 0 || needed == have) return null;
            AxisAlignedBox whole = ms.BoundingBox;
            var boxes = new List<AxisAlignedBox>();
            for (int p = 0; p < needed; p++) boxes.Add(PartBox(geo.Lods.Count > 0 ? geo.Lods[0] : null, p, whole));
            if (meshT.Count > needed) meshT.RemoveRange(needed, meshT.Count - needed);
            if (meshB.Count > needed) meshB.RemoveRange(needed, meshB.Count - needed);
            while (meshT.Count < needed) meshT.Add(LinearTransform.Identity);
            while (meshB.Count < needed) meshB.Add(boxes[meshB.Count]);
            if (mc != null) mc.SetValue(ms, (uint)needed);
            for (int l = 0; l < ms.Lods.Count && l < geo.Lods.Count; l++)
            {
                MeshSetLod lod = ms.Lods[l];
                var lt = new List<LinearTransform>(lod.PartTransforms);
                var lb = new List<AxisAlignedBox>(lod.PartBoundingBoxes);
                if (lt.Count > needed) lt.RemoveRange(needed, lt.Count - needed);
                if (lb.Count > needed) lb.RemoveRange(needed, lb.Count - needed);
                while (lt.Count < needed) lt.Add(LinearTransform.Identity);
                while (lb.Count < needed) lb.Add(boxes[lb.Count]);
                var idx = new List<List<int>>();
                foreach (GeoSection gs in geo.Lods[l].Sections) idx.Add(SectionParts(gs) ?? Enumerable.Range(0, needed).ToList());
                lod.SetParts(lt, lb, idx);
            }
            return "composite part tables set " + have + " -> " + needed + " (the source's parts; identity transforms, boxes from LOD 0)";
        }

        private static AxisAlignedBox PartBox(GeoLod lod, int part, AxisAlignedBox fallback)
        {
            if (lod == null) return fallback;
            float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
            bool any = false;
            foreach (GeoSection s in lod.Sections)
            {
                GeoElement bi = s.Find(VertexElementUsage.BoneIndices), pos = s.Find(VertexElementUsage.Pos);
                if (bi == null || pos == null) continue;
                for (int v = 0; v < s.VertexCount; v++)
                {
                    if (PartOf(s, v, bi) != part) continue;
                    float[] xyz = VertexCodec.Decode(s.Vertices, v * s.Stride + pos.Offset, pos.Format);
                    for (int c = 0; c < 3; c++) { lo[c] = Math.Min(lo[c], xyz[c]); hi[c] = Math.Max(hi[c], xyz[c]); }
                    any = true;
                }
            }
            if (!any) return fallback;
            var box = new AxisAlignedBox();
            box.min = new Vec3 { x = lo[0], y = lo[1], z = lo[2] };
            box.max = new Vec3 { x = hi[0], y = hi[1], z = hi[2] };
            return box;
        }

        // ------------------------------------------------------------------ material fields in the depot

        public const uint TypeBoolean = 2520298017u, TypeFloat32 = 2134484916u, TypeInt64 = 214528500u;

        /// <summary>
        /// A holder entry that carries one of the MeshMaterial's own FIELDS (the tessellation constants, the enum of the
        /// tessellation type, CastShadow-like flags) rather than an EBX shader-parameter override: every Float32, Int64 or
        /// Boolean entry (EBX VectorParameters are Vec, textures ITexture). Stripping these as "donor overrides" left the
        /// Hoth 2015 rock-wall ports' tessellated sections with no triangle size / max distance, and the GPU faulted a few
        /// seconds after spawning near them (DXGI_ERROR_DEVICE_REMOVED; HOTH_HX_A1b).
        /// </summary>
        public static bool IsMaterialFieldEntry(uint typeHash)
        {
            return typeHash == TypeFloat32 || typeHash == TypeInt64 || typeHash == TypeBoolean;
        }

        /// <summary>MeshMaterial field -> depot parameter NameHash, measured on BFII's own tessellated meshes (EBX field value =
        /// depot value, ArcticBase_PileWall_Medium_01 and ArcticBase_RockWall_Large_07 mat 0, every field distinct between them).
        /// The two Boolean keys 0x025960AC / 0x06C84F78 are not separable from CastShadow / SmoothEdgeVertices on that sample and
        /// keep the donor's values; 0x5943AD36 (Int64) is the tessellation-type enum, the donor's by construction.</summary>
        public static readonly Dictionary<string, uint> MaterialFieldKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            { "TessellationTriangleSize", 0x0DCC3660u }, { "TessellationMaxDistance", 0x5448AEFAu },
            { "BackFaceCullEpsilon", 0x44772713u }, { "ShapeFactor", 0xEC9A636Fu },
            { "DisplacementScale", 0x31BE4382u }, { "DisplacementBias", 0x018B0B63u },
        };

        /// <summary>Writes the source material's own field values into the holder entries that carry them (entries only; a key
        /// the holder lacks is not invented). Returns how many were set.</summary>
        public static int ApplyMaterialFields(ShaderStaticParamDbBlock holder, IDictionary<string, string> fields)
        {
            if (holder == null || fields == null) return 0;
            int set = 0;
            foreach (var kv in MaterialFieldKeys)
            {
                string v; float f;
                if (!fields.TryGetValue(kv.Key, out v) || !float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f)) continue;
                foreach (ShaderBlockResource c in holder.Resources)
                {
                    var pb = c as ShaderPersistentParamDbBlock;
                    if (pb == null) continue;
                    foreach (ParameterEntry pe in pb.Parameters)
                        if (pe.NameHash == kv.Value && pe.TypeHash == TypeFloat32) { pe.Value = BitConverter.GetBytes(f); pb.IsModified = true; set++; }
                }
            }
            return set;
        }

        /// <summary>The tessellation-type enum's depot key (Int64). A TrianglePatch section whose holder lacks it draws plainly beyond
        /// its TessellationMaxDistance and NOTHING inside it (Hoth 2015 N7: the ported Corridor_01 kit floors vanished within
        /// ~10 m; the enum was stripped as a donor override).</summary>
        public const uint TessellationTypeKey = 0x5943AD36;

        /// <summary>For a mesh with a shader block depot: how many TrianglePatch sections (all LODs) have a holder without the
        /// tessellation-type enum, of how many TrianglePatch sections. False when the mesh or its depot cannot be read.</summary>
        public static bool PatchSectionsWithoutTessType(EbxAssetEntry mesh, out int missing, out int patches)
        {
            missing = 0; patches = 0;
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(mesh);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return false;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                ResAssetEntry nb = App.AssetManager.GetResEntry(mesh.Name.ToLowerInvariant() + "_mesh/blocks");
                if (ms == null || nb == null) return false;
                ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(nb);
                var entries = (List<ShaderBlockEntry>)typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot);
                for (int l = 0; l < ms.Lods.Count && l < entries.Count; l++)
                    for (int s = 0; s < ms.Lods[l].Sections.Count && s < entries[l].Resources.Count; s++)
                    {
                        if (ms.Lods[l].Sections[s].PrimitiveType != PrimitiveType.PrimitiveType_TrianglePatch) continue;
                        patches++;
                        var sec = entries[l].Resources[s] as ShaderStaticParamDbBlock;
                        var holder = sec != null && sec.Resources.Count > 1 ? sec.Resources[1] as ShaderStaticParamDbBlock : null;
                        bool has = false;
                        if (holder != null)
                            foreach (ShaderBlockResource c in holder.Resources)
                            {
                                var pb = c as ShaderPersistentParamDbBlock;
                                if (pb != null) foreach (ParameterEntry pe in pb.Parameters) if (pe.NameHash == TessellationTypeKey) { has = true; break; }
                                if (has) break;
                            }
                        if (!has) missing++;
                    }
                return true;
            }
            catch { return false; }
        }

        /// <summary>For a mesh whose EnlightenType is EnlightenType_Static: how many sections drawn in the opaque pass (subset category 0)
        /// have no RadiosityTexCoord in their first declaration, of how many opaque sections (all LODs). A lightmapped mesh without
        /// lightmap uvs draws NOTHING in BFII once the bake charts it (Hoth 2015 ContainerHuge_01, 71 placements: invisible as
        /// Static, drawn as Dynamic in the same build). False when the mesh is not static or cannot be read.</summary>
        public static bool StaticWithoutLightmapUvs(EbxAssetEntry mesh, out int missing, out int opaque)
        {
            missing = 0; opaque = 0;
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(mesh);
                dynamic root = asset.RootObject;
                if (!((object)root.EnlightenType).ToString().EndsWith("Static", StringComparison.OrdinalIgnoreCase)) return false;
                ulong rid = (ulong)root.MeshSetResource;
                if (rid == 0) return false;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                if (ms == null) return false;
                foreach (MeshSetLod lod in ms.Lods)
                {
                    if (lod.CategorySubsetIndices == null || lod.CategorySubsetIndices.Count == 0) continue;
                    foreach (byte si in lod.CategorySubsetIndices[0])
                    {
                        if (si >= lod.Sections.Count) continue;
                        MeshSetSection sec = lod.Sections[si];
                        opaque++;
                        bool has = false;
                        if (sec.GeometryDeclDesc != null && sec.GeometryDeclDesc.Length > 0 && sec.GeometryDeclDesc[0].Elements != null)
                            for (int i = 0; i < sec.GeometryDeclDesc[0].ElementCount && i < sec.GeometryDeclDesc[0].Elements.Length; i++)
                                if (sec.GeometryDeclDesc[0].Elements[i].Usage == VertexElementUsage.RadiosityTexCoord) { has = true; break; }
                        if (!has) missing++;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>A copy of a donor holder (the section's parameter/texture holder) with its own hashes; a zero-hash (empty)
        /// child is kept shared, as shipped depots share their empty parameter block.</summary>
        public static ShaderStaticParamDbBlock CopyHolder(ShaderStaticParamDbBlock donor, Guid meshGuid, int material, Dictionary<ulong, ShaderBlockResource> sharedEmpty)
        {
            var h = new ShaderStaticParamDbBlock { Hash = Salt(donor.Hash, meshGuid, material, 3), IsModified = true };
            int child = 0;
            foreach (ShaderBlockResource c in donor.Resources)
            {
                var p = c as ShaderPersistentParamDbBlock;
                if (p == null) throw new InvalidDataException("holder child " + c.GetType().Name + " is not a parameter block");
                if (p.Hash == 0 && p.Parameters.Count == 0)
                {
                    ShaderBlockResource e;
                    if (!sharedEmpty.TryGetValue(0, out e)) { e = new ShaderPersistentParamDbBlock { Hash = 0, IsModified = true }; sharedEmpty[0] = e; }
                    h.Resources.Add(e); child++; continue;
                }
                var np = new ShaderPersistentParamDbBlock { Hash = Salt(p.Hash, meshGuid, material, 4 + child++), IsModified = true };
                foreach (ParameterEntry pe in p.Parameters)
                    using (var r = new NativeReader(new MemoryStream(pe.ToBytes()))) np.Parameters.Add(new ParameterEntry(r));
                h.Resources.Add(np);
            }
            return h;
        }

        private static ulong Derive(Guid mesh, int lod, int section, int kind)
        {
            using (var w = new NativeWriter(new MemoryStream()))
            {
                w.Write(mesh.ToByteArray()); w.Write(lod); w.Write(section); w.Write(kind);
                return Frosty.Hash.CityHash.Hash64(w.ToByteArray());
            }
        }

        private static ulong Salt(ulong hash, Guid mesh, int material, int kind)
        {
            using (var w = new NativeWriter(new MemoryStream()))
            {
                w.Write(hash); w.Write(mesh.ToByteArray()); w.Write(material); w.Write(kind);
                return Frosty.Hash.CityHash.Hash64(w.ToByteArray());
            }
        }

        /// <summary>Field-by-field copy of a section (MeshSetSection has only an internal constructor); the declarations and
        /// per-section lists are copied, not shared.</summary>
        public static MeshSetSection CloneSection(MeshSetSection src)
        {
            Type t = typeof(MeshSetSection);
            var clone = (MeshSetSection)Activator.CreateInstance(t, nonPublic: true);
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object v = f.GetValue(src);
                if (v is List<ushort> us) v = new List<ushort>(us);
                else if (v is List<float> fs) v = new List<float>(fs);
                else if (v is byte[] ba) v = (byte[])ba.Clone();
                else if (v is GeometryDeclarationDesc[] gd)
                {
                    var copy = new GeometryDeclarationDesc[gd.Length];
                    for (int i = 0; i < gd.Length; i++)
                    {
                        copy[i] = gd[i];
                        if (gd[i].Elements != null) copy[i].Elements = (GeometryDeclarationDesc.Element[])gd[i].Elements.Clone();
                        if (gd[i].Streams != null) copy[i].Streams = (GeometryDeclarationDesc.Stream[])gd[i].Streams.Clone();
                    }
                    v = copy;
                }
                f.SetValue(clone, v);
            }
            return clone;
        }
    }
}
