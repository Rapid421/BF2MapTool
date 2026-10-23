using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace FrostyCLI.Core
{
    public static class PropertyPathWriter
    {
        internal struct Frame
        {
            public object        Parent;
            public PropertyInfo  Prop;
            public int?          Index;
        }

        // Resolves the IList at a dot-path (the leaf property must itself be a list). Used by
        // `ebx array-add` / `ebx array-remove`. EBX list properties are reference types (List<T> /
        // observable collections), so mutating the returned list mutates the asset graph in place
        // even when the path traverses value-type structs.
        public static IList ResolveList(object root, string dotPath)
        {
            return ResolveList(root, dotPath, null);
        }

        public static IList ResolveList(object root, string dotPath, IPartitionObjectFactory factory)
        {
            if (root == null)    throw new ArgumentNullException("root");
            if (dotPath == null) throw new ArgumentNullException("dotPath");

            string[] segments = dotPath.Split('.');
            object current = root;

            for (int s = 0; s < segments.Length; s++)
            {
                string seg = segments[s];
                int? idx = null;
                string propName = seg;

                int br = seg.IndexOf('[');
                if (br >= 0)
                {
                    if (!seg.EndsWith("]")) throw new InvalidOperationException("ebx array: malformed segment '" + seg + "'");
                    propName = seg.Substring(0, br);
                    string idxStr = seg.Substring(br + 1, seg.Length - br - 2);
                    int parsed;
                    if (!int.TryParse(idxStr, out parsed)) throw new InvalidOperationException("ebx array: bad index in '" + seg + "'");
                    idx = parsed;
                }
                if (propName.Length == 0)
                    throw new InvalidOperationException("ebx array: segment '" + seg + "' has no property name before '['");

                PropertyInfo pi = current.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null)
                    throw new InvalidOperationException("ebx array: property '" + propName + "' not found on type '" + current.GetType().Name + "'");

                object val = pi.GetValue(current);
                if (idx.HasValue)
                {
                    IList parentList = val as IList;
                    if (parentList == null) throw new InvalidOperationException("ebx array: property '" + propName + "' is not an IList");
                    if (idx.Value < 0 || idx.Value >= parentList.Count)
                    {
                        string growErr;
                        if (!PathAutoCreate.TryGrowList(parentList, pi, ElementTypeOf(pi.PropertyType),
                                                        idx.Value, factory, out growErr))
                            throw new InvalidOperationException("ebx array: index [" + idx.Value + "] out of range on '" + propName + "' (count: " + parentList.Count + "): " + growErr);
                    }
                    val = parentList[idx.Value];
                }
                if (val == null || PointerRefUtil.IsNullRef(val))
                {
                    object made; string createErr;
                    Type memberType = idx.HasValue ? ElementTypeOf(pi.PropertyType) : pi.PropertyType;
                    if (!PathAutoCreate.TryCreateMemberValue(pi, memberType, factory, out made, out createErr))
                        throw new InvalidOperationException("ebx array: nothing at '" + propName + "'; cannot continue: " + createErr);
                    if (idx.HasValue) ((IList)pi.GetValue(current))[idx.Value] = made;
                    else pi.SetValue(current, made);
                    val = made;
                }
                // Sub-objects are reached through internal PointerRefs; follow them so a path like
                // Settings[0].Config resolves the list on the referenced object.
                current = PointerRefUtil.Descend(val);
            }

            IList list = current as IList;
            if (list == null)
                throw new InvalidOperationException("ebx array: property at '" + dotPath + "' is not a list (type " + current.GetType().Name + ")");
            return list;
        }

        // The element type of a declared List<T> / T[] member.
        internal static Type ElementTypeOf(Type collectionType)
        {
            if (collectionType == null) return null;
            if (collectionType.IsArray) return collectionType.GetElementType();
            if (collectionType.IsGenericType) return collectionType.GetGenericArguments()[0];
            return null;
        }

        // The element type of a List<T> / T[] IList.
        public static Type ListElementType(IList list)
        {
            Type lt = list.GetType();
            if (lt.IsArray) return lt.GetElementType();
            if (lt.IsGenericType) return lt.GetGenericArguments()[0];
            // Fall back to a non-generic IList: probe the first element.
            foreach (object o in list) if (o != null) return o.GetType();
            return typeof(object);
        }

        public static void SetValue(object root, string dotPath, string strValue)
        {
            SetValue(root, dotPath, strValue, null);
        }

        public static void SetValue(object root, string dotPath, string strValue, IPartitionObjectFactory factory)
        {
            if (root == null)    throw new ArgumentNullException("root");
            if (dotPath == null) throw new ArgumentNullException("dotPath");

            string[] segments = dotPath.Split('.');
            var stack = new List<Frame>(segments.Length);
            object current = root;

            for (int s = 0; s < segments.Length; s++)
            {
                string seg = segments[s];
                int? idx = null;
                string propName = seg;

                int br = seg.IndexOf('[');
                if (br >= 0)
                {
                    if (!seg.EndsWith("]")) throw new InvalidOperationException("ebx set: malformed segment '" + seg + "'");
                    propName = seg.Substring(0, br);
                    string idxStr = seg.Substring(br + 1, seg.Length - br - 2);
                    int parsed;
                    if (!int.TryParse(idxStr, out parsed)) throw new InvalidOperationException("ebx set: bad index in '" + seg + "'");
                    idx = parsed;
                }

                if (propName.Length == 0)
                    throw new InvalidOperationException("ebx set: segment '" + seg + "' has no property name before '['");

                Type curType = current.GetType();
                PropertyInfo pi = curType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (pi == null)
                    throw new InvalidOperationException("ebx set: property '" + propName + "' not found on type '" + curType.Name + "'");

                stack.Add(new Frame { Parent = current, Prop = pi, Index = idx });

                bool isLeaf = (s == segments.Length - 1);
                if (isLeaf) break;

                // descend
                object parent = current;
                object child = pi.GetValue(parent);
                if (idx.HasValue)
                {
                    IList list = child as IList;
                    if (list == null) throw new InvalidOperationException("ebx set: property '" + propName + "' is not an IList");
                    if (idx.Value < 0 || idx.Value >= list.Count)
                    {
                        string growErr;
                        if (!PathAutoCreate.TryGrowList(list, pi, ElementTypeOf(pi.PropertyType),
                                                        idx.Value, factory, out growErr))
                            throw new InvalidOperationException("ebx set: index [" + idx.Value + "] out of range on '" + propName + "' (count: " + list.Count + "): " + growErr);
                    }
                    current = list[idx.Value];
                }
                else
                {
                    current = child;
                }
                // A PointerRef is a struct, so an unset reference reads as a present-but-Null ref
                // rather than a null value. Either way the path is stuck, and either way the fix is
                // to put a real object there -- when the caller asked for one.
                if (current == null || PointerRefUtil.IsNullRef(current))
                {
                    object made; string createErr;
                    Type memberType = idx.HasValue ? ElementTypeOf(pi.PropertyType) : pi.PropertyType;
                    if (!PathAutoCreate.TryCreateMemberValue(pi, memberType, factory, out made, out createErr))
                        throw new InvalidOperationException("ebx set: nothing at '" + propName + "'; cannot descend: " + createErr);
                    if (idx.HasValue) ((IList)pi.GetValue(parent))[idx.Value] = made;
                    else pi.SetValue(parent, made);
                    current = made;
                }
                // Follow internal refs so scalars on sub-objects are addressable. This is a descend
                // step only -- a PointerRef at the leaf stays a PointerRef for set-ref to overwrite.
                current = PointerRefUtil.Descend(current);
            }

            // Determine leaf target type
            Frame leaf = stack[stack.Count - 1];
            Type targetType;
            if (leaf.Index.HasValue)
            {
                Type pt = leaf.Prop.PropertyType;
                if (pt.IsArray) targetType = pt.GetElementType();
                else if (pt.IsGenericType) targetType = pt.GetGenericArguments()[0];
                else throw new InvalidOperationException("ebx set: cannot determine element type of '" + leaf.Prop.Name + "'");
            }
            else
            {
                targetType = leaf.Prop.PropertyType;
            }

            object coerced = TypeCoercion.Coerce(targetType, strValue);

            // Write leaf
            if (leaf.Index.HasValue)
            {
                IList list = (IList)leaf.Prop.GetValue(leaf.Parent);
                if (list == null) throw new InvalidOperationException("ebx set: list '" + leaf.Prop.Name + "' is null");
                if (leaf.Index.Value < 0 || leaf.Index.Value >= list.Count)
                    throw new InvalidOperationException("ebx set: index [" + leaf.Index.Value + "] out of range on '" + leaf.Prop.Name + "' (count: " + list.Count + ")");
                list[leaf.Index.Value] = coerced;
            }
            else
            {
                leaf.Prop.SetValue(leaf.Parent, coerced);
                // Before the struct write-back, so a value-type owner carries the synced Name up too.
                SyncDerivedName(leaf.Parent, leaf.Prop);
            }

            // Struct write-back: walk i = Count-2 down to 0
            for (int i = stack.Count - 2; i >= 0; i--)
            {
                Frame outer = stack[i];
                Frame inner = stack[i + 1];

                // Re-read the child object that was potentially mutated
                object childObj = inner.Parent;
                if (!childObj.GetType().IsValueType) break;

                // Write child back into outer
                if (outer.Index.HasValue)
                {
                    IList list = (IList)outer.Prop.GetValue(outer.Parent);
                    list[outer.Index.Value] = childObj;
                }
                else
                {
                    outer.Prop.SetValue(outer.Parent, childObj);
                }
            }

            // A value-type copy that was not written back leaves the graph untouched while the set
            // "succeeds"; read the path again from the root so that can never pass silently.
            object readBack = PropertyPathReader.GetValue(root, dotPath);
            if (!SameValue(coerced, readBack))
                throw new InvalidOperationException("ebx set: '" + dotPath + "' did not take: wrote " + Show(coerced) +
                    " but it reads back as " + Show(readBack));
        }

        // EventSpec (Id + transient Name), and anything shaped like it: Name's getter caches a string
        // rendered from Id the first time it is read, and its setter re-derives Id from a string. The
        // project writer saves transient members and the reader assigns Name after Id, so a Name read
        // (by a dump, a connection listing, lint) before Id changed turns the old Id back on reload: the
        // set looks right in-process and is gone after save + load. Writing Id has to bring Name along.
        internal static void SyncDerivedName(object owner, PropertyInfo written)
        {
            if (owner == null || written == null || written.Name != "Id") return;
            PropertyInfo name = owner.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            if (name == null || !name.CanRead || !name.CanWrite || !IsTransient(name)) return;
            object id = written.GetValue(owner);

            // Drop the cached string so the getter renders it from the new Id (the event's name when the
            // string table knows it, "0x<hex>" otherwise). Assigning that back goes through the setter,
            // so Id stays exactly what the setter derives from the Name that will be saved.
            string rendered = null;
            FieldInfo backing = owner.GetType().GetField("_Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (backing != null)
            {
                backing.SetValue(owner, backing.FieldType.IsValueType ? Activator.CreateInstance(backing.FieldType) : null);
                try { object n = name.GetValue(owner); rendered = n != null ? n.ToString() : null; }
                catch { rendered = null; }
            }
            if (TrySetName(owner, name, rendered, written, id)) return;

            IFormattable f = id as IFormattable;
            if ((id is int || id is uint) && TrySetName(owner, name, "0x" + f.ToString("x8", CultureInfo.InvariantCulture), written, id))
                return;

            throw new InvalidOperationException("ebx set: " + owner.GetType().Name + ".Id = " + Show(id) +
                " would not survive a save: its transient Name re-derives Id on load and no Name maps back to that id");
        }

        private static bool TrySetName(object owner, PropertyInfo name, string text, PropertyInfo idProp, object wantId)
        {
            if (string.IsNullOrEmpty(text)) return false;
            object v = MakeString(name.PropertyType, text);
            if (v == null) return false;
            try { name.SetValue(owner, v); }
            catch (TargetInvocationException) { idProp.SetValue(owner, wantId); return false; }
            if (SameValue(wantId, idProp.GetValue(owner))) return true;
            idProp.SetValue(owner, wantId);
            return false;
        }

        // A string or CString value of the given member type (CString converts implicitly from string).
        private static object MakeString(Type t, string text)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t == typeof(string)) return text;
            MethodInfo imp = t.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (imp != null && imp.ReturnType == t) return imp.Invoke(null, new object[] { text });
            ConstructorInfo ctor = t.GetConstructor(new[] { typeof(string) });
            return ctor != null ? ctor.Invoke(new object[] { text }) : null;
        }

        // Matched by attribute name so Core keeps no compile-time dependency on FrostySdk.
        internal static bool IsTransient(PropertyInfo p)
        {
            foreach (object a in p.GetCustomAttributes(true))
                if (a.GetType().Name == "IsTransientAttribute") return true;
            return false;
        }

        /// <summary>
        /// True when a load can put back a different value in <paramref name="member"/> of <paramref name="t"/>:
        /// the member is transient itself, or it is the Id of an EventSpec-shaped object whose transient Name
        /// re-derives it. A write anywhere else is saved and read back as written.
        /// </summary>
        public static bool CanBeReappliedOnLoad(Type t, string member)
        {
            if (t == null || string.IsNullOrEmpty(member)) return false;
            PropertyInfo p = t.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && IsTransient(p)) return true;
            if (member != "Id") return false;
            PropertyInfo name = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            return name != null && IsTransient(name);
        }

        /// <summary>
        /// True when <paramref name="t"/> declares a transient member. Transient members are rebuilt or
        /// re-applied when a saved project is read back, so a write to such an object is the one kind
        /// whose in-memory result can differ from what a save persists.
        /// Frosty's own bookkeeping members (<c>__Id</c>, <c>__InstanceGuid</c>: any name starting with "__") are
        /// transient on nearly every SDK type (8,193 of 10,921 in SWBF2) but are not data a set can lose, so they do
        /// not count. Counting them put every ordinary set through a full-partition round trip (minutes per set on
        /// a 45 MB partition such as DefaultSoldierStateMachine).
        /// </summary>
        public static bool HasTransientMembers(Type t)
        {
            if (t == null) return false;
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (!p.Name.StartsWith("__", StringComparison.Ordinal) && IsTransient(p)) return true;
            return false;
        }

        /// <summary>
        /// The object that owns the leaf member of <paramref name="dotPath"/> (after following internal
        /// refs), i.e. the object a set on that path writes into.
        /// </summary>
        public static object LeafOwner(object root, string dotPath)
        {
            int dot = dotPath.LastIndexOf('.');
            return dot < 0 ? root : PointerRefUtil.Descend(PropertyPathReader.GetValue(root, dotPath.Substring(0, dot)));
        }

        /// <summary>Value equality for written-vs-read-back checks (CString and friends by their text).</summary>
        public static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (Equals(a, b)) return true;
            return a.GetType() == b.GetType() && string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
        }

        private static string Show(object v)
        {
            return v == null ? "null" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
