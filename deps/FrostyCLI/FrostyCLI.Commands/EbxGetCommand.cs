using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    public class EbxGetCommand : ICommand
    {
        public string Name => "ebx";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: frostycli ebx <get|set|clone|dump|search|refs|diff-shipped|delete|revert|set-ref|create|new-object|array-add|array-remove|graph-export|graph-plan|graph-import|schema> ...");
                return 1;
            }
            switch (args[0].ToLowerInvariant())
            {
                case "get":    return ExecuteGet(SubArgs(args, 1), logger, formatter);
                case "set":    return EbxWriteCommand.ExecuteSet(SubArgs(args, 1), logger, formatter);
                case "clone":  return EbxWriteCommand.ExecuteClone(SubArgs(args, 1), logger, formatter);
                case "dump":   return ExecuteDump(SubArgs(args, 1), logger, formatter);
                case "search": return ExecuteSearch(SubArgs(args, 1), logger, formatter);
                case "refs":   return ExecuteRefs(SubArgs(args, 1), logger, formatter);
                case "delete":  return EbxWriteCommand.ExecuteDelete(SubArgs(args, 1), logger, formatter);
                case "revert":  return EbxWriteCommand.ExecuteRevert(SubArgs(args, 1), logger, formatter);
                case "set-ref": return EbxWriteCommand.ExecuteSetRef(SubArgs(args, 1), logger, formatter);
                case "create":  return EbxWriteCommand.ExecuteCreate(SubArgs(args, 1), logger, formatter);
                case "new-object": return EbxWriteCommand.ExecuteNewObject(SubArgs(args, 1), logger, formatter);
                case "array-add":    return EbxWriteCommand.ExecuteArrayAdd(SubArgs(args, 1), logger, formatter);
                case "array-remove": return EbxWriteCommand.ExecuteArrayRemove(SubArgs(args, 1), logger, formatter);
                case "replace-string": return EbxWriteCommand.ExecuteReplaceString(SubArgs(args, 1), logger, formatter);
                case "diff-shipped": return EbxDiffShippedCommand.Execute(SubArgs(args, 1), logger, formatter);
                case "graph-export":
                case "graph-plan":
                case "graph-import":
                case "schema":       return EbxGraphCommand.Execute(args[0].ToLowerInvariant(), SubArgs(args, 1), logger, formatter);
                default:
                    Console.Error.WriteLine("ebx: unknown subcommand '" + args[0] + "'. Use get, set, clone, dump, search, refs, diff-shipped, delete, revert, set-ref, create, new-object, array-add, or array-remove.");
                    return 1;
            }
        }

        private static string[] SubArgs(string[] args, int skip)
        {
            var r = new string[args.Length - skip];
            Array.Copy(args, skip, r, 0, r.Length);
            return r;
        }

        // ── get ───────────────────────────────────────────────────────────────

        private int ExecuteGet(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null;
            string format  = "json";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length)
                    format = args[++i].ToLowerInvariant();
                else if (!args[i].StartsWith("--"))
                    ebxPath = args[i];
            }

            if (ebxPath == null) { Console.Error.WriteLine("ebx get: missing <ebx-path>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx get: AssetManager not initialized."); return 1; }

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
            if (entry == null) { Console.Error.WriteLine("ebx get: entry not found: " + ebxPath); return 1; }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("ebx get: load failed: " + ex.Message); return 1; }

            try
            {
                object rootObj = asset.RootObject;
                if (format == "json")
                {
                    Console.Out.WriteLine(JsonConvert.SerializeObject(rootObj, Newtonsoft.Json.Formatting.Indented));
                }
                else if (format == "xml")
                {
                    string json = JsonConvert.SerializeObject(rootObj);
                    var xmlDoc = JsonConvert.DeserializeXmlNode(json, "root");
                    var sb = new StringBuilder();
                    using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true }))
                        xmlDoc.Save(writer);
                    Console.Out.WriteLine(sb.ToString());
                }
                else { Console.Error.WriteLine("ebx get: unknown format '" + format + "'"); return 1; }
            }
            catch (Exception ex) { Console.Error.WriteLine("ebx get: serialization failed: " + ex.Message); return 1; }
            return 0;
        }

        // ── dump ──────────────────────────────────────────────────────────────

        public static int ExecuteDump(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath   = null;
            string outFile   = null;
            string rawFile   = null;
            bool   noResolve = false;
            bool   deep      = false;
            string objectSel = null;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--object"     && i + 1 < args.Length) objectSel = args[++i];
                else if (args[i] == "--out"        && i + 1 < args.Length) outFile   = args[++i];
                else if (args[i] == "--file"       && i + 1 < args.Length) rawFile   = args[++i];
                else if (args[i] == "--no-resolve")                         noResolve = true;
                else if (args[i] == "--deep" || args[i] == "--all-objects") deep      = true;
                else if (!args[i].StartsWith("--"))                         ebxPath   = args[i];
            }

            if (ebxPath == null && rawFile == null) { Console.Error.WriteLine("ebx dump: missing <ebx-path> (or --file <raw-ebx-file>)"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx dump: AssetManager not initialized."); return 1; }

            EbxAsset asset;
            if (rawFile != null)
            {
                // A raw EBX partition on disk (for instance one pulled out of a .fbmod), read with the
                // game's type library loaded so external references still resolve to names.
                try
                {
                    byte[] raw = File.ReadAllBytes(rawFile);
                    // A .fbmod payload is a CAS-compressed block (zstd/LZ4/Oodle behind a Frostbite block
                    // header); a plain partition starts with the EBX magic. Decompress when needed.
                    byte[] plain = raw;
                    if (raw.Length > 8 && !(raw[0] == 0xCE && raw[1] == 0xD1 && raw[2] == 0xB2 && raw[3] == 0x0F) && !(raw[0] == 0xCE && raw[1] == 0xD1 && raw[2] == 0xB4 && raw[3] == 0x0F))
                    {
                        try { using (var cr = new FrostySdk.IO.CasReader(new MemoryStream(raw))) plain = cr.Read(); }
                        catch { plain = raw; }
                    }
                    using (var reader = FrostySdk.IO.EbxReader.CreateReader(new MemoryStream(plain), App.FileSystem, true))
                        asset = reader.ReadAsset<EbxAsset>();
                }
                catch (Exception ex) { Console.Error.WriteLine("ebx dump: could not read " + rawFile + ": " + ex.Message); return 1; }
                int nObjects = 0; foreach (object o in asset.Objects) nObjects++;
                if (nObjects == 0) { Console.Error.WriteLine("ebx dump: " + rawFile + " holds no objects (not an EBX partition?)"); return 1; }
            }
            else
            {
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(ebxPath);
                if (entry == null) { Console.Error.WriteLine("ebx dump: entry not found: " + ebxPath); return 1; }
                try { asset = App.AssetManager.GetEbx(entry); }
                catch (Exception ex) { Console.Error.WriteLine("ebx dump: load failed: " + ex.Message); return 1; }
            }

            JObject jo;
            if (objectSel != null)
            {
                // One partition object (by exported instance guid), shallow, internal refs named.
                Guid sel; if (!Guid.TryParse(objectSel, out sel)) { Console.Error.WriteLine("ebx dump: --object needs an instance guid"); return 1; }
                object found = null;
                foreach (object o in asset.Objects)
                {
                    if (o == null) continue;
                    try { dynamic d = o; if (((Guid)d.GetInstanceGuid().ExportedGuid) == sel) { found = o; break; } } catch { }
                }
                if (found == null) { Console.Error.WriteLine("ebx dump: --object not found: " + objectSel); return 1; }
                try { jo = EbxDumper.DumpRootObject(found, !noResolve, false); }
                catch (Exception ex) { Console.Error.WriteLine("ebx dump: serialization failed: " + ex.Message); return 1; }
            }
            else
            try { jo = EbxDumper.Dump(asset, resolve: !noResolve, deep: deep); }
            catch (Exception ex) { Console.Error.WriteLine("ebx dump: serialization failed: " + ex.Message); return 1; }

            string output = jo.ToString(Newtonsoft.Json.Formatting.Indented);
            if (outFile != null)
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, output);
            }
            else
            {
                Console.Out.WriteLine(output);
            }
            return 0;
        }

        // ── search ────────────────────────────────────────────────────────────

        public static int ExecuteSearch(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string prop       = null;
            string value      = null;
            string typeFilter = null;
            string outFile    = null;
            bool   useRegex   = false;
            int    limit      = 100;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--prop"  && i + 1 < args.Length) prop       = args[++i];
                else if (args[i] == "--value" && i + 1 < args.Length) value      = args[++i];
                else if (args[i] == "--type"  && i + 1 < args.Length) typeFilter = args[++i];
                else if (args[i] == "--out"   && i + 1 < args.Length) outFile    = args[++i];
                else if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
                else if (args[i] == "--regex")                          useRegex   = true;
            }

            if (prop  == null) { Console.Error.WriteLine("ebx search: missing --prop");  return 1; }
            if (value == null) { Console.Error.WriteLine("ebx search: missing --value"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("ebx search: AssetManager not initialized."); return 1; }

            Regex rx = null;
            if (useRegex)
            {
                try { rx = new Regex(value); }
                catch (Exception ex) { Console.Error.WriteLine("ebx search: invalid regex: " + ex.Message); return 1; }
            }

            AssetCatalog catalog = IndexService.GetCatalog();
            var results = new JArray();
            // "Name" is synthesized by the dumper from the asset path, not a real root property on most
            // types -- so match the path-derived name directly (and skip loading every asset, which made
            // `--prop Name` both empty AND slow). beta bug #4.
            bool isNameProp = string.Equals(prop, "Name", StringComparison.OrdinalIgnoreCase);

            foreach (var rec in catalog.Assets)
            {
                if (limit >= 0 && results.Count >= limit) break;
                if (typeFilter != null && !string.Equals(rec.Type, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

                string strVal;
                if (isNameProp)
                {
                    int slash = rec.Path.LastIndexOf('/');
                    strVal = slash >= 0 ? rec.Path.Substring(slash + 1) : rec.Path;
                }
                else
                {
                    EbxAssetEntry entry = App.AssetManager.GetEbxEntry(rec.Path);
                    if (entry == null) continue;
                    EbxAsset asset;
                    try { asset = App.AssetManager.GetEbx(entry); }
                    catch { continue; }

                    object leafVal;
                    try { leafVal = PropertyPathReader.GetValue(asset.RootObject, prop); }
                    catch { continue; } // property not found -- skip
                    if (leafVal == null) continue;

                    // Stringify by ToString(), NOT JSON: a CString/string compares unquoted (was wrapped
                    // in JSON quotes) and an enum compares by NAME (JSON rendered it as a number) -- both
                    // made `ebx search` silently miss real values.
                    strVal = SearchStringify(leafVal);
                }

                bool match = useRegex
                    ? rx.IsMatch(strVal)
                    : string.Equals(strVal, value, isNameProp ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (!match) continue;

                results.Add(new JObject { ["path"] = rec.Path, ["type"] = rec.Type, ["value"] = strVal });
            }

            string output = results.ToString(Newtonsoft.Json.Formatting.Indented);
            if (outFile != null) File.WriteAllText(outFile, output);
            else Console.Out.WriteLine(output);
            return 0;
        }

        // Renders a leaf value for `ebx search` comparison: strings/CStrings unquoted, enums by name,
        // primitives invariant; complex objects fall back to JSON.
        private static string SearchStringify(object v)
        {
            if (v == null) return null;
            if (v is string s) return s;
            Type t = v.GetType();
            if (t.IsEnum) return v.ToString();           // enum NAME, not its numeric value
            if (t.Name == "CString") return v.ToString();
            if (t.IsPrimitive || t == typeof(decimal) || t == typeof(Guid))
                return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            string str = v.ToString();
            // If ToString() is just the type name (default object ToString), JSON is more useful.
            return (str == t.FullName || str == t.ToString()) ? JsonConvert.SerializeObject(v) : str;
        }

        // ── refs ──────────────────────────────────────────────────────────────

        public static int ExecuteRefs(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string ebxPath = null;
            string guidStr = null;
            string outFile = null;
            int    depth   = 1;
            int    limit   = 1000;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--guid"  && i + 1 < args.Length) guidStr = args[++i];
                else if (args[i] == "--out"   && i + 1 < args.Length) outFile = args[++i];
                else if (args[i] == "--depth" && i + 1 < args.Length) int.TryParse(args[++i], out depth);
                else if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
                else if (!args[i].StartsWith("--"))                    ebxPath = args[i];
            }

            if (ebxPath == null && guidStr == null)
            { Console.Error.WriteLine("ebx refs: missing <path> or --guid <fileGuid>"); return 1; }
            if (App.AssetManager == null)
            { Console.Error.WriteLine("ebx refs: AssetManager not initialized."); return 1; }

            // Resolve to fileGuid string
            string fileGuidStr = guidStr;
            if (fileGuidStr == null)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(ebxPath);
                if (e == null) { Console.Error.WriteLine("ebx refs: entry not found: " + ebxPath); return 1; }
                fileGuidStr = e.Guid.ToString("D");
            }

            RefIndex index      = IndexService.GetRefIndex();
            AssetCatalog catalog = IndexService.GetCatalog();

            // Build path→type map once for O(1) lookups in BFS
            var typeByPath = new Dictionary<string, string>(catalog.Assets.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var r in catalog.Assets)
                if (!string.IsNullOrEmpty(r.Path) && !typeByPath.ContainsKey(r.Path))
                    typeByPath[r.Path] = r.Type ?? "";

            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue   = new Queue<string>();
            var results = new JArray();

            // Seed with direct refs
            List<string> direct;
            if (index.Refs.TryGetValue(fileGuidStr, out direct))
                foreach (string p in direct) if (seen.Add(p)) queue.Enqueue(p);

            int hop = 0;
            while (queue.Count > 0 && (depth < 0 || hop < depth) && (limit < 0 || results.Count < limit))
            {
                int sz = queue.Count;
                for (int i = 0; i < sz; i++)
                {
                    if (limit >= 0 && results.Count >= limit) break;
                    string p = queue.Dequeue();

                    string type;
                    if (!typeByPath.TryGetValue(p, out type)) type = "";

                    results.Add(new JObject { ["path"] = p, ["type"] = type });

                    if (depth < 0 || hop + 1 < depth)
                    {
                        EbxAssetEntry e2 = App.AssetManager.GetEbxEntry(p);
                        if (e2 != null)
                        {
                            string fg2 = e2.Guid.ToString("D");
                            List<string> next;
                            if (index.Refs.TryGetValue(fg2, out next))
                                foreach (string np in next)
                                    if (seen.Add(np)) queue.Enqueue(np);
                        }
                    }
                }
                hop++;
            }

            string output = results.ToString(Newtonsoft.Json.Formatting.Indented);
            if (outFile != null) File.WriteAllText(outFile, output);
            else Console.Out.WriteLine(output);
            return 0;
        }
    }
}
