using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Object-table package of ONE EBX partition of any root type (the generalisation of SoundGraphPackage).
    ///
    /// Every object the root reaches is listed once, in first-reach order (root = objects[0]); objects of the partition
    /// that nothing reaches follow, flagged "orphan". Internal pointers are {"ref": i} so sharing and cycles survive,
    /// external pointers carry file guid, instance guid, asset path and target type, ResourceRefs carry their RID and
    /// resource name. Fields are written in ordinal name order with every value -- nothing is filtered.
    ///
    /// The package is the unit of a cross-game EBX port: capture it in the source game, convert it (outside the CLI)
    /// into the destination schema, then build it with <see cref="EbxGraphBuilder"/> in the destination game. The same
    /// capture of the imported asset is the read-back used to prove the import is field-exact.
    /// </summary>
    public sealed class EbxGraphPackage
    {
        public int Version = 1;
        public string Kind = "ebx-graph";
        public string Game, Asset, FileGuid, RootType, Sha256;
        public JArray Objects = new JArray();
        public JObject Source;   // free-form provenance written by a converter; covered by the checksum

        internal sealed class IdentityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        /// <summary>Public, readable, non-indexed instance properties that are real EBX fields, in ordinal name order.</summary>
        public static IEnumerable<PropertyInfo> Fields(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.Name.StartsWith("__", StringComparison.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        public static bool IsDataContainer(Type type) => type != null && type.GetMethod("GetInstanceGuid", Type.EmptyTypes) != null;

        public static AssetClassGuid InstanceGuidOf(object value) =>
            (AssetClassGuid)value.GetType().GetMethod("GetInstanceGuid", Type.EmptyTypes).Invoke(value, null);

        public static EbxGraphPackage Capture(EbxAssetEntry entry)
        {
            if (entry == null) throw new InvalidDataException("Graph export needs an existing EBX asset.");
            return CaptureAsset(App.AssetManager.GetEbx(entry), entry.Name);
        }

        public static EbxGraphPackage CaptureAsset(EbxAsset asset, string name)
        {
            if (asset?.RootObject == null) throw new InvalidDataException("Asset has no root object: " + name);
            var result = new EbxGraphPackage { Game = Session.GameName, Asset = name, FileGuid = asset.FileGuid.ToString(), RootType = asset.RootObject.GetType().Name };
            var ids = new Dictionary<object, int>(new IdentityComparer());
            var queue = new List<object>();
            Func<object, int> identify = obj => {
                if (!ids.TryGetValue(obj, out int index)) { index = ids.Count; ids.Add(obj, index); queue.Add(obj); }
                return index;
            };
            identify(asset.RootObject);
            Func<object, JToken> encode = null;
            encode = value => EncodeValue(value, identify, encode);
            var orphanFrom = -1;
            for (int i = 0; ; i++)
            {
                if (i == queue.Count)
                {
                    // Everything the root reaches is listed; now the partition objects nothing reaches (kept, flagged).
                    if (orphanFrom >= 0) break;
                    orphanFrom = queue.Count;
                    foreach (object o in asset.Objects) if (!ids.ContainsKey(o)) identify(o);
                    if (i == queue.Count) break;
                }
                if (queue.Count > 20000) throw new InvalidDataException("Graph exceeds 20000 objects.");
                object obj = queue[i];
                var record = new JObject { ["type"] = obj.GetType().Name };
                if (IsDataContainer(obj.GetType()))
                {
                    AssetClassGuid g = InstanceGuidOf(obj);
                    record["exported"] = g.IsExported;
                    if (g.IsExported) record["guid"] = g.ExportedGuid.ToString();
                }
                if (orphanFrom >= 0 && i >= orphanFrom) record["orphan"] = true;
                record["fields"] = EncodeFields(obj, encode);
                result.Objects.Add(record);
            }
            result.Seal();
            result.Validate();
            return result;
        }

        internal static JObject EncodeFields(object value, Func<object, JToken> encode)
        {
            var fields = new JObject();
            foreach (var property in Fields(value.GetType())) fields[property.Name] = encode(property.GetValue(value));
            return fields;
        }

        internal static JToken EncodeValue(object value, Func<object, int> identify, Func<object, JToken> encode)
        {
            if (value == null) return JValue.CreateNull();
            Type type = value.GetType();
            if (value is PointerRef pointer)
            {
                if (pointer.Type == PointerRefType.Null) return new JObject { ["null"] = true };
                if (pointer.Type == PointerRefType.Internal) return new JObject { ["ref"] = identify(pointer.Internal) };
                var external = pointer.External;
                var target = App.AssetManager.GetEbxEntry(external.FileGuid);
                string targetType = null;
                if (target != null)
                {
                    try
                    {
                        var targetObject = App.AssetManager.GetEbx(target).Objects.FirstOrDefault(o => IsDataContainer(o.GetType()) && InstanceGuidOf(o).ExportedGuid == external.ClassGuid);
                        targetType = targetObject?.GetType().Name;
                    }
                    catch { }
                }
                return new JObject { ["external"] = new JObject {
                    ["file_guid"] = external.FileGuid.ToString(), ["class_guid"] = external.ClassGuid.ToString(),
                    ["asset"] = target?.Name, ["type"] = targetType
                } };
            }
            string tname = type.Name;
            if (tname == "ResourceRef")
            {
                ulong rid = ResourceRid(value);
                string resName = null;
                if (rid != 0) { try { resName = App.AssetManager.GetResEntry(rid)?.Name; } catch { } }
                return new JObject { ["resource"] = new JObject { ["rid"] = "0x" + rid.ToString("x16"), ["name"] = resName } };
            }
            if (tname == "TypeRef") return new JObject { ["typeref"] = (type.GetProperty("Name") ?? type.GetProperty("TypeName"))?.GetValue(value)?.ToString() ?? value.ToString() };
            if (tname == "FileRef") return new JObject { ["fileref"] = value.ToString() };
            if (tname == "BoxedValueRef") throw new InvalidDataException("BoxedValueRef fields are not supported by graph packages yet.");
            if (value is IList list) return new JArray(list.Cast<object>().Select(encode));
            if (type.IsEnum || type == typeof(Guid) || tname == "CString") return new JValue(value.ToString());
            if (type == typeof(float)) return new JValue((float)value);
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)) return JToken.FromObject(value);
            if (IsDataContainer(type)) return new JObject { ["ref"] = identify(value) };
            return new JObject { ["type"] = tname, ["fields"] = EncodeFields(value, encode) };
        }

        public static ulong ResourceRid(object value)
        {
            if (value == null) return 0;
            Type t = value.GetType();
            object raw = null;
            PropertyInfo ridProp = t.GetProperty("ResRid") ?? t.GetProperty("ResourceId") ?? t.GetProperty("resourceId");
            if (ridProp != null) raw = ridProp.GetValue(value);
            if (raw == null)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType == typeof(ulong)) { raw = f.GetValue(value); break; }
            if (raw == null)
                foreach (var pi in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (pi.PropertyType == typeof(ulong)) { raw = pi.GetValue(value); break; }
            return raw == null ? 0 : Convert.ToUInt64(raw);
        }

        // ---------------------------------------------------------------- checksum / validation

        public static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public string Digest() => Hash(Encoding.UTF8.GetBytes(new JObject {
            ["version"] = Version, ["kind"] = Kind, ["game"] = Game, ["asset"] = Asset, ["file_guid"] = FileGuid,
            ["root_type"] = RootType, ["source"] = Source?.DeepClone(), ["objects"] = Objects.DeepClone()
        }.ToString(Formatting.None)));

        public void Seal() { Sha256 = Digest(); }

        public static EbxGraphPackage Load(string file, bool allowUnsealed = false)
        {
            var package = JsonConvert.DeserializeObject<EbxGraphPackage>(File.ReadAllText(file));
            if (package == null) throw new InvalidDataException("Empty graph package: " + file);
            if (allowUnsealed && string.IsNullOrEmpty(package.Sha256)) package.Seal();
            package.Validate();
            return package;
        }

        public void Validate()
        {
            if (Version != 1 || Kind != "ebx-graph") throw new InvalidDataException("Unsupported graph package version/kind.");
            if (Game != "swbf1" && Game != "swbf2") throw new InvalidDataException("Unsupported graph package profile: " + Game);
            if (Objects == null || Objects.Count == 0 || Objects.Count > 20000) throw new InvalidDataException("Graph package object count out of range.");
            if (!string.Equals(Sha256, Digest(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Graph package checksum mismatch.");
            if (RootType != null && (string)Objects[0]["type"] != RootType) throw new InvalidDataException("objects[0] is not the declared root type " + RootType + ".");
            for (int i = 0; i < Objects.Count; i++)
            {
                var record = Objects[i] as JObject;
                if (record == null || record["type"]?.Type != JTokenType.String || !(record["fields"] is JObject))
                    throw new InvalidDataException("Invalid graph object record at objects[" + i + "].");
            }
            foreach (var token in Objects.Descendants().OfType<JObject>())
            {
                if (token["ref"] != null && (token.Count != 1 || token["ref"].Type != JTokenType.Integer || (int)token["ref"] < 0 || (int)token["ref"] >= Objects.Count))
                    throw new InvalidDataException("Graph has a dangling internal reference.");
                if (token["external"] is JObject external)
                {
                    var fileToken = external["file_guid"];
                    if (fileToken != null && fileToken.Type != JTokenType.Null && !Guid.TryParse((string)fileToken, out _))
                        throw new InvalidDataException("Graph has an external reference with an invalid file guid.");
                    bool hasFile = Guid.TryParse((string)external["file_guid"], out Guid f) && f != Guid.Empty;
                    bool hasAsset = !string.IsNullOrWhiteSpace((string)external["asset"]);
                    if (!hasFile && !hasAsset) throw new InvalidDataException("Graph has an external reference with neither file guid nor asset path.");
                    if (external["class_guid"] != null && external["class_guid"].Type != JTokenType.Null && !Guid.TryParse((string)external["class_guid"], out _))
                        throw new InvalidDataException("Graph has an external reference with an invalid class guid.");
                }
            }
        }

        public static string ReferenceKey(JToken external) => (string)external["file_guid"] + "/" + (string)external["class_guid"];
    }
}
