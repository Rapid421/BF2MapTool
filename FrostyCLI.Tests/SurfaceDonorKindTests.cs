using FrostyCLI.Adapters.Mesh;

namespace FrostyCLI.Tests
{
    // A donor section can only draw a port of its own kind. A rigid port drawn through a composite donor's permutation places
    // its vertices by part matrices a static-group rigid mesh never gets: HOTH_N4's hangar crates (BoxMedium_02 on
    // df9_01_cluster_01, BoxMedium_01_B on df9, LightGroundLit_03 on boxsmall_01_cluster) were invisible with their collision
    // intact, and ContainerHuge_01 on DICE's Container_L_01_Occluder stand-in likewise.
    public class SurfaceDonorKindTests
    {
        private static SurfaceDonors.Donor D(string mesh, string type)
        {
            return new SurfaceDonors.Donor { Mesh = mesh, MeshType = type, Occluder = SurfaceDonors.IsOccluderStandIn(mesh) };
        }

        [Test]
        public void ARigidPortTakesOnlyRigidDonors()
        {
            Assert.IsTrue(SurfaceDonors.Drawable(D("objects/props/boxsmall_01/boxsmall_01_mesh", "MeshType_Rigid"), false));
            Assert.IsFalse(SurfaceDonors.Drawable(D("gameplay/vehicles/stationary/df9/old/df9_01_cluster_01_mesh", "MeshType_Composite"), false));
            Assert.IsFalse(SurfaceDonors.Drawable(D("characters/x/x_mesh", "MeshType_Skinned"), false));
        }

        [Test]
        public void ACompositePortTakesRigidAndCompositeDonors()
        {
            Assert.IsTrue(SurfaceDonors.Drawable(D("objects/props/boxsmall_01/boxsmall_01_mesh", "MeshType_Rigid"), true));
            Assert.IsTrue(SurfaceDonors.Drawable(D("gameplay/vehicles/stationary/df9/old/df9_01_cluster_01_mesh", "MeshType_Composite"), true));
            Assert.IsFalse(SurfaceDonors.Drawable(D("characters/x/x_mesh", "MeshType_Skinned"), true));
        }

        [Test]
        public void OccluderStandInsAreNeverDonors()
        {
            Assert.IsTrue(SurfaceDonors.IsOccluderStandIn("objects/props/objectsets/_rebelalliance/container_l_01/container_l_01_occluder_mesh"));
            Assert.IsFalse(SurfaceDonors.IsOccluderStandIn("objects/props/objectsets/_rebelalliance/container_l_01/container_l_01_mesh"));
            Assert.IsFalse(SurfaceDonors.Drawable(D("objects/props/objectsets/_rebelalliance/container_l_01/container_l_01_occluder_mesh", "MeshType_Rigid"), false));
            Assert.IsFalse(SurfaceDonors.Drawable(D("objects/props/objectsets/_rebelalliance/container_l_01/container_l_01_occluder_mesh", "MeshType_Rigid"), true));
        }
    }
}
