using System;
using System.Collections.Generic;
using System.Linq;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// The sky + bounce model a bake is re-lit with, fitted against the bake's own shipped terrain texels
    /// (port of scripts/fit_sky3.py). Sky radiance = non-negative combination of 5 elevation hats x 8 azimuth lobes;
    /// terrain bounce fixed from the visual environment: TerrainColor/pi x (E_sun x max(0, n_hit.s) + E_sky).
    /// </summary>
    public sealed class SkyModel
    {
        public const int Lobes = 8;
        public const int P = TexelMoments.Hats * Lobes;
        public const double Y0 = 0.282095, Y1 = 0.488603;

        public double[][] W = new double[3][];        // per channel, P lobe weights
        public double[][] Bounce = new double[3][];   // per channel {sky-bounce, sun-bounce} weights
        public double[] Kappa = new double[3];
        public double[] FlatShading = new double[3];
        public double[] LightBounce = new double[3];  // per channel: kappa x light gain (the lamps' photon radiance to model units)
        public double[] LampAlbedo = new double[3];   // per channel: albedo x light gain of the ground the lamps light (probes)
        public double[] MeshLampAlbedo;               // the same for meshes (null: as the ground)
        public int SunIndex;
        public float[] SkyQuantX, SkyQuantY, AoQuantX, AoQuantY;
        public int CalibrationTexels;

        /// <summary>A copy whose sky (and the sky-lit ground) is divided by <paramref name="sky"/> and whose bounced sun
        /// by <paramref name="sun"/> (per channel; null = 1): the model as fitted, before a user's --sky/--sun-scale.</summary>
        public SkyModel Unscaled(double[] sky, double[] sun)
        {
            var m = (SkyModel)MemberwiseClone();
            m.W = new double[3][]; m.Bounce = new double[3][];
            for (int c = 0; c < 3; c++)
            {
                double s = sky != null ? sky[c] : 1, u = sun != null ? sun[c] : 1;
                m.W[c] = W[c].Select(v => s != 0 ? v / s : v).ToArray();
                m.Bounce[c] = new[] { s != 0 ? Bounce[c][0] / s : Bounce[c][0], u != 0 ? Bounce[c][1] / u : Bounce[c][1] };
            }
            m.Kappa = (double[])Kappa.Clone(); m.LightBounce = (double[])LightBounce.Clone(); m.LampAlbedo = (double[])LampAlbedo.Clone();
            return m;
        }

        /// <summary>15 old basis functions -> 40 lobes: B_ik = hat_i x (0.5 + 0.5 cos(az - az_k)).</summary>
        public static double[,] LobeMatrix()
        {
            var t = new double[TexelMoments.SkyFns, P];
            for (int i = 0; i < TexelMoments.Hats; i++)
                for (int k = 0; k < Lobes; k++)
                {
                    double pk = 2 * Math.PI * k / Lobes;
                    t[3 * i, i * Lobes + k] = 0.5;
                    t[3 * i + 1, i * Lobes + k] = 0.5 * Math.Cos(pk);
                    t[3 * i + 2, i * Lobes + k] = 0.5 * Math.Sin(pk);
                }
            return t;
        }

        /// <summary>BFII computeShL1Lighting(n, (Y0, Y1 d)) (CommonLighting.hlsl:590) divided by Y0.</summary>
        public static double ShL1(double nx, double ny, double nz, double dx, double dy, double dz)
        {
            double lx = Y1 * dx, ly = Y1 * dy, lz = Y1 * dz;
            double mod = Math.Sqrt(lx * lx + ly * ly + lz * lz);
            if (mod <= 0) return 1;
            double q = 0.5 * (1 + (nx * lx + ny * ly + nz * lz) / mod);
            double lerp = 2 * mod / (Y0 + mod);
            return (lerp * Y0 * ((10.0 / 3.0) * (2 * q * q * q - q * q * q * q) - 1) + Y0) / Y0;
        }

        /// <summary>Normal-equation accumulator for one colour channel.</summary>
        public sealed class Accumulator
        {
            public double[,] AtA = new double[P, P];
            public double[] At0 = new double[P], AtSky = new double[P], AtSun = new double[P];
        }

        /// <summary>
        /// Accumulate calibration texels of one tile. l0[t*3+c] shipped L0; d[(t*3+c)*3+axis] shipped direction;
        /// use[t] selects calibration texels.
        /// </summary>
        public static void Accumulate(Accumulator[] acc, TexelMoments m, int sun, double[,] T, float[] l0, float[] d, bool[] use, double[] floor = null)
        {
            int F = TexelMoments.SkyFns;
            var idx = new List<int>();
            for (int t = 0; t < m.Count; t++) if (use[t]) idx.Add(t);
            if (idx.Count == 0) return;
            var m0 = new double[P]; var m1 = new double[3, P];
            for (int c = 0; c < 3; c++)
            {
                // rows are weighted by the tile's own median L0 (relative error per tile); the floor keeps a dark tile
                // from outweighing the rest of the map
                var vals = idx.Select(t => (double)l0[t * 3 + c]).OrderBy(v => v).ToList();
                double w0 = 1.0 / Math.Max(vals[vals.Count / 2], Math.Max(floor != null ? floor[c] : 0, 1e-6));
                var a = acc[c];
                var row = new double[P];
                foreach (int t in idx)
                {
                    // lobe moments
                    for (int p = 0; p < P; p++)
                    {
                        double s0 = 0, sx = 0, sy = 0, sz = 0;
                        for (int b = 0; b < F; b++)
                        {
                            double tb = T[b, p];
                            if (tb == 0) continue;
                            s0 += m.M0[t * F + b] * tb;
                            sx += m.M1[t * 3 * F + b] * tb; sy += m.M1[t * 3 * F + F + b] * tb; sz += m.M1[t * 3 * F + 2 * F + b] * tb;
                        }
                        m0[p] = s0; m1[0, p] = sx; m1[1, p] = sy; m1[2, p] = sz;
                    }
                    double bs0 = m.Bs0[t], bu0 = m.Bu0[t * m.Suns + sun];
                    // main row: m0 . w = l0 - bounce
                    for (int p = 0; p < P; p++) row[p] = m0[p] * w0;
                    AddRow(a, row, l0[t * 3 + c] * w0, bs0 * w0, bu0 * w0);
                    for (int ax = 0; ax < 3; ax++)
                    {
                        double dc = d[(t * 3 + c) * 3 + ax];
                        for (int p = 0; p < P; p++) row[p] = (dc * m0[p] - m1[ax, p]) * w0;
                        double bs1 = m.Bs1[t * 3 + ax], bu1 = m.Bu1[(t * m.Suns + sun) * 3 + ax];
                        AddRow(a, row, 0, (dc * bs0 - bs1) * w0, (dc * bu0 - bu1) * w0);
                    }
                }
            }
        }

        static void AddRow(Accumulator a, double[] row, double t0, double tSky, double tSun)
        {
            for (int i = 0; i < P; i++)
            {
                double ri = row[i];
                if (ri == 0) continue;
                a.At0[i] += ri * t0; a.AtSky[i] += ri * tSky; a.AtSun[i] += ri * tSun;
                for (int j = 0; j < P; j++) a.AtA[i, j] += ri * row[j];
            }
        }

        /// <summary>min x'Hx/2 - g'x, x >= 0 (projected coordinate descent on a scaled, ridge-regularised system).</summary>
        public static double[] Nnls(double[,] H, double[] g, double ridge = 1e-4, int sweeps = 20000)
        {
            int n = g.Length;
            var sc = new double[n];
            for (int i = 0; i < n; i++) sc[i] = Math.Sqrt(Math.Max(H[i, i], 1e-30));
            var Hs = new double[n, n]; var gs = new double[n];
            for (int i = 0; i < n; i++)
            {
                gs[i] = g[i] / sc[i];
                for (int j = 0; j < n; j++) Hs[i, j] = H[i, j] / (sc[i] * sc[j]);
                Hs[i, i] += ridge;
            }
            var x = new double[n];
            var grad = new double[n];
            for (int i = 0; i < n; i++) grad[i] = -gs[i];
            for (int it = 0; it < sweeps; it++)
            {
                double maxStep = 0;
                for (int i = 0; i < n; i++)
                {
                    double xi = Math.Max(0, x[i] - grad[i] / Hs[i, i]);
                    double dxi = xi - x[i];
                    if (dxi == 0) continue;
                    x[i] = xi;
                    for (int j = 0; j < n; j++) grad[j] += Hs[j, i] * dxi;
                    maxStep = Math.Max(maxStep, Math.Abs(dxi));
                }
                if (maxStep < 1e-10) break;
            }
            for (int i = 0; i < n; i++) x[i] /= sc[i];
            return x;
        }

        /// <summary>Integral over the upper hemisphere of the fitted sky radiance x cos(theta), per channel.</summary>
        public double[] SkyCosIntegral(double[,] T)
        {
            var outv = new double[3];
            int K = 128, M = 64;
            var tmp = new float[TexelMoments.SkyFns];
            for (int m = 0; m < M; m++)
            {
                double e0 = Math.PI / 2 * m / M, e1 = Math.PI / 2 * (m + 1) / M, ec = 0.5 * (e0 + e1);
                double omega = 2 * Math.PI / K * (Math.Sin(e1) - Math.Sin(e0));
                for (int k = 0; k < K; k++)
                {
                    double ph = 2 * Math.PI * k / K;
                    double x = Math.Cos(ec) * Math.Cos(ph), y = Math.Sin(ec), z = Math.Cos(ec) * Math.Sin(ph);
                    TerrainRelight.SkyBasis(x, y, z, tmp);
                    for (int c = 0; c < 3; c++)
                    {
                        double L = 0;
                        for (int p = 0; p < P; p++)
                        {
                            double bp = 0;
                            for (int b = 0; b < TexelMoments.SkyFns; b++) bp += tmp[b] * T[b, p];
                            L += bp * W[c][p];
                        }
                        outv[c] += L * y * omega;
                    }
                }
            }
            return outv;
        }

        /// <summary>
        /// Solve with physical bounce: rho = TerrainColor, eSun = SunIntensity x SunScale x SunColor (x gain).
        /// flat = median shipped shading (E/pi) of open flat texels.
        /// </summary>
        public static SkyModel Solve(Accumulator[] acc, double[] rho, double[] eSun, double[] flat, int sun)
        {
            var model = new SkyModel { SunIndex = sun, FlatShading = (double[])flat.Clone() };
            double[,] T = LobeMatrix();
            var bw = new double[3][];
            for (int c = 0; c < 3; c++) bw[c] = new double[2];
            for (int it = 0; it < 4; it++)
            {
                for (int c = 0; c < 3; c++)
                {
                    var g = new double[P];
                    for (int p = 0; p < P; p++) g[p] = acc[c].At0[p] - bw[c][0] * acc[c].AtSky[p] - bw[c][1] * acc[c].AtSun[p];
                    model.W[c] = Nnls(acc[c].AtA, g);
                }
                double[] cosInt = model.SkyCosIntegral(T);
                for (int c = 0; c < 3; c++)
                {
                    model.Kappa[c] = cosInt[c] / Math.PI / Math.Max(flat[c], 1e-9);
                    bw[c][0] = model.Kappa[c] * rho[c] * flat[c];
                    bw[c][1] = model.Kappa[c] * rho[c] * eSun[c] / Math.PI;
                }
            }
            model.Bounce = bw;
            return model;
        }

        /// <summary>Predict L0 (3) and d (3 channels x 3 axes) for every texel of a moment set.</summary>
        public void Predict(TexelMoments m, float[] l0, float[] d)
        {
            double[,] T = LobeMatrix();
            int F = TexelMoments.SkyFns;
            // collapse lobe weights back onto the 15-function basis: w15 = T w40
            var w15 = new double[3, F];
            for (int c = 0; c < 3; c++)
                for (int b = 0; b < F; b++)
                {
                    double s = 0;
                    for (int p = 0; p < P; p++) s += T[b, p] * W[c][p];
                    w15[c, b] = s;
                }
            for (int t = 0; t < m.Count; t++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double c0 = 0, cx = 0, cy = 0, cz = 0;
                    for (int b = 0; b < F; b++)
                    {
                        double wb = w15[c, b];
                        c0 += m.M0[t * F + b] * wb;
                        cx += m.M1[t * 3 * F + b] * wb; cy += m.M1[t * 3 * F + F + b] * wb; cz += m.M1[t * 3 * F + 2 * F + b] * wb;
                    }
                    double bs = Bounce[c][0], bu = Bounce[c][1];
                    int su = t * m.Suns + SunIndex;
                    c0 += bs * m.Bs0[t] + bu * m.Bu0[su];
                    cx += bs * m.Bs1[t * 3] + bu * m.Bu1[su * 3];
                    cy += bs * m.Bs1[t * 3 + 1] + bu * m.Bu1[su * 3 + 1];
                    cz += bs * m.Bs1[t * 3 + 2] + bu * m.Bu1[su * 3 + 2];
                    if (m.X0 != null && LightBounce[c] != 0)
                    {
                        double lb = LightBounce[c];
                        c0 += lb * m.X0[t * 3 + c];
                        cx += lb * m.X1[(t * 3 + c) * 3]; cy += lb * m.X1[(t * 3 + c) * 3 + 1]; cz += lb * m.X1[(t * 3 + c) * 3 + 2];
                    }
                    double inv = 1.0 / Math.Max(c0, 1e-6);
                    double dx = cx * inv, dy = cy * inv, dz = cz * inv;
                    double dn = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dn > 1) { dx /= dn; dy /= dn; dz /= dn; }
                    l0[t * 3 + c] = (float)Math.Max(c0, 0);
                    d[(t * 3 + c) * 3] = (float)dx; d[(t * 3 + c) * 3 + 1] = (float)dy; d[(t * 3 + c) * 3 + 2] = (float)dz;
                }
            }
        }

        /// <summary>Monotone quantile map (41 quantiles) from computed values onto shipped byte values.</summary>
        public static void QuantileMap(List<float> mine, List<float> shipped, out float[] xs, out float[] ys)
        {
            var a = mine.OrderBy(v => v).ToArray(); var b = shipped.OrderBy(v => v).ToArray();
            xs = new float[41]; ys = new float[41];
            for (int i = 0; i <= 40; i++)
            {
                double q = i / 40.0;
                xs[i] = a.Length == 0 ? i / 40f : a[(int)Math.Min(a.Length - 1, Math.Round(q * (a.Length - 1)))];
                ys[i] = b.Length == 0 ? 255 * i / 40f : b[(int)Math.Min(b.Length - 1, Math.Round(q * (b.Length - 1)))];
                if (i > 0 && xs[i] <= xs[i - 1]) xs[i] = xs[i - 1] + 1e-6f;
            }
        }

        public static float Interp(float x, float[] xs, float[] ys)
        {
            if (x <= xs[0]) return ys[0];
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
            return ys[ys.Length - 1];
        }
    }
}
