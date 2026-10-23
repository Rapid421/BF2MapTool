using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    public static class SoundGraphPort
    {
        public sealed class Prepared
        {
            public EbxAsset Asset;
            public JObject Report;
            public readonly HashSet<EbxAssetEntry> Dependencies = new HashSet<EbxAssetEntry>();
            public bool Ready => Asset != null;
        }

        // A migration is explicit: reference identities, renamed fields, replacement values and
        // discarded legacy fields are all recorded in the plan. No inferred cross-game GUID reuse.
        public static Prepared Prepare(SoundGraphPackage package, JObject migration, string name)
        {
            package.Validate();
            if (Session.GameName != "swbf1" && Session.GameName != "swbf2") throw new InvalidDataException("Unsupported destination profile.");
            migration = migration ?? new JObject();
            foreach (var p in migration.Properties())
                if (p.Name != "references" && p.Name != "fields" && p.Name != "types") throw new InvalidDataException("Unknown migration section: " + p.Name);
            foreach (var p in migration.Properties()) if (!(p.Value is JObject)) throw new InvalidDataException("Migration sections must be objects.");
            var rules = (JObject)migration["fields"] ?? new JObject();
            var references = (JObject)migration["references"] ?? new JObject();
            var typeMap = (JObject)migration["types"] ?? new JObject();
            var issues = new JArray(); var changes = new JArray(); var defaults = new JArray();
            var usedRules = new HashSet<string>(); var usedRefs = new HashSet<string>(); var usedTypes = new HashSet<string>();
            var prepared = new Prepared();
            prepared.Report = new JObject { ["source_game"] = package.Game, ["destination_game"] = Session.GameName,
                ["asset"] = name, ["ready"] = false, ["issues"] = issues, ["changes"] = changes, ["destination_defaults"] = defaults,
                ["source_references"] = package.Inspect()["references"],
                ["runtime_validation_required"] = true };
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("A destination name is required.");

            Func<string, object> create = sourceType => {
                string type = (string)typeMap[sourceType] ?? sourceType;
                if (typeMap[sourceType] != null) { usedTypes.Add(sourceType); changes.Add("type " + sourceType + " -> " + type); }
                return TypeLibrary.CreateObject(type) ?? throw new InvalidDataException("Destination SDK lacks type " + type);
            };
            var objects = new object[package.Objects.Count];
            for (int i = 0; i < objects.Length; i++)
                try { objects[i] = create((string)package.Objects[i]["type"]); }
                catch (Exception ex) { issues.Add("objects[" + i + "]: " + ex.Message); }

            Func<JObject, PointerRef> resolve = spec => {
                string path = (string)spec["asset"];
                var entry = path != null ? App.AssetManager.GetEbxEntry(path) : App.AssetManager.GetEbxEntry(Guid.Parse((string)spec["file_guid"]));
                if (entry == null) throw new InvalidDataException("Destination dependency not found: " + (path ?? (string)spec["file_guid"]));
                var asset = App.AssetManager.GetEbx(entry);
                object target = asset.RootObject;
                if (spec["class_guid"] != null)
                {
                    Guid guid = Guid.Parse((string)spec["class_guid"]);
                    target = asset.Objects.FirstOrDefault(o => SoundGraphPackage.InstanceGuid(o) == guid);
                    if (target == null) throw new InvalidDataException("Exported object not found in " + entry.Name + ": " + guid);
                }
                Guid targetGuid = SoundGraphPackage.InstanceGuid(target);
                if (targetGuid == Guid.Empty) throw new InvalidDataException("Destination dependency must be exported: " + entry.Name);
                if (spec["type"] != null && (string)spec["type"] != target.GetType().Name)
                    throw new InvalidDataException("Destination dependency type differs: expected " + spec["type"] + ", got " + target.GetType().Name);
                if (spec["channels"]?.Type == JTokenType.Integer && (target.GetType().Name != "SoundWaveAsset" || (int)((dynamic)target).ChannelCount != (int)spec["channels"]))
                    throw new InvalidDataException("Destination wave channel count differs from the graph's source recording.");
                prepared.Dependencies.Add(entry);
                return new PointerRef(new EbxImportReference { FileGuid = entry.Guid, ClassGuid = targetGuid });
            };

            Func<JToken, Type, string, object> decode = null;
            Action<object, JObject, string> populate = null;
            decode = (value, type, path) => {
                if (type == typeof(PointerRef))
                {
                    var pointer = value as JObject ?? throw new InvalidDataException("Expected a pointer at " + path);
                    if (pointer.Count != 1) throw new InvalidDataException("Pointer must have exactly one tag at " + path);
                    if ((bool?)pointer["null"] == true) return new PointerRef();
                    if (pointer["ref"] != null)
                    {
                        int index = (int)pointer["ref"];
                        if (index < 0 || index >= objects.Length || objects[index] == null) throw new InvalidDataException("Unresolved internal object " + index);
                        return new PointerRef(objects[index]);
                    }
                    if (pointer["target"] is JObject target) return resolve(target); // explicit destination reference in a field override
                    if (pointer["external"] is JObject external)
                    {
                        string key = SoundGraphPackage.ReferenceKey(external);
                        var mapped = references[key] as JObject;
                        if (mapped != null)
                        {
                            usedRefs.Add(key);
                            var spec = (JObject)mapped.DeepClone();
                            if (spec.Properties().Any(p => p.Name != "asset" && p.Name != "class_guid"))
                                throw new InvalidDataException("Reference mappings accept asset and class_guid only: " + key);
                            spec["type"] = external["type"]?.DeepClone();
                            spec["channels"] = external["channels"]?.DeepClone();
                            return resolve(spec);
                        }
                        if (package.Game != Session.GameName) throw new InvalidDataException("Map reference " + key + " (" + external["asset"] + ")");
                        return resolve(external);
                    }
                    throw new InvalidDataException("Unknown pointer tag at " + path);
                }
                if (value is JObject record)
                {
                    if (record["ref"] != null)
                    {
                        object obj = objects[(int)record["ref"]];
                        if (obj == null || !type.IsInstanceOfType(obj)) throw new InvalidDataException("Internal object has an incompatible type at " + path);
                        return obj;
                    }
                    if (!(record["fields"] is JObject)) throw new InvalidDataException("Expected a typed record at " + path);
                    object nested = create((string)record["type"]);
                    if (!type.IsInstanceOfType(nested)) throw new InvalidDataException("Type " + record["type"] + " does not fit destination " + type.Name + " at " + path);
                    populate(nested, record, path); return nested;
                }
                if (value.Type == JTokenType.Null)
                {
                    if (type.IsValueType) throw new InvalidDataException("Cannot assign null to " + type.Name);
                    return null;
                }
                if (type.IsEnum)
                {
                    string enumName = (string)value;
                    if (!Enum.GetNames(type).Contains(enumName)) throw new InvalidDataException("Unknown " + type.Name + " value " + enumName);
                    return Enum.Parse(type, enumName);
                }
                if (type.Name == "CString") return Activator.CreateInstance(type, (string)value);
                if (type == typeof(Guid)) return Guid.Parse((string)value);
                if (!type.IsPrimitive && type != typeof(string) && type != typeof(decimal)) throw new InvalidDataException("Unsupported scalar type " + type.Name);
                if ((type == typeof(bool) && value.Type != JTokenType.Boolean) || (type == typeof(string) && value.Type != JTokenType.String))
                    throw new InvalidDataException("Scalar kind differs from destination " + type.Name);
                if (type != typeof(bool) && type != typeof(string) && type != typeof(char))
                {
                    bool floating = type == typeof(float) || type == typeof(double) || type == typeof(decimal);
                    if (value.Type != JTokenType.Integer && !(floating && value.Type == JTokenType.Float))
                        throw new InvalidDataException("Numeric kind differs from destination " + type.Name + "; use an explicit replacement value");
                    double number = (double)value;
                    if (double.IsNaN(number) || double.IsInfinity(number) || (type == typeof(float) && (number > float.MaxValue || number < -float.MaxValue)))
                        throw new InvalidDataException("Non-finite/out-of-range number at " + path);
                }
                return value.ToObject(type);
            };
            populate = (obj, record, path) => {
                var assigned = new HashSet<string>();
                var sourceFields = (JObject)record["fields"].DeepClone();
                // Explicit values can also initialize fields newly introduced by the destination SDK.
                foreach (var rule in rules.Properties())
                    foreach (string prefix in new[] { path + ".", (string)record["type"] + "." })
                        if (rule.Name.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            string fieldName = rule.Name.Substring(prefix.Length);
                            if (fieldName.IndexOfAny(new[] { '.', '[' }) < 0 && sourceFields[fieldName] == null && rule.Value["value"] != null)
                                sourceFields[fieldName] = rule.Value["value"].DeepClone();
                        }
                foreach (var field in sourceFields.Properties())
                {
                    if (path == "objects[0]" && field.Name == "Name") continue;
                    string fullPath = path + "." + field.Name;
                    try
                    {
                        string ruleKey = rules[fullPath] != null ? fullPath : (string)record["type"] + "." + field.Name;
                        var rule = rules[ruleKey] as JObject;
                        JToken value = field.Value; string destinationField = field.Name;
                        if (rules[ruleKey] != null && rule == null) throw new InvalidDataException("Field rule must be an object: " + ruleKey);
                        if (rule != null)
                        {
                            usedRules.Add(ruleKey);
                            if (rule.Count == 0 || rule.Properties().Any(p => p.Name != "drop" && p.Name != "rename" && p.Name != "value" && p.Name != "constant_port")) throw new InvalidDataException("Unknown/empty field rule: " + ruleKey);
                            if ((bool?)rule["drop"] == true)
                            {
                                if (rule.Count != 1) throw new InvalidDataException("drop cannot be combined with other rules");
                                changes.Add(fullPath + ": dropped explicitly"); continue;
                            }
                            destinationField = (string)rule["rename"] ?? destinationField;
                            value = rule["value"] ?? value;
                            if ((bool?)rule["constant_port"] == true)
                            {
                                if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer) throw new InvalidDataException("constant_port requires a numeric source value");
                                value = new JObject { ["type"] = "AudioGraphNodePort", ["fields"] = new JObject {
                                    ["UnconnectedValue"] = value.DeepClone(), ["ValueIndex"] = 0, ["IsConnected"] = false
                                } };
                            }
                            changes.Add(fullPath + " -> " + destinationField + (rule["value"] != null ? ": explicit replacement" : ""));
                        }
                        var property = obj.GetType().GetProperty(destinationField);
                        if (property == null || destinationField.StartsWith("__", StringComparison.Ordinal)) throw new InvalidDataException("Destination SDK lacks field " + obj.GetType().Name + "." + destinationField);
                        if (!assigned.Add(destinationField)) throw new InvalidDataException("Two fields write " + destinationField);
                        if (value is JArray array)
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
                            property.SetValue(obj, decode(value, property.PropertyType, fullPath));
                        }
                    }
                    catch (Exception ex) { issues.Add(fullPath + ": " + ex.Message); }
                }
                foreach (var field in SoundGraphPackage.Fields(obj.GetType()).Where(f => !assigned.Contains(f.Name) && f.Name != "Name"))
                    defaults.Add(path + "." + field.Name + " (" + field.PropertyType.Name + ")");
            };

            for (int i = 0; i < objects.Length; i++)
                if (objects[i] != null) populate(objects[i], (JObject)package.Objects[i], "objects[" + i + "]");
            foreach (var rule in rules.Properties().Where(p => !usedRules.Contains(p.Name))) issues.Add("Unused field rule: " + rule.Name);
            foreach (var rule in references.Properties().Where(p => !usedRefs.Contains(p.Name))) issues.Add("Unused reference mapping: " + rule.Name);
            foreach (var rule in typeMap.Properties().Where(p => !usedTypes.Contains(p.Name))) issues.Add("Unused type mapping: " + rule.Name);
            if (issues.Count != 0) return prepared;
            try
            {
                for (int i = 0; i < objects.Length; i++) ((dynamic)objects[i]).SetInstanceGuid(new AssetClassGuid(Guid.NewGuid(), i));
                ((dynamic)objects[0]).Name = name;
                var asset = new EbxAsset(objects);
                asset.SetFileGuid(Guid.NewGuid());
                foreach (object node in objects.Where(o => o.GetType().Name == "SoundGraphData"))
                    foreach (PointerRef reference in (IEnumerable)SoundWavePort.Get(node, "Nodes"))
                        if (reference.Type != PointerRefType.Internal || !IsNode(reference.Internal.GetType())) throw new InvalidDataException("Graph execution order must reference internal AudioGraphNodeData objects.");
                var expected = SoundGraphPackage.CaptureAsset(asset, name); // validates the migrated graph too
                EbxAsset roundTrip;
                // Graphs need the standard writer traversal. The wave helper's transient/raw-order
                // flags do not produce a readable partition for these multi-object graph schemas.
                using (var writer = EbxBaseWriter.CreateWriter(new MemoryStream(), EbxWriteFlags.None))
                {
                    writer.WriteAsset(asset);
                    using (var reader = EbxReader.CreateReader(new MemoryStream(writer.ToByteArray()))) roundTrip = reader.ReadAsset<EbxAsset>();
                }
                var actual = SoundGraphPackage.CaptureAsset(roundTrip, name);
                if (!SoundWavePort.EqualFields(expected.Objects, actual.Objects)) throw new InvalidDataException("Destination binary round trip changed graph data or references.");
                prepared.Asset = roundTrip;
                prepared.Report["ready"] = true;
                prepared.Report["objects"] = actual.Objects.Count;
                prepared.Report["dependencies"] = new JArray(prepared.Dependencies.Select(e => e.Name).OrderBy(n => n));
            }
            catch (Exception ex) { issues.Add("Target serialization/validation: " + ex.Message); }
            return prepared;
        }

        private static bool IsNode(Type type)
        {
            for (; type != null; type = type.BaseType) if (type.Name == "AudioGraphNodeData") return true;
            return false;
        }

        public static EbxAssetEntry Import(Prepared prepared, string name, int bundleId, bool dryRun)
        {
            if (!prepared.Ready) throw new InvalidDataException("Graph migration is not ready; resolve the graph-plan issues first.");
            if (!Session.Project.IsLoaded || !Session.Project.IsComplete) throw new InvalidOperationException("Load a complete project or create a new project first.");
            if (App.AssetManager.GetEbxEntry(name) != null) throw new InvalidDataException("Destination graph already exists: " + name);
            if (bundleId < 0 || App.AssetManager.GetBundleEntry(bundleId) == null) throw new InvalidDataException("Destination bundle is required.");
            if ((string)((dynamic)prepared.Asset.RootObject).Name != name) throw new InvalidDataException("Prepared destination name differs.");
            if (dryRun) return null;
            EbxAssetEntry entry = null;
            try
            {
                App.AssetManager.AddEbx(name, prepared.Asset);
                entry = App.AssetManager.GetEbxEntry(name) ?? throw new IOException("AddEbx failed for graph.");
                entry.AddToBundle(bundleId);
                foreach (var dependency in prepared.Dependencies) entry.LinkAsset(dependency);
                var readBack = SoundGraphPackage.Capture(entry);
                var expected = SoundGraphPackage.CaptureAsset(prepared.Asset, name);
                if (!SoundWavePort.EqualFields(expected.Objects, readBack.Objects)) throw new IOException("Graph read-back verification failed.");
                return entry;
            }
            catch { entry = entry ?? App.AssetManager.GetEbxEntry(name); if (entry != null) App.AssetManager.RevertAsset(entry); throw; }
        }
    }
}
