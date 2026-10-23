using System;
using System.Collections.Generic;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Enlighten;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `enlighten verify`: parse and re-write every shipped Enlighten system database (resType 1892010814) and
    /// static database (3335336582) and compare payload and res meta byte for byte. The writer is only trusted for
    /// edits once this passes on the whole install.
    /// </summary>
    public static class EnlightenVerify
    {
        public static int Execute(string[] args, ExportLogger logger)
        {
            int limit = int.MaxValue;
            string only = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--limit" && i + 1 < args.Length) limit = int.Parse(args[++i]);
                else if (args[i] == "--only" && i + 1 < args.Length) only = args[++i].ToLowerInvariant();
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("enlighten verify: AssetManager not initialized."); return 1; }
            int ok = 0, bad = 0, seen = 0, unmodelled = 0;
            var failures = new List<string>();
            var notes = new List<string>();
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes())
            {
                uint type = Convert.ToUInt32(e.ResType);
                bool isEnd = type == EnlightenDatabase.ResType, isFls = type == StaticEnlightenDatabase.ResType;
                if (!isEnd && !isFls) continue;
                if (only == "end" && !isEnd || only == "fls" && !isFls) continue;
                if (seen++ >= limit) break;
                byte[] data;
                using (Stream s = App.AssetManager.GetRes(e))
                using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                byte[] meta = e.ResMeta;
                try
                {
                    byte[] outMeta, outData;
                    string note = null;
                    if (isEnd)
                    {
                        EnlightenDatabase db = EnlightenDatabase.Parse(data, meta);
                        note = db.UnmodelledReason;
                        outData = db.Write(out outMeta);
                    }
                    else
                    {
                        StaticEnlightenDatabase sdb = StaticEnlightenDatabase.Parse(data, meta);
                        if (sdb.Unmodelled != null) note = "placeholder (" + data.Length + " bytes)";
                        outData = sdb.Write(out outMeta);
                    }
                    if (note != null) { unmodelled++; notes.Add(e.Name + ": " + note); }
                    string diff = Diff(data, outData, meta, outMeta);
                    if (diff == null) ok++;
                    else { bad++; failures.Add(e.Name + ": " + diff); }
                }
                catch (Exception ex) { bad++; failures.Add(e.Name + ": " + ex.Message); }
            }
            foreach (string f in notes) Console.Out.WriteLine("RAW  " + f);
            foreach (string f in failures) Console.Out.WriteLine("FAIL " + f);
            Console.Out.WriteLine(string.Format("enlighten verify: {0} databases byte-exact ({1} of them kept raw: not modelled), {2} failed", ok, unmodelled, bad));
            return bad == 0 ? 0 : 1;
        }

        private static string Diff(byte[] a, byte[] b, byte[] ma, byte[] mb)
        {
            if (a.Length != b.Length) return "length " + a.Length + " -> " + b.Length;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return "first difference at 0x" + i.ToString("X");
            if (ma != null && ma.Length >= 16)
                for (int i = 0; i < 16; i++) if (ma[i] != mb[i]) return "res meta differs at byte " + i;
            return null;
        }
    }
}
