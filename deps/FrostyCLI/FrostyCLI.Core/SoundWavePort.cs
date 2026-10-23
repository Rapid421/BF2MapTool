using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    public static class SoundWavePort
    {
        public static AudioPackage Capture(EbxAssetEntry entry, out List<byte[]> payloads)
        {
            if (entry == null || entry.Type != "SoundWaveAsset")
                throw new InvalidDataException("Expected a SoundWaveAsset. Use audio dependencies to find waves inside a sound patch.");
            JObject dump = EbxDumper.Dump(App.AssetManager.GetEbx(entry), true, true);
            var package = new AudioPackage { Game = Session.GameName, Asset = entry.Name, FileGuid = entry.Guid.ToString() };
            foreach (string field in AudioPackage.Fields)
            {
                if (dump[field] == null) throw new InvalidDataException("Source wave lacks " + field);
                package.Wave[field] = dump[field].DeepClone();
            }
            payloads = new List<byte[]>();
            foreach (JToken chunk in (JArray)package.Wave["Chunks"])
            {
                Guid id = Guid.Parse((string)chunk["ChunkId"]);
                var entryChunk = App.AssetManager.GetChunkEntry(id);
                if (entryChunk == null) throw new InvalidDataException("Missing audio chunk " + id);
                byte[] bytes = ReadChunk(entryChunk);
                package.Chunks.Add(new AudioChunkFile { File = "chunk-" + payloads.Count + ".bin", Size = bytes.LongLength, Sha256 = AudioPackage.Hash(bytes) });
                payloads.Add(bytes);
            }
            package.Validate(payloads);
            return package;
        }

        public static byte[] ReadChunk(ChunkAssetEntry chunk)
        {
            using (Stream stream = App.AssetManager.GetChunk(chunk))
            using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
        }

        internal static EbxAsset CloneEbx(EbxAsset asset)
        {
            using (var writer = EbxBaseWriter.CreateProjectWriter(new MemoryStream(), EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(asset);
                using (var reader = EbxReader.CreateProjectReader(new MemoryStream(writer.ToByteArray())))
                    return reader.ReadAsset<EbxAsset>();
            }
        }

        public static EbxAssetEntry Import(AudioPackage package, IList<byte[]> payloads, string template, string name, int bundleId, bool dryRun)
        {
            if (!Session.Project.IsLoaded || !Session.Project.IsComplete)
                throw new InvalidOperationException("Load a complete project or create a new project before importing audio.");
            if (Session.GameName != "swbf1" && Session.GameName != "swbf2") throw new InvalidOperationException("Unsupported destination game.");
            if (string.IsNullOrWhiteSpace(name) || App.AssetManager.GetEbxEntry(name) != null)
                throw new InvalidDataException("Provide a new, unused destination asset name.");
            if (bundleId < 0) throw new InvalidDataException("A destination bundle is required.");
            package.Validate(payloads);
            var donor = App.AssetManager.GetEbxEntry(template);
            if (donor == null || donor.Type != "SoundWaveAsset") throw new InvalidDataException("Template must be a destination-game SoundWaveAsset.");
            var asset = CloneEbx(App.AssetManager.GetEbx(donor));
            object root = asset.RootObject;
            var donorDump = EbxDumper.Dump(asset, true, true);
            if (((JArray)donorDump["RuntimeVariations"]).Any(v => (int)v["SubtitleCount"] != 0))
                throw new InvalidDataException("Use a non-dialogue template without subtitles.");
            JObject wave = (JObject)package.Wave.DeepClone();
            var ids = payloads.Select(_ => Guid.NewGuid()).ToList();
            for (int i = 0; i < ids.Count; i++) wave["Chunks"][i]["ChunkId"] = ids[i].ToString();
            foreach (var field in wave.Properties()) WriteValue(root, field.Name, field.Value);
            // Subtitle indices have no meaning across games; subtitles are explicitly outside this route.
            foreach (object v in (IEnumerable)Get(root, "RuntimeVariations")) WriteValue(v, "FirstSubtitleIndex", new JValue(0));
            foreach (string field in new[] { "Subtitles", "SubtitleStringIds" })
                if (Get(root, field) is IList list) list.Clear();
            asset.SetFileGuid(Guid.NewGuid());
            dynamic dynamicRoot = root;
            dynamicRoot.Name = name;
            dynamicRoot.SetInstanceGuid(new AssetClassGuid(Guid.NewGuid(), -1));
            // Exercise the target SDK writer and reader before anything enters the asset manager.
            asset = CloneEbx(asset);
            JObject roundTrip = EbxDumper.Dump(asset, true, true);
            foreach (string field in AudioPackage.Fields)
            {
                // FirstSubtitleIndex was intentionally zeroed above.
                if (field == "RuntimeVariations")
                    foreach (JToken v in (JArray)wave[field]) v["FirstSubtitleIndex"] = 0;
                if (!EqualFields(wave[field], roundTrip[field]))
                    throw new InvalidDataException("Destination SDK cannot preserve audio field " + field);
            }
            if (dryRun) return null;

            var added = new List<AssetEntry>();
            try
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    App.AssetManager.AddChunk(payloads[i], ids[i]);
                    var chunk = App.AssetManager.GetChunkEntry(ids[i]);
                    if (chunk == null) throw new IOException("AddChunk failed for " + ids[i]);
                    added.Add(chunk);
                    chunk.AddToBundle(bundleId);
                    if (!string.Equals(AudioPackage.Hash(ReadChunk(chunk)), package.Chunks[i].Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Imported audio chunk failed read-back verification.");
                }
                App.AssetManager.AddEbx(name, asset);
                var entry = App.AssetManager.GetEbxEntry(name);
                if (entry == null) throw new IOException("AddEbx did not create the sound wave.");
                added.Add(entry);
                entry.AddToBundle(bundleId);
                foreach (Guid id in ids) entry.LinkAsset(App.AssetManager.GetChunkEntry(id));
                List<byte[]> verifiedBytes;
                Capture(entry, out verifiedBytes);
                return entry;
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                for (int i = added.Count - 1; i >= 0; i--)
                    try { App.AssetManager.RevertAsset(added[i]); } catch (Exception rollback) { errors.Add(rollback); }
                if (errors.Count > 1) throw new AggregateException("Audio import failed and rollback was incomplete.", errors);
                throw;
            }
        }

        internal static object Get(object obj, string field)
        {
            var property = obj.GetType().GetProperty(field);
            if (property == null) throw new InvalidDataException("Destination SDK lacks " + obj.GetType().Name + "." + field);
            return property.GetValue(obj);
        }

        internal static bool EqualFields(JToken expected, JToken actual)
        {
            if (expected == null || actual == null) return expected == actual;
            if (expected.Type == JTokenType.Float && actual.Type == JTokenType.Float)
                return (float)expected == (float)actual; // EBX float32 versus JSON decimal round-trip
            if (expected is JObject obj && actual is JObject other)
                return obj.Count == other.Count && obj.Properties().All(p => EqualFields(p.Value, other[p.Name]));
            if (expected is JArray arr && actual is JArray otherArr)
                return arr.Count == otherArr.Count && arr.Select((v, i) => EqualFields(v, otherArr[i])).All(v => v);
            return JToken.DeepEquals(expected, actual);
        }

        internal static void WriteValue(object obj, string field, JToken value)
        {
            var property = obj.GetType().GetProperty(field);
            if (property == null) throw new InvalidDataException("Destination SDK lacks " + obj.GetType().Name + "." + field);
            if (value is JArray array)
            {
                var list = property.GetValue(obj) as IList;
                if (list == null || list.IsReadOnly || list.IsFixedSize) throw new InvalidDataException("Destination list is not writable: " + field);
                Type element = property.PropertyType.GetGenericArguments().Single();
                list.Clear();
                foreach (JToken token in array)
                {
                    if (token is JObject record)
                    {
                        if ((string)record["__type"] != element.Name) throw new InvalidDataException("Audio record type differs: " + field);
                        object item = Activator.CreateInstance(element);
                        foreach (var member in record.Properties())
                            if (!member.Name.StartsWith("__", StringComparison.Ordinal)) WriteValue(item, member.Name, member.Value);
                        list.Add(item);
                    }
                    else list.Add(token.ToObject(element));
                }
            }
            else
            {
                if (!property.CanWrite || !(value is JValue)) throw new InvalidDataException("Unsupported audio field: " + field);
                property.SetValue(obj, property.PropertyType.IsEnum
                    ? Enum.Parse(property.PropertyType, (string)value, false)
                    : value.ToObject(property.PropertyType));
            }
        }
    }
}
