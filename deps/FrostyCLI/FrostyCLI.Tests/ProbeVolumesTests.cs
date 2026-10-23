using System;
using System.Collections.Generic;
using System.Linq;
using FrostyCLI.Core.Enlighten;

namespace FrostyCLI.Tests
{
    public class ProbeVolumesTests
    {
        // set 0: default probe (static, sky); set 1: terrain grid over everything (non-static, bright);
        // set 2: scattered interior probes (static, dark) in a 10 m box; set 3: scattered set outdoors (as bright as the terrain)
        private static EnlightenDatabase Sample(out StaticEnlightenDatabase fls)
        {
            var db = new EnlightenDatabase();
            var probes = new List<float[]>();
            db.ProbeSets.Add(Set(true, new[] { -1000.0, -1000, -1000, 1000, 1000, 1000 }, 1, 1, 1, 0, 0, probes, new[] { 0.0, 900, 0 }, 3000));
            db.ProbeSets.Add(Set(false, new[] { -100.0, -10, -100, 100, 50, 100 }, 1, 1, 1, 0, 3, probes, new[] { 0.0, 40, 0 }, 20000));
            db.ProbeSets.Add(Set(true, new[] { 0.0, 0, 0, 10, 4, 10 }, 2, 1, 1, 0, 0, probes, new[] { 2.0, 1, 2, 8, 1, 8 }, 150));
            db.ProbeSets.Add(Set(true, new[] { 50.0, 0, 50, 60, 4, 60 }, 1, 1, 1, 0, 0, probes, new[] { 55.0, 1, 55 }, 19000));
            fls = new StaticEnlightenDatabase { Probes = new byte[probes.Count * 144] };
            for (int i = 0; i < probes.Count; i++)
                for (int f = 0; f < 36; f++) EnlightenDatabase.PutF(fls.Probes, i * 144 + f * 4, probes[i][f]);
            return db;
        }

        private static EnlightenDatabase.ProbeSet Set(bool isStatic, double[] box, uint dx, uint dy, uint dz, uint prio, float blend,
                                                       List<float[]> probes, double[] pos, float dc)
        {
            int n = pos.Length / 3;
            var ps = new EnlightenDatabase.ProbeSet { Positions = new byte[n * 16] };
            ps.Raw[0] = (byte)(probes.Count + 1); ps.Raw[1] = (byte)n; ps.Raw[2] = (byte)dc;
            for (int k = 0; k < 6; k++) EnlightenDatabase.PutF(ps.Raw, 0x90 + (k < 3 ? k : k + 1) * 4, (float)box[k]);
            EnlightenDatabase.PutF(ps.Raw, 0xB0, blend);
            EnlightenDatabase.Put(ps.Raw, 0xB4, dx); EnlightenDatabase.Put(ps.Raw, 0xB8, dy); EnlightenDatabase.Put(ps.Raw, 0xBC, dz);
            EnlightenDatabase.Put(ps.Raw, 0xC0, prio); EnlightenDatabase.Put(ps.Raw, 0xC4, (uint)n);
            ps.GlobalOffset = (uint)probes.Count;
            ps.Raw[0xCC] = (byte)(isStatic ? 1 : 0);
            for (int k = 0; k < n; k++)
            {
                ps.SetPosition(k, (float)pos[k * 3], (float)pos[k * 3 + 1], (float)pos[k * 3 + 2]);
                var c = new float[36]; c[0] = c[1] = c[2] = dc; probes.Add(c);
            }
            return ps;
        }

        [Test]
        public void DynamicLookupSkipsScatteredSets()
        {
            StaticEnlightenDatabase fls; var db = Sample(out fls);
            Assert.AreEqual(20000.0, ProbeVolumes.DynamicLookupDc(db, fls.Probes, 5, 1, 5), 1e-3);       // inside the interior box: terrain
            Assert.AreEqual(3000.0, ProbeVolumes.DynamicLookupDc(db, fls.Probes, 500, 1, 500), 1e-3);   // outside everything: default
        }

        [Test]
        public void InteriorSetBecomesAVolumeThatWins()
        {
            StaticEnlightenDatabase fls; var db = Sample(out fls);
            var plan = ProbeVolumes.Plan(db, fls.Probes, 0.25, 3, 2, 101, 1);
            Assert.AreEqual(2, plan.Count);                                                  // sets 2 and 3 (set 0 is the default)
            var interior = plan.Single(c => c.Set == 2); var outdoor = plan.Single(c => c.Set == 3);
            Assert.IsTrue(interior.Interior); Assert.IsFalse(outdoor.Interior);
            Assert.IsNull(outdoor.Volume);
            Assert.AreEqual(4u, interior.Volume.DimX); Assert.AreEqual(2u, interior.Volume.DimY); Assert.AreEqual(4u, interior.Volume.DimZ);
            Assert.AreEqual(101u, interior.Volume.Priority);
            Assert.IsFalse(ProbeVolumes.IsStatic(interior.Volume));
            int before = fls.ProbeCount;
            Assert.AreEqual(1, ProbeVolumes.Apply(db, fls, plan));
            Assert.AreEqual(before + 32, fls.ProbeCount);
            Assert.AreEqual((uint)before, db.ProbeSets[4].GlobalOffset);
            Assert.AreEqual(150.0, ProbeVolumes.DynamicLookupDc(db, fls.Probes, 5, 1, 5), 1e-2);        // now the interior's own light
            Assert.AreEqual(20000.0, ProbeVolumes.DynamicLookupDc(db, fls.Probes, 40, 1, 40), 1e-3);    // outdoors unchanged
            // idempotent: the derived guid is found, nothing planned twice
            var again = ProbeVolumes.Plan(db, fls.Probes, 0.25, 3, 2, 101, 1);
            Assert.IsTrue(again.Single(c => c.Set == 2).AlreadyPresent);
            Assert.AreEqual(0, ProbeVolumes.Apply(db, fls, again));
        }

        [Test]
        public void WrittenDatabasesParseBack()
        {
            StaticEnlightenDatabase fls; var db = Sample(out fls);
            ProbeVolumes.Apply(db, fls, ProbeVolumes.Plan(db, fls.Probes, 0.25, 3, 2, 101, 1));
            byte[] fm, fd = fls.Write(out fm);
            var back = StaticEnlightenDatabase.Parse(fd, fm);
            Assert.AreEqual(fls.ProbeCount, back.ProbeCount);
            Assert.AreEqual(db.ProbeSets.Sum(p => (int)p.Count), back.ProbeCount);
        }
    }
}
