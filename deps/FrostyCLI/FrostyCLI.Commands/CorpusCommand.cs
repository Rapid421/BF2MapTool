using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Frosty.Core;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using FrostyCLI.Core.Corpus;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    // Queries over the mod corpus: thousands of shipped .fbmod mods, parsed and indexed, next to
    // the game's own values. It answers what the editor cannot: what a field is normally set to,
    // what mods actually change, which mods fight, and what a duplicated asset was copied from.
    //
    // corpus.db alone answers stats / field / asset / mod / conflicts / bundles / wires.
    // asset-index.jsonl beside it adds diff and donor. Vanilla values come from the game install
    // (live, one asset at a time) or from vanilla-index.jsonl once `corpus dump-vanilla` has
    // written it, which makes diff instant.
    public class CorpusCommand : ICommand
    {
        public string Name => "corpus";

        private const string Usage =
            "Usage: frostycli corpus <sub> [--dir <corpus folder>] ...\n" +
            "  stats                                 what is indexed, and what is missing\n" +
            "  field <name> [--top n]                values mods ship for a field (RateOfFire, MaxHealth...)\n" +
            "  asset <path-fragment> [--top n]       which mods modify or duplicate an asset\n" +
            "  mod <name-fragment> [--top n]         every asset one mod touches, ADD or mod\n" +
            "  conflicts <mod> [--top n]             mods that set the same fields to different values\n" +
            "  bundles <mod> [--top n]               bundles a mod adds assets to\n" +
            "  wires [--type <T>] [--port <p>] [--top n]   common blueprint wiring across mods\n" +
            "  vanilla <asset>                       the game's own values for one asset\n" +
            "  diff <mod> [--limit n] [--multi] [--out f.jsonl]   exactly what a mod changes vs the game\n" +
            "  donor <added-asset> [--top n]         what a duplicated asset was copied from, and what changed\n" +
            "  dump-vanilla [--limit n]              write vanilla-index.jsonl from the game (makes diff instant)";

        private static readonly string[] GameSubs = { "dump-vanilla", "vanilla", "diff" };

        // Program asks before initializing the game: aggregate queries need no install, and
        // loading one costs far more than the query. vanilla/diff need it only when there is no
        // vanilla-index.jsonl to read instead.
        public static bool NeedsGame(string[] args)
        {
            if (args.Length == 0) return false;
            string sub = args[0].ToLowerInvariant();
            if (sub == "dump-vanilla") return true;
            if (!GameSubs.Contains(sub)) return false;
            return !new CorpusStore(CorpusStore.ResolveDir(Opt(args, "--dir"))).HasVanillaIndex;
        }

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length == 0) { Console.Error.WriteLine(Usage); return 1; }
            string sub = args[0].ToLowerInvariant();
            string[] rest = args.Skip(1).ToArray();
            var store = new CorpusStore(CorpusStore.ResolveDir(Opt(rest, "--dir")));
            try
            {
                switch (sub)
                {
                    case "stats":        return Stats(store);
                    case "field":        return Field(store, rest);
                    case "asset":        return Asset(store, rest);
                    case "mod":          return Mod(store, rest);
                    case "conflicts":    return Conflicts(store, rest);
                    case "bundles":      return Bundles(store, rest);
                    case "wires":        return Wires(store, rest);
                    case "vanilla":      return Vanilla(store, rest);
                    case "diff":         return Diff(store, rest);
                    case "donor":        return Donor(store, rest);
                    case "dump-vanilla": return DumpVanilla(store, rest, logger);
                    default: Console.Error.WriteLine(Usage); return 1;
                }
            }
            catch (FileNotFoundException ex) { Console.Error.WriteLine("corpus: " + ex.Message); return 1; }
            catch (DllNotFoundException) { Console.Error.WriteLine("corpus: winsqlite3.dll not found (it ships with Windows 10 and 11)."); return 1; }
        }

        // ------------------------------------------------------------------ aggregates

        private static int Stats(CorpusStore store)
        {
            Console.WriteLine("corpus folder  " + store.Dir);
            using (Sqlite db = store.OpenDb())
            {
                foreach (object[] r in db.Query("SELECT key, value FROM meta"))
                    Console.WriteLine("  {0,-18} {1}", r[0], r[1]);
                foreach (string t in new[] { "assets", "field_values", "vanilla", "wires", "conflicts", "bundles" })
                {
                    try { Console.WriteLine("  {0,-18} {1:N0} rows", t, db.Scalar("SELECT COUNT(*) FROM " + t)); }
                    catch (InvalidOperationException) { }
                }
                Console.WriteLine("  {0,-18} {1:N0}", "mods", db.Scalar("SELECT COUNT(DISTINCT mod) FROM assets"));
            }
            Console.WriteLine("  {0,-18} {1}", "corpus.db", Size(store.DbPath));
            Console.WriteLine("  {0,-18} {1}", "asset-index", store.HasModIndex ? Size(store.ModIndexPath)
                : "missing - diff and donor need it");
            Console.WriteLine("  {0,-18} {1}", "vanilla-index", store.HasVanillaIndex ? Size(store.VanillaIndexPath)
                : "missing - vanilla/diff read the game live; `corpus dump-vanilla` makes them instant");
            return 0;
        }

        private static int Field(CorpusStore store, string[] a)
        {
            string field = Positional(a);
            if (field == null) { Console.Error.WriteLine("corpus field: missing <name>"); return 1; }
            int top = IntOpt(a, "--top", 25);
            using (Sqlite db = store.OpenDb())
            {
                string like = "%" + field + "%";
                var rows = db.Query("SELECT field, value, n, example_mod FROM field_values WHERE field LIKE ? ORDER BY n DESC LIMIT ?", like, top);
                if (rows.Count == 0) { Console.WriteLine("no field matching '" + field + "'. Try a shorter fragment."); return 1; }
                Console.WriteLine("fields matching '" + field + "':");
                foreach (object[] s in db.Query("SELECT field, total, distinct_seen FROM field_stats WHERE field LIKE ? ORDER BY total DESC LIMIT 12", like))
                    Console.WriteLine("  {0,-50} {1,7:N0} assets carry it, {2} distinct values{3}", s[0], s[1], s[2],
                        Convert.ToInt64(s[2]) > 119 ? " (capped)" : "");
                Console.WriteLine();
                Console.WriteLine("most common values:");
                foreach (object[] r in rows)
                    Console.WriteLine("  {0,6:N0}  {1,-44} = {2,-22} e.g. {3}", r[2], Cut(r[0], 44), Cut(r[1], 22), Cut(r[3], 50));
            }
            return 0;
        }

        private static int Asset(CorpusStore store, string[] a)
        {
            string frag = Positional(a);
            if (frag == null) { Console.Error.WriteLine("corpus asset: missing <path-fragment>"); return 1; }
            int top = IntOpt(a, "--top", 30);
            string like = "%" + frag.ToLowerInvariant() + "%";
            using (Sqlite db = store.OpenDb())
            {
                var rows = db.Query("SELECT mod, added FROM assets WHERE asset LIKE ? LIMIT 4000", like);
                if (rows.Count == 0) { Console.WriteLine("no asset matching '" + frag + "' in the corpus"); return 1; }
                var names = db.Query("SELECT DISTINCT asset FROM assets WHERE asset LIKE ? LIMIT 8", like).Select(r => (string)r[0]);
                var added = new SortedSet<string>(rows.Where(r => (long)r[1] != 0).Select(r => (string)r[0]));
                var mods = new SortedSet<string>(rows.Where(r => (long)r[1] == 0).Select(r => (string)r[0]));
                Console.WriteLine("{0} rows across {1} mods", rows.Count, rows.Select(r => r[0]).Distinct().Count());
                Console.WriteLine("  matching assets: " + string.Join(", ", names));
                Console.WriteLine("  modified by {0} mods, ADDED (duplicated) by {1}", mods.Count, added.Count);
                Console.WriteLine();
                foreach (string m in mods.Take(top)) Console.WriteLine("  mod  " + m);
                foreach (string m in added.Take(top)) Console.WriteLine("  ADD  " + m);
            }
            return 0;
        }

        private static int Mod(CorpusStore store, string[] a)
        {
            string frag = Positional(a);
            if (frag == null) { Console.Error.WriteLine("corpus mod: missing <name-fragment>"); return 1; }
            int top = IntOpt(a, "--top", 120);
            using (Sqlite db = store.OpenDb())
            {
                string mod = ResolveMod(db, frag);
                if (mod == null) return 1;
                var rows = db.Query("SELECT asset, added, objectCount FROM assets WHERE mod = ? ORDER BY added DESC, asset", mod);
                Console.WriteLine(mod);
                Console.WriteLine("  {0} assets: {1} modified, {2} added", rows.Count,
                    rows.Count(r => (long)r[1] == 0), rows.Count(r => (long)r[1] != 0));
                Console.WriteLine();
                foreach (object[] r in rows.Take(top))
                    Console.WriteLine("  {0}  {1}  ({2} objects)", (long)r[1] != 0 ? "ADD" : "mod", r[0], r[2]);
                if (rows.Count > top) Console.WriteLine("  ... {0} more (--top)", rows.Count - top);
            }
            return 0;
        }

        private static int Conflicts(CorpusStore store, string[] a)
        {
            string frag = Positional(a);
            if (frag == null) { Console.Error.WriteLine("corpus conflicts: missing <mod>"); return 1; }
            int top = IntOpt(a, "--top", 25);
            using (Sqlite db = store.OpenDb())
            {
                string like = "%" + frag + "%";
                var rows = db.Query("SELECT mod_a, mod_b, n FROM conflicts WHERE mod_a LIKE ? OR mod_b LIKE ? ORDER BY n DESC LIMIT ?", like, like, top);
                if (rows.Count == 0)
                {
                    Console.WriteLine("no recorded conflicts for '" + frag + "': it sets no field another mod sets differently.");
                    return 0;
                }
                Console.WriteLine("mods that fight '" + frag + "' (both enabled: load order decides, silently):");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object[] r in rows)
                {
                    string ma = (string)r[0], mb = (string)r[1];
                    string other = ma.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0 ? mb : ma;
                    if (seen.Add(other)) Console.WriteLine("  {0,6:N0} fields  {1}", r[2], other);
                }
            }
            return 0;
        }

        private static int Bundles(CorpusStore store, string[] a)
        {
            string frag = Positional(a);
            if (frag == null) { Console.Error.WriteLine("corpus bundles: missing <mod>"); return 1; }
            int top = IntOpt(a, "--top", 40);
            using (Sqlite db = store.OpenDb())
            {
                string like = "%" + frag + "%";
                var rows = db.Query("SELECT bundle, n, mod FROM bundles WHERE mod LIKE ? ORDER BY n DESC LIMIT ?", like, top);
                if (rows.Count == 0)
                {
                    Console.WriteLine("no bundle additions recorded for '" + frag + "'. A modify-only mod adds none, which is normal.");
                    return 0;
                }
                Console.WriteLine("'{0}' adds assets to {1} bundles:", frag, db.Scalar("SELECT COUNT(*) FROM bundles WHERE mod LIKE ?", like));
                foreach (object[] r in rows) Console.WriteLine("  {0,6:N0}  {1}", r[1], r[0]);
            }
            return 0;
        }

        private static int Wires(CorpusStore store, string[] a)
        {
            string type = Opt(a, "--type"), port = Opt(a, "--port");
            int top = IntOpt(a, "--top", 30);
            var sql = new StringBuilder("SELECT kind, src_type, src_field, dst_type, dst_field, n FROM wires WHERE 1=1");
            var args = new List<object>();
            if (type != null) { sql.Append(" AND (src_type LIKE ? OR dst_type LIKE ?)"); args.Add("%" + type + "%"); args.Add("%" + type + "%"); }
            if (port != null) { sql.Append(" AND (src_field LIKE ? OR dst_field LIKE ?)"); args.Add("%" + port + "%"); args.Add("%" + port + "%"); }
            sql.Append(" ORDER BY n DESC LIMIT ?");
            args.Add(top);
            using (Sqlite db = store.OpenDb())
            {
                var rows = db.Query(sql.ToString(), args.ToArray());
                if (rows.Count == 0) { Console.WriteLine("no wiring matching those filters"); return 1; }
                foreach (object[] r in rows)
                    Console.WriteLine("  {0,7:N0}  {1,-8} {2}.{3} -> {4}.{5}", r[5], r[0], r[1], r[2], r[3], r[4]);
            }
            return 0;
        }

        // ------------------------------------------------------------------ vanilla

        // Vanilla rows come from vanilla-index.jsonl when it exists, else straight from the
        // game install. Both are the same flattener, so the two sources are interchangeable.
        private sealed class VanillaSource
        {
            private readonly CorpusStore _store;
            private Dictionary<string, long> _offsets;
            private readonly Dictionary<string, JObject> _cache = new Dictionary<string, JObject>();
            public bool Live => !_store.HasVanillaIndex;

            public VanillaSource(CorpusStore store) { _store = store; }

            // null when the game has no such asset.
            public JObject Get(string asset)
            {
                string key = asset.ToLowerInvariant();
                JObject rec;
                if (_cache.TryGetValue(key, out rec)) return rec;
                rec = Live ? LiveRecord(asset) : Indexed(key);
                if (_cache.Count < 20000) _cache[key] = rec;
                return rec;
            }

            private JObject Indexed(string key)
            {
                if (_offsets == null) _offsets = LoadOffsets(_store);
                long off;
                return _offsets.TryGetValue(key, out off) ? CorpusStore.ReadAt(_store.VanillaIndexPath, off) : null;
            }

            private JObject LiveRecord(string asset)
            {
                if (App.AssetManager == null)
                    throw new InvalidOperationException("no vanilla-index.jsonl and no game loaded: pass --game-path, or run `corpus dump-vanilla` once.");
                EnsureStringList(_store);
                EbxAssetEntry entry = App.AssetManager.GetEbxEntry(asset);
                if (entry == null) return null;
                EbxAsset ebx = App.AssetManager.GetEbx(entry, false);   // the shipped data, never a project's edit
                return ebx == null ? null : CorpusFlattener.Record(entry.Name, ebx.Objects);
            }
        }

        // Asset -> byte offset in vanilla-index.jsonl. corpus.db's vanilla table holds exactly
        // this, but it is only trusted when it was written for the file that is there now; an
        // offset into a different file reads as garbage, not as an error.
        private static Dictionary<string, long> LoadOffsets(CorpusStore store)
        {
            long size = new FileInfo(store.VanillaIndexPath).Length;
            using (Sqlite db = store.OpenDb())
            {
                object stamp = null;
                try { stamp = db.Scalar("SELECT value FROM meta WHERE key = 'vanilla_index_size'"); } catch (InvalidOperationException) { }
                if (stamp != null && stamp.ToString() == size.ToString(CultureInfo.InvariantCulture))
                {
                    var d = new Dictionary<string, long>();
                    db.ForEach("SELECT asset, offset FROM vanilla", null, r => d[(string)r[0]] = (long)r[1]);
                    if (d.Count > 0) return d;
                }
            }
            Console.Error.WriteLine("indexing vanilla-index.jsonl (corpus.db's table is for a different file) ...");
            return ScanOffsets(store.VanillaIndexPath);
        }

        private static Dictionary<string, long> ScanOffsets(string path)
        {
            var d = new Dictionary<string, long>();
            foreach (KeyValuePair<long, string> line in Lines(path))
            {
                string name = AssetOf(line.Value);
                if (name != null) d[name.ToLowerInvariant()] = line.Key;
            }
            return d;
        }

        private static int Vanilla(CorpusStore store, string[] a)
        {
            string asset = Positional(a);
            if (asset == null) { Console.Error.WriteLine("corpus vanilla: missing <asset>"); return 1; }
            JObject rec = new VanillaSource(store).Get(asset);
            if (rec == null)
            {
                Console.WriteLine("'" + asset + "' is not a game asset. If a mod ADDS it, try `corpus donor`.");
                return 1;
            }
            Console.WriteLine(rec["asset"]);
            Console.WriteLine("  objects {0}   types {1}", rec["objectCount"], ((JArray)rec["types"]).Count);
            foreach (JProperty p in ((JObject)rec["scalars"]).Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                Console.WriteLine("  {0,-56} {1}", Cut(p.Name, 56), Cut(p.Value.ToString(Formatting.None), 70));
            return 0;
        }

        // ------------------------------------------------------------------ diff

        private static readonly string[] NoiseTypes = { "Vec2.", "Vec3.", "Vec4.", "LinearTransform.", "AxisAlignedBox.", "QuatTransform.", "Matrix." };
        private static readonly Regex ResId = new Regex("^(?:res:)?([0-9a-fA-F]{16})$");

        private static int Diff(CorpusStore store, string[] a)
        {
            string frag = Positional(a);
            if (frag == null) { Console.Error.WriteLine("corpus diff: missing <mod>"); return 1; }
            if (!store.HasModIndex) { Console.Error.WriteLine("corpus diff: asset-index.jsonl missing from " + store.Dir); return 1; }
            int limit = IntOpt(a, "--limit", 200);
            bool showMulti = a.Contains("--multi");
            string outPath = Opt(a, "--out");

            List<object[]> rows;
            string mod;
            using (Sqlite db = store.OpenDb())
            {
                mod = ResolveMod(db, frag);
                if (mod == null) return 1;
                rows = db.Query("SELECT asset, added, offset FROM assets WHERE mod = ? ORDER BY asset", mod);
            }

            var vanilla = new VanillaSource(store);
            Console.WriteLine("{0}   {1} assets   vanilla from {2}", mod, rows.Count, vanilla.Live ? "the game (live)" : "vanilla-index.jsonl");
            Console.WriteLine();

            int compared = 0, identical = 0, added = 0, missing = 0, printed = 0, done = 0;
            var fieldCounts = new Dictionary<string, int>();
            StreamWriter outFile = outPath != null ? new StreamWriter(outPath, false, new UTF8Encoding(false)) : null;
            try
            {
                foreach (object[] r in rows)
                {
                    if (vanilla.Live && ++done % 200 == 0) Console.Error.WriteLine("  {0}/{1} assets", done, rows.Count);
                    if ((long)r[1] != 0) { added++; continue; }
                    JObject v = vanilla.Get((string)r[0]);
                    if (v == null) { missing++; continue; }
                    JObject m = CorpusStore.ReadAt(store.ModIndexPath, (long)r[2]);
                    var vs = (JObject)v["scalars"];
                    var changes = new List<JObject>();
                    foreach (JProperty p in ((JObject)m["scalars"]).Properties())
                    {
                        string k = p.Name;
                        if (k.EndsWith("__Id", StringComparison.Ordinal) || NoiseTypes.Any(n => k.StartsWith(n, StringComparison.Ordinal))) continue;
                        JToken vv = vs[k];
                        if (vv == null && !vs.ContainsKey(k)) continue;           // not in the vanilla flattening
                        if (IsInternalRef(p.Value) || IsInternalRef(vv)) continue;  // object indexes differ between readers
                        if (ValuesEqual(vv, p.Value)) continue;
                        bool multi = p.Value.Type == JTokenType.Array || vv.Type == JTokenType.Array;
                        changes.Add(new JObject { ["field"] = k, ["vanilla"] = vv, ["mod"] = p.Value, ["multi"] = multi });
                    }
                    compared++;
                    if (changes.Count == 0) { identical++; continue; }
                    foreach (JObject c in changes.Where(c => !(bool)c["multi"]))
                    {
                        string f = (string)c["field"];
                        fieldCounts[f] = fieldCounts.TryGetValue(f, out int n) ? n + 1 : 1;
                    }
                    outFile?.WriteLine(new JObject { ["mod"] = mod, ["asset"] = r[0].ToString(), ["changes"] = new JArray(changes) }.ToString(Formatting.None));

                    var shown = changes.Where(c => showMulti || !(bool)c["multi"]).ToList();
                    if (shown.Count == 0 || printed >= limit) continue;
                    printed++;
                    Console.WriteLine(r[0]);
                    foreach (JObject c in shown.Take(40))
                        Console.WriteLine("    {0,-52} {1,-22} -> {2}{3}", Cut(c["field"], 52), Cut(((JToken)c["vanilla"]).ToString(Formatting.None), 22),
                            Cut(((JToken)c["mod"]).ToString(Formatting.None), 40), (bool)c["multi"] ? "  (multi)" : "");
                    if (shown.Count > 40) Console.WriteLine("    ... {0} more", shown.Count - 40);
                }
            }
            finally { outFile?.Dispose(); }

            Console.WriteLine();
            Console.WriteLine("  compared {0}   identical {1}   changed {2}   added (no vanilla) {3}   not in game {4}",
                compared, identical, compared - identical, added, missing);
            if (printed >= limit) Console.WriteLine("  printed the first {0} changed assets (--limit)", limit);
            if (fieldCounts.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("MOST-CHANGED FIELDS");
                foreach (var kv in fieldCounts.OrderByDescending(kv => kv.Value).Take(15))
                    Console.WriteLine("  {0,6}  {1}", kv.Value, kv.Key);
            }
            Console.WriteLine();
            Console.WriteLine("  'identical' is a mod carrying an asset unchanged - the normal case. (multi) marks a key");
            Console.WriteLine("  collapsed from several objects: a lead, not proof. Hidden unless --multi.");
            if (outFile != null) Console.WriteLine("  wrote " + outPath);
            return 0;
        }

        private static bool IsInternalRef(JToken t)
        {
            return t != null && t.Type == JTokenType.String && ((string)t).StartsWith("int:", StringComparison.Ordinal);
        }

        // Equal once the two readers' formatting differences are taken out: floats to a
        // relative tolerance, res ids in either spelling, lists as unordered sets.
        private static bool ValuesEqual(JToken a, JToken b)
        {
            if (a.Type == JTokenType.Float && b.Type == JTokenType.Float)
            {
                double x = (double)a, y = (double)b;
                return x == y || Math.Abs(x - y) <= Math.Max(1e-6 * Math.Max(Math.Abs(x), Math.Abs(y)), 1e-9);
            }
            if (Norm(a) == Norm(b)) return true;
            return Norm(a, hashed: true) == Norm(b, hashed: true);
        }

        private static readonly Regex HexName = new Regex("^0x([0-9a-fA-F]{8})$");

        // A hashed name reads as "Start" through one reader and "0x0dc0efa5" through the other.
        // Compared by hash, they are the same value.
        private static string AsHash(string s)
        {
            Match m = HexName.Match(s);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            return FrostySdk.Utils.HashString(s, false).ToString("x8");
        }

        private static string Norm(JToken v, bool hashed = false)
        {
            if (v.Type == JTokenType.Array)
            {
                var items = v.Select(x => NormScalar(x, hashed)).ToList();
                items.Sort(StringComparer.Ordinal);
                return "L:" + string.Join("\u0001", items);
            }
            return NormScalar(v, hashed);
        }

        private static string NormScalar(JToken v, bool hashed)
        {
            switch (v.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return "n:" + Math.Round((double)v, 4).ToString("R", CultureInfo.InvariantCulture);
                case JTokenType.Boolean: return (bool)v ? "b:1" : "b:0";
                case JTokenType.Null: return "null";
                case JTokenType.String:
                    string s = (string)v;
                    Match m = ResId.Match(s);
                    if (m.Success) return "s:" + m.Groups[1].Value.ToLowerInvariant();
                    return hashed ? "h:" + AsHash(s) : "s:" + s;
                default: return v.ToString(Formatting.None);
            }
        }

        // ------------------------------------------------------------------ donor

        private static int Donor(CorpusStore store, string[] a)
        {
            string needle = Positional(a);
            if (needle == null) { Console.Error.WriteLine("corpus donor: missing <added-asset>"); return 1; }
            if (!store.HasModIndex) { Console.Error.WriteLine("corpus donor: asset-index.jsonl missing from " + store.Dir); return 1; }
            int top = IntOpt(a, "--top", 5);

            JObject target = null;
            using (Sqlite db = store.OpenDb())
            {
                foreach (object[] r in db.Query("SELECT offset FROM assets WHERE added = 1 AND asset LIKE ? LIMIT 50", "%" + needle.ToLowerInvariant() + "%"))
                {
                    JObject rec = CorpusStore.ReadAt(store.ModIndexPath, (long)r[0]);
                    if (target == null || ((JObject)rec["scalars"]).Count > ((JObject)target["scalars"]).Count) target = rec;
                }
            }
            if (target == null) { Console.WriteLine("no ADDED asset matching '" + needle + "'"); return 1; }

            var tTypes = new HashSet<string>(target["types"].Select(t => (string)t));
            var tScalars = (JObject)target["scalars"];
            Console.WriteLine("DUPLICATED ASSET : " + target["asset"]);
            Console.WriteLine("  from mod       : " + target["mod"]);
            Console.WriteLine("  {0} objects, {1} types, {2} scalars", target["objectCount"], tTypes.Count, tScalars.Count);
            Console.WriteLine();
            Console.Error.WriteLine("scanning asset-index.jsonl for structural matches ...");

            var ranked = new List<Tuple<double, double, double, JObject>>();
            var seen = new HashSet<string>();
            string targetAsset = (string)target["asset"];
            foreach (KeyValuePair<long, string> line in Lines(store.ModIndexPath))
            {
                string s = line.Value;
                if (s.Contains("\"added\": true") || s.Contains("\"added\":true")) continue;
                string name = AssetOf(s);
                if (name == null || name == targetAsset || seen.Contains(name)) continue;
                JObject r = JObject.Parse(s);
                double ts, vs;
                double sc = Score(tTypes, tScalars, r, out ts, out vs);
                if (sc <= 0) continue;
                seen.Add(name);
                ranked.Add(Tuple.Create(sc, ts, vs, r));
            }
            if (ranked.Count == 0)
            {
                Console.WriteLine("no structural match: the donor is not carried by any mod. Search the game by name with `where` instead.");
                return 1;
            }
            ranked.Sort((x, y) => y.Item1.CompareTo(x.Item1));
            Console.WriteLine("LIKELY DONORS");
            foreach (var r in ranked.Take(top))
            {
                Console.WriteLine("  {0:F3}  (types {1:F2}  values {2:F2})  {3}", r.Item1, r.Item2, r.Item3, r.Item4["asset"]);
                Console.WriteLine("         carried by " + Cut(r.Item4["mod"], 60));
            }
            Console.WriteLine();

            JObject donor = ranked[0].Item4;
            var d = (JObject)donor["scalars"];
            var changed = tScalars.Properties().Where(p => d[p.Name] != null && !CorpusFlattener.PyEquals(d[p.Name], p.Value)).ToList();
            var onlyDupe = tScalars.Properties().Where(p => !d.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            int onlyDonor = d.Properties().Count(p => !tScalars.ContainsKey(p.Name));
            Console.WriteLine("DIFF vs best donor  " + donor["asset"]);
            Console.WriteLine("  {0} fields changed, {1} only in the duplicate, {2} only in the donor", changed.Count, onlyDupe.Count, onlyDonor);
            if (changed.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  CHANGED (donor -> duplicate):");
                foreach (JProperty p in changed.Take(40))
                    Console.WriteLine("    {0,-48} {1,-20} -> {2}", Cut(p.Name, 48), Cut(d[p.Name].ToString(Formatting.None), 20), Cut(p.Value.ToString(Formatting.None), 30));
            }
            if (onlyDupe.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  ONLY IN THE DUPLICATE: " + string.Join(", ", onlyDupe.Take(12)));
            }
            return 0;
        }

        // 0.35 * type-set Jaccard + 0.65 * shared (field, value) pairs over the union of fields.
        private static double Score(HashSet<string> tTypes, JObject a, JObject r, out double typeScore, out double valueScore)
        {
            typeScore = valueScore = 0;
            var rt = new HashSet<string>(r["types"].Select(t => (string)t));
            if (rt.Count == 0) return 0;
            int inter = tTypes.Count(rt.Contains);
            int union = tTypes.Count + rt.Count - inter;
            typeScore = union > 0 ? (double)inter / union : 0;
            if (typeScore < 0.30) return 0;
            var b = (JObject)r["scalars"];
            int shared = a.Properties().Count(p => b[p.Name] != null && CorpusFlattener.PyEquals(b[p.Name], p.Value));
            int keys = a.Count + b.Properties().Count(p => !a.ContainsKey(p.Name));
            valueScore = (double)shared / Math.Max(keys, 1);
            return 0.35 * typeScore + 0.65 * valueScore;
        }

        // ------------------------------------------------------------------ dump-vanilla

        // Flattens every EBX asset in the install into vanilla-index.jsonl, then points
        // corpus.db's vanilla table at it. Reads shipped data only (GetEbx(entry, false)), so a
        // loaded project cannot leak into the baseline.
        private static int DumpVanilla(CorpusStore store, string[] a, ExportLogger logger)
        {
            if (App.AssetManager == null) { Console.Error.WriteLine("corpus dump-vanilla: no game loaded. Pass --game-path."); return 1; }
            EnsureStringList(store);
            int limit = IntOpt(a, "--limit", int.MaxValue);
            Directory.CreateDirectory(store.Dir);

            var entries = App.AssetManager.EnumerateEbx().Take(limit).ToList();
            Console.Error.WriteLine("dumping {0:N0} assets to {1}", entries.Count, store.VanillaIndexPath);

            var offsets = new List<object[]>(entries.Count);
            int done = 0, failed = 0;
            var started = DateTime.UtcNow;
            string tmp = store.VanillaIndexPath + ".partial";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var utf8 = new UTF8Encoding(false);
                foreach (EbxAssetEntry entry in entries)
                {
                    JObject rec;
                    try
                    {
                        EbxAsset ebx = App.AssetManager.GetEbx(entry, false);
                        if (ebx == null) { failed++; continue; }
                        rec = CorpusFlattener.Record(entry.Name, ebx.Objects);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        if (failed <= 5) Console.Error.WriteLine("  failed {0}: {1}", entry.Name, ex.GetBaseException().Message);
                        continue;
                    }
                    offsets.Add(new object[] { entry.Name.ToLowerInvariant(), fs.Position });
                    byte[] line = utf8.GetBytes(rec.ToString(Formatting.None) + "\n");
                    fs.Write(line, 0, line.Length);
                    if (++done % 5000 == 0)
                    {
                        double rate = done / Math.Max((DateTime.UtcNow - started).TotalSeconds, 1);
                        Console.Error.WriteLine("  {0,7:N0}/{1:N0}  {2:F0} assets/s  eta {3:F1} min", done, entries.Count, rate, (entries.Count - done) / rate / 60);
                    }
                }
            }
            if (File.Exists(store.VanillaIndexPath)) File.Delete(store.VanillaIndexPath);
            File.Move(tmp, store.VanillaIndexPath);
            long size = new FileInfo(store.VanillaIndexPath).Length;

            if (File.Exists(store.DbPath))
            {
                using (Sqlite db = store.OpenDb(writable: true))
                {
                    db.Exec("DELETE FROM vanilla");
                    db.ExecMany("INSERT OR REPLACE INTO vanilla VALUES (?, ?)", offsets);
                    db.Exec("DELETE FROM meta WHERE key IN ('vanilla', 'vanilla_index_size', 'vanilla_built')");
                    db.ExecMany("INSERT INTO meta VALUES (?, ?)", new[]
                    {
                        new object[] { "vanilla", store.VanillaIndexPath },
                        new object[] { "vanilla_index_size", size.ToString(CultureInfo.InvariantCulture) },
                        new object[] { "vanilla_built", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                    });
                }
            }
            Console.WriteLine("dumped {0:N0} assets ({1} failed) in {2:F0} s -> {3} ({4})", done, failed,
                (DateTime.UtcNow - started).TotalSeconds, store.VanillaIndexPath, Size(store.VanillaIndexPath));
            return 0;
        }

        // ------------------------------------------------------------------ helpers

        private static bool _stringsTried;

        // Hashed EBX names (AudioGraphEvent.Name, ExpressionPortData.PortName...) read back as
        // "0x0dc0efa5" unless Frosty's strings.txt is loaded before the asset is read; the editor
        // loads it at startup, which is where the mod-side and original vanilla indexes got their
        // names. Looked for beside the corpus, beside the exe, then beside the editor's Plugins.
        private static void EnsureStringList(CorpusStore store)
        {
            if (_stringsTried) return;
            _stringsTried = true;
            var candidates = new List<string>
            {
                Environment.GetEnvironmentVariable("FROSTYCLI_STRINGS"),
                Path.Combine(store.Dir, "strings.txt"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "strings.txt"),
            };
            if (!string.IsNullOrEmpty(Session.EditorPluginsDir))
                candidates.Add(Path.Combine(Path.GetDirectoryName(Session.EditorPluginsDir.TrimEnd('\\', '/')), "strings.txt"));
            string found = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
            if (found != null)
            {
                FrostySdk.Utils.LoadStringList(found, new ExportLogger(quiet: true));
                Console.Error.WriteLine("names from " + found);
            }
            else
            {
                Console.Error.WriteLine("no strings.txt found: hashed names print as 0x........ (diff still matches them by hash).");
            }
        }

        // An exact mod name, or the only one a fragment matches; otherwise list the candidates.
        private static string ResolveMod(Sqlite db, string frag)
        {
            var hits = db.Query("SELECT DISTINCT mod FROM assets WHERE mod LIKE ? LIMIT 40", "%" + frag + "%").Select(r => (string)r[0]).ToList();
            string exact = hits.FirstOrDefault(h => string.Equals(h, frag, StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(Path.GetFileNameWithoutExtension(h), frag, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0)
            {
                Console.WriteLine("no mod matching '" + frag + "' in the corpus. This is a name miss, not a mod that changes nothing.");
                return null;
            }
            Console.WriteLine("{0}{1} mods match '{2}' - narrow it down:", hits.Count, hits.Count == 40 ? "+" : "", frag);
            foreach (string h in hits.Take(20)) Console.WriteLine("  " + h);
            return null;
        }

        // Each line of a JSONL file with the byte offset it starts at.
        private static IEnumerable<KeyValuePair<long, string>> Lines(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buf = new MemoryStream();
                long start = 0, pos = 0;
                int b;
                while ((b = fs.ReadByte()) != -1)
                {
                    pos++;
                    if (b == '\n')
                    {
                        yield return new KeyValuePair<long, string>(start, Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length));
                        buf.SetLength(0);
                        start = pos;
                    }
                    else buf.WriteByte((byte)b);
                }
                if (buf.Length > 0) yield return new KeyValuePair<long, string>(start, Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length));
            }
        }

        // The "asset" value of a row without parsing the whole row.
        private static string AssetOf(string line)
        {
            int i = line.IndexOf("\"asset\":", StringComparison.Ordinal);
            if (i < 0) return null;
            int j = line.IndexOf('"', i + 8);
            if (j < 0) return null;
            int k = line.IndexOf('"', j + 1);
            return k < 0 ? null : line.Substring(j + 1, k - j - 1);
        }

        private static string Positional(string[] a)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].StartsWith("--")) { if (i + 1 < a.Length && a[i] != "--multi") i++; continue; }
                return a[i];
            }
            return null;
        }

        private static string Opt(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        private static int IntOpt(string[] a, string name, int def)
        {
            int v;
            return int.TryParse(Opt(a, name), out v) ? v : def;
        }

        private static string Cut(object o, int n)
        {
            string s = o?.ToString() ?? "";
            return s.Length <= n ? s : s.Substring(0, n - 3) + "...";
        }

        private static string Size(string path)
        {
            double mb = new FileInfo(path).Length / 1e6;
            return mb >= 1000 ? (mb / 1000).ToString("F1") + " GB" : mb.ToString("F0") + " MB";
        }
    }
}
