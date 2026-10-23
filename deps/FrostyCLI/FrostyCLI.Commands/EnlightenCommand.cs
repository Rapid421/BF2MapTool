using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Resources;
using FrostyCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // Read-only inspection of the precomputed-GI assets used by SWBF1 and SWBF2.
    // This deliberately does not claim the two games' database payloads are compatible.
    public class EnlightenCommand : ICommand
    {
        public string Name => "enlighten";

        private static readonly string[] TextureRoles =
        {
            "SkyVisibilityTexture",
            "StaticSkyVisibilityTexture",
            "StaticIrradianceTexture",
            "StaticDirectionTexture",
            "StaticDirectionTextureG",
            "StaticDirectionTextureB"
        };

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args == null || args.Length == 0)
            {
                Console.Error.WriteLine("Usage: frostycli enlighten inspect <level-or-enlighten-ebx> [--format table|json] [--hash] | enlighten verify [--limit n] [--only end|fls]");
                return 1;
            }
            if (string.Equals(args[0], "verify", StringComparison.OrdinalIgnoreCase))
                return EnlightenVerify.Execute(Sub(args), logger);
            if (string.Equals(args[0], "relight", StringComparison.OrdinalIgnoreCase))
                return EnlightenRelight.Execute(Sub(args), logger);
            if (string.Equals(args[0], "keys", StringComparison.OrdinalIgnoreCase))
                return EnlightenKeys.Execute(Sub(args), logger);
            if (string.Equals(args[0], "export", StringComparison.OrdinalIgnoreCase))
                return EnlightenRelight.Export(Sub(args), logger);
            if (string.Equals(args[0], "geometry", StringComparison.OrdinalIgnoreCase))
                return EnlightenGeometry.Execute(Sub(args), logger);
            if (string.Equals(args[0], "port-bake", StringComparison.OrdinalIgnoreCase))
                return EnlightenPortBake.Execute(Sub(args), logger);
            if (string.Equals(args[0], "bake-apply", StringComparison.OrdinalIgnoreCase))
                return EnlightenBakeApply.Execute(Sub(args), logger);
            if (string.Equals(args[0], "chart-plan", StringComparison.OrdinalIgnoreCase))
                return EnlightenChartPlan.Execute(Sub(args), logger);
            if (string.Equals(args[0], "flux-scene", StringComparison.OrdinalIgnoreCase))
                return EnlightenFluxScene.Execute(Sub(args), logger);
            if (string.Equals(args[0], "interior-volumes", StringComparison.OrdinalIgnoreCase))
                return EnlightenInteriorVolumes.Execute(Sub(args), logger);
            if (!string.Equals(args[0], "inspect", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("enlighten: unknown subcommand '" + args[0] + "'. Use inspect, verify, export, relight, keys, geometry, port-bake or interior-volumes.");
                return 1;
            }

            string input = null;
            string format = "table";
            bool hash = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--format" && i + 1 < args.Length) format = args[++i].ToLowerInvariant();
                else if (args[i] == "--hash") hash = true;
                else if (!args[i].StartsWith("--")) input = args[i];
            }

            if (input == null)
            {
                Console.Error.WriteLine("enlighten inspect: missing <level-or-enlighten-ebx>");
                return 1;
            }
            if (format != "table" && format != "json")
            {
                Console.Error.WriteLine("enlighten inspect: --format must be table or json");
                return 1;
            }
            if (App.AssetManager == null)
            {
                Console.Error.WriteLine("enlighten inspect: AssetManager not initialized.");
                return 1;
            }

            EbxAssetEntry inputEntry = App.AssetManager.GetEbxEntry(input);
            if (inputEntry == null)
            {
                Console.Error.WriteLine("enlighten inspect: entry not found: " + input);
                return 1;
            }

            EbxAsset inputAsset;
            try { inputAsset = App.AssetManager.GetEbx(inputEntry); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("enlighten inspect: load failed: " + ex.Message);
                return 1;
            }

            JObject rootDump;
            try { rootDump = EbxDumper.Dump(inputAsset, resolve: true, deep: true); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("enlighten inspect: dump failed: " + ex.Message);
                return 1;
            }

            var warnings = new JArray();
            var entities = new JArray();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectEnlightenReferences(rootDump, entities, paths);

            string rootType = (string)rootDump["__type"] ?? inputEntry.Type;
            if (IsEnlightenAssetType(rootType)) paths.Add(inputEntry.Name);
            if (paths.Count == 0)
                warnings.Add("No Enlighten entities or Enlighten data asset were found in the selected EBX.");

            var assets = new JArray();
            foreach (string path in paths)
            {
                JObject inspected = InspectAsset(path, hash, warnings);
                if (inspected != null) assets.Add(inspected);
            }

            var result = new JObject
            {
                ["profile"] = ProfilesLibrary.ProfileName,
                ["dataVersion"] = ProfilesLibrary.DataVersion,
                ["input"] = inputEntry.Name,
                ["inputType"] = rootType,
                ["entities"] = entities,
                ["assets"] = assets,
                ["warnings"] = warnings
            };

            if (format == "json") Console.Out.WriteLine(result.ToString(Formatting.Indented));
            else WriteTable(result);
            return 0;
        }

        internal static string[] Sub(string[] args)
        {
            var r = new string[args.Length - 1];
            Array.Copy(args, 1, r, 0, r.Length);
            return r;
        }

        public static void CollectEnlightenReferences(JToken token, JArray entities, ISet<string> paths)
        {
            if (token == null) return;
            JObject obj = token as JObject;
            if (obj != null)
            {
                string type = (string)obj["__type"];
                if (type == "DynamicEnlightenEntityData" || type == "StaticEnlightenEntityData")
                {
                    var row = new JObject { ["type"] = type };
                    CopyIfPresent(obj, row, "Priority");
                    CopyIfPresent(obj, row, "Enable");
                    CopyIfPresent(obj, row, "FluxAutoBake");
                    CopyIfPresent(obj, row, "Mixed");
                    CopyIfPresent(obj, row, "DatabaseVersion");
                    CopyIfPresent(obj, row, "Flags");
                    AddPointer(obj, row, paths, "EnlightenData");
                    AddPointer(obj, row, paths, "DynamicEnlightenData");
                    entities.Add(row);
                }
                foreach (JProperty p in obj.Properties()) CollectEnlightenReferences(p.Value, entities, paths);
                return;
            }
            JArray arr = token as JArray;
            if (arr != null) foreach (JToken child in arr) CollectEnlightenReferences(child, entities, paths);
        }

        private static void AddPointer(JObject source, JObject row, ISet<string> paths, string property)
        {
            JObject pointer = source[property] as JObject;
            if (pointer == null) return;
            string path = (string)pointer["__path"];
            string target = path ?? (string)pointer["__target"];
            if (target != null) row[char.ToLowerInvariant(property[0]) + property.Substring(1)] = target;
            if (path != null) paths.Add(path);
        }

        private static void CopyIfPresent(JObject source, JObject target, string name)
        {
            JToken value = source[name];
            if (value != null) target[char.ToLowerInvariant(name[0]) + name.Substring(1)] = value.DeepClone();
        }

        private static bool IsEnlightenAssetType(string type)
        {
            return type == "EnlightenDataAsset" || type == "StaticEnlightenData";
        }

        private static JObject InspectAsset(string path, bool hash, JArray warnings)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null)
            {
                warnings.Add("Referenced Enlighten EBX was not found: " + path);
                return null;
            }

            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex)
            {
                warnings.Add("Could not load " + path + ": " + ex.Message);
                return null;
            }

            JObject dump = EbxDumper.Dump(asset, resolve: true, deep: true);
            object root = asset.RootObject;
            string kind = (string)dump["__type"] ?? entry.Type;
            var result = new JObject
            {
                ["path"] = entry.Name,
                ["fileGuid"] = entry.Guid.ToString("D"),
                ["type"] = kind,
                ["settings"] = ExtractSettings(dump)
            };

            object databaseRef = ReadMember(root, "DatabaseResource");
            ulong databaseRid = ReadResourceRid(databaseRef);
            if (databaseRid != 0)
                result["database"] = InspectRes(databaseRid, hash, warnings, "database for " + entry.Name);
            else
                warnings.Add("No database RID was readable from " + entry.Name);

            var textures = new JArray();
            foreach (string role in TextureRoles)
            {
                JObject pointer = dump[role] as JObject;
                string texturePath = pointer == null ? null : (string)pointer["__path"];
                if (texturePath == null) continue;
                JObject texture = InspectTexture(role, texturePath, hash, warnings);
                if (texture != null) textures.Add(texture);
            }
            result["textures"] = textures;
            return result;
        }

        private static JObject ExtractSettings(JObject dump)
        {
            var settings = new JObject();
            foreach (JProperty p in dump.Properties())
            {
                if (p.Name.StartsWith("__") || p.Name == "Name" || p.Name == "DatabaseResource") continue;
                if (Array.IndexOf(TextureRoles, p.Name) >= 0) continue;
                if (p.Value is JValue) settings[p.Name] = p.Value.DeepClone();
                else
                {
                    JObject obj = p.Value as JObject;
                    string type = obj == null ? null : (string)obj["__type"];
                    if (type == "Vec2" || type == "Vec3" || type == "Vec4") settings[p.Name] = obj.DeepClone();
                }
            }
            return settings;
        }

        private static JObject InspectTexture(string role, string path, bool hash, JArray warnings)
        {
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null)
            {
                warnings.Add("Baked texture EBX was not found: " + path);
                return null;
            }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex)
            {
                warnings.Add("Could not load baked texture " + path + ": " + ex.Message);
                return null;
            }

            ulong rid = ReadResourceRid(ReadMember(asset.RootObject, "Resource"));
            var result = new JObject
            {
                ["role"] = role,
                ["path"] = entry.Name,
                ["fileGuid"] = entry.Guid.ToString("D")
            };
            if (rid == 0)
            {
                warnings.Add("No texture resource RID was readable from " + entry.Name);
                return result;
            }

            JObject res = InspectRes(rid, hash, warnings, "texture " + entry.Name);
            result["resource"] = res;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(rid);
            if (resEntry == null) return result;
            try
            {
                Texture texture = App.AssetManager.GetResAs<Texture>(resEntry);
                if (texture == null) return result;
                var metadata = new JObject
                {
                    ["type"] = texture.Type.ToString(),
                    ["pixelFormat"] = texture.PixelFormat,
                    ["width"] = texture.Width,
                    ["height"] = texture.Height,
                    ["depth"] = texture.Depth,
                    ["mipCount"] = texture.MipCount,
                    ["firstMip"] = texture.FirstMip,
                    ["flags"] = texture.Flags.ToString(),
                    ["textureGroup"] = texture.TextureGroup,
                    ["chunkId"] = texture.ChunkId.ToString("D")
                };
                if (texture.ChunkId != Guid.Empty)
                {
                    ChunkAssetEntry chunk = App.AssetManager.GetChunkEntry(texture.ChunkId);
                    if (chunk != null)
                    {
                        PayloadInfo payload = ReadChunkInfo(chunk, hash);
                        if (payload != null)
                        {
                            metadata["chunkBytes"] = payload.Length;
                            if (payload.Hash != null) metadata["chunkSha256"] = payload.Hash;
                        }
                    }
                }
                result["metadata"] = metadata;
            }
            catch (Exception ex) { warnings.Add("Could not decode texture metadata for " + entry.Name + ": " + ex.Message); }
            return result;
        }

        private static JObject InspectRes(ulong rid, bool hash, JArray warnings, string label)
        {
            ResAssetEntry entry = App.AssetManager.GetResEntry(rid);
            if (entry == null)
            {
                warnings.Add("RES not found for RID " + rid + " (" + label + ")");
                return new JObject { ["rid"] = rid.ToString(), ["missing"] = true };
            }
            var result = new JObject
            {
                ["path"] = entry.Name,
                ["rid"] = entry.ResRid.ToString(),
                ["type"] = Convert.ToUInt32(entry.ResType).ToString()
            };
            try
            {
                PayloadInfo payload = ReadResInfo(entry, hash);
                if (payload != null)
                {
                    result["bytes"] = payload.Length;
                    if (payload.Hash != null) result["sha256"] = payload.Hash;
                }
            }
            catch (Exception ex) { warnings.Add("Could not read " + label + ": " + ex.Message); }
            return result;
        }

        private sealed class PayloadInfo
        {
            public long Length;
            public string Hash;
        }

        private static PayloadInfo ReadResInfo(ResAssetEntry entry, bool hash)
        {
            if (!hash)
            {
                long? entryLength = ReadEntryLength(entry);
                return entryLength.HasValue ? new PayloadInfo { Length = entryLength.Value } : null;
            }
            MethodInfo method = App.AssetManager.GetType().GetMethod("GetRes", new[] { typeof(ResAssetEntry) });
            object raw = method == null ? null : method.Invoke(App.AssetManager, new object[] { entry });
            byte[] bytes = raw as byte[];
            if (bytes != null) return new PayloadInfo { Length = bytes.LongLength, Hash = hash ? Sha256(bytes) : null };
            Stream stream = raw as Stream;
            if (stream == null) return null;
            using (stream)
            {
                if (!hash && stream.CanSeek) return new PayloadInfo { Length = stream.Length };
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    byte[] data = memory.ToArray();
                    return new PayloadInfo { Length = data.LongLength, Hash = hash ? Sha256(data) : null };
                }
            }
        }

        private static PayloadInfo ReadChunkInfo(ChunkAssetEntry entry, bool hash)
        {
            if (!hash)
            {
                long? entryLength = ReadEntryLength(entry);
                return entryLength.HasValue ? new PayloadInfo { Length = entryLength.Value } : null;
            }
            Stream stream = App.AssetManager.GetChunk(entry);
            if (stream == null) return null;
            using (stream)
            {
                if (!hash && stream.CanSeek) return new PayloadInfo { Length = stream.Length };
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    byte[] data = memory.ToArray();
                    return new PayloadInfo { Length = data.LongLength, Hash = hash ? Sha256(data) : null };
                }
            }
        }

        private static long? ReadEntryLength(object entry)
        {
            // Reading a RES/chunk can decompress hundreds of megabytes. Frosty asset
            // entries normally expose the stored size, so use that for the default
            // metadata-only inspection and only open payloads when --hash is requested.
            string[] names = { "OriginalSize", "LogicalSize", "Size" };
            foreach (string name in names)
            {
                object value = ReadMember(entry, name);
                if (value == null) continue;
                try
                {
                    long length = Convert.ToInt64(value);
                    if (length > 0) return length;
                }
                catch { }
            }
            return null;
        }

        private static object ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            Type type = obj.GetType();
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null) return property.GetValue(obj, null);
                FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null) return field.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static ulong ReadResourceRid(object resourceRef)
        {
            if (resourceRef == null) return 0;
            object direct = ReadMember(resourceRef, "ResRid") ?? ReadMember(resourceRef, "ResourceId");
            if (direct != null) try { return Convert.ToUInt64(direct); } catch { }
            try { return (ulong)(dynamic)resourceRef; } catch { return 0; }
        }

        private static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteTable(JObject result)
        {
            Console.Out.WriteLine("Profile: " + result["profile"] + " (data version " + result["dataVersion"] + ")");
            Console.Out.WriteLine("Input: " + result["input"] + " [" + result["inputType"] + "]");
            Console.Out.WriteLine("Entities: " + ((JArray)result["entities"]).Count);
            foreach (JObject entity in (JArray)result["entities"])
            {
                Console.Out.WriteLine("  " + entity["type"] + " enable=" + (entity["enable"] ?? "?")
                    + " fluxAutoBake=" + (entity["fluxAutoBake"] ?? "?")
                    + " -> " + (entity["enlightenData"] ?? "?"));
            }
            foreach (JObject asset in (JArray)result["assets"])
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(asset["type"] + ": " + asset["path"]);
                JObject database = asset["database"] as JObject;
                if (database != null)
                    Console.Out.WriteLine("  database: " + database["path"] + " type=" + database["type"]
                        + " bytes=" + database["bytes"] + HashSuffix(database));
                foreach (JObject texture in (JArray)asset["textures"])
                {
                    JObject metadata = texture["metadata"] as JObject;
                    Console.Out.WriteLine("  " + texture["role"] + ": " + texture["path"]);
                    if (metadata != null)
                        Console.Out.WriteLine("    " + metadata["width"] + "x" + metadata["height"]
                            + " " + metadata["pixelFormat"] + " mips=" + metadata["mipCount"]
                            + " chunk=" + metadata["chunkId"]);
                }
            }
            JArray warnings = (JArray)result["warnings"];
            foreach (JToken warning in warnings) Console.Out.WriteLine("WARN: " + warning);
        }

        private static string HashSuffix(JObject value)
        {
            JToken hash = value["sha256"];
            return hash == null ? "" : " sha256=" + hash;
        }
    }
}
