using System;
using System.Collections.Generic;
using System.IO;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // A bot that spawns off the navmesh, or on a piece of it joined to nothing else, stands still all round; the spawn
    // report after `navmesh build` has to tell those apart from spawns on the walked piece, and must not mistake a roof
    // over a spawn for where it stands.
    public class NavmeshSpawnReachTests
    {
        // main ground 10 x 10 at y 0 (two triangles), an island 2 x 2 at y 0 off to the side, a 4 x 4 roof at y 5 over
        // the main ground's corner
        private static NavmeshSpawnReach.Mesh Scene()
        {
            var m = new NavmeshSpawnReach.Mesh();
            void quad(double x0, double z0, double x1, double z1, double y)
            {
                int b = m.V.Count;
                m.V.Add(new[] { x0, y, z0 }); m.V.Add(new[] { x1, y, z0 }); m.V.Add(new[] { x1, y, z1 }); m.V.Add(new[] { x0, y, z1 });
                m.F.Add(new[] { b, b + 1, b + 2 });
                m.F.Add(new[] { b, b + 2, b + 3 });
            }
            quad(0, 0, 10, 10, 0);
            quad(20, 0, 22, 2, 0);
            quad(0, 0, 4, 4, 5);
            return m;
        }

        [Test]
        public void SpawnsAreSortedIntoMainIslandAndOff()
        {
            var r = NavmeshSpawnReach.Classify(Scene(), new List<double[]>
            {
                new[] { 5.0, 0.5, 5.0 },    // main ground
                new[] { 21.0, 0.5, 1.0 },   // the island
                new[] { 30.0, 0.0, 30.0 },  // nowhere
            });
            Assert.AreEqual(NavmeshSpawnReach.Where.Main, r.Spots[0].Class);
            Assert.AreEqual(NavmeshSpawnReach.Where.Island, r.Spots[1].Class);
            Assert.IsTrue(Math.Abs(r.Spots[1].IslandArea - 4.0) < 1e-9);
            Assert.AreEqual(NavmeshSpawnReach.Where.Off, r.Spots[2].Class);
            Assert.IsTrue(Math.Abs(r.Spots[2].Distance - Math.Sqrt(20 * 20 + 20 * 20)) < 1e-9);   // the main ground's (10, 10)
            Assert.AreEqual(3, r.Pieces);
            Assert.IsTrue(Math.Abs(r.MainArea - 100.0) < 1e-9);
        }

        [Test]
        public void ARoofOverASpawnIsNotWhereItStands()
        {
            var r = NavmeshSpawnReach.Classify(Scene(), new List<double[]>
            {
                new[] { 2.0, 0.5, 2.0 },    // under the roof: stands on the main ground
                new[] { 2.0, 5.5, 2.0 },    // on the roof: a piece of its own
            });
            Assert.AreEqual(NavmeshSpawnReach.Where.Main, r.Spots[0].Class);
            Assert.AreEqual(NavmeshSpawnReach.Where.Island, r.Spots[1].Class);
        }

        [Test]
        public void TheMainPieceIsWhereTheSpawnsAreNotTheLargest()
        {
            // an interior whose largest walkable piece is its roof (Kamino): 30 x 30 at y 20, no spawn on it
            var m = Scene();
            int b = m.V.Count;
            m.V.Add(new[] { -40.0, 20.0, -40.0 }); m.V.Add(new[] { -10.0, 20.0, -40.0 });
            m.V.Add(new[] { -10.0, 20.0, -10.0 }); m.V.Add(new[] { -40.0, 20.0, -10.0 });
            m.F.Add(new[] { b, b + 1, b + 2 });
            m.F.Add(new[] { b, b + 2, b + 3 });
            var r = NavmeshSpawnReach.Classify(m, new List<double[]>
            {
                new[] { 5.0, 0.5, 5.0 },
                new[] { 8.0, 0.5, 2.0 },
                new[] { 21.0, 0.5, 1.0 },   // the island
            });
            Assert.AreEqual(NavmeshSpawnReach.Where.Main, r.Spots[0].Class);
            Assert.AreEqual(NavmeshSpawnReach.Where.Main, r.Spots[1].Class);
            Assert.AreEqual(NavmeshSpawnReach.Where.Island, r.Spots[2].Class);
            Assert.IsTrue(Math.Abs(r.MainArea - 100.0) < 1e-9);
        }

        [Test]
        public void ObjFromNavgenReadsBack()
        {
            string p = Path.Combine(Path.GetTempPath(), "spawnreach_" + Guid.NewGuid().ToString("N") + ".obj");
            File.WriteAllText(p, "# navgen polys\nv 0 0 0\nv 10 0 0\nv 10 0 10\nv 0 0 10\nf 1 2 3 4\n");
            try
            {
                var m = NavmeshSpawnReach.ReadObj(p);
                Assert.AreEqual(4, m.V.Count);
                Assert.AreEqual(1, m.F.Count);
                var r = NavmeshSpawnReach.Classify(m, new List<double[]> { new[] { 5.0, 0.2, 5.0 } });
                Assert.AreEqual(NavmeshSpawnReach.Where.Main, r.Spots[0].Class);
                Assert.IsTrue(NavmeshSpawnReach.Describe(r).StartsWith("1 spawn(s): 1 on the main navmesh", StringComparison.Ordinal));
            }
            finally { File.Delete(p); }
        }
    }
}
