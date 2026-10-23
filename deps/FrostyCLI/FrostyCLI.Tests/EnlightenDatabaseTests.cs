using System;
using System.IO;
using System.Linq;
using FrostyCLI.Core.Enlighten;

namespace FrostyCLI.Tests
{
    public class EnlightenDatabaseTests
    {
        // A small END in the shipped layout: header, 3 mesh instances, 2 terrain records, uv transforms,
        // 2 probe sets (one with 2 positions, one empty), fixup table.
        private static byte[] SampleEnd(out byte[] meta)
        {
            var db = new EnlightenDatabase();
            EnlightenDatabase.Put(db.Header, 0, 1024); EnlightenDatabase.Put(db.Header, 4, 512);
            EnlightenDatabase.Put(db.Header, 8, 1); EnlightenDatabase.PutF(db.Header, 0xC, 1.0f);
            EnlightenDatabase.Put(db.Header, 0x10, 2); EnlightenDatabase.Put(db.Header, 0x14, 1);
            for (int i = 0; i < 16; i++) db.Header[0x18 + i] = (byte)(0x40 + i);
            for (uint k = 0; k < 3; k++)
            {
                var mi = new EnlightenDatabase.MeshInstance { Hash = 1000 + k * 77 };
                var uv = new byte[32];
                EnlightenDatabase.PutF(uv, 0, 0.01f * (k + 1)); EnlightenDatabase.PutF(uv, 12, 0.02f);
                EnlightenDatabase.PutF(uv, 16, 0.5f); EnlightenDatabase.PutF(uv, 20, 0.25f);
                mi.UvTransforms.Add(uv);
                db.MeshInstances.Add(mi);
            }
            for (int i = 0; i < 2; i++)
            {
                var t = new EnlightenDatabase.TerrainLightmap();
                t.Place(-1024 + 1024 * i, -1024, 1024 * i, 0, 256 * i, 0, 256, 1024, 512);
                db.Terrain.Add(t);
            }
            for (int i = 0; i < 2; i++)
            {
                var ps = new EnlightenDatabase.ProbeSet();
                uint n = i == 0 ? 2u : 0u;
                EnlightenDatabase.Put(ps.Raw, 0xC4, n);
                EnlightenDatabase.Put(ps.Raw, 0xB4, 1); EnlightenDatabase.Put(ps.Raw, 0xB8, 1); EnlightenDatabase.Put(ps.Raw, 0xBC, 1);
                ps.Positions = new byte[n * 16];
                for (int p = 0; p < n; p++) ps.SetPosition(p, p, 2 * p, 3 * p);
                db.ProbeSets.Add(ps);
            }
            return db.Write(out meta);
        }

        [Test]
        public void EndRoundTripsByteExact()
        {
            byte[] meta;
            byte[] data = SampleEnd(out meta);
            var db = EnlightenDatabase.Parse(data, meta);
            byte[] meta2;
            byte[] again = db.Write(out meta2);
            Assert.IsTrue(data.SequenceEqual(again), "payload differs");
            Assert.IsTrue(meta.SequenceEqual(meta2), "res meta differs");
            Assert.AreEqual(3, db.MeshInstances.Count);
            Assert.AreEqual(2, db.Terrain.Count);
            Assert.AreEqual(2, db.ProbeSets.Count);
            Assert.AreEqual(9u, BitConverter.ToUInt32(meta, 0));
        }

        [Test]
        public void EndLayoutMatchesTheShippedOrder()
        {
            byte[] meta;
            byte[] data = SampleEnd(out meta);
            // mesh instances right after the header, terrain records after them, uv transforms after the terrain
            Assert.AreEqual(0x70UL, BitConverter.ToUInt64(data, 0x30));
            Assert.AreEqual((ulong)(0x70 + 3 * 32), BitConverter.ToUInt64(data, 0x38));
            ulong firstUv = BitConverter.ToUInt64(data, 0x70 + 8);
            Assert.AreEqual((ulong)(0x70 + 3 * 32 + 2 * 48), firstUv);
            // fixup table: 0x30, 0x38, each instance +8, the probe set with positions +0xD0, then 0x50 last
            uint tableOff = BitConverter.ToUInt32(meta, 4), tableBytes = BitConverter.ToUInt32(meta, 8);
            var table = Enumerable.Range(0, (int)tableBytes / 4).Select(i => BitConverter.ToUInt32(data, (int)tableOff + 4 * i)).ToArray();
            Assert.AreEqual(0x30u, table[0]);
            Assert.AreEqual(0x38u, table[1]);
            Assert.AreEqual(0x78u, table[2]);
            Assert.AreEqual(0x50u, table[table.Length - 1]);
            Assert.AreEqual(3 + 1 + 3, table.Length);
        }

        [Test]
        public void TerrainTilePlacementMatchesTheShippedConvention()
        {
            // Tatooine tile 0: world -1024..-512 x -1024..-512 at texels (0..256, 524..780) of a 3264 x 3052 atlas
            var t = new EnlightenDatabase.TerrainLightmap();
            t.Place(-1024, -1024, -512, -512, 0, 524, 256, 3264, 3052);
            Assert.IsTrue(Math.Abs(t.ScaleU - 0.000152588f) < 1e-8f, "su " + t.ScaleU);
            Assert.IsTrue(Math.Abs(t.ScaleV - 0.000163187f) < 1e-8f, "sv " + t.ScaleV);
            Assert.IsTrue(Math.Abs(t.OffsetU - 0.156403f) < 1e-6f, "ou " + t.OffsetU);
            Assert.IsTrue(Math.Abs(t.OffsetV - 0.338958f) < 1e-6f, "ov " + t.OffsetV);
            int x0, x1, y0, y1;
            t.TexelRect(3264, 3052, out x0, out x1, out y0, out y1);
            Assert.AreEqual(0, x0); Assert.AreEqual(256, x1); Assert.AreEqual(524, y0); Assert.AreEqual(780, y1);
        }

        [Test]
        public void PlaceholderDatabasesStayRaw()
        {
            var bytes = new byte[] { 0xEF, 0xBE, 0xAD, 0xDE };
            var db = EnlightenDatabase.Parse(bytes, new byte[16]);
            Assert.IsNotNull(db.Unmodelled);
            byte[] meta;
            Assert.IsTrue(db.Write(out meta).SequenceEqual(bytes));
        }

        [Test]
        public void StaticDatabaseRoundTripsWithLightStates()
        {
            var s = new StaticEnlightenDatabase();
            EnlightenDatabase.Put(s.Header, 0, 64); EnlightenDatabase.Put(s.Header, 4, 32);
            s.Probes = new byte[3 * 144];
            for (int i = 0; i < s.Probes.Length; i++) s.Probes[i] = (byte)i;
            s.LightStates = new byte[5 * 8];
            byte[] meta;
            byte[] data = s.Write(out meta);
            var back = StaticEnlightenDatabase.Parse(data, meta);
            byte[] meta2;
            Assert.IsTrue(back.Write(out meta2).SequenceEqual(data));
            Assert.IsTrue(meta.SequenceEqual(meta2));
            Assert.AreEqual(3, back.ProbeCount);
            Assert.AreEqual(0u, (uint)(BitConverter.ToUInt32(meta, 4) % 16));
        }

        [Test]
        public void ShL1MatchesTheShaderForUniformAndDirectionalLight()
        {
            // no direction: flat
            Assert.IsTrue(Math.Abs(SkyModel.ShL1(0, 1, 0, 0, 0, 0) - 1) < 1e-9);
            // d = 0.5 up, normal up: computeShL1Lighting = 0.8929 (hand-evaluated from CommonLighting.hlsl:590)
            double v = SkyModel.ShL1(0, 1, 0, 0, 0.5, 0) * SkyModel.Y0;
            Assert.IsTrue(Math.Abs(v - 0.8929) < 1e-3, "got " + v);
            // energy: average over the sphere of normals is L0 (Y0)
            double sum = 0; int n = 0;
            for (int i = 0; i < 64; i++)
                for (int j = 0; j < 128; j++)
                {
                    double ct = -1 + (i + 0.5) / 32.0, st = Math.Sqrt(1 - ct * ct), ph = 2 * Math.PI * j / 128;
                    sum += SkyModel.ShL1(st * Math.Cos(ph), ct, st * Math.Sin(ph), 0.3, 0.6, 0.1); n++;
                }
            Assert.IsTrue(Math.Abs(sum / n - 1) < 1e-3, "mean " + sum / n);
        }

        [Test]
        public void HdrRoundTripKeepsEightBitMantissaPrecision()
        {
            var f = new float[] { 0, 0, 0, 1, 2, 3, 7970, 10530, 13732, 0.001f, 0.002f, 0.0005f };
            byte[] hdr = AtlasIo.WriteHdr(f, 4, 1);
            int w, h;
            float[] back = AtlasIo.ReadHdr(hdr, out w, out h);
            Assert.AreEqual(4, w); Assert.AreEqual(1, h);
            for (int i = 0; i < f.Length; i++)
            {
                double tol = Math.Max(f.Skip(i / 3 * 3).Take(3).Max() / 128.0, 1e-9);
                Assert.IsTrue(Math.Abs(back[i] - f[i]) <= tol, i + ": " + back[i] + " vs " + f[i]);
            }
        }

        [Test]
        public void GeometrySoupSkipsTheTerrainGroupAndHitsTheNearestTriangle()
        {
            string path = Path.Combine(Path.GetTempPath(), "frostycli-soup-test.obj");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "g terrain",
                "v -10 0 -10", "v 10 0 -10", "v 10 0 10", "v -10 0 10", "f 1 2 3 4",
                "g objects/house",
                "v 0 5 0", "v 1 5 0", "v 1 5 1", "v 0 5 1", "f 5 6 7 8",
            }));
            try
            {
                float nx, ny, nz;
                GeometrySoup soup = GeometrySoup.LoadObj(path);
                Assert.AreEqual(2, soup.TriangleCount);
                double up = soup.Cast(0.5, 1, 0.5, 0, 1, 0, 80, out nx, out ny, out nz);
                Assert.IsTrue(Math.Abs(up - 4) < 1e-6, "up " + up);
                Assert.IsTrue(ny < -0.99f, "the normal faces the ray: " + ny);
                Assert.IsTrue(soup.Cast(0.5, 1, 0.5, 0, -1, 0, 80, out nx, out ny, out nz) < 0, "the terrain group was not skipped");
                GeometrySoup all = GeometrySoup.LoadObj(path, skipTerrain: false);
                Assert.AreEqual(4, all.TriangleCount);
                Assert.IsTrue(Math.Abs(all.Cast(0.5, 1, 0.5, 0, -1, 0, 80, out nx, out ny, out nz) - 1) < 1e-6);
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void ProbeUnderUniformRadianceEvaluatesToThatRadianceEverywhere()
        {
            // a probe 2 m above flat ground; sky radiance L over the upper hemisphere, ground radiance L below:
            // E / pi = L for every normal, and the sky-visibility SH of an open horizon is (0.5, 0, 0.5, 0) (SPEC.md 3)
            const double L = 1234.5;
            var db = new EnlightenDatabase();
            var ps = new EnlightenDatabase.ProbeSet();
            EnlightenDatabase.Put(ps.Raw, 0xC4, 1u);
            EnlightenDatabase.Put(ps.Raw, 0xB4, 1); EnlightenDatabase.Put(ps.Raw, 0xB8, 1); EnlightenDatabase.Put(ps.Raw, 0xBC, 1);
            ps.Positions = new byte[16];
            ps.SetPosition(0, 0, 2, 0);
            db.ProbeSets.Add(ps);
            var fls = new StaticEnlightenDatabase { Probes = new byte[144] };
            var hf = new DenseHeightfield(3, -4096, -4096, 4096, new float[9]);
            double[,] T = SkyModel.LobeMatrix();
            double perLobe = 0;
            for (int p = 0; p < SkyModel.P; p++) perLobe += T[0, p];      // constant term of hat 0 per unit lobe weight
            var model = new SkyModel();
            for (int c = 0; c < 3; c++)
            {
                model.Kappa[c] = 0.16;
                model.W[c] = Enumerable.Repeat(L * model.Kappa[c] / perLobe, SkyModel.P).ToArray();
                model.Bounce[c] = new[] { L * model.Kappa[c], 0.0 };
            }
            int lit = ProbeRelight.Relight(db, fls, hf, null, model, new[] { 0f, 1f, 0f }, s => true);
            Assert.AreEqual(1, lit);
            foreach (var n in new[] { new[] { 0.0, 1, 0 }, new[] { 0.0, -1, 0 }, new[] { 1.0, 0, 0 }, new[] { 0.6, 0.8, 0 } })
                for (int ch = 0; ch < 3; ch++)
                {
                    double v = ProbeRelight.Evaluate(fls, 0, ch, n[0], n[1], n[2]);
                    Assert.IsTrue(Math.Abs(v / L - 1) < 0.01, string.Format("({0}, {1}, {2}) ch{3}: {4}", n[0], n[1], n[2], ch, v));
                }
            Assert.IsTrue(Math.Abs(fls.Coef(0, 0, 3) - 0.5) < 0.01 && Math.Abs(fls.Coef(0, 2, 3) - 0.5) < 0.01, "sky visibility " + fls.Coef(0, 0, 3) + ", " + fls.Coef(0, 2, 3));
            Assert.IsTrue(Math.Abs(fls.Coef(0, 1, 3)) < 1e-3 && Math.Abs(fls.Coef(0, 3, 3)) < 1e-3);
        }

        [Test]
        public void ResizeAtlasKeepsEveryTexelInPlace()
        {
            byte[] meta;
            var db = EnlightenDatabase.Parse(SampleEnd(out meta), meta);
            var before = db.Terrain.Select(t => { int a, b, c, d; t.TexelRect(db.Width, db.Height, out a, out b, out c, out d); return new[] { a, b, c, d }; }).ToList();
            var mi = db.MeshInstances[1];
            double u0 = (0.3 * mi.ScaleU() + mi.OffsetU()) * db.Width, v0 = (0.7 * mi.ScaleV() + mi.OffsetV()) * db.Height;
            db.ResizeAtlas(3072, 1536);
            Assert.AreEqual(3072u, db.Width); Assert.AreEqual(1536u, db.Height);
            for (int i = 0; i < db.Terrain.Count; i++)
            {
                int a, b, c, d; db.Terrain[i].TexelRect(db.Width, db.Height, out a, out b, out c, out d);
                Assert.IsTrue(new[] { a, b, c, d }.SequenceEqual(before[i]), "tile " + i + " moved");
            }
            double u1 = (0.3 * mi.ScaleU() + mi.OffsetU()) * db.Width, v1 = (0.7 * mi.ScaleV() + mi.OffsetV()) * db.Height;
            Assert.IsTrue(Math.Abs(u1 - u0) < 1e-3 && Math.Abs(v1 - v0) < 1e-3, "chart moved: " + u0 + "," + v0 + " -> " + u1 + "," + v1);
            byte[] meta2;
            var again = EnlightenDatabase.Parse(db.Write(out meta2), meta2);
            Assert.AreEqual(3072u, again.Width);
        }

        [Test]
        public void GrowDdsKeepsTheBlocksWhereTheyWere()
        {
            // 8 x 8 BC7 (DX10 header, dxgi 98): 2 x 2 blocks, block (bx, by) filled with 16*by + 4*bx + 1
            var dds = new byte[148 + 4 * 16];
            dds[0] = (byte)'D'; dds[1] = (byte)'D'; dds[2] = (byte)'S'; dds[3] = (byte)' ';
            BitConverter.GetBytes(124).CopyTo(dds, 4);
            BitConverter.GetBytes(8).CopyTo(dds, 12); BitConverter.GetBytes(8).CopyTo(dds, 16);
            BitConverter.GetBytes(0x30315844u).CopyTo(dds, 84);
            BitConverter.GetBytes(98u).CopyTo(dds, 128);
            for (int by = 0; by < 2; by++)
                for (int bx = 0; bx < 2; bx++)
                    for (int k = 0; k < 16; k++) dds[148 + (by * 2 + bx) * 16 + k] = (byte)(16 * by + 4 * bx + 1);
            byte[] big = AtlasIo.GrowDds(dds, 16, 12);
            int w, h; uint f;
            int off = AtlasIo.DdsDataOffset(big, out w, out h, out f);
            Assert.AreEqual(16, w); Assert.AreEqual(12, h); Assert.AreEqual(98u, f); Assert.AreEqual(148, off);
            Assert.AreEqual(148 + 4 * 3 * 16, big.Length);
            for (int by = 0; by < 3; by++)
                for (int bx = 0; bx < 4; bx++)
                {
                    byte expect = bx < 2 && by < 2 ? (byte)(16 * by + 4 * bx + 1) : (byte)0;
                    Assert.IsTrue(expect == big[off + (by * 4 + bx) * 16], "block " + bx + "," + by);
                }
        }

        [Test]
        public void LightmapKeyMatchesTheVerifiedFloat32Quantisation()
        {
            // reference values from the Mustafar tools' key_f32 (I:\MustafarLightmapHandover\tools\gi_end2.py), which
            // found every in-game key; 412.305 and -117.845 are the translations float64 rounding gets wrong
            var mesh = new Guid("f9691ab3-0962-7f15-bf0f-803097a043f4");
            double c = Math.Cos(0.7), s = Math.Sin(0.7);
            uint k1 = LightmapKey.Compute(mesh, new[] { c * 1.2, 0, -s * 1.2 }, new[] { 0, 1.2, 0 }, new[] { s * 1.2, 0, c * 1.2 },
                                          new[] { 412.305, 63.25, -117.845 });
            Assert.AreEqual(1725248153u, k1);
            uint k2 = LightmapKey.Compute(mesh, new[] { -1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.5, 2.25, -3.125 });
            Assert.AreEqual(337911659u, k2);
        }

        [Test]
        public void ChartRasterMapsTexelsToTheSurfaceUnderThem()
        {
            // a 1 m quad in the xz plane, uv = (x, z), placed at (10, 2, 5) turned 90 degrees about y (right = -z, forward = +x)
            var pos = new float[] { 0, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1 };
            var uv = new float[] { 0, 0, 1, 0, 1, 1, 0, 1 };
            var tris = new[] { 0, 2, 1, 0, 3, 2 };
            var cr = new ChartRaster(4, 4);
            cr.Rasterise(pos, uv, tris, new[] { 0.0, 0, -1 }, new[] { 0.0, 1, 0 }, new[] { 1.0, 0, 0 }, new[] { 10.0, 2, 5 });
            Assert.AreEqual(16, cr.Covered);
            // texel (1, 2): uv centre (0.375, 0.625) -> object (0.375, 0, 0.625) -> world (10 + 0.625, 2, 5 - 0.375)
            int i = 2 * 4 + 1;
            Assert.IsTrue(Math.Abs(cr.X[i] - 10.625) < 1e-9 && Math.Abs(cr.Y[i] - 2) < 1e-9 && Math.Abs(cr.Z[i] - 4.625) < 1e-9,
                cr.X[i] + ", " + cr.Y[i] + ", " + cr.Z[i]);
            // winding 0,2,1 / 0,3,2 gives (b - a) x (c - a) = +y: facing up
            Assert.IsTrue(cr.N[i * 3 + 1] > 0.99f, "normal y " + cr.N[i * 3 + 1]);
            // a chart with a hole: only the left half covered, one dilation ring fills the next column
            var half = new ChartRaster(8, 4);
            half.Rasterise(new float[] { 0, 0, 0, 0.5f, 0, 0, 0.5f, 0, 1, 0, 0, 1 }, new float[] { 0, 0, 0.5f, 0, 0.5f, 1, 0, 1 }, tris,
                new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 0 });
            Assert.AreEqual(16, half.Covered);
            half.Dilate(1);
            Assert.AreEqual(2, (int)half.State[1 * 8 + 4]);
            Assert.AreEqual(0, (int)half.State[1 * 8 + 6]);
            // a closed box: cross products point out
            var box = new float[] { 0,0,0, 1,0,0, 1,1,0, 0,1,0, 0,0,1, 1,0,1, 1,1,1, 0,1,1 };
            var boxTris = new[] { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,6,2, 3,7,6, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            Assert.IsTrue(ChartRaster.CrossPointsOut(box, boxTris));
            Assert.IsFalse(ChartRaster.CrossPointsOut(box, boxTris.Select((v, k) => k % 3 == 1 ? boxTris[k + 1] : k % 3 == 2 ? boxTris[k - 1] : v).ToArray()));
        }

        [Test]
        public void TerrainGridMatchesTheShippedLayoutAndRoundTrips()
        {
            byte[] meta;
            var db = EnlightenDatabase.Parse(SampleEnd(out meta), meta);
            var template = new EnlightenDatabase.ProbeSet();
            EnlightenDatabase.PutF(template.Raw, 0xB0, 3.0f);
            var ps = EnlightenDatabase.ProbeSet.TerrainGrid(template, 1024, -2048, 2048, -1024, 30, (x, z) => 60 + x / 1024.0, 2u, Guid.NewGuid());
            Assert.AreEqual(900u, ps.Count); Assert.AreEqual(2u, ps.GlobalOffset);
            Assert.IsTrue(ps.IsTerrainGrid);
            float x0, y0, z0, x1, y1, z1;
            ps.Position(0, out x0, out y0, out z0); ps.Position(899, out x1, out y1, out z1);
            Assert.IsTrue(x0 == 1024 && z0 == -2048 && x1 == 2048 && z1 == -1024, "corners");
            Assert.IsTrue(Math.Abs(y0 - 63) < 1e-4 && Math.Abs(y1 - 64) < 1e-4, "2 m above ground: " + y0 + ", " + y1);
            float px, py, pz; ps.Position(1, out px, out py, out pz);
            Assert.IsTrue(Math.Abs(px - (1024 + 1024 / 29.0)) < 1e-3 && pz == -2048, "x runs fastest");
            Assert.IsTrue(Math.Abs(ps.MinY - 53) < 1e-4 && Math.Abs(ps.MaxY - 94) < 1e-4, "aabb y " + ps.MinY + ".." + ps.MaxY);
            db.ProbeSets.Add(ps);
            byte[] meta2;
            var back = EnlightenDatabase.Parse(db.Write(out meta2), meta2);
            Assert.AreEqual(3, back.ProbeSets.Count);
            Assert.AreEqual(900u, back.ProbeSets[2].Count);
            back.ProbeSets[2].Position(899, out x1, out y1, out z1);
            Assert.IsTrue(x1 == 2048 && z1 == -1024);
        }

        [Test]
        public void SunRotationTurnsAzimuthFromXTowardsZ()
        {
            double x, z;
            TerrainRelight.RotateY(1, 0, Math.PI / 2, out x, out z);
            Assert.IsTrue(Math.Abs(x) < 1e-12 && Math.Abs(z - 1) < 1e-12, x + ", " + z);
            // the VE convention: sun = (sin X cos Y, sin Y, cos X cos Y); turning the sun by a is SunRotationX - a
            double X = 109.352 * Math.PI / 180, a = 30 * Math.PI / 180;
            double sx = Math.Sin(X), sz = Math.Cos(X), tx, tz;
            TerrainRelight.RotateY(sx, sz, a, out tx, out tz);
            Assert.IsTrue(Math.Abs(tx - Math.Sin(X - a)) < 1e-12 && Math.Abs(tz - Math.Cos(X - a)) < 1e-12);
        }

        [Test]
        public void NnlsKeepsWeightsNonNegative()
        {
            // unconstrained optimum of 1/2 x'Hx - g'x has a negative component
            var H = new double[,] { { 2, 1 }, { 1, 2 } };
            var g = new double[] { 3, -3 };
            double[] x = SkyModel.Nnls(H, g, ridge: 0);
            Assert.IsTrue(x[0] >= 0 && x[1] >= 0);
            Assert.IsTrue(Math.Abs(x[0] - 1.5) < 1e-6 && Math.Abs(x[1]) < 1e-9, x[0] + ", " + x[1]);
        }

        [Test]
        public void BakedSunIsTheVeSunSteepenedBySunShadowHeightScale()
        {
            // Tatooine's Sunset and Night VEs (SunShadowHeightScale 0.45) against the shipped shadow-extrusion directions
            foreach (var pair in new[] { new[] { 51.429, 15.5, 31.6 }, new[] { 310.0, 31.967, 54.2 } })
            {
                double X = pair[0] * Math.PI / 180, Y = pair[1] * Math.PI / 180;
                var ve = new[] { (float)(Math.Sin(X) * Math.Cos(Y)), (float)Math.Sin(Y), (float)(Math.Cos(X) * Math.Cos(Y)) };
                float[] b = FrostyCLI.Commands.EnlightenRelight.ScaledSun(ve, 0.45);
                double elev = Math.Asin(b[1]) * 180 / Math.PI;
                Assert.IsTrue(Math.Abs(elev - pair[2]) < 0.1, "elevation " + elev + " vs " + pair[2]);
                Assert.IsTrue(Math.Abs(Math.Atan2(b[0], b[2]) - Math.Atan2(ve[0], ve[2])) < 1e-5, "the azimuth is kept");
                Assert.IsTrue(Math.Abs(b[0] * b[0] + b[1] * b[1] + b[2] * b[2] - 1) < 1e-5);
            }
            float[] same = FrostyCLI.Commands.EnlightenRelight.ScaledSun(new[] { 0.6f, 0.64f, 0.48f }, 1.0);
            Assert.IsTrue(Math.Abs(same[1] - 0.64f) < 1e-6, "scale 1 leaves the sun alone");
        }

        [Test]
        public void PrefabChildTransformsComposeAsRowVectors()
        {
            // a child 1 m along the prefab's x, the prefab turned 90 degrees about +Y (right = -z) and placed at x = 10:
            // the child lands at (10, 0, -1) and its own right axis points along -z
            double[][] child = { new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 } };
            double[][] prefab = { new[] { 0.0, 0, -1 }, new[] { 0.0, 1, 0 }, new[] { 1.0, 0, 0 }, new[] { 10.0, 0, 0 } };
            double[][] w = FrostyCLI.Commands.EnlightenKeys.Compose(child, prefab);
            Assert.IsTrue(Math.Abs(w[3][0] - 10) < 1e-12 && Math.Abs(w[3][1]) < 1e-12 && Math.Abs(w[3][2] + 1) < 1e-12, string.Join(",", w[3]));
            Assert.IsTrue(Math.Abs(w[0][2] + 1) < 1e-12 && Math.Abs(w[2][0] - 1) < 1e-12);
            // scale on the prefab scales the child's offset and axes
            prefab[0] = new[] { 0.0, 0, -2 }; prefab[1] = new[] { 0.0, 2, 0 }; prefab[2] = new[] { 2.0, 0, 0 };
            w = FrostyCLI.Commands.EnlightenKeys.Compose(child, prefab);
            Assert.IsTrue(Math.Abs(w[3][2] + 2) < 1e-12 && Math.Abs(w[1][1] - 2) < 1e-12);
        }

        static System.Collections.Generic.Dictionary<string, string> Fields(params string[] kv)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        [Test]
        public void LightUnitsFollowTheFluxConversion()
        {
            string why;
            // punctual sphere: I = power / 4 pi; 2 m straight below, radius 10: E = I / 4 x (1 - (4/100)^2)^2
            LocalLight s = LocalLight.FromRecord("PbrSphereLightEntityData", Fields("Intensity", "1000", "Color", "1,0.5,0", "AttenuationRadius", "10",
                "LightUnit", "LightUnitType_LuminousPower", "Enabled", "True", "AffectRadiosity", "True"), new[] { 0f, 2f, 0f }, new[] { 0f, 0f, 1f }, out why);
            Assert.IsTrue(s != null, why);
            Assert.IsTrue(Math.Abs(s.I[0] - 1000 / (4 * Math.PI)) < 1e-9 && Math.Abs(s.I[1] - 500 / (4 * Math.PI)) < 1e-9 && s.I[2] == 0);
            var e = new double[3];
            s.Illuminance(0, 0, 0, 0, 1, 0, e);
            double win = 1 - 0.04 * 0.04;
            Assert.IsTrue(Math.Abs(e[0] - 1000 / (4 * Math.PI) / 4 * win * win) < 1e-9, "E " + e[0]);
            e[0] = e[1] = e[2] = 0;
            s.Illuminance(0, 0, 11, 0, 1, 0, e);
            Assert.IsTrue(e[0] == 0, "outside the attenuation radius");
            // punctual spot: I = power / pi along -forward (forward up = shining down, as Tatooine's ceiling lamps); full
            // cone 60 degrees, no inner cone: 20 degrees off the axis gets ((cos 20 - cos 30) / (1 - cos 30))^2 of it; 40
            // degrees off gets nothing, and nothing lands behind the light
            LocalLight p = LocalLight.FromRecord("PbrSpotLightEntityData", Fields("Intensity", "1000", "Color", "1,1,1", "AttenuationRadius", "100",
                "OuterAngle", "60", "InnerAngle", "0"), new[] { 0f, 10f, 0f }, new[] { 0f, 1f, 0f }, out why);
            Assert.IsTrue(p != null && p.Spot, why);
            Assert.IsTrue(Math.Abs(p.I[0] - 1000 / Math.PI) < 1e-9);
            double off = 20 * Math.PI / 180, d = 10 / Math.Cos(off);
            e[0] = e[1] = e[2] = 0;
            p.Illuminance(10 * Math.Tan(off), 0, 0, 0, 1, 0, e);
            double f = (Math.Cos(off) - Math.Cos(Math.PI / 6)) / (1 - Math.Cos(Math.PI / 6));
            double q = d * d / 1e4;
            double expect = 1000 / Math.PI / (d * d) * Math.Cos(off) * f * f * (1 - q * q) * (1 - q * q);
            Assert.IsTrue(Math.Abs(e[0] / expect - 1) < 1e-9, e[0] + " vs " + expect);
            e[0] = 0;
            p.Illuminance(10 * Math.Tan(40 * Math.PI / 180), 0, 0, 0, 1, 0, e);
            Assert.IsTrue(e[0] == 0, "outside the cone");
            p.Illuminance(0, 20, 0, 0, -1, 0, e);
            Assert.IsTrue(e[0] == 0, "behind the light");
            // luminance unit: the intensity is the surface luminance; a spot with no disc is promoted to a 1 cm area disc
            LocalLight lum = LocalLight.FromRecord("PbrSpotLightEntityData", Fields("Intensity", "7", "LightUnit", "LightUnitType_Luminance", "AttenuationRadius", "5",
                "DiscRadius", "0"), new[] { 0f, 0f, 0f }, null, out why);
            Assert.IsTrue(lum != null && Math.Abs(lum.I[0] - 7 * Math.PI * 1e-4) < 1e-12, why);
            // lights that bake nothing are refused with a reason
            Assert.IsTrue(LocalLight.FromRecord("PbrSphereLightEntityData", Fields("Intensity", "10", "AttenuationRadius", "5", "AffectRadiosity", "False"),
                new[] { 0f, 0f, 0f }, null, out why) == null && why == "AffectRadiosity off");
            Assert.IsTrue(LocalLight.FromRecord("PbrSphereLightEntityData", Fields("Intensity", "10", "AttenuationRadius", "5", "Enabled", "False"),
                new[] { 0f, 0f, 0f }, null, out why) == null && why == "disabled");
        }

        static GeometrySoup Obj(string path, params string[] lines)
        {
            File.WriteAllText(path, string.Join("\n", lines));
            return GeometrySoup.LoadObj(path);
        }

        [Test]
        public void LampPhotonsConserveEnergyInAClosedBox()
        {
            // a 4 m box around an isotropic 1000 lm light (the terrain far below): every photon's first hit is a wall (the
            // runtime's direct light, not stored), then albedo 0.5 per bounce - two bounces deposit power x (0.5 + 0.25)
            string path = Path.Combine(Path.GetTempPath(), "frostycli-photon-box.obj");
            try
            {
                GeometrySoup box = Obj(path, "g objects/box",
                    "v -2 0 -2", "v 2 0 -2", "v 2 0 2", "v -2 0 2", "v -2 4 -2", "v 2 4 -2", "v 2 4 2", "v -2 4 2",
                    "f 1 2 3 4", "f 5 6 7 8", "f 1 2 6 5", "f 2 3 7 6", "f 3 4 8 7", "f 4 1 5 8");
                var hf = new DenseHeightfield(65, -32, -32, 1, Enumerable.Repeat(-100f, 65 * 65).ToArray());
                string why;
                LocalLight l = LocalLight.FromRecord("PbrSphereLightEntityData", Fields("Intensity", "1000", "AttenuationRadius", "1000"),
                    new[] { 0f, 2f, 0f }, null, out why);
                var alb = new[] { 0.5, 0.5, 0.5 };
                LampPhotons ph = LampPhotons.Trace(new System.Collections.Generic.List<LocalLight> { l }, hf, box, alb, 20000, 2);
                double[] tot = ph.TotalDeposited();
                double expect = 1000 * 0.75;       // power / 4 pi x 4 pi x (rho + rho^2)
                Assert.IsTrue(Math.Abs(tot[0] / expect - 1) < 0.03, "deposited " + tot[0] + " vs " + expect);
                Assert.IsTrue(ph.TerrainDeposits == 0 && ph.MeshDeposits > 0, "all deposits on the box");
                // a wall's centre reads about the mean deposited irradiance: total / the box's 96 m2
                var w = new double[3]; var d = new double[9]; var e = new double[3];
                Assert.IsTrue(ph.Gather(2, 2, 0, -1, 0, 0, false, w, d, e), "photons around the wall centre");
                double mean = tot[0] / 96;
                Assert.IsTrue(e[0] > 0.6 * mean && e[0] < 1.6 * mean, "wall irradiance " + e[0] + " vs mean " + mean);
                Assert.IsTrue(w[0] >= e[0], "the radiance integral is at least the irradiance (1 / cos)");
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void LampPhotonsCarryABeamsBounceToTheWallBesideIt()
        {
            // a narrow spot 3 m up shining straight down (forward = up) 1 m in front of a wall: the hot spot on the ground
            // bounces onto the wall, which a chart texel there reads coming from below and in front (-x, -y)
            string path = Path.Combine(Path.GetTempPath(), "frostycli-photon-wall.obj");
            try
            {
                GeometrySoup wall = Obj(path, "g objects/wall", "v 2 0 -5", "v 2 0 5", "v 2 6 5", "v 2 6 -5", "f 1 2 3 4");
                var hf = new DenseHeightfield(65, -32, -32, 1, new float[65 * 65]);
                string why;
                LocalLight spot = LocalLight.FromRecord("PbrSpotLightEntityData", Fields("Intensity", "4000", "AttenuationRadius", "20",
                    "OuterAngle", "20", "InnerAngle", "10"), new[] { 1f, 3f, 0f }, new[] { 0f, 1f, 0f }, out why);
                Assert.IsTrue(spot != null, why);
                LampPhotons ph = LampPhotons.Trace(new System.Collections.Generic.List<LocalLight> { spot }, hf, wall, new[] { 0.5, 0.5, 0.5 }, 20000, 2);
                Assert.IsTrue(ph.MeshDeposits > 0, "bounced light reaches the wall");
                var w = new double[3]; var d = new double[9]; var e = new double[3];
                Assert.IsTrue(ph.Gather(2, 0.8, 0, -1, 0, 0, false, w, d, e), "the wall near the ground reads photons");
                Assert.IsTrue(d[0] < 0 && d[1] < 0, "arriving from the lit ground in front: " + d[0] + ", " + d[1]);
                // flat ground far from the wall sees nothing bounce back onto it
                Assert.IsTrue(!ph.Gather(-20, 0, 0, 0, 1, 0, true, w, d, e), "no light far away");
                // the model turns the gathered radiance into L0 with kappa
                var m = TerrainRelight.Moments(hf, new[] { 1.5 }, new[] { 0.0 }, new[] { new[] { 0f, 1f, 0f } }, 0.3, wall);
                TerrainRelight.LampMoments(m, hf, new[] { 1.5 }, new[] { 0.0 }, ph);
                var model = new SkyModel();
                for (int c = 0; c < 3; c++) { model.W[c] = new double[SkyModel.P]; model.Bounce[c] = new[] { 0.0, 0.0 }; model.LightBounce[c] = 0.18; }
                var l0 = new float[3]; var dd = new float[9];
                model.Predict(m, l0, dd);
                Assert.IsTrue(Math.Abs(l0[0] - 0.18 * m.X0[0]) < 1e-4 * Math.Max(l0[0], 1e-6), "L0 " + l0[0] + " vs " + 0.18 * m.X0[0]);
            }
            finally { File.Delete(path); }
        }
    }
}
