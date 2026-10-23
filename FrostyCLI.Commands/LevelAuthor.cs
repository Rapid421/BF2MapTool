using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Frosty.Core;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Commands
{
    /// <summary>
    /// `level author &lt;spec.json&gt;`: build entities, shapes and their wiring in a layer from one JSON spec -- the way a
    /// shipped BFII level holds them. A layer keeps no connections of its own: Hoth_01's interior-VE triggers
    /// (AreaProximity.Out -> VisualEnvironmentReferenceObjectData.Visibility, AreaProximity.Geometry -> OBBData) are
    /// objects of the Lighting layer wired by the ROOT LevelData's Property/Link connections with external ends, and the
    /// shapes are exported partition objects outside the layer's Objects list. The spec reproduces exactly that.
    /// <code>
    /// {
    ///   "layer": "&lt;LayerData path&gt;",
    ///   "createLayer": { "parent": "&lt;world partition that references it&gt;" },       optional: new LayerData + reference
    ///   "connectionsIn": "&lt;partition that holds the wiring&gt;",                      default: createLayer.parent
    ///   "seed": "&lt;text&gt;",                                                         instance guids = MD5(seed|id)
    ///   "objects": [ { "id": "ap_int", "type": "AreaProximityEntityData", "world": true,
    ///                  "template": { "ebx": "&lt;asset&gt;", "object": "&lt;selector&gt;" },          defaults copied from a shipped object
    ///                  "flagByte": 96, "identityBits": true,                         Flags = low 24 bits of the guid | byte &lt;&lt; 24
    ///                  "fields": { "ProximityDistance": "10", ... },                 dot paths, as `ebx set`
    ///                  "refs": { "Blueprint": "&lt;asset&gt;" },
    ///                  "points": [[x,y,z], ...] } ],                                 VectorShapeData.Points
    ///   "connections": [ { "kind": "property|link|event", "source": "&lt;id&gt;", "sourceField": "Out",
    ///                      "target": "&lt;id&gt;", "targetField": "Visibility", "flags": 34, "targetType": "Client" } ]
    /// }
    /// </code>
    /// "world": true puts the object in the layer's Objects list; false keeps it a partition object (shapes). Every
    /// object is exported (a connection in another partition can only name an exported object). An endpoint may also be
    /// "&lt;asset&gt;#&lt;exported guid&gt;" for an object that already exists elsewhere.
    /// </summary>
    public static class LevelAuthor
    {
        public static int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1) { Console.Error.WriteLine("Usage: frostycli level author <spec.json>"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("level author: AssetManager not initialized."); return 1; }
            JObject spec;
            try { spec = JObject.Parse(File.ReadAllText(args[0])); }
            catch (Exception ex) { Console.Error.WriteLine("level author: " + args[0] + ": " + ex.Message); return 1; }

            string layerPath = (string)spec["layer"];
            if (string.IsNullOrEmpty(layerPath)) { Console.Error.WriteLine("level author: spec needs \"layer\""); return 1; }
            string seed = (string)spec["seed"] ?? layerPath;
            JObject create = spec["createLayer"] as JObject;
            string wiringPath = (string)spec["connectionsIn"] ?? (string)create?["parent"];

            // 1. the layer (new or existing)
            if (create != null)
            {
                if (App.AssetManager.GetEbxEntry(layerPath) != null) { Console.Error.WriteLine("level author: layer already exists: " + layerPath); return 1; }
                if (EbxWriteCommand.ExecuteCreate(new[] { layerPath, "--type", "LayerData" }, logger, formatter) != 0) return 1;
            }
            EbxAssetEntry layerEntry = App.AssetManager.GetEbxEntry(layerPath);
            if (layerEntry == null) { Console.Error.WriteLine("level author: layer not found: " + layerPath); return 1; }
            EbxAsset layer = App.AssetManager.GetEbx(layerEntry);
            if (create != null)
            {
                object root = layer.RootObject;
                TrySet(root, "Name", layerPath);
                TrySet(root, "Enabled", "true");
                TrySet(root, "TimeDeltaType", "TimeDeltaType_None");
            }
            IList layerObjects = LevelCommand.GetEntityList(layer.RootObject);
            if (layerObjects == null) { Console.Error.WriteLine("level author: " + layerPath + " has no Objects list"); return 1; }

            // 2. objects
            var byId = new Dictionary<string, object>(StringComparer.Ordinal);
            var guidOf = new Dictionary<string, Guid>(StringComparer.Ordinal);
            int created = 0;
            foreach (JObject o in (spec["objects"] as JArray) ?? new JArray())
            {
                string id = (string)o["id"], type = (string)o["type"];
                if (id == null || type == null) { Console.Error.WriteLine("level author: every object needs id and type"); return 1; }
                if (byId.ContainsKey(id)) { Console.Error.WriteLine("level author: duplicate id " + id); return 1; }
                object obj = EbxWriteCommand.CreateObjectByType(type);
                if (obj == null) { Console.Error.WriteLine("level author: unknown type " + type); return 1; }
                if (o["template"] is JObject tpl)
                {
                    object src = FindIn((string)tpl["ebx"], (string)tpl["object"]);
                    if (src == null) { Console.Error.WriteLine("level author: " + id + ": template not found " + tpl); return 1; }
                    if (src.GetType() != obj.GetType()) { Console.Error.WriteLine("level author: " + id + ": template is a " + src.GetType().Name); return 1; }
                    CopyValues(src, obj);
                }
                Guid g = DeterministicGuid(seed, id);
                bool world = o["world"] == null || (bool)o["world"];
                if (world)
                {
                    obj.GetType().GetMethod("SetInstanceGuid").Invoke(obj, new object[] { new AssetClassGuid(g, LevelCommand.NextInternalId(layer)) });
                    layer.AddObject(obj, false);
                    layerObjects.Add(new PointerRef(obj));
                }
                else
                {
                    // referenced only from another partition: a root object, or EbxAsset.Update drops it on save
                    layer.AddRootObject(obj);
                    int iid = ((AssetClassGuid)obj.GetType().GetMethod("GetInstanceGuid").Invoke(obj, null)).InternalId;
                    obj.GetType().GetMethod("SetInstanceGuid").Invoke(obj, new object[] { new AssetClassGuid(g, iid) });
                }
                int flagByte = o["flagByte"] == null ? 0 : (int)o["flagByte"];
                bool identity = o["identityBits"] == null || (bool)o["identityBits"];
                TrySet(obj, "Flags", EntityFlags(g, flagByte, identity).ToString(CultureInfo.InvariantCulture));
                try
                {
                    foreach (var f in (o["fields"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                        PropertyPathWriter.SetValue(obj, f.Name, f.Value.Type == JTokenType.Boolean ? ((bool)f.Value ? "true" : "false") : f.Value.ToString());
                    if (o["points"] is JArray pts)
                    {
                        IList list = PropertyPathWriter.ResolveList(obj, "Points");
                        Type vt = PropertyPathWriter.ListElementType(list);
                        while (list.Count > 0) list.RemoveAt(list.Count - 1);
                        foreach (JToken p in pts)
                        {
                            object v = Activator.CreateInstance(vt);
                            for (int a = 0; a < 3; a++)
                                PropertyPathWriter.SetValue(v, "xyz"[a].ToString(), ((double)p[a]).ToString("R", CultureInfo.InvariantCulture));
                            list.Add(v);
                        }
                    }
                    foreach (var r in (o["refs"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    {
                        if (r.Value.Type == JTokenType.Null) { EbxRefWriter.SetRefByPath(obj, r.Name, Guid.Empty, Guid.Empty); continue; }
                        EbxAssetEntry te = App.AssetManager.GetEbxEntry((string)r.Value);
                        if (te == null) { Console.Error.WriteLine("level author: " + id + "." + r.Name + ": asset not found " + r.Value); return 1; }
                        Guid cls = ExportedGuid(App.AssetManager.GetEbx(te).RootObject);
                        EbxRefWriter.SetRefByPath(obj, r.Name, te.Guid, cls);
                        layer.AddDependency(te.Guid);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("level author: " + id + ": " + ex.Message); return 1; }
                byId[id] = obj; guidOf[id] = g; created++;
            }

            // 3. the reference to a new layer
            EbxAsset wiring = null;
            EbxAssetEntry wiringEntry = null;
            if (wiringPath != null)
            {
                wiringEntry = App.AssetManager.GetEbxEntry(wiringPath);
                if (wiringEntry == null) { Console.Error.WriteLine("level author: not found: " + wiringPath); return 1; }
                wiring = wiringPath.Equals(layerPath, StringComparison.OrdinalIgnoreCase) ? layer : App.AssetManager.GetEbx(wiringEntry);
            }
            if (create != null)
            {
                if (wiring == null || !wiringPath.Equals((string)create["parent"], StringComparison.OrdinalIgnoreCase))
                { Console.Error.WriteLine("level author: createLayer.parent must be the wiring partition"); return 1; }
                IList parentObjects = LevelCommand.GetEntityList(wiring.RootObject);
                object lref = EbxWriteCommand.CreateObjectByType("LayerReferenceObjectData");
                object tplRef = null;
                foreach (object x in parentObjects)
                {
                    object io = LevelCommand.ResolveInternal(x);
                    if (io != null && io.GetType().Name == "LayerReferenceObjectData") { tplRef = io; break; }
                }
                if (tplRef != null) CopyValues(tplRef, lref);
                Guid lg = DeterministicGuid(seed, "layer-reference");
                lref.GetType().GetMethod("SetInstanceGuid").Invoke(lref, new object[] { new AssetClassGuid(LevelCommand.NextInternalId(wiring)) });
                TrySet(lref, "Flags", EntityFlags(lg, 1, true).ToString(CultureInfo.InvariantCulture));
                EbxRefWriter.SetRefByPath(lref, "Blueprint", layerEntry.Guid, ExportedGuid(layer.RootObject));
                wiring.AddObject(lref, false);
                parentObjects.Add(new PointerRef(lref));
                wiring.AddDependency(layerEntry.Guid);
            }

            // 4. connections
            int wired = 0;
            foreach (JObject c in (spec["connections"] as JArray) ?? new JArray())
            {
                if (wiring == null) { Console.Error.WriteLine("level author: connections need connectionsIn"); return 1; }
                string kind = ((string)c["kind"] ?? "property").ToLowerInvariant();
                string listName = kind == "property" ? "PropertyConnections" : kind == "link" ? "LinkConnections" : kind == "event" ? "EventConnections" : null;
                if (listName == null) { Console.Error.WriteLine("level author: unknown connection kind " + kind); return 1; }
                PropertyInfo lp = wiring.RootObject.GetType().GetProperty(listName);
                IList list = lp?.GetValue(wiring.RootObject) as IList;
                if (list == null) { Console.Error.WriteLine("level author: " + wiringPath + " has no " + listName); return 1; }
                Type ct = PropertyPathWriter.ListElementType(list);
                object conn = Activator.CreateInstance(ct);
                ConnectionEndpoint src, dst;
                string err;
                if (!Endpoint((string)c["source"], byId, guidOf, layer, layerEntry, wiring, out src, out err) ||
                    !Endpoint((string)c["target"], byId, guidOf, layer, layerEntry, wiring, out dst, out err))
                { Console.Error.WriteLine("level author: connection " + c.ToString(Newtonsoft.Json.Formatting.None) + ": " + err); return 1; }
                try
                {
                    ConnectionCommand.SetEndpoint(conn, ct, "Source", src, (string)c["sourceField"] ?? "0x00000000");
                    ConnectionCommand.SetEndpoint(conn, ct, "Target", dst, (string)c["targetField"] ?? "0x00000000");
                    if (c["flags"] != null) TrySet(conn, "Flags", ((int)c["flags"]).ToString(CultureInfo.InvariantCulture));
                    if (c["targetType"] != null)
                    {
                        string tt = (string)c["targetType"];
                        TrySet(conn, "TargetType", tt.StartsWith("EventConnectionTargetType_", StringComparison.Ordinal) ? tt : "EventConnectionTargetType_" + tt);
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine("level author: connection: " + ex.Message); return 1; }
                list.Add(conn);
                if (src.IsExternal) wiring.AddDependency(src.FileGuid);
                if (dst.IsExternal) wiring.AddDependency(dst.FileGuid);
                wired++;
            }

            if (!EbxWriteCommand.CallModifyEbx(layerPath, layer, logger)) return 1;
            if (wiring != null && !ReferenceEquals(wiring, layer) && !EbxWriteCommand.CallModifyEbx(wiringPath, wiring, logger)) return 1;
            formatter.WriteResult("OK", "level author", new Dictionary<string, string>
            {
                { "layer", layerPath }, { "created_layer", (create != null).ToString() },
                { "objects", created.ToString(CultureInfo.InvariantCulture) }, { "connections", wired.ToString(CultureInfo.InvariantCulture) },
                { "wiring", wiringPath ?? "" },
            });
            return 0;
        }

        // ---------------------------------------------------------------- pure helpers (unit-tested)

        /// <summary>A stable instance guid for a spec object: MD5 of "seed|id" (re-running a spec gives the same guids).</summary>
        internal static Guid DeterministicGuid(string seed, string id)
        {
            using (var md5 = MD5.Create())
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(seed + "|" + id)));
        }

        /// <summary>Shipped entity Flags: (instanceGuid.Data1 &amp; 0xFFFFFF) | flagByte &lt;&lt; 24 (memory bfii-entity-flags-field;
        /// 2015's AreaProximity 4edfce27 -> 0x60DFCE27). Shipped shapes carry the flag byte alone (OBBData 0x02000000).</summary>
        internal static uint EntityFlags(Guid instance, int flagByte, bool identityBits)
        {
            byte[] b = instance.ToByteArray();
            uint low = identityBits ? (uint)(b[0] | b[1] << 8 | b[2] << 16) : 0u;
            return low | (uint)(flagByte & 0xFF) << 24;
        }

        /// <summary>A local point through a Frostbite LinearTransform (row vectors: p.x*right + p.y*up + p.z*forward + trans).</summary>
        internal static double[] TransformPoint(double[] p, double[] right, double[] up, double[] forward, double[] trans)
        {
            var r = new double[3];
            for (int i = 0; i < 3; i++) r[i] = p[0] * right[i] + p[1] * up[i] + p[2] * forward[i] + trans[i];
            return r;
        }

        // ---------------------------------------------------------------- engine helpers

        static bool Endpoint(string text, Dictionary<string, object> byId, Dictionary<string, Guid> guidOf, EbxAsset layer,
                             EbxAssetEntry layerEntry, EbxAsset wiring, out ConnectionEndpoint ep, out string err)
        {
            ep = default(ConnectionEndpoint); err = null;
            if (string.IsNullOrEmpty(text)) { err = "missing endpoint"; return false; }
            if (byId.TryGetValue(text, out object obj))
            {
                ep = ReferenceEquals(wiring, layer) ? ConnectionEndpoint.Internal(obj) : ConnectionEndpoint.External(layerEntry.Guid, guidOf[text]);
                return true;
            }
            int hash = text.IndexOf('#');
            if (hash > 0 && Guid.TryParse(text.Substring(hash + 1), out Guid cg))
            {
                EbxAssetEntry e = App.AssetManager.GetEbxEntry(text.Substring(0, hash));
                if (e == null) { err = "asset not found " + text.Substring(0, hash); return false; }
                ep = ConnectionEndpoint.External(e.Guid, cg);
                return true;
            }
            err = "unknown endpoint " + text;
            return false;
        }

        internal static object FindIn(string ebx, string selector)
        {
            EbxAssetEntry e = App.AssetManager.GetEbxEntry(ebx);
            if (e == null) return null;
            EbxAsset a = App.AssetManager.GetEbx(e);
            Guid g;
            if (Guid.TryParse(selector, out g)) return a.Objects.FirstOrDefault(o => ExportedGuid(o) == g);
            string type = selector; int n = 0;
            int b = selector.IndexOf('[');
            if (b > 0) { type = selector.Substring(0, b); n = int.Parse(selector.Substring(b + 1).TrimEnd(']'), CultureInfo.InvariantCulture); }
            return a.Objects.Where(o => o != null && o.GetType().Name == type).Skip(n).FirstOrDefault();
        }

        /// <summary>Copy every value member of <paramref name="src"/> onto <paramref name="dst"/> (same type): structs and lists
        /// deep-copied, references to other objects and assets left unset -- a template lends its values, not its links.</summary>
        internal static void CopyValues(object src, object dst)
        {
            foreach (PropertyInfo pi in src.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || !pi.CanWrite || pi.GetIndexParameters().Length > 0) continue;
                Type t = pi.PropertyType;
                if (t.Name == "PointerRef") continue;
                if (typeof(IList).IsAssignableFrom(t) && t.IsGenericType && t.GetGenericArguments()[0].Name == "PointerRef") continue;
                object v;
                try { v = pi.GetValue(src); } catch { continue; }
                try { pi.SetValue(dst, PortCommand.DeepCopyEbxValue(v)); } catch { }
            }
        }

        internal static Guid ExportedGuid(object o)
        {
            try
            {
                object g = o.GetType().GetMethod("GetInstanceGuid")?.Invoke(o, null);
                object e = g?.GetType().GetProperty("ExportedGuid")?.GetValue(g) ?? g?.GetType().GetField("ExportedGuid")?.GetValue(g);
                return e is Guid x ? x : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        static void TrySet(object o, string path, string value)
        {
            try { PropertyPathWriter.SetValue(o, path, value); } catch { }
        }
    }
}
