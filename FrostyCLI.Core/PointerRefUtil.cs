using System;
using System.Collections;
using System.Reflection;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Internal-PointerRef handling for dot-path traversal.
    ///
    /// An EBX partition is one root object plus a flat pool of sub-objects, and a field that
    /// "contains" a sub-object actually holds a PointerRef with Type=Internal pointing at it.
    /// The dot-path walkers used to stop there ("property 'Config' not found on type 'PointerRef'"),
    /// which put every sub-object out of reach of `ebx set` / `array-add` / `set-ref` — that is
    /// most of a level's or a settings bundle's real content, and all of a blueprint's wiring.
    ///
    /// Descending therefore transparently follows an internal ref. A PointerRef sitting at the
    /// *leaf* is left alone: that is the thing `set-ref` overwrites, and dereferencing it there
    /// would rewrite the target's contents instead of the reference.
    ///
    /// Everything matches by type NAME so Core keeps no compile-time dependency on the generated
    /// EBX types, consistent with EbxRefWriter and EbxDumper.
    /// </summary>
    public static class PointerRefUtil
    {
        public static bool IsPointerRef(object o)
        {
            return o != null && o.GetType().Name == "PointerRef";
        }

        private static object Member(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (pi != null) return pi.GetValue(obj);
            FieldInfo fi = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return fi != null ? fi.GetValue(obj) : null;
        }

        /// <summary>PointerRef.Type as a string: "Null", "Internal" or "External" ("" if not a PointerRef).</summary>
        public static string RefKind(object pr)
        {
            if (!IsPointerRef(pr)) return "";
            object t = Member(pr, "Type");
            return t != null ? t.ToString() : "";
        }

        /// <summary>
        /// True when <paramref name="value"/> is a PointerRef that points at nothing.
        ///
        /// Distinct from "the member is null": a PointerRef is a struct, so an unset reference field
        /// is a present-but-Null ref rather than a null value. A path that has to descend through one
        /// is just as stuck either way.
        /// </summary>
        public static bool IsNullRef(object value)
        {
            return IsPointerRef(value) && RefKind(value) == "Null";
        }

        /// <summary>
        /// The sub-object an internal PointerRef points at, or null when <paramref name="pr"/> is not
        /// an internal ref.
        /// </summary>
        public static object Target(object pr)
        {
            return RefKind(pr) == "Internal" ? Member(pr, "Internal") : null;
        }

        /// <summary>
        /// Used at every descend step: follows an internal PointerRef, otherwise returns the value
        /// unchanged. Chained refs are followed to a fixed point, with a bound so a malformed graph
        /// that points at itself fails loudly instead of hanging.
        /// </summary>
        public static object Descend(object value)
        {
            int guard = 0;
            while (IsPointerRef(value))
            {
                string kind = RefKind(value);
                if (kind != "Internal")
                    throw new InvalidOperationException(
                        "path: cannot descend through a " + (kind == "" ? "null" : kind.ToLowerInvariant()) +
                        " reference; only internal refs (sub-objects of the same partition) can be traversed");

                object inner = Target(value);
                if (inner == null)
                    throw new InvalidOperationException("path: internal reference has no target object");
                value = inner;

                if (++guard > 64)
                    throw new InvalidOperationException("path: internal reference chain exceeded 64 hops (cycle?)");
            }
            return value;
        }

        /// <summary>
        /// The instance GUID of an EBX object, via its generated GetInstanceGuid(). Returns
        /// Guid.Empty when the object does not expose one.
        /// </summary>
        public static Guid InstanceGuid(object obj)
        {
            if (obj == null) return Guid.Empty;
            try
            {
                MethodInfo m = obj.GetType().GetMethod("GetInstanceGuid", Type.EmptyTypes);
                object g = m != null ? m.Invoke(obj, null) : null;
                if (g == null) return Guid.Empty;
                // AssetClassGuid wraps the real Guid in ExportedGuid; a bare Guid is returned as-is.
                if (g is Guid direct) return direct;
                object exported = Member(g, "ExportedGuid");
                return exported is Guid eg ? eg : Guid.Empty;
            }
            catch { return Guid.Empty; }
        }

        /// <summary>
        /// Resolves an object inside <paramref name="objects"/> from a user-supplied reference:
        /// an instance GUID, or "#N" for the Nth object in the partition's own order.
        /// </summary>
        public static object ResolveObjectRef(IList objects, string reference, out string error)
        {
            error = null;
            if (objects == null)            { error = "no object pool"; return null; }
            if (string.IsNullOrEmpty(reference)) { error = "empty object reference"; return null; }

            if (reference[0] == '#')
            {
                int idx;
                if (!int.TryParse(reference.Substring(1), out idx))
                { error = "bad object index '" + reference + "'"; return null; }
                if (idx < 0 || idx >= objects.Count)
                { error = "object index " + idx + " out of range (partition has " + objects.Count + ")"; return null; }
                return objects[idx];
            }

            Guid want;
            if (!Guid.TryParse(reference, out want))
            { error = "'" + reference + "' is neither a GUID nor a #index"; return null; }

            foreach (object o in objects)
            {
                if (o == null) continue;
                if (InstanceGuid(o) == want) return o;
            }
            error = "no object with instance GUID " + want + " in this partition";
            return null;
        }
    }
}
