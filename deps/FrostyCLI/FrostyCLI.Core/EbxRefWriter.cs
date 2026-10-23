using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Sets a PointerRef (External/2) at a dot-path leaf via reflection.
    /// Matches PointerRef and EbxImportReference by type NAME so Core has no
    /// compile-time dependency on generated EBX types.
    /// </summary>
    public static class EbxRefWriter
    {
        /// <summary>Reads the leaf's declared reference type without creating missing path objects.</summary>
        public static Type DeclaredTargetType(object root, string dotPath)
        {
            if (root == null || string.IsNullOrWhiteSpace(dotPath)) return null;
            Type currentType = root.GetType();
            object current = root;
            string[] segments = dotPath.Split('.');
            for (int i = 0; i < segments.Length; i++)
            {
                string name = segments[i]; int? index = null;
                int bracket = name.IndexOf('[');
                if (bracket >= 0)
                {
                    if (!name.EndsWith("]") || !int.TryParse(name.Substring(bracket + 1, name.Length - bracket - 2), out int parsed) || parsed < 0)
                        throw new InvalidOperationException("set-ref: malformed path segment " + name);
                    index = parsed; name = name.Substring(0, bracket);
                }
                var member = MemberAccessor.Find(currentType, name);
                if (member == null) return null;
                Type declared = PathAutoCreate.DeclaredBaseType(member.Member);
                if (i == segments.Length - 1) return declared;
                object value = current != null ? member.Get(current) : null;
                Type nextType = index.HasValue ? ElementType(member.MemberType) : member.MemberType;
                if (index.HasValue) value = value is IList list && index.Value < list.Count ? list[index.Value] : null;
                if (nextType?.Name == "PointerRef")
                {
                    if (value == null || PointerRefUtil.IsNullRef(value)) value = null;
                    else value = PointerRefUtil.Descend(value);
                    nextType = value?.GetType() ?? declared;
                }
                if (nextType == null) return null;
                current = value; currentType = value?.GetType() ?? nextType;
            }
            return null;
        }

        private struct Frame
        {
            public object         Parent;
            public MemberAccessor Acc;
            public int?           Index;
        }

        /// <summary>
        /// Points the PointerRef at <paramref name="internalObject"/>, another object in the SAME
        /// partition. Connections between entities inside one level (an AreaProximityEntityData
        /// driving a VisualEnvironmentReferenceObjectData's Visibility, say) are internal refs; an
        /// external ref to the level's own file guid is a different thing and the runtime rejects it.
        /// </summary>
        public static void SetRefByPath(object root, string dotPath, object internalObject)
        {
            SetRefByPathCore(root, dotPath, Guid.Empty, Guid.Empty, internalObject, null);
        }

        public static void SetRefByPath(object root, string dotPath, object internalObject,
                                        IPartitionObjectFactory factory)
        {
            SetRefByPathCore(root, dotPath, Guid.Empty, Guid.Empty, internalObject, factory);
        }

        public static void SetRefByPath(object root, string dotPath, Guid fileGuid, Guid classGuid)
        {
            SetRefByPathCore(root, dotPath, fileGuid, classGuid, null, null);
        }

        public static void SetRefByPath(object root, string dotPath, Guid fileGuid, Guid classGuid,
                                        IPartitionObjectFactory factory)
        {
            SetRefByPathCore(root, dotPath, fileGuid, classGuid, null, factory);
        }

        private static void SetRefByPathCore(object root, string dotPath, Guid fileGuid, Guid classGuid,
                                             object internalObject, IPartitionObjectFactory factory)
        {
            if (root == null)              throw new ArgumentNullException("root");
            if (string.IsNullOrEmpty(dotPath)) throw new ArgumentNullException("dotPath");

            string[] segments = dotPath.Split('.');
            var stack = new List<Frame>(segments.Length);
            object current = root;

            for (int s = 0; s < segments.Length; s++)
            {
                string seg   = segments[s];
                int?   idx   = null;
                string name  = seg;

                int br = seg.IndexOf('[');
                if (br >= 0)
                {
                    if (!seg.EndsWith("]"))
                        throw new InvalidOperationException("set-ref: malformed segment '" + seg + "'");
                    name = seg.Substring(0, br);
                    int parsed;
                    if (!int.TryParse(seg.Substring(br + 1, seg.Length - br - 2), out parsed))
                        throw new InvalidOperationException("set-ref: bad index in '" + seg + "'");
                    idx = parsed;
                }

                MemberAccessor acc = MemberAccessor.Find(current.GetType(), name);
                if (acc == null)
                    throw new InvalidOperationException(
                        "set-ref: member '" + name + "' not found on '" + current.GetType().Name + "'");

                stack.Add(new Frame { Parent = current, Acc = acc, Index = idx });

                if (s == segments.Length - 1) break;

                object parent = current;
                object child = acc.Get(parent);
                if (idx.HasValue)
                {
                    IList list = child as IList;
                    if (list == null)
                        throw new InvalidOperationException("set-ref: '" + name + "' is not an IList");
                    if (idx.Value < 0 || idx.Value >= list.Count)
                    {
                        string growErr;
                        if (!PathAutoCreate.TryGrowList(list, acc.Member, ElementType(acc.MemberType),
                                                        idx.Value, factory, out growErr))
                            throw new InvalidOperationException(
                                "set-ref: index [" + idx.Value + "] out of range on '" + name + "': " + growErr);
                    }
                    current = list[idx.Value];
                }
                else
                {
                    current = child;
                }
                // An unset reference field is a present-but-Null PointerRef, not a null value; the
                // path is equally stuck either way.
                if (current == null || PointerRefUtil.IsNullRef(current))
                {
                    object made; string createErr;
                    Type memberType = idx.HasValue ? ElementType(acc.MemberType) : acc.MemberType;
                    if (!PathAutoCreate.TryCreateMemberValue(acc.Member, memberType, factory, out made, out createErr))
                        throw new InvalidOperationException(
                            "set-ref: nothing at '" + name + "'; cannot descend: " + createErr);
                    if (idx.HasValue) ((IList)acc.Get(parent))[idx.Value] = made;
                    else acc.Set(parent, made);
                    current = made;
                }
                // Descend through internal refs so a ref can be written on a sub-object. The leaf is
                // never dereferenced -- that PointerRef is exactly what this command overwrites.
                current = PointerRefUtil.Descend(current);
            }

            // --- resolve leaf type ---
            Frame leaf     = stack[stack.Count - 1];
            Type  leafType = leaf.Index.HasValue
                                 ? ElementType(leaf.Acc.MemberType)
                                 : leaf.Acc.MemberType;

            if (leafType == null || leafType.Name != "PointerRef")
                throw new InvalidOperationException(
                    "set-ref: leaf property is not a PointerRef (got '"
                    + (leafType == null ? "null" : leafType.Name) + "')");

            // Both GUIDs empty means "write a null ref": a default-constructed PointerRef has
            // Type 0 (Null), which is what shipped assets carry in an unset reference field. An
            // external ref to GUID 0 is not the same thing and the runtime treats it as a broken import.
            object pointerRef;
            if (internalObject != null)
                pointerRef = Activator.CreateInstance(leafType, new object[] { internalObject });
            else
                pointerRef = fileGuid == Guid.Empty && classGuid == Guid.Empty
                    ? Activator.CreateInstance(leafType)
                    : BuildExternalPointerRef(leafType, fileGuid, classGuid);

            // --- write leaf ---
            if (leaf.Index.HasValue)
            {
                IList list = (IList)leaf.Acc.Get(leaf.Parent);
                list[leaf.Index.Value] = pointerRef;
            }
            else
            {
                leaf.Acc.Set(leaf.Parent, pointerRef);
            }

            // --- struct write-back: walk i = Count-2 down to 0 ---
            for (int i = stack.Count - 2; i >= 0; i--)
            {
                object childObj = stack[i + 1].Parent;
                if (!childObj.GetType().IsValueType) break;

                Frame outer = stack[i];
                if (outer.Index.HasValue)
                {
                    IList list = (IList)outer.Acc.Get(outer.Parent);
                    list[outer.Index.Value] = childObj;
                }
                else
                {
                    outer.Acc.Set(outer.Parent, childObj);
                }
            }
        }

        // ------------------------------------------------------------------ //

        /// <summary>
        /// Builds an EXTERNAL PointerRef -- a reference to an object in another partition, addressed
        /// by the containing asset's file guid plus that object's exported instance guid.
        ///
        /// Public because connection endpoints need the same thing: a cross-partition connection
        /// stores exactly this shape. Sharing one builder keeps the immutable-struct handling (the
        /// real PointerRef sets Type/External only through its constructors) in a single place --
        /// duplicating it is how one caller ends up writing a ref with Type left at Null, which
        /// serializes without complaint and resolves to nothing.
        /// </summary>
        public static object BuildExternalPointerRef(Type pointerRefType, Guid fileGuid, Guid classGuid)
        {
            // Discover the import-reference type from PointerRef.External's own declared type.
            // This works in unit tests (where the struct is FakeImport) AND in production
            // (where it is EbxImportReference) without any hard-coded name search.
            Type importType = null;
            MemberAccessor extAcc = MemberAccessor.Find(pointerRefType, "External");
            if (extAcc != null)
                importType = extAcc.MemberType;

            // Fallback: scan loaded assemblies by name (production safety net).
            if (importType == null)
                importType = FindTypeByName("EbxImportReference");

            if (importType == null)
                throw new InvalidOperationException(
                    "set-ref: could not determine EbxImportReference type from PointerRef.External");

            object import = Activator.CreateInstance(importType);
            SetMember(import, importType, "FileGuid",  fileGuid);
            SetMember(import, importType, "ClassGuid", classGuid);

            // The real FrostySdk.Ebx.PointerRef is an *immutable* struct: External/Internal/Type
            // are get-only properties, set only through its constructors. Trying to assign them
            // via reflection fails with "Property set method not found". Prefer the
            // PointerRef(EbxImportReference) constructor (it also sets Type = External for us).
            ConstructorInfo extCtor = pointerRefType.GetConstructor(new[] { importType });
            if (extCtor != null)
                return extCtor.Invoke(new[] { import });

            // Fallback for mutable PointerRef-shaped types (e.g. the unit-test fakes, which expose
            // settable Type/External members and have no matching constructor).
            object pr      = Activator.CreateInstance(pointerRefType);
            MemberAccessor typeAcc = MemberAccessor.Find(pointerRefType, "Type");
            if (typeAcc != null)
            {
                object enumVal = typeAcc.MemberType.IsEnum
                    ? Enum.ToObject(typeAcc.MemberType, 2)
                    : (object)2;
                typeAcc.Set(pr, enumVal);
            }
            SetMember(pr, pointerRefType, "External", import);
            return pr;
        }

        private static Type FindTypeByName(string simpleName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in asm.GetTypes())
                        if (t.Name == simpleName) return t;
                }
                catch { }
            }
            return null;
        }

        private static Type ElementType(Type collType)
        {
            if (collType.IsArray)       return collType.GetElementType();
            if (collType.IsGenericType) return collType.GetGenericArguments()[0];
            return null;
        }

        private static void SetMember(object obj, Type t, string name, object value)
        {
            MemberAccessor a = MemberAccessor.Find(t, name);
            if (a == null)
                throw new InvalidOperationException(
                    "set-ref: member '" + name + "' not found on '" + t.Name + "'");
            a.Set(obj, value);
        }

        // ------------------------------------------------------------------ //

        private sealed class MemberAccessor
        {
            private readonly PropertyInfo _p;
            private readonly FieldInfo    _f;

            public Type MemberType { get { return _p != null ? _p.PropertyType : _f.FieldType; } }

            /// <summary>The member itself, so its EbxFieldMeta can be read.</summary>
            public MemberInfo Member { get { return _p != null ? (MemberInfo)_p : _f; } }

            private MemberAccessor(PropertyInfo p, FieldInfo f) { _p = p; _f = f; }

            public static MemberAccessor Find(Type t, string name)
            {
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return new MemberAccessor(p, null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return new MemberAccessor(null, f);
                return null;
            }

            public object Get(object o) { return _p != null ? _p.GetValue(o)    : _f.GetValue(o); }
            public void   Set(object o, object v) { if (_p != null) _p.SetValue(o, v); else _f.SetValue(o, v); }
        }
    }
}
