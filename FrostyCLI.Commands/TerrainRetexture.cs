using System;
using System.Collections.Generic;
using System.Linq;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // `terrain retexture <terrain> --from <TextureAsset> --to <TextureAsset>`: point a terrain's layers at another texture
    // asset. The binding (SPEC §14, found 2026-09-23): each layer combination's compiled shader has a ShaderBlockDepot RES
    // `<terrain>__<combination>__<pass>__0_graph/blocks` (type 3639990959) holding texture parameters of 36 bytes -- u64 key,
    // u32 0xAD0ABFD3, u16 1, u16, u32 1, then the TextureAsset's instance GUID. Tatooine: 23 of its 59 depots name its
    // layer textures (the 2d pass the _CS and _NOH sets, displacement2d the _NOH). Rewriting the GUID there gives the
    // layers a new texture asset (e.g. one made with `import texture --template`) and leaves the shared asset -- used by
    // other levels and decals -- as it was. The depots are the terrain's, so every level using this terrain sees it.
    public partial class TerrainCommand
    {
        private const uint ShaderBlockDepotResType = 3639990959;
        private const uint TextureParamTag = 0xAD0ABFD3;

        private static int ExecuteRetexture(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "terrain retexture";
            string terrain = null, from = null, to = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--from" && i + 1 < args.Length) from = args[++i];
                else if (args[i] == "--to" && i + 1 < args.Length) to = args[++i];
                else if (!args[i].StartsWith("--")) terrain = args[i];
            }
            if (terrain == null || from == null || to == null)
            { Console.Error.WriteLine("Usage: frostycli terrain retexture <terrain> --from <TextureAsset> --to <TextureAsset>"); return 1; }
            if (App.AssetManager == null || !Session.Project.IsLoaded) { Console.Error.WriteLine(label + ": needs a loaded project (project new/load)"); return 1; }

            Guid fromGuid, toGuid;
            EbxAssetEntry toEntry;
            if (!TextureGuid(label, from, out fromGuid, out _) || !TextureGuid(label, to, out toGuid, out toEntry)) return 1;
            byte[] a = fromGuid.ToByteArray(), b = toGuid.ToByteArray();

            string baseName = terrain.ToLowerInvariant();
            if (baseName.EndsWith(ResSuffix)) baseName = baseName.Substring(0, baseName.Length - ResSuffix.Length);
            int depots = 0, parameters = 0;
            var bundles = new HashSet<int>();
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes(ShaderBlockDepotResType).ToList())
            {
                string n = e.Name.ToLowerInvariant();
                if (!n.StartsWith(baseName + "__") || !n.EndsWith("_graph/blocks")) continue;
                byte[] data = ReadRes(e);
                if (data == null) continue;
                int count = RetargetTextureParams(data, a, b);
                if (count == 0) continue;
                byte[] meta = e.ModifiedEntry != null && e.ModifiedEntry.ResMeta != null ? e.ModifiedEntry.ResMeta : e.ResMeta;
                ResWriter.Modify(e.Name, data, meta);
                depots++; parameters += count;
                foreach (int bid in e.Bundles) bundles.Add(bid);
                foreach (int bid in e.AddedBundles) bundles.Add(bid);
            }
            if (depots == 0)
            { Console.Error.WriteLine(label + ": no layer combination of " + baseName + " samples " + from); return 1; }

            // the new texture must load where the depots do
            var missing = bundles.Where(bid => !toEntry.Bundles.Contains(bid) && !toEntry.AddedBundles.Contains(bid)).ToList();
            var fields = new Dictionary<string, string>
            {
                { "terrain", baseName },
                { "retargeted", parameters + " texture parameter(s) in " + depots + " combination depot(s): " + from + " -> " + to },
            };
            if (missing.Count > 0)
                fields["warning"] = to + " is not in " + missing.Count + " bundle(s) the depots load in (" +
                    string.Join(", ", missing.Take(4).Select(bid => App.AssetManager.GetBundleEntry(bid)?.Name ?? bid.ToString())) +
                    "): add it with `bundle add` (--with-resources), or make it with `import texture --template` from a texture those bundles carry";
            formatter.WriteResult("OK", label, fields);
            return 0;
        }

        // Replaces the texture GUID of every texture parameter that names `from`; returns how many it replaced.
        internal static int RetargetTextureParams(byte[] data, byte[] from, byte[] to)
        {
            int count = 0;
            for (int o = 20; o + 16 <= data.Length; o++)
            {
                if (data[o] != from[0] || !Match(data, o, from)) continue;
                // the 20 bytes before the GUID: u64 key, u32 tag, u16 1, u16, u32 1
                if (BitConverter.ToUInt32(data, o - 12) != TextureParamTag || BitConverter.ToUInt32(data, o - 4) != 1) continue;
                Buffer.BlockCopy(to, 0, data, o, 16);
                count++;
            }
            return count;

            bool Match(byte[] d, int at, byte[] g) { for (int k = 0; k < 16; k++) if (d[at + k] != g[k]) return false; return true; }
        }

        private static bool TextureGuid(string label, string name, out Guid guid, out EbxAssetEntry entry)
        {
            guid = Guid.Empty;
            entry = App.AssetManager.GetEbxEntry(name);
            if (entry == null) { Console.Error.WriteLine(label + ": no asset " + name); return false; }
            if (!entry.Type.Contains("Texture")) { Console.Error.WriteLine(label + ": " + name + " is a " + entry.Type + ", not a texture"); return false; }
            dynamic root = App.AssetManager.GetEbx(entry).RootObject;
            guid = ((AssetClassGuid)root.GetInstanceGuid()).ExportedGuid;
            return true;
        }
    }
}
