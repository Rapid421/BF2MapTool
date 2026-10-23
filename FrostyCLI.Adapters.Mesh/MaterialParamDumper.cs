using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Frosty.Core;
using FrostySdk.Interfaces;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // READ side of MaterialParamEditor: walks a mesh's ShaderBlockDepot(s) exactly the way SetParam
    // does and reports every parameter slot it finds, so a caller can see what a material currently
    // binds without mutating anything.
    //
    // Parameters are stored BY HASH -- the res files carry no ASCII names -- and the hash function is
    // not reproducible here, so names are recovered by probing: GetParameterValue(name) is called for
    // each candidate name and the ones that answer are reported with that name attached. Candidates
    // come from the distinct ParameterName values in a MeshVariationDatabase dump.
    public static class MaterialParamDumper
    {
        // Names seen across the SWBF2 MVDBs. Extend with --names when a shader uses something else.
        public static readonly string[] DefaultNames = new[]
        {
            "ColorSmoothness", "Normal", "CS", "NW", "NS", "MSW", "_CS", "_Normal", "_BaseColor",
            "BaseColor", "Basecolor", "colormap", "Reflectance", "SssMask", "TranslucencyMask",
            "UniqueNormal", "NormalAO", "DestructionRGBA", "_Mask", "_Emissive", "_CA",
            "T_BaseColour", "T_Normal", "ColorPalette", "Normal_AO_Mask", "NormalDetailTextureArray",
            "AOSlice", "_Opacity2UV", "_NAM_texcoord0", "_NAM_texcoord1", "Emissive", "Opacity",
            "Detail", "DetailNormal", "Roughness", "Metallic", "Specular", "AO",
            // BF1 (2015) spellings seen in Desert_04's MVDB
            "CS_textCoord1", "NW_texCoord0", "NW_texCoord1", "RGBA",
            // BF1 Bespin (Clouds_04) metallic workflow - without these the dumper reports a
            // material as having fewer slots than it really samples, and a caller binds blind.
            "MSR", "MSRAO", "WM", "WeatheringMask", "NormalDetail", "NormalDetailTextureArray",
            "NP", "NA", "NM", "CM", "CD", "CA", "_CA", "_CS", "_Nb", "_Nrg", "HelmetAO",
            "TranslucencyMask", "Reflectance", "IdMap", "__IdMap", "__NormalEmissiveAO",
            "BaseMetal_CS", "Rust_CS_Texture", "Overall_NA", "Overall_RGBA_Mask",
            "_NAM_texcoord0", "_NAM_texcoord1", "_Mask", "_Emissive", "_BaseColor", "_Normal"
        };

        /// <summary>
        /// Dumps every parameter slot of <paramref name="meshEntry"/>'s shader block depots as JSON.
        /// <paramref name="onlyMaterial"/> &lt; 0 dumps all materials. Returns null on success.
        /// </summary>
        public static string Dump(EbxAssetEntry meshEntry, int onlyMaterial, IEnumerable<string> names,
            ILogger logger, out string json) => Dump(meshEntry, onlyMaterial, names, null, logger, out json);

        /// <summary>
        /// As above; with <paramref name="variation"/> (an ObjectVariation asset name) it dumps that variation's depot for the
        /// mesh, "&lt;variation&gt;/&lt;mesh file&gt;_&lt;MeshSet name hash&gt;/shaderblocks_variation/blocks", instead of the mesh's own.
        /// </summary>
        public static string Dump(EbxAssetEntry meshEntry, int onlyMaterial, IEnumerable<string> names, string variation,
            ILogger logger, out string json)
        {
            json = "[]";
            if (App.AssetManager == null) return "AssetManager not initialized.";
            if (meshEntry == null) return "no mesh entry.";

            FrostySdk.IO.EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(meshEntry); }
            catch (Exception ex) { return "mesh load failed: " + ex.Message; }

            ulong rid;
            try { rid = (ulong)((dynamic)asset.RootObject).MeshSetResource; }
            catch (Exception ex) { return "not a mesh asset (no MeshSetResource): " + ex.Message; }
            if (rid == 0) return "mesh has no geometry resource.";

            ResAssetEntry meshRes = App.AssetManager.GetResEntry(rid);
            if (meshRes == null) return "mesh res not found.";
            MeshSet meshSet;
            try { meshSet = App.AssetManager.GetResAs<MeshSet>(meshRes); }
            catch (Exception ex) { return "could not load MeshSet: " + ex.Message; }

            List<string> cand = new List<string>();
            if (names != null) foreach (string n in names) if (!string.IsNullOrEmpty(n)) cand.Add(n);
            if (cand.Count == 0) cand.AddRange(DefaultNames);

            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            bool first = true;
            bool anyDepot = false;

            string meshNameLower = meshEntry.Name.ToLowerInvariant();
            string prefix = variation == null ? meshNameLower
                : variation.ToLowerInvariant() + "/" + meshNameLower.Substring(meshNameLower.LastIndexOf('/') + 1) + "_" + meshSet.NameHash + "/";
            foreach (ResAssetEntry re in App.AssetManager.EnumerateRes((uint)ResourceType.ShaderBlockDepot))
            {
                if (re.Name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) != 0) continue;
                anyDepot = true;

                ShaderBlockDepot sbd;
                try { sbd = App.AssetManager.GetResAs<ShaderBlockDepot>(re); }
                catch (Exception ex) { logger.LogWarning("material dump: {0}: {1}", re.Name, ex.Message); continue; }

                for (int lodIdx = 0; lodIdx < meshSet.Lods.Count; lodIdx++)
                {
                    ShaderBlockEntry sbe = sbd.GetSectionEntry(lodIdx);
                    if (sbe == null) continue;
                    int index = 0;
                    foreach (MeshSetSection section in meshSet.Lods[lodIdx].Sections)
                    {
                        if (onlyMaterial < 0 || section.MaterialId == onlyMaterial)
                        {
                            Emit(sb, ref first, re.Name, lodIdx, index, section.MaterialId, "params",
                                 Safe(() => sbe.GetParams(index)), cand);
                            Emit(sb, ref first, re.Name, lodIdx, index, section.MaterialId, "textures",
                                 Safe(() => sbe.GetTextureParams(index)), cand);
                        }
                        index++;
                    }
                }
            }

            sb.Append(']');
            json = sb.ToString();
            if (!anyDepot) return "no ShaderBlockDepot found for mesh '" + meshEntry.Name + "'" + (variation != null ? " under " + prefix : "") + ".";
            return null;
        }

        private static ShaderPersistentParamDbBlock Safe(Func<ShaderPersistentParamDbBlock> f)
        {
            try { return f(); } catch { return null; }
        }

        private static void Emit(StringBuilder sb, ref bool first, string res, int lod, int sec,
            int matId, string kind, ShaderPersistentParamDbBlock block, List<string> cand)
        {
            if (block == null) return;

            // How many entries the block holds, for a completeness check against the probed names.
            int total = -1;
            try
            {
                object pars = block.GetType().GetProperty("Parameters") != null
                    ? block.GetType().GetProperty("Parameters").GetValue(block)
                    : (block.GetType().GetField("Parameters") != null
                        ? block.GetType().GetField("Parameters").GetValue(block) : null);
                ICollection col = pars as ICollection;
                if (col != null) total = col.Count;
            }
            catch { }

            // FROSTYCLI_PARAM_HASHES=1 prints each ParameterEntry's own fields. Names are NOT
            // stored in the RVM database - parameters are identified only by a 64-bit hash - so
            // recovering the (name -> hash) mapping means asking Frosty, which computes it.
            if (Environment.GetEnvironmentVariable("FROSTYCLI_PARAM_HASHES") != null)
            {
                try
                {
                    // Parameters is a FIELD on this SDK build, not a property
                    var pp = block.GetType().GetProperty("Parameters");
                    object pv = pp != null ? pp.GetValue(block) : null;
                    if (pv == null)
                    {
                        var pf = block.GetType().GetField("Parameters");
                        if (pf != null) pv = pf.GetValue(block);
                    }
                    var col = pv as IEnumerable;
                    if (col != null)
                    {
                        foreach (object pe in col)
                        {
                            var sb2 = new StringBuilder("param-entry:");
                            foreach (var prop in pe.GetType().GetProperties())
                            {
                                object val;
                                try { val = prop.GetValue(pe); } catch { continue; }
                                if (val is byte[]) val = BitConverter.ToString((byte[])val).Replace("-", "");
                                sb2.Append(' ').Append(prop.Name).Append('=').Append(Convert.ToString(val, CultureInfo.InvariantCulture));
                            }
                            foreach (var fld in pe.GetType().GetFields())
                            {
                                object val;
                                try { val = fld.GetValue(pe); } catch { continue; }
                                if (val is byte[]) val = BitConverter.ToString((byte[])val).Replace("-", "");
                                sb2.Append(' ').Append(fld.Name).Append('=').Append(Convert.ToString(val, CultureInfo.InvariantCulture));
                            }
                            Console.Error.WriteLine(sb2.ToString());
                        }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("param-entry dump failed: " + ex.Message); }
            }

            foreach (string name in cand)
            {
                object cur;
                try { cur = block.GetParameterValue(name); }
                catch { continue; }
                if (cur == null) continue;

                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                Field(sb, "res", res);            sb.Append(',');
                Field(sb, "lod", lod.ToString()); sb.Append(',');
                Field(sb, "section", sec.ToString()); sb.Append(',');
                Field(sb, "material", matId.ToString()); sb.Append(',');
                Field(sb, "block", kind);         sb.Append(',');
                Field(sb, "entries", total.ToString()); sb.Append(',');
                Field(sb, "name", name);          sb.Append(',');
                Field(sb, "type", cur.GetType().Name); sb.Append(',');
                Field(sb, "value", Render(cur));
                sb.Append('}');
            }
        }

        private static string Render(object v)
        {
            if (v is Guid) return ((Guid)v).ToString();
            float[] f = v as float[];
            if (f != null)
            {
                StringBuilder s = new StringBuilder();
                for (int i = 0; i < f.Length; i++)
                {
                    if (i > 0) s.Append(',');
                    s.Append(f[i].ToString("R", CultureInfo.InvariantCulture));
                }
                return s.ToString();
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static void Field(StringBuilder sb, string k, string v)
        {
            sb.Append('"').Append(Esc(k)).Append("\":\"").Append(Esc(v ?? "")).Append('"');
        }

        private static string Esc(string s)
        {
            StringBuilder o = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') { o.Append('\\').Append(c); }
                else if (c == '\n') o.Append("\\n");
                else if (c == '\r') o.Append("\\r");
                else if (c == '\t') o.Append("\\t");
                else if (c < 0x20) o.Append("\\u").Append(((int)c).ToString("x4"));
                else o.Append(c);
            }
            return o.ToString();
        }
    }
}
