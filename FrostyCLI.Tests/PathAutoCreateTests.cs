using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // Building an object graph along a dotted path.
    //
    // Gating a sub-world needs three partition objects joined by internal PointerRefs:
    //
    //   SubWorldReferenceObjectData.InclusionSettings -> SubWorldInclusionSettings
    //      .Settings[i]                               -> SubWorldInclusionSetting
    //         .Criterion                              -> (external) GameModeInclusionSettings
    //         .EnabledOptions[j]                       = CString
    //
    // Each hop is makeable today, but only if you already know every intermediate object's GUID --
    // and `set-ref --target-object new:` never reports the GUID it created, so there is no way to
    // write the chain from a script. Auto-creation closes that by making the path itself build what
    // is missing.
    //
    // The concrete type to create is never guessed: the generated SDK records it on each member as
    // EbxFieldMeta.BaseType. These fakes mirror that attribute's shape, because production matches it
    // by type NAME the way PointerRefUtil and EbxRefWriter match PointerRef. The real attribute's
    // contract is pinned separately by an integration test against SWBF2's own types.
    public class PathAutoCreateTests
    {
        public enum EbxFieldType { Inherited, Struct, Pointer, Array, CString }

        [AttributeUsage(AttributeTargets.Property)]
        public sealed class EbxFieldMetaAttribute : Attribute
        {
            public EbxFieldType Type { get; set; }
            public EbxFieldType ArrayType { get; set; }
            public Type BaseType { get; set; }
        }

        // Mirrors FrostySdk.Ebx.PointerRef, whose Type is a PointerRefType enum -- so a
        // default-constructed one reads as "Null", which is how a missing hop is recognised.
        public enum PointerRefType { Null, Internal, External }

        public struct PointerRef
        {
            public PointerRefType Type;
            public object Internal;
            public PointerRef(object o) { Type = PointerRefType.Internal; Internal = o; }
        }

        public sealed class Criterion { public string Name { get; set; } }
        public sealed class Setting { public string Option { get; set; } }
        public abstract class AbstractThing { }

        public sealed class Settings
        {
            [EbxFieldMeta(Type = EbxFieldType.Array, ArrayType = EbxFieldType.Pointer, BaseType = typeof(Setting))]
            public List<PointerRef> Items { get; set; }

            [EbxFieldMeta(Type = EbxFieldType.Array, ArrayType = EbxFieldType.Struct, BaseType = typeof(Vec))]
            public List<Vec> Points { get; set; }

            [EbxFieldMeta(Type = EbxFieldType.Array, ArrayType = EbxFieldType.CString)]
            public List<string> Options { get; set; }
        }

        public struct Vec { public float X { get; set; } }

        public sealed class Root
        {
            [EbxFieldMeta(Type = EbxFieldType.Pointer, BaseType = typeof(Settings))]
            public PointerRef Inclusion { get; set; }

            [EbxFieldMeta(Type = EbxFieldType.Pointer)]              // no BaseType recorded
            public PointerRef Unknown { get; set; }

            [EbxFieldMeta(Type = EbxFieldType.Pointer, BaseType = typeof(AbstractThing))]
            public PointerRef Abstract { get; set; }
        }

        private sealed class RecordingFactory : IPartitionObjectFactory
        {
            public readonly List<Type> Created = new List<Type>();
            public object Create(Type type, out string error)
            {
                error = null;
                try { object o = Activator.CreateInstance(type); Created.Add(type); return o; }
                catch (Exception ex) { error = ex.Message; return null; }
            }
        }

        private static PropertyInfo Prop<T>(string name)
        {
            return typeof(T).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        }

        // The core move: a null PointerRef whose declared target type is recorded becomes a real
        // object of that type, wrapped in an internal ref, so the path can descend through it.
        [Test]
        public void TryCreateMemberValue_PointerWithBaseType_CreatesAndWraps()
        {
            var factory = new RecordingFactory();
            object value; string error;
            bool ok = PathAutoCreate.TryCreateMemberValue(Prop<Root>("Inclusion"), typeof(PointerRef),
                                                          factory, out value, out error);
            Assert.IsTrue(ok, "error: " + error);
            Assert.IsTrue(value is PointerRef, "should be wrapped in a PointerRef");
            Assert.IsTrue(((PointerRef)value).Internal is Settings, "should point at a Settings");
            Assert.AreEqual(1, factory.Created.Count);
            Assert.AreEqual(typeof(Settings), factory.Created[0]);
        }

        // Without a recorded target type there is nothing to create. Guessing one -- or picking the
        // declared PointerRef type -- would write an object of the wrong class into the partition,
        // which loads and resolves to nothing.
        [Test]
        public void TryCreateMemberValue_NoBaseType_Refuses()
        {
            object value; string error;
            bool ok = PathAutoCreate.TryCreateMemberValue(Prop<Root>("Unknown"), typeof(PointerRef),
                                                          new RecordingFactory(), out value, out error);
            Assert.IsFalse(ok, "a member with no recorded target type must not be created");
            Assert.IsTrue(error != null && error.IndexOf("Unknown", StringComparison.Ordinal) >= 0,
                "the error should name the member; got: " + error);
        }

        [Test]
        public void TryCreateMemberValue_AbstractBaseType_Refuses()
        {
            object value; string error;
            bool ok = PathAutoCreate.TryCreateMemberValue(Prop<Root>("Abstract"), typeof(PointerRef),
                                                          new RecordingFactory(), out value, out error);
            Assert.IsFalse(ok, "an abstract target type cannot be instantiated");
            Assert.IsTrue(error != null && error.IndexOf("abstract", StringComparison.OrdinalIgnoreCase) >= 0,
                "the error should say the type is abstract; got: " + error);
        }

        // A list of PointerRefs grows with real partition objects behind real refs -- an element left
        // as a default PointerRef is a null ref, which is exactly the silent nothing to avoid.
        [Test]
        public void TryGrowList_PointerElements_CreatesObjectsBehindRefs()
        {
            var list = new List<PointerRef>();
            string error;
            var factory = new RecordingFactory();
            bool ok = PathAutoCreate.TryGrowList(list, Prop<Settings>("Items"), typeof(PointerRef), 2,
                                                 factory, out error);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(3, list.Count);
            for (int i = 0; i < 3; i++)
                Assert.IsTrue(list[i].Internal is Setting, "element " + i + " should point at a Setting");
        }

        [Test]
        public void TryGrowList_StructElements_CreatesStructs()
        {
            var list = new List<Vec>();
            string error;
            bool ok = PathAutoCreate.TryGrowList(list, Prop<Settings>("Points"), typeof(Vec), 1,
                                                 new RecordingFactory(), out error);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(2, list.Count);
        }

        [Test]
        public void TryGrowList_CStringElements_CreatesEmpties()
        {
            var list = new List<string>();
            string error;
            bool ok = PathAutoCreate.TryGrowList(list, Prop<Settings>("Options"), typeof(string), 1,
                                                 new RecordingFactory(), out error);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(2, list.Count);
        }

        // An already-long-enough list must not be touched: growing it would append elements the
        // caller never asked for.
        [Test]
        public void TryGrowList_AlreadyLongEnough_LeavesItAlone()
        {
            var list = new List<PointerRef> { new PointerRef(new Setting()), new PointerRef(new Setting()) };
            string error;
            bool ok = PathAutoCreate.TryGrowList(list, Prop<Settings>("Items"), typeof(PointerRef), 0,
                                                 new RecordingFactory(), out error);
            Assert.IsTrue(ok, "error: " + error);
            Assert.AreEqual(2, list.Count);
        }

        // ---- wired into the path walker ----

        public sealed class Leaf
        {
            [EbxFieldMeta(Type = EbxFieldType.Pointer, BaseType = typeof(Criterion))]
            public PointerRef Child { get; set; }
        }

        public sealed class Outer
        {
            [EbxFieldMeta(Type = EbxFieldType.Pointer, BaseType = typeof(Leaf))]
            public PointerRef Inner { get; set; }
        }

        // The point of the whole feature: one path expression builds the chain it needs.
        [Test]
        public void SetValue_WithFactory_BuildsTheMissingChain()
        {
            var root = new Outer();
            PropertyPathWriter.SetValue(root, "Inner.Child.Name", "gated", new RecordingFactory());

            var inner = (Leaf)root.Inner.Internal;
            Assert.IsNotNull(inner);
            var child = (Criterion)inner.Child.Internal;
            Assert.IsNotNull(child);
            Assert.AreEqual("gated", child.Name);
        }

        // Without a factory the walker must behave exactly as before. Auto-creating by default would
        // turn a typo in a path into a new object nobody asked for.
        [Test]
        public void SetValue_WithoutFactory_StillThrowsOnAMissingHop()
        {
            var root = new Outer();
            Assert.Throws<InvalidOperationException>(
                () => PropertyPathWriter.SetValue(root, "Inner.Child.Name", "gated"));
        }

        // The leaf is never auto-created: that PointerRef is what `set-ref` overwrites, and replacing
        // it with a fresh object would silently discard the reference the caller is about to write.
        [Test]
        public void SetValue_LeafPointerRef_IsNotAutoCreated()
        {
            var root = new Outer();
            var factory = new RecordingFactory();
            // "Inner" alone is the leaf here, so nothing should be created for it.
            try { PropertyPathWriter.SetValue(root, "Inner", "whatever", factory); } catch { }
            Assert.AreEqual(0, factory.Created.Count);
        }

        [Test]
        public void ResolveList_WithFactory_CreatesTheMissingHop()
        {
            var root = new Outer2();
            IList list = PropertyPathWriter.ResolveList(root, "Inner.Items", new RecordingFactory());
            Assert.IsNotNull(list);
            Assert.AreEqual(0, list.Count);
        }

        public sealed class Outer2
        {
            [EbxFieldMeta(Type = EbxFieldType.Pointer, BaseType = typeof(Settings2))]
            public PointerRef Inner { get; set; }
        }

        public sealed class Settings2
        {
            [EbxFieldMeta(Type = EbxFieldType.Array, ArrayType = EbxFieldType.Pointer, BaseType = typeof(Setting))]
            public List<PointerRef> Items { get; set; } = new List<PointerRef>();
        }
    }
}
