using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>A dense heightfield (metres) on a regular grid, rows min-Z first; bilinear sampling, clamped at the edges.</summary>
    public sealed class DenseHeightfield
    {
        public readonly int N;           // samples per side
        public readonly double MinX, MinZ, Spacing;
        public readonly float[] H;       // N*N, row-major, z rows

        public DenseHeightfield(int n, double minX, double minZ, double spacing, float[] h)
        {
            N = n; MinX = minX; MinZ = minZ; Spacing = spacing; H = h;
        }

        public float Sample(double x, double z)
        {
            double fx = (x - MinX) / Spacing, fz = (z - MinZ) / Spacing;
            double lim = N - 1.001;
            if (fx < 0) fx = 0; else if (fx > lim) fx = lim;
            if (fz < 0) fz = 0; else if (fz > lim) fz = lim;
            int ix = (int)fx, iz = (int)fz;
            float tx = (float)(fx - ix), tz = (float)(fz - iz);
            int o = iz * N + ix;
            float a = H[o], b = H[o + 1], c = H[o + N], d = H[o + N + 1];
            float ab = a + (b - a) * tx, cd = c + (d - c) * tx;
            return ab + (cd - ab) * tz;
        }

        public void Normal(double x, double z, out float nx, out float ny, out float nz, double e = 1.0)
        {
            float hx = (Sample(x + e, z) - Sample(x - e, z)) / (float)(2 * e);
            float hz = (Sample(x, z + e) - Sample(x, z - e)) / (float)(2 * e);
            float l = (float)Math.Sqrt(hx * hx + 1 + hz * hz);
            nx = -hx / l; ny = 1 / l; nz = -hz / l;
        }

        /// <summary>
        /// Terrain self-shadow for a sun direction: 1 where a ray from the raster point (lifted 0.3 m) towards the sun
        /// clears the heightfield out to 2 km, else 0. Low suns put whole valleys in shadow; the bounce off them must not
        /// carry sunlight. Cached per direction.
        /// </summary>
        public byte[] SunShadow(float[] sun)
        {
            string key = sun[0].ToString("R") + "," + sun[1].ToString("R") + "," + sun[2].ToString("R");
            byte[] cached;
            lock (_shadow) if (_shadow.TryGetValue(key, out cached)) return cached;
            var m = new byte[N * N];
            double hl = Math.Sqrt(sun[0] * sun[0] + sun[2] * sun[2]);
            if (sun[1] <= 0) { lock (_shadow) _shadow[key] = m; return m; }
            if (hl < 1e-6) { for (int i = 0; i < m.Length; i++) m[i] = 1; lock (_shadow) _shadow[key] = m; return m; }
            double tanE = sun[1] / hl, dx = sun[0] / hl, dz = sun[2] / hl;
            double[] steps = TerrainRelight.HorizonSteps();
            Parallel.For(0, N, j =>
            {
                double z = MinZ + j * Spacing;
                for (int i = 0; i < N; i++)
                {
                    double x = MinX + i * Spacing, h = H[j * N + i] + 0.3;
                    byte lit = 1;
                    foreach (double r in steps)
                        if (Sample(x + dx * r, z + dz * r) > h + r * tanE) { lit = 0; break; }
                    m[j * N + i] = lit;
                }
            });
            lock (_shadow) _shadow[key] = m;
            return m;
        }

        readonly Dictionary<string, byte[]> _shadow = new Dictionary<string, byte[]>();

        /// <summary>Bilinear sunlit fraction at (x, z) from a <see cref="SunShadow"/> map.</summary>
        public float Lit(byte[] shadow, double x, double z)
        {
            double fx = (x - MinX) / Spacing, fz = (z - MinZ) / Spacing;
            double lim = N - 1.001;
            if (fx < 0) fx = 0; else if (fx > lim) fx = lim;
            if (fz < 0) fz = 0; else if (fz > lim) fz = lim;
            int ix = (int)fx, iz = (int)fz;
            float tx = (float)(fx - ix), tz = (float)(fz - iz);
            int o = iz * N + ix;
            float a = shadow[o], b = shadow[o + 1], c = shadow[o + N], d = shadow[o + N + 1];
            float ab = a + (b - a) * tx, cd = c + (d - c) * tx;
            return ab + (cd - ab) * tz;
        }

        /// <summary>Rasterise any height function over the square [minX, minX+extent] x [minZ, minZ+extent].</summary>
        public static DenseHeightfield From(Func<double, double, double> f, double minX, double minZ, double extent, int n)
        {
            var h = new float[n * n];
            double sp = extent / (n - 1);
            Parallel.For(0, n, j =>
            {
                double z = minZ + j * sp;
                for (int i = 0; i < n; i++) h[j * n + i] = (float)f(minX + i * sp, z);
            });
            return new DenseHeightfield(n, minX, minZ, sp, h);
        }
    }

    /// <summary>
    /// Per-texel directional moments of a terrain-tile texel: the visible sky (basis moments), the occluded part (terrain
    /// bounce), cosine-weighted sky visibility and a short-range AO. Port of scripts/relight_proto.py (the model is in
    /// projects/enlighten-authoring/SPEC.md §7).
    /// </summary>
    public sealed class TexelMoments
    {
        public const int Hats = 5;
        public const int SkyFns = Hats * 3;          // hat x {1, cos(az), sin(az)}
        public int Count;
        public int Suns;
        public float[] M0;      // Count x 15
        public float[] M1;      // Count x 3 x 15
        public float[] Bs0;     // Count
        public float[] Bs1;     // Count x 3
        public float[] Bu0;     // Count x Suns
        public float[] Bu1;     // Count x Suns x 3
        public float[] SkyVis, Ao;
        public float[] Nrm;     // Count x 3
        public double[] Wx, Wz;
        public float[] X0;      // Count x 3: sum over the occluded cells of w x E_lamp,c of the surface seen (lux sr); null = no lights
        public float[] X1;      // Count x 3 x 3: the same times the cell direction
        public int LampTexels;  // texels within reach of a light

        public TexelMoments(int n, int suns)
        {
            Count = n; Suns = suns;
            M0 = new float[n * SkyFns]; M1 = new float[n * 3 * SkyFns];
            Bs0 = new float[n]; Bs1 = new float[n * 3];
            Bu0 = new float[n * suns]; Bu1 = new float[n * suns * 3];
            SkyVis = new float[n]; Ao = new float[n]; Nrm = new float[n * 3];
            Wx = new double[n]; Wz = new double[n];
        }
    }

    public static class TerrainRelight
    {
        /// <summary>Elevation cell edges (degrees), as the prototype.</summary>
        public static readonly double[] ElevEdges = { -88, -60, -40, -25, -15, -8, -3, 0, 3, 7, 12, 18, 25, 33, 42, 52, 63, 75, 90 };
        public const int Azimuths = 32;

        static readonly float[] HatKnots = { 0f, 0.2f, 0.45f, 0.75f, 1f };

        /// <summary>Sky basis at direction (x, y, z): hat_i(sin elevation) x {1, cos az, sin az}, zero below the horizon.</summary>
        public static void SkyBasis(double x, double y, double z, float[] outB)
        {
            Array.Clear(outB, 0, TexelMoments.SkyFns);
            if (y <= 0) return;
            double s = Math.Min(1, y);
            double h = Math.Sqrt(Math.Max(x * x + z * z, 1e-12));
            double ca = x / h, sa = z / h;
            for (int i = 0; i < TexelMoments.Hats; i++)
            {
                double kn = HatKnots[i];
                double lo = i > 0 ? HatKnots[i - 1] : kn - 1, hi = i < TexelMoments.Hats - 1 ? HatKnots[i + 1] : kn + 1;
                double v = Math.Min((s - lo) / (kn - lo), (hi - s) / (hi - kn));
                if (v <= 0) continue;
                if (v > 1) v = 1;
                outB[3 * i] = (float)v; outB[3 * i + 1] = (float)(v * ca); outB[3 * i + 2] = (float)(v * sa);
            }
        }

        /// <summary>Turn (x, z) by <paramref name="a"/> radians about +Y (azimuth atan2(z, x) grows by a).</summary>
        public static void RotateY(double x, double z, double a, out double rx, out double rz)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            rx = x * c - z * s; rz = x * s + z * c;
        }

        /// <summary>Horizon marching distances: 0.7 m growing by 16% per step up to rmax.</summary>
        public static double[] HorizonSteps(double rmax = 2000)
        {
            var r = new List<double> { 0.7 };
            while (r[r.Count - 1] < rmax) r.Add(r[r.Count - 1] * 1.16);
            return r.ToArray();
        }

        /// <summary>
        /// Moments for texel world positions (x, z) on heightfield hf; suns = unit directions towards each sun. For
        /// surfaces that are not the terrain (mesh charts) pass their heights <paramref name="ys"/> and normals
        /// <paramref name="normals"/> (3 per point); the hemisphere is then taken around that normal at that height.
        /// </summary>
        public static TexelMoments Moments(DenseHeightfield hf, double[] xs, double[] zs, float[][] suns, double lift = 0.3,
                                           GeometrySoup soup = null, double reach = 80.0, double[] ys = null, float[] normals = null,
                                           double skyRotate = 0)
        {
            int n = xs.Length, K = Azimuths, M = ElevEdges.Length - 1, S = suns.Length;
            var res = new TexelMoments(n, S);
            // direction grid
            var dirX = new double[K * M]; var dirY = new double[K * M]; var dirZ = new double[K * M]; var omega = new double[K * M];
            var elc = new double[M];
            var basis = new float[K * M * TexelMoments.SkyFns];
            var tmp = new float[TexelMoments.SkyFns];
            double dphi = 2 * Math.PI / K;
            for (int m = 0; m < M; m++)
            {
                double e0 = ElevEdges[m] * Math.PI / 180, e1 = ElevEdges[m + 1] * Math.PI / 180;
                elc[m] = 0.5 * (e0 + e1);
                for (int k = 0; k < K; k++)
                {
                    int c = k * M + m;
                    double ph = k * dphi;
                    dirX[c] = Math.Cos(elc[m]) * Math.Cos(ph); dirY[c] = Math.Sin(elc[m]); dirZ[c] = Math.Cos(elc[m]) * Math.Sin(ph);
                    omega[c] = dphi * (Math.Sin(e1) - Math.Sin(e0));
                    double rx, rz;
                    RotateY(dirX[c], dirZ[c], -skyRotate, out rx, out rz);
                    SkyBasis(rx, dirY[c], rz, tmp);
                    Array.Copy(tmp, 0, basis, c * TexelMoments.SkyFns, TexelMoments.SkyFns);
                }
            }
            double[] steps = HorizonSteps();
            var shadows = new byte[S][];
            for (int si = 0; si < S; si++) shadows[si] = hf.SunShadow(suns[si]);
            var cosPh = new double[K]; var sinPh = new double[K];
            for (int k = 0; k < K; k++) { cosPh[k] = Math.Cos(k * dphi); sinPh[k] = Math.Sin(k * dphi); }
            const int F = TexelMoments.SkyFns;

            Parallel.For(0, n, t =>
            {
                double x = xs[t], z = zs[t];
                res.Wx[t] = x; res.Wz[t] = z;
                float nx, ny, nz;
                if (normals != null) { nx = normals[t * 3]; ny = normals[t * 3 + 1]; nz = normals[t * 3 + 2]; }
                else hf.Normal(x, z, out nx, out ny, out nz);
                res.Nrm[t * 3] = nx; res.Nrm[t * 3 + 1] = ny; res.Nrm[t * 3 + 2] = nz;
                double h0 = ys != null ? ys[t] : hf.Sample(x, z) + lift;
                var hor = new double[K]; var horR = new double[K];
                for (int k = 0; k < K; k++)
                {
                    double best = -10, bestR = 0;
                    for (int si = 0; si < steps.Length; si++)
                    {
                        double r = steps[si];
                        double tn = (hf.Sample(x + cosPh[k] * r, z + sinPh[k] * r) - h0) / r;
                        if (tn > best) { best = tn; bestR = r; }
                    }
                    hor[k] = Math.Atan(best); horR[k] = bestR;
                }
                // sunlit factor of the horizon-setting sample, per sun (cosine x terrain self-shadow)
                var sunlit = new double[K * S];
                for (int k = 0; k < K; k++)
                {
                    float hx, hy, hz;
                    double sx = x + cosPh[k] * horR[k], sz = z + sinPh[k] * horR[k];
                    hf.Normal(sx, sz, out hx, out hy, out hz);
                    for (int s = 0; s < S; s++)
                        sunlit[k * S + s] = Math.Max(0, Math.Min(1, hx * suns[s][0] + hy * suns[s][1] + hz * suns[s][2])) * hf.Lit(shadows[s], sx, sz);
                }
                double cwSum = 0, visCw = 0, aoCw = 0;
                int o0 = t * F, o1 = t * 3 * F;
                bool nearSoup = soup != null && x > soup.MinX - reach && x < soup.MaxX + reach && z > soup.MinZ - reach && z < soup.MaxZ + reach
                                && h0 > soup.MinY - reach && h0 < soup.MaxY + reach;
                double ox = x + nx * 0.05, oy = h0 + ny * 0.05, oz = z + nz * 0.05;
                for (int k = 0; k < K; k++)
                {
                    bool near = horR[k] < 6.0;
                    for (int m = 0; m < M; m++)
                    {
                        int c = k * M + m;
                        double cosn = dirX[c] * nx + dirY[c] * ny + dirZ[c] * nz;
                        if (cosn <= 0) continue;
                        double w = omega[c];
                        bool vis = elc[m] > hor[k];
                        double cw = cosn * w;
                        cwSum += cw;
                        bool meshHit = false, meshNear = false;
                        float mnx = 0, mny = 0, mnz = 0;
                        double hit = -1;
                        if (vis && nearSoup)
                        {
                            hit = soup.Cast(ox, oy, oz, dirX[c], dirY[c], dirZ[c], reach, out mnx, out mny, out mnz);
                            if (hit > 0) { vis = false; meshHit = true; meshNear = hit < 6.0; }
                        }
                        if (vis)
                        {
                            visCw += cw; aoCw += cw;
                            int bo = c * F;
                            for (int b = 0; b < F; b++)
                            {
                                float bv = basis[bo + b];
                                if (bv == 0) continue;
                                double v = w * bv;
                                res.M0[o0 + b] += (float)v;
                                res.M1[o1 + b] += (float)(v * dirX[c]);
                                res.M1[o1 + F + b] += (float)(v * dirY[c]);
                                res.M1[o1 + 2 * F + b] += (float)(v * dirZ[c]);
                            }
                        }
                        else
                        {
                            if (meshHit ? !meshNear : !near) aoCw += cw;
                            res.Bs0[t] += (float)w;
                            res.Bs1[t * 3] += (float)(w * dirX[c]); res.Bs1[t * 3 + 1] += (float)(w * dirY[c]); res.Bs1[t * 3 + 2] += (float)(w * dirZ[c]);
                            for (int s = 0; s < S; s++)
                            {
                                double lit = sunlit[k * S + s];
                                if (meshHit)
                                {
                                    // the surface hit is lit only where the sun reaches it (not inside a house or in its neighbour's shadow)
                                    double cs = mnx * suns[s][0] + mny * suns[s][1] + mnz * suns[s][2];
                                    lit = cs > 0 && ProbeRelight.Sunlit(hf, soup, ox + dirX[c] * hit, oy + dirY[c] * hit, oz + dirZ[c] * hit,
                                                                        mnx, mny, mnz, suns[s]) ? Math.Min(1, cs) : 0;
                                }
                                double ws = w * lit;
                                res.Bu0[t * S + s] += (float)ws;
                                int ob = (t * S + s) * 3;
                                res.Bu1[ob] += (float)(ws * dirX[c]); res.Bu1[ob + 1] += (float)(ws * dirY[c]); res.Bu1[ob + 2] += (float)(ws * dirZ[c]);
                            }
                        }
                    }
                }
                res.SkyVis[t] = (float)(visCw / Math.Max(cwSum, 1e-6));
                res.Ao[t] = (float)(aoCw / Math.Max(cwSum, 1e-6));
            });
            return res;
        }

        /// <summary>
        /// Lamp moments (<see cref="TexelMoments.X0"/>, <see cref="TexelMoments.X1"/>) of the same points as
        /// <see cref="Moments"/>, read from the lamps' photon map: X0 = the lamps' incident indirect radiance integral
        /// (sum of power / (area cos) of the deposits within <see cref="LampPhotons.Radius"/>, albedo already in the power),
        /// X1 = X0 x the direction towards where it came from. A terrain texel reads the terrain's deposits at the ground,
        /// a chart texel (<paramref name="ys"/>, <paramref name="normals"/>) the meshes' at its own position and normal.
        /// </summary>
        public static void LampMoments(TexelMoments res, DenseHeightfield hf, double[] xs, double[] zs, LampPhotons photons,
                                       double[] ys = null, float[] normals = null)
        {
            int n = xs.Length;
            res.X0 = new float[n * 3]; res.X1 = new float[n * 9]; res.LampTexels = 0;
            if (photons == null) return;
            int lit = 0;
            Parallel.For(0, n, () => new double[15], (t, state, buf) =>
            {
                double x = xs[t], z = zs[t];
                float nx, ny, nz;
                if (normals != null) { nx = normals[t * 3]; ny = normals[t * 3 + 1]; nz = normals[t * 3 + 2]; }
                else hf.Normal(x, z, out nx, out ny, out nz);
                double y = ys != null ? ys[t] : hf.Sample(x, z);
                var w = new double[3]; var d = new double[9]; var e = new double[3];
                if (!photons.Gather(x, y, z, nx, ny, nz, ys == null, w, d, e)) return buf;
                System.Threading.Interlocked.Increment(ref lit);
                for (int c = 0; c < 3; c++)
                {
                    res.X0[t * 3 + c] = (float)w[c];
                    for (int ax = 0; ax < 3; ax++) res.X1[(t * 3 + c) * 3 + ax] = (float)d[c * 3 + ax];
                }
                return buf;
            }, buf => { });
            res.LampTexels = lit;
        }
    }
}
