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
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    // mod merge <a.fbmod> <b.fbmod> ... [--report]
    //
    // Puts several mods into the loaded project so they work TOGETHER. Each mod is applied as 'mod apply' does; an
    // existing asset that more than one mod changes (no custom handler) would otherwise end up as the last mod's copy -
    // Frosty Mod Manager does the same - so those assets are three-way merged against the unmodified game: every
    // mod's changed fields and every element a mod added to (or removed from) a list are put onto one copy. List
    // insertions keep their place (after the element the mod put them after), and insertions from later mods go after
    // earlier mods' insertions at the same place, so parallel lists (a picker's levels and its labels) stay aligned.
    // A field two mods set to different values is reported (the later mod's value is kept). An object a mod ADDED
    // inside a shared asset is not merged (refused, named in the report). --report: say what would be merged, change
    // nothing. Afterwards 'mod build' writes one mod with all of it.
    internal static class ModMerge
    {
        internal static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            var list = args.Skip(1).ToList();
            bool report = list.Remove("--report");
            bool menuOnly = list.Remove("--menu-only");
            var paths = list.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
            if (paths.Count < 1) { logger.LogError("Usage: mod merge <a.fbmod> <b.fbmod> ... [--report]"); return 2; }
            foreach (string p in paths) if (!File.Exists(p)) { logger.LogError("mod merge: file not found: " + p); return 2; }
            if (!Session.Project.IsLoaded) { logger.LogError("mod merge: no project loaded. Run 'project new' first."); return 2; }
            AssetManager am = App.AssetManager;

            // 1 which existing assets does more than one mod change (with data, no handler)
            var mods = new List<KeyValuePair<string, Frosty.Core.Mod.FrostyMod>>();
            foreach (string p in paths)
            {
                try { mods.Add(new KeyValuePair<string, Frosty.Core.Mod.FrostyMod>(p, new Frosty.Core.Mod.FrostyMod(p))); }
                catch (Exception ex) { logger.LogError("mod merge: cannot read " + p + ": " + ex.Message); return 1; }
            }
            var owners = new Dictionary<string, List<KeyValuePair<int, Frosty.Core.Mod.BaseModResource>>>(StringComparer.OrdinalIgnoreCase);
            var handled = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < mods.Count; i++)
                foreach (Frosty.Core.Mod.BaseModResource r in mods[i].Value.Resources)
                {
                    if (r.Type != Frosty.Core.Mod.ModResourceType.Ebx || r.ResourceIndex == -1 || r.IsAdded) continue;
                    if (am.GetEbxEntry(r.Name) == null) continue;
                    if (r.HasHandler) { handled[r.Name] = handled.TryGetValue(r.Name, out int n) ? n + 1 : 1; continue; }
                    if (!owners.TryGetValue(r.Name, out var l)) owners[r.Name] = l = new List<KeyValuePair<int, Frosty.Core.Mod.BaseModResource>>();
                    l.Add(new KeyValuePair<int, Frosty.Core.Mod.BaseModResource>(i, r));
                }
            var shared = owners.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            logger.Log("mod merge: {0} mod(s); {1} existing asset(s) changed by more than one; {2} handler asset(s) left to their handler ({3})",
                mods.Count, shared.Count, handled.Count(kv => kv.Value > 1), string.Join(", ", handled.Where(kv => kv.Value > 1).Select(kv => kv.Key)));

            // 2 three-way merge of each shared asset
            var merged = new Dictionary<string, EbxAsset>(StringComparer.OrdinalIgnoreCase);
            int refused = 0, conflicts = 0;
            foreach (string name in shared)
            {
                EbxAssetEntry entry = am.GetEbxEntry(name);
                EbxAsset baseAsset = am.GetEbx(entry, true);
                EbxAsset target = am.GetEbx(entry, true);
                if (ReferenceEquals(baseAsset, target)) { logger.LogError("mod merge: the asset manager returned one instance twice for " + name); return 1; }
                var m = new Merger(baseAsset, target, logger, name);
                foreach (var own in owners[name])
                {
                    string modName = Path.GetFileNameWithoutExtension(mods[own.Key].Key);
                    EbxAsset theirs;
                    try
                    {
                        byte[] blob = mods[own.Key].Value.GetResourceData(own.Value);
                        byte[] raw = new CasReader(new MemoryStream(blob), null, 0u).Read();
                        theirs = EbxReader.CreateReader(new MemoryStream(raw)).ReadAsset<EbxAsset>();
                    }
                    catch (Exception ex) { logger.LogError("mod merge: cannot read " + name + " from " + modName + ": " + ex.Message); return 1; }
                    try { m.Merge(theirs, modName); }
                    catch (Exception ex) { m.Lines.Add("REFUSED " + modName + ": the merge failed: " + ex.GetType().Name + ": " + ex.Message); m.Refused++; }
                }
                foreach (string line in m.Lines) logger.Log("  " + name + ": " + line);
                refused += m.Refused; conflicts += m.Conflicts;
                merged[name] = target;
            }
            if (refused > 0)
            {
                logger.LogError("mod merge: {0} change(s) cannot be merged (see 'REFUSED' above); nothing was applied", refused);
                return 1;
            }
            if (report)
            {
                formatter.WriteResult("OK", "mod merge --report", new Dictionary<string, string> { ["mods"] = mods.Count.ToString(), ["shared"] = shared.Count.ToString(), ["conflicts"] = conflicts.ToString() });
                return 0;
            }

            // --menu-only: the project gets ONLY the merged shared assets; 'mod build' then writes a small mod to be
            // loaded after the (unchanged) map mods, so its copies win - no map data is unpacked or re-encoded
            if (menuOnly)
            {
                foreach (var kv in merged) am.ModifyEbx(kv.Key, kv.Value);
                formatter.WriteResult("OK", "mod merge --menu-only", new Dictionary<string, string> { ["mods"] = mods.Count.ToString(), ["merged"] = merged.Count.ToString(), ["conflicts"] = conflicts.ToString() });
                return 0;
            }

            // 3 apply every mod, then put the merged copies over the last mod's
            foreach (var kv in mods)
            {
                int rc = ModBuildCommand.ExecuteApply(new[] { "apply", kv.Key }, logger, formatter);
                if (rc != 0) { logger.LogError("mod merge: applying " + kv.Key + " failed (rc " + rc + ")"); return rc; }
            }
            foreach (var kv in merged) am.ModifyEbx(kv.Key, kv.Value);
            formatter.WriteResult("OK", "mod merge", new Dictionary<string, string> { ["mods"] = mods.Count.ToString(), ["merged"] = merged.Count.ToString(), ["conflicts"] = conflicts.ToString() });
            return 0;
        }

        // ---- three-way merge of one asset ------------------------------------------------------------------------
        //
        // Object identity: base and target are two reads of the same unmodified file, so their objects pair by
        // AssetClassGuid. A mod's copy was written by Frosty, which renumbers the internal (non-exported) objects, so
        // its objects are paired with the base's STRUCTURALLY: exported objects by guid and the roots first, then in
        // parallel along every internal pointer (lists of pointers aligned by a longest common subsequence on type and
        // plain fields). A mod object left unpaired is one the mod added.

        internal sealed class Merger
        {
            readonly EbxAsset baseAsset, target;
            readonly Dictionary<object, object> baseByKey, targetByKey;
            readonly ExportLogger logger;
            readonly string asset;
            // every object of base, target and the current mod's copy -> its key (base/target: AssetClassGuid; mod:
            // the paired base object's key, or a fresh token for an added object)
            readonly Dictionary<object, object> keyOf = new Dictionary<object, object>(RefEq.Instance);
            string mod;
            readonly Dictionary<Tuple<object, int>, int> placed = new Dictionary<Tuple<object, int>, int>();
            readonly Dictionary<string, string> setBy = new Dictionary<string, string>();
            internal readonly List<string> Lines = new List<string>();
            internal int Refused, Conflicts;

            internal Merger(EbxAsset baseAsset, EbxAsset target, ExportLogger logger, string asset)
            {
                this.baseAsset = baseAsset; this.target = target;
                baseByKey = new Dictionary<object, object>(); targetByKey = new Dictionary<object, object>();
                foreach (object o in baseAsset.Objects) { object k = FileKey(o); baseByKey[k] = o; keyOf[o] = k; }
                foreach (object o in target.Objects) { object k = FileKey(o); targetByKey[k] = o; keyOf[o] = k; }
                this.logger = logger; this.asset = asset;
            }

            static object FileKey(object o)
            {
                var g = (AssetClassGuid)o.GetType().GetMethod("GetInstanceGuid").Invoke(o, null);
                return g.IsExported ? (object)g.ExportedGuid : g.InternalId;
            }

            object K(object o) => o != null && keyOf.TryGetValue(o, out object k) ? k : null;

            // ---- pairing the mod's objects with the base's
            Dictionary<object, object> PairObjects(EbxAsset theirs)
            {
                var pair = new Dictionary<object, object>(RefEq.Instance);      // theirs object -> base object
                var taken = new HashSet<object>(RefEq.Instance);                // base objects already paired
                var queue = new Queue<Tuple<object, object>>();
                Action<object, object> link = (m, b) =>
                {
                    if (m == null || b == null || pair.ContainsKey(m) || taken.Contains(b) || m.GetType() != b.GetType()) return;
                    pair[m] = b; taken.Add(b); queue.Enqueue(Tuple.Create(b, m));
                };
                var baseExported = new Dictionary<Guid, object>();
                foreach (object o in baseAsset.Objects) { object k = FileKey(o); if (k is Guid g) baseExported[g] = o; }
                foreach (object o in theirs.Objects) { object k = FileKey(o); if (k is Guid g && baseExported.TryGetValue(g, out object b)) link(o, b); }
                link(theirs.RootObject, baseAsset.RootObject);
                var br = baseAsset.RootObjects.ToList(); var mr = theirs.RootObjects.ToList();
                for (int i = 0; i < Math.Min(br.Count, mr.Count); i++) link(mr[i], br[i]);
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    WalkPair(p.Item1, p.Item2, pair, link, 0);
                }
                return pair;
            }

            void WalkPair(object b, object m, Dictionary<object, object> pair, Action<object, object> link, int depth)
            {
                if (depth > 64 || b == null || m == null || b.GetType() != m.GetType()) return;
                foreach (PropertyInfo p in Props(b.GetType()))
                {
                    object bv = p.GetValue(b), mv = p.GetValue(m);
                    if (bv is PointerRef pb && mv is PointerRef pm)
                    {
                        if (pb.Type == PointerRefType.Internal && pm.Type == PointerRefType.Internal) link(pm.Internal, pb.Internal);
                    }
                    else if (bv is IList bl && mv is IList ml)
                    {
                        // align by LCS on "already paired, or both unpaired with the same type and plain fields"
                        Func<object, object, bool> same = (x, y) => Similar(x, y, pair);
                        foreach (var ij in Lcs(bl, ml, same))
                        {
                            object x = bl[ij.Item1], y = ml[ij.Item2];
                            if (x is PointerRef px && y is PointerRef py) { if (px.Type == PointerRefType.Internal && py.Type == PointerRefType.Internal) link(py.Internal, px.Internal); }
                            else if (IsStruct(x)) WalkPair(x, y, pair, link, depth + 1);
                        }
                    }
                    else if (IsStruct(bv) && IsStruct(mv)) WalkPair(bv, mv, pair, link, depth + 1);
                }
            }

            // could x (base) and y (theirs) be the same element? Structs compare their plain fields and, one level down,
            // what their pointers point at (a picker label is a struct around a pointer to its string object)
            bool Similar(object x, object y, Dictionary<object, object> pair, int depth = 0)
            {
                if (x is PointerRef px && y is PointerRef py)
                {
                    if (px.Type != py.Type) return false;
                    if (px.Type == PointerRefType.External) return px.External.FileGuid == py.External.FileGuid && px.External.ClassGuid == py.External.ClassGuid;
                    if (px.Type != PointerRefType.Internal) return true;
                    if (pair.TryGetValue(py.Internal, out object pb)) return ReferenceEquals(pb, px.Internal);
                    return px.Internal.GetType() == py.Internal.GetType() && PlainEq(px.Internal, py.Internal);
                }
                if (x == null || y == null) return x == null && y == null;
                if (x.GetType() != y.GetType()) return false;
                if (IsStruct(x))
                {
                    if (!PlainEq(x, y)) return false;
                    if (depth >= 2) return true;
                    foreach (PropertyInfo p in Props(x.GetType()))
                    {
                        object a = p.GetValue(x), b = p.GetValue(y);
                        if ((a is PointerRef || IsStruct(a)) && !Similar(a, b, pair, depth + 1)) return false;
                    }
                    return true;
                }
                if (x.GetType().Name == "CString") return x.ToString() == y.ToString();
                return x.Equals(y);
            }

            // plain (non-pointer, non-list, non-struct) fields equal
            static bool PlainEq(object a, object b)
            {
                foreach (PropertyInfo p in Props(a.GetType()))
                {
                    object x = p.GetValue(a), y = p.GetValue(b);
                    if (x is PointerRef || x is IList || IsStruct(x)) continue;
                    if (x == null || y == null) { if (x != y) return false; continue; }
                    if (x.GetType().Name == "CString") { if (x.ToString() != y.ToString()) return false; continue; }
                    if (!x.Equals(y)) return false;
                }
                return true;
            }

            static List<Tuple<int, int>> Lcs(IList a, IList b, Func<object, object, bool> eq)
            {
                int n = a.Count, m = b.Count;
                var L = new int[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                    for (int j = m - 1; j >= 0; j--)
                        L[i, j] = eq(a[i], b[j]) ? L[i + 1, j + 1] + 1 : Math.Max(L[i + 1, j], L[i, j + 1]);
                var r = new List<Tuple<int, int>>();
                for (int i = 0, j = 0; i < n && j < m;)
                {
                    if (eq(a[i], b[j])) { r.Add(Tuple.Create(i, j)); i++; j++; }
                    else if (L[i + 1, j] >= L[i, j + 1]) i++;
                    else j++;
                }
                return r;
            }

            // ---- the merge
            internal void Merge(EbxAsset theirs, string modName)
            {
                mod = modName;
                var pair = PairObjects(theirs);
                var added = new List<object>();
                foreach (object o in theirs.Objects)
                {
                    if (pair.TryGetValue(o, out object b)) keyOf[o] = keyOf[b];
                    else { keyOf[o] = "new:" + mod + ":" + FileKey(o); added.Add(o); }
                }
                // an object the mod added gets a copy in the target first (so pointers to it can be remapped), filled after
                // the merge; like LevelDuplicator.InsertPickerLabel: its own internal id after the target's highest
                var clones = new List<Tuple<object, object>>();
                foreach (object o in added)
                {
                    object c = Activator.CreateInstance(o.GetType());
                    var g = (AssetClassGuid)o.GetType().GetMethod("GetInstanceGuid").Invoke(o, null);
                    int next = target.Objects.Select(x => ((AssetClassGuid)x.GetType().GetMethod("GetInstanceGuid").Invoke(x, null)).InternalId).DefaultIfEmpty(0).Max() + 1;
                    ((dynamic)c).SetInstanceGuid(g.IsExported ? new AssetClassGuid(g.ExportedGuid, next) : new AssetClassGuid(next));
                    target.AddObject(c, false);
                    FrostyCLI.Core.InProcessObjects.Mark(c);
                    keyOf[c] = keyOf[o];
                    targetByKey[keyOf[o]] = c;
                    clones.Add(Tuple.Create(o, c));
                }
                int lists = 0, fields = 0;
                foreach (var kv in pair)
                {
                    object k = keyOf[kv.Value];
                    var c = MergeProps(kv.Value, kv.Key, targetByKey[k], k + "");
                    lists += c.Item1; fields += c.Item2;
                }
                foreach (var oc in clones)
                    foreach (PropertyInfo p in Props(oc.Item1.GetType())) p.SetValue(oc.Item2, Remap(p.GetValue(oc.Item1)));
                int removed = baseByKey.Values.Count(b => !pair.ContainsValue(b));
                if (removed > 0) { Lines.Add("REFUSED " + mod + " removes " + removed + " object(s)"); Refused++; }
                Lines.Add(mod + ": " + lists + " list change(s), " + fields + " field change(s)" + (added.Count > 0 ? ", " + added.Count + " added object(s)" : ""));
                foreach (object o in theirs.Objects) keyOf.Remove(o);
            }

            static IEnumerable<PropertyInfo> Props(Type t) =>
                t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                 .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.Name.StartsWith("__", StringComparison.Ordinal));

            // (list changes, field changes)
            Tuple<int, int> MergeProps(object b, object t2, object tgt, string path)
            {
                int lists = 0, fields = 0;
                foreach (PropertyInfo p in Props(b.GetType()))
                {
                    object bv = p.GetValue(b), mv = p.GetValue(t2), tv = p.GetValue(tgt);
                    string fp = path + "." + p.Name;
                    if (bv is IList bl && mv is IList ml && tv is IList tl)
                    {
                        lists += MergeList(bl, ml, tl, fp);
                        continue;
                    }
                    if (IsStruct(bv) && IsStruct(mv) && IsStruct(tv) && bv.GetType() == mv.GetType() && bv.GetType() == tv.GetType())
                    {
                        var c = MergeProps(bv, mv, tv, fp);
                        lists += c.Item1; fields += c.Item2;
                        continue;
                    }
                    if (Eq(bv, mv)) continue;
                    if (setBy.TryGetValue(fp, out string who) && !Eq(tv, mv))
                    {
                        Lines.Add("CONFLICT " + fp + ": " + who + " and " + mod + " set different values; " + mod + "'s kept"); Conflicts++;
                    }
                    p.SetValue(tgt, Remap(mv));
                    setBy[fp] = mod;
                    fields++;
                }
                return Tuple.Create(lists, fields);
            }

            int MergeList(IList bl, IList ml, IList tl, string path)
            {
                var common = Lcs(bl, ml, Eq);
                var keptBase = new bool[bl.Count];
                var keptMine = new bool[ml.Count];
                foreach (var ij in common) { keptBase[ij.Item1] = true; keptMine[ij.Item2] = true; }
                var anchorOf = new int[ml.Count];               // base index each of their elements follows (-1 = front)
                {
                    int last = -1, c = 0;
                    for (int j = 0; j < ml.Count; j++)
                    {
                        if (keptMine[j]) { last = common[c].Item1; c++; }
                        anchorOf[j] = last;
                    }
                }
                int changes = 0;
                for (int i = 0; i < bl.Count; i++)
                {
                    if (keptBase[i]) continue;
                    int at = FindInTarget(tl, bl[i]);
                    if (at >= 0) { tl.RemoveAt(at); changes++; Lines.Add(mod + " removes an element of " + path); }
                }
                for (int j = 0; j < ml.Count; j++)
                {
                    if (keptMine[j]) continue;
                    int a = anchorOf[j], pos;
                    if (a < 0) pos = 0;
                    else
                    {
                        int at = FindInTarget(tl, bl[a]);
                        if (at < 0) { Lines.Add("REFUSED " + mod + " inserts into " + path + " after an element another mod removed"); Refused++; continue; }
                        pos = at + 1;
                    }
                    var k = Tuple.Create((object)tl, a);
                    placed.TryGetValue(k, out int before);
                    pos = Math.Min(pos + before, tl.Count);
                    tl.Insert(pos, Remap(ml[j]));
                    placed[k] = before + 1;
                    changes++;
                }
                return changes;
            }

            int FindInTarget(IList tl, object baseElem)
            {
                for (int i = 0; i < tl.Count; i++) if (Eq(tl[i], baseElem)) return i;
                return -1;
            }

            // an ebx struct: a generated (profile SDK) class that is not a data container. The SDK's own value types
            // (CString, ResourceRef, FileRef, TypeRef, ...) live in FrostySdk itself and compare by Equals; generated
            // structs override Equals too, but compare internal pointers by reference, so they are walked field by field.
            static readonly Assembly SdkAssembly = typeof(PointerRef).Assembly;
            static bool IsStruct(object v) =>
                v != null && !(v is string) && !(v is IList) && v.GetType().IsClass && v.GetType().Assembly != SdkAssembly
                && v.GetType().Assembly != typeof(object).Assembly && v.GetType().GetMethod("GetInstanceGuid") == null;

            // value equality across the three assets: internal pointers compare by their object's key
            bool Eq(object a, object b)
            {
                if (a == null || b == null) return a == null && b == null;
                if (a is PointerRef pa && b is PointerRef pb)
                {
                    if (pa.Type != pb.Type) return false;
                    if (pa.Type == PointerRefType.External) return pa.External.FileGuid == pb.External.FileGuid && pa.External.ClassGuid == pb.External.ClassGuid;
                    if (pa.Type == PointerRefType.Internal) { object x = K(pa.Internal), y = K(pb.Internal); return x != null && Equals(x, y); }
                    return true;
                }
                if (a.GetType() != b.GetType()) return false;
                if (a is IList la && b is IList lb)
                {
                    if (la.Count != lb.Count) return false;
                    for (int i = 0; i < la.Count; i++) if (!Eq(la[i], lb[i])) return false;
                    return true;
                }
                if (a.GetType().Name == "CString") return a.ToString() == b.ToString();
                if (IsStruct(a))
                {
                    foreach (PropertyInfo p in Props(a.GetType()))
                        if (!Eq(p.GetValue(a), p.GetValue(b))) return false;
                    return true;
                }
                return a.Equals(b);
            }

            // a value of THEIR copy made fit for the target: internal pointers to the target's object of the same key,
            // structs and lists copied
            object Remap(object v)
            {
                if (v == null) return null;
                if (v is PointerRef p)
                {
                    if (p.Type != PointerRefType.Internal) return p;
                    object k = K(p.Internal);
                    if (k != null && targetByKey.TryGetValue(k, out object o)) return new PointerRef(o);
                    Lines.Add("REFUSED " + mod + " points at an object of its own (" + p.Internal.GetType().Name + ")"); Refused++;
                    return p;
                }
                if (v is IList l)
                {
                    var copy = (IList)Activator.CreateInstance(v.GetType());
                    foreach (object e in l) copy.Add(Remap(e));
                    return copy;
                }
                if (IsStruct(v))
                {
                    object c = Activator.CreateInstance(v.GetType());
                    foreach (PropertyInfo q in Props(v.GetType())) q.SetValue(c, Remap(q.GetValue(v)));
                    return c;
                }
                return v;
            }
        }

        internal sealed class RefEq : IEqualityComparer<object>
        {
            internal static readonly RefEq Instance = new RefEq();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
