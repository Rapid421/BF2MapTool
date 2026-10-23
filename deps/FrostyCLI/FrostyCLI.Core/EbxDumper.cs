using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Frosty.Core;
using FrostySdk.IO;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core
{
    public static class EbxDumper
    {
        public static JObject Dump(EbxAsset asset, bool resolve = true, bool deep = false)
        {
            s_visited = new HashSet<int>();
            var root = (JObject)DumpValue(asset.RootObject, new HashSet<int>(), resolve, deep);
            if (!deep) { s_visited = null; return root; }
            var seen = s_visited;

            // Exported instances that nothing inside the partition points at (a LayerData whose
            // spawn points are only reached by another asset's LinkConnections, for instance) never
            // show up under the root. List them separately so --deep really is the whole partition.
            var extra = new JArray();
            foreach (object obj in asset.Objects)
            {
                if (obj == null || ReferenceEquals(obj, asset.RootObject)) continue;
                if (seen.Contains(RuntimeHelpers.GetHashCode(obj))) continue;
                extra.Add(DumpValue(obj, seen, resolve, deep));
            }
            if (extra.Count > 0) root["__PartitionObjects"] = extra;
            s_visited = null;
            return root;
        }

        // Every reference-type object the current Dump() has serialised (pathIds is only a
        // cycle guard along the current path, so it cannot answer "was this already written?").
        [ThreadStatic] private static HashSet<int> s_visited;

        internal static JObject DumpRootObject(object obj, bool resolve, bool deep = false)
        {
            return (JObject)DumpValue(obj, new HashSet<int>(), resolve, deep);
        }

        private static JToken DumpValue(object val, HashSet<int> pathIds, bool resolve, bool deep = false)
        {
            if (val == null) return JValue.CreateNull();

            Type t = val.GetType();

            if (t == typeof(bool))   return new JValue((bool)val);
            if (t == typeof(float))  return new JValue((float)val);
            if (t == typeof(double)) return new JValue((double)val);
            if (t == typeof(string)) return new JValue((string)val);
            if (t == typeof(int))    return new JValue((int)val);
            if (t == typeof(uint))   return new JValue((uint)val);
            if (t == typeof(long))   return new JValue((long)val);
            if (t == typeof(short))  return new JValue((short)val);
            if (t == typeof(byte))   return new JValue((byte)val);
            // ushort/ulong/sbyte were missing, so they fell through to DumpObject and rendered as
            // {"__type":"UInt16"} with no value at all -- a real number displayed as absent. That is
            // indistinguishable from a failed write when you are verifying an edit by dumping it
            // (GameModeConfig.SkillUEDID is a UInt16, and every shipped entry read as empty).
            if (t == typeof(ushort)) return new JValue((ushort)val);
            if (t == typeof(ulong))  return new JValue((ulong)val);
            if (t == typeof(sbyte))  return new JValue((sbyte)val);
            if (t == typeof(char))   return new JValue(val.ToString());
            if (t == typeof(decimal))return new JValue((decimal)val);
            if (t == typeof(Guid))   return new JValue(((Guid)val).ToString("D"));
            if (t.IsEnum)            return new JValue(val.ToString());

            string tname = t.Name;
            if (tname == "CString")      return DumpCString(val, t);
            if (tname == "Sha1")         return DumpSha1(val, t);
            if (tname == "TypeRef")      return DumpTypeRef(val, t);
            if (tname == "FileRef")      return DumpFileRef(val, t);
            if (tname == "PointerRef")   return DumpPointerRef(val, t, pathIds, resolve, deep);
            if (tname == "ResourceRef")  return DumpResourceRef(val, t, resolve);

            if (val is IList list)
            {
                var arr = new JArray();
                foreach (object item in list)
                    arr.Add(DumpValue(item, pathIds, resolve, deep));
                return arr;
            }

            return DumpObject(val, t, pathIds, resolve, deep);
        }

        private static JObject DumpObject(object obj, Type t, HashSet<int> pathIds, bool resolve, bool deep = false)
        {
            var jo = new JObject();
            jo["__type"] = t.Name;

            bool isRef = !t.IsValueType;
            int id = 0;
            if (isRef)
            {
                id = RuntimeHelpers.GetHashCode(obj);
                if (!pathIds.Add(id))
                    return new JObject { ["__circular"] = true, ["__type"] = t.Name };
                if (s_visited != null) s_visited.Add(id);
            }

            if (isRef)
            {
                Guid? ig = TryGetInstanceGuid(obj, t);
                if (ig.HasValue) jo["__guid"] = ig.Value.ToString("D");
            }

            foreach (PropertyInfo pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                object propVal;
                try { propVal = pi.GetValue(obj); }
                catch { continue; }
                jo[pi.Name] = DumpValue(propVal, pathIds, resolve, deep);
            }

            if (isRef) pathIds.Remove(id);
            return jo;
        }

        // Reads a Guid member by name as either a field OR a property (EbxImportReference exposes
        // FileGuid/ClassGuid as fields).
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

        private static Guid? TryGetInstanceGuid(object obj, Type t)
        {
            MethodInfo m = t.GetMethod("GetInstanceGuid", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) return null;
            try
            {
                object ig = m.Invoke(obj, null);
                if (ig == null) return null;
                if (ig is Guid g) return g == Guid.Empty ? (Guid?)null : g;
                // AssetClassGuid carries the value as ExportedGuid (a "Guid" member never existed, so __guid was
                // never written). A zero guid is a non-exported object: report nothing rather than 000...0.
                Guid? eg = ReadGuidMember(ig, ig.GetType(), "ExportedGuid") ?? ReadGuidMember(ig, ig.GetType(), "Guid");
                if (eg != null && eg.Value != Guid.Empty) return eg;
            }
            catch { }
            return null;
        }

        private static JToken DumpCString(object val, Type t)
        {
            PropertyInfo p = t.GetProperty("String") ?? t.GetProperty("Value");
            if (p != null) return new JValue(p.GetValue(val)?.ToString() ?? "");
            return new JValue(val.ToString());
        }

        private static JToken DumpSha1(object val, Type t)
        {
            MethodInfo m = t.GetMethod("ToHexString") ?? t.GetMethod("ToString", Type.EmptyTypes);
            string s = m != null ? m.Invoke(val, null)?.ToString() ?? "" : val.ToString();
            return new JValue(s.ToLowerInvariant());
        }

        private static JToken DumpTypeRef(object val, Type t)
        {
            PropertyInfo p = t.GetProperty("Name") ?? t.GetProperty("TypeName");
            return new JValue(p != null ? p.GetValue(val)?.ToString() ?? "" : val.ToString());
        }

        private static JToken DumpFileRef(object val, Type t)
        {
            PropertyInfo p = t.GetProperty("Path") ?? t.GetProperty("Value");
            return new JValue(p != null ? p.GetValue(val)?.ToString() ?? "" : val.ToString());
        }

        private static JToken DumpPointerRef(object val, Type t, HashSet<int> pathIds, bool resolve, bool deep)
        {
            var jo = new JObject { ["__type"] = "PointerRef" };

            // PointerRef.Type enum: 0=Null, 1=Internal, 2=External
            PropertyInfo typeProp = t.GetProperty("Type");
            if (typeProp == null) return jo;
            int refType;
            try { refType = Convert.ToInt32(typeProp.GetValue(val)); }
            catch { return jo; }

            if (refType == 0) { jo["__note"] = "null ref"; return jo; }
            if (refType == 1)
            {
                // Internal ref -> an object in the SAME asset. With --deep, expand it inline (the target
                // object's full field layout) instead of just noting it. Cycle-protected via pathIds.
                object inner = null;
                try { inner = t.GetProperty("Internal")?.GetValue(val); } catch { }
                if (!deep)
                {
                    jo["__note"] = "internal ref";
                    // Name the target without expanding it, so a single object can be read in context.
                    if (inner != null)
                    {
                        Type it = inner.GetType();
                        jo["__targetType"] = it.Name;
                        Guid? ig = TryGetInstanceGuid(inner, it);
                        if (ig != null && ig.Value != Guid.Empty) jo["__targetGuid"] = ig.Value.ToString("D");
                        try { object n = it.GetProperty("Name")?.GetValue(inner); if (n != null && n.ToString() != "") jo["__targetName"] = n.ToString(); } catch { }
                    }
                    return jo;
                }
                if (inner == null) { jo["__note"] = "internal ref (unresolved)"; return jo; }
                return DumpValue(inner, pathIds, resolve, deep);
            }
            // refType == 2: External

            PropertyInfo extProp = t.GetProperty("External");
            if (extProp == null) return jo;
            object ext;
            try { ext = extProp.GetValue(val); } catch { return jo; }
            if (ext == null) return jo;

            // EbxImportReference.FileGuid/ClassGuid are FIELDS, not properties -- reading them via
            // GetProperty returned null and made EVERY external ref render bare. Read field-or-property.
            Type et = ext.GetType();
            Guid? fgN = ReadGuidMember(ext, et, "FileGuid");
            Guid? cgN = ReadGuidMember(ext, et, "ClassGuid");
            if (fgN == null || cgN == null) return jo;
            Guid fg = fgN.Value, cg = cgN.Value;

            jo["__fileGuid"]  = fg.ToString("D");
            jo["__classGuid"] = cg.ToString("D");

            // Resolve the file GUID -> asset path. Prefer the AssetManager's live map (always available,
            // no `index build` needed); fall back to the on-disk ref index.
            string path = null;
            if (resolve && fg != Guid.Empty)
            {
                try { var fe = App.AssetManager.GetEbxEntry(fg); if (fe != null) path = fe.Name; } catch { }
                if (path == null) path = IndexService.TryResolvePath(fg);
            }
            if (path != null) jo["__path"] = path;
            // Single, always-present "what this points at" field, preserving array order: resolved path
            // if available, otherwise the file GUID. Lets callers read ref targets without --no-resolve.
            jo["__target"] = path ?? fg.ToString("D");
            return jo;
        }

        private static JObject DumpResourceRef(object val, Type t, bool resolve)
        {
            var jo = new JObject { ["__type"] = "ResourceRef" };
            // A ResourceRef read back from a saved project can come through as null; dumping used to
            // throw NullReferenceException there, which hid the field entirely.
            if (val == null) { jo["__note"] = "null resource ref"; return jo; }

            // The RID member is not always called ResRid/ResourceId, and it may be a field rather than
            // a property. Probe both, then fall back to the first ulong member so the value is never
            // silently omitted -- this is the field that says whether a cloned resource-backed asset
            // still points at its donor's payload.
            ulong rid;
            object raw = null;
            PropertyInfo ridProp = t.GetProperty("ResRid") ?? t.GetProperty("ResourceId")
                                   ?? t.GetProperty("resourceId");
            if (ridProp != null) raw = ridProp.GetValue(val);
            if (raw == null)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType == typeof(ulong)) { raw = f.GetValue(val); break; }
            }
            if (raw == null)
            {
                foreach (var pi in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (pi.PropertyType == typeof(ulong)) { raw = pi.GetValue(val); break; }
            }
            if (raw == null) { jo["__note"] = "rid member not found on " + t.FullName; return jo; }

            rid = Convert.ToUInt64(raw);
            jo["__resRid"] = rid.ToString();

            if (resolve && rid != 0)
            {
                string resPath = IndexService.TryResolveResPath(rid);
                if (resPath != null) jo["__resPath"] = resPath;
            }
            return jo;
        }
    }
}
