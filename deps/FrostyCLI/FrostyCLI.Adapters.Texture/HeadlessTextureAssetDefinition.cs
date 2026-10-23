using System;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using TexturePlugin;

namespace FrostyCLI.Adapters.Texture
{
    public class HeadlessTextureAssetDefinition : AssetDefinition
    {
        public override bool Export(EbxAssetEntry entry, string path, string filterType)
        {
            if (App.AssetManager == null)
                throw new InvalidOperationException("AssetManager not initialized.");
            var asset = App.AssetManager.GetEbx(entry);
            if (asset == null) throw new Exception("Failed to load EBX: " + entry.Name);

            dynamic texObj = asset.RootObject;
            ulong resRid;
            try { resRid = (ulong)texObj.Resource; }
            catch (Exception ex) { throw new Exception("Could not read Resource RID: " + ex.Message, ex); }
            if (resRid == 0) throw new Exception("Resource RID is 0 for " + entry.Name);

            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            if (resEntry == null) throw new Exception("ResAssetEntry not found for RID 0x" + resRid.ToString("X16"));

            // Fully-qualify Texture: the namespace 'FrostyCLI.Adapters.Texture' shadows the type name.
            FrostySdk.Resources.Texture tex = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(resEntry);
            if (tex == null) throw new Exception("Failed to load Texture resource for " + entry.Name);

            string outDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            // TextureExporter expects a filter like "*.dds"/"*.png"; derive it from the output extension.
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) ext = ".dds";
            string filter = "*" + ext.ToLowerInvariant();

            new TextureExporter().Export(tex, path, filter);
            return File.Exists(path);
        }
    }
}
