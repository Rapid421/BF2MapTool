using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Adapters.Mesh;
using FrostyCLI.Core;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Commands
{
    // The planning half of the geometry port (MeshGeoPort is the writing half): for every section of the source mesh, pick
    // the donor section whose surface is the section's own material's shader, whose primitive type and subset categories
    // match, and whose vertex layout carries the most of the source's vertex data -- preferring donors of meshes the target
    // level is known to compile (the --resident list, e.g. every mesh the host level's variation database registers).
    public static class PortGeo
    {
        private static readonly Dictionary<Guid, List<SurfaceDonors.Donor>> s_donors = new Dictionary<Guid, List<SurfaceDonors.Donor>>();
        private static readonly Dictionary<string, DonorMesh> s_meshes = new Dictionary<string, DonorMesh>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> s_resident;
        private static readonly Dictionary<string, bool> s_roundTrip = new Dictionary<string, bool>();
        private static string s_residentPath;

        /// <summary>How a material's sections are drawn after the port: tessellated (TrianglePatch donors) and/or as triangle
        /// lists, and the donor MeshMaterial of its first tessellated section (its permutation keys are what the donor's
        /// RVM permutation was compiled with).</summary>
        public sealed class TessInfo
        {
            public bool Patch, List;
            public object DonorMaterial;
            public string DonorMesh;
        }

        /// <summary>From the last non-plan <see cref="Run"/>: colour-pass sections whose source carries lightmap uvs, by whether
        /// the chosen donor layout keeps them (a surface BFII compiles only without RadiosityTexCoord drops them).</summary>
        public static int LastRadiosityKept, LastRadiosityDropped;

        /// <summary>Per material index, from the last non-plan <see cref="Run"/>.</summary>
        public static readonly Dictionary<int, TessInfo> LastTessellation = new Dictionary<int, TessInfo>();

        private sealed class DonorMesh
        {
            public EbxAsset Asset;
            public MeshSet Set;
            public List<ShaderBlockEntry> Entries;
            public List<ShaderBlockResource> All;
        }

        public sealed class Options
        {
            public string ResidentPath;
            public bool AllowForeign;     // consider donors outside the resident list on equal terms
            public int MaxMeshes = 400;
            public bool PlanOnly;         // choose donors and report, change nothing
            public Dictionary<int, string> SourceTessellation;   // material -> the source's SurfaceDonors.TessKey (tessellated materials only)
            public Dictionary<int, IDictionary<string, string>> SourceFields;   // material -> the source material's fields (package manifest)

            /// <summary>The source materials' own fields, from a package manifest.</summary>
            public static Dictionary<int, IDictionary<string, string>> FieldsOf(PortManifest m)
            {
                var d = new Dictionary<int, IDictionary<string, string>>();
                if (m == null || m.Mesh == null) return d;
                foreach (PortMaterial pm in m.Mesh.Materials) if (pm.Fields != null) d[pm.Index] = pm.Fields;
                return d;
            }

            public HashSet<int> FallbackMaterials;               // materials whose source surface BFII does not have (drawn by the fallback)

            /// <summary>Materials whose resolved shader is not the source's own (by name: DICE moved shaders but kept names).</summary>
            public static HashSet<int> FallbacksOf(PortManifest m, IList<EbxAssetEntry> shaderTarget)
            {
                var f = new HashSet<int>();
                for (int i = 0; m != null && i < m.Mesh.Materials.Count && i < shaderTarget.Count; i++)
                {
                    string src = m.Mesh.Materials[i].ShaderPath;
                    if (src == null) continue;
                    string a = src.Substring(src.LastIndexOf('/') + 1), b = shaderTarget[i].Name.Substring(shaderTarget[i].Name.LastIndexOf('/') + 1);
                    if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) f.Add(i);
                }
                return f;
            }

            /// <summary>The source materials' tessellation keys, from a package manifest.</summary>
            public static Dictionary<int, string> TessellationOf(PortManifest m)
            {
                var d = new Dictionary<int, string>();
                if (m == null || m.Mesh == null) return d;
                foreach (PortMaterial pm in m.Mesh.Materials)
                {
                    string t, c, o;
                    if (pm.Fields == null || !pm.Fields.TryGetValue("TessellationType", out t)) continue;
                    pm.Fields.TryGetValue("DisplacementMapTexCoord", out c);
                    pm.Fields.TryGetValue("DisplacementObjectScale", out o);
                    string k = SurfaceDonors.TessKey(t, c, o);
                    if (k.Length > 0) d[pm.Index] = k;
                }
                return d;
            }
        }

        public static string Run(EbxAssetEntry newMesh, GeoMesh geo, IList<EbxAssetEntry> shaderTarget, Options opt, ExportLogger logger, List<string> report, List<string> plan)
        {
            if (opt.ResidentPath != null && opt.ResidentPath != s_residentPath)
            {
                s_resident = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string l in File.ReadAllLines(opt.ResidentPath)) { string t = l.Trim(); if (t.Length > 0 && !t.StartsWith("#")) s_resident.Add(t.ToLowerInvariant()); }
                s_residentPath = opt.ResidentPath;
            }
            s_roundTrip.Clear();   // keyed by section object identity: valid for this mesh only
            LastTessellation.Clear();
            LastRadiosityKept = 0; LastRadiosityDropped = 0;
            RefIndex index = IndexService.GetRefIndex();
            Func<string, List<string>> users = g => { List<string> u; return index.Refs.TryGetValue(g, out u) ? u : null; };

            Guid meshGuid = Guid.Empty;
            if (!opt.PlanOnly)
            {
                EbxAsset meshAsset = App.AssetManager.GetEbx(newMesh);
                meshGuid = (Guid)((dynamic)meshAsset.RootObject).GetInstanceGuid().ExportedGuid;
            }
            var missing = new List<string>();
            var hidden = new HashSet<int>();   // lod*10000+section: sections drawn in no pass

            var choices = new List<List<MeshGeoPort.SectionChoice>>();
            // per material, the tessellation key its tessellated sections must share: the source's, or the first donor's when no
            // donor of the surface has the source's
            var tessWant = opt.SourceTessellation != null ? new Dictionary<int, string>(opt.SourceTessellation) : new Dictionary<int, string>();
            var tessLocked = new HashSet<int>();
            var primaryFor = new Dictionary<int, MeshGeoPort.SectionChoice>();
            int foreign = 0;
            // A composite target keeps each vertex's part index (BoneIndices): the part transforms of the entity's health
            // states show and hide its intact and ruined pieces. A section spanning several parts on a layout without
            // BoneIndices would draw every piece at once (2015 TransporterPersonel_01's glow on its broken-light parts).
            bool compositeTarget = IsComposite(newMesh);
            int colourFilled = 0, partsPinned = 0;
            for (int l = 0; l < geo.Lods.Count; l++)
            {
                var row = new List<MeshGeoPort.SectionChoice>();
                GeoLod gl = geo.Lods[l];
                for (int k = 0; k < gl.Sections.Count; k++)
                {
                    GeoSection gs = gl.Sections[k];
                    if (gs.MaterialId < 0 || gs.MaterialId >= shaderTarget.Count) return "LOD " + l + " section " + k + " uses material " + gs.MaterialId + " but the mesh has " + shaderTarget.Count;
                    EbxAssetEntry shader = shaderTarget[gs.MaterialId];
                    ushort cats = gl.CategoryBits(k);
                    HashSet<VertexElementUsage> empty = MeshGeoPort.EmptyChannels(gs);
                    bool zeroRad = geo.NeverLightmapped;
                    List<SurfaceDonors.Donor> donors = DrawableFor(DonorsFor(shader, opt, users), compositeTarget);
                    string fallbackNote = null;
                    MeshGeoPort.LayoutMatch bestMatch;
                    bool shaderless = shader.Name.Equals(PortCommand.ShadowMeshShader, StringComparison.OrdinalIgnoreCase) && shaderTarget.Count > 0;
                    SurfaceDonors.Donor best;
                    bool translucentFallback = opt.FallbackMaterials != null && opt.FallbackMaterials.Contains(gs.MaterialId) && (cats & 0x1) == 0 && (cats & 0x6) != 0;
                    if (translucentFallback)
                    {
                        // a translucent section whose 2015 surface BFII does not have (a custom graph: cloud cards, snow-whirl
                        // backdrops, ice sheets): an opaque fallback draws it as a solid grey card, so the geometry is carried
                        // whole and drawn in no pass until an equivalent surface exists
                        best = Pick(donors, gs, gs.Prim, false, 0, l, empty, zeroRad, opt, out bestMatch, anyLayout: true);
                        if (best != null) { hidden.Add(l * 10000 + k); fallbackNote = "translucent section on a surface BFII does not have: carried, drawn in no pass"; }
                    }
                    else if (shaderless && gs.Named)
                    {
                        // a named section of a shader-less source material (occluder / character-collision / Enlighten-blocker
                        // stub): never drawn in the source, drawn in no pass here; any donor of the material's surface will do,
                        // resident first (Pick's key), so no database has to be stacked for geometry nobody sees
                        best = Pick(donors, gs, gs.Prim, false, 0, l, empty, zeroRad, opt, out bestMatch, anyLayout: true);
                        if (best != null) { hidden.Add(l * 10000 + k); fallbackNote = "shader-less source material: drawn in no pass, as in the source"; }
                    }
                    else
                    {
                        string want;
                        List<SurfaceDonors.Donor> pool = donors;
                        if (gs.Prim == PrimitiveType.PrimitiveType_TrianglePatch && tessWant.TryGetValue(gs.MaterialId, out want))
                        {
                            List<SurfaceDonors.Donor> same = donors.Where(d => d.Prim != PrimitiveType.PrimitiveType_TrianglePatch || d.Tessellation == want).ToList();
                            if (same.Count > 0) pool = same;
                        }
                        best = Pick(pool, gs, gs.Prim, gs.Named, cats, l, empty, zeroRad, opt, out bestMatch);
                        if (best == null && pool != donors) best = Pick(donors, gs, gs.Prim, gs.Named, cats, l, empty, zeroRad, opt, out bestMatch);
                        if (best != null && gs.Prim == PrimitiveType.PrimitiveType_TrianglePatch)
                        {
                            if (!tessWant.TryGetValue(gs.MaterialId, out want) || want != best.Tessellation)
                            {
                                if (tessLocked.Contains(gs.MaterialId))
                                    report.Add("lod" + l + "/sec" + k + " '" + gs.Name + "': WARNING no donor with the material's tessellation key " + want + "; drawn through " + best.Tessellation);
                                else
                                {
                                    if (want != null) report.Add("material " + gs.MaterialId + ": no donor of " + shader.Name + " has the source's tessellation key " + want + "; using " + best.Tessellation);
                                    tessWant[gs.MaterialId] = best.Tessellation;
                                }
                            }
                            tessLocked.Add(gs.MaterialId);
                        }
                    }
                    // 1. a depth/shadow section whose own surface has no donor for it: the depth donor of another material of
                    //    the mesh (depth-only geometry draws the same through any opaque surface's depth pass); BFII's own kit
                    //    meshes put their depth sections on material 0.
                    if (best == null && !gs.Named)
                        for (int om = 0; om < shaderTarget.Count && best == null; om++)
                        {
                            if (om == gs.MaterialId) continue;
                            List<SurfaceDonors.Donor> od = DrawableFor(DonorsFor(shaderTarget[om], opt, users), compositeTarget);
                            best = Pick(od, gs, gs.Prim, false, cats, l, empty, zeroRad, opt, out bestMatch);
                            if (best != null) { fallbackNote = "depth/shadow section moved from material " + gs.MaterialId + " to " + om + " (no depth donor on " + shader.Name + ")"; gs.MaterialId = om; shader = shaderTarget[om]; donors = od; }
                        }
                    // 2. a shader-less 2015 material (never drawn in 2015: helper stubs, occluders): any SS_Shadowmesh donor, drawn
                    //    in no pass, exactly as invisible as it is in 2015
                    if (best == null && shaderless)
                    {
                        best = Pick(donors, gs, gs.Prim, false, 0, l, empty, zeroRad, opt, out bestMatch, anyLayout: true);
                        if (best != null && gs.Named) { hidden.Add(l * 10000 + k); fallbackNote = "shader-less source material: never drawn (no subset category), as in the source"; }
                    }
                    // 3. a tessellated source section on a surface BFII compiles only for triangle lists: the same control cage as a
                    //    triangle list (the index data of a 3-point patch list and a triangle list are identical)
                    if (best == null && gs.Prim == PrimitiveType.PrimitiveType_TrianglePatch)
                    {
                        best = Pick(donors, gs, PrimitiveType.PrimitiveType_TriangleList, gs.Named, cats, l, empty, zeroRad, opt, out bestMatch);
                        if (best != null) { fallbackNote = "TrianglePatch drawn as TriangleList: " + shader.Name + " has no tessellated permutation in BFII"; gs.Prim = PrimitiveType.PrimitiveType_TriangleList; }
                    }
                    if (fallbackNote != null) report.Add("lod" + l + "/sec" + k + " '" + gs.Name + "': " + fallbackNote);
                    if (compositeTarget && MeshGeoPort.SpansParts(gs) && (best == null || !bestMatch.Carried.Contains(VertexElementUsage.BoneIndices)))
                    {
                        // A composite target: a donor of the same surface that keeps BoneIndices, accepting vertex colours the
                        // source lacks (filled neutral) -- DICE's composite users of a surface often carry colour streams for
                        // their other materials. Without one, every vertex of the section would follow part 0.
                        MeshGeoPort.LayoutMatch pm;
                        SurfaceDonors.Donor pb = PickKeepingParts(donors, gs, cats, l, empty, zeroRad, opt, out pm);
                        if (pb != null) { best = pb; bestMatch = pm; if (pm.FilledColors.Count > 0) colourFilled++; }
                        else if (best != null) partsPinned++;
                    }
                    if (best == null)
                    {
                        // why: the closest candidates and what they could not be filled with
                        var why = new List<string>();
                        foreach (SurfaceDonors.Donor d in donors.Take(40))
                        {
                            MeshGeoPort.LayoutMatch mm = MeshGeoPort.Compare(gs.Elements, d.Elements, empty);
                            string r = d.Prim != gs.Prim ? "prim " + d.Prim : d.Named != gs.Named ? (d.Named ? "named" : "unnamed") : (d.Categories & cats) == 0 ? "categories 0x" + d.Categories.ToString("X") : !mm.Usable ? "unfillable " + string.Join("/", mm.Unfillable) : "?";
                            string line = r + " (decl " + d.DeclHash + (d.Resident ? ", resident" : "") + ")";
                            if (!why.Contains(line)) why.Add(line);
                        }
                        string msg = "no donor section for LOD " + l + " section " + k + " ('" + gs.Name + "', " + gs.Prim + ", categories 0x" + cats.ToString("X") + ") among " + donors.Count +
                               " section(s) drawn by " + shader.Name + "; source layout [" + gs.DeclString() + "]; candidates: " + string.Join("; ", why.Take(6));
                        if (!opt.PlanOnly) return msg;
                        missing.Add(msg);
                        plan.Add("lod" + l + "/sec" + k + " mat" + gs.MaterialId + " '" + gs.Name + "' -> NO DONOR (" + shader.Name + ")");
                        row.Add(null);
                        continue;
                    }
                    if (!best.Resident && s_resident != null) foreign++;
                    if (opt.PlanOnly)
                    {
                        plan.Add("lod" + l + "/sec" + k + " mat" + gs.MaterialId + " '" + gs.Name + "' -> " + best.Mesh + " lod" + best.Lod + "/sec" + best.Section +
                                 " decl=" + best.DeclHash + " rvm=" + best.RvmRef + " sig=" + MeshGeoPort.RvmSignature(best.Elements) + (best.Resident ? "" : " FOREIGN") + " | " + bestMatch);
                        row.Add(null);
                        continue;
                    }
                    DonorMesh dm = LoadDonor(best.Mesh);
                    if (dm == null) return "could not load donor mesh " + best.Mesh;
                    ShaderBlockEntry dsbe = dm.Entries[best.Lod];
                    var ch = new MeshGeoPort.SectionChoice
                    {
                        Lod = l, Section = k, Donor = best, Match = bestMatch,
                        DonorSection = dm.Set.Lods[best.Lod].Sections[best.Section],
                        DonorMeshParams = dsbe.GetMeshParams(best.Section),
                        DonorHolder = (dsbe.Resources[best.Section] as ShaderStaticParamDbBlock) != null && ((ShaderStaticParamDbBlock)dsbe.Resources[best.Section]).Resources.Count > 1
                            ? ((ShaderStaticParamDbBlock)dsbe.Resources[best.Section]).Resources[1] as ShaderStaticParamDbBlock : null,
                        RvmRef = best.RvmRef, RvmInt = best.RvmInt
                    };
                    if (ch.DonorMeshParams == null || ch.DonorHolder == null) return "donor " + best + " has no mesh-parameter block or holder";
                    row.Add(ch);
                    if (!hidden.Contains(l * 10000 + k) && (cats & 0x7) != 0 && gs.Find(VertexElementUsage.RadiosityTexCoord) != null)
                    {
                        if (bestMatch.Dropped.Contains(VertexElementUsage.RadiosityTexCoord)) LastRadiosityDropped++; else LastRadiosityKept++;
                    }
                    if (!hidden.Contains(l * 10000 + k))
                    {
                        TessInfo ti;
                        if (!LastTessellation.TryGetValue(gs.MaterialId, out ti)) LastTessellation[gs.MaterialId] = ti = new TessInfo();
                        if (gs.Prim == PrimitiveType.PrimitiveType_TrianglePatch)
                        {
                            ti.Patch = true;
                            if (ti.DonorMaterial == null)
                            {
                                System.Collections.IList dmats = LevelCommand.ReadList(dm.Asset.RootObject, "Materials");
                                int dmi = ch.DonorSection.MaterialId;
                                ti.DonorMaterial = dmats != null && dmi >= 0 && dmi < dmats.Count ? LevelCommand.ResolveInternal(dmats[dmi]) : null;
                                ti.DonorMesh = best.Mesh;
                            }
                        }
                        else ti.List = true;
                    }
                    MeshGeoPort.SectionChoice prev;
                    // the material's holder comes from its first NAMED section (LOD 0 first); an unnamed section only if nothing better
                    if (!primaryFor.TryGetValue(gs.MaterialId, out prev) || (!prev.Donor.Named && best.Named))
                        primaryFor[gs.MaterialId] = ch;
                    plan.Add("lod" + l + "/sec" + k + " mat" + gs.MaterialId + " '" + gs.Name + "' -> " + best.Mesh + " lod" + best.Lod + "/sec" + best.Section +
                             " decl=" + best.DeclHash + " rvm=" + best.RvmRef + " sig=" + MeshGeoPort.RvmSignature(best.Elements) + (best.Resident ? "" : " FOREIGN") + " | " + bestMatch);
                }
                choices.Add(row);
            }
            if (opt.PlanOnly)
            {
                report.Add(missing.Count == 0 ? "plan: every section has a donor" + (foreign > 0 ? " (" + foreign + " FOREIGN)" : "") : "plan: " + missing.Count + " section(s) without a donor");
                foreach (string mm in missing) report.Add(mm);
                return missing.Count == 0 ? null : "no donor for " + missing.Count + " section(s)";
            }
            foreach (int h in hidden)
            {
                int hl = h / 10000, hk = h % 10000;
                foreach (List<byte> c in geo.Lods[hl].Categories) c.Remove((byte)hk);
            }
            var shared = new Dictionary<ulong, ShaderBlockResource>();
            var holders = new Dictionary<int, ShaderStaticParamDbBlock>();
            foreach (var kv in primaryFor)
            {
                ShaderStaticParamDbBlock h = MeshGeoPort.CopyHolder(kv.Value.DonorHolder, meshGuid, kv.Key, shared);
                // A holder carries its material's own overrides (DICE's blast door: 3 EBX vectors -> 3 holder entries, textures
                // from its MVDB record). Keep only what every donor of the surface binds identically (the surface's standard
                // bindings, e.g. the hangar kit's six textures); drop the donor material's own overrides, so every value the
                // source material does not set falls back to the surface default, as it does in the source.
                int kept, dropped, sample;
                StripDonorOverrides(h, DonorsFor(shaderTarget[kv.Key], opt, users), out kept, out dropped, out sample);
                report.Add("material " + kv.Key + " holder: " + kept + " standard binding(s) of the surface kept, " + dropped + " donor override(s) removed (" + sample + " donor holder(s) compared)" +
                           (sample < 2 && kept > 0 ? " -- ONE donor only: kept bindings unverified" : ""));
                // ...and carry the SOURCE material's own values for those fields (2015's triangle size, distance, scale, bias)
                IDictionary<string, string> srcFields;
                if (opt.SourceFields != null && opt.SourceFields.TryGetValue(kv.Key, out srcFields))
                {
                    int fset = MeshGeoPort.ApplyMaterialFields(h, srcFields);
                    if (fset > 0) report.Add("material " + kv.Key + " holder: " + fset + " material field(s) set from the source (tessellation constants)");
                }
                holders[kv.Key] = h;
            }
            MeshGeoPort.ApplyReport rep;
            string err = MeshGeoPort.Apply(newMesh, geo, choices, holders, out rep);
            if (err != null) return err;
            report.Add("geometry from the source dump: " + rep.Lods + " LOD(s), " + rep.Sections + " section(s), " + rep.Vertices + " vertices, " + rep.Indices + " indices; every section on a donor of its own surface" +
                       (foreign > 0 ? "; " + foreign + " section(s) on donors OUTSIDE the resident list (their level's RVM database must be stacked)" : ""));
            if (compositeTarget)
                report.Add(rep.PartsFromSource ? "composite: " + rep.Parts + " part(s) from the source (part transforms, part boxes, per-section part lists)"
                                               : "WARNING composite: the dump carries no parts (re-dump it with this CLI); the template's " + rep.Parts + " part(s) stay");
            int lost = 0;
            foreach (var row in choices) foreach (var c in row) if (c.Match.Dropped.Count > c.Match.DroppedEmpty.Count) lost++;
            if (lost > 0) report.Add(lost + " section(s) drop a non-empty source channel no donor layout of their surface carries (see the plan)");
            if (colourFilled > 0) report.Add(colourFilled + " multi-part section(s) keep their part indices on a composite donor whose layout adds vertex colours (filled Color0 white, Color1 zero)");
            if (partsPinned > 0) report.Add("WARNING " + partsPinned + " multi-part section(s) have no donor of their surface that keeps part indices: all of their vertices follow part 0");
            return null;
        }

        private static bool IsComposite(EbxAssetEntry mesh)
        {
            try
            {
                EbxAsset a = App.AssetManager.GetEbx(mesh);
                ulong rid = (ulong)((dynamic)a.RootObject).MeshSetResource;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                return ms != null && ms.Type.ToString().EndsWith("Composite", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // As Pick, for a section of a composite target that spans several parts: only donors whose layout carries BoneIndices,
        // vertex colours the source lacks allowed (filled neutral).
        private static SurfaceDonors.Donor PickKeepingParts(List<SurfaceDonors.Donor> donors, GeoSection gs, ushort cats, int lod,
            HashSet<VertexElementUsage> empty, bool zeroRad, Options opt, out MeshGeoPort.LayoutMatch bestMatch)
        {
            SurfaceDonors.Donor best = null; bestMatch = null; long bestKey = long.MaxValue;
            foreach (int pass in new[] { 0, 1 })
            {
                foreach (SurfaceDonors.Donor d in donors)
                {
                    if (d.Prim != gs.Prim || d.Named != gs.Named) continue;
                    if (pass == 0 ? (d.Categories & cats) != cats : (d.Categories & cats) == 0) continue;
                    MeshGeoPort.LayoutMatch m = MeshGeoPort.Compare(gs.Elements, d.Elements, empty, zeroRad, true);
                    if (!m.Usable || !m.Carried.Contains(VertexElementUsage.BoneIndices)) continue;
                    bool usableHere = d.Resident || opt.AllowForeign || s_resident == null;
                    long key = (usableHere ? 0L : 1L) << 50 | (long)Math.Min(MeshGeoPort.Score(m), 1 << 20) << 20 | (long)Math.Min(Math.Abs(d.Lod - lod), 15) << 16 | (long)(d.Categories == cats ? 0 : 1) << 15;
                    if (key < bestKey || (key == bestKey && best != null && string.CompareOrdinal(d.Mesh + d.Lod.ToString("D2") + d.Section.ToString("D3"), best.Mesh + best.Lod.ToString("D2") + best.Section.ToString("D3")) < 0))
                    { best = d; bestMatch = m; bestKey = key; }
                }
                if (best != null) break;
            }
            return best;
        }

        // The donors a section of this target can be drawn through (SurfaceDonors.Drawable: no occluder stand-ins; rigid targets
        // only on rigid donors).
        private static List<SurfaceDonors.Donor> DrawableFor(List<SurfaceDonors.Donor> donors, bool compositeTarget)
        {
            return donors.Where(d => SurfaceDonors.Drawable(d, compositeTarget)).ToList();
        }

        private static List<SurfaceDonors.Donor> DonorsFor(EbxAssetEntry shader, Options opt, Func<string, List<string>> users)
        {
            List<SurfaceDonors.Donor> donors;
            if (!s_donors.TryGetValue(shader.Guid, out donors))
            {
                donors = SurfaceDonors.Scan(shader, s_resident, opt.MaxMeshes, users);
                s_donors[shader.Guid] = donors;
            }
            return donors;
        }

        // The best donor for a section: same primitive type and named/unnamed role, drawing in every pass the section is in
        // (else in at least one), a usable layout with the lowest MeshGeoPort.Score, resident donors first, the same LOD index
        // preferred. anyLayout: pass/category and role checks off (shader-less stubs that are drawn in no pass).
        private static SurfaceDonors.Donor Pick(List<SurfaceDonors.Donor> donors, GeoSection gs, PrimitiveType prim, bool named, ushort cats, int lod,
            HashSet<VertexElementUsage> empty, bool zeroRad, Options opt, out MeshGeoPort.LayoutMatch bestMatch, bool anyLayout = false)
        {
            SurfaceDonors.Donor best = null; bestMatch = null; long bestKey = long.MaxValue;
            foreach (int pass in new[] { 0, 1 })
            {
                foreach (SurfaceDonors.Donor d in donors)
                {
                    if (d.Prim != prim) continue;
                    if (!anyLayout)
                    {
                        if (d.Named != named) continue;
                        if (pass == 0 ? (d.Categories & cats) != cats : (d.Categories & cats) == 0) continue;
                    }
                    MeshGeoPort.LayoutMatch m = MeshGeoPort.Compare(gs.Elements, d.Elements, empty, zeroRad);
                    if (!m.Usable) continue;
                    foreach (var de in d.Elements)
                    {
                        if (de.StreamIndex != 0 || !m.Carried.Contains(de.Usage)) continue;
                        string ck = gs.GetHashCode() + ":" + de.Usage + ":" + de.Format;
                        bool rt;
                        if (!s_roundTrip.TryGetValue(ck, out rt)) { rt = MeshGeoPort.RoundTrips(gs, de.Usage, de.Format); s_roundTrip[ck] = rt; }
                        if (!rt) m.Lossy.Add(de.Usage);
                    }
                    bool usableHere = d.Resident || opt.AllowForeign || s_resident == null;
                    // fidelity first (lost / narrowed / invented channels), residency second: a donor whose permutation the level
                    // does not compile costs one stacked shipped database, a lossy one costs 2015's data
                    long key = (long)Math.Min(MeshGeoPort.Score(m), 1 << 20) << 30 | (usableHere ? 0L : 1L) << 29 | (long)Math.Min(Math.Abs(d.Lod - lod), 15) << 16 | (long)(d.Categories == cats ? 0 : 1) << 15;
                    if (key < bestKey || (key == bestKey && best != null && string.CompareOrdinal(d.Mesh + d.Lod.ToString("D2") + d.Section.ToString("D3"), best.Mesh + best.Lod.ToString("D2") + best.Section.ToString("D3")) < 0))
                    { best = d; bestMatch = m; bestKey = key; }
                }
                if (best != null || anyLayout) break;
            }
            return best;
        }


        // Removes from <h> every parameter whose (name, value) is not bound identically by all sampled donor holders of the surface.
        private static void StripDonorOverrides(ShaderStaticParamDbBlock h, List<SurfaceDonors.Donor> donors, out int kept, out int dropped, out int sample)
        {
            kept = 0; dropped = 0; sample = 0;
            var holders = new List<ShaderStaticParamDbBlock>();
            var seenMesh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SurfaceDonors.Donor d in donors)
            {
                if (d.Lod != 0 || !d.Named || !seenMesh.Add(d.Mesh)) continue;
                DonorMesh dm = LoadDonor(d.Mesh);
                if (dm == null || d.Lod >= dm.Entries.Count || d.Section >= dm.Entries[d.Lod].Resources.Count) continue;
                var sec = dm.Entries[d.Lod].Resources[d.Section] as ShaderStaticParamDbBlock;
                var hh = sec != null && sec.Resources.Count > 1 ? sec.Resources[1] as ShaderStaticParamDbBlock : null;
                if (hh != null) holders.Add(hh);
                if (holders.Count >= 40) break;
            }
            sample = holders.Count;
            Func<ShaderStaticParamDbBlock, HashSet<string>> keys = hb =>
            {
                var set = new HashSet<string>();
                foreach (ShaderBlockResource c in hb.Resources)
                {
                    var pb = c as ShaderPersistentParamDbBlock;
                    if (pb == null) continue;
                    foreach (ParameterEntry pe in pb.Parameters) set.Add(pe.NameHash.ToString("X8") + ":" + BitConverter.ToString(pe.Value ?? new byte[0]));
                }
                return set;
            };
            HashSet<string> common = null;
            foreach (ShaderStaticParamDbBlock hb in holders) { var k = keys(hb); if (common == null) common = k; else common.IntersectWith(k); }
            if (common == null) { foreach (ShaderBlockResource c in h.Resources) { var pb = c as ShaderPersistentParamDbBlock; if (pb != null) kept += pb.Parameters.Count; } return; }
            foreach (ShaderBlockResource c in h.Resources)
            {
                var pb = c as ShaderPersistentParamDbBlock;
                if (pb == null || (pb.Hash == 0 && pb.Parameters.Count == 0)) continue;
                int before = pb.Parameters.Count;
                // the material's own field constants (tessellation, flags) are never donor overrides: keep them
                pb.Parameters.RemoveAll(pe => !MeshGeoPort.IsMaterialFieldEntry(pe.TypeHash) &&
                                              !common.Contains(pe.NameHash.ToString("X8") + ":" + BitConverter.ToString(pe.Value ?? new byte[0])));
                dropped += before - pb.Parameters.Count; kept += pb.Parameters.Count;
                pb.IsModified = true;
            }
        }

        private static DonorMesh LoadDonor(string mesh)
        {
            DonorMesh dm;
            if (s_meshes.TryGetValue(mesh, out dm)) return dm;
            try
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(mesh);
                EbxAsset a = App.AssetManager.GetEbx(e);
                ulong rid = (ulong)((dynamic)a.RootObject).MeshSetResource;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(App.AssetManager.GetResEntry(mesh.ToLowerInvariant() + "_mesh/blocks"));
                dm = new DonorMesh
                {
                    Asset = a,
                    Set = ms,
                    All = (List<ShaderBlockResource>)typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot),
                    Entries = (List<ShaderBlockEntry>)typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot)
                };
            }
            catch { dm = null; }
            s_meshes[mesh] = dm;
            return dm;
        }
    }
}
