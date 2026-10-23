using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Frosty.Core;
using FrostySdk;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json.Linq;
using FrostyLevelTools;

namespace FrostyCLI.Core
{
    // Moving one project's work into another (2026-10-04, Utapau: add Heroes vs Villains to the finished level without
    // rebuilding it). Three projects take part:
    //   base   - the common ancestor (NL_S0: `level create` of the Arcade level)
    //   theirs - base + the change to carry over (KH_S0: the same `level create` with --modes HeroesVersusVillains)
    //   target - base + everything built since (NL22: the finished Utapau level), loaded when applying
    // `manifest` (on base) records a content hash of every asset the project added or modified and keeps their bytes;
    // `export` (on theirs) writes every asset that differs from base; `apply` (on target) writes them in:
    //   * an asset target never touched (still base's bytes, or absent) is taken whole;
    //   * an asset both sides changed is merged object by object against base, by instance id: theirs' new objects
    //     are added, objects only theirs changed are replaced, objects both changed are reported and target's kept;
    //   * file guids differ between projects (clones get fresh ones), so external references are remapped by asset
    //     name; RES get new RIDs in the target, so resource references are remapped too; chunks keep their ids.
    public static class ProjectDelta
    {
        private const EbxWriteFlags Flags = EbxWriteFlags.DoNotSort | EbxWriteFlags.IncludeTransient;

        public static byte[] EbxBytes(EbxAsset a)
        {
            using (var w = EbxBaseWriter.CreateProjectWriter(new MemoryStream(), Flags)) { w.WriteAsset(a); return w.ToByteArray(); }
        }

        // the first project-format serialisation in a process can produce bytes that do not read back (HeadlessProject.Save
        // writes twice for the same reason): every written asset is read back, and re-serialised until it does
        public static byte[] EbxBytesChecked(EbxAsset a, string name, Action<string> log)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                byte[] b = EbxBytes(a);
                try { ReadEbx(b); return b; }
                catch (Exception ex) { last = ex; }
            }
            log("ebx " + name + ": does not read back after 4 serialisations (" + last.Message + ")");
            return null;
        }

        public static EbxAsset ReadEbx(byte[] b)
        {
            using (var r = EbxReader.CreateProjectReader(new MemoryStream(b))) return r.ReadAsset<EbxAsset>();
        }

        private static string Hash(byte[] b)
        {
            using (var s = SHA1.Create()) return BitConverter.ToString(s.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
        }

        private static byte[] ReadAll(Stream st) { using (st) using (var ms = new MemoryStream()) { st.CopyTo(ms); return ms.ToArray(); } }

        private static string Safe(string name) => name.Replace('/', '#').Replace('\\', '#').Replace(':', '_');

        private static JArray BundleNames(AssetManager am, IEnumerable<int> ids) =>
            new JArray(ids.Distinct().Select(id => am.GetBundleEntry(id)?.Name).Where(n => n != null).OrderBy(n => n));

        // every added/modified asset of the loaded project, hashed; with dir, its bytes are kept there too
        public static JObject Manifest(AssetManager am, string dir, Action<string> log)
        {
            if (dir != null) { Directory.CreateDirectory(Path.Combine(dir, "ebx")); Directory.CreateDirectory(Path.Combine(dir, "res")); Directory.CreateDirectory(Path.Combine(dir, "chunk")); }
            var ebx = new JObject(); var res = new JObject(); var chunks = new JObject(); var bundles = new JArray();
            foreach (EbxAssetEntry e in am.EnumerateEbx().Where(x => x.IsAdded || x.IsModified).ToList())
            {
                byte[] b;
                try { b = EbxBytesChecked(am.GetEbx(e), e.Name, log); } catch (Exception ex) { log("ebx " + e.Name + ": cannot serialise (" + ex.Message + ")"); continue; }
                if (b == null) continue;
                // a shipped asset whose project bytes equal the original only gained bundle membership (level create mirrors
                // thousands of them into the new bundles): such an entry is linked, never rewritten
                string shipped = null;
                if (!e.IsAdded)
                {
                    try { shipped = Hash(EbxBytes(am.GetEbx(e, true))); } catch { }
                }
                ebx[e.Name] = new JObject { ["guid"] = e.Guid.ToString(), ["type"] = e.Type, ["added"] = e.IsAdded, ["hash"] = Hash(b),
                                            ["shipped"] = shipped, ["bundles"] = BundleNames(am, e.AddedBundles) };
                if (dir != null) File.WriteAllBytes(Path.Combine(dir, "ebx", Safe(e.Name) + ".bin"), b);
            }
            foreach (ResAssetEntry r in am.EnumerateRes().Where(x => x.IsAdded || x.IsModified).ToList())
            {
                byte[] b = ReadAll(am.GetRes(r));
                res[r.Name] = new JObject { ["rid"] = r.ResRid.ToString(), ["type"] = r.ResType, ["added"] = r.IsAdded, ["hash"] = Hash(b),
                                            ["meta"] = Convert.ToBase64String(r.ResMeta ?? new byte[0]), ["bundles"] = BundleNames(am, r.AddedBundles) };
                if (dir != null) File.WriteAllBytes(Path.Combine(dir, "res", Safe(r.Name) + ".bin"), b);
            }
            foreach (ChunkAssetEntry c in am.EnumerateChunks().Where(x => x.IsAdded || x.IsModified || x.AddedBundles.Count > 0).ToList())
            {
                byte[] b = c.IsAdded || c.IsModified ? ReadAll(am.GetChunk(c)) : null;
                chunks[c.Id.ToString()] = new JObject { ["added"] = c.IsAdded, ["modified"] = c.IsModified, ["hash"] = b == null ? "" : Hash(b),
                    ["h32"] = c.H32, ["firstMip"] = c.FirstMip, ["logicalOffset"] = c.LogicalOffset, ["logicalSize"] = c.LogicalSize,
                    ["rangeStart"] = c.RangeStart, ["rangeEnd"] = c.RangeEnd, ["bundles"] = BundleNames(am, c.AddedBundles) };
                if (dir != null && b != null) File.WriteAllBytes(Path.Combine(dir, "chunk", c.Id + ".bin"), b);
            }
            foreach (BundleEntry be in am.EnumerateBundles().Where(x => x.Added))
                bundles.Add(new JObject { ["name"] = be.Name, ["type"] = (int)be.Type, ["superBundle"] = be.SuperBundleId });
            return new JObject { ["ebx"] = ebx, ["res"] = res, ["chunks"] = chunks, ["bundles"] = bundles };
        }

        // theirs vs base manifest: the assets that differ, with their bytes (the base's kept in baseDir by `manifest`)
        public static JObject Export(AssetManager am, JObject baseManifest, string dir, Action<string> log)
        {
            JObject mine = Manifest(am, dir, log);
            var delta = new JObject { ["ebx"] = new JObject(), ["res"] = new JObject(), ["chunks"] = new JObject(), ["bundles"] = new JArray(),
                                      ["guids"] = new JObject() };
            foreach (var kv in (JObject)mine["ebx"])
            {
                delta["guids"][kv.Key] = kv.Value["guid"];
                JToken b = baseManifest["ebx"][kv.Key];
                bool newBundles = b == null ? kv.Value["bundles"].Any()
                                            : kv.Value["bundles"].Any(x => !b["bundles"].Any(y => (string)y == (string)x));
                bool pristine = !(bool)kv.Value["added"] && (string)kv.Value["shipped"] == (string)kv.Value["hash"];
                bool sameAsBase = b != null && (string)b["hash"] == (string)kv.Value["hash"];
                bool content = !pristine && !sameAsBase;
                if (content || newBundles)
                {
                    var entry = (JObject)kv.Value.DeepClone();
                    entry["bundlesOnly"] = !content;
                    delta["ebx"][kv.Key] = entry;
                }
                if (!content) File.Delete(Path.Combine(dir, "ebx", Safe(kv.Key) + ".bin"));
            }
            foreach (var kv in (JObject)mine["res"])
            {
                JToken b = baseManifest["res"][kv.Key];
                if (b == null || (string)b["hash"] != (string)kv.Value["hash"]) delta["res"][kv.Key] = kv.Value;
                else File.Delete(Path.Combine(dir, "res", Safe(kv.Key) + ".bin"));
            }
            foreach (var kv in (JObject)mine["chunks"])
            {
                // a chunk whose bytes equal base's (or that the project never rewrote) only needs its bundle links - shipping
                // its data again made a 42 MB mod 265 MB (670 chunks)
                JToken b = baseManifest["chunks"][kv.Key];
                bool newBundles = b == null ? kv.Value["bundles"].Any()
                                            : kv.Value["bundles"].Any(x => !b["bundles"].Any(y => (string)y == (string)x));
                string h = (string)kv.Value["hash"];
                bool content = h != "" && (b == null || (string)b["hash"] != h);
                string f = Path.Combine(dir, "chunk", kv.Key + ".bin");
                if (content || newBundles)
                {
                    var entry = (JObject)kv.Value.DeepClone();
                    entry["bundlesOnly"] = !content;
                    delta["chunks"][kv.Key] = entry;
                }
                if (!content && File.Exists(f)) File.Delete(f);
            }
            var baseBundles = new HashSet<string>(baseManifest["bundles"].Select(x => (string)x["name"]), StringComparer.OrdinalIgnoreCase);
            foreach (JToken b in mine["bundles"]) if (!baseBundles.Contains((string)b["name"])) ((JArray)delta["bundles"]).Add(b);
            return delta;
        }

        public sealed class ApplyResult
        {
            public int EbxAdded, EbxReplaced, EbxMerged, ObjectsAdded, ObjectsReplaced, ResAdded, ResReplaced, ChunksAdded, ChunksReplaced, BundlesAdded, BundleLinks;
            public List<string> Conflicts = new List<string>();
            public List<string> Notes = new List<string>();
        }

        // writes the delta into the loaded (target) project
        public static ApplyResult Apply(AssetManager am, string dir, JObject delta, JObject baseManifest, string baseDir, Action<string> log)
        {
            var r = new ApplyResult();
            Func<string, int> bundleId = n => { BundleEntry be = am.EnumerateBundles().FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)); return be == null ? -1 : am.GetBundleId(be); };

            // 1. bundles
            foreach (JToken b in delta["bundles"])
            {
                if (bundleId((string)b["name"]) >= 0) continue;
                am.AddBundle((string)b["name"], (BundleType)(int)b["type"], (int)b["superBundle"]);
                r.BundlesAdded++;
                log("bundle + " + (string)b["name"]);
            }
            Action<AssetEntry, JToken> link = (e, bundles) =>
            {
                foreach (JToken n in bundles)
                {
                    int id = bundleId((string)n);
                    if (id < 0) { r.Notes.Add("no bundle " + (string)n + " for " + e.Name); continue; }
                    if (!e.Bundles.Contains(id) && !e.AddedBundles.Contains(id)) { e.AddToBundle(id); r.BundleLinks++; }
                }
            };

            // 2. RES (new RIDs in the target -> remapped in the EBX below)
            var ridMap = new Dictionary<ulong, ResAssetEntry>();
            foreach (var kv in (JObject)delta["res"])
            {
                byte[] data = File.ReadAllBytes(Path.Combine(dir, "res", Safe(kv.Key) + ".bin"));
                byte[] meta = Convert.FromBase64String((string)kv.Value["meta"]);
                ResAssetEntry t = am.GetResEntry(kv.Key);
                if (t == null) { t = am.AddRes(kv.Key, (ResourceType)(uint)kv.Value["type"], meta, data); r.ResAdded++; }
                else { am.ModifyRes(kv.Key, data, meta); r.ResReplaced++; }
                ridMap[ulong.Parse((string)kv.Value["rid"])] = t;
                link(t, kv.Value["bundles"]);
            }

            // 3. chunks (same ids)
            foreach (var kv in (JObject)delta["chunks"])
            {
                Guid id = Guid.Parse(kv.Key);
                string f = Path.Combine(dir, "chunk", kv.Key + ".bin");
                ChunkAssetEntry c = am.GetChunkEntry(id);
                if ((bool?)kv.Value["bundlesOnly"] != true && File.Exists(f))
                {
                    byte[] data = File.ReadAllBytes(f);
                    if (c == null) { am.AddChunk(data, id); r.ChunksAdded++; }
                    else { am.ModifyChunk(id, data); r.ChunksReplaced++; }
                    c = am.GetChunkEntry(id);
                }
                if (c == null) { r.Notes.Add("chunk " + kv.Key + " neither shipped nor in the delta"); continue; }
                if ((int)kv.Value["h32"] != 0) c.H32 = (int)kv.Value["h32"];
                if ((int)kv.Value["firstMip"] >= 0 && c.FirstMip < 0) c.FirstMip = (int)kv.Value["firstMip"];
                link(c, kv.Value["bundles"]);
            }

            // 4. EBX: theirs' file guid -> target's, by name
            var guidMap = new Dictionary<Guid, Guid>();
            foreach (var kv in (JObject)delta["guids"])
            {
                EbxAssetEntry t = am.GetEbxEntry(kv.Key);
                Guid g = Guid.Parse((string)kv.Value);
                if (t != null && t.Guid != g) guidMap[g] = t.Guid;
            }
            foreach (var kv in (JObject)delta["ebx"])
            {
                if ((bool?)kv.Value["bundlesOnly"] == true)
                {
                    EbxAssetEntry be = am.GetEbxEntry(kv.Key);
                    if (be == null) r.Notes.Add("bundle-only " + kv.Key + " is not in the target");
                    else link(be, kv.Value["bundles"]);
                    continue;
                }
                EbxAsset theirs;
                try { theirs = ReadEbx(File.ReadAllBytes(Path.Combine(dir, "ebx", Safe(kv.Key) + ".bin"))); }
                catch (Exception ex) { throw new InvalidOperationException("reading " + kv.Key + " from the delta: " + ex.Message, ex); }
                Fix(theirs, guidMap, ridMap);
                EbxAssetEntry t = am.GetEbxEntry(kv.Key);
                if (t == null)
                {
                    am.AddEbx(kv.Key, theirs);
                    t = am.GetEbxEntry(kv.Key);
                    r.EbxAdded++;
                    log("ebx + " + kv.Key);
                    link(t, kv.Value["bundles"]);
                    continue;
                }
                JToken bm = baseManifest["ebx"][kv.Key];
                EbxAsset target = am.GetEbx(t);
                string targetHash = Hash(EbxBytes(target));
                bool targetUntouched = !t.IsModified || (bm != null && (string)bm["hash"] == targetHash);
                if (targetUntouched || bm == null)
                {
                    if (!targetUntouched) r.Notes.Add(kv.Key + ": changed in the target but absent from base - theirs taken whole");
                    theirs.SetFileGuid(t.Guid);
                    am.ModifyEbx(kv.Key, theirs);
                    r.EbxReplaced++;
                    log("ebx = " + kv.Key);
                }
                else
                {
                    EbxAsset baseAsset = ReadEbx(File.ReadAllBytes(Path.Combine(baseDir, "ebx", Safe(kv.Key) + ".bin")));
                    Fix(baseAsset, guidMap, ridMap);
                    int added, replaced;
                    Merge(target, baseAsset, theirs, kv.Key, r, out added, out replaced);
                    am.ModifyEbx(kv.Key, target);
                    r.EbxMerged++; r.ObjectsAdded += added; r.ObjectsReplaced += replaced;
                    log("ebx ~ " + kv.Key + " (+" + added + " objects, " + replaced + " replaced)");
                }
                link(am.GetEbxEntry(kv.Key), kv.Value["bundles"]);
            }
            return r;
        }

        private static void Fix(EbxAsset a, Dictionary<Guid, Guid> guidMap, Dictionary<ulong, ResAssetEntry> ridMap)
        {
            var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
            foreach (object o in a.Objects) LevelDuplicator.RemapRefs(o, guidMap, seen);
            if (ridMap.Count == 0) return;
            seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
            foreach (object o in a.Objects) LevelDuplicator.RepointResources(o, ridMap, seen);
        }

        private static string Key(object o)
        {
            try
            {
                AssetClassGuid g = ((dynamic)o).GetInstanceGuid();
                return g.IsExported ? g.ExportedGuid.ToString() : "#" + g.InternalId;
            }
            catch { return null; }
        }

        private static string Text(object o) => EbxDumper.DumpRootObject(o, false).ToString(Newtonsoft.Json.Formatting.None);

        // object-level three-way merge of theirs into target (both descend from base), keyed by instance id
        private static void Merge(EbxAsset target, EbxAsset baseAsset, EbxAsset theirs, string name, ApplyResult r, out int added, out int replaced)
        {
            added = replaced = 0;
            var tObj = target.Objects.ToDictionary(o => Key(o) ?? Guid.NewGuid().ToString(), o => (object)o);
            var bObj = baseAsset.Objects.ToDictionary(o => Key(o) ?? Guid.NewGuid().ToString(), o => (object)o);
            var map = new Dictionary<object, object>(LevelDuplicator.RefEq.Instance);      // theirs instance -> target instance
            var pending = new List<object>();
            foreach (object o in theirs.Objects)
            {
                string k = Key(o);
                if (k == null) continue;
                object t, b;
                bool inT = tObj.TryGetValue(k, out t), inB = bObj.TryGetValue(k, out b);
                if (!inB && !inT) { pending.Add(o); map[o] = o; continue; }                  // new in theirs
                if (inT && inB && Text(o) != Text(b))
                {
                    if (Text(t) == Text(b)) { CopyFields(o, t); replaced++; }                 // only theirs changed it
                    else r.Conflicts.Add(name + " object " + k + " (" + o.GetType().Name + "): changed on both sides, target's kept");
                }
                if (inT) map[o] = t;
            }
            foreach (object o in pending) { target.AddObject(o); InProcessObjects.Mark(o); added++; }
            // internal references inside the copied/added content still point at theirs' instances
            var seen = new HashSet<object>(LevelDuplicator.RefEq.Instance);
            foreach (object o in target.Objects) Repoint(o, map, seen);
        }

        private static void CopyFields(object from, object to)
        {
            foreach (PropertyInfo p in from.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (p.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                try { p.SetValue(to, p.GetValue(from)); } catch { }
            }
        }

        private static void Repoint(object obj, Dictionary<object, object> map, HashSet<object> seen)
        {
            if (obj == null) return;
            Type t = obj.GetType();
            if (LevelDuplicator.IsLeaf(t) || t.Name == "CString" || t.Name == "ResourceRef") return;
            if (!t.IsValueType && !seen.Add(obj)) return;
            if (obj is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object el = list[i];
                    if (el is PointerRef pr) { object repl; if (Internal(pr, map, out repl)) list[i] = repl; continue; }
                    Repoint(el, map, seen);
                    if (el != null && el.GetType().IsValueType) list[i] = el;
                }
                return;
            }
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v == null) continue;
                if (v is PointerRef pr) { object repl; if (Internal(pr, map, out repl) && p.CanWrite) p.SetValue(obj, repl); continue; }
                Repoint(v, map, seen);
                if (v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v);
            }
        }

        private static bool Internal(PointerRef pr, Dictionary<object, object> map, out object repl)
        {
            repl = null;
            if (pr.Type != PointerRefType.Internal || pr.Internal == null) return false;
            object to;
            if (!map.TryGetValue(pr.Internal, out to) || ReferenceEquals(to, pr.Internal)) return false;
            repl = new PointerRef(to);
            return true;
        }
    }
}
