using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    // Object-table format: internal pointers retain sharing/cycles; list order is execution order.
    // This is deliberately separate from EbxDumper's human-readable, expanded pointer format.
    public sealed class SoundGraphPackage
    {
        public int Version = 1;
        public string Game, Asset, FileGuid, Sha256;
        public JArray Objects = new JArray();

        internal sealed class IdentityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        internal static IEnumerable<PropertyInfo> Fields(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.Name.StartsWith("__", StringComparison.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        public static SoundGraphPackage Capture(EbxAssetEntry entry)
        {
            if (entry == null || entry.Type != "SoundPatchAsset") throw new InvalidDataException("Graph packages require a SoundPatchAsset.");
            return CaptureAsset(App.AssetManager.GetEbx(entry), entry.Name);
        }

        internal static SoundGraphPackage CaptureAsset(EbxAsset asset, string name)
        {
            var result = new SoundGraphPackage { Game = Session.GameName, Asset = name, FileGuid = asset.FileGuid.ToString() };
            var ids = new Dictionary<object, int>(new IdentityComparer());
            var queue = new List<object>();
            Func<object, int> identify = obj => {
                if (!ids.TryGetValue(obj, out int index)) { index = ids.Count; ids.Add(obj, index); queue.Add(obj); }
                return index;
            };
            identify(asset.RootObject);
            Func<object, JToken> encode = null;
            encode = value => {
                if (value == null) return JValue.CreateNull();
                Type type = value.GetType();
                if (value is PointerRef pointer)
                {
                    if (pointer.Type == PointerRefType.Null) return new JObject { ["null"] = true };
                    if (pointer.Type == PointerRefType.Internal) return new JObject { ["ref"] = identify(pointer.Internal) };
                    var external = pointer.External;
                    var target = App.AssetManager.GetEbxEntry(external.FileGuid);
                    string targetType = null; int? channels = null;
                    if (target != null)
                    {
                        var targetObject = App.AssetManager.GetEbx(target).Objects.FirstOrDefault(o => InstanceGuid(o) == external.ClassGuid);
                        targetType = targetObject?.GetType().Name;
                        if (targetType == "SoundWaveAsset") channels = (int)((dynamic)targetObject).ChannelCount;
                    }
                    return new JObject { ["external"] = new JObject {
                        ["file_guid"] = external.FileGuid.ToString(), ["class_guid"] = external.ClassGuid.ToString(),
                        ["asset"] = target?.Name, ["type"] = targetType, ["channels"] = channels
                    } };
                }
                if (value is IList list) return new JArray(list.Cast<object>().Select(encode));
                if (type.IsEnum || type == typeof(Guid) || type.Name == "CString") return new JValue(value.ToString());
                if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)) return JToken.FromObject(value);
                if (type.GetMethod("GetInstanceGuid") != null) return new JObject { ["ref"] = identify(value) };
                return new JObject { ["type"] = type.Name, ["fields"] = EncodeFields(value, encode) };
            };
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue.Count > 10000) throw new InvalidDataException("Graph exceeds 10000 objects.");
                object obj = queue[i];
                result.Objects.Add(new JObject { ["type"] = obj.GetType().Name, ["fields"] = EncodeFields(obj, encode) });
            }
            result.Seal();
            result.Validate();
            return result;
        }

        private static JObject EncodeFields(object value, Func<object, JToken> encode)
        {
            var fields = new JObject();
            foreach (var property in Fields(value.GetType())) fields[property.Name] = encode(property.GetValue(value));
            return fields;
        }

        internal static Guid InstanceGuid(object value) => ((AssetClassGuid)value.GetType().GetMethod("GetInstanceGuid").Invoke(value, null)).ExportedGuid;
        private string Digest() => AudioPackage.Hash(Encoding.UTF8.GetBytes(new JObject {
            ["version"] = Version, ["game"] = Game, ["asset"] = Asset, ["file_guid"] = FileGuid, ["objects"] = Objects.DeepClone()
        }.ToString(Formatting.None)));
        internal void Seal() { Sha256 = Digest(); }

        public static SoundGraphPackage Load(string file)
        {
            var package = JsonConvert.DeserializeObject<SoundGraphPackage>(File.ReadAllText(file));
            if (package == null) throw new InvalidDataException("Empty graph package.");
            package.Validate(); return package;
        }

        public void Validate()
        {
            if (Version != 1 || (Game != "swbf1" && Game != "swbf2") || Objects == null || Objects.Count == 0 || Objects.Count > 10000)
                throw new InvalidDataException("Unsupported graph package/profile or object count.");
            if (!string.Equals(Sha256, Digest(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Graph package checksum mismatch.");
            if ((string)Objects[0]["type"] != "SoundPatchAsset") throw new InvalidDataException("Root must be SoundPatchAsset.");
            foreach (var record in Objects)
                if (record["type"]?.Type != JTokenType.String || !(record["fields"] is JObject)) throw new InvalidDataException("Invalid graph object record.");
            foreach (var token in Objects.Descendants().OfType<JObject>())
            {
                if (token["ref"] != null && (token.Count != 1 || token["ref"].Type != JTokenType.Integer || (int)token["ref"] < 0 || (int)token["ref"] >= Objects.Count))
                    throw new InvalidDataException("Graph has a dangling internal reference.");
                if (token["external"] is JObject external &&
                    (!Guid.TryParse((string)external["file_guid"], out Guid f) || f == Guid.Empty || !Guid.TryParse((string)external["class_guid"], out Guid c) || c == Guid.Empty))
                    throw new InvalidDataException("Graph has an invalid external reference.");
            }
            ValidateTopology();
        }

        internal JObject Referenced(JToken pointer)
        {
            if (!(pointer is JObject obj) || obj["ref"] == null) throw new InvalidDataException("Expected an internal graph reference.");
            return (JObject)Objects[(int)obj["ref"]];
        }

        private void ValidateTopology()
        {
            var root = (JObject)Objects[0]["fields"];
            var graph = Referenced(root["Graph"]);
            if ((string)graph["type"] != "SoundGraphData") throw new InvalidDataException("Patch must own a SoundGraphData.");
            var fields = (JObject)graph["fields"];
            int values = AudioPackage.RequiredInt(fields, "ValueCount"), publics = AudioPackage.RequiredInt(fields, "PublicValueCount");
            if (values < 1 || publics < 0 || publics > values) throw new InvalidDataException("Invalid graph value/public-value counts.");
            var nodes = fields["Nodes"] as JArray ?? throw new InvalidDataException("Missing graph node order.");
            var nodeIds = nodes.Select(n => (int)n["ref"]).ToList();
            if (nodeIds.Distinct().Count() != nodeIds.Count) throw new InvalidDataException("Graph node order contains duplicates.");
            foreach (var output in root["OutputNodes"] ?? new JArray())
                if (!nodeIds.Contains((int)output["ref"])) throw new InvalidDataException("Output node is absent from execution order.");
            foreach (JObject record in Objects.Concat(Objects.Descendants().OfType<JObject>()).OfType<JObject>().Where(o => o["fields"] != null))
            {
                var f = record["fields"];
                if ((string)record["type"] == "AudioGraphNodePort" && (bool)f["IsConnected"])
                {
                    int index = (int)f["ValueIndex"];
                    if (index < 0 || index >= values) throw new InvalidDataException("Connected port exceeds ValueCount.");
                }
                if (((string)record["type"] == "AudioGraphEvent" || (string)record["type"] == "AudioGraphParameter") &&
                    ((int)f["ValueIndex"] < 0 || (int)f["ValueIndex"] >= values)) throw new InvalidDataException("Public input/event exceeds ValueCount.");
            }
            var info = (JObject)fields["Info"]?["fields"] ?? throw new InvalidDataException("Missing compiled graph info.");
            var voices = (JArray)info["Voices"];
            var connections = (JArray)info["Connections"];
            var constructs = (JArray)info["ConstructParams"];
            int pluginCount = 0;
            foreach (var voice in voices)
                foreach (var plugin in (JArray)voice["fields"]["Plugins"])
                {
                    pluginCount++;
                    var f = plugin["fields"];
                    int connection = (int)f["ConnectionIndex"], first = (int)f["ConstructParamsIndex"], count = (int)f["ConstructParamCount"];
                    // ConnectionIndex is one-based, with zero meaning no connection.
                    if (connection < 0 || connection > connections.Count || first < 0 || count < 0 || (long)first + count > constructs.Count)
                        throw new InvalidDataException("Invalid compiled plugin connection/constructor range.");
                }
            if (pluginCount != (int)info["PluginCount"]) throw new InvalidDataException("PluginCount does not match the voice table.");
            foreach (var record in Objects.Descendants().OfType<JObject>().Where(o => (string)o["type"] == "SoundGraphPluginRef"))
            {
                var f = record["fields"];
                if (!(bool)f["IsValid"]) continue;
                int voice = (int)f["VoiceIndex"], plugin = (int)f["PluginIndex"];
                if (voice < 0 || voice >= voices.Count || plugin < 0 || plugin >= ((JArray)voices[voice]["fields"]["Plugins"]).Count)
                    throw new InvalidDataException("Node references a missing compiled plugin.");
            }
        }

        public JObject Inspect()
        {
            Validate();
            var graph = Referenced(Objects[0]["fields"]["Graph"])["fields"];
            return new JObject {
                ["asset"] = Asset, ["game"] = Game, ["objects"] = Objects.Count,
                ["value_count"] = graph["ValueCount"], ["public_value_count"] = graph["PublicValueCount"],
                ["nodes"] = new JArray(((JArray)graph["Nodes"]).Select((n, i) => new JObject {
                    ["order"] = i, ["object"] = n["ref"], ["type"] = Referenced(n)["type"], ["fields"] = Referenced(n)["fields"].DeepClone()
                })),
                ["references"] = new JArray(Objects.Descendants().OfType<JObject>().Where(o => o["external"] != null)
                    .Select(o => o["external"]).GroupBy(ReferenceKey).Select(g => g.First().DeepClone())),
                ["runtime_validation_required"] = true
            };
        }

        internal static string ReferenceKey(JToken external) => (string)external["file_guid"] + "/" + (string)external["class_guid"];
    }
}
