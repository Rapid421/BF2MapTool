using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json;

namespace FrostyCLI.Core
{
    public class AssetCatalogRecord
    {
        [JsonProperty("path")]     public string Path     { get; set; }
        [JsonProperty("type")]     public string Type     { get; set; }
        [JsonProperty("fileGuid")] public string FileGuid { get; set; }
    }

    public class AssetCatalog
    {
        // Bump to invalidate stale caches when the catalog shape/build changes.
        public const int CurrentVersion = 2;

        // Default 0 (not CurrentVersion) so a cache file written before this field existed
        // deserializes as 0 and is rejected. SaveCatalog stamps CurrentVersion on write.
        [JsonProperty("v")]        public int Version { get; set; }
        [JsonProperty("gameHash")] public string GameHash { get; set; }
        [JsonProperty("assets")]   public List<AssetCatalogRecord> Assets { get; set; } = new List<AssetCatalogRecord>();

        // A catalog with assets but no populated file GUIDs was built before the EBX SDK types were
        // loaded (stripped entries) — it must never be trusted or persisted, or it gets cached forever
        // under the constant "notoc" game hash and silently breaks every guid/type-dependent command.
        public bool IsDegenerate()
        {
            if (Assets == null || Assets.Count == 0) return false;
            foreach (var a in Assets)
                if (!string.IsNullOrEmpty(a.FileGuid) && a.FileGuid != "00000000-0000-0000-0000-000000000000")
                    return false; // at least one real guid -> not degenerate
            return true;
        }
    }

    public class RefIndex
    {
        // Bump when the ref-collection logic changes so stale caches (e.g. the empty maps produced
        // before the all-objects traversal fix) are discarded and rebuilt.
        public const int CurrentVersion = 2;

        // Default 0 (not CurrentVersion) so a pre-version cache deserializes as 0 and is rejected;
        // SaveRefIndex stamps CurrentVersion on write.
        [JsonProperty("v")] public int Version { get; set; }
        [JsonProperty("gameHash")] public string GameHash { get; set; }
        [JsonProperty("refs")] public Dictionary<string, List<string>> Refs { get; set; } = new Dictionary<string, List<string>>();
    }

    public static class IndexService
    {
        public static string OverrideCacheDir = null;

        private static AssetCatalog _catalog;
        private static RefIndex _refIndex;
        private static Dictionary<string, string> _pathByGuid;
        private static Dictionary<ulong, string> _resByRid;

        public static void Reset()
        {
            _catalog = null;
            _refIndex = null;
            _pathByGuid = null;
            _resByRid = null;
        }

        public static AssetCatalog GetCatalog()
        {
            if (_catalog != null) return _catalog;

            string hash = ComputeGameHash();
            var cached = LoadCatalogIfValid(hash);
            if (cached != null)
            {
                _catalog = cached;
                OverlayProjectAssets();
                BuildPathIndex();
                return _catalog;
            }

            BuildAndSaveCatalog(hash);
            return _catalog;
        }

        public static AssetCatalog LoadCatalogIfValid(string expectedHash)
        {
            string path = GetCatalogPath();
            if (!File.Exists(path)) return null;
            try
            {
                var cat = JsonConvert.DeserializeObject<AssetCatalog>(File.ReadAllText(path));
                if (cat == null || cat.Version != AssetCatalog.CurrentVersion || cat.GameHash != expectedHash) return null;
                if (cat.IsDegenerate()) return null; // built without SDK types — rebuild instead of trusting it
                return cat;
            }
            catch { return null; }
        }

        public static void SaveCatalog(AssetCatalog catalog)
        {
            // Never persist a degenerate catalog (built without SDK types): caching it under the
            // constant "notoc" hash would make the breakage permanent. Use it for this session only.
            if (catalog.IsDegenerate())
            {
                Console.Error.WriteLine("WARN: asset catalog has no file GUIDs (EBX SDK types not loaded?) — not caching.");
                return;
            }
            catalog.Version = AssetCatalog.CurrentVersion;
            string path = GetCatalogPath();
            string tmpPath = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(tmpPath, JsonConvert.SerializeObject(catalog, Formatting.None));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmpPath, path);
        }

        public static List<AssetCatalogRecord> MergeRecords(
            List<AssetCatalogRecord> baseRecords,
            List<AssetCatalogRecord> overrides)
        {
            var result = new List<AssetCatalogRecord>(baseRecords);
            var idx = new Dictionary<string, int>(result.Count);
            for (int i = 0; i < result.Count; i++) idx[result[i].Path] = i;

            foreach (var o in overrides)
            {
                int pos;
                if (idx.TryGetValue(o.Path, out pos)) result[pos] = o;
                else { idx[o.Path] = result.Count; result.Add(o); }
            }
            return result;
        }

        public static string TryResolvePath(Guid fileGuid)
        {
            if (_pathByGuid == null) return null;
            string key = fileGuid.ToString("D");
            string val;
            return _pathByGuid.TryGetValue(key, out val) ? val : null;
        }

        public static string TryResolveResPath(ulong resRid)
        {
            if (App.AssetManager == null || resRid == 0) return null;
            if (_resByRid == null) BuildResIndex();
            string val;
            return _resByRid.TryGetValue(resRid, out val) ? val : null;
        }

        private static void BuildResIndex()
        {
            _resByRid = new Dictionary<ulong, string>();
            foreach (ResAssetEntry e in App.AssetManager.EnumerateRes())
                if (e.ResRid != 0) _resByRid[e.ResRid] = e.Name;
        }

        public static RefIndex GetRefIndex()
        {
            if (_refIndex != null) return _refIndex;
            GetCatalog();

            string hash = ComputeGameHash();
            var cached = LoadRefIndexIfValid(hash);
            if (cached != null)
            {
                _refIndex = cached;
                return _refIndex;
            }

            BuildAndSaveRefIndex(hash);
            return _refIndex;
        }

        // Loads the cached reverse index if present and its GameHash matches; else null.
        // (Mirrors LoadCatalogIfValid so the auto-build/cache path is unit-testable without the game.)
        public static RefIndex LoadRefIndexIfValid(string expectedHash)
        {
            string refPath = GetRefIndexPath();
            string tmpPath = refPath + ".tmp";
            if (File.Exists(tmpPath)) { try { File.Delete(tmpPath); } catch { } }
            if (!File.Exists(refPath)) return null;
            try
            {
                var cached = JsonConvert.DeserializeObject<RefIndex>(File.ReadAllText(refPath));
                if (cached == null || cached.Version != RefIndex.CurrentVersion || cached.GameHash != expectedHash) return null;
                return cached;
            }
            catch { return null; }
        }

        // Persists the reverse index atomically (tmp + move), next to the catalog cache.
        public static void SaveRefIndex(RefIndex index)
        {
            index.Version = RefIndex.CurrentVersion;
            string refPath = GetRefIndexPath();
            string tmpPath = refPath + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(refPath));
            File.WriteAllText(tmpPath, JsonConvert.SerializeObject(index, Formatting.None));
            if (File.Exists(refPath)) File.Delete(refPath);
            File.Move(tmpPath, refPath);
        }

        public static string GetCatalogPath() => Path.Combine(GetCacheDir(), "catalog.json");

        public static string GetRefIndexPath() => Path.Combine(GetCacheDir(), "refs.json");

        public static string GetCacheDir()
        {
            if (OverrideCacheDir != null) return OverrideCacheDir;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "FrostyCLI", GetProfileName());
        }

        private static void BuildAndSaveCatalog(string hash)
        {
            if (App.AssetManager == null)
                throw new InvalidOperationException("IndexService: AssetManager not initialized.");

            Console.Error.WriteLine("Building asset catalog...");
            var catalog = new AssetCatalog { GameHash = hash };
            int count = 0;
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                catalog.Assets.Add(new AssetCatalogRecord
                {
                    Path     = e.Name,
                    Type     = e.Type,
                    FileGuid = e.Guid.ToString("D")
                });
                count++;
                if (count % 2000 == 0)
                    Console.Error.Write("\rBuilding asset catalog... " + count + " assets");
            }
            Console.Error.WriteLine("\rAsset catalog built: " + count + " assets — saving...");
            SaveCatalog(catalog);
            Console.Error.WriteLine("Cached to " + GetCatalogPath());

            _catalog = catalog;
            OverlayProjectAssets();
            BuildPathIndex();
        }

        private static void OverlayProjectAssets()
        {
            if (App.AssetManager == null) return;
            var mods = new List<AssetCatalogRecord>();
            foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
            {
                if (!e.IsModified) continue;
                mods.Add(new AssetCatalogRecord
                    { Path = e.Name, Type = e.Type, FileGuid = e.Guid.ToString("D") });
            }
            if (mods.Count > 0)
                _catalog.Assets = MergeRecords(_catalog.Assets, mods);
        }

        private static void BuildPathIndex()
        {
            _pathByGuid = new Dictionary<string, string>(_catalog.Assets.Count);
            foreach (var r in _catalog.Assets)
                if (!string.IsNullOrEmpty(r.FileGuid))
                    _pathByGuid[r.FileGuid] = r.Path;
        }

        private static void BuildAndSaveRefIndex(string hash)
        {
            Console.Error.WriteLine("Building reference index (first run — this may take several minutes)...");
            var index = new RefIndex { GameHash = hash };
            var refsInProgress = new Dictionary<string, HashSet<string>>();
            int scanned = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            foreach (var rec in _catalog.Assets)
            {
                scanned++;
                if (scanned % 500 == 0)
                    Console.Error.Write("\rBuilding reference index... " + scanned +
                        " assets scanned (" + (int)sw.Elapsed.TotalSeconds + "s elapsed)");

                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(rec.Path);
                if (entry == null) continue;
                EbxAsset asset;
                try { asset = App.AssetManager.GetEbx(entry); }
                catch { continue; }

                // External refs can live on ANY object in the file, not just the root — and
                // CollectRefs deliberately does not follow internal pointers, so walk every object
                // (mirrors ConnectionCommand). Walking only RootObject yielded an empty ref map.
                var visited = new HashSet<int>();
                foreach (object obj in asset.Objects)
                    CollectRefs(obj, rec.Path, refsInProgress, visited);
            }

            foreach (var kvp in refsInProgress)
                index.Refs[kvp.Key] = new List<string>(kvp.Value);

            int total = 0;
            foreach (var v in index.Refs.Values) total += v.Count;
            Console.Error.WriteLine("\rReference index built: " + total + " references (" +
                (int)sw.Elapsed.TotalSeconds + "s) — saving...");

            SaveRefIndex(index);
            Console.Error.WriteLine("Cached to " + GetRefIndexPath());

            _refIndex = index;
        }

        // Reads a Guid member by name as either a field OR a property (EbxImportReference exposes
        // FileGuid/ClassGuid as fields). Mirrors EbxDumper.ReadGuidMember.
        private static Guid? ReadGuidMember(object obj, Type t, string name)
        {
            try
            {
                FieldInfo f = t.GetField(name);
                if (f != null && f.FieldType == typeof(Guid)) return (Guid)f.GetValue(obj);
                PropertyInfo p = t.GetProperty(name);
                if (p != null && p.PropertyType == typeof(Guid)) return (Guid)p.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static void CollectRefs(object obj, string sourcePath, Dictionary<string, HashSet<string>> refs, HashSet<int> visited)
        {
            if (obj == null) return;
            int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            if (!visited.Add(id)) return;

            Type t = obj.GetType();
            if (t.Name == "PointerRef")
            {
                // Check PointerRef.Type: 0=Null, 1=Internal, 2=External
                PropertyInfo typeProp = t.GetProperty("Type");
                if (typeProp != null)
                {
                    int refType;
                    try { refType = Convert.ToInt32(typeProp.GetValue(obj)); } catch { return; }
                    if (refType != 2) return; // only External refs have cross-file GUIDs
                }

                PropertyInfo extProp = t.GetProperty("External");
                if (extProp != null)
                {
                    object ext;
                    try { ext = extProp.GetValue(obj); } catch { return; }
                    if (ext != null)
                    {
                        // EbxImportReference.FileGuid is a FIELD, not a property — reading it via
                        // GetProperty returned null and made the ENTIRE ref index empty.
                        Guid? fgN = ReadGuidMember(ext, ext.GetType(), "FileGuid");
                        if (fgN != null && fgN.Value != Guid.Empty)
                        {
                            string key = fgN.Value.ToString("D");
                            HashSet<string> set;
                            if (!refs.TryGetValue(key, out set))
                                refs[key] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(sourcePath);
                        }
                    }
                }
                return;
            }

            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead) continue;
                object val;
                try { val = pi.GetValue(obj); } catch { continue; }
                if (val == null) continue;

                Type vt = val.GetType();
                if (vt.IsPrimitive || vt == typeof(string) || vt == typeof(Guid) || vt.IsEnum) continue;

                if (val is System.Collections.IList list)
                {
                    foreach (object item in list)
                        if (item != null) CollectRefs(item, sourcePath, refs, visited);
                }
                else
                {
                    CollectRefs(val, sourcePath, refs, visited);
                }
            }
        }

        public static string GetProfileName()
        {
            if (App.FileSystem == null) return "unknown";
            try
            {
                MethodInfo m = App.FileSystem.GetType()
                    .GetMethod("GetProfileName", BindingFlags.Public | BindingFlags.Instance);
                if (m != null)
                {
                    string name = m.Invoke(App.FileSystem, null) as string;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch { }
            string basePath = App.FileSystem.BasePath ?? "";
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(basePath));
                return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLower();
            }
        }

        public static string ComputeGameHash()
        {
            if (App.FileSystem == null) return "unknown";
            string toc = Path.Combine(App.FileSystem.BasePath ?? "", "layout.toc");
            if (!File.Exists(toc)) return "notoc";
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(toc))
            {
                byte[] h = sha.ComputeHash(fs);
                return BitConverter.ToString(h).Replace("-", "").ToLower();
            }
        }
    }
}
