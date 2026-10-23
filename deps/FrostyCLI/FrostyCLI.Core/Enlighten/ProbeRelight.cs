using System;
using System.Threading.Tasks;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// Re-light the static database's SH probes (what dynamic objects -- soldiers, props placed with override Dynamic --
    /// are lit by) with a bake's fitted sky and the new ground, and move the terrain probe grids onto it.
    /// Convention (BFII_EXACT, SPEC.md §3): 9 float4 per probe, RGB = the cosine-convolved radiance divided by pi in the
    /// `simpleShL2` form (order L00, L11(x), L10(y), L1-1(z), L20, L21(xy), L2-1(yz), L2-2(xz), L22(x^2-z^2)), .w of the
    /// first four = the same for the sky-visibility function (open ground: 0.5, 0, 0.5, 0).
    /// </summary>
    public static class ProbeRelight
    {
        const int K = 32, M = 24;

        /// <summary>Probe positions of the terrain grids follow the new ground: y = ground + 2 (FIFA terrain probe builder).</summary>
        public static int MoveTerrainGrids(EnlightenDatabase db, DenseHeightfield hf, double above = 2.0)
        {
            int moved = 0;
            foreach (var ps in db.ProbeSets)
            {
                if (!ps.IsTerrainGrid || ps.Count == 0) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                for (int j = 0; j < ps.Count; j++)
                {
                    float x, y, z;
                    ps.Position(j, out x, out y, out z);
                    float g = hf.Sample(x, z);
                    ps.SetPosition(j, x, (float)(g + above), z);
                    lo = Math.Min(lo, g); hi = Math.Max(hi, g);
                    moved++;
                }
                // bounding box: min.y - 10 .. max.y + 30 around the probes (EnlightenTerrainSceneDescriptionBuilder)
                EnlightenDatabase.PutF(ps.Raw, 0x94, (float)(lo + above - 10));
                EnlightenDatabase.PutF(ps.Raw, 0xA4, (float)(hi + above + 30));
            }
            return moved;
        }

        /// <summary>
        /// Recompute the SH of every probe of the selected sets. radiance: sky from the fitted model (model units / kappa),
        /// bounce = TerrainColor x open-ground shading + TerrainColor x E_sun/pi x max(0, n_hit . s).
        /// </summary>
        public static int Relight(EnlightenDatabase db, StaticEnlightenDatabase fls, DenseHeightfield hf, GeometrySoup soup,
                                  SkyModel model, float[] sun, Func<EnlightenDatabase.ProbeSet, bool> select, double reach = 80.0,
                                  double[] skyScale = null, double[] bounceScale = null, int part = 0, double skyRotate = 0,
                                  LightField lights = null, double lightReach = 40.0, LampPhotons photons = null)
        {
            skyScale = skyScale ?? new[] { 1.0, 1.0, 1.0 };
            bounceScale = bounceScale ?? new[] { 1.0, 1.0, 1.0 };
            double[,] T = SkyModel.LobeMatrix();
            int F = TexelMoments.SkyFns;
            var w15 = new double[3, F];
            for (int c = 0; c < 3; c++)
                for (int b = 0; b < F; b++)
                {
                    double s = 0;
                    for (int p = 0; p < SkyModel.P; p++) s += T[b, p] * model.W[c][p];
                    w15[c, b] = s / model.Kappa[c];              // physical radiance per basis unit
                }
            var bounceSky = new double[3]; var bounceSun = new double[3]; var bounceLamp = new double[3]; var meshLamp = new double[3];
            for (int c = 0; c < 3; c++)
            {
                bounceSky[c] = model.Bounce[c][0] / model.Kappa[c]; bounceSun[c] = model.Bounce[c][1] / model.Kappa[c];
                bounceLamp[c] = model.LampAlbedo[c] / Math.PI;               // albedo / pi x gain: radiance per lux
                meshLamp[c] = (model.MeshLampAlbedo ?? model.LampAlbedo)[c] / Math.PI;
            }
            bool withLamps = lights != null && lights.Lights.Count > 0 && part != 1 && (bounceLamp[0] + bounceLamp[1] + bounceLamp[2]) > 0;
            double lampDist = withLamps ? lightReach + 2 * lights.MaxRadius : 0;
            int lampProbes = 0;

            // full-sphere direction grid
            var dx = new double[K * M]; var dy = new double[K * M]; var dz = new double[K * M]; var om = new double[K * M]; var el = new double[M];
            var sky = new double[K * M, 3];
            var tmp = new float[F];
            for (int m = 0; m < M; m++)
            {
                double e0 = -Math.PI / 2 + Math.PI * m / M, e1 = -Math.PI / 2 + Math.PI * (m + 1) / M;
                el[m] = 0.5 * (e0 + e1);
                for (int k = 0; k < K; k++)
                {
                    int i = k * M + m;
                    double ph = 2 * Math.PI * k / K;
                    dx[i] = Math.Cos(el[m]) * Math.Cos(ph); dy[i] = Math.Sin(el[m]); dz[i] = Math.Cos(el[m]) * Math.Sin(ph);
                    om[i] = 2 * Math.PI / K * (Math.Sin(e1) - Math.Sin(e0));
                    double rx, rz;
                    TerrainRelight.RotateY(dx[i], dz[i], -skyRotate, out rx, out rz);
                    TerrainRelight.SkyBasis(rx, dy[i], rz, tmp);
                    for (int c = 0; c < 3; c++)
                    {
                        double L = 0;
                        for (int b = 0; b < F; b++) L += tmp[b] * w15[c, b];
                        sky[i, c] = Math.Max(0, L);
                    }
                }
            }
            double[] steps = TerrainRelight.HorizonSteps();
            byte[] shadow = hf.SunShadow(sun);
            int done = 0, kept = 0;
            double hfMaxX = hf.MinX + hf.Spacing * (hf.N - 1), hfMaxZ = hf.MinZ + hf.Spacing * (hf.N - 1);
            foreach (var ps in db.ProbeSets)
            {
                if (ps.Count == 0 || !select(ps)) continue;
                int count = (int)ps.Count, off = (int)ps.GlobalOffset;
                if (off + count > fls.ProbeCount) continue;
                int setKept = 0;
                Parallel.For(0, count, j =>
                {
                    float px, py, pz;
                    ps.Position(j, out px, out py, out pz);
                    // probes the model cannot see: under the ground (Tatooine keeps its spawn-screen and end-of-round
                    // rooms' probe volumes at y -3000) or off the heightfield keep their shipped lighting
                    if (px < hf.MinX || px > hfMaxX || pz < hf.MinZ || pz > hfMaxZ || py < hf.Sample(px, pz) - 1.0)
                    {
                        System.Threading.Interlocked.Increment(ref setKept);
                        return;
                    }
                    var hor = new double[K]; var hn = new float[K * 3]; var hl = new float[K];
                    for (int k = 0; k < K; k++)
                    {
                        double cph = Math.Cos(2 * Math.PI * k / K), sph = Math.Sin(2 * Math.PI * k / K);
                        double best = -10, bestR = 0;
                        foreach (double r in steps)
                        {
                            double tn = (hf.Sample(px + cph * r, pz + sph * r) - py) / r;
                            if (tn > best) { best = tn; bestR = r; }
                        }
                        hor[k] = Math.Atan(best);
                        float a, b2, c2;
                        hf.Normal(px + cph * bestR, pz + sph * bestR, out a, out b2, out c2);
                        hn[k * 3] = a; hn[k * 3 + 1] = b2; hn[k * 3 + 2] = c2;
                        hl[k] = hf.Lit(shadow, px + cph * bestR, pz + sph * bestR);
                    }
                    float gx, gy, gz;
                    hf.Normal(px, pz, out gx, out gy, out gz);
                    float gl = hf.Lit(shadow, px, pz);
                    bool nearSoup = soup != null && px > soup.MinX - reach && px < soup.MaxX + reach && pz > soup.MinZ - reach && pz < soup.MaxZ + reach;
                    bool nearLamp = withLamps && lights.Near(px, pz, lightReach);
                    if (nearLamp) System.Threading.Interlocked.Increment(ref lampProbes);
                    var le = nearLamp ? new double[3] : null;
                    var pw = nearLamp ? new double[3] : null; var pd = nearLamp ? new double[9] : null; var pe = nearLamp ? new double[3] : null;
                    var sh = new double[9, 3]; var vis = new double[4];
                    var Y = new double[9];
                    for (int k = 0; k < K; k++)
                        for (int m = 0; m < M; m++)
                        {
                            int i = k * M + m;
                            double x = dx[i], y = dy[i], z = dz[i], w = om[i];
                            bool open = el[m] > hor[k];
                            float nx = 0, ny = 0, nz = 0;
                            bool meshHit = false, meshLit = false;
                            double meshT = -1;
                            if (open && nearSoup)
                            {
                                double t = soup.Cast(px, py, pz, x, y, z, reach, out nx, out ny, out nz);
                                if (t > 0)
                                {
                                    open = false; meshHit = true; meshT = t;
                                    meshLit = nx * sun[0] + ny * sun[1] + nz * sun[2] > 0 &&
                                              Sunlit(hf, soup, px + x * t, py + y * t, pz + z * t, nx, ny, nz, sun);
                                }
                            }
                            // real SH, Y up: L00, L11 (x), L10 (y), L1-1 (z), L20, L21 (xy), L2-1 (yz), L2-2 (xz), L22 (x^2 - z^2)
                            Y[0] = 0.282095; Y[1] = 0.488603 * x; Y[2] = 0.488603 * y; Y[3] = 0.488603 * z;
                            Y[4] = 0.315392 * (3 * y * y - 1); Y[5] = 1.092548 * x * y; Y[6] = 1.092548 * y * z; Y[7] = 1.092548 * x * z;
                            Y[8] = 0.546274 * (x * x - z * z);
                            double r0, r1, r2;
                            if (open)
                            {
                                r0 = part == 2 ? 0 : sky[i, 0] * skyScale[0]; r1 = part == 2 ? 0 : sky[i, 1] * skyScale[1]; r2 = part == 2 ? 0 : sky[i, 2] * skyScale[2];
                                for (int q = 0; q < 4; q++) vis[q] += Y[q] * w;
                            }
                            else
                            {
                                double hx, hy, hz;
                                if (meshHit) { hx = nx; hy = ny; hz = nz; }
                                else if (el[m] < -20 * Math.PI / 180) { hx = gx; hy = gy; hz = gz; }
                                else { hx = hn[k * 3]; hy = hn[k * 3 + 1]; hz = hn[k * 3 + 2]; }
                                double shade = meshHit ? 1 : el[m] < -20 * Math.PI / 180 ? gl : hl[k];
                                double lit = meshHit && !meshLit ? 0 : Math.Max(0, Math.Min(1, hx * sun[0] + hy * sun[1] + hz * sun[2])) * shade;
                                double f = part == 1 ? 0 : 1;
                                r0 = f * (bounceSky[0] + bounceSun[0] * lit) * bounceScale[0];
                                r1 = f * (bounceSky[1] + bounceSun[1] * lit) * bounceScale[1];
                                r2 = f * (bounceSky[2] + bounceSun[2] * lit) * bounceScale[2];
                                // lamps: the surface this direction sees, lit by the lights around it (direct) and by what
                                // their light bounced onto it (the photon map)
                                if (nearLamp)
                                {
                                    double hitT = meshT, hx0 = 0, hy0 = 0, hz0 = 0; float snx = nx, sny = ny, snz = nz; bool onTerrain = false;
                                    if (hitT <= 0)
                                    {
                                        hitT = LightField.TerrainHit(hf, px, py, pz, x, y, z, lampDist);
                                        if (hitT > 0) { onTerrain = true; hf.Normal(px + x * hitT, pz + z * hitT, out snx, out sny, out snz); }
                                    }
                                    if (hitT > 0)
                                    {
                                        hx0 = px + x * hitT; hy0 = py + y * hitT; hz0 = pz + z * hitT;
                                        if (onTerrain) lights.TerrainIlluminance(hx0, hz0, le);
                                        else lights.SurfaceIlluminance(hx0, hy0, hz0, snx, sny, snz, le);
                                        if (photons != null && photons.Gather(hx0, hy0, hz0, snx, sny, snz, onTerrain, pw, pd, pe))
                                            for (int c = 0; c < 3; c++) le[c] += pe[c];
                                        double[] ka = onTerrain ? bounceLamp : meshLamp;
                                        r0 += ka[0] * le[0]; r1 += ka[1] * le[1]; r2 += ka[2] * le[2];
                                    }
                                }
                            }
                            for (int q = 0; q < 9; q++) { sh[q, 0] += r0 * Y[q] * w; sh[q, 1] += r1 * Y[q] * w; sh[q, 2] += r2 * Y[q] * w; }
                        }
                    // cosine convolution / pi: band 0 x1, band 1 x2/3, band 2 x1/4, then the simpleShL2 form (Y constants folded in)
                    double[] band = { 1, 2.0 / 3, 2.0 / 3, 2.0 / 3, 0.25, 0.25, 0.25, 0.25, 0.25 };
                    double[] yc = { 0.282095, 0.488603, 0.488603, 0.488603, 0.315392, 1.092548, 1.092548, 1.092548, 0.546274 };
                    int pi = off + j;
                    for (int q = 0; q < 9; q++)
                        for (int c = 0; c < 3; c++)
                            fls.SetCoef(pi, q, c, (float)(sh[q, c] * band[q] * yc[q]));
                    for (int q = 0; q < 4; q++) fls.SetCoef(pi, q, 3, (float)(vis[q] * band[q] * yc[q]));
                });
                done += count - setKept;
                kept += setKept;
            }
            KeptLastRun = kept;
            LampProbesLastRun = lampProbes;
            return done;
        }

        /// <summary>Probes the last <see cref="Relight"/> left as shipped (under the ground or off the heightfield).</summary>
        public static int KeptLastRun;

        /// <summary>Probes the last <see cref="Relight"/> found within reach of a light.</summary>
        public static int LampProbesLastRun;

        /// <summary>
        /// Is a surface point in sunlight? A ray towards the sun against the geometry soup (200 m) and the heightfield
        /// (marched to 2 km). Mesh-hit bounce uses it so walls inside a house, or in another house's shadow, stay unlit.
        /// </summary>
        public static bool Sunlit(DenseHeightfield hf, GeometrySoup soup, double x, double y, double z, float nx, float ny, float nz, float[] sun)
        {
            if (sun[1] <= 0) return false;
            double ox = x + nx * 0.05, oy = y + ny * 0.05, oz = z + nz * 0.05;
            float a, b, c;
            if (soup != null && soup.Cast(ox, oy, oz, sun[0], sun[1], sun[2], 200.0, out a, out b, out c) > 0) return false;
            double hl = Math.Sqrt(sun[0] * sun[0] + sun[2] * sun[2]);
            if (hl < 1e-6) return true;
            double tanE = sun[1] / hl, dx = sun[0] / hl, dz = sun[2] / hl;
            foreach (double r in TerrainRelight.HorizonSteps())
                if (hf.Sample(ox + dx * r, oz + dz * r) > oy + r * tanE) return false;
            return true;
        }

        /// <summary>simpleShL2 (CommonLighting.hlsl) of one channel of a probe.</summary>
        public static double Evaluate(StaticEnlightenDatabase fls, int probe, int ch, double x, double y, double z)
        {
            Func<int, double> c = k => fls.Coef(probe, k, ch);
            return c(8) * (x * x - z * z) + 3 * c(4) * y * y + c(0) - c(4) + (c(5) * x * y + c(6) * y * z + c(7) * x * z) + (c(1) * x + c(2) * y + c(3) * z);
        }
    }
}
