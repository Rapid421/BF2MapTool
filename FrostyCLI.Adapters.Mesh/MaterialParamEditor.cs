using System;
using System.Globalization;
using Frosty.Core;
using FrostySdk.Interfaces;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // Headless material/shader-parameter editing (a Frosty GUI capability: FrostyMeshSetEditor updates
    // a mesh's ShaderBlockDepot when a material's shader params change). Sets a named parameter on the
    // ShaderBlockDepot param blocks for the sections using a given material, so the game renders the new
    // value. Only sets parameters that ALREADY EXIST (their type is read to coerce the value correctly);
    // it never adds a new (mis-typed) parameter. NOTE: validated structurally headlessly -- the in-game
    // render of the new value is the user's check.
    public static class MaterialParamEditor
    {
        // Sets <paramName> = <valueStr> on <materialIndex> of the mesh. Returns null on success, else an
        // error. setSections reports how many section param-blocks were updated.
        public static string SetParam(EbxAssetEntry meshEntry, int materialIndex, string paramName,
            string valueStr, ILogger logger, out int setSections)
        {
            _typeMismatch = false;
            return SetParam(meshEntry, materialIndex, paramName, valueStr, logger, false, out setSections);
        }

        /// <summary>
        /// As above, but <paramref name="addMissing"/> appends the parameter when the block does not
        /// already carry it. A grafted material inherits the DONOR's parameter block, which only holds
        /// the parameters that donor overrode -- so carrying a source game's values across needs the
        /// absent ones created, not just the present ones overwritten. A value of four floats is added
        /// as a Vec4 (the encoding every VectorParameter in a shader graph uses); a GUID is added as a
        /// texture parameter bound to that texture's class GUID.
        /// </summary>
        public static string SetParam(EbxAssetEntry meshEntry, int materialIndex, string paramName,
            string valueStr, ILogger logger, bool addMissing, out int setSections)
        {
            setSections = 0;
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

            // ShaderBlockDepot(s) for a mesh are the Res whose name starts with the mesh's name
            // (matches FrostyMeshSetEditor's association).
            string meshNameLower = meshEntry.Name.ToLowerInvariant();
            bool anyDepot = false;
            foreach (ResAssetEntry re in App.AssetManager.EnumerateRes((uint)ResourceType.ShaderBlockDepot))
            {
                if (re.Name.IndexOf(meshNameLower, StringComparison.OrdinalIgnoreCase) != 0) continue;
                anyDepot = true;
                ShaderBlockDepot sbd;
                try { sbd = App.AssetManager.GetResAs<ShaderBlockDepot>(re); }
                catch { continue; }

                bool modified = false;
                // Clones share their material parameter/texture blocks (and the holder that owns them)
                // with the template, hash and all. The runtime caches those blocks by hash, so an edit
                // that keeps the template's hash is ignored in game: the template's values win. Give
                // every block we change (and its holder) a hash salted with this mesh's GUID, the way
                // Frosty's DuplicationPlugin rehashes every block of a duplicated mesh.
                var rehashed = new System.Collections.Generic.HashSet<ShaderBlockResource>();
                byte[] salt = meshEntry.Guid.ToByteArray();
                for (int lodIdx = 0; lodIdx < meshSet.Lods.Count; lodIdx++)
                {
                    ShaderBlockEntry sbe = sbd.GetSectionEntry(lodIdx);
                    if (sbe == null) continue;
                    int index = 0; // running section index within the LOD (matches the editor)
                    foreach (MeshSetSection section in meshSet.Lods[lodIdx].Sections)
                    {
                        if (section.MaterialId == materialIndex)
                        {
                            ShaderPersistentParamDbBlock hit = null;
                            if (TrySet(sbe.GetParams(index), paramName, valueStr)) hit = sbe.GetParams(index);
                            else if (TrySet(sbe.GetTextureParams(index), paramName, valueStr)) hit = sbe.GetTextureParams(index);
                            else if (addMissing && TryAdd(sbe.GetParams(index), paramName, valueStr)) hit = sbe.GetParams(index);
                            else if (addMissing && TryAddTexture(sbe.GetTextureParams(index), paramName, valueStr)) hit = sbe.GetTextureParams(index);
                            if (hit != null)
                            {
                                modified = true; setSections++;
                                Rehash(hit, salt, rehashed);
                                var sec = index < sbe.Resources.Count ? sbe.Resources[index] as ShaderStaticParamDbBlock : null;
                                var holder = sec != null && sec.Resources.Count > 1 ? sec.Resources[1] as ShaderStaticParamDbBlock : null;
                                if (holder != null) Rehash(holder, salt, rehashed);
                            }
                        }
                        index++;
                    }
                }
                if (modified)
                {
                    App.AssetManager.ModifyRes(sbd.ResourceId, sbd);

                    // ModifyRes stores a ModifiedShaderBlockDepot *delta* that only the custom action
                    // handler can apply -- and that handler throws on this game, so such a mod applies
                    // nothing. Dumping the whole serialized depot here lets the edit be shipped as raw
                    // res bytes instead, which needs no handler at all.
                    string dump = Environment.GetEnvironmentVariable("FROSTYCLI_SBD_DUMP");
                    if (!string.IsNullOrEmpty(dump))
                    {
                        try
                        {
                            var sbm = sbd.GetType().GetMethod("SaveBytes", Type.EmptyTypes);
                            if (sbm == null) logger.LogWarning("sbd dump: no SaveBytes on {0}", sbd.GetType().FullName);
                            else
                            {
                                byte[] rawBytes = sbm.Invoke(sbd, null) as byte[];
                                if (rawBytes == null) logger.LogWarning("sbd dump: SaveBytes returned null for {0}", re.Name);
                                else
                                {
                                    System.IO.Directory.CreateDirectory(dump);
                                    string safe = re.Name.Replace("/", "_").Replace("\\", "_");
                                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dump, safe + ".res"), rawBytes);
                                    logger.LogWarning("sbd dump: {0} -> {1} bytes", re.Name, rawBytes.Length);
                                }
                            }
                        }
                        catch (Exception ex) { logger.LogWarning("sbd dump failed for {0}: {1}", re.Name, ex.Message); }
                    }
                }
            }

            if (!anyDepot) return "no ShaderBlockDepot found for mesh '" + meshEntry.Name + "' (not a shader-block mesh, or wrong profile).";
            if (setSections == 0)
                return _typeMismatch
                    ? "parameter '" + paramName + "' exists on material " + materialIndex + " but the value '" + valueStr + "' could not be converted to its type (for a texture slot pass an asset path or its class GUID)."
                    : "parameter '" + paramName + "' not found on material " + materialIndex + ".";
            return null;
        }

        private static void Rehash(ShaderBlockResource res, byte[] salt, System.Collections.Generic.HashSet<ShaderBlockResource> done)
        {
            if (res == null || !done.Add(res)) return;
            // The salt exists because a CLONED mesh shares its param blocks (hash and all) with the
            // template, and the runtime caches blocks by hash, so an unsalted edit is ignored. A mesh
            // that already owns its blocks does not need it -- and if anything else references those
            // hashes, re-hashing breaks the reference. FROSTYCLI_SBD_NO_REHASH skips it.
            if (Environment.GetEnvironmentVariable("FROSTYCLI_SBD_NO_REHASH") != null)
            { res.IsModified = true; return; }
            using (var w = new FrostySdk.IO.NativeWriter(new System.IO.MemoryStream()))
            {
                w.Write(res.Hash); w.Write(salt);
                res.Hash = Frosty.Hash.CityHash.Hash64(w.ToByteArray());
            }
            res.IsModified = true;
        }

        // Appends a TEXTURE parameter the block does not have, bound to a texture's class GUID. The name
        // need not exist anywhere in the game: ParameterEntry(name, Guid) keys the entry exactly as the
        // runtime ParamDb does -- (CityHash64(u32 1 | u32 nameLen | u32 typeNameLen | u32 moduleLen |
        // name | "ITexture" | "Render") & 0xFFFFFFFFFFFF) | hashQuickLowerCase(name) << 48, which
        // reproduces every named key stored in a shipped SWBF2 RVM database -- so a shader built to read
        // a brand-new parameter receives the texture bound here.
        private static bool TryAddTexture(ShaderPersistentParamDbBlock block, string name, string valueStr)
        {
            if (block == null) return false;
            if (block.GetParameterValue(name) != null) return false;   // present -> TrySet's job
            // a texture ASSET PATH resolves to its class guid like TrySet's does (the literal-guid-only parse made
            // `--add-missing --value <texture path>` report "not found" for every absent slot; Carbonite materials 2026-09-27)
            Guid? g = TextureValueGuid(valueStr, ResolveGuid);
            if (g == null) return false;
            try { block.Parameters.Add(new ParameterEntry(name, g.Value)); }
            catch { return false; }
            block.IsModified = true;
            return true;
        }

        // Appends a Vec4 parameter the block does not have. ParameterEntry(name, value) computes the
        // name/type hashes itself, so the entry is indistinguishable from one the tools authored.
        private static bool TryAdd(ShaderPersistentParamDbBlock block, string name, string valueStr)
        {
            if (block == null) return false;
            if (block.GetParameterValue(name) != null) return false;   // present -> TrySet's job
            // "true" / "false" adds a Boolean (a static switch or a shader-read flag); shipped depots carry
            // Booleans as 1-byte entries, keyed like every other parameter by ParameterEntry itself.
            bool flag;
            if (bool.TryParse(valueStr, out flag))
            {
                try { block.Parameters.Add(new ParameterEntry(name, flag)); }
                catch { return false; }
                block.IsModified = true;
                return true;
            }
            string[] p = valueStr.Split(',');
            var inv = CultureInfo.InvariantCulture;
            // a single number is a scalar (Float32) parameter -- the type shipped depots use for ShaderParameterType_Scalar
            float single;
            if (p.Length == 1 && float.TryParse(p[0], NumberStyles.Float, inv, out single))
            {
                try { block.Parameters.Add(new ParameterEntry(name, single)); }
                catch { return false; }
                block.IsModified = true;
                return true;
            }
            if (p.Length != 4) return false;
            float[] v;
            try
            {
                v = new[] { float.Parse(p[0], NumberStyles.Float, inv), float.Parse(p[1], NumberStyles.Float, inv),
                            float.Parse(p[2], NumberStyles.Float, inv), float.Parse(p[3], NumberStyles.Float, inv) };
            }
            catch { return false; }
            try { block.Parameters.Add(new ParameterEntry(name, v)); }
            catch { return false; }
            block.IsModified = true;
            return true;
        }

        /// <summary>A texture slot value: a literal GUID as given, else <paramref name="byPath"/> (asset path -> class guid).</summary>
        public static Guid? TextureValueGuid(string valueStr, Func<string, Guid?> byPath)
        {
            if (string.IsNullOrWhiteSpace(valueStr)) return null;
            Guid g;
            if (Guid.TryParse(valueStr, out g)) return g;
            return byPath != null ? byPath(valueStr) : null;
        }

        // Texture slots in a ShaderBlockDepot hold the texture asset's CLASS guid, not its file guid
        // and not its path. Callers naturally pass a path, so accept one: look the asset up and read
        // the class guid off its root object. A literal GUID string still works unchanged.
        private static Guid? ResolveGuid(string v)
        {
            Guid g;
            if (Guid.TryParse(v, out g)) return g;
            if (App.AssetManager == null) return null;
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(v);
            if (e == null) return null;
            try
            {
                dynamic root = App.AssetManager.GetEbx(e).RootObject;
                return (Guid)root.GetInstanceGuid().ExportedGuid;
            }
            catch { return null; }
        }

        // Set when a parameter WAS present but the value could not be coerced to its type, so the
        // caller can say that instead of the misleading "parameter not found".
        [ThreadStatic] private static bool _typeMismatch;

        private static bool TrySet(ShaderPersistentParamDbBlock block, string name, string valueStr)
        {
            if (block == null) return false;
            // "#<hex>": the parameter by its name hash, for parameters whose name no corpus knows (the depot stores only
            // the djb2-xor hash of the lowercased name; e.g. the Corvus landing light's mask texture is #dde1d277).
            if (name.StartsWith("#"))
            {
                uint hash;
                if (!uint.TryParse(name.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash)) return false;
                foreach (ParameterEntry pe in block.Parameters)
                {
                    if (pe.NameHash != hash) continue;
                    object curH = pe.GetValue();
                    if (curH == null) return false;
                    object coercedH;
                    try { coercedH = Coerce(curH, valueStr); } catch { return false; }
                    if (coercedH == null) return false;
                    pe.SetValue(coercedH);
                    block.IsModified = true;
                    return true;
                }
                return false;
            }
            object cur = block.GetParameterValue(name);
            if (cur == null) return false; // not present -> don't add a mis-typed param
            object coerced;
            try { coerced = Coerce(cur, valueStr); }
            catch { _typeMismatch = true; return false; }
            if (coerced == null) { _typeMismatch = true; return false; }
            block.SetParameterValue(name, coerced);
            block.IsModified = true;
            return true;
        }

        // Coerces the string value to the .NET type the ShaderBlockDepot ParameterEntry expects (read
        // from the current value), matching ParameterEntry.SetValue's per-type encoding.
        private static object Coerce(object cur, string v)
        {
            var inv = CultureInfo.InvariantCulture;
            if (cur is bool)   return bool.Parse(v);
            if (cur is float)  return float.Parse(v, NumberStyles.Float, inv);
            if (cur is float[])
            {
                string[] p = v.Split(',');
                if (p.Length != 4) return null;
                return new[] { float.Parse(p[0], NumberStyles.Float, inv), float.Parse(p[1], NumberStyles.Float, inv),
                               float.Parse(p[2], NumberStyles.Float, inv), float.Parse(p[3], NumberStyles.Float, inv) };
            }
            if (cur is uint)   return uint.Parse(v, inv);
            if (cur is byte)   return byte.Parse(v, inv);
            if (cur is long)   return long.Parse(v, inv);
            if (cur is Guid)   return ResolveGuid(v);
            if (cur is int || cur is Enum) return int.Parse(v, inv); // enums encode as int
            return null;
        }
    }
}
