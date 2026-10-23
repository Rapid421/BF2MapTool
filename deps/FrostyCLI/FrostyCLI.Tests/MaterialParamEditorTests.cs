using System;
using FrostyCLI.Adapters.Mesh;

namespace FrostyCLI.Tests
{
    public class MaterialParamEditorTests
    {
        [Test]
        public void TextureValueGuid_LiteralGuid_ParsedWithoutLookup()
        {
            Guid g = Guid.NewGuid();
            bool called = false;
            Guid? r = MaterialParamEditor.TextureValueGuid(g.ToString(), p => { called = true; return null; });
            Assert.AreEqual(g, r.Value);
            Assert.IsFalse(called);
        }

        [Test]
        public void TextureValueGuid_AssetPath_ResolvedThroughLookup()
        {
            Guid cls = Guid.NewGuid();
            Guid? r = MaterialParamEditor.TextureValueGuid("AddedContent/X/T_Test_C", p => p == "AddedContent/X/T_Test_C" ? cls : (Guid?)null);
            Assert.AreEqual(cls, r.Value);
        }

        [Test]
        public void TextureValueGuid_UnknownPathOrEmpty_Null()
        {
            Assert.IsTrue(MaterialParamEditor.TextureValueGuid("No/Such/Texture", p => null) == null);
            Assert.IsTrue(MaterialParamEditor.TextureValueGuid("", p => Guid.NewGuid()) == null);
        }
    }
}
