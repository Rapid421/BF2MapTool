using System.Collections.Generic;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // `navmesh geometry` keeps only the meshes the game collides with: a light cone in a doorway is a render mesh with no
    // physics, and treating it as a wall closed every doorway of Tatooine's Blast arena (connected recall 48% -> 91%
    // once render-only meshes were left out).
    public class NavmeshGeometryTests
    {
        public class Body { }
        public class PhysicsComponent { public List<object> PhysicsBodies { get; set; } = new List<object>(); }
        public class PartComponent { public int PartIndex { get; set; } }
        public class StaticModel { public List<object> Components { get; set; } = new List<object>(); }
        public class Legacy { public object PhysicsData { get; set; } }

        [Test]
        public void AStaticModelWithPhysicsBodiesCollides()
        {
            var m = new StaticModel();
            m.Components.Add(new PartComponent());
            m.Components.Add(new PhysicsComponent { PhysicsBodies = { new Body() } });
            Assert.IsTrue(NavmeshCommand.HasCollision(m));
        }

        [Test]
        public void PartComponentsAloneAreRenderOnly()
        {
            var m = new StaticModel();                   // FX/Meshes/Lighting/Mesh_Light_Cone_00: two PartComponentData
            m.Components.Add(new PartComponent());
            m.Components.Add(new PartComponent());
            Assert.IsFalse(NavmeshCommand.HasCollision(m));
        }

        [Test]
        public void APhysicsComponentWithoutBodiesDoesNotCollide()
        {
            var m = new StaticModel();
            m.Components.Add(new PhysicsComponent());
            Assert.IsFalse(NavmeshCommand.HasCollision(m));
        }

        [Test]
        public void APhysicsDataReferenceCollides()
        {
            Assert.IsTrue(NavmeshCommand.HasCollision(new Legacy { PhysicsData = new Body() }));
            Assert.IsFalse(NavmeshCommand.HasCollision(new Legacy()));
        }

        public class Connection { public object Target { get; set; } public int TargetFieldId { get; set; } }
        public class Prefab { public List<object> PropertyConnections { get; set; } = new List<object>(); }

        [Test]
        public void ADoorLeafTheLogicMovesIsNotGround()
        {
            // pf_door_..._automatic: PF_AutomaticDoor_Core writes the leaf's Transform (hash 0x87525469) to slide it
            // open; the frame beside it is only switched on (Enabled) and stays ground
            var leaf = new StaticModel();
            var frame = new StaticModel();
            var door = new Prefab();
            door.PropertyConnections.Add(new Connection { Target = leaf, TargetFieldId = FrostySdk.Utils.HashString("Transform") });
            door.PropertyConnections.Add(new Connection { Target = frame, TargetFieldId = FrostySdk.Utils.HashString("Enabled") });
            HashSet<object> moved = NavmeshCommand.MovedObjects(door);
            Assert.IsTrue(moved.Contains(leaf));
            Assert.IsFalse(moved.Contains(frame));
            Assert.AreEqual(unchecked((int)0x87525469), FrostySdk.Utils.HashString("Transform"));
        }

        [Test]
        public void TransformsComposeChildThenParent()
        {
            // child: 90 degrees about Y, then +2 on x; parent: translate (10, 0, 5)
            float[] child = { 0, 0, -1, 0, 1, 0, 1, 0, 0, 2, 0, 0 };
            float[] parent = { 1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 5 };
            float[] world = NavmeshCommand.Mul(child, parent);
            double[] p = NavmeshCommand.Apply(world, new float[] { 1, 0, 0 });
            // (1,0,0) -> child right (0,0,-1) + (2,0,0) = (2,0,-1) -> parent (12,0,4)
            Assert.IsTrue(System.Math.Abs(p[0] - 12) < 1e-6 && System.Math.Abs(p[1]) < 1e-6 && System.Math.Abs(p[2] - 4) < 1e-6,
                "got " + p[0] + ", " + p[1] + ", " + p[2]);
        }
    }
}
