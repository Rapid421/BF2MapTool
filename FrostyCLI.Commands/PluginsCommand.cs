using System;
using System.Collections.Generic;
using System.Linq;
using Frosty.Core;
using FrostyCLI.Core;
using Newtonsoft.Json;

namespace FrostyCLI.Commands
{
    public class PluginsCommand : ICommand
    {
        public string Name => "plugins";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (Session.Plugins == null)
            {
                Console.Error.WriteLine("plugins: Session not initialized. Pass --game-path.");
                return 1;
            }

            var entries = Session.Plugins.All
                .OrderBy(kv => kv.Key)
                .ToList();

            bool jsonMode = false;
            foreach (var a in args)
                if (a == "--json") { jsonMode = true; break; }

            if (jsonMode)
            {
                var list = entries.Select(kv => new Dictionary<string, string>
                {
                    ["type"]    = kv.Key,
                    ["handler"] = kv.Value.GetType().Name
                }).ToList();
                Console.Out.WriteLine(JsonConvert.SerializeObject(list, Newtonsoft.Json.Formatting.Indented));
            }
            else
            {
                foreach (var kv in entries)
                {
                    var fields = new Dictionary<string, string>();
                    fields["file"]    = kv.Key;
                    fields["handler"] = kv.Value.GetType().Name;
                    formatter.WriteResult("OK", "plugins", fields);
                }
            }

            return 0;
        }
    }
}
