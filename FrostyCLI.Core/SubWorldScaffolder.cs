using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Pure planning logic for cloning a SubWorld folder: renames paths, assigns
    /// fresh file GUIDs, and remaps internal PointerRefs from old to new GUIDs.
    /// Operates on caller-supplied root objects so it is unit-testable without
    /// the AssetManager. Cloning of roots is done here via JSON round-trip.
    /// </summary>
    public static class SubWorldScaffolder
    {
        public sealed class Source
        {
            public string Path;
            public Guid   FileGuid;
            public object Root;   // the root object graph to clone (unit tests pass a POCO here)
            public FrostySdk.IO.EbxAsset Asset; // real SDK asset -> cloned via binary round-trip (preserves refs/CStrings)
        }

        public sealed class Plan_Result
        {
            public string SourcePath;
            public string DestPath;
            public Guid   NewFileGuid;
            public object NewRoot;
            // The whole cloned partition (every object, instance guids intact). Rebuilding an asset from NewRoot alone
            // keeps only what the root reaches, which silently drops exported objects that only OTHER partitions
            // reference (a level's WorldPartData lost 20 of 24 objects that way).
            public FrostySdk.IO.EbxAsset NewAsset;
        }

        public static List<Plan_Result> Plan(IList<Source> sources, string templatePrefix, string destPrefix)
        {
            if (string.IsNullOrEmpty(templatePrefix)) throw new ArgumentException("templatePrefix required");
            if (string.IsNullOrEmpty(destPrefix)) throw new ArgumentException("destPrefix required");
            return Plan(sources, path =>
            {
                if (!path.StartsWith(templatePrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("scaffold: source '" + path + "' not under template prefix");
                return destPrefix + path.Substring(templatePrefix.Length);
            });
        }

        // Same, with the destination path of each source chosen by the caller (level dupe renames the root
        // leaf as well as the directory, which a prefix swap cannot express).
        public static List<Plan_Result> Plan(IList<Source> sources, Func<string, string> destFor)
        {
            if (sources == null) throw new ArgumentNullException("sources");
            if (destFor == null) throw new ArgumentNullException("destFor");

            var results = new List<Plan_Result>(sources.Count);
            var guidRemap = new Dictionary<Guid, Guid>();

            // First pass: assign new file guids + dest paths + deep-clone roots.
            foreach (Source s in sources)
            {
                string dest = destFor(s.Path);
                Guid newGuid = Guid.NewGuid();
                guidRemap[s.FileGuid] = newGuid;
                results.Add(new Plan_Result
                {
                    SourcePath = s.Path,
                    DestPath = dest,
                    NewFileGuid = newGuid,
                    NewAsset = CloneAsset(s),
                });
            }

            foreach (Plan_Result r in results)
                r.NewRoot = r.NewAsset != null ? r.NewAsset.RootObject : CloneRoot(sources.First(x => x.Path == r.SourcePath));

            // Second pass: remap PointerRefs in every object of each clone (not just what the root reaches).
            foreach (Plan_Result r in results)
            {
                if (r.NewAsset != null) { var seen = new HashSet<object>(); foreach (object o in r.NewAsset.Objects) RemapRefs(o, guidRemap, seen); }
                else RemapRefs(r.NewRoot, guidRemap, new HashSet<object>());
            }

            return results;
        }

        // Clones a source's root object. For a real SDK asset, clone the whole EbxAsset via the binary
        // round-trip (EbxBaseWriter -> EbxReader) and return its root -- this PRESERVES internal
        // PointerRefs, CStrings and EventSpec Ids that a JSON round-trip drops (beta bug #2). The JSON
        // fallback is only for unit tests that pass plain POCOs (no Asset).
        // Full-partition clone for a real SDK asset (null for unit-test POCO sources, which fall back to CloneRoot).
        private static FrostySdk.IO.EbxAsset CloneAsset(Source s)
        {
            if (s.Asset == null) return null;
            using (var writer = FrostySdk.IO.EbxBaseWriter.CreateProjectWriter(new System.IO.MemoryStream(),
                       FrostySdk.IO.EbxWriteFlags.DoNotSort | FrostySdk.IO.EbxWriteFlags.IncludeTransient))
            {
                writer.WriteAsset(s.Asset);
                byte[] buf = writer.ToByteArray();
                using (var reader = FrostySdk.IO.EbxReader.CreateProjectReader(new System.IO.MemoryStream(buf)))
                    return reader.ReadAsset<FrostySdk.IO.EbxAsset>();
            }
        }

        private static object CloneRoot(Source s)
        {
            if (s.Asset != null)
            {
                using (var writer = FrostySdk.IO.EbxBaseWriter.CreateProjectWriter(new System.IO.MemoryStream(),
                           FrostySdk.IO.EbxWriteFlags.DoNotSort | FrostySdk.IO.EbxWriteFlags.IncludeTransient))
                {
                    writer.WriteAsset(s.Asset);
                    byte[] buf = writer.ToByteArray();
                    using (var reader = FrostySdk.IO.EbxReader.CreateProjectReader(new System.IO.MemoryStream(buf)))
                        return reader.ReadAsset<FrostySdk.IO.EbxAsset>().RootObject;
                }
            }
            return DeepClone(s.Root);
        }

        private static object DeepClone(object root)
        {
            if (root == null) return null;
            string json = JsonConvert.SerializeObject(root);
            return JsonConvert.DeserializeObject(json, root.GetType());
        }

        // Walks the object graph; for any struct/class named "PointerRef" with an
        // External whose FileGuid is in remap, rewrites it. Handles value-type
        // write-back for list elements and properties.
        internal static void RemapRefs(object obj, Dictionary<Guid, Guid> remap, HashSet<object> seen)
        {
            if (obj == null) return;
            Type t = obj.GetType();
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum) return;
            if (!t.IsValueType) { if (!seen.Add(obj)) return; }

            if (obj is IList)
            {
                IList list = (IList)obj;
                for (int i = 0; i < list.Count; i++)
                {
                    object el = list[i];
                    if (el == null) continue;
                    if (TryRemapPointer(ref el, remap)) { list[i] = el; continue; }
                    RemapRefs(el, remap, seen);
                    if (el.GetType().IsValueType) list[i] = el; // struct write-back
                }
                return;
            }

            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                object v;
                try { v = p.GetValue(obj); } catch { continue; }
                if (v == null) continue;
                if (TryRemapPointer(ref v, remap)) { if (p.CanWrite) p.SetValue(obj, v); continue; }
                RemapRefs(v, remap, seen);
                if (v.GetType().IsValueType && p.CanWrite) p.SetValue(obj, v); // struct write-back
            }
        }

        private static bool TryRemapPointer(ref object val, Dictionary<Guid, Guid> remap)
        {
            Type t = val.GetType();
            if (t.Name != "PointerRef") return false;
            // External member (field or property)
            object ext = GetMember(val, t, "External");
            if (ext == null) return true; // still a PointerRef; nothing to remap
            Type et = ext.GetType();
            object fgObj = GetMember(ext, et, "FileGuid");
            if (!(fgObj is Guid)) return true;
            Guid fg = (Guid)fgObj;
            Guid mapped;
            if (remap.TryGetValue(fg, out mapped))
            {
                SetMember(ext, et, "FileGuid", mapped);
                // PointerRef.External has no setter, so writing it back through reflection silently did nothing
                // and no reference was ever remapped. Build a new PointerRef from the remapped import instead;
                // every caller writes `val` back into its list slot or property.
                ConstructorInfo ctor = t.GetConstructor(new[] { et });
                if (ctor != null) val = ctor.Invoke(new[] { ext });
                else SetMember(val, t, "External", ext);
            }
            return true;
        }

        private static object GetMember(object o, Type t, string name)
        {
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(o);
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return f != null ? f.GetValue(o) : null;
        }

        private static void SetMember(object o, Type t, string name, object value)
        {
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(o, value); return; }
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(o, value);
        }
    }
}
