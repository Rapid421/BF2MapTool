using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // `terrain clear-decals <terrain>` (and `terrain create --clear-decals`): empties the terrain decals of every level
    // that uses a terrain. Terrain decals (RES 0x15E1F32E, `<terrain>_<level>/decals`, SPEC §12) are world-space
    // placements made for the host's ground -- footprints, tracks, stains -- and on new ground they stay where the host
    // had them. The empty form is the shipped one (test range, Endor 02, Jabba's Palace, A3 M1PIL): the level's f32, u32 0,
    // u32 1, u8 0, then 12 zero bytes (no templates, no instances); the level's own f32 is kept.
    public partial class TerrainCommand
    {
        private const uint DecalsResType = 0x15E1F32E;

        private static int ExecuteClearDecals(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            const string label = "terrain clear-decals";
            string terrain = args.FirstOrDefault(a => !a.StartsWith("--"));
            if (terrain == null) { Console.Error.WriteLine("Usage: frostycli terrain clear-decals <terrain streaming tree RES>"); return 1; }
            if (App.AssetManager == null || !Session.Project.IsLoaded)
            { Console.Error.WriteLine(label + ": needs a loaded project (project new/load)"); return 1; }
            var fields = new Dictionary<string, string>();
            if (ClearDecals(label, terrain, fields) < 0) return 1;
            formatter.WriteResult("OK", label, fields);
            return 0;
        }

        // Returns the number of decal resources emptied (0 when the terrain has none), or -1 on an error.
        private static int ClearDecals(string label, string terrainRes, Dictionary<string, string> fields)
        {
            string baseName = terrainRes.ToLowerInvariant();
            if (baseName.EndsWith(ResSuffix)) baseName = baseName.Substring(0, baseName.Length - ResSuffix.Length);
            var cleared = new List<string>();
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes(DecalsResType).ToList())
            {
                string n = e.Name.ToLowerInvariant();
                if (!n.StartsWith(baseName + "_") || !n.EndsWith("/decals")) continue;
                byte[] old = ReadRes(e);
                if (old == null || old.Length < 13 || BitConverter.ToUInt32(old, 4) != 0 || BitConverter.ToUInt32(old, 8) > 1)
                { Console.Error.WriteLine(label + ": " + e.Name + " does not start like a terrain decals resource (SPEC §12)"); return -1; }
                byte[] empty = EmptyDecals(BitConverter.ToSingle(old, 0));
                byte[] meta = e.ModifiedEntry != null && e.ModifiedEntry.ResMeta != null ? e.ModifiedEntry.ResMeta : e.ResMeta;
                ResWriter.Modify(e.Name, empty, meta);
                cleared.Add(e.Name + " (" + old.Length.ToString("N0") + " -> " + empty.Length + " bytes)");
            }
            fields["decals"] = cleared.Count == 0 ? "none found for " + baseName : "emptied " + string.Join("; ", cleared);
            return cleared.Count;
        }

        internal static byte[] EmptyDecals(float leading)
        {
            var b = new byte[25];
            BitConverter.GetBytes(leading).CopyTo(b, 0);
            BitConverter.GetBytes(1u).CopyTo(b, 8);
            return b;
        }
    }
}
