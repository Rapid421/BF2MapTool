using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Frosty.Core;
using FrostyCLI.Core;
using FrostySdk.Managers;

namespace FrostyCLI.Commands
{
    // logic trace <blueprint> <Type[tag]>.<Port> [--back] [--depth <n>] [--max <n>] [--kinds event,property,link]
    //
    // Follows wiring from one port, offline, across blueprints: what an event or value reaches (or, with
    // --back, what feeds it). Within a blueprint it follows its Event/Property/Link connections; into a
    // prefab reference (LogicPrefabReferenceObject, SpatialPrefab..., anything with a Blueprint) it continues
    // from that blueprint's Interface port of the same name, and back out of an Interface port to the
    // reference that brought it in. External endpoints (GameModes' wiring of GameModes_Flow objects) are
    // followed into their file. What an entity does with an input is its own code, so the trace carries on
    // from every output of the entity it reached - read the chain, not the fan-out, as the claim.
    //
    // Lines use the live feeds' form, so an instance can be handed straight to `game fire` / `game inspect`:
    //   E EventGate[82].Out -> CompareBool[50].In  @UI/InGame/Spawn/Screens/SpawnScreen
    // [82] is the instance's InternalId (Frosty's placeholder GUID ...000082), #xxxxxxxx an exported GUID.
    public class LogicCommand : ICommand
    {
        public string Name => "logic";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length >= 1 && args[0] == "trace") return Trace(args.Skip(1).ToList(), logger);
            if (args.Length >= 1 && args[0] == "lint") return Lint(args.Skip(1).ToList(), logger);
            Console.Error.WriteLine("Usage: frostycli logic trace <blueprint> <Type[tag]>.<Port> [--back] [--depth <n>] [--max <n>] [--kinds event,property,link]" +
                                    " | logic lint <blueprint>... [--scan <path-prefix>] [--max-assets <n>] [--summary] [--duplicates]");
            return 1;
        }

        // ---- the graph ----------------------------------------------------------------------------

        private sealed class Node
        {
            public string Key;           // file guid + "/" + instance key
            public Guid File;
            public object Obj;           // null when the file could not be loaded
            public string Label;         // Type[tag]
            public string Asset;         // the file's name
        }

        private sealed class Wire
        {
            public char Kind;            // E P L
            public string From, To;      // node keys
            public uint FromPort, ToPort;
            public string Asset;         // the blueprint that holds the connection
        }

        private readonly Dictionary<Guid, EbxAssetEntry> loaded = new Dictionary<Guid, EbxAssetEntry>();
        private readonly Dictionary<string, Node> nodes = new Dictionary<string, Node>();
        private readonly List<Wire> wires = new List<Wire>();
        private readonly Dictionary<string, List<Wire>> outOf = new Dictionary<string, List<Wire>>();
        private readonly Dictionary<string, List<Wire>> into = new Dictionary<string, List<Wire>>();
        private readonly Dictionary<Guid, string> interfaceOf = new Dictionary<Guid, string>();   // file -> its Interface node
        private readonly HashSet<char> kinds = new HashSet<char> { 'E', 'P', 'L' };

        private static string InstanceKey(object obj)
        {
            object id;
            try { id = obj.GetType().GetMethod("GetInstanceGuid", Type.EmptyTypes)?.Invoke(obj, null); }
            catch { return null; }
            if (id == null) return null;
            Type t = id.GetType();
            bool exported = t.GetProperty("IsExported")?.GetValue(id) is bool b && b;
            if (exported && t.GetProperty("ExportedGuid")?.GetValue(id) is Guid g) return g.ToString("N");
            if (t.GetProperty("InternalId")?.GetValue(id) is int n) return "id:" + n.ToString("x", CultureInfo.InvariantCulture);
            return id.ToString();
        }

        private static string Label(object obj, string key)
        {
            string type = obj?.GetType().Name ?? "?";
            foreach (string suffix in new[] { "EntityData", "Data" })
                if (type.Length > suffix.Length && type.EndsWith(suffix, StringComparison.Ordinal)) { type = type.Substring(0, type.Length - suffix.Length); break; }
            if (key == null) return type;
            return key.StartsWith("id:", StringComparison.Ordinal) ? type + "[" + key.Substring(3) + "]" : type + "#" + key.Substring(0, Math.Min(8, key.Length));
        }

        private bool Load(Guid file)
        {
            if (loaded.ContainsKey(file)) return loaded[file] != null;
            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(file);
            loaded[file] = entry;
            if (entry == null) return false;
            FrostySdk.IO.EbxAsset asset;
            try { asset = App.AssetManager.GetEbx(entry); }
            catch { loaded[file] = null; return false; }

            foreach (object obj in asset.Objects)
            {
                if (obj == null) continue;
                string key = InstanceKey(obj);
                if (key == null) continue;
                nodes[file.ToString("N") + "/" + key] = new Node { Key = file.ToString("N") + "/" + key, File = file, Obj = obj, Label = Label(obj, key), Asset = entry.Name };
            }
            // the blueprint's Interface (root.Interface, an internal ref)
            object root = asset.RootObject;
            object iface = Deref(Get(root, "Interface"), file, out string ifaceKey);
            if (iface != null && ifaceKey != null) interfaceOf[file] = ifaceKey;

            foreach (object obj in asset.Objects)
            {
                if (obj == null) continue;
                foreach (PropertyInfo pi in obj.GetType().GetProperties())
                {
                    if (!pi.Name.EndsWith("Connections", StringComparison.Ordinal)) continue;
                    char kind = pi.Name.StartsWith("Event", StringComparison.Ordinal) ? 'E' : pi.Name.StartsWith("Property", StringComparison.Ordinal) ? 'P' : pi.Name.StartsWith("Link", StringComparison.Ordinal) ? 'L' : '\0';
                    if (kind == '\0') continue;
                    IList list;
                    try { list = pi.GetValue(obj) as IList; } catch { continue; }
                    if (list == null) continue;
                    foreach (object conn in list)
                    {
                        if (conn == null) continue;
                        Deref(Get(conn, "Source"), file, out string from);
                        Deref(Get(conn, "Target"), file, out string to);
                        if (from == null || to == null) continue;
                        var w = new Wire { Kind = kind, From = from, To = to, FromPort = Port(conn, "Source"), ToPort = Port(conn, "Target"), Asset = entry.Name };
                        wires.Add(w);
                        Add(outOf, from, w);
                        Add(into, to, w);
                    }
                }
            }
            return true;
        }

        private static void Add(Dictionary<string, List<Wire>> map, string key, Wire w)
        {
            if (!map.TryGetValue(key, out var l)) map[key] = l = new List<Wire>();
            l.Add(w);
        }

        // A PointerRef -> the node key (file/instance) it names; the object when it is internal.
        private static object Deref(object pr, Guid file, out string key)
        {
            key = null;
            if (pr == null) return null;
            string type = Get(pr, "Type")?.ToString();
            if (type == "Internal")
            {
                object inner = Get(pr, "Internal");
                string k = inner == null ? null : InstanceKey(inner);
                if (k != null) key = file.ToString("N") + "/" + k;
                return inner;
            }
            if (type == "External")
            {
                object ext = Get(pr, "External");
                if (ext != null && Get(ext, "FileGuid") is Guid fg && Get(ext, "ClassGuid") is Guid cg)
                    key = fg.ToString("N") + "/" + cg.ToString("N");
            }
            return null;
        }

        private static uint Port(object conn, string side)
        {
            object spec = Get(conn, side + "Event");
            if (spec != null && Get(spec, "Id") is int id) return unchecked((uint)id);
            object field = Get(conn, side + "FieldId");
            if (field is int f) return unchecked((uint)f);
            if (field is uint u) return u;
            return 0;
        }

        private static object Get(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            PropertyInfo pi = t.GetProperty(name);
            if (pi != null) { try { return pi.GetValue(obj); } catch { return null; } }
            FieldInfo fi = t.GetField(name);
            return fi?.GetValue(obj);
        }

        private Node NodeFor(string key)
        {
            if (nodes.TryGetValue(key, out Node n)) return n;
            int slash = key.IndexOf('/');
            if (slash > 0 && Guid.TryParseExact(key.Substring(0, slash), "N", out Guid file) && !loaded.ContainsKey(file))
            {
                Load(file);
                if (nodes.TryGetValue(key, out n)) return n;
            }
            n = new Node { Key = key, Label = "?#" + key.Substring(slash + 1, Math.Min(8, key.Length - slash - 1)), Asset = "?" };
            nodes[key] = n;
            return n;
        }

        // The blueprint a reference object points at (its Blueprint, an external ref to the primary instance).
        private static Guid? ReferencedBlueprint(Node n)
        {
            object bp = Get(n.Obj, "Blueprint");
            if (bp == null || Get(bp, "Type")?.ToString() != "External") return null;
            object ext = Get(bp, "External");
            return Get(ext, "FileGuid") is Guid fg ? fg : (Guid?)null;
        }

        // A port id as a name: the CLI's vocabulary first (junk collisions like "O(" dropped), then the
        // live feeds' tables (tools/gamelink/field_hashes.csv, event_hashes_extra.csv), else hex.
        private static Dictionary<uint, string> feedNames;

        internal static string PortText(uint id)
        {
            var clean = PortNames.Resolve(unchecked((int)id)).Where(IsIdentifier).Distinct().ToList();
            if (clean.Count == 1) return clean[0];
            if (clean.Count > 1) return string.Join("|", clean);
            if (feedNames == null) feedNames = LoadFeedNames();
            return feedNames.TryGetValue(id, out string name) ? name : "0x" + id.ToString("x8", CultureInfo.InvariantCulture);
        }

        internal static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s) || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (char c in s) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }

        private static Dictionary<uint, string> LoadFeedNames()
        {
            var map = new Dictionary<uint, string>();
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int up = 0; up < 6 && dir != null; up++, dir = System.IO.Path.GetDirectoryName(dir.TrimEnd('\\', '/')))
            {
                string gl = System.IO.Path.Combine(dir, "tools", "gamelink");
                if (!System.IO.Directory.Exists(gl)) continue;
                foreach (string csv in new[] { "field_hashes.csv", "event_hashes_extra.csv" })
                {
                    string path = System.IO.Path.Combine(gl, csv);
                    if (!System.IO.File.Exists(path)) continue;
                    foreach (string line in System.IO.File.ReadLines(path).Skip(1))
                    {
                        string[] cols = line.Split(',');
                        if (cols.Length < 3 || cols[2].StartsWith("~", StringComparison.Ordinal)) continue;   // ~ = guesses
                        if (uint.TryParse(cols[0].Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h) && !map.ContainsKey(h))
                            map[h] = cols[2];
                    }
                }
                break;
            }
            return map;
        }


        // ---- lint -----------------------------------------------------------------------------------
        // logic lint <blueprint>... [--scan <path-prefix>] [--max-assets <n>] [--summary] [--duplicates]
        // Wiring mistakes that can be read from the files, before a launch:
        //   dangling      an endpoint that is null, not in its file, or in a file/export that doesn't exist
        //   no-port       a wire into or out of a prefab reference on a port its blueprint's Interface lacks
        //   duplicate     the same wire twice (only with --duplicates: shipped data has ~100 of these)
        //   subworld-pc   a SubWorldReferenceObject with IsWin32SubLevel false: it never streams on PC
        // Checks are calibrated on shipped data (--scan over vanilla): a check that fires on the shipped
        // game is a wrong check, not a finding. 2026-10-08: 0 issues in ~12,300 shipped blueprints and levels
        // (Levels/MP, Gameplay, S2, Addons, Objects, Characters, UI) with the default checks.
        private readonly Dictionary<Guid, HashSet<uint>> interfacePorts = new Dictionary<Guid, HashSet<uint>>();

        private HashSet<uint> InterfacePorts(Guid blueprint)
        {
            if (interfacePorts.TryGetValue(blueprint, out var known)) return known;
            HashSet<uint> ports = null;
            if (Load(blueprint) && interfaceOf.TryGetValue(blueprint, out string key) && nodes.TryGetValue(key, out Node iface) && iface.Obj != null)
            {
                ports = new HashSet<uint>();
                foreach (PropertyInfo pi in iface.Obj.GetType().GetProperties())
                {
                    IList list;
                    try { list = pi.GetValue(iface.Obj) as IList; } catch { continue; }
                    if (list == null) continue;
                    foreach (object item in list)
                    {
                        if (item == null) continue;
                        object name = Get(item, "Name");
                        if (name != null && name.ToString().Length > 0)
                        {
                            string text = name.ToString();
                            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                                uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint raw))
                                ports.Add(raw);
                            else
                                ports.Add(unchecked((uint)FrostySdk.Utils.HashString(text)));
                        }
                        object id = Get(item, "Id");
                        if (id is int i) ports.Add(unchecked((uint)i));
                        object nameHash = Get(item, "NameHash");
                        if (nameHash is int nh) ports.Add(unchecked((uint)nh));
                        else if (nameHash is uint unh) ports.Add(unh);
                    }
                }
                // an Interface with no ports read at all is a shape this check doesn't know - stay quiet
                if (ports.Count == 0) ports = null;
            }
            interfacePorts[blueprint] = ports;
            return ports;
        }

        private bool duplicates;                    // shipped data has ~100 harmless duplicate wires: opt-in

        private int Lint(List<string> args, ExportLogger logger)
        {
            bool summary = args.Remove("--summary");
            duplicates = args.Remove("--duplicates");
            string scan = null;
            int maxAssets = 2000;
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == "--scan") { scan = args[i + 1]; args.RemoveRange(i, 2); i--; }
                else if (args[i] == "--max-assets" && int.TryParse(args[i + 1], out int m)) { maxAssets = m; args.RemoveRange(i, 2); i--; }
            }
            if (App.AssetManager == null) { Console.Error.WriteLine("logic lint: AssetManager not initialized."); return 1; }
            PortNames.Load(null);

            var targets = new List<EbxAssetEntry>();
            foreach (string a in args)
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(a);
                if (e == null) { Console.Error.WriteLine("logic lint: asset not found: " + a); return 1; }
                targets.Add(e);
            }
            if (scan != null)
                foreach (EbxAssetEntry e in App.AssetManager.EnumerateEbx())
                {
                    if (targets.Count >= maxAssets) break;
                    if (e.Name.StartsWith(scan, StringComparison.OrdinalIgnoreCase) &&
                        (e.Type.EndsWith("Blueprint", StringComparison.Ordinal) || e.Type == "LevelData" || e.Type == "SubWorldData"))
                        targets.Add(e);
                }
            if (targets.Count == 0 && scan != null)
            {
                var near = App.AssetManager.EnumerateEbx().Where(e => e.Name.IndexOf(scan, StringComparison.OrdinalIgnoreCase) >= 0).Take(8).ToList();
                Console.Error.WriteLine("logic lint: no blueprint, LevelData or SubWorldData starts with '" + scan + "'" +
                    (near.Count == 0 ? " (no asset contains it either)" : "; assets containing it: " + string.Join(", ", near.Select(e => e.Name + " [" + e.Type + "]"))));
                return 1;
            }
            if (targets.Count == 0) { Console.Error.WriteLine("logic lint: give blueprints, or --scan <path-prefix>"); return 1; }

            var counts = new Dictionary<string, int>();
            int issues = 0, assets = 0;
            foreach (EbxAssetEntry entry in targets)
            {
                if (!Load(entry.Guid)) continue;
                assets++;
                foreach (string issue in LintOne(entry))
                {
                    string check = issue.Substring(0, issue.IndexOf(' '));
                    counts[check] = counts.TryGetValue(check, out int c) ? c + 1 : 1;
                    issues++;
                    if (!summary) Console.Out.WriteLine(entry.Name + ": " + issue);
                }
            }
            Console.Out.WriteLine(issues + " issue(s) in " + assets + " asset(s)" +
                (counts.Count > 0 ? "  (" + string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value)) + ")" : ""));
            return issues > 0 ? 3 : 0;
        }

        private IEnumerable<string> LintOne(EbxAssetEntry entry)
        {
            FrostySdk.IO.EbxAsset asset = null;
            try { asset = App.AssetManager.GetEbx(entry); } catch { }
            if (asset == null) yield break;
            Guid file = entry.Guid;
            var inFile = new HashSet<object>(asset.Objects.Where(o => o != null));
            var seenWires = new HashSet<string>();

            foreach (object obj in asset.Objects)
            {
                if (obj == null) continue;
                string typeName = obj.GetType().Name;
                if (typeName == "SubWorldReferenceObjectData" && Get(obj, "IsWin32SubLevel") is bool pc && !pc)
                    yield return "subworld-pc " + Label(obj, InstanceKey(obj)) + " has IsWin32SubLevel false - it never streams on PC";

                foreach (PropertyInfo pi in obj.GetType().GetProperties())
                {
                    if (!pi.Name.EndsWith("Connections", StringComparison.Ordinal)) continue;
                    char kind = pi.Name.StartsWith("Event", StringComparison.Ordinal) ? 'E' : pi.Name.StartsWith("Property", StringComparison.Ordinal) ? 'P' : pi.Name.StartsWith("Link", StringComparison.Ordinal) ? 'L' : '\0';
                    if (kind == '\0') continue;
                    IList list;
                    try { list = pi.GetValue(obj) as IList; } catch { continue; }
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        object conn = list[i];
                        if (conn == null) continue;
                        uint fromPort = Port(conn, "Source"), toPort = Port(conn, "Target");
                        string fromWhy = EndpointProblem(Get(conn, "Source"), file, inFile, out string fromKey);
                        string toWhy = EndpointProblem(Get(conn, "Target"), file, inFile, out string toKey);
                        if (kind == 'L')                // vanilla ships link wires with a null end; harmless
                        {
                            if (fromWhy == "is null") fromWhy = null;
                            if (toWhy == "is null") toWhy = null;
                            if (fromKey == null || toKey == null) continue;
                        }
                        string where = kind + " " + pi.Name + "[" + i + "] ";
                        if (fromWhy != null || toWhy != null)
                        {
                            yield return "dangling " + where + (fromWhy != null ? "source " + fromWhy : "") + (fromWhy != null && toWhy != null ? ", " : "") +
                                         (toWhy != null ? "target " + toWhy : "") + "  (" + Describe(fromKey, fromPort) + " -> " + Describe(toKey, toPort) + ")";
                            continue;
                        }
                        string wire = Describe(fromKey, fromPort) + " -> " + Describe(toKey, toPort);
                        if (!seenWires.Add(kind + wire) && duplicates) yield return "duplicate " + where + wire;

                        foreach (bool targetSide in new[] { true, false })
                        {
                            Node end = NodeFor(targetSide ? toKey : fromKey);
                            uint port = targetSide ? toPort : fromPort;
                            if (end.Obj == null || port == 0) continue;
                            Guid? bp = ReferencedBlueprint(end);
                            if (!bp.HasValue) continue;
                            // Only a logic prefab's Interface is its whole API: a reference to a vehicle, character
                            // or object blueprint also forwards its primary entity's own ports (Spawn, Destroy,
                            // OnDestroyed, TakeControl... 80+ seen on shipped data), which the files don't list.
                            if (App.AssetManager.GetEbxEntry(bp.Value)?.Type != "LogicPrefabBlueprint") continue;
                            if (ReferencePorts.Contains(port)) continue;
                            HashSet<uint> ports = InterfacePorts(bp.Value);
                            if (ports != null && !ports.Contains(port) && !OwnFields(end.Obj).Contains(port))
                                yield return "no-port " + where + wire + "  - " + PortText(port) + " is not on the Interface of " +
                                             (App.AssetManager.GetEbxEntry(bp.Value)?.Name ?? bp.Value.ToString());
                        }
                    }
                }
            }
        }

        // Ports every prefab reference has, whatever it references (calibrated on shipped levels).
        private static readonly HashSet<uint> ReferencePorts = new HashSet<uint>(
            new[] { "StreamIn", "StreamOut", "OnStreamedIn", "OnStreamedOut", "Visible", "Visibility", "Enabled", "Space",
                    "Transform", "BlueprintTransform", "AnimatePhysics", "ShaderParameters", "EnableUpdates", "Preload" }
            .Select(n => unchecked((uint)FrostySdk.Utils.HashString(n))));

        private static readonly Dictionary<Type, HashSet<uint>> ownFields = new Dictionary<Type, HashSet<uint>>();

        // The hashes of an object's own reflected property names (a reference object's Space, Enabled...).
        private static HashSet<uint> OwnFields(object obj)
        {
            Type t = obj.GetType();
            if (ownFields.TryGetValue(t, out var set)) return set;
            set = new HashSet<uint>();
            for (Type at = t; at != null && at != typeof(object); at = at.BaseType)
                foreach (PropertyInfo pi in at.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    set.Add(unchecked((uint)FrostySdk.Utils.HashString(pi.Name)));
            ownFields[t] = set;
            return set;
        }

        private string Describe(string key, uint port) => (key == null ? "<null>" : NodeFor(key).Label) + "." + PortText(port);

        // Null when the endpoint resolves; else why not.
        private string EndpointProblem(object pr, Guid file, HashSet<object> inFile, out string key)
        {
            key = null;
            if (pr == null) return "is missing";
            string type = Get(pr, "Type")?.ToString();
            if (type == "Null") return "is null";
            object inner = Deref(pr, file, out key);
            if (type == "Internal")
                return inner == null ? "is an empty internal ref" : inFile.Contains(inner) ? null : "is an object that isn't in this file";
            if (type == "External")
            {
                if (key == null) return "is an unreadable external ref";
                Guid other = Guid.ParseExact(key.Substring(0, key.IndexOf('/')), "N");
                if (App.AssetManager.GetEbxEntry(other) == null) return "is in a file that doesn't exist (" + other + ")";
                if (!Load(other)) return "is in a file that won't load";
                return nodes.ContainsKey(key) ? null : "is an exported object missing from " + App.AssetManager.GetEbxEntry(other).Name;
            }
            return null;
        }

        // ---- the walk -------------------------------------------------------------------------------

        private int Trace(List<string> args, ExportLogger logger)
        {
            bool back = args.Remove("--back");
            int depth = 6, max = 300;
            for (int i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == "--depth" && int.TryParse(args[i + 1], out int d)) { depth = d; args.RemoveRange(i, 2); i--; }
                else if (args[i] == "--max" && int.TryParse(args[i + 1], out int m)) { max = m; args.RemoveRange(i, 2); i--; }
                else if (args[i] == "--kinds")
                {
                    kinds.Clear();
                    foreach (string k in args[i + 1].Split(','))
                        if (k.Length > 0) kinds.Add(char.ToUpperInvariant(k[0]));
                    args.RemoveRange(i, 2); i--;
                }
            }
            if (args.Count != 2) { Console.Error.WriteLine("logic trace: usage: logic trace <blueprint> <Type[tag]>.<Port> [--back] [--depth <n>] [--max <n>] [--kinds event,property,link]"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("logic trace: AssetManager not initialized."); return 1; }
            PortNames.Load(null);

            EbxAssetEntry entry = App.AssetManager.GetEbxEntry(args[0]);
            if (entry == null) { Console.Error.WriteLine("logic trace: asset not found: " + args[0]); return 1; }
            Load(entry.Guid);

            string spec = args[1];
            int dot = spec.LastIndexOf('.');
            if (dot <= 0) { Console.Error.WriteLine("logic trace: give <Type[tag]>.<Port>, e.g. EventGate[82].Out"); return 1; }
            string label = spec.Substring(0, dot);
            ConnectionCommand.TryResolveFieldId(spec.Substring(dot + 1), out uint port);
            string file = entry.Guid.ToString("N") + "/";
            List<Node> starts = nodes.Values.Where(n => n.Key.StartsWith(file, StringComparison.Ordinal) && Matches(n.Label, label)).ToList();
            if (starts.Count == 0) { Console.Error.WriteLine("logic trace: no " + label + " in " + entry.Name + " (tags as in the feed: Type[82] or Type#1a2b3c4d)"); return 1; }
            if (starts.Count > 1) { Console.Error.WriteLine("logic trace: " + label + " matches " + starts.Count + " instances - give its tag"); return 1; }

            Console.Out.WriteLine((back ? "what feeds " : "what ") + starts[0].Label + "." + PortText(port) + (back ? "" : " reaches") + "  @" + entry.Name);
            var seen = new HashSet<string>();
            int printed = 0;
            Walk(starts[0].Key, port, true, back, 1, depth, new List<string>(), seen, ref printed, max);
            if (printed == 0) Console.Out.WriteLine("  (nothing " + (back ? "is wired into" : "is wired from") + " that port)");
            if (printed >= max) Console.Out.WriteLine("  ... stopped at --max " + max);
            return 0;
        }

        private static bool Matches(string nodeLabel, string wanted)
        {
            if (string.Equals(nodeLabel, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            int at = wanted.IndexOfAny(new[] { '[', '#' });
            if (at < 0) return nodeLabel.StartsWith(wanted + "[", StringComparison.OrdinalIgnoreCase) || nodeLabel.StartsWith(wanted + "#", StringComparison.OrdinalIgnoreCase);
            return wanted[at] == '#' && nodeLabel.StartsWith(wanted, StringComparison.OrdinalIgnoreCase);   // a guid prefix
        }

        // From (key, port): forward = the wires out of that port; back = the wires into it. Each reached
        // entity continues from all its outputs (forward) / inputs (back), except across an interface,
        // where the port name carries over. `stack` holds the reference objects entered, to come back out.
        private void Walk(string key, uint port, bool exactPort, bool back, int level, int depth, List<string> stack,
                          HashSet<string> seen, ref int printed, int max)
        {
            if (level > depth || printed >= max) return;
            Node here = NodeFor(key);
            List<Wire> list;
            if (!(back ? into : outOf).TryGetValue(key, out list)) list = new List<Wire>();
            foreach (Wire w in list)
            {
                if (printed >= max) return;
                if (!kinds.Contains(w.Kind)) continue;
                if (exactPort && (back ? w.ToPort : w.FromPort) != port) continue;
                Node from = NodeFor(w.From), to = NodeFor(w.To);
                string line = new string(' ', level * 2) + w.Kind + " " + from.Label + "." + PortText(w.FromPort) + " -> " + to.Label + "." + PortText(w.ToPort) + "  @" + w.Asset;
                string visit = w.From + ":" + w.FromPort + ">" + w.To + ":" + w.ToPort + "|" + string.Join(",", stack);
                if (!seen.Add(visit)) { Console.Out.WriteLine(line + "   (seen)"); printed++; continue; }
                Console.Out.WriteLine(line);
                printed++;

                Node next = back ? from : to;
                uint nextPort = back ? w.FromPort : w.ToPort;
                Continue(next, nextPort, back, level, depth, stack, seen, ref printed, max);
            }
        }

        private void Continue(Node next, uint nextPort, bool back, int level, int depth, List<string> stack,
                              HashSet<string> seen, ref int printed, int max)
        {
            string pad = new string(' ', level * 2 + 2);
            // Into a prefab reference: the same port on its blueprint's Interface.
            Guid? inner = next.Obj != null ? ReferencedBlueprint(next) : null;
            if (inner.HasValue && Load(inner.Value) && interfaceOf.TryGetValue(inner.Value, out string iface))
            {
                if (level + 1 > depth) { Console.Out.WriteLine(pad + "... into " + NodeFor(iface).Asset + " (--depth)"); printed++; return; }
                Console.Out.WriteLine(pad + "into " + NodeFor(iface).Asset + " via Interface." + PortText(nextPort));
                printed++;
                var deeper = new List<string>(stack) { next.Key };
                Walk(iface, nextPort, true, back, level + 1, depth, deeper, seen, ref printed, max);
                return;
            }
            // Out of an Interface: back to the reference that brought us in, same port.
            if (interfaceOf.TryGetValue(next.File, out string ownIface) && ownIface == next.Key)
            {
                if (stack.Count > 0)
                {
                    string caller = stack[stack.Count - 1];
                    Console.Out.WriteLine(pad + "out to " + NodeFor(caller).Label + "." + PortText(nextPort) + "  @" + NodeFor(caller).Asset);
                    printed++;
                    Walk(caller, nextPort, true, back, level, depth, stack.Take(stack.Count - 1).ToList(), seen, ref printed, max);
                }
                else
                {
                    Console.Out.WriteLine(pad + "leaves " + next.Asset + " through its Interface." + PortText(nextPort) + " (to whatever places this blueprint)");
                    printed++;
                }
                return;
            }
            // An entity: its own code decides; carry on from all its outputs (inputs when going back).
            Walk(next.Key, 0, false, back, level + 1, depth, stack, seen, ref printed, max);
        }
    }
}
