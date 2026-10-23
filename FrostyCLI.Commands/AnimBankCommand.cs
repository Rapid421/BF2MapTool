using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Animation;

namespace FrostyCLI.Commands
{
    // anim-bank <types|find|dump|refs|tree|mask|index-table> ... [--file bank.bin] [--res name] [--depth n] [--max n] [--type T]
    // Browses any asset in a GD animation bank (default: the shared antstate bank) by schema:
    //   types               instance count per bank type
    //   find <filter>       assets whose __name contains the filter (--type narrows by type name)
    //   dump <name|guid>    every field of one asset (key refs show their target)
    //   refs <name|guid>    assets that reference it by key or pointer (who plays this clip?)
    //   tree <name|guid>    the key/reference graph below it, to --depth (default 4)
    //   mask <name|guid>    a BlendMaskListAsset's resolved weight per layout / per dof slot
    //   index-table <EnumGS> every IndexChooser driven by that enum game state, with what each index picks
    // A guid is the AntRef guid EBX uses (only its first 8 bytes are the bank key).
    public class AnimBankCommand : ICommand
    {
        public string Name => "anim-bank";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string file = null, res = ExportAnimCommand.SharedAnimBankRes, type = null;
            int depth = 4, max = 200;
            var pos = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
                else if (args[i] == "--res" && i + 1 < args.Length) res = args[++i];
                else if (args[i] == "--type" && i + 1 < args.Length) type = args[++i];
                else if (args[i] == "--depth" && i + 1 < args.Length) depth = int.Parse(args[++i]);
                else if (args[i] == "--max" && i + 1 < args.Length) max = int.Parse(args[++i]);
                else if (!args[i].StartsWith("--")) pos.Add(args[i]);
            }
            if (pos.Count == 0) { Console.Error.WriteLine("anim-bank: usage <types|find|dump|refs|tree|mask|index-table> [name|guid] [--file bank.bin] [--res name] [--type T] [--depth n] [--max n]"); return 1; }
            string verb = pos[0], arg = pos.Count > 1 ? pos[1] : null;

            byte[] bank;
            if (file != null) bank = File.ReadAllBytes(file);
            else
            {
                if (App.AssetManager == null) { Console.Error.WriteLine("anim-bank: AssetManager not initialized (or pass --file)."); return 1; }
                ResAssetEntry e = App.AssetManager.GetResEntry(res);
                if (e == null) { Console.Error.WriteLine("anim-bank: res not found: " + res); return 1; }
                object raw = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) }).Invoke(App.AssetManager, new object[] { e });
                if (raw is byte[] b) bank = b; else using (var ms = new MemoryStream()) { ((Stream)raw).CopyTo(ms); bank = ms.ToArray(); }
            }

            using (var br = new AntBankBrowser(new MemoryStream(bank)))
            {
                switch (verb)
                {
                    case "types":
                        foreach (var kv in br.TypeCounts().OrderByDescending(k => k.Value)) Console.WriteLine(kv.Value.ToString().PadLeft(8) + "  " + kv.Key);
                        return 0;
                    case "find":
                    {
                        if (arg == null) { Console.Error.WriteLine("anim-bank find: needs a filter"); return 1; }
                        int n = 0;
                        foreach (long b in br.Find(arg, false))
                        {
                            if (type != null && !string.Equals(br.TypeName(b), type, StringComparison.OrdinalIgnoreCase)) continue;
                            if (n++ >= max) { Console.WriteLine("... (--max " + max + ")"); break; }
                            Console.WriteLine(br.Label(b));
                        }
                        return 0;
                    }
                    case "index-table":
                    {
                        // Every IndexChooserControllerAsset driven by the named enumeration game state, one line per
                        // chooser: name, then the clip/controller each index picks (TSV: index<TAB>label).
                        if (arg == null) { Console.Error.WriteLine("anim-bank index-table: needs a GameStateEnumerationAsset name (e.g. SpecificWeapon.EnumGS)"); return 1; }
                        var gs = br.Find(arg, true); if (gs.Count == 0) { Console.Error.WriteLine("anim-bank: no asset " + arg); return 1; }
                        foreach (long b in br.Bodies)
                        {
                            if (br.TypeName(b) != "IndexChooserControllerAsset" || br.RefField(b, "IndexAsset") != gs[0]) continue;
                            Console.WriteLine("# " + br.Label(b));
                            var list = br.RefArray(b, "ChoiceAssetList");
                            for (int i = 0; i < list.Count; i++) Console.WriteLine(i + "	" + (list[i] > 0 ? br.Label(list[i]) : "null"));
                        }
                        return 0;
                    }
                    case "mask":
                    {
                        if (arg == null) { Console.Error.WriteLine("anim-bank mask: needs a BlendMaskListAsset name or guid"); return 1; }
                        foreach (long h in br.Find(arg, true))
                        {
                            Console.WriteLine(br.Label(h));
                            foreach (var kv in br.MaskWeights(h)) Console.WriteLine("  " + kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture).PadLeft(6) + "  " + kv.Key);
                        }
                        return 0;
                    }
                    case "dump":
                    case "refs":
                    case "tree":
                    {
                        if (arg == null) { Console.Error.WriteLine("anim-bank " + verb + ": needs a name or guid"); return 1; }
                        var hits = br.Find(arg, true);
                        if (hits.Count == 0) { Console.Error.WriteLine("anim-bank: no asset named or keyed '" + arg + "'"); return 1; }
                        foreach (long h in hits)
                        {
                            if (verb == "dump") foreach (string l in br.Dump(h, depth, max)) Console.WriteLine(l);
                            else if (verb == "tree") Tree(br, h, "", depth, new HashSet<long>(), "");
                            else Refs(br, h, max);
                        }
                        return 0;
                    }
                }
            }
            Console.Error.WriteLine("anim-bank: unknown verb " + verb);
            return 1;
        }

        private static void Tree(AntBankBrowser br, long body, string ind, int depth, HashSet<long> seen, string via)
        {
            bool first = seen.Add(body);
            Console.WriteLine(ind + (via.Length > 0 ? via + ": " : "") + br.Label(body) + (first ? "" : " (seen)"));
            if (!first || depth <= 0) return;
            foreach (var kv in br.Children(body))
            {
                if (kv.Value < 0) { Console.WriteLine(ind + "  " + kv.Key + ": (missing key " + AntBankBrowser.KeyGuid(-kv.Value) + ")"); continue; }
                Tree(br, kv.Value, ind + "  ", depth - 1, seen, kv.Key);
            }
        }

        private static void Refs(AntBankBrowser br, long target, int max)
        {
            // Reverse index over top-level assets: a reference counts when any field of the asset, or of a
            // sub-object it owns through Reference fields, points at the target.
            int n = 0;
            foreach (long b in br.Bodies)
            {
                if (br.Key(b) == 0) continue;
                var stack = new Stack<long>(); var seen = new HashSet<long>(); stack.Push(b);
                string via = null;
                while (stack.Count > 0 && via == null)
                {
                    long cur = stack.Pop(); if (!seen.Add(cur)) continue;
                    foreach (var kv in br.Children(cur))
                    {
                        if (kv.Value == target) { via = kv.Key; break; }
                        if (kv.Value > 0 && br.Key(kv.Value) == 0) stack.Push(kv.Value);
                    }
                }
                if (via == null) continue;
                if (n++ >= max) { Console.WriteLine("... (--max " + max + ")"); return; }
                Console.WriteLine(br.Label(b) + "   via " + via);
            }
        }
    }
}
