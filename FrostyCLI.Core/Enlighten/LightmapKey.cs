using System;
using System.Collections.Generic;

namespace FrostyCLI.Core.Enlighten
{
    /// <summary>
    /// The key a placed mesh's lightmap is found by: `createLightMapInstanceHashKey`, FNV-1 over the mesh asset's GUID
    /// (16 bytes, little-endian GUID layout) and 11 ints quantised from the instance transform - translation x100,
    /// rotation quaternion x10000, axis scales x100, each `trunc(f32(f32(v) * k) + 0.4999f)` in float32, then -1 when
    /// the transform is mirrored else 0. Verified on shipped data (Cloud City, Carbonite port) and in game (Mustafar port,
    /// I:\MustafarLightmapHandover: float64 rounding missed 72 of 562 keys, float32 got all of them).
    /// </summary>
    public static class LightmapKey
    {
        public static uint Fnv1(IEnumerable<byte> data)
        {
            uint h = 2166136261;
            foreach (byte b in data) { h = unchecked(h * 16777619u); h ^= b; }
            return h;
        }

        /// <summary>Key of mesh <paramref name="meshGuid"/> placed with rows right, up, forward and translation. BFII keys hash
        /// 11 ints; SWBF 2015 (Frostbite 2014) hashes the first 10 (no mirrored flag) - <paramref name="ints"/> 10.</summary>
        public static uint Compute(Guid meshGuid, double[] right, double[] up, double[] forward, double[] trans, int ints = 11)
        {
            bool mirrored = Dot(Cross(right, up), forward) < 0;
            double[] nL = Norm(right), nU = Norm(up), nF = Norm(forward);
            if (mirrored) nL = new[] { -nL[0], -nL[1], -nL[2] };
            double[] q = QuatFromRows(nL, nU, nF);
            double[] sc = { Math.Sqrt(Dot(right, right)), Math.Sqrt(Dot(up, up)), Math.Sqrt(Dot(forward, forward)) };
            var fuzzy = new List<int>(11);
            foreach (double v in trans) fuzzy.Add(Q(v, 100f));
            foreach (double v in q) fuzzy.Add(Q(v, 10000f));
            foreach (double v in sc) fuzzy.Add(Q(v, 100f));
            if (ints >= 11) fuzzy.Add(mirrored ? -1 : 0);
            var bytes = new List<byte>(meshGuid.ToByteArray());
            foreach (int i in fuzzy) bytes.AddRange(BitConverter.GetBytes(i));
            return Fnv1(bytes);
        }

        static int Q(double v, float k)
        {
            float p = (float)v * k;       // float32 throughout, as the game (float64 misses ~13% of keys)
            float s = p + 0.4999f;
            return (int)Math.Truncate(s);
        }

        static double[] QuatFromRows(double[] r0, double[] r1, double[] r2)
        {
            double[][] R = { r0, r1, r2 };
            double tr = R[0][0] + R[1][1] + R[2][2];
            var q = new double[4];
            if (tr > 0)
            {
                double w = Math.Sqrt(tr + 1.0) * 0.5, c = 1.0 / (4.0 * w);
                q[0] = (R[1][2] - R[2][1]) * c; q[1] = (R[2][0] - R[0][2]) * c; q[2] = (R[0][1] - R[1][0]) * c; q[3] = w;
            }
            else
            {
                int i = R[1][1] > R[0][0] ? 1 : 0;
                if (R[2][2] > R[i][i]) i = 2;
                int[] nxt = { 1, 2, 0 };
                int j = nxt[i], k = nxt[j];
                double c = Math.Sqrt((R[i][i] - (R[j][j] + R[k][k])) + 1.0) * 0.5;
                q[i] = c; c = 1.0 / (4.0 * c);
                q[3] = (R[j][k] - R[k][j]) * c; q[j] = (R[j][i] + R[i][j]) * c; q[k] = (R[k][i] + R[i][k]) * c;
            }
            return q;
        }

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static double[] Cross(double[] a, double[] b) => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double[] Norm(double[] v) { double l = Math.Sqrt(Dot(v, v)); return new[] { v[0] / l, v[1] / l, v[2] / l }; }
    }
}
