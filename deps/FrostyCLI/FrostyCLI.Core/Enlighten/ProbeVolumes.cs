using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// What BFII's runtime does with a bake's probe sets for a Dynamic (probe-lit) object, and interior probe volumes
    /// built from scattered probe sets.
    ///
    /// BFII (LightProbeSystem::updateDynamicLightProbeAtPos) lights a Dynamic instance from the probe sets whose box holds
    /// its bounding-sphere centre, skipping every set flagged staticProbeSet (+0xCC): those "scattered" sets serve only
    /// LightProbe-type instances (nearest probe within 3 m). Sets are taken in descending priority; once inside one, lower
    /// priorities stop; outside every box, sets within their blend distance blend against the default probe, else the
    /// default probe alone (set 0, probe 0). SWBF 2015 bakes ship their interior probes only as scattered sets, so ported to
    /// BFII a Dynamic prop inside a 2015 interior is lit by the outdoor terrain probes above it (Hoth: ~100x too bright,
    /// the "emissive" cables). DICE's own BFII interiors carry regular probe volumes (priority 1..2001) for that.
    /// </summary>
    public static class ProbeVolumes
    {
        public const int ProbeBytes = 144;          // 9 SH coefficients x float4 (RGB + sky-visibility SH)

        public static bool IsStatic(EnlightenDatabase.ProbeSet ps) => ps.Raw[0xCC] != 0;

        /// <summary>Mean RGB of a probe's DC coefficient: a direction-free brightness.</summary>
        public static double Dc(byte[] probes, int index)
        {
            int o = index * ProbeBytes;
            return (BitConverter.ToSingle(probes, o) + BitConverter.ToSingle(probes, o + 4) + BitConverter.ToSingle(probes, o + 8)) / 3.0;
        }

        static bool Inside(EnlightenDatabase.ProbeSet ps, double x, double y, double z, double tol = 0)
        {
            return x >= ps.MinX - tol && x <= ps.MaxX + tol && y >= ps.MinY - tol && y <= ps.MaxY + tol && z >= ps.MinZ - tol && z <= ps.MaxZ + tol;
        }

        /// <summary>sampleVoxelGrid of the runtime, on the DC term (identity-transform sets).</summary>
        static double SampleDc(EnlightenDatabase.ProbeSet ps, byte[] probes, double x, double y, double z)
        {
            uint[] dim = { ps.DimX, ps.DimY, ps.DimZ };
            double[] p = { x, y, z }, mn = { ps.MinX, ps.MinY, ps.MinZ }, mx = { ps.MaxX, ps.MaxY, ps.MaxZ };
            var i0 = new uint[3]; var i1 = new uint[3]; var f = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double v = Math.Max(0.0, dim[k] * (p[k] - mn[k]) / Math.Max(mx[k] - mn[k], 1e-6) - 0.5);
                f[k] = v - Math.Floor(v);
                i0[k] = Math.Min((uint)v, dim[k] - 1); i1[k] = Math.Min(i0[k] + 1, dim[k] - 1);
            }
            double s = 0;
            for (int c = 0; c < 8; c++)
            {
                uint xi = (c & 1) != 0 ? i1[0] : i0[0], yi = (c & 2) != 0 ? i1[1] : i0[1], zi = (c & 4) != 0 ? i1[2] : i0[2];
                double w = ((c & 1) != 0 ? f[0] : 1 - f[0]) * ((c & 2) != 0 ? f[1] : 1 - f[1]) * ((c & 4) != 0 ? f[2] : 1 - f[2]);
                s += w * Dc(probes, (int)(ps.GlobalOffset + dim[0] * dim[1] * zi + dim[0] * yi + xi));
            }
            return s;
        }

        /// <summary>The DC brightness BFII's dynamic lookup gives an instance centred at (x, y, z).</summary>
        public static double DynamicLookupDc(EnlightenDatabase db, byte[] probes, double x, double y, double z)
        {
            var order = Enumerable.Range(0, db.ProbeSets.Count).Where(i => !IsStatic(db.ProbeSets[i]))
                                  .OrderByDescending(i => db.ProbeSets[i].Priority).ToList();
            double sum = 0, weight = 0; bool found = false; uint cur = uint.MaxValue, maxP = 0;
            foreach (int i in order)
            {
                var ps = db.ProbeSets[i];
                if (found && ps.Priority < cur) break;
                cur = ps.Priority;
                if (Inside(ps, x, y, z))
                {
                    double w = cur < maxP ? Math.Max(1 - weight, 0) : 1.0;
                    sum += w * SampleDc(ps, probes, x, y, z); weight += w; found = true; maxP = Math.Max(cur, maxP);
                }
                else if (ps.BlendDistance > 0)
                {
                    double cx = Math.Max(ps.MinX, Math.Min(ps.MaxX, x)), cy = Math.Max(ps.MinY, Math.Min(ps.MaxY, y)), cz = Math.Max(ps.MinZ, Math.Min(ps.MaxZ, z));
                    double w1 = ((cx - x) * (cx - x) + (cy - y) * (cy - y) + (cz - z) * (cz - z)) / (ps.BlendDistance * ps.BlendDistance);
                    if (w1 < 1) { double w = 1 - Math.Sqrt(w1); sum += w * SampleDc(ps, probes, x, y, z); weight += w; maxP = Math.Max(cur, maxP); }
                }
            }
            double def = db.ProbeSets.Count > 0 ? Dc(probes, (int)db.ProbeSets[0].GlobalOffset) : 0;
            if (weight <= 0) return def;
            if (weight < 1) { sum += (1 - weight) * def; weight = 1; }
            return sum / weight;
        }

        public sealed class Candidate
        {
            public int Set;
            public int Probes;            // probes inside the set's own box
            public double OwnDc, LookupDc;
            public double Ratio => LookupDc > 0 ? OwnDc / LookupDc : 1.0;
            public bool Interior;
            public Guid VolumeGuid;
            public bool AlreadyPresent;
            public EnlightenDatabase.ProbeSet Volume;
            public byte[] VolumeProbes;
        }

        static double Median(List<double> v) { v.Sort(); return v.Count == 0 ? 0 : (v.Count % 2 == 1 ? v[v.Count / 2] : 0.5 * (v[v.Count / 2 - 1] + v[v.Count / 2])); }

        public static Guid DerivedGuid(Guid source)
        {
            using (var md5 = MD5.Create())
                return new Guid(md5.ComputeHash(Encoding.ASCII.GetBytes("interior-probe-volume:" + source.ToString("D"))));
        }

        /// <summary>
        /// Every scattered set whose own probes (those inside its box) are darker than the dynamic lookup at the same
        /// points by more than <paramref name="maxRatio"/> becomes an interior volume: the set's box, a regular grid
        /// (cell centres, x fastest) of about <paramref name="spacingXZ"/> x <paramref name="spacingY"/> m, each probe the
        /// inverse-square-distance blend of the set's 4 nearest own probes; priority, blend as given; no static flag.
        /// </summary>
        public static List<Candidate> Plan(EnlightenDatabase db, byte[] probes, double maxRatio, double spacingXZ, double spacingY,
                                           uint priority, float blend, int maxDim = 50)
        {
            var have = new HashSet<Guid>(db.ProbeSets.Select(p => p.Guid));
            var result = new List<Candidate>();
            for (int s = 0; s < db.ProbeSets.Count; s++)
            {
                var ps = db.ProbeSets[s];
                if (!IsStatic(ps) || s == 0 || ps.Count == 0 || ps.Positions.Length < ps.Count * 16) continue;
                var own = new List<int>();
                for (int k = 0; k < ps.Count; k++)
                {
                    float x, y, z; ps.Position(k, out x, out y, out z);
                    if (Inside(ps, x, y, z, 0.5)) own.Add(k);
                }
                var c = new Candidate { Set = s, Probes = own.Count, VolumeGuid = DerivedGuid(ps.Guid) };
                result.Add(c);
                if (own.Count == 0) continue;
                var ownDc = new List<double>(); var lookDc = new List<double>();
                foreach (int k in own)
                {
                    float x, y, z; ps.Position(k, out x, out y, out z);
                    ownDc.Add(Dc(probes, (int)ps.GlobalOffset + k));
                    lookDc.Add(DynamicLookupDc(db, probes, x, y, z));
                }
                c.OwnDc = Median(ownDc); c.LookupDc = Median(lookDc);
                c.Interior = c.Ratio < maxRatio;
                c.AlreadyPresent = have.Contains(c.VolumeGuid);
                if (!c.Interior || c.AlreadyPresent) continue;
                BuildVolume(ps, own, probes, spacingXZ, spacingY, priority, blend, maxDim, c);
            }
            return result;
        }

        internal static void BuildVolume(EnlightenDatabase.ProbeSet src, List<int> own, byte[] probes, double spacingXZ, double spacingY,
                                         uint priority, float blend, int maxDim, Candidate c)
        {
            double[] mn = { src.MinX, src.MinY, src.MinZ }, mx = { src.MaxX, src.MaxY, src.MaxZ };
            double[] sp = { spacingXZ, spacingY, spacingXZ };
            var dim = new int[3];
            for (int k = 0; k < 3; k++) dim[k] = Math.Max(1, Math.Min(maxDim, (int)Math.Ceiling((mx[k] - mn[k]) / sp[k])));
            int n = dim[0] * dim[1] * dim[2];
            var v = new EnlightenDatabase.ProbeSet { Raw = (byte[])src.Raw.Clone(), Positions = new byte[n * 16] };
            Array.Copy(c.VolumeGuid.ToByteArray(), 0, v.Raw, 0, 16);
            EnlightenDatabase.PutF(v.Raw, 0xB0, blend);
            EnlightenDatabase.Put(v.Raw, 0xB4, (uint)dim[0]); EnlightenDatabase.Put(v.Raw, 0xB8, (uint)dim[1]); EnlightenDatabase.Put(v.Raw, 0xBC, (uint)dim[2]);
            EnlightenDatabase.Put(v.Raw, 0xC0, priority);
            EnlightenDatabase.Put(v.Raw, 0xC4, (uint)n);
            v.Raw[0xCC] = 0; v.Raw[0xCD] = 0;       // staticProbeSet, dynamicDataEnable (as DICE's volumes)
            var srcPos = own.Select(k => { float x, y, z; src.Position(k, out x, out y, out z); return new[] { (double)x, y, z, (double)((int)src.GlobalOffset + k) }; }).ToList();
            var outProbes = new byte[n * ProbeBytes];
            var acc = new double[36];
            for (int zi = 0; zi < dim[2]; zi++)
                for (int yi = 0; yi < dim[1]; yi++)
                    for (int xi = 0; xi < dim[0]; xi++)
                    {
                        int idx = dim[0] * dim[1] * zi + dim[0] * yi + xi;
                        double px = mn[0] + (xi + 0.5) / dim[0] * (mx[0] - mn[0]);
                        double py = mn[1] + (yi + 0.5) / dim[1] * (mx[1] - mn[1]);
                        double pz = mn[2] + (zi + 0.5) / dim[2] * (mx[2] - mn[2]);
                        v.SetPosition(idx, (float)px, (float)py, (float)pz);
                        var near = srcPos.Select(q => new { q, d2 = (q[0] - px) * (q[0] - px) + (q[1] - py) * (q[1] - py) + (q[2] - pz) * (q[2] - pz) })
                                         .OrderBy(t => t.d2).Take(4).ToList();
                        Array.Clear(acc, 0, 36);
                        double wsum = 0;
                        foreach (var t in near)
                        {
                            double w = 1.0 / (t.d2 + 0.25);
                            int o = (int)t.q[3] * ProbeBytes;
                            for (int f = 0; f < 36; f++) acc[f] += w * BitConverter.ToSingle(probes, o + f * 4);
                            wsum += w;
                        }
                        for (int f = 0; f < 36; f++)
                            Array.Copy(BitConverter.GetBytes((float)(acc[f] / wsum)), 0, outProbes, idx * ProbeBytes + f * 4, 4);
                    }
            c.Volume = v; c.VolumeProbes = outProbes;
        }

        /// <summary>Append the planned volumes: sets after the existing ones, SH after the existing probes.</summary>
        public static int Apply(EnlightenDatabase db, StaticEnlightenDatabase fls, IEnumerable<Candidate> plan)
        {
            int added = 0;
            var bytes = new List<byte>(fls.Probes);
            foreach (var c in plan)
            {
                if (c.Volume == null) continue;
                c.Volume.GlobalOffset = (uint)(bytes.Count / ProbeBytes);
                bytes.AddRange(c.VolumeProbes);
                db.ProbeSets.Add(c.Volume);
                added++;
            }
            fls.Probes = bytes.ToArray();
            return added;
        }
    }
}
