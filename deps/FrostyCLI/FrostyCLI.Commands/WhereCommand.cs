using System;
using System.Linq;
using System.Collections.Generic;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // Resolves an asset reference to its canonical identity and prints it: by exact EBX path, by file
    // GUID (e.g. a __fileGuid from an `ebx dump`), or by case-insensitive substring (lists matches).
    // Useful for turning a partial name or a GUID into the exact path other commands expect.
    public class WhereCommand : ICommand
    {
        public string Name => "where";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string query = null; int limit = 50;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
                else if (!args[i].StartsWith("--")) query = args[i];
            }
            if (query == null) { Console.Error.WriteLine("where: missing <name-or-guid>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("where: AssetManager not initialized."); return 1; }

            // 0) --data1 <hex8>: every EBX whose file GUID -- or, with --instances, root instance GUID -- and every chunk
            //    whose id starts with those 32 bits. A fault register often holds only Data1 of the GUID the engine failed
            //    to resolve (game+0x1522B97: r9 = Data1 of the unbundled texture's instance guid, MISS_M6).
            if (args.Contains("--data1"))
            {
                string hex = null; bool instances = args.Contains("--instances");
                for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--data1") hex = args[i + 1];
                uint d1;
                if (hex == null || !uint.TryParse(hex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out d1))
                { Console.Error.WriteLine("where --data1: need 8 hex digits"); return 1; }
                int hits = 0;
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (BitConverter.ToUInt32(e.Guid.ToByteArray(), 0) == d1) { Console.WriteLine("ebx file guid  " + e.Guid + "  " + e.Name + "  (" + e.Type + ")"); hits++; }
                    if (!instances) continue;
                    try
                    {
                        object root = App.AssetManager.GetEbx(e).RootObject;
                        var ig = ((FrostySdk.Ebx.AssetClassGuid)root.GetType().GetMethod("GetInstanceGuid").Invoke(root, null)).ExportedGuid;
                        if (BitConverter.ToUInt32(ig.ToByteArray(), 0) == d1) { Console.WriteLine("ebx instance   " + ig + "  " + e.Name + "  (" + e.Type + ")"); hits++; }
                    }
                    catch { }
                }
                foreach (ChunkAssetEntry c in App.AssetManager.EnumerateChunks())
                    if (BitConverter.ToUInt32(c.Id.ToByteArray(), 0) == d1) { Console.WriteLine("chunk          " + c.Id + "  bundles=" + c.Bundles.Count + " added=" + c.IsAdded); hits++; }
                Console.WriteLine(hits + " hit(s)");
                return hits > 0 ? 0 : 1;
            }

            // 1) GUID -> the asset with that file GUID.
            Guid g;
            if (Guid.TryParse(query, out g))
            {
                EbxAssetEntry byGuid = App.AssetManager.GetEbxEntry(g);
                if (byGuid != null) { Print(byGuid, formatter); return 0; }
                Console.Error.WriteLine("where: no asset with file GUID " + query); return 1;
            }

            // 2) Exact path.
            EbxAssetEntry exact = App.AssetManager.GetEbxEntry(query);
            if (exact != null) { Print(exact, formatter); return 0; }

            // 3) Substring match (case-insensitive) -> list candidates.
            int n = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (e.Name == null || e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Print(e, formatter);
                if (++n >= limit) break;
            }
            if (n == 0) { Console.Error.WriteLine("where: no asset matching '" + query + "'"); return 1; }
            return 0;
        }

        private static void Print(EbxAssetEntry e, OutputFormatter formatter)
        {
            formatter.WriteResult("OK", "where", new Dictionary<string, string>
            {
                { "path", e.Name },
                { "guid", e.Guid.ToString() },
                { "type", e.Type ?? "" },
            });
        }
    }
}
