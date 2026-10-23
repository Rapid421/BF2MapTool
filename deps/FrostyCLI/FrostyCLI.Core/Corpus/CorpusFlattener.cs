using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core.Corpus
{
    // Flattens a loaded EBX asset into the corpus's `Type.Field -> value` form, the shape both
    // corpus indexes use:
    //
    //   asset-index.jsonl    what each mod ships        (parsed from the .fbmod payloads)
    //   vanilla-index.jsonl  what the game ships        (this class, reading the install)
    //
    // The vanilla index was first produced through FrostyControlPlane (EbxSerializer then
    // dump_vanilla.py's flatten_cp). This is those two collapsed into one pass, and it keeps
    // their exact semantics, because `corpus diff` compares the two indexes key for key and
    // any spelling difference reads as a change a mod never made:
    //
    //   - enums as their name, CString/Guid/ResourceRef/FileRef/Sha1 via ToString()
    //   - PointerRef as "ext:<fileGuid>", "int:<internalId>", or null
    //   - a *FieldId int also emitted unsigned as <name>_u32
    //   - list items: only the first 16, only external refs (as Field[]) and structs
    //   - nested structs recurse under their own type name, at most 6 deep
    //   - strings longer than 120 characters dropped; at most 600 keys per asset
    //   - a key seen twice becomes a list of its distinct values
    public static class CorpusFlattener
    {
        public const int ScalarCap = 600;
        public const int ObjectLimit = 2000;
        private const int SerializerDepth = 12;   // what dump_vanilla.py asked the editor for
        private const int FlattenDepth = 6;

        private static readonly Dictionary<Type, PropertyInfo[]> Props = new Dictionary<Type, PropertyInfo[]>();

        // The whole corpus record for one asset: {asset, objectCount, returned, types, scalars}.
        public static JObject Record(string assetName, IEnumerable<object> objects)
        {
            var scalars = new JObject();
            var types = new SortedSet<string>(StringComparer.Ordinal);
            int total = 0, returned = 0;
            foreach (object o in objects)
            {
                total++;
                if (o == null || returned >= ObjectLimit) continue;
                returned++;
                string t = o.GetType().Name;
                types.Add(t);
                Flatten(o, t, scalars, 0, 0);
            }
            return new JObject
            {
                ["asset"] = assetName,
                ["objectCount"] = total,
                ["returned"] = returned,
                ["types"] = new JArray(types),
                ["scalars"] = scalars,
            };
        }

        // `obj` is an object or struct whose members sit at serializer depth sdepth + 1.
        private static void Flatten(object obj, string tname, JObject outp, int depth, int sdepth)
        {
            if (depth > FlattenDepth || outp.Count >= ScalarCap) return;

            foreach (PropertyInfo p in PropsOf(obj.GetType()))
            {
                if (p.Name == "__Id" || p.Name == "__InstanceGuid") continue;
                object v;
                try { v = p.GetValue(obj, null); }
                catch { continue; }                     // the editor emits {$error}: nothing to flatten

                string key = tname + "." + p.Name;
                Member(v, key, tname, outp, depth, sdepth + 1);

                if (v is int iv && p.Name.EndsWith("FieldId", StringComparison.Ordinal) && outp.Count < ScalarCap)
                    Put(outp, key + "_u32", new JValue(unchecked((uint)iv)));
            }
        }

        // One member value at serializer depth `sdepth`.
        private static void Member(object v, string key, string tname, JObject outp, int depth, int sdepth)
        {
            if (v == null) { Scalar(outp, key, JValue.CreateNull()); return; }

            JValue simple = Simple(v);
            if (simple != null) { Scalar(outp, key, simple); return; }

            Type t = v.GetType();
            if (t.Name == "PointerRef") { Put(outp, key, RefValue(v)); return; }

            if (v is IList list)
            {
                // The editor leaves a list's items out once the list itself sits at max depth.
                if (sdepth >= SerializerDepth) return;
                int n = Math.Min(list.Count, 16);
                for (int i = 0; i < n; i++)
                {
                    object item = list[i];
                    if (item == null) continue;
                    if (item.GetType().Name == "PointerRef")
                    {
                        if (RefKind(item) == "External") Put(outp, key + "[]", RefValue(item));
                    }
                    else if (IsStruct(item))
                    {
                        if (sdepth + 1 < SerializerDepth)
                            Flatten(item, item.GetType().Name, outp, depth + 1, sdepth + 1);
                    }
                }
                return;
            }

            // A struct (or an inline object): recurse under its own type name, unless the
            // editor would have truncated it here.
            if (sdepth < SerializerDepth) Flatten(v, t.Name, outp, depth + 1, sdepth);
        }

        private static bool IsStruct(object v)
        {
            return Simple(v) == null && !(v is IList);
        }

        private static JValue Simple(object v)
        {
            Type t = v.GetType();
            if (t.IsEnum) return new JValue(v.ToString());
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean: case TypeCode.Byte: case TypeCode.SByte:
                case TypeCode.Int16: case TypeCode.UInt16: case TypeCode.Int32: case TypeCode.UInt32:
                case TypeCode.Int64: case TypeCode.UInt64: case TypeCode.Single: case TypeCode.Double:
                case TypeCode.String:
                    return new JValue(v);
            }
            if (v is Guid g) return new JValue(g.ToString());
            switch (t.Name)
            {
                case "CString": case "ResourceRef": case "Sha1": case "FileRef": case "AssetClassGuid":
                    return new JValue(v.ToString());
            }
            return null;
        }

        private static void Scalar(JObject outp, string key, JValue v)
        {
            if (v.Type == JTokenType.String && ((string)v).Length > 120) return;
            if (outp.Count < ScalarCap) Put(outp, key, v);
        }

        private static string RefKind(object pr)
        {
            object kind = pr.GetType().GetProperty("Type")?.GetValue(pr, null);
            return kind?.ToString() ?? "Unknown";
        }

        private static JValue RefValue(object pr)
        {
            Type t = pr.GetType();
            string kind = RefKind(pr);
            if (kind == "External")
            {
                object ext = t.GetProperty("External")?.GetValue(pr, null);
                object fg = ext?.GetType().GetField("FileGuid")?.GetValue(ext);
                string s = fg is Guid g && g != Guid.Empty ? g.ToString() : "None";
                return new JValue("ext:" + s);
            }
            if (kind == "Internal")
            {
                object inner = t.GetProperty("Internal")?.GetValue(pr, null);
                int? id = InternalId(inner);
                return new JValue("int:" + (id.HasValue ? id.Value.ToString() : "None"));
            }
            return JValue.CreateNull();
        }

        private static int? InternalId(object obj)
        {
            if (obj == null) return null;
            try
            {
                object acg = obj.GetType().GetMethod("GetInstanceGuid", Type.EmptyTypes)?.Invoke(obj, null);
                return acg?.GetType().GetProperty("InternalId")?.GetValue(acg, null) as int?;
            }
            catch { return null; }
        }

        // dump_vanilla.py's put(): first value wins, a different second value turns the key
        // into a list, and a list collects distinct values.
        private static void Put(JObject outp, string key, JToken value)
        {
            JToken cur = outp[key];
            if (cur == null && !outp.ContainsKey(key)) { outp[key] = value; return; }
            if (cur is JArray arr)
            {
                foreach (JToken x in arr) if (PyEquals(x, value)) return;
                arr.Add(value);
            }
            else if (!PyEquals(cur, value))
            {
                outp[key] = new JArray(cur, value);
            }
        }

        // Python equality on JSON values: numbers compare by value and True == 1.
        public static bool PyEquals(JToken a, JToken b)
        {
            if (a == null || b == null) return a == b;
            if (IsNum(a) && IsNum(b)) return Num(a) == Num(b);
            return JToken.DeepEquals(a, b);
        }

        private static bool IsNum(JToken t)
        {
            return t.Type == JTokenType.Integer || t.Type == JTokenType.Float || t.Type == JTokenType.Boolean;
        }

        private static double Num(JToken t)
        {
            if (t.Type == JTokenType.Boolean) return (bool)t ? 1 : 0;
            return (double)t;
        }

        private static PropertyInfo[] PropsOf(Type t)
        {
            PropertyInfo[] ps;
            if (!Props.TryGetValue(t, out ps))
            {
                var list = new List<PropertyInfo>();
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.GetIndexParameters().Length == 0) list.Add(p);
                ps = list.ToArray();
                Props[t] = ps;
            }
            return ps;
        }
    }
}
