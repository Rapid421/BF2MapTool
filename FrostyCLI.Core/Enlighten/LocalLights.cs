using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// An IES profile as BFII ships it (the resource of an IesProfileAsset, e.g. Lighting/IES/Guide/IES_guide21):
    /// u32 resolution N, f32 max candela, f32 lumens output, 24 bytes, then N x N half floats in [0, 1] - rows run
    /// linearly in cos(theta) from the light's axis, row 0 at theta 0 and row N-1 at 180 deg (the mapping whose sphere
    /// integral gives the stored lumens: guide21 920 lm vs 909; texel centres give 1060, theta-linear rows 288-302),
    /// columns in the horizontal angle. Tatooine's wall lights (guide21): 3000 cd on the axis, 5% at 30 deg, under 1%
    /// from 60 to 90 deg, dark behind.
    /// </summary>
    public sealed class IesProfile
    {
        public double MaxCandela, Lumens;
        public double[] Rows;             // per row, averaged over the horizontal angle
        public int N;                     // the stored N x N profile matrix, row-major (Flux takes it as its IES texture)
        public float[] Grid;

        public static IesProfile Parse(byte[] d)
        {
            if (d == null || d.Length < 36) return null;
            int n = BitConverter.ToInt32(d, 0);
            if (n <= 0 || n > 4096 || d.Length - n * n * 2 < 12) return null;
            int hdr = d.Length - n * n * 2;
            var p = new IesProfile { MaxCandela = BitConverter.ToSingle(d, 4), Lumens = BitConverter.ToSingle(d, 8), Rows = new double[n], N = n, Grid = new float[n * n] };
            for (int r = 0; r < n; r++)
            {
                double s = 0;
                for (int c = 0; c < n; c++)
                {
                    double v = Half(BitConverter.ToUInt16(d, hdr + (r * n + c) * 2));
                    p.Grid[r * n + c] = (float)v;
                    s += v;
                }
                p.Rows[r] = s / n;
            }
            return p;
        }

        /// <summary>The profile towards a direction at cos(theta) from the axis (linear between rows).</summary>
        public double At(double cosTheta)
        {
            int n = Rows.Length;
            double f = (1 - cosTheta) * 0.5 * (n - 1);
            if (f <= 0) return Rows[0];
            if (f >= n - 1) return Rows[n - 1];
            int i = (int)f; double t = f - i;
            return Rows[i] * (1 - t) + Rows[i + 1] * t;
        }

        static double Half(ushort h)
        {
            int e = (h >> 10) & 31, m = h & 1023;
            double v = e == 0 ? m / 1024.0 * Math.Pow(2, -14) : e == 31 ? double.PositiveInfinity : (1 + m / 1024.0) * Math.Pow(2, e - 15);
            return (h & 0x8000) != 0 ? -v : v;
        }
    }

    /// <summary>
    /// A light as the Flux bake sees it (FIFA21_SOURCE Pipeline/Flux/FluxLightingUtils.cpp getBakedLightInfo,
    /// World/Render/Lighting/LightingUtils.cpp): colour x intensity / (area x pi) with the punctual areas 4 (sphere, so
    /// I = power / 4 pi) and 1 (spot and frustum lights, I = power / pi); luminance-unit lights use the intensity as is.
    /// Spots, frustums, rectangles and IES profiles emit along -forward (Tatooine's ceiling lamps have forward = up); the
    /// cone is the full OuterAngle with the squared smooth
    /// falloff saturate((cos - cos(outer/2)) / (cos(inner/2) - cos(outer/2)))^2 (an ortho frustum's cone opening is
    /// atan2(max(width, height), radius), LightCullJob.cpp); distance attenuation 1/d^2 x saturate(1 - (d/r)^4)^2.
    /// Only the light's bounce reaches a lightmap or a probe (Flux indirectEnable = AffectRadiosity; the direct part is
    /// drawn at runtime unless DirectLightmapEnable).
    /// </summary>
    public sealed class LocalLight
    {
        public double X, Y, Z;
        public double Fx, Fy, Fz = 1;            // forward (spots, frustums, rectangles and IES emit along -forward)
        public double[] I = new double[3];        // luminous intensity per channel, candela
        public double Radius = 10;                // AttenuationRadius
        public bool Spot;
        public bool Cosine;                       // a one-sided emitting rectangle: I(theta) = I x cos(theta)
        public double CosInner = 1, CosOuter = -1;
        public string Type;
        public IesProfile Ies;                    // the intensity is I x Ies.At(cos from forward) when set

        /// <summary>
        /// A light from an exported record (`level export-lights`: type name, scalar fields as invariant text, world
        /// transform). Null, with the reason, for a light that bakes no bounce (disabled, AffectRadiosity off, dark).
        /// </summary>
        public static LocalLight FromRecord(string type, IDictionary<string, string> f, float[] trans, float[] forward, out string why)
        {
            return FromRecord(type, f, trans, forward, null, out why);
        }

        /// <summary>
        /// As above, with the light's IES profile (its IESProfile reference resolved by the caller): Flux then drops the
        /// intensity units and emits Color x (max candela x IESMultiplier, or max candela x Intensity / lumens when the
        /// profile is a mask) x profile(theta) (LightingUtils.cpp calculateIESLuminance; FluxLightingUtils.cpp).
        /// </summary>
        public static LocalLight FromRecord(string type, IDictionary<string, string> f, float[] trans, float[] forward, IesProfile ies, out string why)
        {
            why = null;
            Func<string, double, double> num = (k, d) =>
            {
                string v; double r;
                return f != null && f.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : d;
            };
            Func<string, bool, bool> flag = (k, d) =>
            {
                string v; bool r;
                return f != null && f.TryGetValue(k, out v) && bool.TryParse(v, out r) ? r : d;
            };
            Func<string, string> str = k => { string v; return f != null && f.TryGetValue(k, out v) ? v : null; };
            if (!flag("Enabled", true)) { why = "disabled"; return null; }
            if (!flag("AffectRadiosity", true)) { why = "AffectRadiosity off"; return null; }
            if (trans == null) { why = "no transform"; return null; }
            double[] col = { 1, 1, 1 };
            string cs = str("Color");
            if (cs != null)
            {
                string[] p = cs.Split(',');
                for (int i = 0; i < 3 && i < p.Length; i++)
                    double.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out col[i]);
            }
            double intensity = num("Intensity", 0) * num("Dimmer", 1);
            bool luminance = str("LightUnit") == "LightUnitType_Luminance";
            // what the bake bounces is colour x RadiosityColorScale (Flux bounceColorScale; LightStateData.ddf
            // RadiosityColorMode Multiply: indirect colour = Color x Intensity x RadiosityColorScale). Tatooine's wall,
            // ground and cantina lights carry 2.
            string rs = str("RadiosityColorScale");
            if (rs != null)
            {
                string[] p = rs.Split(',');
                for (int i = 0; i < 3 && i < p.Length; i++)
                {
                    double s;
                    if (double.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out s)) col[i] *= s;
                }
            }
            var l = new LocalLight { X = trans[0], Y = trans[1], Z = trans[2], Radius = num("AttenuationRadius", 0), Type = type };
            if (forward != null)
            {
                double n = Math.Sqrt(forward[0] * forward[0] + forward[1] * forward[1] + forward[2] * forward[2]);
                if (n > 1e-6) { l.Fx = forward[0] / n; l.Fy = forward[1] / n; l.Fz = forward[2] / n; }
            }
            if (ies != null && (type == "PbrSphereLightEntityData" || type == "PbrSpotLightEntityData"))
            {
                // the profile carries the intensity: max candela x IESMultiplier, or as a mask the light's own lumens spread
                // by the profile (max candela x Intensity / lumens output); Dimmer and colour on top
                bool mask = flag("UseIESProfileAsMask", false);
                double imax = (mask ? ies.MaxCandela * num("Intensity", 0) / Math.Max(ies.Lumens, 1e-6) : ies.MaxCandela * num("IESMultiplier", 1)) * num("Dimmer", 1);
                l.Ies = ies;
                if (type == "PbrSpotLightEntityData")
                {
                    l.Spot = true;
                    l.CosOuter = Math.Cos(num("OuterAngle", 90) * Math.PI / 360);
                    l.CosInner = Math.Cos(Math.Min(num("InnerAngle", 0), num("OuterAngle", 90)) * Math.PI / 360);
                }
                l.SetI(col, imax);
                return l.Check(out why);
            }
            double area;
            switch (type)
            {
                case "PbrSphereLightEntityData":
                {
                    double r = num("SphereRadius", 0);
                    bool punctual = r == 0 && !luminance;
                    area = punctual ? 4 : 4 * Math.PI * Math.Max(r, 0.01) * Math.Max(r, 0.01);
                    // an area sphere of luminance L emits L x pi r^2 per steradian
                    if (!punctual) { l.SetI(col, (luminance ? intensity : intensity / (area * Math.PI)) * Math.PI * Math.Max(r, 0.01) * Math.Max(r, 0.01)); return l.Check(out why); }
                    break;
                }
                case "PbrSpotLightEntityData":
                {
                    double r = num("DiscRadius", 0);
                    bool punctual = r == 0 && !luminance;
                    l.Spot = true;
                    l.CosOuter = Math.Cos(num("OuterAngle", 90) * Math.PI / 360);
                    l.CosInner = Math.Cos(Math.Min(num("InnerAngle", 0), num("OuterAngle", 90)) * Math.PI / 360);
                    // an area disc (and a luminance-unit spot, promoted to one) is at least 1 cm across (PUNCTUAL_LIGHT_THRESHOLD)
                    double ra = Math.Max(r, 0.01);
                    area = punctual ? 1 : Math.PI * ra * ra;
                    if (!punctual) { l.SetI(col, (luminance ? intensity : intensity / (area * Math.PI)) * Math.PI * ra * ra); return l.Check(out why); }
                    break;
                }
                case "PbrTubeLightEntityData":
                {
                    // a tube bakes as its full surface; seen from the side it is a line source, approximated as a point
                    double r = Math.Max(num("TubeRadius", 0), 0.01), w = Math.Max(num("TubeWidth", 0), 0.01);
                    area = 2 * Math.PI * r * w + (flag("IsCapsule", false) ? 4 * Math.PI * r * r : 0);
                    double L = luminance ? intensity : intensity / (area * Math.PI);
                    l.SetI(col, L * area / 4);                 // mean projected area of a convex body = surface / 4
                    return l.Check(out why);
                }
                case "PbrRectangularLightEntityData":
                {
                    string shape = str("Shape") ?? "RectangularLightShape_Rectangular";
                    double w = Math.Max(num("Width", 0), 0.01), h = Math.Max(num("Height", 0), 0.01);
                    l.Spot = true;
                    if (shape == "RectangularLightShape_Rectangular")
                    {
                        // a one-sided emitting rectangle: I(theta) = L x w x h x cos(theta)
                        double L = luminance ? intensity : intensity / (w * h * Math.PI);
                        l.SetI(col, L * w * h);
                        l.Spot = false; l.Cosine = true;
                        return l.Check(out why);
                    }
                    double outer = shape == "RectangularLightShape_OrthoFrustum"
                        ? Math.Atan2(Math.Max(w, h), Math.Max(l.Radius, 0.01)) * 180 / Math.PI
                        : num("OuterAngle", 90);
                    l.CosOuter = Math.Cos(outer * Math.PI / 360);
                    area = 1;
                    break;
                }
                default:
                    why = "not a Pbr light (" + type + ")";
                    return null;
            }
            l.SetI(col, luminance ? intensity : intensity / (area * Math.PI));
            return l.Check(out why);
        }

        void SetI(double[] col, double v) { for (int c = 0; c < 3; c++) I[c] = col[c] * v; }

        LocalLight Check(out string why)
        {
            why = null;
            if (Radius <= 1e-3) { why = "AttenuationRadius 0"; return null; }
            if (I[0] + I[1] + I[2] <= 0) { why = "no intensity"; return null; }
            return this;
        }

        /// <summary>Illuminance (lux) at point (x, y, z) with normal n, without visibility; e accumulates.</summary>
        public void Illuminance(double x, double y, double z, double nx, double ny, double nz, double[] e)
        {
            double lx = X - x, ly = Y - y, lz = Z - z;
            double d2 = lx * lx + ly * ly + lz * lz;
            double r2 = Radius * Radius;
            if (d2 >= r2) return;
            double d = Math.Sqrt(Math.Max(d2, 1e-8));
            lx /= d; ly /= d; lz /= d;
            double cos = nx * lx + ny * ly + nz * lz;
            if (cos <= 0) return;
            double q = d2 / r2;
            double win = 1 - q * q;
            win = win <= 0 ? 0 : win * win;
            double att = cos * win / Math.Max(d2, 1e-4);
            // Frostbite lights face backward: the shader's angle term is dot(matrixForward, L) with L from the surface to the
            // light (AnalyticLighting.hlsl getAngleAttenuation; rect lights: "direction default to opposite of forward";
            // the IES row is 0.5 + 0.5 dot(forward, light->surface)). ca = cosine from the emission axis -forward.
            att *= Emission(lx * Fx + ly * Fy + lz * Fz);
            if (att <= 0) return;
            e[0] += I[0] * att; e[1] += I[1] * att; e[2] += I[2] * att;
        }

        /// <summary>The share of <see cref="I"/> emitted at cosine <paramref name="ca"/> from the emission axis (-forward).</summary>
        public double Emission(double ca)
        {
            double f = 1;
            if (Cosine)
            {
                if (ca <= 0) return 0;
                f = ca;
            }
            else if (Spot)
            {
                double s = (ca - CosOuter) / Math.Max(CosInner - CosOuter, 1e-3);
                if (s <= 0) return 0;
                if (s > 1) s = 1;
                f = s * s;
            }
            if (Ies != null) f *= Ies.At(ca);
            return f;
        }

        /// <summary>Half-angle cosine of the cone that holds this light's beam (photon sampling); -1 = all directions.</summary>
        public double BeamCos
        {
            get
            {
                if (Spot) return Math.Max(CosOuter, Math.Cos(80 * Math.PI / 180));
                if (Cosine) return 0;
                if (Ies != null) return Math.Cos(30 * Math.PI / 180);
                return -1;
            }
        }
    }

    /// <summary>
    /// The lamps' indirect light by photon tracing. Each light emits photons importance-sampled around its emission axis
    /// (power = intensity x angular factor / pdf / count); the first surface hit (terrain or soup, within the light's
    /// attenuation radius, x its distance window) is the direct light the runtime draws and is not stored; the photon then
    /// bounces (x albedo, cosine-distributed) and every later hit is a deposit. A texel or chart texel reads the deposits
    /// within <see cref="Radius"/> of it (Epanechnikov kernel): the incident radiance integral sum(power / (area cos))
    /// and its direction - what a narrow beam's small, hot footprint puts around it, which a texel's hemisphere of 576
    /// directions mostly misses. Probes read the deposited irradiance at the surfaces they see.
    /// </summary>
    public sealed class LampPhotons
    {
        public const double Radius = 1.0, Cell = 1.0;
        struct Rec { public float X, Y, Z, Nx, Ny, Nz, Dx, Dy, Dz, R, G, B; }
        readonly Dictionary<long, List<Rec>> _terrain = new Dictionary<long, List<Rec>>();
        readonly Dictionary<long, List<Rec>> _mesh = new Dictionary<long, List<Rec>>();
        public long Emitted, TerrainDeposits, MeshDeposits;
        public double[] Albedo, MeshAlbedo;

        static long Key2(int i, int k) { return ((long)i << 32) ^ (uint)k; }
        static long Key3(int i, int j, int k) { return ((long)(i & 0x1FFFFF) << 42) | ((long)(j & 0x1FFFFF) << 21) | (long)(k & 0x1FFFFF); }

        public static LampPhotons Trace(IList<LocalLight> lights, DenseHeightfield hf, GeometrySoup soup, double[] albedo,
                                        int perLight = 2048, int bounces = 2, double reach = 60, int seed = 1234, double[] meshAlbedo = null)
        {
            // the ground reflects the terrain's albedo, meshes theirs (default: the same)
            double[] meshAlb = meshAlbedo ?? albedo;
            var result = new LampPhotons { Albedo = (double[])albedo.Clone(), MeshAlbedo = (double[])meshAlb.Clone() };
            var terrainBag = new System.Collections.Concurrent.ConcurrentBag<List<Rec>>();
            var meshBag = new System.Collections.Concurrent.ConcurrentBag<List<Rec>>();
            long emitted = 0;
            Parallel.For(0, lights.Count, li =>
            {
                LocalLight l = lights[li];
                var rng = new Random(seed ^ (li * 7919));
                var tRecs = new List<Rec>(); var mRecs = new List<Rec>();
                double ax = -l.Fx, ay = -l.Fy, az = -l.Fz;
                double tx, ty, tz, bx, by, bz;
                Frame(ax, ay, az, out tx, out ty, out tz, out bx, out by, out bz);
                double cb = l.BeamCos;
                bool mix = cb > -1;
                double capSolid = 2 * Math.PI * (1 - cb);
                int half = Math.Max(1, perLight / 2);
                var p = new double[3];
                for (int k = 0; k < perLight; k++)
                {
                    bool capDraw = mix && (k & 1) == 0;
                    int s = mix ? k >> 1 : k, n = mix ? half : perLight;
                    double u1 = (s + rng.NextDouble()) / n, u2 = rng.NextDouble();
                    double cz = capDraw ? 1 - u1 * (1 - cb) : 1 - 2 * u1;
                    double sz = Math.Sqrt(Math.Max(0, 1 - cz * cz)), ph = 2 * Math.PI * u2;
                    double dx = tx * sz * Math.Cos(ph) + bx * sz * Math.Sin(ph) + ax * cz;
                    double dy = ty * sz * Math.Cos(ph) + by * sz * Math.Sin(ph) + ay * cz;
                    double dz = tz * sz * Math.Cos(ph) + bz * sz * Math.Sin(ph) + az * cz;
                    double pdf = mix ? 0.5 * (cz >= cb ? 1 / capSolid : 0) + 0.5 / (4 * Math.PI) : 1 / (4 * Math.PI);
                    double em = l.Emission(cz);
                    if (em <= 0) continue;
                    for (int c = 0; c < 3; c++) p[c] = l.I[c] * em / pdf / perLight;
                    // first hit: what the light shines on directly (runtime light), inside its attenuation radius
                    double hx, hy, hz; float nx, ny, nz; bool onTerrain;
                    double t = Hit(hf, soup, l.X, l.Y, l.Z, dx, dy, dz, l.Radius, LightField.FixtureClearance, out hx, out hy, out hz, out nx, out ny, out nz, out onTerrain);
                    if (t < 0) continue;
                    double q = t * t / (l.Radius * l.Radius), win = 1 - q * q;
                    win = win <= 0 ? 0 : win * win;
                    for (int c = 0; c < 3; c++) p[c] *= win;
                    for (int b = 0; b < bounces; b++)
                    {
                        double[] alb = onTerrain ? albedo : meshAlb;
                        for (int c = 0; c < 3; c++) p[c] *= alb[c];
                        if (p[0] + p[1] + p[2] <= 1e-9) break;
                        // cosine-distributed about the surface normal (facing the side the photon came from)
                        double ctx, cty, ctz, cbx, cby, cbz;
                        Frame(nx, ny, nz, out ctx, out cty, out ctz, out cbx, out cby, out cbz);
                        double r1 = rng.NextDouble(), r2 = rng.NextDouble(), sr = Math.Sqrt(r1), phi = 2 * Math.PI * r2, cn = Math.Sqrt(Math.Max(0, 1 - r1));
                        dx = ctx * sr * Math.Cos(phi) + cbx * sr * Math.Sin(phi) + nx * cn;
                        dy = cty * sr * Math.Cos(phi) + cby * sr * Math.Sin(phi) + ny * cn;
                        dz = ctz * sr * Math.Cos(phi) + cbz * sr * Math.Sin(phi) + nz * cn;
                        double ox = hx + nx * 0.02, oy = hy + ny * 0.02, oz = hz + nz * 0.02;
                        t = Hit(hf, soup, ox, oy, oz, dx, dy, dz, reach, 0, out hx, out hy, out hz, out nx, out ny, out nz, out onTerrain);
                        if (t < 0) break;                                 // out to the sky
                        var rec = new Rec { X = (float)hx, Y = (float)hy, Z = (float)hz, Nx = nx, Ny = ny, Nz = nz,
                                            Dx = (float)-dx, Dy = (float)-dy, Dz = (float)-dz, R = (float)p[0], G = (float)p[1], B = (float)p[2] };
                        (onTerrain ? tRecs : mRecs).Add(rec);
                    }
                }
                System.Threading.Interlocked.Add(ref emitted, perLight);
                terrainBag.Add(tRecs); meshBag.Add(mRecs);
            });
            result.Emitted = emitted;
            foreach (var list in terrainBag)
                foreach (Rec r in list)
                {
                    long key = Key2((int)Math.Floor(r.X / Cell), (int)Math.Floor(r.Z / Cell));
                    List<Rec> b; if (!result._terrain.TryGetValue(key, out b)) result._terrain[key] = b = new List<Rec>();
                    b.Add(r); result.TerrainDeposits++;
                }
            foreach (var list in meshBag)
                foreach (Rec r in list)
                {
                    long key = Key3((int)Math.Floor(r.X / Cell), (int)Math.Floor(r.Y / Cell), (int)Math.Floor(r.Z / Cell));
                    List<Rec> b; if (!result._mesh.TryGetValue(key, out b)) result._mesh[key] = b = new List<Rec>();
                    b.Add(r); result.MeshDeposits++;
                }
            return result;
        }

        static double Hit(DenseHeightfield hf, GeometrySoup soup, double ox, double oy, double oz, double dx, double dy, double dz, double maxT,
                          double skip, out double hx, out double hy, out double hz, out float nx, out float ny, out float nz)
        {
            bool onTerrain;
            return Hit(hf, soup, ox, oy, oz, dx, dy, dz, maxT, skip, out hx, out hy, out hz, out nx, out ny, out nz, out onTerrain);
        }

        /// <summary>Nearest surface along a ray (soup hits nearer than <paramref name="skip"/> ignored); the normal faces the ray.</summary>
        static double Hit(DenseHeightfield hf, GeometrySoup soup, double ox, double oy, double oz, double dx, double dy, double dz, double maxT,
                          double skip, out double hx, out double hy, out double hz, out float nx, out float ny, out float nz, out bool onTerrain)
        {
            hx = hy = hz = 0; nx = ny = nz = 0; onTerrain = false;
            double tm = -1; float mx = 0, my = 0, mz = 0;
            if (soup != null)
            {
                double t0 = skip > 0 ? skip : 0;
                double t = soup.Cast(ox + dx * t0, oy + dy * t0, oz + dz * t0, dx, dy, dz, maxT - t0, out mx, out my, out mz);
                if (t > 0) tm = t + t0;
            }
            double tt = LightField.TerrainHit(hf, ox, oy, oz, dx, dy, dz, tm > 0 ? tm : maxT);
            if (tt > 0)
            {
                hx = ox + dx * tt; hy = oy + dy * tt; hz = oz + dz * tt;
                hf.Normal(hx, hz, out nx, out ny, out nz);
                if (nx * dx + ny * dy + nz * dz > 0) { nx = -nx; ny = -ny; nz = -nz; }
                onTerrain = true;
                return tt;
            }
            if (tm > 0)
            {
                hx = ox + dx * tm; hy = oy + dy * tm; hz = oz + dz * tm;
                nx = mx; ny = my; nz = mz;
                return tm;
            }
            return -1;
        }

        static void Frame(double ax, double ay, double az, out double tx, out double ty, out double tz, out double bx, out double by, out double bz)
        {
            // any unit vector t perpendicular to a, and b = a x t
            if (Math.Abs(ay) < 0.9) { tx = az; ty = 0; tz = -ax; } else { tx = 0; ty = -az; tz = ay; }
            double n = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            tx /= n; ty /= n; tz /= n;
            bx = ay * tz - az * ty; by = az * tx - ax * tz; bz = ax * ty - ay * tx;
        }

        /// <summary>Summed power of every deposit (per channel): energy checks.</summary>
        public double[] TotalDeposited()
        {
            var s = new double[3];
            foreach (var b in _terrain.Values.Concat(_mesh.Values))
                foreach (Rec r in b) { s[0] += r.R; s[1] += r.G; s[2] += r.B; }
            return s;
        }

        /// <summary>
        /// Deposits around a surface point with normal n (the terrain's or a mesh's): w = incident radiance integral
        /// sum(power k / (area cos)) per channel, d = w x the direction towards the sources (3 per channel), e = irradiance
        /// sum(power k / area). False when nothing lies within <see cref="Radius"/>.
        /// </summary>
        public bool Gather(double x, double y, double z, double nx, double ny, double nz, bool terrain, double[] w, double[] d, double[] e)
        {
            Array.Clear(w, 0, 3); Array.Clear(d, 0, 9); Array.Clear(e, 0, 3);
            double r2 = Radius * Radius, area = Math.PI * r2;
            int i0 = (int)Math.Floor((x - Radius) / Cell), i1 = (int)Math.Floor((x + Radius) / Cell);
            int k0 = (int)Math.Floor((z - Radius) / Cell), k1 = (int)Math.Floor((z + Radius) / Cell);
            int j0 = (int)Math.Floor((y - Radius) / Cell), j1 = (int)Math.Floor((y + Radius) / Cell);
            bool any = false;
            for (int i = i0; i <= i1; i++)
                for (int k = k0; k <= k1; k++)
                    for (int j = terrain ? 0 : j0; j <= (terrain ? 0 : j1); j++)
                    {
                        List<Rec> b;
                        if (!(terrain ? _terrain : _mesh).TryGetValue(terrain ? Key2(i, k) : Key3(i, j, k), out b)) continue;
                        foreach (Rec r in b)
                        {
                            double ex = r.X - x, ey = r.Y - y, ez = r.Z - z, dd = ex * ex + ey * ey + ez * ez;
                            if (dd >= r2) continue;
                            if (r.Nx * nx + r.Ny * ny + r.Nz * nz < 0.6) continue;       // the other side of a wall, a floor under a wall
                            double kern = 2 * (1 - dd / r2) / area;                       // Epanechnikov, integrates to 1 over the disc
                            double cosIn = Math.Max(0.25, r.Dx * nx + r.Dy * ny + r.Dz * nz);
                            double[] pw = { r.R, r.G, r.B };
                            for (int c = 0; c < 3; c++)
                            {
                                double v = pw[c] * kern;
                                e[c] += v;
                                double vw = v / cosIn;
                                w[c] += vw;
                                d[c * 3] += vw * r.Dx; d[c * 3 + 1] += vw * r.Dy; d[c * 3 + 2] += vw * r.Dz;
                            }
                            any = true;
                        }
                    }
            return any;
        }
    }

    /// <summary>
    /// The lights of one bake and what they put on the scene: a raster of terrain illuminance aligned with the heightfield
    /// (lux per channel, shadowed by the terrain and the geometry soup), an exact evaluation at any surface point (mesh
    /// hits), and a bucket grid so points out of every light's reach cost nothing.
    /// </summary>
    public sealed class LightField
    {
        public readonly List<LocalLight> Lights;
        public readonly DenseHeightfield Hf;
        public readonly GeometrySoup Soup;
        public readonly float[] Terrain;          // N * N * 3, lux
        public readonly double MaxRadius;
        const double Cell = 32;
        readonly Dictionary<long, List<LocalLight>> _grid = new Dictionary<long, List<LocalLight>>();

        static long Key(int i, int j) { return ((long)i << 32) ^ (uint)j; }

        public LightField(List<LocalLight> lights, DenseHeightfield hf, GeometrySoup soup)
        {
            Lights = lights; Hf = hf; Soup = soup;
            foreach (var l in lights)
            {
                MaxRadius = Math.Max(MaxRadius, l.Radius);
                int i0 = (int)Math.Floor((l.X - l.Radius) / Cell), i1 = (int)Math.Floor((l.X + l.Radius) / Cell);
                int j0 = (int)Math.Floor((l.Z - l.Radius) / Cell), j1 = (int)Math.Floor((l.Z + l.Radius) / Cell);
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                    {
                        List<LocalLight> b;
                        if (!_grid.TryGetValue(Key(i, j), out b)) _grid[Key(i, j)] = b = new List<LocalLight>();
                        b.Add(l);
                    }
            }
            int n = hf.N;
            Terrain = new float[n * n * 3];
            var gate = new object();
            Parallel.ForEach(lights, l =>
            {
                int ii0 = Math.Max(0, (int)Math.Floor((l.X - l.Radius - hf.MinX) / hf.Spacing));
                int ii1 = Math.Min(n - 1, (int)Math.Ceiling((l.X + l.Radius - hf.MinX) / hf.Spacing));
                int jj0 = Math.Max(0, (int)Math.Floor((l.Z - l.Radius - hf.MinZ) / hf.Spacing));
                int jj1 = Math.Min(n - 1, (int)Math.Ceiling((l.Z + l.Radius - hf.MinZ) / hf.Spacing));
                if (ii0 > ii1 || jj0 > jj1) return;
                var local = new float[(ii1 - ii0 + 1) * (jj1 - jj0 + 1) * 3];
                var e = new double[3];
                for (int j = jj0; j <= jj1; j++)
                    for (int i = ii0; i <= ii1; i++)
                    {
                        double x = hf.MinX + i * hf.Spacing, z = hf.MinZ + j * hf.Spacing, y = hf.H[j * n + i];
                        float nx, ny, nz;
                        hf.Normal(x, z, out nx, out ny, out nz);
                        e[0] = e[1] = e[2] = 0;
                        l.Illuminance(x, y, z, nx, ny, nz, e);
                        if (e[0] + e[1] + e[2] <= 0 || !Visible(x + nx * 0.05, y + ny * 0.05, z + nz * 0.05, l)) continue;
                        int o = ((j - jj0) * (ii1 - ii0 + 1) + (i - ii0)) * 3;
                        local[o] = (float)e[0]; local[o + 1] = (float)e[1]; local[o + 2] = (float)e[2];
                    }
                lock (gate)
                    for (int j = jj0; j <= jj1; j++)
                        for (int i = ii0; i <= ii1; i++)
                        {
                            int o = ((j - jj0) * (ii1 - ii0 + 1) + (i - ii0)) * 3, t = (j * n + i) * 3;
                            Terrain[t] += local[o]; Terrain[t + 1] += local[o + 1]; Terrain[t + 2] += local[o + 2];
                        }
            });
        }

        /// <summary>Is any light's sphere within <paramref name="reach"/> of (x, z)?</summary>
        public bool Near(double x, double z, double reach)
        {
            int i0 = (int)Math.Floor((x - reach) / Cell), i1 = (int)Math.Floor((x + reach) / Cell);
            int j0 = (int)Math.Floor((z - reach) / Cell), j1 = (int)Math.Floor((z + reach) / Cell);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    List<LocalLight> b;
                    if (!_grid.TryGetValue(Key(i, j), out b)) continue;
                    foreach (var l in b)
                    {
                        double dx = l.X - x, dz = l.Z - z, r = l.Radius + reach;
                        if (dx * dx + dz * dz < r * r) return true;
                    }
                }
            return false;
        }

        /// <summary>
        /// Clear line from a surface point to the light. The last <see cref="FixtureClearance"/> metres are not tested: a
        /// wall lamp sits in (or a few centimetres inside) the collision surface it hangs on, and its own fixture is
        /// no occluder.
        /// </summary>
        bool Visible(double x, double y, double z, LocalLight l)
        {
            double dx = l.X - x, dy = l.Y - y, dz = l.Z - z, d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d <= FixtureClearance) return true;
            dx /= d; dy /= d; dz /= d;
            double reach = d - FixtureClearance;
            for (double t = 0.5; t < reach; t += 0.5)
                if (Hf.Sample(x + dx * t, z + dz * t) > y + dy * t + 0.05) return false;
            if (Soup != null)
            {
                float a, b, c;
                if (Soup.Cast(x, y, z, dx, dy, dz, reach, out a, out b, out c) > 0) return false;
            }
            return true;
        }

        public const double FixtureClearance = 0.3;

        /// <summary>Illuminance on the terrain at (x, z), bilinear from the raster (e is overwritten).</summary>
        public void TerrainIlluminance(double x, double z, double[] e)
        {
            var hf = Hf;
            double fx = (x - hf.MinX) / hf.Spacing, fz = (z - hf.MinZ) / hf.Spacing, lim = hf.N - 1.001;
            if (fx < 0) fx = 0; else if (fx > lim) fx = lim;
            if (fz < 0) fz = 0; else if (fz > lim) fz = lim;
            int ix = (int)fx, iz = (int)fz;
            double tx = fx - ix, tz = fz - iz;
            int n = hf.N;
            for (int c = 0; c < 3; c++)
            {
                double a = Terrain[(iz * n + ix) * 3 + c], b = Terrain[(iz * n + ix + 1) * 3 + c];
                double cc = Terrain[((iz + 1) * n + ix) * 3 + c], d = Terrain[((iz + 1) * n + ix + 1) * 3 + c];
                double ab = a + (b - a) * tx, cd = cc + (d - cc) * tx;
                e[c] = ab + (cd - ab) * tz;
            }
        }

        /// <summary>Illuminance at a surface point (a mesh hit) with normal n, shadowed (e is overwritten).</summary>
        public void SurfaceIlluminance(double x, double y, double z, double nx, double ny, double nz, double[] e)
        {
            e[0] = e[1] = e[2] = 0;
            List<LocalLight> b;
            if (!_grid.TryGetValue(Key((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell)), out b)) return;
            var tmp = new double[3];
            foreach (var l in b)
            {
                tmp[0] = tmp[1] = tmp[2] = 0;
                l.Illuminance(x, y, z, nx, ny, nz, tmp);
                if (tmp[0] + tmp[1] + tmp[2] <= 0 || !Visible(x + nx * 0.05, y + ny * 0.05, z + nz * 0.05, l)) continue;
                e[0] += tmp[0]; e[1] += tmp[1]; e[2] += tmp[2];
            }
        }

        /// <summary>
        /// Where a ray from (ox, oy, oz) meets the heightfield within maxDist (marched 0.25 m growing 8% per step, the
        /// crossing refined by bisection); -1 when it does not.
        /// </summary>
        public static double TerrainHit(DenseHeightfield hf, double ox, double oy, double oz, double dx, double dy, double dz, double maxDist)
        {
            double t0 = 0, step = 0.25;
            for (double t = step; t <= maxDist; t += step, step *= 1.08)
            {
                if (hf.Sample(ox + dx * t, oz + dz * t) >= oy + dy * t)
                {
                    double a = t0, b = t;
                    for (int it = 0; it < 12; it++)
                    {
                        double m = 0.5 * (a + b);
                        if (hf.Sample(ox + dx * m, oz + dz * m) >= oy + dy * m) b = m; else a = m;
                    }
                    return b;
                }
                t0 = t;
            }
            return -1;
        }

        /// <summary>
        /// Illuminance of the surface a ray from (ox, oy, oz) along d sees first: the mesh it hits within meshDist
        /// (hit distance and normal from the soup) or the terrain within maxDist. False when it sees neither.
        /// </summary>
        public bool SeenIlluminance(double ox, double oy, double oz, double dx, double dy, double dz, double meshHit, float mnx, float mny, float mnz,
                                    double maxDist, double[] e)
        {
            if (meshHit > 0)
            {
                SurfaceIlluminance(ox + dx * meshHit, oy + dy * meshHit, oz + dz * meshHit, mnx, mny, mnz, e);
                return true;
            }
            double t = TerrainHit(Hf, ox, oy, oz, dx, dy, dz, maxDist);
            if (t < 0) { e[0] = e[1] = e[2] = 0; return false; }
            TerrainIlluminance(ox + dx * t, oz + dz * t, e);
            return true;
        }
    }
}
