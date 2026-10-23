using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class ReferenceClassTests
    {
        [Test]
        public void ReferenceClassFor_EffectBlueprint_IsEffectReference()
        {
            Assert.AreEqual("EffectReferenceObjectData", LevelCommand.ReferenceClassFor("EffectBlueprint", null));
            Assert.AreEqual("EffectReferenceObjectData", LevelCommand.ReferenceClassFor("EffectBlueprint", "ObjectReferenceObjectData"));
        }

        [Test]
        public void ReferenceClassFor_OtherBlueprints_KeepRequestedOrDefault()
        {
            Assert.AreEqual("SpatialPrefabReferenceObjectData", LevelCommand.ReferenceClassFor("ObjectBlueprint", null));
            Assert.AreEqual("ObjectReferenceObjectData", LevelCommand.ReferenceClassFor("ObjectBlueprint", "ObjectReferenceObjectData"));
        }
    }
}
