using System.Collections.Generic;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // A mod-added mesh whose shader block depot (<mesh>_mesh/blocks) is in no bundle the level loads draws nothing, silently:
    // `bundle add --with-resources` never reached the depot (it is found by name, not referenced), so 84 of the Hoth 2015
    // port's 85 geometry ports shipped invisible. bundle add now takes the depot along; level validate reports the rest.
    public class MeshDepotBundleTests
    {
        [Test]
        public void TheDepotIsTheMeshNameLowerCasedPlusMeshBlocks()
        {
            Assert.AreEqual("objects/ported/hoth2015/ioncannon_01/ioncannon_01_mesh_mesh/blocks",
                BundleCommand.DepotResName("Objects/Ported/Hoth2015/IonCannon_01/IonCannon_01_Mesh"));
            Assert.IsNull(BundleCommand.DepotResName(null));
            Assert.IsTrue(BundleCommand.IsMeshType("RigidMeshAsset"));
            Assert.IsTrue(BundleCommand.IsMeshType("CompositeMeshAsset"));
            Assert.IsFalse(BundleCommand.IsMeshType("TextureAsset"));
        }

        [Test]
        public void ADepotOutsideTheLoadedBundlesIsReported()
        {
            var loaded = new HashSet<int> { 7, 9 };
            Assert.IsNull(LevelCommand.DepotBundleProblem(new List<int> { 3, 9 }, "d", loaded));
            Assert.IsNotNull(LevelCommand.DepotBundleProblem(new List<int>(), "d", loaded));
            Assert.IsNotNull(LevelCommand.DepotBundleProblem(new List<int> { 3 }, "d", loaded));
            Assert.IsNotNull(LevelCommand.DepotBundleProblem((ICollection<int>)null, "d", loaded));
        }
    }
}
