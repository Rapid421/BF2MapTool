using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    public sealed class AudioCommand : ICommand
    {
        public string Name => "audio";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            try
            {
                if (args.Length == 0) throw new ArgumentException("Use audio inspect, inventory, dependencies, export, import, place, graph-inspect, graph-validate, graph-export, graph-plan or graph-import.");
                string op = args[0];
                var options = Parse(args.Skip(1).ToArray());
                if (App.AssetManager == null) throw new InvalidOperationException("AssetManager not initialized.");
                switch (op)
                {
                    case "graph-inspect":
                    case "graph-validate":
                        Check(options, "", "out");
                        Write(SoundGraphPackage.Capture(Entry(Required(options, ""))).Inspect(), options, logger); return 0;
                    case "graph-export":
                        Check(options, "", "out");
                        string graphFile = Required(options, "out");
                        if (File.Exists(graphFile) || Directory.Exists(graphFile)) throw new IOException("Graph export requires a new file.");
                        var graphPackage = SoundGraphPackage.Capture(Entry(Required(options, "")));
                        Write(JObject.FromObject(graphPackage), options, logger); return 0;
                    case "graph-plan":
                    case "graph-import":
                        Check(options, "", "as", "map", "bundle", "out");
                        var sourceGraph = SoundGraphPackage.Load(Required(options, ""));
                        var migration = options.TryGetValue("map", out string mapFile) ? JObject.Parse(File.ReadAllText(mapFile)) : new JObject();
                        string graphName = Required(options, "as");
                        var prepared = SoundGraphPort.Prepare(sourceGraph, migration, graphName);
                        if (op == "graph-import" && prepared.Ready)
                        {
                            string graphBundleName = Required(options, "bundle");
                            var graphBundle = BundleCommand.FindBundle(graphBundleName);
                            if (graphBundle == null) throw new ArgumentException("Destination bundle not found: " + graphBundleName);
                            var addedGraph = SoundGraphPort.Import(prepared, graphName, App.AssetManager.GetBundleId(graphBundle), DryRun.Active);
                            prepared.Report["file_guid"] = addedGraph?.Guid.ToString();
                            prepared.Report["dry_run"] = DryRun.Active;
                        }
                        Write(prepared.Report, options, logger);
                        return prepared.Ready ? 0 : 1;
                    case "inspect":
                        Check(options, "", "out");
                        Write(Describe(Entry(Required(options, ""))), options, logger); return 0;
                    case "inventory":
                        Check(options, "", "out");
                        var rows = new JArray(); int failures = 0;
                        string filter = options.ContainsKey("") ? options[""] : "";
                        foreach (var e in App.AssetManager.EnumerateEbx("SoundWaveAsset").Where(e => e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(e => e.Name))
                        {
                            try { rows.Add(Describe(e)); }
                            catch (Exception error) { rows.Add(new JObject { ["asset"] = e.Name, ["error"] = error.Message }); failures++; }
                        }
                        Write(new JObject { ["game"] = Session.GameName, ["count"] = rows.Count, ["failures"] = failures, ["assets"] = rows }, options, logger);
                        return failures == 0 ? 0 : 1;
                    case "dependencies":
                        Check(options, "", "out");
                        Write(Dependencies(Entry(Required(options, ""))), options, logger); return 0;
                    case "export":
                        Check(options, "", "out");
                        return Export(Entry(Required(options, "")), Required(options, "out"), logger, formatter);
                    case "import":
                        Check(options, "", "as", "template", "bundle");
                        List<byte[]> payloads;
                        var package = AudioPackage.Load(Required(options, ""), out payloads);
                        string bundleName = Required(options, "bundle");
                        var bundle = App.AssetManager.EnumerateBundles().FirstOrDefault(b => string.Equals(b.Name, bundleName, StringComparison.OrdinalIgnoreCase));
                        if (bundle == null) throw new ArgumentException("Destination bundle not found: " + bundleName);
                        var imported = SoundWavePort.Import(package, payloads, Required(options, "template"), Required(options, "as"), App.AssetManager.GetBundleId(bundle), DryRun.Active);
                        formatter.WriteResult("OK", "audio import", new Dictionary<string, string> {
                            { "asset", Required(options, "as") }, { "chunks", payloads.Count.ToString() },
                            { "source_game", package.Game }, { "dry_run", DryRun.Active.ToString() },
                            { "guid", imported?.Guid.ToString() ?? "" }
                        });
                        return 0;
                    case "place":
                        Check(options, "", "into", "pos", "play-on-creation");
                        return Place(options, logger, formatter);
                    default: throw new ArgumentException("Unknown audio operation: " + op);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("audio: " + ex.Message); return 1; }
        }

        internal static Dictionary<string, string> Parse(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string key = "", value = args[i];
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    key = args[i].Substring(2);
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--" + key + " requires a value.");
                    value = args[i];
                }
                if (result.ContainsKey(key)) throw new ArgumentException("Duplicate or unexpected argument: " + (key == "" ? value : "--" + key));
                result.Add(key, value);
            }
            return result;
        }

        private static void Check(Dictionary<string, string> options, params string[] allowed)
        {
            foreach (string key in options.Keys)
                if (!allowed.Contains(key)) throw new ArgumentException("Unknown option --" + key);
        }

        private static string Required(Dictionary<string, string> options, string key)
        {
            if (!options.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Missing " + (key == "" ? "asset/package argument" : "--" + key));
            return value;
        }

        private static EbxAssetEntry Entry(string name)
        {
            return App.AssetManager.GetEbxEntry(name) ?? throw new ArgumentException("Asset not found: " + name);
        }

        private static JObject Describe(EbxAssetEntry entry)
        {
            List<byte[]> bytes;
            var p = SoundWavePort.Capture(entry, out bytes);
            var variations = new JArray();
            int index = 0;
            foreach (JToken v in (JArray)p.Wave["RuntimeVariations"])
            {
                var segments = new JArray();
                int first = (int)v["FirstSegmentIndex"], count = (int)v["SegmentCount"], ci = (int)v["ChunkIndex"];
                for (int i = first; i < first + count; i++)
                {
                    var segment = p.Wave["Segments"][i];
                    var h = AudioSegmentHeader.Read(bytes[ci], (long)segment["SamplesOffset"]);
                    segments.Add(new JObject { ["segment"] = i - first, ["codec"] = "0x" + h.Codec.ToString("x2"), ["channels"] = h.Channels,
                        ["sample_rate"] = h.SampleRate, ["frames"] = h.Frames, ["seconds"] = h.Seconds,
                        ["samples_offset"] = segment["SamplesOffset"], ["seek_table_offset"] = segment["SeekTableOffset"] });
                }
                variations.Add(new JObject { ["variation"] = index++, ["chunk_index"] = ci,
                    ["loop_first_segment"] = v["FirstLoopSegmentIndex"], ["loop_last_segment"] = v["LastLoopSegmentIndex"],
                    ["weight"] = v["Weight"], ["segments"] = segments });
            }
            return new JObject { ["asset"] = entry.Name, ["game"] = Session.GameName, ["type"] = entry.Type,
                ["chunk_bytes"] = bytes.Sum(b => (long)b.Length), ["variations"] = variations };
        }

        private static JObject Dependencies(EbxAssetEntry source)
        {
            var rows = new JArray();
            var queued = new Queue<EbxAssetEntry>();
            var seen = new HashSet<Guid>();
            queued.Enqueue(source);
            while (queued.Count > 0)
            {
                var entry = queued.Dequeue();
                if (!seen.Add(entry.Guid)) continue;
                if (seen.Count > 10000) throw new InvalidDataException("Audio dependency walk exceeded 10000 assets.");
                JObject dump = EbxDumper.Dump(App.AssetManager.GetEbx(entry), true, true);
                var refs = new JArray();
                foreach (var references in dump.Descendants().OfType<JObject>().Where(o => o["__fileGuid"] != null).GroupBy(o => (string)o["__fileGuid"] + ":" + (string)o["__classGuid"]))
                {
                    JObject reference = references.First();
                    Guid guid = Guid.Parse((string)reference["__fileGuid"]);
                    var target = App.AssetManager.GetEbxEntry(guid);
                    bool follow = target != null && (target.Type == "SoundWaveAsset" || target.Type == "SoundPatchAsset" || target.Type == "SoundPatchConfigurationAsset")
                        && !target.Name.StartsWith("Sound/System/", StringComparison.OrdinalIgnoreCase);
                    refs.Add(new JObject { ["asset"] = target?.Name, ["type"] = target?.Type, ["file_guid"] = guid.ToString(),
                        ["class_guid"] = reference["__classGuid"], ["fields"] = new JArray(references.Select(r => r.Path)), ["followed"] = follow,
                        ["status"] = target == null ? "missing" : follow ? "audio" : "destination dependency/template required" });
                    if (follow) queued.Enqueue(target);
                }
                rows.Add(new JObject { ["asset"] = entry.Name, ["type"] = entry.Type, ["references"] = refs });
            }
            return new JObject { ["game"] = Session.GameName, ["root"] = source.Name, ["assets"] = rows };
        }

        private static void Write(JToken json, Dictionary<string, string> options, ExportLogger logger)
        {
            string text = json.ToString(Formatting.Indented);
            if (!options.TryGetValue("out", out string path)) { Console.WriteLine(text); return; }
            if (DryRun.Active) { DryRun.Report(logger, "write audio report to {0}", path); return; }
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, text);
        }

        private static int Export(EbxAssetEntry entry, string directory, ExportLogger logger, OutputFormatter formatter)
        {
            List<byte[]> bytes;
            var package = SoundWavePort.Capture(entry, out bytes);
            string full = Path.GetFullPath(directory);
            if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Export directory already exists; choose a new directory.");
            if (DryRun.Active) { DryRun.Report(logger, "export {0} audio chunks to {1}", bytes.Count, full); return 0; }
            Directory.CreateDirectory(full);
            for (int i = 0; i < bytes.Count; i++) File.WriteAllBytes(Path.Combine(full, package.Chunks[i].File), bytes[i]);
            // Commit the manifest last: an interrupted export cannot be mistaken for a package.
            File.WriteAllText(Path.Combine(full, "audio.json"), JsonConvert.SerializeObject(package, Formatting.Indented));
            formatter.WriteResult("OK", "audio export", new Dictionary<string, string> { { "asset", entry.Name }, { "directory", full }, { "chunks", bytes.Count.ToString() } });
            return 0;
        }

        private static int Place(Dictionary<string, string> options, ExportLogger logger, OutputFormatter formatter)
        {
            if (!Session.Project.IsLoaded || !Session.Project.IsComplete) throw new InvalidOperationException("A complete project is required.");
            var sound = Entry(Required(options, ""));
            if (sound.Type != "SoundPatchAsset" && sound.Type != "SoundPatchConfigurationAsset")
                throw new ArgumentException("Place a playable SoundPatchAsset or SoundPatchConfigurationAsset; waves must be referenced by a destination-game sound patch.");
            var entry = Entry(Required(options, "into"));
            var before = App.AssetManager.GetEbx(entry);
            var asset = SoundWavePort.CloneEbx(before);
            var list = SoundWavePort.Get(asset.RootObject, "Objects") as IList;
            if (list == null || list.IsReadOnly || list.IsFixedSize) throw new ArgumentException("Destination must have a writable Objects list.");
            object entity = EbxWriteCommand.CreateObjectByType("SoundEntityData");
            if (entity == null) throw new InvalidOperationException("SDK lacks SoundEntityData.");
            dynamic root = App.AssetManager.GetEbx(sound).RootObject;
            AssetClassGuid soundGuid = root.GetInstanceGuid();
            EbxRefWriter.SetRefByPath(entity, "Sound", sound.Guid, soundGuid.ExportedGuid);
            bool play = options.TryGetValue("play-on-creation", out string value) ? bool.Parse(value) : false;
            PropertyPathWriter.SetValue(entity, "PlayOnCreation", play ? "true" : "false");
            PropertyPathWriter.SetValue(entity, "EnableOnCreation", "true");
            PropertyPathWriter.SetValue(entity, "UseParentTransform", "false");
            var transform = TransformBuilder.Build(Required(options, "pos"), null, null);
            object matrix = SoundWavePort.Get(entity, "Transform");
            foreach (var row in new[] { Tuple.Create("right", transform.Right), Tuple.Create("up", transform.Up), Tuple.Create("forward", transform.Forward), Tuple.Create("trans", transform.Trans) })
                for (int i = 0; i < 3; i++) PropertyPathWriter.SetValue(matrix, row.Item1 + "." + "xyz"[i], row.Item2[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            entity.GetType().GetProperty("Transform").SetValue(entity, matrix);
            Guid id = Guid.NewGuid();
            ((dynamic)entity).SetInstanceGuid(new AssetClassGuid(id, LevelCommand.NextInternalId(asset)));
            list.Add(new PointerRef(entity));
            asset.AddObject(entity, false);
            InProcessObjects.Mark(entity);
            asset = SoundWavePort.CloneEbx(asset);
            if (DryRun.Active) { DryRun.Report(logger, "place sound {0} in {1}", sound.Name, entry.Name); return 0; }
            try { App.AssetManager.ModifyEbx(entry.Name, asset); }
            catch { App.AssetManager.ModifyEbx(entry.Name, before); throw; }
            formatter.WriteResult("OK", "audio place", new Dictionary<string, string> {
                { "layer", entry.Name }, { "sound", sound.Name }, { "guid", id.ToString() }, { "play_on_creation", play.ToString() }
            });
            return 0;
        }
    }
}
