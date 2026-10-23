using System;
using System.Collections.Generic;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Managers;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Writes a modified resource. The SDK's ModifyRes compresses with the profile's shipping
    /// settings (zstd level 18 for Battlefront II, about a second per megabyte), which is right for
    /// the final state but ruinous when a bulk edit rewrites the same resource hundreds of times.
    /// With Fast set, intermediate writes use LZ4 and the names are remembered so Finish can
    /// recompress them once with the shipping settings.
    /// </summary>
    public static class ResWriter
    {
        public static bool Fast;
        private static readonly HashSet<string> touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Modify(string name, byte[] bytes, byte[] meta)
        {
            if (!Fast) { App.AssetManager.ModifyRes(name, bytes, meta); return; }
            ResAssetEntry entry = App.AssetManager.GetResEntry(name);
            if (entry == null) throw new InvalidOperationException("resource not found: " + name);
            if (entry.ModifiedEntry == null) entry.ModifiedEntry = new ModifiedAssetEntry();
            entry.ModifiedEntry.Data = Utils.CompressFile(bytes, resType: (ResourceType)entry.ResType, compressionOverride: CompressionType.LZ4);
            entry.ModifiedEntry.OriginalSize = bytes.Length;
            entry.ModifiedEntry.Sha1 = Utils.GenerateSha1(entry.ModifiedEntry.Data);
            entry.ModifiedEntry.IsDirty = true;
            if (meta != null) entry.ModifiedEntry.ResMeta = meta;
            entry.IsDirty = true;
            touched.Add(name);
        }

        /// <summary>Recompresses every resource written while Fast was on, then clears Fast.</summary>
        public static int Finish(Func<ResAssetEntry, byte[]> read)
        {
            Fast = false;
            int n = 0;
            foreach (string name in new List<string>(touched))
            {
                ResAssetEntry entry = App.AssetManager.GetResEntry(name);
                if (entry == null || entry.ModifiedEntry == null) continue;
                byte[] bytes = read(entry);
                if (bytes == null) continue;
                App.AssetManager.ModifyRes(name, bytes, entry.ModifiedEntry.ResMeta);
                n++;
            }
            touched.Clear();
            return n;
        }
    }
}
