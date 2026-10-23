using System;
using System.Collections.Generic;
using System.Linq;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // The pure pieces of `navmesh build --collision --tile-size`: tile stitching, the adaptive terrain mesher, the
    // convex hull and the transform arithmetic the Havok shape walk uses, and the collision-layer filter.
    public class NavmeshCollisionTests
    {
        private static NavTileStitch.Tile Tile(int[][] verts, params int[][] polys)
        {
            var t = new NavTileStitch.Tile();
            t.Verts.AddRange(verts);
            t.Polys.AddRange(polys);
            return t;
        }

        private static Dictionary<long, int> SegmentUse(NavTileStitch.Result r)
        {
            var use = new Dictionary<long, int>();
            foreach (int[] p in r.Polys)
                for (int k = 0; k < p.Length; k++)
                {
                    int a = p[k], b = p[(k + 1) % p.Length];
                    long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    int n;
                    use[key] = use.TryGetValue(key, out n) ? n + 1 : 1;
                }
            return use;
        }

        private static NavTileStitch.Tile Left(int y)
        {
            return Tile(new[] { new[] { 0, y, 0 }, new[] { 4, y, 0 }, new[] { 4, y, 4 }, new[] { 0, y, 4 } }, new[] { 0, 1, 2, 3 });
        }

        private static NavTileStitch.Tile RightSplit(int y)
        {
            return Tile(new[] { new[] { 4, y, 0 }, new[] { 8, y, 0 }, new[] { 8, y, 2 }, new[] { 4, y, 2 }, new[] { 8, y, 4 }, new[] { 4, y, 4 } },
                        new[] { 0, 1, 2, 3 }, new[] { 3, 2, 4, 5 });
        }

        // DICE stores every area with a negative plan signed area (sum of x[i] z[i+1] - x[i+1] z[i]; all 44,397 areas of
        // the four Hoth_01 streams): the order that gives a +Y normal from the first two corners and the basis vertex.
        // navgen once wrote Recast's polygons reversed, every Hoth 2015 area came out the other way round, and the game
        // stalled in its area-edge walk (GameSimulation watchdog, game+0x78A2C43). Corners on one line have no normal.
        [Test]
        public void NavPowerOrderMatchesDicesWindingAndDropsFlatPolygons()
        {
            var V = new List<int[]> { new[] { 0, 5, 0 }, new[] { 4, 5, 0 }, new[] { 4, 5, 4 }, new[] { 0, 5, 4 }, new[] { 8, 5, 0 } };
            int[] positive = { 0, 1, 2, 3 };            // x z: (0,0) (4,0) (4,4) (0,4)
            Assert.IsTrue(NavTileStitch.SignedPlanArea2(positive, V) > 0);
            int[] fixedOrder = NavTileStitch.NavPowerOrder(positive, V);
            Assert.IsTrue(NavTileStitch.SignedPlanArea2(fixedOrder, V) < 0, "reversed into DICE's order");
            Assert.IsTrue(fixedOrder.SequenceEqual(new[] { 3, 2, 1, 0 }), "the same corners, reversed");
            int[] dice = { 3, 2, 1, 0 };
            Assert.IsTrue(ReferenceEquals(dice, NavTileStitch.NavPowerOrder(dice, V)), "already in DICE's order: kept as is");
            Assert.IsNull(NavTileStitch.NavPowerOrder(new[] { 0, 1, 4 }, V));   // three corners on z = 0: no plan area
        }

        // Every shipped area is flat (all corners on the plane through corners 0, 1 and the basis vertex); Recast
        // polygons follow the ground. PlanarPieces keeps a flat polygon whole, splits any other into flat convex pieces
        // with no new corner (every boundary segment survives), and drops a corner stacked over another (vertical edge).
        [Test]
        public void PlanarPiecesKeepsFlatPolygonsAndSplitsWarpedOnes()
        {
            // plan square (0,0) (0,10) (10,10) (10,0) in DICE's order (negative signed area), heights in cells of 0.1 m
            var V = new List<int[]> { new[] { 0, 0, 0 }, new[] { 0, 0, 10 }, new[] { 10, 0, 10 }, new[] { 10, 0, 0 },
                                      new[] { 10, 30, 10 }, new[] { 5, 0, 0 }, new[] { 5, 20, 0 } };
            int[] flat = { 0, 1, 2, 3 };
            Assert.IsTrue(NavTileStitch.SignedPlanArea2(flat, V) < 0);
            List<int[]> one = NavTileStitch.PlanarPieces(flat, V, 1.0, 0.1, 0.02);
            Assert.AreEqual(1, one.Count);
            Assert.AreEqual(4, one[0].Length);

            int[] warped = { 0, 1, 4, 3 };                              // one corner 3 m up
            List<int[]> parts = NavTileStitch.PlanarPieces(warped, V, 1.0, 0.1, 0.02);
            Assert.AreEqual(2, parts.Count);
            foreach (int[] part in parts)
            {
                Assert.IsTrue(NavTileStitch.SignedPlanArea2(part, V) < 0, "every piece keeps DICE's order and has area");
                Assert.IsTrue(NavTileStitch.Flat(part, V, 1.0, 0.1, 0.02), "every piece is flat");
            }
            var segments = new HashSet<string>();
            foreach (int[] part in parts)
                for (int k = 0; k < part.Length; k++) segments.Add(part[k] + ">" + part[(k + 1) % part.Length]);
            for (int k = 0; k < warped.Length; k++)
                Assert.IsTrue(segments.Contains(warped[k] + ">" + warped[(k + 1) % warped.Length]), "boundary segment kept");

            // a T-junction corner (5,0) on the edge (10,0)-(0,0) and a corner stacked over it 2 m up: the stacked one goes
            int[] stacked = { 0, 1, 2, 3, 6, 5 };
            List<int[]> cleaned = NavTileStitch.PlanarPieces(stacked, V, 1.0, 0.1, 0.02);
            Assert.AreEqual(1, cleaned.Count);
            Assert.IsTrue(!cleaned[0].Contains(6) && cleaned[0].Contains(5), "the corner off the floor is dropped");
        }

        [Test]
        public void StitchSplitsABorderEdgeAtTheNeighboursVertex()
        {
            // tile 0 (x 0..4) is one quad; tile 1 (x 4..8) is two quads split at z 2: the seam x = 4 has a T-junction
            NavTileStitch.Result r = NavTileStitch.Stitch(new[] { Left(10), RightSplit(10) }, 4, 4, 3);
            Assert.AreEqual(3, r.Polys.Count);
            Assert.IsTrue(r.Polys[0].Length == 5, "tile 0's quad takes the seam vertex (4, 2)");
            Assert.AreEqual(1, r.Inserted);
            Dictionary<long, int> use = SegmentUse(r);
            Assert.IsTrue(use.Values.All(n => n <= 2), "manifold");
            Assert.IsTrue(use.Values.Count(n => n == 2) == 3, "two seam segments + the inner edge of tile 1 are shared");
        }

        [Test]
        public void StitchDoesNotJoinFloorsOfDifferentHeights()
        {
            // the same layout with tile 1 two metres (20 cells) higher: a ledge over the seam, not one floor
            NavTileStitch.Result r = NavTileStitch.Stitch(new[] { Left(10), RightSplit(30) }, 4, 4, 3);
            Assert.AreEqual(0, r.Inserted);
            Assert.AreEqual(4, r.Polys[0].Length);
            Assert.AreEqual(0, r.PairedEdges);
        }

        [Test]
        public void StitchUsesRealSpanHeightsOverStraightEdges()
        {
            // the straight edges agree in height, but the regions' real spans along the seam are 2 m apart (two layers)
            NavTileStitch.Tile a = Left(10), b = RightSplit(10);
            a.EdgeHeights[(0L << 8) | 1] = new[] { 10, 10, 10, 10 };        // tile 0's edge (4,0)-(4,4)
            b.EdgeHeights[(0L << 8) | 3] = new[] { 30, 30 };                // tile 1's lower quad edge (4,2)-(4,0)
            b.EdgeHeights[(1L << 8) | 3] = new[] { 30, 30 };                // upper quad edge (4,4)-(4,2)
            NavTileStitch.Result r = NavTileStitch.Stitch(new[] { a, b }, 4, 4, 3);
            Assert.AreEqual(0, r.PairedEdges);
            Assert.AreEqual(0, r.Inserted);
        }

        [Test]
        public void StitchJoinsOnlyTheRunOfColumnsWhereTheFloorsMeet()
        {
            // one seam edge each side over z 0..8; tile 0's region is a floor at 10 for z 0..4 and a ramp top at 30 for
            // z 4..8 (its straight edge runs 10 -> 30), tile 1's floor is at 10 throughout: only z 0..4 is one floor
            var a = Tile(new[] { new[] { 0, 10, 0 }, new[] { 4, 10, 0 }, new[] { 4, 30, 8 }, new[] { 0, 30, 8 } }, new[] { 0, 1, 2, 3 });
            var b = Tile(new[] { new[] { 4, 10, 0 }, new[] { 8, 10, 0 }, new[] { 8, 10, 8 }, new[] { 4, 10, 8 } }, new[] { 0, 1, 2, 3 });
            a.EdgeHeights[(0L << 8) | 1] = new[] { 10, 10, 10, 10, 30, 30, 30, 30 };
            b.EdgeHeights[(0L << 8) | 3] = new[] { 10, 10, 10, 10, 10, 10, 10, 10 };
            NavTileStitch.Result r = NavTileStitch.Stitch(new[] { a, b }, 4, 4, 3);
            Assert.IsTrue(r.PairedEdges == 1, "paired " + r.PairedEdges);
            Assert.IsTrue(r.Polys[0].Length == 5 && r.Polys[1].Length == 5, "both quads take the run's end at z 4");
            Dictionary<long, int> use = SegmentUse(r);
            Assert.IsTrue(use.Values.Count(n => n == 2) == 1, "exactly the segment z 0..4 is shared");
            Assert.IsTrue(use.Values.All(n => n <= 2), "manifold");
        }

        [Test]
        public void StitchDropsASliverUnderALargerPolygonOfTheSameTile()
        {
            // each tile: a big quad and a 1-cell sliver inside it, both touching the seam x = 4 at z 1..2 (Recast removed
            // the border vertices between them, so the big quad's seam edge runs over the sliver)
            var a = Tile(new[] { new[] { 0, 10, 0 }, new[] { 4, 10, 0 }, new[] { 4, 10, 1 }, new[] { 4, 10, 2 }, new[] { 4, 10, 4 }, new[] { 0, 10, 4 }, new[] { 3, 10, 2 }, new[] { 3, 10, 1 } },
                         new[] { 0, 1, 2, 3, 4, 5 }, new[] { 7, 2, 3, 6 });
            var b = Tile(new[] { new[] { 4, 10, 0 }, new[] { 8, 10, 0 }, new[] { 8, 10, 4 }, new[] { 4, 10, 4 }, new[] { 4, 10, 2 }, new[] { 4, 10, 1 }, new[] { 5, 10, 1 }, new[] { 5, 10, 2 } },
                         new[] { 0, 1, 2, 3, 4, 5 }, new[] { 5, 6, 7, 4 });
            NavTileStitch.Result r = NavTileStitch.Stitch(new[] { a, b }, 4, 4, 3);
            Assert.AreEqual(2, r.DroppedOverlaps);
            Assert.AreEqual(2, r.Polys.Count);
            Assert.IsTrue(SegmentUse(r).Values.All(n => n <= 2), "manifold after the slivers are dropped");
        }

        [Test]
        public void AdaptiveTerrainMergesFlatGroundAndKeepsHoles()
        {
            var verts = new List<float>(); var tris = new List<int>();
            // 16 m square at 1 m: flat at 5 m except a 2 m bump at (12, 12), and a hole cell at (2..3, 2..3) whose four
            // corner samples carry no ground
            TerrainNavMesher.Stats st = TerrainNavMesher.Build(
                (double x, double z, out double h) =>
                {
                    h = (Math.Abs(x - 12) < 0.1 && Math.Abs(z - 12) < 0.1) ? 7 : 5;
                    return !(x > 1.9 && x < 3.1 && z > 1.9 && z < 3.1);
                },
                (x, z) => x > 2 && x < 3 && z > 2 && z < 3,
                0, 0, 16, 16, 1.0, 0.05, 3, verts, tris);
            Assert.AreEqual(1, st.HoleCells);
            Assert.IsTrue(st.FilledPoints == 4, "the hole's corners take their neighbours' height");
            Assert.IsTrue(st.Cells < 64, "flat blocks merge (" + st.Cells + " cells)");
            for (int i = 1; i < verts.Count; i += 3) Assert.IsTrue(verts[i] == 5 || verts[i] == 7, "height " + verts[i] + " is not ground");
            for (int t = 0; t < tris.Count; t += 3)
            {
                int a = tris[t] * 3, b = tris[t + 1] * 3, c = tris[t + 2] * 3;
                double cx = (verts[a] + verts[b] + verts[c]) / 3, cz = (verts[a + 2] + verts[b + 2] + verts[c + 2]) / 3;
                Assert.IsFalse(cx > 2 && cx < 3 && cz > 2 && cz < 3, "a triangle covers the hole");
                double ux = verts[b] - verts[a], uz = verts[b + 2] - verts[a + 2], vx = verts[c] - verts[a], vz = verts[c + 2] - verts[a + 2];
                Assert.IsTrue(uz * vx - ux * vz > 0, "triangle " + t / 3 + " faces down");
            }
        }

        [Test]
        public void ConvexHullOfACubeHasTwelveOutwardTriangles()
        {
            var p = new List<double[]>();
            for (int c = 0; c < 8; c++) p.Add(new double[] { (c & 1) * 2 - 1, ((c >> 1) & 1) * 2 - 1, ((c >> 2) & 1) * 2 - 1 });
            p.Add(new double[] { 0, 0, 0 });                               // an interior point is ignored
            List<int[]> hull = ConvexHull3.Build(p);
            Assert.IsNotNull(hull);
            Assert.AreEqual(12, hull.Count);
            foreach (int[] t in hull)
            {
                double[] a = p[t[0]], b = p[t[1]], c = p[t[2]];
                double[] n = { (b[1] - a[1]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[1] - a[1]), (b[2] - a[2]) * (c[0] - a[0]) - (b[0] - a[0]) * (c[2] - a[2]), (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]) };
                double side = n[0] * (a[0] + b[0] + c[0]) + n[1] * (a[1] + b[1] + c[1]) + n[2] * (a[2] + b[2] + c[2]);
                Assert.IsTrue(side > 0, "inward face");
            }
            Assert.IsTrue(ConvexHull3.Build(new List<double[]> { new double[] { 0, 0, 0 }, new double[] { 1, 0, 0 }, new double[] { 0, 0, 1 }, new double[] { 1, 0, 1 } }) == null, "flat points span no volume");
        }

        [Test]
        public void InstanceRecordsScaleRotateThenTranslate()
        {
            // a 0x80 hknpShapeInstance: 90 degrees about Y, uniform scale 2, translation (10, 0, 5)
            var rec = new byte[0x80];
            float[] f = { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 10, 0, 5, 0, 2, 2, 2, 0 };
            for (int i = 0; i < f.Length; i++) Array.Copy(BitConverter.GetBytes(f[i]), 0, rec, i * 4, 4);
            double[] m = HavokPhysicsResource.InstanceTransform(rec, 0);
            double[] p = HavokPhysicsResource.Apply(m, new double[] { 1, 0, 0 });
            // x axis -> (0, 0, -1) * 2 -> + (10, 0, 5) = (10, 0, 3)
            Assert.IsTrue(Math.Abs(p[0] - 10) < 1e-6 && Math.Abs(p[1]) < 1e-6 && Math.Abs(p[2] - 3) < 1e-6, "got " + string.Join(", ", p));
            double[] world = HavokPhysicsResource.Compose(m, new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 100, 0 });
            double[] q = HavokPhysicsResource.Apply(world, new double[] { 1, 0, 0 });
            Assert.IsTrue(Math.Abs(q[1] - 100) < 1e-6 && Math.Abs(q[2] - 3) < 1e-6, "parent applied after the instance");
            Assert.AreEqual(42, HavokPhysicsResource.SpherePoints(new double[] { 0, 0, 0 }, 1).Count);
        }

        [Test]
        public void CharacterLayersBlockSoldiersOnly()
        {
            Assert.IsTrue(FrostyCLI.Commands.NavmeshCollisionLayers.Blocks("RigidBodyCollisionLayer_StaticLayer"));
            Assert.IsTrue(FrostyCLI.Commands.NavmeshCollisionLayers.Blocks("RigidBodyCollisionLayer_CharacterCollisionGeometryLayer"));
            Assert.IsFalse(FrostyCLI.Commands.NavmeshCollisionLayers.Blocks("RigidBodyCollisionLayer_BlockVehicleOnlyCollisionLayer"));
            Assert.IsFalse(FrostyCLI.Commands.NavmeshCollisionLayers.Blocks("RigidBodyCollisionLayer_DebrisLayer"));
            Assert.IsFalse(FrostyCLI.Commands.NavmeshCollisionLayers.Blocks("RigidBodyCollisionLayer_CharacterLayer"));
        }
    }
}
