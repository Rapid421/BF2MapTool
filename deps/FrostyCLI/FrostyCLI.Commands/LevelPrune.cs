using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `level prune`: take objects out of a level asset together with every connection that touches them. `level clear
    /// --remove` drops objects but leaves their Event/Property/Link connections pointing at nothing; a sub-world whose
    /// lighting variants are chosen by a random roll (Hoth_01's Hoth_012: a RandomMultiEvent in Lighting_Setup streaming
    /// Cloudy or Sunset in, a PC-only prefab streaming HighEnd in) has to lose the references, the roll's layer AND the
    /// wiring, or the dangling connections stay in the asset.
    /// <code>
    /// level prune &lt;asset&gt; [--object &lt;sel&gt;]... [--drop-connections-to &lt;sel&gt;]... [--refs-into &lt;asset&gt;]... [--connections-in &lt;asset&gt;]... [--dry-run]
    ///   sel = &lt;exported instance guid&gt; | &lt;TypeName&gt; | &lt;TypeName&gt;:&lt;Field&gt;=&lt;value&gt;
    ///         (a PointerRef field compares the referenced asset's name, anything else its string form; case-insensitive)
    /// </code>
    /// --object removes the matching objects from the root Objects list and every connection with one of them at either
    /// end; --drop-connections-to removes the connections whose TARGET matches (the object stays); --refs-into removes the
    /// connections with an end in that other asset (the objects of a layer whose reference was pruned).
    /// --connections-in &lt;asset&gt; also removes, from that OTHER partition, every connection with an end on a removed object:
    /// a BFII layer keeps no wiring of its own -- its connections live in the world partition that references it (Hoth's
    /// Lighting layer: 5 property, 2 event and 36 link connections on the root LevelData), so pruning the layer's objects
    /// alone would leave those pointing at nothing.
    /// connections with an end in that other asset (the objects of a layer whose reference was pruned); --dangling removes
    /// the connections with an external end that names an instance its partition no longer exports (the closure audit's
    /// MISSING rule: what `level clear --remove` / `level remove` leave behind in the parent sub-worlds).
    /// </summary>
    public static class LevelPrune
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string path = null;
            var objects = new List<string>();
            var targets = new List<string>();
            var into = new List<string>();
            var connectionsIn = new List<string>();
            bool dangling = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--dangling") { dangling = true; continue; }
                if (args[i] == "--object" && i + 1 < args.Length) objects.Add(args[++i]);
                else if (args[i] == "--drop-connections-to" && i + 1 < args.Length) targets.Add(args[++i]);
                else if (args[i] == "--refs-into" && i + 1 < args.Length) into.Add(args[++i]);
                else if (args[i] == "--connections-in" && i + 1 < args.Length) connectionsIn.Add(args[++i]);
                else if (args[i].StartsWith("--")) { Console.Error.WriteLine("level prune: unknown option " + args[i]); return 1; }
                else path = args[i];
            }
            if (path == null || (objects.Count == 0 && targets.Count == 0 && into.Count == 0 && !dangling))
            {
                Console.Error.WriteLine("Usage: frostycli level prune <asset> [--object <sel>]... [--drop-connections-to <sel>]... [--refs-into <asset>]... [--connections-in <asset>]... [--dangling]");
                return 1;
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("level prune: AssetManager not initialized."); return 1; }
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(path);
            if (entry == null) { Console.Error.WriteLine("level prune: not found: " + path); return 1; }
            var intoGuids = new HashSet<Guid>();
            foreach (string a in into)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(a);
                if (e == null) { Console.Error.WriteLine("level prune: --refs-into asset not found: " + a); return 1; }
                intoGuids.Add(e.Guid);
            }
            EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch (Exception ex) { Console.Error.WriteLine("level prune: load failed: " + ex.Message); return 1; }
            IList list = LevelCommand.GetEntityList(asset.RootObject);
            if (list == null) { Console.Error.WriteLine("level prune: " + path + " has no Objects list"); return 1; }

            // 1. the objects to remove
            var removed = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var removedGuids = new HashSet<Guid>();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                object o = LevelCommand.ResolveInternal(list[i]);
                if (o == null) continue;
                if (!objects.Any(s => Matches(o, s))) continue;
                removed.Add(o);
                Guid g = ExportedGuid(o);
                if (g != Guid.Empty) removedGuids.Add(g);
                if (!DryRun.Active) list.RemoveAt(i);
                logger.Log("level prune: removing {0} {1}", o.GetType().Name, Describe(o));
            }
            foreach (string s in objects)
                if (!removed.Any(o => Matches(o, s))) { Console.Error.WriteLine("level prune: no object matches --object " + s); return 1; }

            // 2. connections touching them, targeting a --drop-connections-to object, or reaching into a pruned layer
            int dropped = 0;
            var kinds = new Dictionary<string, int>();
            var dang = dangling ? new DanglingRefs() : null;
            // --dangling on a list of external refs (a NetworkRegistryAsset's Objects): drop the entries naming an instance
            // their partition no longer exports (Mygeeto GA1: 54 FantasyBattle registry entries for pruned Kamino doors /
            // air spawns = 54 unresolved imports in the closure audit)
            if (dang != null)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    object item = list[i];
                    if (!(item is FrostySdk.Ebx.PointerRef pr) || pr.Type != FrostySdk.IO.PointerRefType.External) continue;
                    if (!dang.IsDangling(item, out string whyEntry)) continue;
                    if (!DryRun.Active) list.RemoveAt(i);
                    dropped++;
                    kinds["Objects"] = kinds.TryGetValue("Objects", out int n0) ? n0 + 1 : 1;
                    logger.Log("level prune: dangling Objects entry: {0}", whyEntry);
                }
            }
            foreach (object owner in asset.Objects.ToList())
            {
                if (owner == null) continue;
                foreach (PropertyInfo pi in owner.GetType().GetProperties())
                {
                    if (!pi.Name.EndsWith("Connections", StringComparison.Ordinal)) continue;
                    IList conns;
                    try { conns = pi.GetValue(owner) as IList; } catch { continue; }
                    if (conns == null) continue;
                    for (int k = conns.Count - 1; k >= 0; k--)
                    {
                        object c = conns[k];
                        if (c == null) continue;
                        object src = Endpoint(c, "Source"), dst = Endpoint(c, "Target");
                        bool drop = Touches(src, removed, removedGuids, intoGuids) || Touches(dst, removed, removedGuids, intoGuids)
                                    || targets.Any(s => EndpointMatches(dst, s));
                        string why = null;
                        if (!drop && dang != null && (dang.IsDangling(src, out why) || dang.IsDangling(dst, out why)))
                        {
                            drop = true;
                            logger.Log("level prune: dangling {0}: {1}", pi.Name, why);
                        }
                        if (!drop) continue;
                        if (!DryRun.Active) conns.RemoveAt(k);
                        dropped++;
                        kinds[pi.Name] = kinds.TryGetValue(pi.Name, out int n) ? n + 1 : 1;
                    }
                }
            }
            // 3. the same objects' wiring in other partitions (external ends: this asset's file guid + the object's
            //    exported instance guid)
            var elsewhere = new List<KeyValuePair<string, EbxAsset>>();
            int droppedElsewhere = 0;
            foreach (string other in connectionsIn)
            {
                EbxAssetEntry oe = App.AssetManager.GetEbxEntry(other);
                if (oe == null) { Console.Error.WriteLine("level prune: --connections-in asset not found: " + other); return 1; }
                EbxAsset oa;
                try { oa = App.AssetManager.GetEbx(oe); }
                catch (Exception ex) { Console.Error.WriteLine("level prune: load failed: " + other + ": " + ex.Message); return 1; }
                int n = DropExternalConnections(oa, entry.Guid, removedGuids, !DryRun.Active, kinds);
                logger.Log("level prune: {0}: {1} connection(s) touching the pruned objects", other, n);
                droppedElsewhere += n;
                if (n > 0) elsewhere.Add(new KeyValuePair<string, EbxAsset>(other, oa));
            }
            if (DryRun.Active)
            {
                DryRun.Report(logger, "prune {0}: remove {1} object(s), {2} connection(s), {3} in other partitions", path, removed.Count, dropped, droppedElsewhere);
                return 0;
            }
            // the pruned objects leave the partition too: taken out of Objects only, they stay partition objects and the
            // writer keeps their imports (a pruned LayerReferenceObjectData still pulled its layer into the bundle closure)
            foreach (object o in removed) asset.RemoveObject(o);
            if (!EbxWriteCommand.CallModifyEbx(path, asset, logger)) return 1;
            foreach (var kv in elsewhere)
                if (!EbxWriteCommand.CallModifyEbx(kv.Key, kv.Value, logger)) return 1;
            formatter.WriteResult("OK", "level prune", new Dictionary<string, string>
            {
                { "asset", path },
                { "objects_removed", removed.Count.ToString(CultureInfo.InvariantCulture) },
                { "connections_removed", dropped.ToString(CultureInfo.InvariantCulture) },
                { "connections_removed_elsewhere", droppedElsewhere.ToString(CultureInfo.InvariantCulture) },
                { "by_kind", string.Join(",", kinds.Select(kv => kv.Key + "=" + kv.Value)) },
            });
            return 0;
        }

        /// <summary>Remove (when <paramref name="apply"/>) every connection of <paramref name="asset"/> with an external end
        /// on (<paramref name="fileGuid"/>, one of <paramref name="classGuids"/>); returns how many matched.</summary>
        internal static int DropExternalConnections(EbxAsset asset, Guid fileGuid, HashSet<Guid> classGuids, bool apply, Dictionary<string, int> kinds)
        {
            int n = 0;
            if (classGuids.Count == 0) return 0;
            foreach (object owner in asset.Objects.ToList())
            {
                if (owner == null) continue;
                foreach (PropertyInfo pi in owner.GetType().GetProperties())
                {
                    if (!pi.Name.EndsWith("Connections", StringComparison.Ordinal)) continue;
                    IList conns;
                    try { conns = pi.GetValue(owner) as IList; } catch { continue; }
                    if (conns == null) continue;
                    for (int k = conns.Count - 1; k >= 0; k--)
                    {
                        object c = conns[k];
                        if (c == null) continue;
                        if (!ExternalEndIs(Endpoint(c, "Source"), fileGuid, classGuids) && !ExternalEndIs(Endpoint(c, "Target"), fileGuid, classGuids)) continue;
                        if (apply) conns.RemoveAt(k);
                        n++;
                        if (kinds != null) kinds[pi.Name] = kinds.TryGetValue(pi.Name, out int m) ? m + 1 : 1;
                    }
                }
            }
            return n;
        }

        static bool ExternalEndIs(object pr, Guid fileGuid, HashSet<Guid> classGuids)
        {
            if (pr == null) return false;
            Type t = pr.GetType();
            string type = (t.GetField("Type")?.GetValue(pr) ?? t.GetProperty("Type")?.GetValue(pr))?.ToString();
            if (type != "External") return false;
            object ext = t.GetField("External")?.GetValue(pr) ?? t.GetProperty("External")?.GetValue(pr);
            if (ext == null) return false;
            Type et = ext.GetType();
            object fg = et.GetField("FileGuid")?.GetValue(ext) ?? et.GetProperty("FileGuid")?.GetValue(ext);
            object cg = et.GetField("ClassGuid")?.GetValue(ext) ?? et.GetProperty("ClassGuid")?.GetValue(ext);
            return fg is Guid f && f == fileGuid && cg is Guid c && classGuids.Contains(c);
        }

        /// <summary>A selector: an exported instance guid, a type name, or TypeName:Field=value.</summary>
        internal static bool Matches(object o, string sel)
        {
            if (o == null || string.IsNullOrEmpty(sel)) return false;
            Guid g;
            if (Guid.TryParse(sel, out g)) return ExportedGuid(o) == g;
            string type = sel, field = null, value = null;
            int colon = sel.IndexOf(':');
            if (colon > 0)
            {
                type = sel.Substring(0, colon);
                string rest = sel.Substring(colon + 1);
                int eq = rest.IndexOf('=');
                if (eq <= 0) return false;
                field = rest.Substring(0, eq); value = rest.Substring(eq + 1);
            }
            if (!string.Equals(o.GetType().Name, type, StringComparison.OrdinalIgnoreCase)) return false;
            if (field == null) return true;
            PropertyInfo pi = o.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null) return false;
            object v = pi.GetValue(o);
            return string.Equals(ValueString(v), value, StringComparison.OrdinalIgnoreCase);
        }

        static string ValueString(object v)
        {
            if (v == null) return "";
            if (v.GetType().Name == "PointerRef")
            {
                string t = v.GetType().GetField("Type")?.GetValue(v)?.ToString() ?? v.GetType().GetProperty("Type")?.GetValue(v)?.ToString();
                if (t == "External")
                {
                    object ext = v.GetType().GetField("External")?.GetValue(v) ?? v.GetType().GetProperty("External")?.GetValue(v);
                    object fg = ext?.GetType().GetField("FileGuid")?.GetValue(ext) ?? ext?.GetType().GetProperty("FileGuid")?.GetValue(ext);
                    if (fg is Guid fileGuid) return App.AssetManager.GetEbxEntry(fileGuid)?.Name ?? fileGuid.ToString();
                }
                return "";
            }
            return v.ToString();
        }

        static object Endpoint(object conn, string side)
        {
            object pr = conn.GetType().GetProperty(side)?.GetValue(conn);
            return pr;
        }

        /// <summary>Does a connection end (a PointerRef) name a removed object, or an object of a --refs-into asset?</summary>
        static bool Touches(object pr, HashSet<object> removed, HashSet<Guid> removedGuids, HashSet<Guid> intoGuids)
        {
            if (pr == null) return false;
            Type t = pr.GetType();
            string type = (t.GetField("Type")?.GetValue(pr) ?? t.GetProperty("Type")?.GetValue(pr))?.ToString();
            if (type == "Internal")
            {
                object inner = t.GetField("Internal")?.GetValue(pr) ?? t.GetProperty("Internal")?.GetValue(pr);
                return inner != null && removed.Contains(inner);
            }
            if (type == "External")
            {
                object ext = t.GetField("External")?.GetValue(pr) ?? t.GetProperty("External")?.GetValue(pr);
                if (ext == null) return false;
                Type et = ext.GetType();
                object fg = et.GetField("FileGuid")?.GetValue(ext) ?? et.GetProperty("FileGuid")?.GetValue(ext);
                object cg = et.GetField("ClassGuid")?.GetValue(ext) ?? et.GetProperty("ClassGuid")?.GetValue(ext);
                if (fg is Guid f && intoGuids.Contains(f)) return true;
                if (cg is Guid c && removedGuids.Contains(c)) return true;
            }
            return false;
        }

        /// <summary>A connection end against a selector: an internal end is the object itself; an external end can only
        /// be matched by its exported instance guid (the other asset is not loaded here).</summary>
        static bool EndpointMatches(object pr, string sel)
        {
            if (pr == null) return false;
            Type t = pr.GetType();
            string type = (t.GetField("Type")?.GetValue(pr) ?? t.GetProperty("Type")?.GetValue(pr))?.ToString();
            if (type == "Internal") return Matches(t.GetField("Internal")?.GetValue(pr) ?? t.GetProperty("Internal")?.GetValue(pr), sel);
            if (type == "External")
            {
                object ext = t.GetField("External")?.GetValue(pr) ?? t.GetProperty("External")?.GetValue(pr);
                object cg = ext?.GetType().GetField("ClassGuid")?.GetValue(ext) ?? ext?.GetType().GetProperty("ClassGuid")?.GetValue(ext);
                Guid g;
                return cg is Guid c && Guid.TryParse(sel, out g) && c == g;
            }
            return false;
        }

        static Guid ExportedGuid(object o)
        {
            try
            {
                object g = o.GetType().GetMethod("GetInstanceGuid")?.Invoke(o, null);
                object eg = g?.GetType().GetProperty("ExportedGuid")?.GetValue(g) ?? g?.GetType().GetField("ExportedGuid")?.GetValue(g);
                return eg is Guid e ? e : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        static string Describe(object o)
        {
            foreach (string f in new[] { "BundleName", "Blueprint" })
            {
                PropertyInfo pi = o.GetType().GetProperty(f);
                if (pi == null) continue;
                string s = ValueString(pi.GetValue(o));
                if (!string.IsNullOrEmpty(s)) return f + "=" + s;
            }
            Guid g = ExportedGuid(o);
            return g != Guid.Empty ? g.ToString() : "";
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }
}
