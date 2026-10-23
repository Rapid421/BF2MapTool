using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    // Creates an independent NEW resource-backed asset by duplicating a template's EBX + its single
    // `Resource` res, rewiring the new EBX at the new res. Generalizes TextureCloner for any single-
    // resource asset whose root object exposes a `Resource` ResRid (AtlasTexture, SvgImage, IesResource,
    // etc.). The res bytes are cloned verbatim, so the new res initially references the same chunk(s) as
    // the template (correct for a read-only clone, and moot once the new res blob is replaced via
    // `import resource`/`res import`). Type-specific chunk duplication (e.g. textures, meshes) lives in
    // the per-type cloners; this is the byte-blob path.
    public static class AssetCloner
    {
        public static string Clone(string templateName, string newName, out EbxAssetEntry newEntry)
        {
            return Clone(templateName, newName, out newEntry, "Resource");
        }

        /// <summary>As <see cref="Clone(string, string, out EbxAssetEntry)"/> for an asset whose res rid is in <paramref name="resourceField"/>
        /// (e.g. `DatabaseResource` on StaticEnlightenData / EnlightenDataAsset).</summary>
        public static string Clone(string templateName, string newName, out EbxAssetEntry newEntry, string resourceField)
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
            PropertyInfo resProp = tmplAsset.RootObject.GetType().GetProperty(resourceField);
            if (resProp == null) return "template '" + templateName + "' has no " + resourceField + " property";
            try { resRid = (ulong)(dynamic)resProp.GetValue(tmplAsset.RootObject); }
            catch (Exception ex) { return "template '" + templateName + "': could not read " + resourceField + ": " + ex.Message; }
            if (resRid == 0) return "template '" + templateName + "' has no resource.";
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

            // 2. Duplicate the res (verbatim bytes).
            byte[] resBytes;
            try { resBytes = GetResBytes(tmplRes); }
            catch (Exception ex) { return "could not read template res: " + ex.Message; }
            string newResName = newName.ToLowerInvariant();
            if (App.AssetManager.GetResEntry(newResName) != null)
                newResName = newResName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            try { App.AssetManager.AddRes(newResName, (ResourceType)tmplRes.ResType, (byte[])(tmplRes.ResMeta ?? new byte[0]).Clone(), resBytes); }
            catch (Exception ex) { return "AddRes failed: " + ex.Message; }
            ResAssetEntry newRes = App.AssetManager.GetResEntry(newResName);
            if (newRes == null) return "AddRes did not create " + newResName;
            ulong newResRid = newRes.ResRid;

            // 3. Point the new EBX at the new res, add it.
            try
            {
                PropertyInfo np = newAsset.RootObject.GetType().GetProperty(resourceField);
                object current = np.GetValue(newAsset.RootObject);
                // ResourceRef wraps a ulong; build the same type from the new rid
                object value = current is ulong ? (object)newResRid : Activator.CreateInstance(current.GetType(), newResRid);
                np.SetValue(newAsset.RootObject, value);
            }
            catch (Exception ex) { return "could not set " + resourceField + ": " + ex.Message; }
            try { newEntry = App.AssetManager.AddEbx(newName, newAsset); }
            catch (Exception ex) { return "AddEbx failed: " + ex.Message; }
            if (newEntry == null) return "AddEbx did not create " + newName;
            try { newEntry.LinkAsset(newRes); } catch { }
            return null;
        }

        private static EbxAsset BinaryCloneEbx(EbxAsset source)
        {
            using (var writer = EbxBaseWriter.CreateProjectWriter(new MemoryStream(), EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
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
            if (raw is byte[] b) return b;
            if (raw is Stream s) { using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); } }
            throw new InvalidOperationException("unexpected GetRes return type");
        }
    }
}
