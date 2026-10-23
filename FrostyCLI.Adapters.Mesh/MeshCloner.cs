using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace FrostyCLI.Adapters.Mesh
{
    // Creates an independent NEW mesh asset by duplicating a template mesh's EBX + MeshSet res (and,
    // for chunk-backed LODs, the geometry chunks) and rewiring them, so a subsequent FBX import writes
    // into the new asset without touching the template. This is the headless equivalent of the GUI's
    // "duplicate a structurally-similar mesh, then import" workflow -- it reuses the template's section
    // structure / vertex declarations / material assignment (no material authoring), which is exactly
    // what the importer needs (it fills FBX geometry into existing sections).
    public static class MeshCloner
    {
        // Clones <templateName> to a new mesh asset <newName>. Returns null on success (newEntry set),
        // otherwise an error message.
        public static string Clone(string templateName, string newName, out EbxAssetEntry newEntry)
        {
            newEntry = null;
            LastCloneNote = null;
            if (App.AssetManager == null) return "AssetManager not initialized.";
            if (App.AssetManager.GetEbxEntry(newName) != null) return "destination already exists: " + newName;

            EbxAssetEntry tmplEntry = App.AssetManager.GetEbxEntry(templateName);
            if (tmplEntry == null) return "template not found: " + templateName;
            EbxAsset tmplAsset;
            try { tmplAsset = App.AssetManager.GetEbx(tmplEntry); }
            catch (Exception ex) { return "template load failed: " + ex.Message; }

            ulong tmplResRid;
            try { tmplResRid = (ulong)((dynamic)tmplAsset.RootObject).MeshSetResource; }
            catch (Exception ex) { return "template has no MeshSetResource: " + ex.Message; }
            if (tmplResRid == 0) return "template '" + templateName + "' has no geometry resource.";
            ResAssetEntry tmplResEntry = App.AssetManager.GetResEntry(tmplResRid);
            if (tmplResEntry == null) return "template res not found.";

            // 1. Clone the EBX (serialize round-trip), assign fresh file + instance GUIDs and the new name.
            EbxAsset newAsset;
            try { newAsset = CloneEbx(tmplAsset); }
            catch (Exception ex) { return "EBX clone failed: " + ex.Message + (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") == "1" ? Environment.NewLine + ex.ToString() : ""); }
            newAsset.SetFileGuid(Guid.NewGuid());
            dynamic root = newAsset.RootObject;
            try { root.Name = newName; } catch { /* some mesh roots may not expose Name */ }
            Guid newRootGuid = Guid.Empty;
            try
            {
                var guid = new AssetClassGuid(
                    Utils.GenerateDeterministicGuid(newAsset.Objects, (Type)root.GetType(), newAsset.FileGuid), -1);
                root.SetInstanceGuid(guid);
                newRootGuid = guid.ExportedGuid;
            }
            catch (Exception ex) { return "instance-guid assign failed: " + ex.Message; }

            // 2. Duplicate the MeshSet res (raw decompressed bytes + meta -> AddRes).
            byte[] resBytes;
            try { resBytes = GetResBytes(tmplResEntry); }
            catch (Exception ex) { return "could not read template res: " + ex.Message; }
            // Shipped meshes name their MeshSet res exactly like the EBX (lower case); the depot then
            // hangs off it as "<res>_mesh/blocks". Keep that shape so nothing has to special-case clones.
            string newResName = newName.ToLowerInvariant();
            if (App.AssetManager.GetResEntry(newResName) != null)
                newResName = newResName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            // COPY the template's res meta: MeshSet.ToBytes writes the clone's inline-data offsets INTO the
            // meta array it was given, so sharing the template's array between clones (and with the
            // template) let the last clone written in a process stamp its offsets onto every earlier one.
            // The game then reads inline data past the end of the resource and never finishes loading.
            try { App.AssetManager.AddRes(newResName, (ResourceType)tmplResEntry.ResType, (byte[])(tmplResEntry.ResMeta ?? new byte[0]).Clone(), resBytes); }
            catch (Exception ex) { return "AddRes failed: " + ex.Message; }
            ResAssetEntry newResEntry = App.AssetManager.GetResEntry(newResName);
            if (newResEntry == null) return "AddRes did not create " + newResName;
            ulong newResRid = newResEntry.ResRid;

            // 3. For chunk-backed LODs, duplicate the geometry chunk and rewire the new MeshSet's ChunkId
            //    (so a later ImportFBX ModifyChunk targets the NEW chunk, not the template's). Inline LODs
            //    need nothing -- their geometry lives in the res bytes we just duplicated.
            MeshSet newMeshSet;
            try { newMeshSet = App.AssetManager.GetResAs<MeshSet>(newResEntry); }
            catch (Exception ex) { return "could not load duplicated MeshSet: " + ex.Message; }
            bool rewired = false;
            try
            {
                foreach (MeshSetLod lod in newMeshSet.Lods)
                {
                    if (lod.ChunkId == Guid.Empty) continue;
                    byte[] chunkData = GetChunkBytes(lod.ChunkId);
                    if (chunkData == null) continue;
                    Guid newChunkId = App.AssetManager.AddChunk(chunkData, null);
                    lod.ChunkId = newChunkId;
                    rewired = true;
                }
            }
            catch (Exception ex) { return "chunk duplication failed: " + ex.Message; }
            // The MeshSet carries the mesh's own name (and per-LOD names) plus their hash; the editor
            // resolves the EBX entry from it and the shader block depot is keyed by it. Give the clone
            // its own name so it no longer masquerades as the template.
            uint templateNameHash = newMeshSet.NameHash;
            try
            {
                newMeshSet.FullName = newName;
                foreach (MeshSetLod lod in newMeshSet.Lods) lod.Name = newName.ToLowerInvariant();   // Name keeps the "Mesh:" debug-name prefix; FullName would make it collide with the LOD name in the string table
                rewired = true;
            }
            catch (Exception ex) { return "could not rename the MeshSet: " + ex.Message; }
            uint newNameHash = newMeshSet.NameHash;
            if (rewired) App.AssetManager.ModifyRes(newResRid, newMeshSet);

            // 3b. Shader block depot: shipped meshes carry "<mesh>_mesh/blocks" (per-LOD/section shader
            //     parameter blocks). Duplicate it verbatim under the new name so the material editor and
            //     the bundler find it; a cloned mesh without one renders with no block parameters.
            ResAssetEntry tmplBlocks = App.AssetManager.GetResEntry(templateName.ToLowerInvariant() + "_mesh/blocks");
            if (tmplBlocks != null)
            {
                string newBlocksName = newName.ToLowerInvariant() + "_mesh/blocks";
                if (App.AssetManager.GetResEntry(newBlocksName) == null)
                {
                    try
                    {
                        byte[] blockBytes = GetResBytes(tmplBlocks);
                        App.AssetManager.AddRes(newBlocksName, (ResourceType)tmplBlocks.ResType, (byte[])(tmplBlocks.ResMeta ?? new byte[0]).Clone(), blockBytes);
                        // Entries are keyed by CityHash(meshNameHash, variationNameHash, lodIndex); re-key
                        // the ones the template's name hash explains so the runtime finds them under
                        // the clone's name. Unexplained entries (other variations) keep their hash.
                        ResAssetEntry nb = App.AssetManager.GetResEntry(newBlocksName);
                        ShaderBlockDepot depot = nb != null ? App.AssetManager.GetResAs<ShaderBlockDepot>(nb) : null;
                        if (depot != null)
                        {
                            int rekeyed = 0, unexplained = 0;
                            for (int i = 0; i < depot.ResourceCount; i++)
                            {
                                ShaderBlockResource res = depot.GetResource(i);
                                if (!(res is ShaderBlockEntry) && !(res is ShaderBlockMeshVariationEntry)) continue;
                                int lod; uint variation;
                                if (!ExplainDepotHash(res.Hash, templateNameHash, newMeshSet.Lods.Count + 2, out lod, out variation)) { unexplained++; continue; }
                                if (res is ShaderBlockEntry) ((ShaderBlockEntry)res).SetHash(newNameHash, variation, lod);
                                else ((ShaderBlockMeshVariationEntry)res).SetHash(newNameHash, variation, lod);
                                rekeyed++;
                            }
                            // Every mesh-parameter block names the mesh it belongs to by the mesh root's
                            // instance GUID; the runtime pairs blocks with the mesh through it. Left at the
                            // template's, the clone's sections find no parameters and never draw.
                            int retagged = 0;
                            for (int i = 0; i < depot.ResourceCount; i++)
                            {
                                MeshParamDbBlock mp = depot.GetResource(i) as MeshParamDbBlock;
                                if (mp == null || newRootGuid == Guid.Empty || mp.MeshAssetGuid == newRootGuid) continue;
                                mp.MeshAssetGuid = newRootGuid; mp.IsModified = true; retagged++;
                            }
                            // The per-section blocks are content-hashed and the runtime caches section
                            // bindings by those hashes. A verbatim copy shares them with the template and
                            // with every other clone of it, so the first clone loaded wins and all the
                            // others draw as that mesh. Give this clone's mesh-parameter blocks and the
                            // section blocks that own them hashes salted with the clone's GUID; shared
                            // material parameter/texture holders keep theirs (same content everywhere).
                            if (newRootGuid != Guid.Empty)
                            {
                                byte[] salt = newRootGuid.ToByteArray();
                                for (int i = 0; i < depot.ResourceCount; i++)
                                {
                                    ShaderBlockResource res = depot.GetResource(i);
                                    var sec = res as ShaderStaticParamDbBlock;
                                    bool isSectionBlock = sec != null && !(res is ShaderBlockEntry) && sec.Resources.Count > 0 && sec.Resources[0] is MeshParamDbBlock;
                                    // Material parameter/texture blocks and their holders too: they are
                                    // cached by hash as well, so a clone that keeps the template's hashes
                                    // can never show its own material edits (DuplicationPlugin rehashes all).
                                    bool isHolder = sec != null && !(res is ShaderBlockEntry) && sec.Resources.Count > 0 && sec.Resources[0] is ShaderPersistentParamDbBlock;
                                    if (!(res is MeshParamDbBlock) && !isSectionBlock && !isHolder && !(res is ShaderPersistentParamDbBlock)) continue;
                                    using (var w = new NativeWriter(new MemoryStream())) { w.Write(res.Hash); w.Write(salt); res.Hash = Frosty.Hash.CityHash.Hash64(w.ToByteArray()); }
                                    res.IsModified = true; retagged++;
                                }
                            }
                            if (rekeyed > 0 || retagged > 0) App.AssetManager.ModifyRes(nb.ResRid, depot);
                            if (unexplained > 0) LastCloneNote = unexplained + " shader block entr(y/ies) kept the template's key (variation hash unknown)";
                        }
                    }
                    catch (Exception ex) { return "shader block depot duplication failed: " + ex.Message; }
                }
            }

            // 4. Point the new EBX at the new res, give the root the clone's name hash, then add it.
            try { ((dynamic)newAsset.RootObject).MeshSetResource = newResRid; }
            catch (Exception ex) { return "could not set MeshSetResource: " + ex.Message; }
            try { ((dynamic)newAsset.RootObject).NameHash = newNameHash; } catch { }
            try { newEntry = App.AssetManager.AddEbx(newName, newAsset); }
            catch (Exception ex) { return "AddEbx failed: " + ex.Message; }
            if (newEntry == null) return "AddEbx did not create " + newName;
            try { newEntry.LinkAsset(newResEntry); } catch { }
            if (tmplBlocks != null)
            {
                ResAssetEntry newBlocks = App.AssetManager.GetResEntry(newName.ToLowerInvariant() + "_mesh/blocks");
                if (newBlocks != null) { try { newEntry.LinkAsset(newBlocks); } catch { } }
            }
            return null;
        }

        // For `bundle add --with-resources`: if <entry> is a mesh (has a MeshSetResource), reports its
        // MeshSet res rid + the geometry chunk ids of its LODs, so the bundler can pull them into the
        // same bundle as the EBX. Returns false (and clears outputs) for non-mesh assets. Lets
        // FrostyCLI.Commands gather a mesh's sub-resources without referencing the MeshSet plugin types.
        public static bool TryGetLinkedResources(EbxAssetEntry entry, out ulong resRid, out List<Guid> chunkIds)
        {
            resRid = 0; chunkIds = new List<Guid>();
            if (entry == null || App.AssetManager == null) return false;
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                resRid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (resRid == 0) return false;
                ResAssetEntry re = App.AssetManager.GetResEntry(resRid);
                if (re == null) { resRid = 0; return false; }
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms != null)
                    foreach (MeshSetLod lod in ms.Lods)
                        if (lod.ChunkId != Guid.Empty && !chunkIds.Contains(lod.ChunkId)) chunkIds.Add(lod.ChunkId);
                return true;
            }
            catch { resRid = 0; chunkIds = new List<Guid>(); return false; }
        }

        // Reports a mesh's LOD/section layout: per LOD the section names (the FBX importer matches
        // FBX nodes to sections by these) and each section's material index.
        public static string DescribeSections(EbxAssetEntry entry, out int lodCount, out List<List<string>> names, out List<List<int>> materialIds)
        {
            lodCount = 0; names = new List<List<string>>(); materialIds = new List<List<int>>();
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                if (re == null) return "mesh res not found.";
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load MeshSet.";
                lodCount = ms.Lods.Count;
                foreach (MeshSetLod lod in ms.Lods)
                {
                    var n = new List<string>(); var m = new List<int>();
                    foreach (MeshSetSection s in lod.Sections) { n.Add(s.Name ?? ""); m.Add(s.MaterialId); }
                    names.Add(n); materialIds.Add(m);
                }
                return null;
            }
            catch (Exception ex) { return "describe-sections failed: " + ex.Message; }
        }

        // Whether any section of the mesh declares a vertex element with the given usage name (e.g.
        // "SubMaterialIndex"). The FBX importer throws for such elements when the FBX carries no
        // matching layer, so templates with them cannot take a foreign FBX. Null when unreadable.
        public static bool? HasVertexUsage(EbxAssetEntry entry, string usage)
        {
            if (entry == null) return null;
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return null;
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return null;
                foreach (MeshSetLod lod in ms.Lods)
                    foreach (MeshSetSection s in lod.Sections)
                    {
                        object decls = s.GeometryDeclDesc;
                        if (!(decls is System.Collections.IEnumerable)) continue;
                        foreach (object decl in (System.Collections.IEnumerable)decls)
                        {
                            if (decl == null) continue;
                            object elements = ReadMemberValue(decl, "Elements");
                            if (!(elements is System.Collections.IEnumerable)) continue;
                            foreach (object el in (System.Collections.IEnumerable)elements)
                            {
                                object u = el != null ? ReadMemberValue(el, "Usage") : null;
                                if (u != null && string.Equals(u.ToString(), usage, StringComparison.OrdinalIgnoreCase)) return true;
                            }
                        }
                    }
                return false;
            }
            catch { return null; }
        }

        private static object ReadMemberValue(object o, string name)
        {
            Type t = o.GetType();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o);
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f != null ? f.GetValue(o) : null;
        }

        // Renames a (just-cloned) mesh's sections and re-points their material indices, LOD by LOD,
        // so an FBX exported from another mesh (whose nodes carry that mesh's section names) maps onto
        // the clone. Lists shorter than a LOD leave the remaining sections untouched.
        public static string RenameSections(EbxAssetEntry entry, List<List<string>> names, List<List<int>> materialIds)
        {
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load MeshSet.";
                FieldInfo nameField = typeof(MeshSetSection).GetField("m_materialName", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo matField = typeof(MeshSetSection).GetField("m_materialId", BindingFlags.Instance | BindingFlags.NonPublic);
                if (nameField == null || matField == null) return "MeshSetSection layout unknown (m_materialName/m_materialId missing).";
                for (int l = 0; l < ms.Lods.Count && names != null && l < names.Count; l++)
                {
                    var sections = ms.Lods[l].Sections;
                    for (int s = 0; s < sections.Count && s < names[l].Count; s++)
                    {
                        nameField.SetValue(sections[s], names[l][s]);
                        if (materialIds != null && l < materialIds.Count && s < materialIds[l].Count) matField.SetValue(sections[s], materialIds[l][s]);
                    }
                }
                App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "rename-sections failed: " + ex.Message; }
        }

        // Drops trailing LODs from a (just-cloned) mesh so its LOD count matches an FBX with fewer
        // LODs (the importer requires an equal count). Only shrinking is supported: extra LODs would
        // need shader block depot entries the template does not have.
        public static string TrimLods(EbxAssetEntry entry, int lodCount)
        {
            if (entry == null) return "no mesh entry.";
            if (lodCount <= 0) return "LOD count must be positive.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load MeshSet.";
                if (ms.Lods.Count < lodCount) return "mesh has " + ms.Lods.Count + " LOD(s); cannot grow to " + lodCount + ".";
                if (ms.Lods.Count == lodCount) return null;
                while (ms.Lods.Count > lodCount) ms.Lods.RemoveAt(ms.Lods.Count - 1);
                FieldInfo idxField = typeof(MeshSetSection).GetField("m_sectionIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                if (idxField != null)
                {
                    int idx = 0;
                    foreach (MeshSetLod lod in ms.Lods)
                        foreach (MeshSetSection s in lod.Sections) idxField.SetValue(s, idx++);
                }
                App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "trim-lods failed: " + ex.Message; }
        }

        // Loads the (just-cloned) mesh's MeshSet, grows its LODs to <perLodCount> sections, and persists.
        // Lets FrostyCLI.Commands trigger section-growing without referencing the MeshSet plugin types.
        public static string GrowNewMeshSections(EbxAssetEntry entry, int perLodCount)
        {
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "new mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load new MeshSet.";
                string err = GrowSections(ms, perLodCount);
                if (err != null) return err;
                App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "grow-new-mesh failed: " + ex.Message + (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") == "1" ? Environment.NewLine + ex.ToString() : ""); }
        }

        // Grows the clone's shader block depot ("<mesh>_mesh/blocks") to one section block per MeshSet
        // section. The template's depot only carries blocks for the template's own sections; Frosty's
        // FBX importer skips sections without one (GetMeshParams returns null), and Battlefront II then
        // dereferences a null block the first time such a section is drawn (ACCESS_VIOLATION reading
        // +0xD0 at game+0x7DFB64C -- the "crashes when you get near it" of the Carbonite Chamber port).
        // Every new section gets its own MeshParamDbBlock (the importer fills in the geometry offsets)
        // and shares the parameter/texture holder of the template section whose material matches
        // <sectionMaterials>; the LOD's mesh-variation entry gets the matching RVM shader reference.
        // Call after GrowNewMeshSections and before RenameSections (the sections must still carry the
        // template's MaterialIds). Returns null when there is nothing to grow.
        public static string GrowDepotSections(EbxAssetEntry entry, List<List<int>> sectionMaterials, out int added)
        {
            added = 0;
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                if (ms == null) return "could not load the MeshSet.";
                ResAssetEntry nb = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
                if (nb == null) return null;   // no depot (not a Battlefront II style mesh): nothing to grow
                ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(nb);
                if (depot == null) return "could not load the shader block depot.";
                FieldInfo resField = typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo entField = typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic);
                if (resField == null || entField == null) return "ShaderBlockDepot layout not recognised (sbResources/sbEntries).";
                var all = (List<ShaderBlockResource>)resField.GetValue(depot);
                var entries = (List<ShaderBlockEntry>)entField.GetValue(depot);
                for (int lodIndex = 0; lodIndex < ms.Lods.Count && lodIndex < entries.Count; lodIndex++)
                {
                    ShaderBlockEntry sbe = entries[lodIndex];
                    MeshSetLod lod = ms.Lods[lodIndex];
                    int have = sbe.Resources.Count, need = lod.Sections.Count;
                    if (need <= have || have == 0) continue;
                    // The variation entry paired with this LOD entry: both are keyed by (mesh, variation, lod).
                    ShaderBlockMeshVariationEntry mv = null;
                    foreach (ShaderBlockResource r in all)
                        if (r is ShaderBlockMeshVariationEntry && r.Hash == sbe.Hash) { mv = (ShaderBlockMeshVariationEntry)r; break; }
                    // Template section blocks, by the template material of the section they serve.
                    var byMaterial = new Dictionary<int, int>();
                    for (int i = 0; i < have && i < lod.Sections.Count; i++)
                        if (!byMaterial.ContainsKey(lod.Sections[i].MaterialId)) byMaterial[lod.Sections[i].MaterialId] = i;
                    int insertAt = all.IndexOf(sbe);
                    if (insertAt < 0) return "LOD " + lodIndex + " entry is not in the depot's resource list.";
                    for (int i = have; i < need; i++)
                    {
                        int wantMat = sectionMaterials != null && lodIndex < sectionMaterials.Count && i < sectionMaterials[lodIndex].Count ? sectionMaterials[lodIndex][i] : 0;
                        int src;
                        if (!byMaterial.TryGetValue(wantMat, out src)) src = byMaterial.ContainsKey(0) ? byMaterial[0] : have - 1;
                        var tmplSec = sbe.Resources[src] as ShaderStaticParamDbBlock;
                        if (tmplSec == null || tmplSec.Resources.Count < 2) return "LOD " + lodIndex + " section block " + src + " has an unexpected layout.";
                        var tmplMp = tmplSec.Resources[0] as MeshParamDbBlock;
                        if (tmplMp == null) return "LOD " + lodIndex + " section block " + src + " has no mesh parameter block.";
                        var mp = new MeshParamDbBlock { MeshAssetGuid = tmplMp.MeshAssetGuid, LodIndex = tmplMp.LodIndex, Hash = DeriveHash(tmplMp.Hash, lodIndex, i, 1) };
                        foreach (ParameterEntry p in tmplMp.Parameters)
                            using (var r = new NativeReader(new MemoryStream(p.ToBytes()))) mp.Parameters.Add(new ParameterEntry(r));
                        var sec = new ShaderStaticParamDbBlock { Hash = DeriveHash(tmplSec.Hash, lodIndex, i, 2) };
                        sec.Resources.Add(mp);
                        for (int k = 1; k < tmplSec.Resources.Count; k++) sec.Resources.Add(tmplSec.Resources[k]);   // the shared parameter/texture holder
                        // Children sit in front of their entry: shipped depots never reference forward.
                        all.Insert(insertAt++, mp);
                        all.Insert(insertAt++, sec);
                        sbe.Resources.Add(sec);
                        if (mv != null && src < mv.RvmShaderRefGuids.Count && src < mv.RvmShaderRefInts.Count)
                        {
                            mv.RvmShaderRefGuids.Add(mv.RvmShaderRefGuids[src]);
                            mv.RvmShaderRefInts.Add(mv.RvmShaderRefInts[src]);
                        }
                        added++;
                    }
                }
                if (added == 0) return null;
                for (int i = 0; i < all.Count; i++) all[i].Index = i;
                App.AssetManager.ModifyRes(nb.ResRid, depot);
                return null;
            }
            catch (Exception ex) { return "grow-depot failed: " + ex.Message + (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") == "1" ? Environment.NewLine + ex.ToString() : ""); }
        }

        // Describes a mesh's MeshSet for diagnostics: res meta, per LOD the type, chunk (with the chunk
        // entry's size fields), buffer sizes, inline data, and every section's geometry window.
        public static string DescribeLods(EbxAssetEntry entry, out List<string> lines)
        {
            lines = new List<string>();
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                if (re == null) return "MeshSet res not found for rid 0x" + rid.ToString("X16");
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load the MeshSet.";
                byte[] meta = re.ModifiedEntry != null && re.ModifiedEntry.ResMeta != null ? re.ModifiedEntry.ResMeta : re.ResMeta;
                lines.Add("res: " + re.Name + " size=" + (re.ModifiedEntry != null ? re.ModifiedEntry.OriginalSize : re.OriginalSize) + " meta=" + (meta != null ? BitConverter.ToString(meta).Replace("-", "") : "?") + " added=" + re.IsAdded);
                lines.Add("meshset: type=" + ms.Type + " name=" + ms.FullName + " nameHash=" + ms.NameHash + " lods=" + ms.Lods.Count + " flags=" + ms.Flags + " (0x" + ((ulong)ms.Flags).ToString("X") + ")");
                try { var bb = ms.BoundingBox; lines.Add("bbox: min=(" + bb.min.x + "," + bb.min.y + "," + bb.min.z + ") max=(" + bb.max.x + "," + bb.max.y + "," + bb.max.z + ")"); } catch (Exception ex) { lines.Add("bbox: ? " + ex.Message); }
                try { dynamic root = asset.RootObject; lines.Add("ebx: LodGroup=" + FrostyCLI.Adapters.Mesh.MeshCloner.RefPath(root.LodGroup) + " EnlightenMeshLod=" + root.EnlightenMeshLod + " MeshLodMinDistance? " + SafeGet(root, "MeshLodMinDistance")); } catch (Exception ex) { lines.Add("ebx: ? " + ex.Message); }
                int i = 0;
                foreach (MeshSetLod lod in ms.Lods)
                {
                    string chunk = lod.ChunkId == Guid.Empty ? "inline" : lod.ChunkId.ToString();
                    string chunkInfo = "";
                    if (lod.ChunkId != Guid.Empty)
                    {
                        ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(lod.ChunkId);
                        chunkInfo = ce == null ? " CHUNK MISSING" : " chunkEntry(size=" + (ce.ModifiedEntry != null ? ce.ModifiedEntry.OriginalSize : ce.OriginalSize) + " logical=" + ce.LogicalOffset + "/" + ce.LogicalSize + " range=" + ce.RangeStart + "-" + ce.RangeEnd + " h32=" + ce.H32 + " firstMip=" + ce.FirstMip + " added=" + ce.IsAdded + " bundles=" + ce.Bundles.Count + ")";
                    }
                    lines.Add("lod" + i + ": type=" + lod.Type + " flags=" + lod.Flags + " (0x" + ((uint)lod.Flags).ToString("X") + ") chunk=" + chunk + chunkInfo + " vb=" + lod.VertexBufferSize + " ib=" + lod.IndexBufferSize + " indexUnit=" + lod.IndexUnitSize + " inline=" + (lod.InlineData != null ? lod.InlineData.Length : 0) + " sections=" + lod.Sections.Count);
                    try
                    {
                        if (lod.PartTransforms != null && lod.PartTransforms.Count > 0)
                        {
                            int pi = 0;
                            foreach (var pt in lod.PartTransforms)
                            {
                                if (pi >= 6) { lines.Add("   ... " + lod.PartTransforms.Count + " part transforms"); break; }
                                lines.Add("   part" + pi + ": trans=(" + pt.trans.x + "," + pt.trans.y + "," + pt.trans.z + ") right=(" + pt.right.x + "," + pt.right.y + "," + pt.right.z + ") up=(" + pt.up.x + "," + pt.up.y + "," + pt.up.z + ")");
                                pi++;
                            }
                        }
                    }
                    catch (Exception ex) { lines.Add("   parts: ? " + ex.Message); }
                    int s = 0;
                    foreach (MeshSetSection sec in lod.Sections)
                    {
                        lines.Add("   sec" + s + ": name=" + sec.Name + " mat=" + sec.MaterialId + " verts=" + sec.VertexCount + " prims=" + sec.PrimitiveCount + " voff=" + sec.VertexOffset + " sidx=" + sec.StartIndex + " stride=" + sec.VertexStride + " prim=" + sec.PrimitiveType);
                        try
                        {
                            var el = new List<string>();
                            foreach (var e in sec.GeometryDeclDesc[0].Elements) if ((byte)e.Usage != 0) el.Add(e.Usage + "@" + e.Offset + "/" + e.Format + "/s" + e.StreamIndex);
                            lines.Add("      elems: " + string.Join(" ", el));
                        }
                        catch (Exception ex) { lines.Add("      elems: ? " + ex.Message); }
                        s++;
                    }
                    i++;
                }
                return null;
            }
            catch (Exception ex) { return "describe-lods failed: " + ex.Message; }
        }

        // Marks every LOD of a (cloned) mesh as a base LOD. Shipped Battlefront II meshes flag only the
        // LODs whose chunks sit in the level bundle as IsBaseLod; the higher LODs are streamed on demand
        // from outside the bundle, and a StaticModelEntity relies on that flag to decide. A clone inherits
        // the template's flags, but every clone LOD's chunk is bundle-resident, so an entity placement of
        // the clone asks the streaming system for a chunk it cannot find and then draws with a null
        // vertex buffer (ACCESS_VIOLATION at game+0x7DFB64C the moment the higher LOD is selected, i.e.
        // when the camera gets near). Static-group instances never take that path, which is why the
        // same meshes render fine as group members. Call after the clone, before the FBX import.
        public static string MarkAllLodsResident(EbxAssetEntry entry, out int changed)
        {
            changed = 0;
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                if (ms == null) return "could not load the MeshSet.";
                FieldInfo f = typeof(MeshSetLod).GetField("m_flags", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f == null) return "MeshSetLod has no m_flags field.";
                foreach (MeshSetLod lod in ms.Lods)
                {
                    MeshLayoutFlags flags = (MeshLayoutFlags)f.GetValue(lod);
                    if ((flags & MeshLayoutFlags.IsBaseLod) != 0) continue;
                    f.SetValue(lod, flags | MeshLayoutFlags.IsBaseLod);
                    changed++;
                }
                if (changed > 0) App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "mark-resident failed: " + ex.Message; }
        }

        // One section's stored bounding box against the positions it actually draws.
        public sealed class SectionBoundsRow
        {
            public int Lod, Section;
            public string Name;
            public float[] StoredMin, StoredMax, ActualMin, ActualMax;   // Actual* null when the vertices could not be read
            public string Why;                                          // why the vertices could not be read
            // BFII ships every SECTION box as all zeros (shipped rocks, boxes, transporters alike), so a zero box is
            // "not stored", not a box to cull by; only the MeshSet box is real.
            public bool Stored
            {
                get
                {
                    if (StoredMin == null || StoredMax == null) return false;
                    for (int k = 0; k < 3; k++) if (StoredMin[k] != 0f || StoredMax[k] != 0f) return true;
                    return false;
                }
            }
            // how far the actual positions reach outside the stored box (metres; 0 = contained or not stored)
            public float Overhang
            {
                get
                {
                    if (ActualMin == null || !Stored) return 0f;
                    float o = 0f;
                    for (int k = 0; k < 3; k++) o = Math.Max(o, Math.Max(StoredMin[k] - ActualMin[k], ActualMax[k] - StoredMax[k]));
                    return o;
                }
            }
        }

        // Positions of one section, from its LOD's vertex data (stream 0 Pos element; Float3/Half3/Half4).
        // Returns false with why set when the layout cannot be read.
        private static bool SectionExtents(MeshSetSection sec, byte[] data, out float[] min, out float[] max, out string why)
        {
            min = max = null; why = null;
            if (sec.VertexCount == 0) { why = "no vertices"; return false; }
            if (data == null) { why = "no vertex data"; return false; }
            if (sec.GeometryDeclDesc == null || sec.GeometryDeclDesc.Length == 0) { why = "no geometry declaration"; return false; }
            var decl = sec.GeometryDeclDesc[0];
            int stride = (int)decl.Streams[0].VertexStride;
            int offset = 0; bool found = false; var fmt = VertexElementFormat.None;
            foreach (var elem in decl.Elements)
            {
                if (elem.Usage == VertexElementUsage.Unknown || elem.StreamIndex != 0) continue;
                if (elem.Usage == VertexElementUsage.Pos) { found = true; fmt = elem.Format; break; }
                offset += elem.Size;
            }
            if (!found) { why = "no position element in stream 0"; return false; }
            if (fmt != VertexElementFormat.Float3 && fmt != VertexElementFormat.Half3 && fmt != VertexElementFormat.Half4) { why = "position format " + fmt; return false; }
            var mn = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var mx = new[] { float.MinValue, float.MinValue, float.MinValue };
            int read = 0;
            for (uint i = 0; i < sec.VertexCount; i++)
            {
                long p = (long)sec.VertexOffset + (long)i * stride + offset;
                if (p + 12 > data.Length) { why = "vertex data shorter than the section window"; return false; }
                float x, y, z;
                if (fmt == VertexElementFormat.Float3) { x = BitConverter.ToSingle(data, (int)p); y = BitConverter.ToSingle(data, (int)p + 4); z = BitConverter.ToSingle(data, (int)p + 8); }
                else { x = HalfToFloat(BitConverter.ToUInt16(data, (int)p)); y = HalfToFloat(BitConverter.ToUInt16(data, (int)p + 2)); z = HalfToFloat(BitConverter.ToUInt16(data, (int)p + 4)); }
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) continue;
                mn[0] = Math.Min(mn[0], x); mn[1] = Math.Min(mn[1], y); mn[2] = Math.Min(mn[2], z);
                mx[0] = Math.Max(mx[0], x); mx[1] = Math.Max(mx[1], y); mx[2] = Math.Max(mx[2], z);
                read++;
            }
            if (read == 0) { why = "no readable positions"; return false; }
            min = mn; max = mx; return true;
        }

        private static readonly FieldInfo SectionBoxField = typeof(MeshSetSection).GetField("m_boundingBox", BindingFlags.Instance | BindingFlags.NonPublic);

        private static float[][] ReadBox(object box)
        {
            if (box == null) return null;
            var b = (AxisAlignedBox)box;
            return new[] { new[] { b.min.x, b.min.y, b.min.z }, new[] { b.max.x, b.max.y, b.max.z } };
        }

        // Read-only: every section's stored bounding box next to its vertices' extents, plus the MeshSet box (lod -1).
        public static string SectionBounds(EbxAssetEntry entry, out List<SectionBoundsRow> rows)
        {
            rows = new List<SectionBoundsRow>();
            if (entry == null) return "no mesh entry.";
            if (SectionBoxField == null) return "MeshSetSection has no m_boundingBox field.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                if (ms == null || ms.Lods.Count == 0) return "could not load the MeshSet.";
                FieldInfo mf = typeof(MeshSet).GetField("m_boundingBox", BindingFlags.Instance | BindingFlags.NonPublic);
                var whole = new SectionBoundsRow { Lod = -1, Section = -1, Name = "(mesh)" };
                float[][] wb = mf != null ? ReadBox(mf.GetValue(ms)) : null;
                if (wb != null) { whole.StoredMin = wb[0]; whole.StoredMax = wb[1]; }
                rows.Add(whole);
                for (int l = 0; l < ms.Lods.Count; l++)
                {
                    MeshSetLod lod = ms.Lods[l];
                    byte[] data = lod.ChunkId != Guid.Empty ? GetChunkBytes(lod.ChunkId) : lod.InlineData;
                    for (int s = 0; s < lod.Sections.Count; s++)
                    {
                        MeshSetSection sec = lod.Sections[s];
                        var row = new SectionBoundsRow { Lod = l, Section = s, Name = sec.Name };
                        float[][] sb = ReadBox(SectionBoxField.GetValue(sec));
                        if (sb != null) { row.StoredMin = sb[0]; row.StoredMax = sb[1]; }
                        float[] mn, mx; string why;
                        if (SectionExtents(sec, data, out mn, out mx, out why)) { row.ActualMin = mn; row.ActualMax = mx; }
                        else row.Why = why;
                        rows.Add(row);
                        if (l == 0 && row.ActualMin != null)
                        {
                            if (whole.ActualMin == null) { whole.ActualMin = (float[])mn.Clone(); whole.ActualMax = (float[])mx.Clone(); }
                            else for (int k = 0; k < 3; k++) { whole.ActualMin[k] = Math.Min(whole.ActualMin[k], mn[k]); whole.ActualMax[k] = Math.Max(whole.ActualMax[k], mx[k]); }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex) { return "section bounds failed: " + ex.Message; }
        }

        // Recomputes the MeshSet's bounding box from the imported LOD0 positions. Frosty's FBX importer
        // never touches it for rigid meshes, so a clone keeps its TEMPLATE's box (a 1.28 m wall for a
        // 20 m platform) and the engine culls the piece whenever that little box is off screen: the
        // room "loads" but almost nothing of it is ever drawn. Falls back to a +-<fallbackHalf> box when
        // the vertex layout cannot be read. Call after the FBX import.
        public static string RecomputeBoundingBox(EbxAssetEntry entry, float fallbackHalf, out string detail)
        {
            detail = null;
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null || ms.Lods.Count == 0) return "could not load the MeshSet.";
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                int read = 0; string why = null;
                MeshSetLod lod = ms.Lods[0];
                byte[] data = lod.ChunkId != Guid.Empty ? GetChunkBytes(lod.ChunkId) : lod.InlineData;
                if (data == null) why = "no LOD0 vertex data";
                else
                {
                    foreach (MeshSetSection sec in lod.Sections)
                    {
                        if (sec.VertexCount == 0 || sec.GeometryDeclDesc == null || sec.GeometryDeclDesc.Length == 0) continue;
                        var decl = sec.GeometryDeclDesc[0];
                        int stride = (int)decl.Streams[0].VertexStride;
                        int offset = 0; bool found = false; var fmt = VertexElementFormat.None;
                        foreach (var elem in decl.Elements)
                        {
                            if (elem.Usage == VertexElementUsage.Unknown || elem.StreamIndex != 0) continue;
                            if (elem.Usage == VertexElementUsage.Pos) { found = true; fmt = elem.Format; break; }
                            offset += elem.Size;
                        }
                        if (!found) { why = "section " + sec.Name + " has no position element in stream 0"; break; }
                        if (fmt != VertexElementFormat.Float3 && fmt != VertexElementFormat.Half3 && fmt != VertexElementFormat.Half4) { why = "position format " + fmt; break; }
                        for (uint i = 0; i < sec.VertexCount; i++)
                        {
                            long p = (long)sec.VertexOffset + (long)i * stride + offset;
                            if (p + 12 > data.Length) { why = "vertex data shorter than the section window"; break; }
                            float x, y, z;
                            if (fmt == VertexElementFormat.Float3) { x = BitConverter.ToSingle(data, (int)p); y = BitConverter.ToSingle(data, (int)p + 4); z = BitConverter.ToSingle(data, (int)p + 8); }
                            else { x = HalfToFloat(BitConverter.ToUInt16(data, (int)p)); y = HalfToFloat(BitConverter.ToUInt16(data, (int)p + 2)); z = HalfToFloat(BitConverter.ToUInt16(data, (int)p + 4)); }
                            if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) continue;
                            if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
                            if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
                            read++;
                        }
                        if (why != null) break;
                    }
                }
                if (why != null || read == 0)
                {
                    minX = minY = minZ = -fallbackHalf; maxX = maxY = maxZ = fallbackHalf;
                    detail = "fallback +-" + fallbackHalf + " box (" + (why ?? "no vertices read") + ")";
                }
                else detail = "box from " + read + " vertices: (" + minX.ToString("0.##") + "," + minY.ToString("0.##") + "," + minZ.ToString("0.##") + ")..(" + maxX.ToString("0.##") + "," + maxY.ToString("0.##") + "," + maxZ.ToString("0.##") + ")";
                FieldInfo f = typeof(MeshSet).GetField("m_boundingBox", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f == null) return "MeshSet has no m_boundingBox field.";
                var box = new AxisAlignedBox();
                box.min = new Vec3 { x = minX, y = minY, z = minZ };
                box.max = new Vec3 { x = maxX, y = maxY, z = maxZ };
                f.SetValue(ms, box);
                App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "bounding box failed: " + ex.Message; }
        }

        // Translates every vertex position of every LOD by (dx,dy,dz) in mesh space, and shifts the MeshSet
        // bounding box to match. Writes the geometry chunks in place, so run it on a clone (mesh clone) rather
        // than a shipped mesh shared by other assets. A skinned mesh moves the same way in its bind space, so a
        // rigid weapon moves by the offset in its own frame. Stream-major vertex layout, as FaeMeshExporter reads it.
        public static string OffsetVertices(EbxAssetEntry entry, float dx, float dy, float dz, out int moved)
        {
            return OffsetVertices(entry, dx, dy, dz, false, out moved);
        }

        // Shifts a culling box in place (AxisAlignedBox is a struct: returns the moved copy).
        private static AxisAlignedBox Shift(AxisAlignedBox b, float dx, float dy, float dz)
        {
            b.min = new Vec3 { x = b.min.x + dx, y = b.min.y + dy, z = b.min.z + dz };
            b.max = new Vec3 { x = b.max.x + dx, y = b.max.y + dy, z = b.max.z + dz };
            return b;
        }

        private static AxisAlignedBox Xform(AxisAlignedBox b, float k, float[] c, float dx, float dy, float dz)
        {
            b.min = new Vec3 { x = c[0] + k * (b.min.x - c[0]) + dx, y = c[1] + k * (b.min.y - c[1]) + dy, z = c[2] + k * (b.min.z - c[2]) + dz };
            b.max = new Vec3 { x = c[0] + k * (b.max.x - c[0]) + dx, y = c[1] + k * (b.max.y - c[1]) + dy, z = c[2] + k * (b.max.z - c[2]) + dz };
            return b;
        }

        // boxesOnly: move the culling boxes but not the vertices (repairs a clone offset before the boxes were handled).
        // Skinned meshes are culled with per-bone boxes in MODEL space (verified: t21_mesh1p bone 6's box equals the
        // extent of its vertices), so vertices moved without them leave the raised parts outside their box and the
        // renderer drops them -- the partly/fully invisible guns of V8.9.
        public static string OffsetVertices(EbxAssetEntry entry, float dx, float dy, float dz, bool boxesOnly, out int moved)
        {
            return TransformVertices(entry, 1f, new float[3], new[] { dx, dy, dz }, boxesOnly, out moved);
        }

        // p' = c + k (p - c) + d for every vertex position, the MeshSet box, and the per-bone/section culling boxes.
        // Scaling about the eye point (k > 1) keeps the first-person image identical while pushing the geometry past a
        // near clip plane: every point slides out along its own line of sight.
        public static string TransformVertices(EbxAssetEntry entry, float k, float[] cc, float[] dd, bool boxesOnly, out int moved)
        {
            float dx = dd[0], dy = dd[1], dz = dd[2];
            moved = 0;
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null || ms.Lods.Count == 0) return "could not load the MeshSet.";
                FieldInfo secBox = typeof(MeshSetSection).GetField("m_boundingBox", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (MeshSetLod lod in ms.Lods)
                {
                    if (secBox != null)
                        foreach (MeshSetSection sec in lod.Sections)
                        {
                            var sb = (AxisAlignedBox)secBox.GetValue(sec);
                            if (sb.min.x != 0 || sb.min.y != 0 || sb.min.z != 0 || sb.max.x != 0 || sb.max.y != 0 || sb.max.z != 0)
                                secBox.SetValue(sec, Xform(sb, k, cc, dx, dy, dz));
                        }
                    if (boxesOnly) continue;
                    byte[] data = lod.ChunkId != Guid.Empty ? GetChunkBytes(lod.ChunkId) : lod.InlineData;
                    if (data == null) return "LOD " + lod.ShortName + " has no vertex data.";
                    data = (byte[])data.Clone();
                    foreach (MeshSetSection sec in lod.Sections)
                    {
                        if (sec.VertexCount == 0 || sec.GeometryDeclDesc == null || sec.GeometryDeclDesc.Length == 0) continue;
                        GeometryDeclarationDesc decl = sec.GeometryDeclDesc[0];
                        var streams = decl.Streams;
                        int[] streamBase = new int[streams.Length];
                        int acc = 0;
                        for (int s = 0; s < streams.Length; s++) { streamBase[s] = acc; acc += streams[s].VertexStride; }
                        foreach (GeometryDeclarationDesc.Element e in decl.Elements)
                        {
                            if (e.Usage != VertexElementUsage.Pos) continue;
                            int s = e.StreamIndex;
                            if (s >= streams.Length || streams[s].VertexStride == 0) continue;
                            bool full = e.Format == VertexElementFormat.Float3 || e.Format == VertexElementFormat.Float4;
                            bool half = e.Format == VertexElementFormat.Half3 || e.Format == VertexElementFormat.Half4;
                            if (!full && !half) return "section " + sec.Name + ": position format " + e.Format + " not supported";
                            for (long j = 0; j < sec.VertexCount; j++)
                            {
                                long p = sec.VertexOffset + (long)streamBase[s] * sec.VertexCount + j * streams[s].VertexStride + e.Offset;
                                if (p + (full ? 12 : 6) > data.Length) return "section " + sec.Name + ": vertex data shorter than its window";
                                float[] d = { dx, dy, dz };
                                for (int c = 0; c < 3; c++)
                                {
                                    if (full)
                                    {
                                        int at = (int)p + c * 4;
                                        Buffer.BlockCopy(BitConverter.GetBytes(cc[c] + k * (BitConverter.ToSingle(data, at) - cc[c]) + d[c]), 0, data, at, 4);
                                    }
                                    else
                                    {
                                        int at = (int)p + c * 2;
                                        ushort h = FloatToHalf(cc[c] + k * (HalfToFloat(BitConverter.ToUInt16(data, at)) - cc[c]) + d[c]);
                                        data[at] = (byte)h; data[at + 1] = (byte)(h >> 8);
                                    }
                                }
                                moved++;
                            }
                        }
                    }
                    if (lod.ChunkId != Guid.Empty)
                    {
                        if (!App.AssetManager.ModifyChunk(lod.ChunkId, data)) return "ModifyChunk failed for LOD " + lod.ShortName;
                    }
                    else lod.SetInlineData(data);
                }
                FieldInfo f = typeof(MeshSet).GetField("m_boundingBox", BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null && !boxesOnly) f.SetValue(ms, Xform((AxisAlignedBox)f.GetValue(ms), k, cc, dx, dy, dz));
                List<ushort> boneIdx;
                List<AxisAlignedBox> bones = BoneBoxes(ms, out boneIdx);
                if (bones != null)
                    for (int i = 0; i < bones.Count; i++) bones[i] = Xform(bones[i], k, cc, dx, dy, dz);
                App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "offset failed: " + ex.Message; }
        }

        // The MeshSet's per-bone culling boxes (skinned meshes), by bone index. Null when the layout differs.
        internal static List<AxisAlignedBox> BoneBoxes(MeshSet ms, out List<ushort> boneIndices)
        {
            boneIndices = null;
            FieldInfo fb = typeof(MeshSet).GetField("m_boneBoundingBoxes", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo fi = typeof(MeshSet).GetField("m_boneIndices", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null) boneIndices = fi.GetValue(ms) as List<ushort>;
            return fb == null ? null : fb.GetValue(ms) as List<AxisAlignedBox>;
        }

        // Debug: the overall box, each bone's stored box, and the model-space extent of the LOD0 vertices whose
        // strongest influence is that bone (stream-major layout, as OffsetVertices).
        public static string DescribeBones(EbxAssetEntry entry, out List<string> lines)
        {
            lines = new List<string>();
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                FieldInfo f = typeof(MeshSet).GetField("m_boundingBox", BindingFlags.Instance | BindingFlags.NonPublic);
                var bb = (AxisAlignedBox)f.GetValue(ms);
                lines.Add("box " + Fmt(bb));
                List<ushort> idx; var boxes = BoneBoxes(ms, out idx);
                lines.Add("boneIndices " + (idx == null ? "null" : string.Join(",", idx)));
                var ext = new Dictionary<int, float[]>();
                MeshSetLod lod = ms.Lods[0];
                byte[] data = lod.ChunkId != Guid.Empty ? GetChunkBytes(lod.ChunkId) : lod.InlineData;
                foreach (MeshSetSection sec in lod.Sections)
                {
                    if (sec.VertexCount == 0 || sec.GeometryDeclDesc == null || sec.GeometryDeclDesc.Length == 0) continue;
                    var decl = sec.GeometryDeclDesc[0]; var streams = decl.Streams;
                    int[] sb = new int[streams.Length]; int acc = 0;
                    for (int s = 0; s < streams.Length; s++) { sb[s] = acc; acc += streams[s].VertexStride; }
                    GeometryDeclarationDesc.Element pe = default(GeometryDeclarationDesc.Element), be = default(GeometryDeclarationDesc.Element), we = default(GeometryDeclarationDesc.Element);
                    bool hp = false, hb = false, hw = false;
                    foreach (var e in decl.Elements)
                    {
                        if (e.Usage == VertexElementUsage.Pos) { pe = e; hp = true; }
                        else if (e.Usage == VertexElementUsage.BoneIndices) { be = e; hb = true; }
                        else if (e.Usage == VertexElementUsage.BoneWeights) { we = e; hw = true; }
                    }
                    if (!hp) continue;
                    for (long j = 0; j < sec.VertexCount; j++)
                    {
                        long p = sec.VertexOffset + (long)sb[pe.StreamIndex] * sec.VertexCount + j * streams[pe.StreamIndex].VertexStride + pe.Offset;
                        bool full = pe.Format == VertexElementFormat.Float3 || pe.Format == VertexElementFormat.Float4;
                        float[] v = new float[3];
                        for (int c = 0; c < 3; c++) v[c] = full ? BitConverter.ToSingle(data, (int)p + c * 4) : HalfToFloat(BitConverter.ToUInt16(data, (int)p + c * 2));
                        int bone = -1;
                        if (hb)
                        {
                            long q = sec.VertexOffset + (long)sb[be.StreamIndex] * sec.VertexCount + j * streams[be.StreamIndex].VertexStride + be.Offset;
                            bool wide = be.Format == VertexElementFormat.UShort4 || be.Format == VertexElementFormat.UShort4N;
                            int best = 0, bw = -1;
                            for (int k = 0; k < 4; k++)
                            {
                                int w = 255;
                                if (hw) { long r = sec.VertexOffset + (long)sb[we.StreamIndex] * sec.VertexCount + j * streams[we.StreamIndex].VertexStride + we.Offset; w = data[r + k]; }
                                if (w > bw) { bw = w; best = k; }
                            }
                            int local = wide ? BitConverter.ToUInt16(data, (int)q + best * 2) : data[q + best];
                            bone = sec.BoneList != null && local < sec.BoneList.Count ? sec.BoneList[local] : local;
                        }
                        float[] x;
                        if (!ext.TryGetValue(bone, out x)) { x = new[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue }; ext[bone] = x; }
                        for (int c = 0; c < 3; c++) { x[c] = Math.Min(x[c], v[c]); x[c + 3] = Math.Max(x[c + 3], v[c]); }
                    }
                }
                for (int i = 0; boxes != null && i < boxes.Count; i++)
                {
                    int bone = idx != null && i < idx.Count ? idx[i] : i;
                    float[] x; string vx = ext.TryGetValue(bone, out x) ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.###},{1:0.###},{2:0.###})..({3:0.###},{4:0.###},{5:0.###})", x[0], x[1], x[2], x[3], x[4], x[5]) : "no verts";
                    lines.Add("bone[" + i + "]=" + bone + " box " + Fmt(boxes[i]) + "  verts " + vx);
                }
                foreach (var kv in ext) if (idx == null || !idx.Contains((ushort)kv.Key)) lines.Add("verts of bone " + kv.Key + " (not in boneIndices)");
                return null;
            }
            catch (Exception ex) { return "describe bones failed: " + ex.Message; }
        }

        // Debug: the mesh's shader block depot as the runtime pairs it -- per-LOD entries (is the key this mesh's
        // CityHash(nameHash, 0, lod)?) and the mesh-parameter blocks (MeshAssetGuid = this mesh's root guid?).
        public static string DescribeDepot(EbxAssetEntry entry, out List<string> lines)
        {
            lines = new List<string>();
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                Guid root = Guid.Empty;
                try { root = ((dynamic)asset.RootObject).GetInstanceGuid().ExportedGuid; } catch { }
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                uint nh = ms.NameHash;
                lines.Add("mesh " + entry.Name + " root " + root + " meshset.NameHash " + nh.ToString("X8") + " FullName " + ms.FullName + " lods " + ms.Lods.Count);
                for (int l = 0; l < ms.Lods.Count; l++)
                {
                    lines.Add("  lod" + l + " name '" + ms.Lods[l].Name + "' sections " + ms.Lods[l].Sections.Count);
                    ChunkAssetEntry ce = ms.Lods[l].ChunkId != Guid.Empty ? App.AssetManager.GetChunkEntry(ms.Lods[l].ChunkId) : null;
                    if (ce != null)
                    {
                        var me = ce.ModifiedEntry;
                        lines.Add("    chunk " + ce.Id + " added=" + ce.IsAdded + " base(logical " + ce.LogicalOffset + "/" + ce.LogicalSize + " range " + ce.RangeStart + "-" + ce.RangeEnd + " firstMip " + ce.FirstMip + " h32 " + ce.H32 + " bundles " + ce.Bundles.Count + " added-bundles " + ce.AddedBundles.Count + ")"
                            + (me == null ? " no modified entry" : " modified(logical " + me.LogicalOffset + "/" + me.LogicalSize + " range " + me.RangeStart + "-" + me.RangeEnd + " firstMip " + me.FirstMip + " h32 " + me.H32 + " data " + (me.Data == null ? -1 : me.Data.Length) + ")"));
                    }
                }
                ResAssetEntry de = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
                if (de == null) { lines.Add("  NO DEPOT " + entry.Name.ToLowerInvariant() + "_mesh/blocks"); return null; }
                ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(de);
                lines.Add("  depot " + de.Name + " resources " + depot.ResourceCount + " added=" + de.IsAdded + " modified=" + de.IsModified);
                for (int i = 0; i < depot.ResourceCount; i++)
                {
                    ShaderBlockResource r = depot.GetResource(i);
                    if (r is ShaderBlockEntry || r is ShaderBlockMeshVariationEntry)
                    {
                        int lod; uint v;
                        bool mine = ExplainDepotHash(r.Hash, nh, ms.Lods.Count + 2, out lod, out v);
                        lines.Add("  [" + i + "] " + r.GetType().Name + " hash " + r.Hash.ToString("X16") + (mine ? " = this mesh lod" + lod : " (not this mesh, variation?)"));
                    }
                    else if (r is MeshParamDbBlock)
                    {
                        var mp = (MeshParamDbBlock)r;
                        lines.Add("  [" + i + "] MeshParamDbBlock lod " + mp.LodIndex + " guid " + mp.MeshAssetGuid + (mp.MeshAssetGuid == root ? " (this mesh)" : " (OTHER)"));
                    }
                }
                return null;
            }
            catch (Exception ex) { return "describe depot failed: " + ex.Message; }
        }

        private static string Fmt(AxisAlignedBox b) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "({0:0.###},{1:0.###},{2:0.###})..({3:0.###},{4:0.###},{5:0.###})", b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z);

        private static ushort FloatToHalf(float value)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            int sign = (bits >> 16) & 0x8000;
            int exp = ((bits >> 23) & 0xFF) - 127 + 15;
            int mant = bits & 0x7FFFFF;
            if (exp <= 0)
            {
                if (exp < -10) return (ushort)sign;
                mant |= 0x800000;
                int shift = 14 - exp;
                int hm = mant >> shift;
                if (((mant >> (shift - 1)) & 1) != 0) hm++;
                return (ushort)(sign | hm);
            }
            if (exp >= 31) return (ushort)(sign | 0x7C00);
            int r = sign | (exp << 10) | (mant >> 13);
            if ((mant & 0x1000) != 0) r++; // round to nearest
            return (ushort)r;
        }

        private static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 0x1F, mant = h & 0x3FF;
            float v;
            if (exp == 0) v = (float)(mant * Math.Pow(2, -24));
            else if (exp == 31) v = mant == 0 ? float.PositiveInfinity : float.NaN;
            else v = (float)((1 + mant / 1024.0) * Math.Pow(2, exp - 15));
            return sign == 1 ? -v : v;
        }

        private static string SafeGet(dynamic root, string name)
        {
            try { object v = root.GetType().GetProperty(name)?.GetValue(root); return v == null ? "-" : v.ToString(); } catch { return "-"; }
        }
        internal static string RefPath(object pr)
        {
            try { dynamic p = pr; EbxAssetEntry e = App.AssetManager.GetEbxEntry((Guid)p.External.FileGuid); return e != null ? e.Name : p.External.FileGuid.ToString(); } catch { return "?"; }
        }

        // Re-types a composite MeshSet as rigid in the asset manager's working copy (ModifyRes, not
        // saved), so whichever handler exports it next writes plain geometry: no PART skeleton, no
        // skins, no part move. Returns null when the mesh is not composite.
        /// <summary>
        /// Hides the render sections of a composite mesh whose vertices all belong to <paramref name="hiddenParts"/>: no
        /// primitives and no name, so the FBX exporter skips them (IsSectionRenderable) and DescribeSections reports them
        /// unnamed. In memory only, like PresentAsRigid. A 2015 destructible prop keeps every health state's geometry in
        /// one composite -- BoxMedium_02 is the crate (part 0) and its "ruin" (part 1), BoxMedium_01 adds intact and
        /// damaged glass -- and shows a state's part only while that state is active; exported --as-rigid, all states
        /// would draw at once. Composite vertices carry their part in BoneIndices[0]. Sections spanning a hidden and a
        /// visible part are left alone and reported in <paramref name="mixed"/>.
        /// </summary>
        public static string HideCompositeParts(EbxAssetEntry entry, ICollection<int> hiddenParts, out int hidden, out List<string> mixed)
        {
            hidden = 0; mixed = new List<string>();
            if (entry == null) return "no mesh entry.";
            if (hiddenParts == null || hiddenParts.Count == 0) return null;
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load the MeshSet.";
                if (ms.Type != MeshType.MeshType_Composite) return null;
                FieldInfo nameField = typeof(MeshSetSection).GetField("m_materialName", BindingFlags.Instance | BindingFlags.NonPublic);
                if (nameField == null) return "MeshSetSection layout not recognised (m_materialName).";
                foreach (MeshSetLod lod in ms.Lods)
                {
                    byte[] data;
                    using (Stream st = lod.ChunkId != Guid.Empty ? App.AssetManager.GetChunk(App.AssetManager.GetChunkEntry(lod.ChunkId)) : new MemoryStream(lod.InlineData))
                    using (var buf = new MemoryStream()) { st.CopyTo(buf); data = buf.ToArray(); }
                    foreach (MeshSetSection sec in lod.Sections)
                    {
                        if (string.IsNullOrEmpty(sec.Name) || sec.PrimitiveCount == 0) continue;
                        HashSet<int> parts = SectionParts(data, sec);
                        if (parts == null || parts.Count == 0) continue;
                        int inHidden = 0;
                        foreach (int part in parts) if (hiddenParts.Contains(part)) inHidden++;
                        if (inHidden == parts.Count)
                        {
                            sec.PrimitiveCount = 0;
                            nameField.SetValue(sec, "");
                            hidden++;
                        }
                        else if (inHidden > 0) mixed.Add(lod.ShortName + "/" + sec.Name);
                    }
                }
                if (hidden > 0) App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "hide-composite-parts failed: " + ex.Message; }
        }

        // The distinct part indices (BoneIndices[0]) of a composite section's vertices; null without a BoneIndices element.
        private static HashSet<int> SectionParts(byte[] chunk, MeshSetSection sec)
        {
            GeometryDeclarationDesc decl = sec.GeometryDeclDesc[0];
            var streams = decl.Streams;
            int[] streamBase = new int[streams.Length];
            int acc = 0;
            for (int s = 0; s < streams.Length; s++) { streamBase[s] = acc; acc += streams[s].VertexStride; }
            foreach (GeometryDeclarationDesc.Element e in decl.Elements)
            {
                if (e.Usage != VertexElementUsage.BoneIndices) continue;
                int s = e.StreamIndex;
                if (s >= streams.Length || streams[s].VertexStride == 0) return null;
                bool wide = e.Format == VertexElementFormat.UShort4 || e.Format == VertexElementFormat.UShort4N;
                var parts = new HashSet<int>();
                for (long j = 0; j < sec.VertexCount; j++)
                {
                    long pos = sec.VertexOffset + (long)streamBase[s] * sec.VertexCount + j * streams[s].VertexStride + e.Offset;
                    if (pos + (wide ? 2 : 1) > chunk.Length) return null;
                    parts.Add(wide ? BitConverter.ToUInt16(chunk, (int)pos) : chunk[pos]);
                }
                return parts;
            }
            return null;
        }

        public static string PresentAsRigid(EbxAssetEntry entry)
        {
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load the MeshSet.";
                if (ms.Type != MeshType.MeshType_Composite) return null;
                ms.Type = MeshType.MeshType_Rigid;
                foreach (MeshSetLod lod in ms.Lods) lod.Type = MeshType.MeshType_Rigid;
                App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "present-as-rigid failed: " + ex.Message; }
        }

        // Drops the depot's LOD entries (and the section blocks only they use) beyond <lodCount>, so a
        // mesh trimmed to fewer LODs than its template does not advertise shader blocks for LODs it
        // no longer has. With the depot bound to the clone (MeshAssetGuid), the loader walks the
        // entries and indexes the MeshSet's LOD table by entry: one entry too many reads a null LOD
        // (ACCESS_VIOLATION at game+0x1520BBF before the world comes up).
        public static string TrimDepotLods(EbxAssetEntry entry, int lodCount, out int removed)
        {
            removed = 0;
            if (entry == null) return "no mesh entry.";
            try
            {
                ResAssetEntry nb = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
                if (nb == null) return null;
                ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(nb);
                if (depot == null) return "could not load the shader block depot.";
                FieldInfo resField = typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo entField = typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic);
                if (resField == null || entField == null) return "ShaderBlockDepot layout not recognised.";
                var all = (List<ShaderBlockResource>)resField.GetValue(depot);
                var entries = (List<ShaderBlockEntry>)entField.GetValue(depot);
                if (entries.Count <= lodCount) return null;
                var doomed = new HashSet<ShaderBlockResource>();
                for (int lod = lodCount; lod < entries.Count; lod++)
                {
                    ShaderBlockEntry sbe = entries[lod];
                    doomed.Add(sbe);
                    foreach (ShaderBlockResource r in all)
                        if (r is ShaderBlockMeshVariationEntry && r.Hash == sbe.Hash) doomed.Add(r);
                    foreach (ShaderBlockResource sec in sbe.Resources)
                    {
                        var sb = sec as ShaderStaticParamDbBlock;
                        if (sb == null) continue;
                        doomed.Add(sb);
                        if (sb.Resources.Count > 0 && sb.Resources[0] is MeshParamDbBlock) doomed.Add(sb.Resources[0]);   // the per-section mesh block; the holder may be shared with kept LODs
                    }
                }
                // Anything a kept entry still reaches must survive.
                var keep = new HashSet<ShaderBlockResource>();
                Action<ShaderBlockResource> mark = null;
                mark = r => { if (r == null || !keep.Add(r)) return; var s = r as ShaderStaticParamDbBlock; if (s != null) foreach (var c in s.Resources) mark(c); };
                for (int lod = 0; lod < lodCount && lod < entries.Count; lod++) { mark(entries[lod]); foreach (ShaderBlockResource r in all) if (r is ShaderBlockMeshVariationEntry && r.Hash == entries[lod].Hash) keep.Add(r); }
                doomed.ExceptWith(keep);
                entries.RemoveRange(lodCount, entries.Count - lodCount);
                removed = all.RemoveAll(r => doomed.Contains(r));
                for (int i = 0; i < all.Count; i++) all[i].Index = i;
                App.AssetManager.ModifyRes(nb.ResRid, depot);
                return null;
            }
            catch (Exception ex) { return "trim-depot failed: " + ex.Message; }
        }

        // Grows every LOD's *real* (named, drawable) sections to <namedCount>, keeping the unnamed
        // depth/shadow sections at the end, and grows the depot in lockstep. A shipped LOD is laid out
        // [named material sections..., unnamed shadow/depth sections]; only the named ones are drawn
        // through the opaque subset category. Cloning the LAST section (the shadow one, position-only
        // stride 8) gave sections that could not be drawn at all -- most of a big wall's geometry
        // vanished, and forcing them into the opaque category drew spikes. New sections are clones of
        // the last named section, inserted before the shadow sections, added to its categories, and
        // get their own depot block at the same position. <sectionMaterials> lists the source's
        // material index per named section per LOD (used to pick the depot holder).
        public static string GrowRealSections(EbxAssetEntry entry, int namedCount, List<List<int>> sectionMaterials, out int grown)
        {
            grown = 0;
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load the MeshSet.";
                ResAssetEntry nb = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
                ShaderBlockDepot depot = nb != null ? App.AssetManager.GetResAs<ShaderBlockDepot>(nb) : null;
                List<ShaderBlockResource> all = null; List<ShaderBlockEntry> entries = null;
                if (depot != null)
                {
                    all = (List<ShaderBlockResource>)typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot);
                    entries = (List<ShaderBlockEntry>)typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot);
                }
                bool depotChanged = false;
                for (int lodIndex = 0; lodIndex < ms.Lods.Count; lodIndex++)
                {
                    MeshSetLod lod = ms.Lods[lodIndex];
                    var sections = lod.Sections;
                    int real = 0;
                    while (real < sections.Count && !string.IsNullOrEmpty(sections[real].Name)) real++;
                    if (real == 0 || real >= namedCount) continue;
                    ShaderBlockEntry sbe = entries != null && lodIndex < entries.Count ? entries[lodIndex] : null;
                    ShaderBlockMeshVariationEntry mv = null;
                    if (sbe != null) foreach (ShaderBlockResource r in all) if (r is ShaderBlockMeshVariationEntry && r.Hash == sbe.Hash) { mv = (ShaderBlockMeshVariationEntry)r; break; }
                    while (real < namedCount)
                    {
                        int at = real;
                        // Prefer the template section whose material matches the source section's.
                        int src = real - 1;
                        int wantMat = sectionMaterials != null && lodIndex < sectionMaterials.Count && at < sectionMaterials[lodIndex].Count ? sectionMaterials[lodIndex][at] : -1;
                        if (wantMat >= 0) for (int i = 0; i < real; i++) if (sections[i].MaterialId == wantMat) { src = i; break; }
                        MeshSetSection s = CloneSection(sections[src]);
                        s.VertexCount = 0; s.PrimitiveCount = 0; s.VertexOffset = 0; s.StartIndex = 0;
                        sections.Insert(at, s);
                        lod.SectionCount = sections.Count;
                        var cats = lod.CategorySubsetIndices;
                        if (cats != null)
                            foreach (List<byte> cat in cats)
                            {
                                if (cat == null) continue;
                                for (int k = 0; k < cat.Count; k++) if (cat[k] >= at) cat[k] = (byte)(cat[k] + 1);
                                if (cat.Contains((byte)src) && !cat.Contains((byte)at)) cat.Add((byte)at);
                            }
                        if (sbe != null && src < sbe.Resources.Count)
                        {
                            var tmplSec = sbe.Resources[src] as ShaderStaticParamDbBlock;
                            var tmplMp = tmplSec != null && tmplSec.Resources.Count > 0 ? tmplSec.Resources[0] as MeshParamDbBlock : null;
                            if (tmplSec == null || tmplMp == null) return "LOD " + lodIndex + " section block " + src + " has an unexpected layout.";
                            var mp = new MeshParamDbBlock { MeshAssetGuid = tmplMp.MeshAssetGuid, LodIndex = tmplMp.LodIndex, Hash = DeriveHash(tmplMp.Hash, lodIndex, at, 1) };
                            foreach (ParameterEntry p in tmplMp.Parameters)
                                using (var r = new NativeReader(new MemoryStream(p.ToBytes()))) mp.Parameters.Add(new ParameterEntry(r));
                            var sec = new ShaderStaticParamDbBlock { Hash = DeriveHash(tmplSec.Hash, lodIndex, at, 2) };
                            sec.Resources.Add(mp);
                            for (int k = 1; k < tmplSec.Resources.Count; k++) sec.Resources.Add(tmplSec.Resources[k]);
                            int insertAt = all.IndexOf(sbe);
                            all.Insert(insertAt++, mp); all.Insert(insertAt, sec);
                            sbe.Resources.Insert(at, sec);
                            if (mv != null && src < mv.RvmShaderRefGuids.Count) { mv.RvmShaderRefGuids.Insert(at, mv.RvmShaderRefGuids[src]); mv.RvmShaderRefInts.Insert(at, mv.RvmShaderRefInts[src]); }
                            depotChanged = true;
                        }
                        real++; grown++;
                    }
                }
                if (grown == 0) return null;
                FieldInfo idxField = typeof(MeshSetSection).GetField("m_sectionIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                if (idxField != null) { int idx = 0; foreach (MeshSetLod lod in ms.Lods) foreach (MeshSetSection s in lod.Sections) idxField.SetValue(s, idx++); }
                App.AssetManager.ModifyRes(rid, ms);
                if (depotChanged) { for (int i = 0; i < all.Count; i++) all[i].Index = i; App.AssetManager.ModifyRes(nb.ResRid, depot); }
                return null;
            }
            catch (Exception ex) { return "grow-real-sections failed: " + ex.Message + (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") == "1" ? Environment.NewLine + ex.ToString() : ""); }
        }

        // Renames only the named (drawable) sections of each LOD to the source's named sections, in
        // order, with their material index; unnamed shadow/depth sections keep their empty name.
        public static string RenameNamedSections(EbxAssetEntry entry, List<List<string>> names, List<List<int>> materialIds)
        {
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(re);
                if (ms == null) return "could not load MeshSet.";
                FieldInfo nameField = typeof(MeshSetSection).GetField("m_materialName", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo matField = typeof(MeshSetSection).GetField("m_materialId", BindingFlags.Instance | BindingFlags.NonPublic);
                if (nameField == null || matField == null) return "MeshSetSection layout unknown (m_materialName/m_materialId missing).";
                for (int l = 0; l < ms.Lods.Count && names != null && l < names.Count; l++)
                {
                    var sections = ms.Lods[l].Sections;
                    var srcNames = new List<string>(); var srcMats = new List<int>();
                    for (int s = 0; s < names[l].Count; s++)
                        if (!string.IsNullOrEmpty(names[l][s])) { srcNames.Add(names[l][s]); srcMats.Add(materialIds != null && l < materialIds.Count && s < materialIds[l].Count ? materialIds[l][s] : 0); }
                    int real = 0;
                    while (real < sections.Count && !string.IsNullOrEmpty(sections[real].Name)) real++;
                    for (int s = 0; s < real && s < srcNames.Count; s++)
                    {
                        nameField.SetValue(sections[s], srcNames[s]);
                        matField.SetValue(sections[s], srcMats[s]);
                    }
                }
                App.AssetManager.ModifyRes(rid, ms);
                return null;
            }
            catch (Exception ex) { return "rename-sections failed: " + ex.Message; }
        }

        // Grows a mesh to <count> LODs by copying its last LOD record (its own chunk copy or inline data, lists deep-copied,
        // names renumbered) and adding the matching depot LOD entry + variation entry, keyed CityHash(meshNameHash, 0, lod)
        // like every shipped entry. The new LODs hold the last LOD's geometry until the caller writes their own (the geometry
        // port rebuilds every LOD's sections and depot blocks), so this is only a structural step.
        public static string GrowLods(EbxAssetEntry entry, int count)
        {
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                if (ms == null || ms.Lods.Count == 0) return "could not load the MeshSet.";
                if (ms.Lods.Count >= count) return null;
                ResAssetEntry nb = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
                ShaderBlockDepot depot = nb != null ? App.AssetManager.GetResAs<ShaderBlockDepot>(nb) : null;
                List<ShaderBlockResource> all = depot != null ? (List<ShaderBlockResource>)typeof(ShaderBlockDepot).GetField("sbResources", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot) : null;
                List<ShaderBlockEntry> entries = depot != null ? (List<ShaderBlockEntry>)typeof(ShaderBlockDepot).GetField("sbEntries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(depot) : null;
                Type lt = typeof(MeshSetLod);
                while (ms.Lods.Count < count)
                {
                    MeshSetLod src = ms.Lods[ms.Lods.Count - 1];
                    int n = ms.Lods.Count;
                    var lod = (MeshSetLod)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(lt);
                    foreach (FieldInfo f in lt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object v = f.GetValue(src);
                        if (v is List<MeshSetSection> secs) { var l = new List<MeshSetSection>(); foreach (MeshSetSection sec in secs) l.Add(CloneSection(sec)); v = l; }
                        else if (v is List<List<byte>> cats) { var l = new List<List<byte>>(); foreach (List<byte> c in cats) l.Add(new List<byte>(c)); v = l; }
                        else if (v is byte[] b) v = (byte[])b.Clone();
                        else if (v is System.Collections.IList il && v.GetType().IsGenericType)
                        {
                            var copy = (System.Collections.IList)Activator.CreateInstance(v.GetType());
                            foreach (object o in il) copy.Add(o);
                            v = copy;
                        }
                        f.SetValue(lod, v);
                    }
                    // names end in the LOD index; the LOD index is read from the short name's last character
                    foreach (string fn in new[] { "m_shortName", "m_name", "m_shaderDebugName" })
                    {
                        FieldInfo f = lt.GetField(fn, BindingFlags.Instance | BindingFlags.NonPublic);
                        string v = f != null ? f.GetValue(lod) as string : null;
                        if (v == null || v.Length == 0 || !char.IsDigit(v[v.Length - 1])) continue;
                        int k = v.Length; while (k > 0 && char.IsDigit(v[k - 1])) k--;
                        f.SetValue(lod, v.Substring(0, k) + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    FieldInfo nameF = lt.GetField("m_name", BindingFlags.Instance | BindingFlags.NonPublic), hashF = lt.GetField("m_nameHash", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (nameF != null && hashF != null) hashF.SetValue(lod, (uint)Frosty.Hash.Fnv1.HashString((string)nameF.GetValue(lod)));
                    FieldInfo idxF = lt.GetField("m_lodIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (idxF != null) idxF.SetValue(lod, n);
                    if (src.ChunkId != Guid.Empty)
                    {
                        byte[] data = GetChunkBytes(src.ChunkId);
                        if (data == null) return "LOD " + (n - 1) + " chunk not found";
                        lod.ChunkId = App.AssetManager.AddChunk(data, null);
                    }
                    ms.Lods.Add(lod);
                    if (entries != null)
                    {
                        ulong h;
                        using (var mem = new MemoryStream()) using (var w = new BinaryWriter(mem)) { w.Write(ms.NameHash); w.Write((uint)0); w.Write(n); w.Flush(); h = Frosty.Hash.CityHash.Hash64(mem.ToArray()); }
                        var e = new ShaderBlockEntry { Hash = h, IsModified = true };
                        var mv = new ShaderBlockMeshVariationEntry { Hash = h, IsModified = true };
                        all.Add(e); all.Add(mv); entries.Add(e);
                    }
                }
                FieldInfo idxField = typeof(MeshSetSection).GetField("m_sectionIndex", BindingFlags.Instance | BindingFlags.NonPublic);
                if (idxField != null) { int idx = 0; foreach (MeshSetLod l in ms.Lods) foreach (MeshSetSection sec in l.Sections) idxField.SetValue(sec, idx++); }
                App.AssetManager.ModifyRes(rid, ms);
                if (depot != null) { for (int i = 0; i < all.Count; i++) all[i].Index = i; App.AssetManager.ModifyRes(nb.ResRid, depot); }
                ResAssetEntry re = App.AssetManager.GetResEntry(rid);
                foreach (MeshSetLod l in ms.Lods) if (l.ChunkId != Guid.Empty) { try { re.LinkAsset(App.AssetManager.GetChunkEntry(l.ChunkId)); } catch { } }
                return null;
            }
            catch (Exception ex) { return "grow-lods failed: " + ex.Message; }
        }

        // Reports, per LOD, how many MeshSet sections have no shader block in the depot (0 = healthy).
        public static string CountDepotGaps(EbxAssetEntry entry, out List<int> gapsPerLod)
        {
            gapsPerLod = new List<int>();
            if (entry == null) return "no mesh entry.";
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                if (rid == 0) return "mesh has no MeshSetResource.";
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                ResAssetEntry nb = App.AssetManager.GetResEntry(entry.Name.ToLowerInvariant() + "_mesh/blocks");
                if (ms == null || nb == null) return null;
                ShaderBlockDepot depot = App.AssetManager.GetResAs<ShaderBlockDepot>(nb);
                if (depot == null) return null;
                for (int lodIndex = 0; lodIndex < ms.Lods.Count; lodIndex++)
                {
                    ShaderBlockEntry sbe = depot.GetSectionEntry(lodIndex);
                    int have = sbe != null ? sbe.Resources.Count : 0;
                    gapsPerLod.Add(Math.Max(0, ms.Lods[lodIndex].Sections.Count - have));
                }
                return null;
            }
            catch (Exception ex) { return "depot check failed: " + ex.Message; }
        }

        private static ulong DeriveHash(ulong baseHash, int lod, int section, int salt)
        {
            using (var w = new NativeWriter(new MemoryStream()))
            {
                w.Write(baseHash); w.Write(lod); w.Write(section); w.Write(salt);
                return Frosty.Hash.CityHash.Hash64(w.ToByteArray());
            }
        }

        // Grows each LOD's section list to at least <perLodCount> by cloning the LOD's last section
        // (reflection -- MeshSetSection has only an internal ctor + private fields). Lets an FBX with
        // MORE sections than the template import (ProcessLod fills the extra/empty sections). The new
        // sections reuse the cloned section's vertex declaration + material; ImportFBX resets their
        // vertex/index counts. NOTE: only structurally validated headlessly -- in-game render is unverified.
        public static string GrowSections(MeshSet meshSet, int perLodCount)
        {
            try
            {
                foreach (MeshSetLod lod in meshSet.Lods)
                {
                    var sections = lod.Sections;
                    if (sections.Count == 0 || sections.Count >= perLodCount) continue;
                    MeshSetSection tmpl = sections[sections.Count - 1];
                    int originalCount = sections.Count;
                    while (sections.Count < perLodCount)
                    {
                        MeshSetSection s = CloneSection(tmpl);
                        // Start empty (like ProcessLod's "kill section"): the template's geometry
                        // offsets/counts would otherwise miscompute the buffer sizes during serialize.
                        // ImportFBX fills these from the FBX node mapped into this section.
                        s.VertexCount = 0; s.PrimitiveCount = 0; s.VertexOffset = 0; s.StartIndex = 0;
                        sections.Add(s);
                    }
                    lod.SectionCount = sections.Count; // keep the cached count consistent with the list
                    // The renderer draws a section only through its subset categories (opaque, shadow,
                    // ...); a grown section joins the categories of the section it was cloned from.
                    if (Environment.GetEnvironmentVariable("FROSTYCLI_GROWN_SECTION_CATEGORIES") == "1") try
                    {
                        var cats = lod.CategorySubsetIndices;
                        if (cats != null)
                            foreach (List<byte> cat in cats)
                                if (cat != null && cat.Contains((byte)(originalCount - 1)))
                                    for (int n = originalCount; n < sections.Count; n++) if (!cat.Contains((byte)n)) cat.Add((byte)n);
                    }
                    catch { }
                }
                // The serializer keys each section's string/reloc by m_sectionIndex (global, assigned
                // sequentially across LODs on read). Clones copied the template's index, so renumber all
                // sections 0..N-1 in read order, else the STR reloc collides and re-parse seeks to a bad offset.
                FieldInfo idxField = typeof(MeshSetSection).GetField("m_sectionIndex",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (idxField != null)
                {
                    int idx = 0;
                    foreach (MeshSetLod lod in meshSet.Lods)
                        foreach (MeshSetSection s in lod.Sections)
                            idxField.SetValue(s, idx++);
                }
                return null;
            }
            catch (Exception ex) { return "grow-sections failed: " + ex.Message + (Environment.GetEnvironmentVariable("FROSTYCLI_DEBUG") == "1" ? Environment.NewLine + ex.ToString() : ""); }
        }

        /// <summary>The name and name hash stored inside a mesh's MeshSet resource.</summary>
        public static string ReadMeshSetName(EbxAssetEntry entry, out uint nameHash)
        {
            nameHash = 0;
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).MeshSetResource;
                MeshSet ms = App.AssetManager.GetResAs<MeshSet>(App.AssetManager.GetResEntry(rid));
                nameHash = ms.NameHash;
                return ms.FullName;
            }
            catch { return null; }
        }

        /// <summary>Set by Clone when something about the clone deserves a warning; null otherwise.</summary>
        public static string LastCloneNote;

        // Finds the (lodIndex, variationHash) pair that produces a depot entry hash for a given mesh
        // name hash. Only the default variation (0) is tried: other variations' hashes are not
        // recoverable from the entry alone.
        private static bool ExplainDepotHash(ulong hash, uint meshNameHash, int maxLod, out int lod, out uint variation)
        {
            variation = 0;
            for (lod = 0; lod < maxLod; lod++)
            {
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(meshNameHash); w.Write(variation); w.Write(lod);
                    if (Frosty.Hash.CityHash.Hash64(ms.ToArray()) == hash) return true;
                }
            }
            lod = -1;
            return false;
        }

        private static MeshSetSection CloneSection(MeshSetSection src)
        {
            Type t = typeof(MeshSetSection);
            var clone = (MeshSetSection)Activator.CreateInstance(t, nonPublic: true);
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object v = f.GetValue(src);
                // Deep-copy the mutable per-section lists so the clone doesn't alias the template's.
                if (v is System.Collections.Generic.List<ushort> us) v = new System.Collections.Generic.List<ushort>(us);
                else if (v is System.Collections.Generic.List<float> fs) v = new System.Collections.Generic.List<float>(fs);
                f.SetValue(clone, v);
            }
            return clone;
        }

        private static EbxAsset CloneEbx(EbxAsset source)
        {
            using (var writer = EbxBaseWriter.CreateProjectWriter(new MemoryStream(),
                       EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(source);
                byte[] buf = writer.ToByteArray();
                using (var reader = EbxReader.CreateProjectReader(new MemoryStream(buf)))
                    return reader.ReadAsset<EbxAsset>();
            }
        }

        private static byte[] GetResBytes(ResAssetEntry entry)
        {
            MethodInfo m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = m?.Invoke(App.AssetManager, new object[] { entry });
            if (raw is byte[] bytes) return bytes;
            if (raw is Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            throw new InvalidOperationException("unexpected GetRes return type");
        }

        private static byte[] GetChunkBytes(Guid chunkId)
        {
            ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(chunkId);
            if (ce == null) return null;
            object raw = App.AssetManager.GetChunk(ce);
            if (raw is byte[] bytes) return bytes;
            if (raw is Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            return null;
        }
    }
}
