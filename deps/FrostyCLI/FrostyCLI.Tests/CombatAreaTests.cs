using System.Collections.Generic;
using FrostyLevelTools;

namespace FrostyCLI.Tests
{
    // `level combat-area` / `level create --combat-area`: the play-area outline a level hands its game modes, and the
    // engine's inside rule (AreaTestUtilBase::isInsideArea: crossing test on x/z; a volume with Height > 0 also needs
    // centre.y < y <= centre.y + Height).
    public class CombatAreaTests
    {
        public class Vec3 { public float x { get; set; } public float y { get; set; } public float z { get; set; } }
        public class LinearTransform
        {
            public Vec3 right { get; set; } = new Vec3 { x = 1 };
            public Vec3 up { get; set; } = new Vec3 { y = 1 };
            public Vec3 forward { get; set; } = new Vec3 { z = 1 };
            public Vec3 trans { get; set; } = new Vec3();
        }
        public class VolumeVectorShapeData
        {
            public LinearTransform Transform { get; set; } = new LinearTransform();
            public List<Vec3> Points { get; set; } = new List<Vec3>();
            public bool IsClosed { get; set; }
            public float Height { get; set; }
        }

        [Test]
        public void TheLinkFieldHashIsTheOneShippedDataCarries()
        {
            // Tatooine TeamDeathmatch_Skirmish: LinkConnection SourceFieldId -1150572636 -> the Skirmish arena polygon
            Assert.AreEqual(-1150572636, FrostySdk.Utils.HashString("CombatArea"));
        }

        [Test]
        public void FieldsAreNamesOrRawPortIds()
        {
            Assert.AreEqual(-1150572636, CombatArea.PortId("CombatArea"));
            Assert.AreEqual(-1609661850, CombatArea.PortId("#-1609661850"));
            Assert.AreEqual(1432407218, CombatArea.PortId("1432407218"));
        }

        [Test]
        public void ARectBecomesACounterClockwiseOutline()
        {
            var p = CombatArea.ParsePolygon("rect:190,160,55,10");
            Assert.AreEqual(4, p.Count);
            Assert.AreEqual(55.0, p[0][0]); Assert.AreEqual(10.0, p[0][1]);
            Assert.AreEqual(190.0, p[2][0]); Assert.AreEqual(160.0, p[2][1]);
            Assert.IsTrue(CombatArea.SignedArea(p) > 0);
            Assert.AreEqual(135.0 * 150.0, CombatArea.SignedArea(p));
        }

        [Test]
        public void AClockwisePointListIsTurnedRound()
        {
            var p = CombatArea.ParsePolygon("0,0; 0,10; 10,10; 10,0");
            Assert.IsTrue(CombatArea.SignedArea(p) > 0);
        }

        [Test]
        public void DegenerateOutlinesAreRefused()
        {
            Assert.Throws<System.ArgumentException>(() => CombatArea.ParsePolygon("0,0;10,10"));
            Assert.Throws<System.ArgumentException>(() => CombatArea.ParsePolygon("0,0;5,5;10,10"));
            Assert.Throws<System.ArgumentException>(() => CombatArea.ParsePolygon("rect:5,5,5,20"));
            Assert.Throws<System.ArgumentException>(() => CombatArea.ParsePolygon("0,0,1;5,5;10,0"));
        }

        [Test]
        public void TheCrossingTestHandlesAConcaveOutline()
        {
            // an L: the notch (7, 7) is outside
            var l = CombatArea.ParsePolygon("0,0;10,0;10,5;5,5;5,10;0,10");
            Assert.IsTrue(CombatArea.Contains(l, 2, 2));
            Assert.IsTrue(CombatArea.Contains(l, 2, 8));
            Assert.IsTrue(CombatArea.Contains(l, 8, 2));
            Assert.IsFalse(CombatArea.Contains(l, 7, 7));
            Assert.IsFalse(CombatArea.Contains(l, -1, 5));
        }

        [Test]
        public void HeightBandsFollowTheEngineRule()
        {
            var p = CombatArea.ParsePolygon("rect:0,0,10,10");
            // Tatooine's Skirmish arena: y 57.81, Height 40 -> inside for 57.81 < y <= 97.81
            Assert.IsTrue(CombatArea.Inside(p, 57.81, 40, 5, 64, 5));
            Assert.IsFalse(CombatArea.Inside(p, 57.81, 40, 5, 57.81, 5));
            Assert.IsTrue(CombatArea.Inside(p, 57.81, 40, 5, 97.81, 5));
            Assert.IsFalse(CombatArea.Inside(p, 57.81, 40, 5, 98, 5));
            // Height 0: no vertical limit
            Assert.IsTrue(CombatArea.Inside(p, 57.81, 0, 5, -500, 5));
            Assert.IsTrue(CombatArea.Inside(p, 57.81, 0, 5, 5000, 5));
            Assert.IsFalse(CombatArea.Inside(p, 57.81, 0, 15, 64, 5));
        }

        [Test]
        public void EdgeDistanceIsToTheNearestSide()
        {
            var p = CombatArea.ParsePolygon("rect:0,0,100,50");
            Assert.AreEqual(3.0, CombatArea.EdgeDistance(p, 97, 20));
            Assert.AreEqual(5.0, CombatArea.EdgeDistance(p, 50, -5));
        }

        [Test]
        public void RewriteKeepsPointsLocalToTheShapeTransform()
        {
            var s = new VolumeVectorShapeData { Height = 40 };
            s.Transform.trans = new Vec3 { x = 100, y = 50, z = -20 };
            s.Points.Add(new Vec3 { x = 1, y = 7.81f, z = 1 });
            CombatArea.Rewrite(s, CombatArea.ParsePolygon("rect:55,10,190,160"), 60, 0);
            Assert.AreEqual(4, s.Points.Count);
            Assert.AreEqual(-45f, s.Points[0].x);   // 55 - 100
            Assert.AreEqual(10f, s.Points[0].y);    // 60 - 50
            Assert.AreEqual(30f, s.Points[0].z);    // 10 - (-20)
            Assert.AreEqual(0f, s.Height);
            Assert.IsTrue(s.IsClosed);
            var w = CombatArea.WorldPoints(s);
            Assert.AreEqual(190.0, w[2][0]); Assert.AreEqual(60.0, w[2][1]); Assert.AreEqual(160.0, w[2][2]);
        }

        [Test]
        public void RewriteUndoesARotatedTransform()
        {
            // yawed 90 degrees: right = +z, forward = -x
            var s = new VolumeVectorShapeData();
            s.Transform.right = new Vec3 { z = 1 };
            s.Transform.forward = new Vec3 { x = -1 };
            s.Transform.trans = new Vec3 { x = 10, y = 0, z = 10 };
            CombatArea.Rewrite(s, CombatArea.ParsePolygon("rect:0,0,20,20"), 5, null);
            var w = CombatArea.WorldPoints(s);
            Assert.AreEqual(0.0, System.Math.Round(w[0][0], 4)); Assert.AreEqual(5.0, System.Math.Round(w[0][1], 4)); Assert.AreEqual(0.0, System.Math.Round(w[0][2], 4));
            Assert.AreEqual(20.0, System.Math.Round(w[2][0], 4)); Assert.AreEqual(20.0, System.Math.Round(w[2][2], 4));
            Assert.AreEqual(0f, s.Height);            // null height leaves the shape's own
        }

        [Test]
        public void BoundsAreXThenZ()
        {
            var b = CombatArea.Bounds(CombatArea.ParsePolygon("68.6,23.6;175.5,23.6;175.5,145.3;68.6,145.3"));
            Assert.AreEqual(68.6, b[0]); Assert.AreEqual(175.5, b[1]); Assert.AreEqual(23.6, b[2]); Assert.AreEqual(145.3, b[3]);
        }
    }
}
