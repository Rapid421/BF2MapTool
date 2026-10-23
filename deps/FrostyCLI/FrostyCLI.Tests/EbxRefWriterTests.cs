using System;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class EbxRefWriterTests
    {
        // Stand-in PointerRef: a struct named PointerRef with Type + External,
        // mirroring the SDK shape. The production code matches by type NAME.
        public struct FakeImport { public Guid FileGuid; public Guid ClassGuid; }
        public struct PointerRef { public int Type; public FakeImport External; }

        public class Leaf    { public PointerRef Blueprint { get; set; } }
        public class Mid     { public Leaf Child { get; set; } }
        public class NotARef { public string Blueprint { get; set; } }

        [Test]
        public void SetRefByPath_DirectProperty_SetsExternalRef()
        {
            var obj = new Leaf();
            Guid fg = Guid.NewGuid(), cg = Guid.NewGuid();
            EbxRefWriter.SetRefByPath(obj, "Blueprint", fg, cg);
            Assert.AreEqual(2, obj.Blueprint.Type);
            Assert.AreEqual(fg, obj.Blueprint.External.FileGuid);
            Assert.AreEqual(cg, obj.Blueprint.External.ClassGuid);
        }

        [Test]
        public void SetRefByPath_NestedPath_SetsRef()
        {
            var obj = new Mid { Child = new Leaf() };
            Guid fg = Guid.NewGuid(), cg = Guid.NewGuid();
            EbxRefWriter.SetRefByPath(obj, "Child.Blueprint", fg, cg);
            Assert.AreEqual(fg, obj.Child.Blueprint.External.FileGuid);
        }

        [Test]
        public void SetRefByPath_LeafNotPointerRef_Throws()
        {
            var obj = new NotARef();
            Assert.Throws<InvalidOperationException>(
                () => EbxRefWriter.SetRefByPath(obj, "Blueprint", Guid.NewGuid(), Guid.NewGuid()));
        }
    }
}
