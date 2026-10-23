using System;
using Frosty.Core;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public class IndexCommand : ICommand
    {
        public string Name => "index";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1 || !string.Equals(args[0], "build", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Usage: frostycli index build [--refs]");
                return 1;
            }

            bool buildRefs = false;
            for (int i = 1; i < args.Length; i++)
                if (args[i] == "--refs") buildRefs = true;

            if (App.AssetManager == null)
            { Console.Error.WriteLine("index build: AssetManager not initialized."); return 1; }

            IndexService.Reset();
            IndexService.GetCatalog();
            if (buildRefs) IndexService.GetRefIndex();
            return 0;
        }
    }
}
