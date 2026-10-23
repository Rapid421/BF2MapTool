using System;
using System.Collections;
using System.Reflection;

namespace FrostyCLI.Core
{
    /// <summary>
    /// Creates and registers the partition objects that path auto-creation needs.
    ///
    /// Supplied by the command layer, which owns the EbxAsset: a new sub-object has to be given an
    /// instance GUID and added to the partition, or it is unreachable from the root and EBX
    /// serialization -- which keeps only the reachable graph -- drops it on save. Core stays free of
    /// any EBX dependency by taking this instead.
    /// </summary>
    public interface IPartitionObjectFactory
    {
        /// <summary>Creates an instance of <paramref name="type"/>, registered in the partition.</summary>
        object Create(Type type, out string error);
    }

    /// <summary>
    /// Builds what a dotted path needs in order to descend: the partition object a null PointerRef
    /// should point at, and the elements an array is too short to have.
    ///
    /// Gating a sub-world needs three partition objects joined by internal PointerRefs --
    /// SubWorldReferenceObjectData.InclusionSettings -> SubWorldInclusionSettings -> Settings[i] ->
    /// SubWorldInclusionSetting. Every hop is makeable one command at a time, but only if the caller
    /// already knows each intermediate object's GUID, and the command that creates one
    /// (`set-ref --target-object new:`) never reports the GUID it made. So the chain cannot be
    /// written from a script at all.
    ///
    /// The concrete type is never guessed. The generated SDK records it on each member as
    /// EbxFieldMeta.BaseType -- Pointer members name what they point at, Array members name their
    /// element type -- and where that is missing or abstract this refuses rather than inventing one.
    /// An object of the wrong class loads happily and resolves to nothing, so a guess here would be
    /// indistinguishable from success.
    ///
    /// The attribute is read by NAME, as PointerRefUtil and EbxRefWriter read PointerRef, so Core
    /// keeps no compile-time dependency on the generated types and the logic stays unit-testable.
    /// </summary>
    public static class PathAutoCreate
    {
        private struct Meta
        {
            public string FieldType;    // "Pointer", "Array", "Struct", ...
            public string ArrayType;    // element kind when FieldType is "Array"
            public Type BaseType;       // what a Pointer points at / what an Array holds
        }

        private static Meta ReadMeta(MemberInfo member)
        {
            var meta = new Meta();
            if (member == null) return meta;
            foreach (object attr in member.GetCustomAttributes(false))
            {
                if (attr == null || attr.GetType().Name != "EbxFieldMetaAttribute") continue;
                Type at = attr.GetType();
                meta.FieldType = ReadString(attr, at, "Type");
                meta.ArrayType = ReadString(attr, at, "ArrayType");
                PropertyInfo bp = at.GetProperty("BaseType", BindingFlags.Public | BindingFlags.Instance);
                if (bp != null) { try { meta.BaseType = bp.GetValue(attr) as Type; } catch { } }
                break;
            }
            return meta;
        }

        private static string ReadString(object attr, Type at, string name)
        {
            PropertyInfo p = at.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return null;
            try { object v = p.GetValue(attr); return v == null ? null : v.ToString(); }
            catch { return null; }
        }

        /// <summary>The type a member declares it points at (EbxFieldMeta.BaseType), or null.</summary>
        public static Type DeclaredBaseType(MemberInfo member)
        {
            return ReadMeta(member).BaseType;
        }

        /// <summary>
        /// The value a missing member needs so a path can descend through it: for a PointerRef
        /// member, a real object of its declared target type wrapped in an internal reference.
        /// </summary>
        public static bool TryCreateMemberValue(MemberInfo member, Type memberType,
                                                IPartitionObjectFactory factory,
                                                out object value, out string error)
        {
            value = null;
            error = null;
            string name = member != null ? member.Name : "<member>";

            if (factory == null)
            {
                error = "'" + name + "' is not set, and nothing may be created here";
                return false;
            }

            Meta meta = ReadMeta(member);
            Type target = meta.BaseType;
            if (target == null)
            {
                error = "'" + name + "' does not record the type it points at, so there is nothing to " +
                        "create; set it explicitly with `ebx set-ref --target-object new:<TypeName>`";
                return false;
            }
            if (target.IsAbstract || target.IsInterface)
            {
                error = "'" + name + "' points at " + target.Name + ", which is abstract; name a " +
                        "concrete type with `ebx set-ref --target-object new:<TypeName>`";
                return false;
            }

            object created = factory.Create(target, out error);
            if (created == null)
            {
                if (error == null) error = "could not create a " + target.Name + " for '" + name + "'";
                return false;
            }

            return TryWrap(created, memberType, target, name, out value, ref error);
        }

        /// <summary>
        /// Grows <paramref name="list"/> until <paramref name="index"/> is addressable, creating
        /// elements of the kind the member declares. A list that is already long enough is left
        /// alone -- growing it would append elements nobody asked for.
        /// </summary>
        public static bool TryGrowList(IList list, MemberInfo member, Type elementType, int index,
                                       IPartitionObjectFactory factory, out string error)
        {
            error = null;
            string name = member != null ? member.Name : "<member>";

            if (list == null) { error = "'" + name + "' is not a list"; return false; }
            if (index < 0) { error = "negative index on '" + name + "'"; return false; }
            if (index < list.Count) return true;
            if (list.IsFixedSize || list.IsReadOnly)
            { error = "'" + name + "' is a fixed-size list and cannot grow"; return false; }
            if (factory == null)
            {
                error = "index [" + index + "] is past the end of '" + name + "' (count: " + list.Count + ")";
                return false;
            }

            Meta meta = ReadMeta(member);
            bool pointerElements = elementType != null && elementType.Name == "PointerRef";

            while (list.Count <= index)
            {
                object element;
                if (pointerElements)
                {
                    // An element left as a default PointerRef is a NULL reference -- present in the
                    // list, pointing at nothing, and silent about it. Each new slot gets a real
                    // partition object behind a real ref.
                    if (meta.BaseType == null)
                    {
                        error = "'" + name + "' does not record what its elements point at, so its " +
                                "slots cannot be created; add them with `ebx array-add --type <TypeName>`";
                        return false;
                    }
                    if (meta.BaseType.IsAbstract || meta.BaseType.IsInterface)
                    {
                        error = "'" + name + "' holds " + meta.BaseType.Name + ", which is abstract; " +
                                "add elements with `ebx array-add --type <TypeName>`";
                        return false;
                    }
                    object created = factory.Create(meta.BaseType, out error);
                    if (created == null)
                    {
                        if (error == null) error = "could not create a " + meta.BaseType.Name + " for '" + name + "'";
                        return false;
                    }
                    if (!TryWrap(created, elementType, meta.BaseType, name, out element, ref error)) return false;
                }
                else if (!TryCreateDefault(elementType, name, out element, ref error))
                {
                    return false;
                }

                try { list.Add(element); }
                catch (Exception ex) { error = "could not extend '" + name + "': " + ex.Message; return false; }
            }
            return true;
        }

        private static bool TryWrap(object created, Type memberType, Type targetType, string name,
                                    out object value, ref string error)
        {
            if (memberType != null && memberType.Name == "PointerRef")
            {
                try { value = Activator.CreateInstance(memberType, new object[] { created }); return true; }
                catch (Exception ex)
                {
                    error = "could not build an internal reference to " + targetType.Name +
                            " for '" + name + "': " + ex.Message;
                    value = null;
                    return false;
                }
            }
            value = created;
            return true;
        }

        // Elements that are values rather than references: a CString slot, a struct slot.
        private static bool TryCreateDefault(Type elementType, string name, out object value, ref string error)
        {
            value = null;
            if (elementType == null) { error = "cannot determine the element type of '" + name + "'"; return false; }
            if (elementType == typeof(string)) { value = string.Empty; return true; }
            try
            {
                ConstructorInfo fromString = elementType.GetConstructor(new[] { typeof(string) });
                // CString and friends have no parameterless constructor but do take a string.
                if (!elementType.IsValueType && fromString != null)
                { value = fromString.Invoke(new object[] { string.Empty }); return true; }
                value = Activator.CreateInstance(elementType);
                return true;
            }
            catch (Exception ex)
            {
                error = "could not create a " + elementType.Name + " element for '" + name + "': " + ex.Message;
                return false;
            }
        }
    }
}
