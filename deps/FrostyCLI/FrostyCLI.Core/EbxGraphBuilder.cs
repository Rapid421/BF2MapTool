using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Builds a NEW EBX asset in the current game from a destination-ready <see cref="EbxGraphPackage"/> (its Game must
    /// be the session's game: the package has already been converted into this game's schema).
    ///
    /// Nothing is inferred: every type must exist, every field named by the package must exist and take the value's
    /// kind, every external reference must resolve (by file guid, else by asset path; instance guid, else the root),
    /// and with <c>strict</c> every destination field the package does not name is an error -- so a successful build
    /// accounts for every field of every object. Instance guids are deterministic (MD5 of asset name + object index),
    /// exported-ness follows the package, and entity Flags keep their real flag byte while the identity bits follow the
    /// new guid (the invariant every shipped exported object obeys: Flags &amp; 0xFFFFFF == Data1 &amp; 0xFFFFFF).
    /// The asset is written and re-read through the binary EBX writer before it is accepted, and after the import it
    /// is captured again from the asset manager; both captures must equal the package field for field.
    /// </summary>
    public static class EbxGraphBuilder
    {
        public sealed class Prepared
        {
            public EbxAsset Asset;
            public JObject Report;
            public EbxGraphPackage Expected;
            public readonly HashSet<EbxAssetEntry> Dependencies = new HashSet<EbxAssetEntry>();
            public bool Ready => Asset != null;
        }

        public sealed class Options
        {
            public bool Strict;
            public bool KeepFlags;         // copy Flags verbatim instead of re-deriving the identity bits
        }

        /// <summary>Deterministic guid for (asset name, salt): MD5 of the lower-cased name and salt, RFC-4122 v3 bits.</summary>
        public static Guid DeterministicGuid(string name, string salt)
        {
            using (var md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes((name ?? "").ToLowerInvariant() + "|" + salt));
                h[7] = (byte)((h[7] & 0x0F) | 0x30);
                h[8] = (byte)((h[8] & 0x3F) | 0x80);
                return new Guid(h);
            }
        }

        /// <summary>Flags with the identity bits of <paramref name="guid"/> and the real flag byte of <paramref name="source"/>.</summary>
        public static uint IdentityFlags(uint source, Guid guid)
        {
            uint data1 = BitConverter.ToUInt32(guid.ToByteArray(), 0);
            return (source & 0xFF000000u) | (data1 & 0x00FFFFFFu);
        }

        public static Prepared Prepare(EbxGraphPackage package, string name, Options options)
        {
            options = options ?? new Options();
            package.Validate();
            var prepared = new Prepared();
            var issues = new JArray(); var defaults = new JArray(); var notes = new JArray();
            prepared.Report = new JObject { ["asset"] = name, ["package_game"] = package.Game, ["destination_game"] = Session.GameName,
                ["root_type"] = package.RootType, ["objects"] = package.Objects.Count, ["ready"] = false,
                ["issues"] = issues, ["unassigned_destination_fields"] = defaults, ["notes"] = notes };
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A destination name is required.");
            if (package.Game != Session.GameName)
            {
                issues.Add("Package is in " + package.Game + " schema; convert it into " + Session.GameName + " first (graph-import builds destination-ready packages only).");
                return prepared;
            }

            var objects = new object[package.Objects.Count];
            for (int i = 0; i < objects.Length; i++)
            {
                string type = (string)package.Objects[i]["type"];
                try { objects[i] = TypeLibrary.CreateObject(type) ?? throw new InvalidDataException("Destination SDK lacks type " + type); }
                catch (Exception ex) { issues.Add("objects[" + i + "]: " + ex.Message); }
            }
            if (issues.Count != 0) return prepared;

            Func<JObject, PointerRef> resolve = spec => {
                EbxAssetEntry entry = null;
                if (Guid.TryParse((string)spec["file_guid"], out Guid fileGuid) && fileGuid != Guid.Empty) entry = App.AssetManager.GetEbxEntry(fileGuid);
                string path = (string)spec["asset"];
                if (entry == null && !string.IsNullOrWhiteSpace(path)) entry = App.AssetManager.GetEbxEntry(path);
                if (entry == null) throw new InvalidDataException("Destination dependency not found: " + (path ?? (string)spec["file_guid"]));
                if (!string.IsNullOrWhiteSpace(path) && !string.Equals(entry.Name, path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Dependency file guid " + fileGuid + " is " + entry.Name + ", not " + path);
                var asset = App.AssetManager.GetEbx(entry);
                object target = asset.RootObject;
                if (spec["class_guid"] != null && spec["class_guid"].Type != JTokenType.Null)
                {
                    Guid guid = Guid.Parse((string)spec["class_guid"]);
                    target = asset.Objects.FirstOrDefault(o => EbxGraphPackage.IsDataContainer(o.GetType()) && EbxGraphPackage.InstanceGuidOf(o).ExportedGuid == guid);
                    if (target == null) throw new InvalidDataException("Exported object " + guid + " not found in " + entry.Name);
                }
                AssetClassGuid targetGuid = EbxGraphPackage.InstanceGuidOf(target);
                if (!targetGuid.IsExported) throw new InvalidDataException("Destination dependency must be exported: " + entry.Name);
                string wantType = (string)spec["type"];
                if (!string.IsNullOrEmpty(wantType) && wantType != target.GetType().Name)
                    throw new InvalidDataException("Dependency " + entry.Name + " is a " + target.GetType().Name + ", package expects " + wantType);
                prepared.Dependencies.Add(entry);
                return new PointerRef(new EbxImportReference { FileGuid = entry.Guid, ClassGuid = targetGuid.ExportedGuid });
            };

            Func<JToken, Type, string, object> decode = null;
            decode = (value, type, path) => {
                if (type == typeof(PointerRef))
                {
                    var pointer = value as JObject ?? throw new InvalidDataException("Expected a pointer at " + path);
                    if (pointer.Count != 1) throw new InvalidDataException("Pointer must have exactly one tag at " + path);
                    if ((bool?)pointer["null"] == true) return new PointerRef();
                    if (pointer["ref"] != null)
                    {
                        int index = (int)pointer["ref"];
                        if (index < 0 || index >= objects.Length) throw new InvalidDataException("Unresolved internal object " + index + " at " + path);
                        return new PointerRef(objects[index]);
                    }
                    if (pointer["external"] is JObject external) return resolve(external);
                    throw new InvalidDataException("Unknown pointer tag at " + path);
                }
                if (type.Name == "ResourceRef")
                {
                    var res = (value as JObject)?["resource"] as JObject ?? throw new InvalidDataException("Expected a resource tag at " + path);
                    string rname = (string)res["name"];
                    ulong rid = 0;
                    if (!string.IsNullOrWhiteSpace(rname))
                    {
                        var re = App.AssetManager.GetResEntry(rname) ?? throw new InvalidDataException("Resource not found: " + rname + " at " + path);
                        rid = re.ResRid;
                    }
                    else
                    {
                        string hex = (string)res["rid"] ?? "0x0";
                        rid = ulong.Parse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.Substring(2) : hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        if (rid != 0) throw new InvalidDataException("A non-zero resource must be named (RIDs are per build) at " + path);
                    }
                    return TypeCoercion.Coerce(type, "0x" + rid.ToString("x16"));
                }
                if (type.Name == "TypeRef")
                {
                    string tn = (string)((value as JObject)?["typeref"]) ?? throw new InvalidDataException("Expected a typeref tag at " + path);
                    return Activator.CreateInstance(type, tn);
                }
                if (type.Name == "FileRef")
                {
                    string fr = (string)((value as JObject)?["fileref"]) ?? throw new InvalidDataException("Expected a fileref tag at " + path);
                    return Activator.CreateInstance(type, fr);
                }
                if (value is JObject record)
                {
                    if (record["ref"] != null)
                    {
                        int index = (int)record["ref"];
                        object obj = index >= 0 && index < objects.Length ? objects[index] : null;
                        if (obj == null || !type.IsInstanceOfType(obj)) throw new InvalidDataException("Internal object has an incompatible type at " + path);
                        return obj;
                    }
                    if (!(record["fields"] is JObject)) throw new InvalidDataException("Expected a typed record at " + path);
                    object nested = TypeLibrary.CreateObject((string)record["type"]) ?? throw new InvalidDataException("Destination SDK lacks type " + record["type"]);
                    if (!type.IsInstanceOfType(nested)) throw new InvalidDataException("Type " + record["type"] + " does not fit destination " + type.Name + " at " + path);
                    nested = Populate(nested, (JObject)record["fields"], path, decode, issues, defaults, options.Strict);
                    return nested;
                }
                if (value.Type == JTokenType.Null)
                {
                    if (type.IsValueType) throw new InvalidDataException("Cannot assign null to " + type.Name + " at " + path);
                    return null;
                }
                if (type.IsEnum)
                {
                    string enumName = (string)value;
                    if (!Enum.GetNames(type).Contains(enumName)) throw new InvalidDataException("Unknown " + type.Name + " value " + enumName + " at " + path);
                    return Enum.Parse(type, enumName);
                }
                if (type.Name == "CString") return Activator.CreateInstance(type, (string)value);
                if (type == typeof(Guid)) return Guid.Parse((string)value);
                if (!type.IsPrimitive && type != typeof(string) && type != typeof(decimal)) throw new InvalidDataException("Unsupported scalar type " + type.Name + " at " + path);
                if ((type == typeof(bool) && value.Type != JTokenType.Boolean) || (type == typeof(string) && value.Type != JTokenType.String))
                    throw new InvalidDataException("Scalar kind differs from destination " + type.Name + " at " + path);
                if (type != typeof(bool) && type != typeof(string) && type != typeof(char))
                {
                    bool floating = type == typeof(float) || type == typeof(double) || type == typeof(decimal);
                    if (value.Type != JTokenType.Integer && !(floating && value.Type == JTokenType.Float))
                        throw new InvalidDataException("Numeric kind differs from destination " + type.Name + " at " + path);
                    double number = (double)value;
                    // float.MaxValue is printed as 3.40282347E+38, which parses to a double a hair above float.MaxValue;
                    // what matters is that the value is finite once narrowed to float (LinearTransform's editor fields carry it).
                    if (double.IsNaN(number) || double.IsInfinity(number) || (type == typeof(float) && float.IsInfinity((float)number)))
                        throw new InvalidDataException("Non-finite/out-of-range number at " + path);
                }
                return value.ToObject(type);
            };

            for (int i = 0; i < objects.Length; i++)
                if (objects[i] != null) Populate(objects[i], (JObject)package.Objects[i]["fields"], "objects[" + i + "]", decode, issues, defaults, options.Strict, i == 0);
            if (issues.Count != 0) return prepared;
            try
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    if (!EbxGraphPackage.IsDataContainer(objects[i].GetType())) { issues.Add("objects[" + i + "] (" + objects[i].GetType().Name + ") is not a data container."); continue; }
                    var record = (JObject)package.Objects[i];
                    bool exported = i == 0 || (bool?)record["exported"] == true;
                    Guid g = DeterministicGuid(name, "object:" + i);
                    ((dynamic)objects[i]).SetInstanceGuid(exported ? new AssetClassGuid(g, i) : new AssetClassGuid(i));
                    var flagsProp = objects[i].GetType().GetProperty("Flags");
                    if (!options.KeepFlags && exported && flagsProp != null && flagsProp.PropertyType == typeof(uint) && flagsProp.CanWrite
                        && ((uint)flagsProp.GetValue(objects[i]) & 0x00FFFFFFu) != 0)
                    {
                        // Only objects that carry identity bits get the new guid's: shipped blueprints/layers keep Flags 0
                        // and shapes keep a bare flag byte (0x02000000), and those stay verbatim.
                        uint src = (uint)flagsProp.GetValue(objects[i]);
                        uint dst = IdentityFlags(src, g);
                        if (dst != src) { flagsProp.SetValue(objects[i], dst); notes.Add("objects[" + i + "].Flags 0x" + src.ToString("x8") + " -> 0x" + dst.ToString("x8") + " (identity bits of the new guid)"); }
                    }
                }
                if (issues.Count != 0) return prepared;
                var nameProp = objects[0].GetType().GetProperty("Name");
                if (nameProp != null && nameProp.CanWrite)
                    nameProp.SetValue(objects[0], nameProp.PropertyType == typeof(string) ? (object)name : Activator.CreateInstance(nameProp.PropertyType, name));
                var asset = new EbxAsset(objects);
                asset.SetFileGuid(DeterministicGuid(name, "file"));
                // No in-process binary round trip: both the game-format and the project-format reader flaked in long
                // import runs (EbxReader.ReadField "Index was out of range", shifted fields) once other assets had been
                // written in the same process. The proof that counts is the read-back of the SAVED project in a fresh
                // process (ebx graph-export on the saved project, diffed against the package -- fx_verify.py).
                var actual = EbxGraphPackage.CaptureAsset(asset, name);
                var roundTrip = asset;
                var againstPackage = DiffAgainstPackage(package, actual, 20);
                if (againstPackage.Count != 0) { issues.Add("Built asset differs from the package: " + string.Join("; ", againstPackage)); return prepared; }
                prepared.Asset = roundTrip;
                prepared.Expected = actual;
                prepared.Report["ready"] = true;
                prepared.Report["file_guid"] = roundTrip.FileGuid.ToString();
                prepared.Report["dependencies"] = new JArray(prepared.Dependencies.Select(e => e.Name).OrderBy(n => n));
            }
            catch (Exception ex) { issues.Add("Target serialization/validation: " + ex); }
            return prepared;
        }

        private static object Populate(object obj, JObject fields, string path, Func<JToken, Type, string, object> decode,
                                       JArray issues, JArray defaults, bool strict, bool isRoot = false)
        {
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in fields.Properties())
            {
                string fullPath = path + "." + field.Name;
                try
                {
                    if (isRoot && field.Name == "Name") { assigned.Add("Name"); continue; }
                    var property = obj.GetType().GetProperty(field.Name);
                    if (property == null || field.Name.StartsWith("__", StringComparison.Ordinal)) throw new InvalidDataException("Destination SDK lacks field " + obj.GetType().Name + "." + field.Name);
                    assigned.Add(field.Name);
                    if (field.Value is JArray array)
                    {
                        var list = property.GetValue(obj) as IList;
                        if (list == null || list.IsReadOnly || list.IsFixedSize) throw new InvalidDataException("Destination list is not writable");
                        Type element = property.PropertyType.GetGenericArguments().Single();
                        list.Clear();
                        for (int i = 0; i < array.Count; i++) list.Add(decode(array[i], element, fullPath + "[" + i + "]"));
                    }
                    else
                    {
                        if (!property.CanWrite) throw new InvalidDataException("Destination field is read-only");
                        property.SetValue(obj, decode(field.Value, property.PropertyType, fullPath));
                    }
                }
                catch (Exception ex) { issues.Add(fullPath + ": " + ex.Message); }
            }
            foreach (var field in EbxGraphPackage.Fields(obj.GetType()).Where(f => !assigned.Contains(f.Name)))
            {
                if (isRoot && field.Name == "Name") continue;
                string entry = path + "." + field.Name + " (" + field.PropertyType.Name + ")";
                if (strict) issues.Add(entry + ": not assigned by the package (strict)");
                else defaults.Add(entry);
            }
            return obj;
        }

        /// <summary>Field-level differences between two object tables (instance identity and Flags excluded).</summary>
        public static List<string> Diff(JArray a, JArray b, int max)
        {
            var list = new List<string>();
            if (a.Count != b.Count) { list.Add("object count " + a.Count + " vs " + b.Count); return list; }
            for (int i = 0; i < a.Count && list.Count < max; i++)
            {
                if ((string)a[i]["type"] != (string)b[i]["type"]) { list.Add("objects[" + i + "] type " + a[i]["type"] + " vs " + b[i]["type"]); continue; }
                var fa = (JObject)a[i]["fields"]; var fb = (JObject)b[i]["fields"];
                foreach (var p in fa.Properties())
                    if (!JToken.DeepEquals(p.Value, fb[p.Name]))
                    {
                        string va = p.Value?.ToString(Newtonsoft.Json.Formatting.None) ?? "null", vb = fb[p.Name]?.ToString(Newtonsoft.Json.Formatting.None) ?? "missing";
                        list.Add("objects[" + i + "](" + a[i]["type"] + ")." + p.Name + " " + (va.Length > 80 ? va.Substring(0, 80) : va) + " vs " + (vb.Length > 80 ? vb.Substring(0, 80) : vb));
                        if (list.Count >= max) break;
                    }
                foreach (var p in fb.Properties())
                    if (fa[p.Name] == null) { list.Add("objects[" + i + "]." + p.Name + " (only in read-back)"); if (list.Count >= max) break; }
            }
            return list;
        }

        /// <summary>
        /// Compare a capture of the built asset with the package that described it. Pointers to external assets are
        /// compared by the asset path the package named (the destination guids are the resolved ones), the root Name is
        /// the destination name, and Flags of exported objects carry the new identity bits.
        /// </summary>
        public static List<string> DiffAgainstPackage(EbxGraphPackage package, EbxGraphPackage built, int max)
        {
            var list = new List<string>();
            if (package.Objects.Count != built.Objects.Count) { list.Add("object count " + package.Objects.Count + " vs " + built.Objects.Count); return list; }
            for (int i = 0; i < package.Objects.Count && list.Count < max; i++)
            {
                var pa = (JObject)package.Objects[i]; var pb = (JObject)built.Objects[i];
                if ((string)pa["type"] != (string)pb["type"]) { list.Add("objects[" + i + "] type " + pa["type"] + " vs " + pb["type"]); continue; }
                var fa = (JObject)pa["fields"]; var fb = (JObject)pb["fields"];
                foreach (var p in fa.Properties())
                {
                    if (i == 0 && p.Name == "Name") continue;
                    if (p.Name == "Flags" && (i == 0 || (bool?)pa["exported"] == true)) continue;
                    if (!EquivalentValue(p.Value, fb[p.Name])) { list.Add("objects[" + i + "]." + p.Name); if (list.Count >= max) break; }
                }
            }
            return list;
        }

        private static bool EquivalentValue(JToken a, JToken b)
        {
            if (a == null || b == null) return a == b;
            if (a is JObject oa && oa["external"] is JObject ea)
            {
                var eb = (b as JObject)?["external"] as JObject;
                if (eb == null) return false;
                string pathA = (string)ea["asset"], pathB = (string)eb["asset"];
                if (!string.IsNullOrEmpty(pathA)) return string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase);
                return string.Equals((string)ea["file_guid"], (string)eb["file_guid"], StringComparison.OrdinalIgnoreCase);
            }
            if (a is JObject ra && ra["resource"] is JObject resA)
            {
                var resB = (b as JObject)?["resource"] as JObject;
                if (resB == null) return false;
                string na = (string)resA["name"];
                if (!string.IsNullOrEmpty(na)) return string.Equals(na, (string)resB["name"], StringComparison.OrdinalIgnoreCase);
                return (string)resB["rid"] == "0x0000000000000000";
            }
            if (a.Type == JTokenType.Float || b.Type == JTokenType.Float)
            {
                if ((a.Type != JTokenType.Float && a.Type != JTokenType.Integer) || (b.Type != JTokenType.Float && b.Type != JTokenType.Integer)) return false;
                return (float)(double)a == (float)(double)b;
            }
            if (a is JObject jo && b is JObject jb)
            {
                if (jo.Count != jb.Count) return false;
                foreach (var p in jo.Properties()) if (!EquivalentValue(p.Value, jb[p.Name])) return false;
                return true;
            }
            if (a is JArray ja && b is JArray jb2)
            {
                if (ja.Count != jb2.Count) return false;
                for (int i = 0; i < ja.Count; i++) if (!EquivalentValue(ja[i], jb2[i])) return false;
                return true;
            }
            return JToken.DeepEquals(a, b);
        }

        public static EbxAssetEntry Import(Prepared prepared, string name, int bundleId, bool dryRun)
        {
            if (!prepared.Ready) throw new InvalidDataException("Graph is not ready; resolve the reported issues first.");
            if (!Session.Project.IsLoaded) throw new InvalidOperationException("Load a project first.");
            if (App.AssetManager.GetEbxEntry(name) != null) throw new InvalidDataException("Destination asset already exists: " + name);
            if (bundleId < 0 || App.AssetManager.GetBundleEntry(bundleId) == null) throw new InvalidDataException("Destination bundle is required.");
            if (dryRun) return null;
            EbxAssetEntry entry = null;
            try
            {
                App.AssetManager.AddEbx(name, prepared.Asset);
                entry = App.AssetManager.GetEbxEntry(name) ?? throw new IOException("AddEbx failed for " + name);
                entry.AddToBundle(bundleId);
                foreach (var dependency in prepared.Dependencies) entry.LinkAsset(dependency);
                var readBack = EbxGraphPackage.Capture(entry);
                var diff = Diff(prepared.Expected.Objects, readBack.Objects, 20);
                if (diff.Count != 0) throw new IOException("Read-back verification failed: " + string.Join("; ", diff));
                return entry;
            }
            catch { entry = entry ?? App.AssetManager.GetEbxEntry(name); if (entry != null) App.AssetManager.RevertAsset(entry); throw; }
        }

        /// <summary>Field schema of SDK types in the current game: name, CLR type, list element, and the SDK default value.</summary>
        public static JObject Schema(IEnumerable<string> typeNames)
        {
            var result = new JObject { ["game"] = Session.GameName };
            var types = new JObject();
            foreach (string tn in typeNames.Distinct())
            {
                Type t = null;
                try { t = TypeLibrary.GetType(tn); } catch { }
                if (t == null) { types[tn] = null; continue; }
                object instance = null;
                try { instance = TypeLibrary.CreateObject(tn); } catch { }
                var fields = new JObject();
                foreach (var p in EbxGraphPackage.Fields(t))
                {
                    var f = new JObject { ["type"] = FriendlyTypeName(p.PropertyType) };
                    if (instance != null)
                    {
                        try
                        {
                            object v = p.GetValue(instance);
                            Func<object, JToken> enc = null;
                            enc = x => EbxGraphPackage.EncodeValue(x, o => -1, enc);
                            f["default"] = enc(v);
                        }
                        catch (Exception ex) { f["default_error"] = ex.Message; }
                    }
                    fields[p.Name] = f;
                }
                var chain = new JArray();
                for (Type b = t.BaseType; b != null && b != typeof(object); b = b.BaseType) chain.Add(b.Name);
                types[tn] = new JObject { ["base"] = chain, ["container"] = EbxGraphPackage.IsDataContainer(t), ["fields"] = fields };
            }
            result["types"] = types;
            return result;
        }

        private static string FriendlyTypeName(Type t)
        {
            if (t.IsGenericType) return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(FriendlyTypeName)) + ">";
            return t.Name;
        }
    }
}
