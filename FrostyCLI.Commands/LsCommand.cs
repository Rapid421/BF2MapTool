using System;
using System.Collections.Generic;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public class LsCommand : ICommand
    {
        public string Name => "ls";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string prefix = null;
            string typeFilter = null;
            bool countOnly = false;
            int limit = -1;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--type" && i + 1 < args.Length)
                    typeFilter = args[++i];
                else if (args[i] == "--count")
                    countOnly = true;
                else if (args[i] == "--limit" && i + 1 < args.Length)
                    limit = int.Parse(args[++i]);
                else if (!args[i].StartsWith("--"))
                    prefix = args[i];
            }

            if (App.AssetManager == null)
            {
                Console.Error.WriteLine("ls: AssetManager not initialized. Pass --game-path.");
                return 1;
            }

            int count = 0;
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx())
            {
                if (prefix != null && !entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (typeFilter != null && !string.Equals(entry.Type, typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                count++;
                if (!countOnly)
                {
                    var fields = new Dictionary<string, string>();
                    fields["file"] = entry.Name;
                    fields["type"] = entry.Type ?? "";
                    fields["guid"] = entry.Guid.ToString();
                    formatter.WriteResult("OK", "ls", fields);
                    if (limit > 0 && count >= limit) break;
                }
            }

            if (countOnly)
                Console.Out.WriteLine(count);

            return 0;
        }
    }
}
