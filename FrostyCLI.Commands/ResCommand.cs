using System.Collections.Generic;
using System;
using System.IO;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    public class ResCommand : ICommand
    {
        public string Name => "res";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: frostycli res <ls|export|import|copy|revert|meta> ...");
                return 1;
            }
            switch (args[0].ToLowerInvariant())
            {
                case "ls":     return ExecuteLs(Sub(args, 1), logger, formatter);
                case "export": return ExecuteExport(Sub(args, 1), logger, formatter);
                case "import": return ExecuteImport(Sub(args, 1), logger, formatter);
                case "copy":   return ExecuteCopy(Sub(args, 1), logger, formatter);
                case "revert": return ExecuteRevert(Sub(args, 1), logger, formatter);
                case "meta":   return ExecuteMeta(Sub(args, 1), logger, formatter);
                default:
                    Console.Error.WriteLine("res: unknown subcommand '" + args[0] + "'. Use ls, export, import, copy, revert or meta.");
                    return 1;
            }
        }

        private static string[] Sub(string[] args, int skip)
        {
            var r = new string[args.Length - skip];
            Array.Copy(args, skip, r, 0, r.Length);
            return r;
        }

        public static int ExecuteLs(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string filter  = null;
            string outFile = null;
            int    limit   = 500;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--out"   && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
                else if (!args[i].StartsWith("--"))                    filter  = args[i];
            }

            if (App.AssetManager == null) { Console.Error.WriteLine("res ls: AssetManager not initialized."); return 1; }

            var results = new JArray();
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes())
            {
                if (limit >= 0 && results.Count >= limit) break;
                if (filter != null && !e.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new JObject
                {
                    ["path"]   = e.Name,
                    ["type"]   = e.ResType.ToString(),
                    ["resRid"] = e.ResRid.ToString()
                });
            }

            string output = results.ToString(Formatting.Indented);
            if (outFile != null) File.WriteAllText(outFile, output);
            else Console.Out.WriteLine(output);
            return 0;
        }


        // ModifiedEntry/DataObject are a property on some SDK builds and a field on others.
        private static object Member(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name);
            if (p != null) return p.GetValue(o);
            var f = o.GetType().GetField(name);
            return f == null ? null : f.GetValue(o);
        }
        public static int ExecuteExport(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string resPath = null;
            string outFile = null;
            // --framed: write the 16-byte res meta first, the format `res import --framed` reads back.
            bool framed = false;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--framed")                      framed = true;
                else if (!args[i].StartsWith("--"))                  resPath = args[i];
            }

            if (resPath == null) { Console.Error.WriteLine("res export: missing <path>"); return 1; }
            if (outFile == null) { Console.Error.WriteLine("res export: missing --out");  return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("res export: AssetManager not initialized."); return 1; }

            ResAssetEntry entry = App.AssetManager.GetResEntry(resPath);
            if (entry == null) { Console.Error.WriteLine("res export: entry not found: " + resPath); return 1; }

            byte[] data = null;

            // A res modified through ModifyRes(id, Resource) keeps only the live resource object
            // until a mod is built, so GetRes would hand back the VANILLA bytes and an export would
            // silently lose the edit. Serialize the modified resource instead -- the same bytes mod
            // build would write -- so the result can be re-imported as raw bytes (which avoids the
            // custom action handlers entirely).
            try
            {
                object me = Member(entry, "ModifiedEntry");
                object dobj = me == null ? null : Member(me, "DataObject");
                if (Environment.GetEnvironmentVariable("FROSTYCLI_EXPORT_DEBUG") != null)
                    logger.LogWarning("res export: {0} modifiedEntry={1} dataObject={2}",
                        resPath, me == null ? "null" : me.GetType().Name,
                        dobj == null ? "null" : dobj.GetType().FullName);
                if (dobj != null)
                {
                    System.Reflection.MethodInfo sb = dobj.GetType().GetMethod("SaveBytes", Type.EmptyTypes);
                    if (sb != null) data = sb.Invoke(dobj, null) as byte[];
                    if (data != null) logger.LogWarning("res export: wrote MODIFIED resource ({0} bytes) for {1}", data.Length, resPath);
                }
            }
            catch (Exception ex) { logger.LogWarning("res export: modified-resource path failed: {0}", ex.Message); data = null; }

            if (data == null)
            try
            {
                // GetRes may return byte[] or Stream depending on SDK version
                System.Reflection.MethodInfo m = App.AssetManager.GetType()
                    .GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
                object raw = m?.Invoke(App.AssetManager, new object[] { entry });
                if (raw is byte[] bytes)
                    data = bytes;
                else if (raw is Stream stream)
                    { using (var ms = new MemoryStream()) { stream.CopyTo(ms); data = ms.ToArray(); } }
                else
                    { Console.Error.WriteLine("res export: unexpected GetRes return type"); return 1; }
            }
            catch (Exception ex) { Console.Error.WriteLine("res export: read failed: " + ex.Message); return 1; }

            if (framed)
            {
                byte[] meta = RawMeta(entry);
                if (meta == null || meta.Length != 16)
                { Console.Error.WriteLine("res export --framed: " + resPath + " has no 16-byte res meta"); return 1; }
                byte[] both = new byte[16 + data.Length];
                Array.Copy(meta, both, 16);
                Array.Copy(data, 0, both, 16, data.Length);
                data = both;
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outFile, data);
            return 0;
        }

        // `res meta <res>` prints a resource's 16-byte res meta: the header the game reads before the
        // data (a ShaderBlockDepot's is {u16 version; u16; u32 dataLength; u32 relocTableBytes;
        // u32 resourceCount}, an RVM database's {version; payload length; header length; hash}). A
        // resized resource must ship with a matching meta (`res import --framed`): imported raw, it
        // keeps the old one and the loader walks garbage (SWBF2: a write fault at game+0x5498867).
        public static int ExecuteMeta(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string name = null;
            foreach (string a in args) if (!a.StartsWith("--")) { name = a; break; }
            if (name == null) { Console.Error.WriteLine("res meta: missing <res>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("res meta: AssetManager not initialized."); return 1; }
            ResAssetEntry entry = App.AssetManager.GetResEntry(name);
            if (entry == null) { Console.Error.WriteLine("res meta: entry not found: " + name); return 1; }

            var result = new Dictionary<string, string>
            {
                { "res", entry.Name },
                { "type", entry.ResType.ToString("X8") },
                { "resRid", entry.ResRid.ToString("X16") },
                { "modified", entry.IsModified ? "true" : "false" }
            };
            AddMeta(result, "meta", RawMeta(entry));
            // a typed modification carries its own, possibly different, meta (see LiveResourceMeta)
            byte[] live = LiveResourceMeta(entry);
            if (live != null) AddMeta(result, "resourceMeta", live);
            formatter.WriteResult("OK", "res meta", result);
            return 0;
        }

        private static void AddMeta(Dictionary<string, string> result, string key, byte[] meta)
        {
            var words = new List<string>();
            if (meta != null)
                for (int i = 0; i + 4 <= meta.Length; i += 4) words.Add(BitConverter.ToUInt32(meta, i).ToString("X8"));
            result[key] = meta == null ? "" : BitConverter.ToString(meta).Replace("-", "").ToLowerInvariant();
            result[key + "U32"] = string.Join(" ", words);
        }

        // The meta that goes with the bytes GetRes returns: a raw modification's own (`res import`),
        // else the shipped one.
        private static byte[] RawMeta(ResAssetEntry entry)
        {
            object me = MetaMember(entry, "ModifiedEntry");
            if (me != null && MetaMember(me, "ResMeta") is byte[] raw && raw.Length > 0) return raw;
            return entry.ResMeta;
        }

        // A typed modification (ModifyRes(id, Resource)) keeps its meta in the resource object, and
        // SaveBytes brings it up to date -- e.g. a ShaderBlockDepot's data length after a parameter was
        // added. Null when the entry has no such modification.
        private static byte[] LiveResourceMeta(ResAssetEntry entry)
        {
            object me = MetaMember(entry, "ModifiedEntry");
            object dobj = me == null ? null : MetaMember(me, "DataObject");
            if (dobj == null) return null;
            System.Reflection.MethodInfo sb = dobj.GetType().GetMethod("SaveBytes", Type.EmptyTypes);
            if (sb == null) return null;
            sb.Invoke(dobj, null);
            return MetaMember(dobj, "ResourceMeta") as byte[];
        }

        // Members that are a property on some SDK builds and a field on others.
        private static object MetaMember(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name);
            if (p != null) return p.GetValue(o);
            var f = o.GetType().GetField(name);
            return f == null ? null : f.GetValue(o);
        }

        // Imports raw bytes from a file into a Res asset. Replaces an existing Res (ModifyRes), or
        // creates a new one when --type <ResType> is given and the Res doesn't exist (AddRes). The
        // buffer is the decompressed bytes (e.g. what `res export` writes); ModifyRes/AddRes compress.
        // `res copy <source-res> --to <target-res> [--fast]` replaces a resource's bytes and meta with
        // another resource's. Built for swapping a level's RVM shader databases for a level's that
        // carries the permutations the ported props need; --fast keeps the intermediate LZ4
        // compression (the shipping zstd-18 pass would take minutes per hundred megabytes).
        // Drops the project's modification of a shipped resource so the game's own bytes are used
        // again. A resource the project added is removed instead.
        public static int ExecuteRevert(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string name = null;
            foreach (string a in args) if (!a.StartsWith("--")) { name = a; break; }
            if (name == null) { Console.Error.WriteLine("res revert: missing <res-name>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("res revert: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("res revert: AssetManager not initialized."); return 1; }
            ResAssetEntry entry = App.AssetManager.GetResEntry(name);
            if (entry == null) { Console.Error.WriteLine("res revert: resource not found: " + name); return 1; }
            if (!entry.IsModified && !entry.IsAdded) { Console.Error.WriteLine("res revert: resource is not modified in this project: " + name); return 1; }
            if (!EbxWriteCommand.CallRevertAsset(entry, logger)) return 1;
            formatter.WriteResult("OK", "res revert", new Dictionary<string, string> { { "res", name }, { "was", entry.IsAdded ? "added" : "modified" } });
            return 0;
        }

        public static int ExecuteCopy(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string source = null, target = null; bool fast = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--to" && i + 1 < args.Length) target = args[++i];
                else if (args[i] == "--fast") fast = true;
                else if (!args[i].StartsWith("--")) source = args[i];
            }
            if (source == null) { Console.Error.WriteLine("res copy: missing <source-res>"); return 1; }
            if (target == null) { Console.Error.WriteLine("res copy: missing --to <target-res>"); return 1; }
            if (!Session.Project.IsLoaded) { Console.Error.WriteLine("res copy: no project loaded."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("res copy: AssetManager not initialized."); return 1; }
            ResAssetEntry src = App.AssetManager.GetResEntry(source);
            if (src == null) { Console.Error.WriteLine("res copy: source not found: " + source); return 1; }
            ResAssetEntry dst = App.AssetManager.GetResEntry(target);
            if (dst == null) { Console.Error.WriteLine("res copy: target not found: " + target); return 1; }
            if (src.ResType != dst.ResType) { Console.Error.WriteLine("res copy: resource types differ (" + src.ResType + " vs " + dst.ResType + ")"); return 1; }
            byte[] data, meta = src.ModifiedEntry != null && src.ModifiedEntry.ResMeta != null ? src.ModifiedEntry.ResMeta : src.ResMeta;
            try
            {
                var m = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
                object raw = m != null ? m.Invoke(App.AssetManager, new object[] { src }) : null;
                data = raw as byte[];
                if (data == null && raw is System.IO.Stream) { using (var st = (System.IO.Stream)raw) using (var ms = new System.IO.MemoryStream()) { st.CopyTo(ms); data = ms.ToArray(); } }
                if (data == null) { Console.Error.WriteLine("res copy: could not read " + source); return 1; }
            }
            catch (Exception ex) { Console.Error.WriteLine("res copy: read failed: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return 1; }
            if (DryRun.Active) { DryRun.Report(logger, "copy {0} ({1} bytes) over {2}", source, data.Length, target); return 0; }
            try
            {
                ResWriter.Fast = fast;
                ResWriter.Modify(dst.Name, data, meta);
                ResWriter.Fast = false;
            }
            catch (Exception ex) { ResWriter.Fast = false; Console.Error.WriteLine("res copy: write failed: " + ex.Message); return 1; }
            formatter.WriteResult("OK", "res copy", new Dictionary<string, string>
            { { "source", src.Name }, { "target", dst.Name }, { "bytes", data.Length.ToString() }, { "meta", meta != null ? meta.Length.ToString() : "0" }, { "compression", fast ? "lz4" : "shipping" } });
            return 0;
        }

        public static int ExecuteImport(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            bool framed = false;
            string resPath  = null;
            string inFile   = null;
            string typeStr  = null;
            string metaFrom = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--as" && i + 1 < args.Length) resPath = args[++i];
                else if (args[i] == "--framed") framed = true;
                else if (args[i] == "--type" && i + 1 < args.Length) typeStr = args[++i];
                else if (args[i] == "--meta-from" && i + 1 < args.Length) metaFrom = args[++i];
                else if (!args[i].StartsWith("--")) inFile = args[i];
            }

            if (inFile == null)  { Console.Error.WriteLine("res import: missing <file>"); return 1; }
            if (resPath == null) { Console.Error.WriteLine("res import: missing --as <res-path>"); return 1; }
            if (!File.Exists(inFile)) { Console.Error.WriteLine("res import: file not found: " + inFile); return 1; }
            if (!Session.Project.IsLoaded)
            { Console.Error.WriteLine("res import: no project loaded. Run 'project load' or 'project new' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("res import: AssetManager not initialized."); return 1; }

            byte[] data;
            try { data = File.ReadAllBytes(inFile); }
            catch (Exception ex) { Console.Error.WriteLine("res import: could not read " + inFile + ": " + ex.Message); return 1; }
            // --framed: the file starts with the 16-byte res meta (the framing mount_game --dump-res
            // and rvm_merge write); it becomes the entry's ResMeta and the rest is the payload.
            byte[] framedMeta = null;
            if (framed)
            {
                if (data.Length < 16) { Console.Error.WriteLine("res import: --framed file is shorter than 16 bytes"); return 1; }
                framedMeta = new byte[16]; Array.Copy(data, 0, framedMeta, 0, 16);
                byte[] payload = new byte[data.Length - 16]; Array.Copy(data, 16, payload, 0, payload.Length); data = payload;
            }
            try { }
            catch (Exception ex) { Console.Error.WriteLine("res import: read failed: " + ex.Message); return 1; }

            if (DryRun.Active) { DryRun.Report(logger, "import {0} bytes into res {1}{2}", data.Length, resPath, typeStr != null ? " (--type " + typeStr + ")" : ""); return 0; }

            ResAssetEntry entry = App.AssetManager.GetResEntry(resPath);
            if (entry == null)
            {
                // Create-new path: requires --type <ResType> (enum name, case-insensitive, or numeric).
                if (typeStr == null)
                { Console.Error.WriteLine("res import: res not found: " + resPath + " — pass --type <ResType> to create a new Res."); return 1; }

                FrostySdk.Managers.ResourceType resType;
                uint typeNum;
                if (Enum.TryParse(typeStr, true, out resType)) { /* parsed by name */ }
                else if (uint.TryParse(typeStr, out typeNum)) { resType = (FrostySdk.Managers.ResourceType)typeNum; }
                else { Console.Error.WriteLine("res import: invalid --type '" + typeStr + "' (use a ResourceType name or numeric value)."); return 1; }

                // A new Res created with an EMPTY meta cannot be read back after a project save --
                // reloading it throws NullReferenceException, the same class of failure as the Havok
                // cookie. --meta-from <res> copies the meta off a shipped resource of the same type,
                // which is what makes the new entry round-trip.
                byte[] newMeta = framedMeta ?? new byte[0];
                if (metaFrom != null)
                {
                    ResAssetEntry donor = App.AssetManager.GetResEntry(metaFrom);
                    if (donor == null)
                    { Console.Error.WriteLine("res import: --meta-from res not found: " + metaFrom); return 1; }
                    byte[] dm = donor.ModifiedEntry != null && donor.ModifiedEntry.ResMeta != null
                                ? donor.ModifiedEntry.ResMeta : donor.ResMeta;
                    if (dm != null) newMeta = (byte[])dm.Clone();
                    if (donor.ResType != (uint)resType)
                        logger.LogWarning("res import: --meta-from type {0} differs from --type {1}", donor.ResType, (uint)resType);
                }
                try { App.AssetManager.AddRes(resPath, resType, newMeta, data); }
                catch (Exception ex) { Console.Error.WriteLine("res import: add failed: " + ex.Message); return 1; }

                if (App.AssetManager.GetResEntry(resPath) == null)
                { Console.Error.WriteLine("res import: AddRes did not create " + resPath); return 1; }

                formatter.WriteResult("OK", "res import",
                    new System.Collections.Generic.Dictionary<string, string> { { "res", resPath }, { "created", "true" }, { "type", resType.ToString() }, { "bytes", data.Length.ToString() }, { "meta", newMeta.Length.ToString() } });
                return 0;
            }

            try { if (framedMeta != null) App.AssetManager.ModifyRes(entry.Name, data, framedMeta); else App.AssetManager.ModifyRes(entry.Name, data); }
            catch (Exception ex) { Console.Error.WriteLine("res import: modify failed: " + ex.Message); return 1; }

            if (!App.AssetManager.GetResEntry(resPath).IsModified)
            { Console.Error.WriteLine("res import: ModifyRes did not take effect for " + resPath); return 1; }

            formatter.WriteResult("OK", "res import",
                new System.Collections.Generic.Dictionary<string, string> { { "res", resPath }, { "bytes", data.Length.ToString() } });
            return 0;
        }
    }
}
