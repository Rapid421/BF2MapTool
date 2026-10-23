using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Commands
{
    // anim-info <name-filter> [--file <bank.bin>] [--res <bank res>]: list the bank's clip controllers whose name
    // contains the filter, with each clip's codec. Reads the shared animation bank by default (where the
    // first-person weapon clips live, not just the state-machine ones export-anim can reach).
    public class AnimInfoCommand : ICommand
    {
        public string Name => "anim-info";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string filter = null, file = null, res = ExportAnimCommand.SharedAnimBankRes; bool dump = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dump") dump = true;
                else if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
                else if (args[i] == "--res" && i + 1 < args.Length) res = args[++i];
                else if (!args[i].StartsWith("--")) filter = args[i];
            }
            if (filter == null) { Console.Error.WriteLine("anim-info: usage <name-filter> [--file <bank.bin>] [--res <bank res>]"); return 1; }
            byte[] bank;
            if (file != null) bank = File.ReadAllBytes(file);
            else
            {
                if (App.AssetManager == null) { Console.Error.WriteLine("anim-info: AssetManager not initialized (or pass --file)."); return 1; }
                ResAssetEntry e = App.AssetManager.GetResEntry(res);
                if (e == null) { Console.Error.WriteLine("anim-info: res not found: " + res); return 1; }
                object raw = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) }).Invoke(App.AssetManager, new object[] { e });
                if (raw is byte[] b) bank = b; else using (var ms = new MemoryStream()) { ((Stream)raw).CopyTo(ms); bank = ms.ToArray(); }
            }
            using (var dec = new AntAnimationDecoder(new MemoryStream(bank)))
                foreach (string l in (dump ? dec.DumpClipByName(filter) : dec.DescribeClipsByName(filter))) Console.WriteLine(l);
            return 0;
        }
    }
}
