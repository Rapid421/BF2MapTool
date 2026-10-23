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
    // anim-index-remap --enum <GameStateEnumerationAsset> [--graph <root>[,<root>...]] [--also <chooser>[,...]]
    //                  [--copy <from>:<to>]... [--set <chooser>:<index>=<asset name|guid>]... [--res <bank res>] [--dry-run]
    // Rewrites IndexChooser entries in place (8-byte keys, nothing structural). --copy makes index <to> pick
    // whatever index <from> picks, in every chooser driven by --enum (limited to the choosers reachable from the
    // --graph roots, plus --also). --set then points one chooser's entry at a specific asset.
    // Use: give a weapon its own SpecificWeapon slot that behaves exactly like a donor weapon's, except for the
    // entries you --set (e.g. Wep.ZoomPose.Index), so one gun can get its own held aim pose.
    public class AnimIndexRemapCommand : ICommand
    {
        public string Name => "anim-index-remap";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string res = ExportAnimCommand.SharedAnimBankRes, enumName = null; bool dry = false;
            var roots = new List<string>(); var also = new List<string>(); var copies = new List<string>(); var sets = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--enum" && i + 1 < args.Length) enumName = args[++i];
                else if (args[i] == "--graph" && i + 1 < args.Length) roots.AddRange(args[++i].Split(','));
                else if (args[i] == "--also" && i + 1 < args.Length) also.AddRange(args[++i].Split(','));
                else if (args[i] == "--copy" && i + 1 < args.Length) copies.Add(args[++i]);
                else if (args[i] == "--set" && i + 1 < args.Length) sets.Add(args[++i]);
                else if (args[i] == "--res" && i + 1 < args.Length) res = args[++i];
                else if (args[i] == "--dry-run") dry = true;
            }
            if (enumName == null || (copies.Count == 0 && sets.Count == 0))
            { Console.Error.WriteLine("anim-index-remap: usage --enum <EnumGS> [--graph roots] [--also choosers] --copy from:to | --set chooser:index=asset [--dry-run]"); return 1; }
            if (App.AssetManager == null || (!dry && !Session.Project.IsLoaded)) { Console.Error.WriteLine("anim-index-remap: needs a loaded project (--project), or --dry-run."); return 1; }
            ResAssetEntry entry = App.AssetManager.GetResEntry(res);
            if (entry == null) { Console.Error.WriteLine("anim-index-remap: res not found: " + res); return 1; }
            object raw = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) }).Invoke(App.AssetManager, new object[] { entry });
            byte[] bank; if (raw is byte[] bb) bank = bb; else using (var ms = new MemoryStream()) { ((Stream)raw).CopyTo(ms); bank = ms.ToArray(); }
            byte[] outBank = (byte[])bank.Clone();
            int writes = 0;

            using (var br = new AntBankBrowser(new MemoryStream(bank)))
            {
                var gs = br.Find(enumName, true);
                if (gs.Count == 0) { Console.Error.WriteLine("anim-index-remap: no asset " + enumName); return 1; }
                HashSet<long> scope = null;
                if (roots.Count > 0)
                {
                    var rb = new List<long>();
                    foreach (string r in roots)
                    {
                        var h = br.Find(r, true);
                        if (h.Count == 0) { Console.Error.WriteLine("anim-index-remap: no graph root " + r); return 1; }
                        rb.AddRange(h);
                    }
                    scope = br.Reachable(rb);
                    foreach (string a in also) foreach (long h in br.Find(a, true)) scope.Add(h);
                }
                var choosers = br.Bodies.Where(b => br.TypeName(b) == "IndexChooserControllerAsset" && br.RefField(b, "IndexAsset") == gs[0]
                                                    && (scope == null || scope.Contains(b))).ToList();
                Console.WriteLine("choosers driven by " + enumName + (scope != null ? " in scope" : "") + ": " + choosers.Count);

                foreach (string c in copies)
                {
                    string[] p = c.Split(':'); int from = int.Parse(p[0]), to = int.Parse(p[1]);
                    int changed = 0;
                    foreach (long ch in choosers)
                    {
                        long fa = br.ArrayElementAddr(ch, "ChoiceAssetList", from), ta = br.ArrayElementAddr(ch, "ChoiceAssetList", to);
                        if (fa < 0 || ta < 0) { Console.Error.WriteLine("anim-index-remap: note: " + br.Label(ch) + " has no index " + Math.Max(from, to)); continue; }
                        if (br.ReadKeyAt(fa) == br.ReadKeyAt(ta)) continue;
                        Array.Copy(bank, fa, outBank, ta, 8); changed++; writes++;
                    }
                    formatter.WriteResult("OK", "anim-index-remap", new Dictionary<string, string> { { "copy", from + " -> " + to }, { "entries changed", changed.ToString() } });
                }
                foreach (string s in sets)
                {
                    int colon = s.LastIndexOf(':'), eq = colon < 0 ? -1 : s.IndexOf('=', colon + 1);
                    if (colon < 0 || eq < 0) { Console.Error.WriteLine("anim-index-remap: bad --set " + s); return 1; }
                    string chName = s.Substring(0, colon); int idx = int.Parse(s.Substring(colon + 1, eq - colon - 1)); string target = s.Substring(eq + 1);
                    var chs = br.Find(chName, true).Where(b => br.TypeName(b) == "IndexChooserControllerAsset").ToList();
                    if (chs.Count != 1) { Console.Error.WriteLine("anim-index-remap: chooser " + chName + " matched " + chs.Count); return 1; }
                    var tg = br.Find(target, true);
                    if (tg.Count != 1) { Console.Error.WriteLine("anim-index-remap: target " + target + " matched " + tg.Count); return 1; }
                    long key = br.Key(tg[0]); if (key == 0) { Console.Error.WriteLine("anim-index-remap: target has no key"); return 1; }
                    long ea = br.ArrayElementAddr(chs[0], "ChoiceAssetList", idx); if (ea < 0) { Console.Error.WriteLine("anim-index-remap: no index " + idx); return 1; }
                    Array.Copy(BitConverter.GetBytes(key), 0, outBank, ea, 8); writes++;
                    formatter.WriteResult("OK", "anim-index-remap", new Dictionary<string, string> { { "set", chName + "[" + idx + "]" }, { "to", br.Label(tg[0]) } });
                }
            }
            if (dry) { Console.WriteLine("dry run: " + writes + " entry write(s), nothing saved"); return 0; }
            App.AssetManager.ModifyRes(entry.Name, outBank);
            if (!App.AssetManager.GetResEntry(entry.Name).IsModified) { Console.Error.WriteLine("anim-index-remap: ModifyRes did not take effect."); return 1; }
            return 0;
        }
    }
}
