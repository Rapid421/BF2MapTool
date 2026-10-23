using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// A scene for DICE's Flux lightmap baker (Frostbite's path tracer for Enlighten-format lightmaps; FIFA21_SOURCE
    /// External/EA/Flux, API v64), written as the FXS1 file `tools/fluxbake` reads. The fields mirror Flux.h:
    /// LightmapVertex, LightmapFace, LightmapMesh, LightmapMeshInstance, LightmapMaterial, LightSource, TextureBuffer and
    /// light-probe point sets. Everything a Frostbite freeze bake hands Flux is here; nothing is fitted.
    /// </summary>
    public sealed class FluxScene
    {
        public int AtlasWidth, AtlasHeight;
        public int Samples = 256, Depth = 5, Antialiasing = 1, FilterSize = 0;
        public float AoRadius = 1f, PreExposure = 1f;
        public bool ProbeShl2 = true, Stitch = true;
        /// <summary>Flux ComponentMask: AO | SkyVisibility | LightProbes | Lightmaps | Indirect (+Direct only on request).</summary>
        public uint Components = (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 5);

        public sealed class Texture { public int W, H; public uint Format = 2, Layout = 1; public float[] Rgba; }   // RGBA_Float, 2D
        public sealed class Light
        {
            public Guid Id;
            public float[] Position = new float[3], Forward = { 0, 0, 1 }, Left = { 1, 0, 0 }, Up = { 0, 1, 0 };
            public float[] Color = new float[3], BounceColorScale = { 1, 1, 1 };
            public uint Type;                    // 0 directional, 1 sphere, 2 spot, 3 tube, 4 rectangular, 5 environment
            public bool DirectEnable, IndirectEnable;
            public float AttenuationRadius, AttenuationOffset, SphereRadius, ConeInner, ConeOuter, DiskRadius;
            public float TubeRadius, TubeWidth; public bool IsCapsule;
            public float RectWidth, RectHeight; public uint RectShape;
            public float FrustumNear, FrustumFar, FrustumOuterAngle, FrustumAspect;
            public uint TextureId;
        }
        public sealed class Material { public Guid Id; public float[] Albedo = { 0.3f, 0.3f, 0.3f }, Emissive = new float[3]; public float Opacity = 1; public uint Backface; }
        public sealed class Mesh
        {
            public Guid Id;
            public float[] Vertices;             // 10 floats per vertex: position 3, normal 3, lightmap uv 2, albedo uv 2
            public int[] Faces;                  // 3 indices per face
            public Guid[] FaceMaterials;         // one per face
            public int VertexCount => Vertices.Length / 10;

            /// <summary>
            /// Texel density of a placement: sqrt(texels its triangles cover / their world area), from the radiosity uvs
            /// scaled by the instance's uv transform onto a W x H atlas and the positions under the row transform.
            /// </summary>
            public double TexelDensity(float[] t, float[] uvTransform, int atlasW, int atlasH)
            {
                double world = 0, uvArea = 0;
                for (int f = 0; f + 2 < Faces.Length; f += 3)
                {
                    int a = Faces[f] * 10, b = Faces[f + 1] * 10, c = Faces[f + 2] * 10;
                    double[] pa = Xf(a, t), pb = Xf(b, t), pc = Xf(c, t);
                    double ex = pb[0] - pa[0], ey = pb[1] - pa[1], ez = pb[2] - pa[2], fx = pc[0] - pa[0], fy = pc[1] - pa[1], fz = pc[2] - pa[2];
                    double cx = ey * fz - ez * fy, cy = ez * fx - ex * fz, cz = ex * fy - ey * fx;
                    world += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
                    double u1 = Vertices[b + 6] - Vertices[a + 6], v1 = Vertices[b + 7] - Vertices[a + 7], u2 = Vertices[c + 6] - Vertices[a + 6], v2 = Vertices[c + 7] - Vertices[a + 7];
                    uvArea += 0.5 * Math.Abs(u1 * v2 - v1 * u2);
                }
                double texels = uvArea * Math.Abs(uvTransform[0] * atlasW) * Math.Abs(uvTransform[3] * atlasH);
                return world > 1e-9 ? Math.Sqrt(texels / world) : 0;
            }

            double[] Xf(int o, float[] t)
            {
                double x = Vertices[o], y = Vertices[o + 1], z = Vertices[o + 2];
                return new[] { x * t[0] + y * t[3] + z * t[6] + t[9], x * t[1] + y * t[4] + z * t[7] + t[10], x * t[2] + y * t[5] + z * t[8] + t[11] };
            }
        }
        public sealed class Instance { public Guid Id, MeshId; public float[] Transform = new float[12], UvTransform = new float[6]; public bool ReceiveOnly; }
        public sealed class ProbeSet { public Guid Id; public float[] Positions; }

        public readonly List<Texture> Textures = new List<Texture>();
        public readonly List<Light> Lights = new List<Light>();
        public readonly List<Material> Materials = new List<Material>();
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly List<Instance> Instances = new List<Instance>();
        public readonly List<ProbeSet> ProbeSets = new List<ProbeSet>();

        public void Write(string path)
        {
            using (var w = new BinaryWriter(new BufferedStream(File.Create(path), 1 << 20)))
            {
                w.Write(new[] { (byte)'F', (byte)'X', (byte)'S', (byte)'1' });
                w.Write(1u); w.Write((uint)AtlasWidth); w.Write((uint)AtlasHeight);
                w.Write((uint)Samples); w.Write((uint)Depth); w.Write(AoRadius); w.Write(Components); w.Write((uint)Antialiasing);
                w.Write(ProbeShl2 ? 1u : 0u); w.Write((uint)FilterSize); w.Write(Stitch ? 1u : 0u); w.Write(PreExposure);
                w.Write((uint)Textures.Count);
                foreach (Texture t in Textures)
                {
                    w.Write((uint)t.W); w.Write((uint)t.H); w.Write(t.Format); w.Write(t.Layout);
                    foreach (float f in t.Rgba) w.Write(f);
                }
                w.Write((uint)Lights.Count);
                foreach (Light l in Lights)
                {
                    w.Write(l.Id.ToByteArray());
                    F3(w, l.Position); F3(w, l.Forward); F3(w, l.Left); F3(w, l.Up); F3(w, l.Color); F3(w, l.BounceColorScale);
                    w.Write(l.Type); w.Write((l.DirectEnable ? 1u : 0u) | (l.IndirectEnable ? 2u : 0u));
                    w.Write(l.AttenuationRadius); w.Write(l.AttenuationOffset); w.Write(l.SphereRadius); w.Write(l.ConeInner); w.Write(l.ConeOuter); w.Write(l.DiskRadius);
                    w.Write(l.TubeRadius); w.Write(l.TubeWidth); w.Write(l.IsCapsule ? 1u : 0u);
                    w.Write(l.RectWidth); w.Write(l.RectHeight); w.Write(l.RectShape);
                    w.Write(l.FrustumNear); w.Write(l.FrustumFar); w.Write(l.FrustumOuterAngle); w.Write(l.FrustumAspect);
                    w.Write(l.TextureId);
                }
                w.Write((uint)Materials.Count);
                foreach (Material m in Materials)
                {
                    w.Write(m.Id.ToByteArray()); w.Write(0u);
                    F3(w, m.Albedo); F3(w, m.Emissive); w.Write(m.Opacity); w.Write(0f); w.Write(0u); w.Write(0u); w.Write(m.Backface);
                }
                w.Write((uint)Meshes.Count);
                foreach (Mesh m in Meshes)
                {
                    w.Write(m.Id.ToByteArray());
                    w.Write((uint)m.VertexCount);
                    foreach (float f in m.Vertices) w.Write(f);
                    int nf = m.Faces.Length / 3;
                    w.Write((uint)nf);
                    for (int f = 0; f < nf; f++)
                    {
                        w.Write((uint)m.Faces[f * 3]); w.Write((uint)m.Faces[f * 3 + 1]); w.Write((uint)m.Faces[f * 3 + 2]);
                        w.Write(0xFFFFFFFFu);                 // chart id: Flux generates the charts from uv connectivity
                        w.Write(m.FaceMaterials[f].ToByteArray());
                    }
                }
                w.Write((uint)Instances.Count);
                foreach (Instance x in Instances)
                {
                    w.Write(x.Id.ToByteArray()); w.Write(x.MeshId.ToByteArray()); w.Write(0u);
                    foreach (float f in x.Transform) w.Write(f);
                    foreach (float f in x.UvTransform) w.Write(f);
                    w.Write(x.ReceiveOnly ? 1u : 0u);
                }
                w.Write((uint)ProbeSets.Count);
                foreach (ProbeSet p in ProbeSets)
                {
                    w.Write(p.Id.ToByteArray());
                    w.Write((uint)(p.Positions.Length / 3));
                    foreach (float f in p.Positions) w.Write(f);
                }
            }
        }

        static void F3(BinaryWriter w, float[] v) { w.Write(v[0]); w.Write(v[1]); w.Write(v[2]); }

        // ---------------------------------------------------------------------------------------------------------------
        // Hashes the Enlighten shader database keys its materials by (FIFA21_SOURCE EnlightenUtil.h createEnlightenShaderHash,
        // Core/Misc/Fletcher.cpp, Base/String/StringUtil.h).

        /// <summary>Frostbite calcFletcher32(byteCount, data): big-endian 16-bit words, both sums folded per 360-word block.</summary>
        public static uint Fletcher32(byte[] data)
        {
            uint s1 = 0, s2 = 0;
            int words = data.Length / 2, i = 0;
            while (words > 0)
            {
                int len = Math.Min(words, 360);
                words -= len;
                for (int k = 0; k < len; k++, i += 2)
                {
                    uint v = (uint)((data[i] << 8) | data[i + 1]);
                    s1 += v; s2 += s1;
                }
                s1 = (s1 & 0xffff) + (s1 >> 16);
                s2 = (s2 & 0xffff) + (s2 >> 16);
            }
            if ((data.Length & 1) != 0)
            {
                s1 += (uint)(data[data.Length - 1] << 8); s2 += s1;
                s1 = (s1 & 0xffff) + (s1 >> 16);
                s2 = (s2 & 0xffff) + (s2 >> 16);
            }
            return (s1 << 16) | s2;
        }

        /// <summary>Frostbite hashQuickLowerCase: h = h * 33 ^ lower(c), from 5381.</summary>
        public static uint HashQuickLowerCase(string s)
        {
            uint h = 5381;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s ?? ""))
            {
                uint c = b;
                if (c - 'A' <= 'Z' - 'A') c += 'a' - 'A';
                h = unchecked(h * 33) ^ c;
            }
            return h;
        }

        // ---------------------------------------------------------------------------------------------------------------
        // The sky. Flux reads a 16 x 16 RGBA float cube (faces stacked vertically) through imageSampleCubemapNearest:
        // world direction with z negated (Frostbite cubes are D3D left-handed), cartesianToCubemap, floor(uv * 16).

        public const int CubeRes = 16;

        /// <summary>The Flux cube texel (face, x, y) a world direction reads (FIFA21_SOURCE Flux Envmap.h / ImageProcessing.cpp).</summary>
        public static void CubeTexel(double dx, double dy, double dz, int res, out int face, out int px, out int py)
        {
            double rx = dx, ry = dy, rz = -dz;
            double ax = Math.Abs(rx), ay = Math.Abs(ry), az = Math.Abs(rz), ma = Math.Max(Math.Max(ax, ay), az);
            double sc, tc;
            if (ax > ay && ax > az) { sc = rx > 0 ? rz : -rz; tc = ry; face = rx > 0 ? 0 : 1; }
            else if (ay > ax && ay > az) { sc = rx; tc = ry > 0 ? rz : -rz; face = ry > 0 ? 2 : 3; }
            else { sc = rz > 0 ? -rx : rx; tc = ry; face = rz > 0 ? 5 : 4; }
            double u = 0.5 * (sc / ma) + 0.5, v = -0.5 * (tc / ma) + 0.5;
            px = Math.Max(0, Math.Min(res - 1, (int)Math.Floor(u * res)));
            py = Math.Max(0, Math.Min(res - 1, (int)Math.Floor(v * res)));
        }

        /// <summary>
        /// A cube texture whose every texel is the mean of <paramref name="radiance"/> over the directions that read it
        /// (a dense sphere sampling through <see cref="CubeTexel"/>): the box-filtered sky Flux sees, in its own layout.
        /// </summary>
        public static Texture BuildCube(Func<double, double, double, double[]> radiance, int res = CubeRes, int samplesPerAxis = 512)
        {
            var sum = new double[6 * res * res * 3];
            var cnt = new double[6 * res * res];
            // equal-area sampling of the sphere: z uniform in [-1, 1], longitude uniform
            for (int i = 0; i < samplesPerAxis; i++)
            {
                double y = 1 - 2 * (i + 0.5) / samplesPerAxis, r = Math.Sqrt(Math.Max(0, 1 - y * y));
                int nPhi = 2 * samplesPerAxis;
                for (int j = 0; j < nPhi; j++)
                {
                    double phi = 2 * Math.PI * (j + 0.5) / nPhi, x = r * Math.Cos(phi), z = r * Math.Sin(phi);
                    int f, px, py;
                    CubeTexel(x, y, z, res, out f, out px, out py);
                    int t = (f * res + py) * res + px;
                    double[] L = radiance(x, y, z);
                    sum[t * 3] += L[0]; sum[t * 3 + 1] += L[1]; sum[t * 3 + 2] += L[2]; cnt[t]++;
                }
            }
            var tex = new Texture { W = res, H = res, Format = 2, Layout = 2, Rgba = new float[6 * res * res * 4] };
            for (int t = 0; t < 6 * res * res; t++)
            {
                double n = Math.Max(1, cnt[t]);
                tex.Rgba[t * 4] = (float)(sum[t * 3] / n); tex.Rgba[t * 4 + 1] = (float)(sum[t * 3 + 1] / n);
                tex.Rgba[t * 4 + 2] = (float)(sum[t * 3 + 2] / n); tex.Rgba[t * 4 + 3] = 1f;
            }
            return tex;
        }

        /// <summary>The VE EnlightenComponent sky box (FIFA21_SOURCE EnlightenFreezePipeline.cpp computeSkyBox).</summary>
        public sealed class SkyBox
        {
            public bool Enable;
            public double[] SkyColor = new double[3], GroundColor = new double[3], SunLightColor = new double[3], BackLightColor = new double[3];
            public double SunLightSize, BackLightSize, BackLightRotationX, BackLightRotationY;
            public string BlendMode = "SkyBoxBlendMode_Add";
            public double Blend = 1;

            public double[] Eval(double dx, double dy, double dz, double[] sunDir)
            {
                double sunF = Math.Max(0, dx * sunDir[0] + dy * sunDir[1] + dz * sunDir[2]);
                sunF = 1 - Clamp((1 - sunF) / (0.5 * SunLightSize), 0, 1);
                double th = BackLightRotationX * Math.PI / 180, ph = BackLightRotationY * Math.PI / 180;
                double bx = Math.Sin(th) * Math.Cos(ph), by = Math.Sin(ph), bz = Math.Cos(th) * Math.Cos(ph);
                double backF = Math.Max(0, dx * bx + dy * by + dz * bz);
                backF = 1 - Clamp((1 - backF) / (0.5 * BackLightSize), 0, 1);
                double norm = Math.Max(0, 1 - sunF - backF), skyF = (dy + 1) * 0.5;
                var c = new double[3];
                for (int k = 0; k < 3; k++)
                    c[k] = (SkyColor[k] * skyF + GroundColor[k] * (1 - skyF)) * norm + SunLightColor[k] * sunF + BackLightColor[k] * backF;
                return c;
            }

            /// <summary>Combine with a captured sky value (DistantLightingModule.cpp retrieveSkyBoxValues).</summary>
            public double[] Combine(double[] captured, double[] box)
            {
                var r = new double[3];
                for (int k = 0; k < 3; k++)
                {
                    double a = captured != null ? captured[k] : 0;
                    if (BlendMode == "SkyBoxBlendMode_Multiply") r[k] = a * box[k];
                    else if (BlendMode == "SkyBoxBlendMode_Lerp") r[k] = a + (box[k] - a) * (Blend < 1e-6 ? 0 : Blend);
                    else r[k] = a + box[k];
                }
                return r;
            }

            static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
        }

        /// <summary>The VE sun direction (towards the sun): (sin X cos Y, sin Y, cos X cos Y) (FluxLightingUtils.cpp getSunDirection).</summary>
        public static double[] SunDirection(double rotX, double rotY)
        {
            double t = rotX * Math.PI / 180, p = rotY * Math.PI / 180;
            return new[] { Math.Sin(t) * Math.Cos(p), Math.Sin(p), Math.Cos(t) * Math.Cos(p) };
        }

        /// <summary>The direction Flux bakes the sun along: normal(-sun x (1, 1/SunShadowHeightScale, 1)) (getScaledShadowDir).</summary>
        public static double[] ScaledShadowDirection(double rotX, double rotY, double heightScale)
        {
            double[] s = SunDirection(rotX, rotY);
            double hs = Math.Max(0.0001, heightScale);
            double x = -s[0], y = -s[1] / hs, z = -s[2], l = Math.Sqrt(x * x + y * y + z * z);
            return new[] { x / l, y / l, z / l };
        }

        // ---------------------------------------------------------------------------------------------------------------
        // Lights (FIFA21_SOURCE FluxLightingUtils.cpp fluxExportLightSources / getBakedLightInfo, LightingUtils.cpp).

        /// <summary>
        /// A Pbr*LightEntityData record as the Flux light the freeze pipeline exports: colour = Color x Intensity (x Dimmer)
        /// / (area x pi) (punctual sphere area 4, punctual spot / frustum 1, luminance units as is; IES: luminance =
        /// max candela x IESMultiplier, or max candela x Intensity / lumens as a mask), bounceColorScale =
        /// RadiosityColorScale, direct = DirectLightmapEnable, indirect = AffectRadiosity, cone angles in radians,
        /// left = -right (the pipeline's "match what Frosted exports"). Null with a reason for a light the bake skips.
        /// </summary>
        public static Light FromRecord(string type, IDictionary<string, string> f, float[] right, float[] up, float[] forward, float[] trans,
                                       IesProfile ies, uint iesTexture, out string why)
        {
            why = null;
            Func<string, double, double> num = (k, d) =>
            {
                string v; double r;
                return f != null && f.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : d;
            };
            Func<string, bool, bool> flag = (k, d) => { string v; bool r; return f != null && f.TryGetValue(k, out v) && bool.TryParse(v, out r) ? r : d; };
            Func<string, string> str = k => { string v; return f != null && f.TryGetValue(k, out v) ? v : null; };
            Func<string, double[], double[]> vec = (k, d) =>
            {
                string s = str(k);
                if (s == null) return d;
                string[] p = s.Split(',');
                var r = (double[])d.Clone();
                for (int i = 0; i < 3 && i < p.Length; i++) double.TryParse(p[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i]);
                return r;
            };
            if (!flag("Enabled", true)) { why = "disabled"; return null; }
            bool indirect = flag("AffectRadiosity", true), direct = flag("DirectLightmapEnable", false);
            if (!indirect && !direct) { why = "bakes nothing (AffectRadiosity and DirectLightmapEnable off)"; return null; }
            if (trans == null) { why = "no transform"; return null; }
            double[] col = vec("Color", new double[] { 1, 1, 1 });
            double intensity = num("Intensity", 0) * num("Dimmer", 1);
            bool luminance = str("LightUnit") == "LightUnitType_Luminance";
            var l = new Light
            {
                Position = trans, Forward = forward ?? new float[] { 0, 0, 1 }, Up = up ?? new float[] { 0, 1, 0 },
                Left = right != null ? new[] { -right[0], -right[1], -right[2] } : new float[] { -1, 0, 0 },
                DirectEnable = direct, IndirectEnable = indirect,
                AttenuationRadius = (float)num("AttenuationRadius", 0), AttenuationOffset = (float)num("AttenuationOffset", 0),
            };
            double[] rs = vec("RadiosityColorScale", new double[] { 1, 1, 1 });
            l.BounceColorScale = new[] { (float)rs[0], (float)rs[1], (float)rs[2] };
            bool useIes = ies != null && (type == "PbrSphereLightEntityData" || type == "PbrSpotLightEntityData");
            if (useIes)
            {
                luminance = true;
                intensity = flag("UseIESProfileAsMask", false) ? ies.MaxCandela * num("Intensity", 0) / Math.Max(ies.Lumens, 1e-6)
                                                               : ies.MaxCandela * num("IESMultiplier", 1);
                intensity *= num("Dimmer", 1);
                l.TextureId = iesTexture;
            }
            double area;
            switch (type)
            {
                case "PbrSphereLightEntityData":
                {
                    l.Type = 1; double r = num("SphereRadius", 0); l.SphereRadius = (float)r;
                    bool punctual = r == 0 && !luminance;
                    area = punctual ? 4 : 4 * Math.PI * r * r;
                    break;
                }
                case "PbrSpotLightEntityData":
                {
                    l.Type = 2; double r = num("DiscRadius", 0); l.DiskRadius = (float)r;
                    l.ConeInner = (float)(num("InnerAngle", 0) * Math.PI / 180); l.ConeOuter = (float)(num("OuterAngle", 90) * Math.PI / 180);
                    bool punctual = r == 0 && !luminance;
                    area = punctual ? 1 : Math.PI * r * r;
                    break;
                }
                case "PbrTubeLightEntityData":
                {
                    l.Type = 3; l.TubeRadius = (float)num("TubeRadius", 0); l.TubeWidth = (float)num("TubeWidth", 0); l.IsCapsule = flag("IsCapsule", false);
                    double tr = Math.Max(l.TubeRadius, 0.01), tw = Math.Max(l.TubeWidth, 0.01);   // PUNCTUAL_LIGHT_THRESHOLD
                    area = 2 * Math.PI * tr * tw + (l.IsCapsule ? 4 * Math.PI * tr * tr : 0);
                    break;
                }
                case "PbrRectangularLightEntityData":
                {
                    l.Type = 4; l.RectWidth = (float)num("Width", 0); l.RectHeight = (float)num("Height", 0);
                    string shape = str("Shape") ?? "RectangularLightShape_Rectangular";
                    bool punctual = shape != "RectangularLightShape_Rectangular";
                    area = punctual ? 1 : l.RectWidth * l.RectHeight;
                    if (shape == "RectangularLightShape_Frustum")
                    {
                        l.RectShape = 1; l.FrustumNear = (float)num("ShadowNearRadius", num("FrustumNear", 0)); l.FrustumFar = l.AttenuationRadius;
                        l.FrustumOuterAngle = (float)(num("RectOuterShadowAngle", num("OuterAngle", 90)) * Math.PI / 180); l.FrustumAspect = (float)num("Aspect", 1);
                    }
                    else if (shape == "RectangularLightShape_OrthoFrustum")
                    {
                        l.RectShape = 2; l.FrustumNear = (float)num("ShadowNearRadius", num("FrustumNear", 0)); l.FrustumFar = l.AttenuationRadius;
                    }
                    break;
                }
                default: why = "not a PBR light type (" + type + ")"; return null;
            }
            double k = luminance ? intensity : intensity / (Math.Max(area, 1e-8) * Math.PI);
            l.Color = new[] { (float)(col[0] * k), (float)(col[1] * k), (float)(col[2] * k) };
            if (l.Color[0] <= 0 && l.Color[1] <= 0 && l.Color[2] <= 0) { why = "dark"; return null; }
            return l;
        }
    }
}
