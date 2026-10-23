using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FbTexture = FrostySdk.Resources.Texture;

namespace FrostyCLI.Adapters.Texture
{
    // Creates an independent NEW TextureAsset by duplicating a template texture's EBX + Texture res +
    // chunk and rewiring them, so a subsequent `import texture` writes into the new asset without
    // touching the template. Headless equivalent of "duplicate a texture, then import over it".
    public static class TextureCloner
    {
        public static string Clone(string templateName, string newName, out EbxAssetEntry newEntry)
        {
            newEntry = null;
            if (App.AssetManager == null) return "AssetManager not initialized.";
            if (App.AssetManager.GetEbxEntry(newName) != null) return "destination already exists: " + newName;

            EbxAssetEntry tmplEntry = App.AssetManager.GetEbxEntry(templateName);
            if (tmplEntry == null) return "template not found: " + templateName;
            EbxAsset tmplAsset;
            try { tmplAsset = App.AssetManager.GetEbx(tmplEntry); }
            catch (Exception ex) { return "template load failed: " + ex.Message; }

            ulong resRid;
            try { resRid = (ulong)((dynamic)tmplAsset.RootObject).Resource; }
            catch (Exception ex) { return "template is not a TextureAsset (no Resource): " + ex.Message; }
            if (resRid == 0) return "template '" + templateName + "' has no texture resource.";
            ResAssetEntry tmplRes = App.AssetManager.GetResEntry(resRid);
            if (tmplRes == null) return "template res not found.";

            // 1. Clone the EBX (binary round-trip) + fresh GUIDs + name.
            EbxAsset newAsset;
            try { newAsset = BinaryCloneEbx(tmplAsset); }
            catch (Exception ex) { return "EBX clone failed: " + ex.Message; }
            newAsset.SetFileGuid(Guid.NewGuid());
            dynamic root = newAsset.RootObject;
            try { root.Name = newName; } catch { }
            try
            {
                var guid = new AssetClassGuid(Utils.GenerateDeterministicGuid(newAsset.Objects, (Type)root.GetType(), newAsset.FileGuid), -1);
                root.SetInstanceGuid(guid);
            }
            catch (Exception ex) { return "instance-guid assign failed: " + ex.Message; }

            // 2. Duplicate the Texture res.
            byte[] resBytes;
            try { resBytes = GetResBytes(tmplRes); }
            catch (Exception ex) { return "could not read template res: " + ex.Message; }
            // Shipped textures name their res exactly like the EBX (lower case); keep that shape.
            string newResName = newName.ToLowerInvariant();
            if (App.AssetManager.GetResEntry(newResName) != null)
                newResName = newResName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try { App.AssetManager.AddRes(newResName, (ResourceType)tmplRes.ResType, (byte[])(tmplRes.ResMeta ?? new byte[0]).Clone(), resBytes); }
            catch (Exception ex) { return "AddRes failed: " + ex.Message; }
            ResAssetEntry newRes = App.AssetManager.GetResEntry(newResName);
            if (newRes == null) return "AddRes did not create " + newResName;
            ulong newResRid = newRes.ResRid;

            // 3. Duplicate the pixel chunk + rewire (so a later import-texture ModifyChunk targets the
            //    NEW chunk, not the template's).
            try
            {
                FbTexture tex = App.AssetManager.GetResAs<FbTexture>(newRes);
                if (tex != null && tex.ChunkId != Guid.Empty)
                {
                    byte[] chunkData = GetChunkBytes(tex.ChunkId);
                    if (chunkData != null)
                    {
                        Guid newChunkId = App.AssetManager.AddChunk(chunkData, null);
                        tex.ChunkId = newChunkId;
                        App.AssetManager.ModifyRes(newResRid, tex);
                    }
                }
            }
            catch (Exception ex) { return "chunk duplication failed: " + ex.Message; }

            // 4. Point the new EBX at the new res, add it.
            try { ((dynamic)newAsset.RootObject).Resource = newResRid; }
            catch (Exception ex) { return "could not set Resource: " + ex.Message; }
            try { newEntry = App.AssetManager.AddEbx(newName, newAsset); }
            catch (Exception ex) { return "AddEbx failed: " + ex.Message; }
            if (newEntry == null) return "AddEbx did not create " + newName;
            try { newEntry.LinkAsset(newRes); } catch { }
            return null;
        }

        // For `bundle add --with-resources`: if <entry> is a texture (has a Resource that loads as a
        // Texture), reports its res rid + pixel chunk id so the bundler can pull them into the EBX's
        // bundle. Returns false for non-texture assets.
        /// <summary>The texture's first streamed mip (what the bundle's chunk entry must carry), or -1.</summary>
        public static int GetFirstMip(EbxAssetEntry entry)
        {
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                ulong rid = (ulong)((dynamic)asset.RootObject).Resource;
                ResAssetEntry re = rid != 0 ? App.AssetManager.GetResEntry(rid) : null;
                FbTexture tex = re != null ? App.AssetManager.GetResAs<FbTexture>(re) : null;
                return tex != null ? tex.FirstMip : -1;
            }
            catch { return -1; }
        }

        public static bool TryGetLinkedResources(EbxAssetEntry entry, out ulong resRid, out System.Collections.Generic.List<Guid> chunkIds)
        {
            resRid = 0; chunkIds = new System.Collections.Generic.List<Guid>();
            if (entry == null || App.AssetManager == null) return false;
            try
            {
                var asset = App.AssetManager.GetEbx(entry);
                resRid = (ulong)((dynamic)asset.RootObject).Resource;
                if (resRid == 0) return false;
                ResAssetEntry re = App.AssetManager.GetResEntry(resRid);
                if (re == null) { resRid = 0; return false; }
                FbTexture tex = App.AssetManager.GetResAs<FbTexture>(re);
                if (tex == null) { resRid = 0; return false; } // res isn't a Texture -> not our type
                if (tex.ChunkId != Guid.Empty) chunkIds.Add(tex.ChunkId);
                return true;
            }
            catch { resRid = 0; chunkIds = new System.Collections.Generic.List<Guid>(); return false; }
        }

        private static EbxAsset BinaryCloneEbx(EbxAsset source)
        {
            using (var writer = EbxBaseWriter.CreateWriter(new MemoryStream(), EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(source);
                byte[] buf = writer.ToByteArray();
                using (var reader = EbxReader.CreateReader(new MemoryStream(buf)))
                    return reader.ReadAsset<EbxAsset>();
            }
        }

        private static byte[] GetResBytes(ResAssetEntry entry)
        {
            MethodInfo m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = m?.Invoke(App.AssetManager, new object[] { entry });
            if (raw is byte[] b) return b;
            if (raw is Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            throw new InvalidOperationException("unexpected GetRes return type");
        }

        private static byte[] GetChunkBytes(Guid chunkId)
        {
            ChunkAssetEntry ce = App.AssetManager.GetChunkEntry(chunkId);
            if (ce == null) return null;
            object raw = App.AssetManager.GetChunk(ce);
            if (raw is byte[] b) return b;
            if (raw is Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            return null;
        }
    }
}
