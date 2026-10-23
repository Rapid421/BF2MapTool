using System;
using System.Globalization;

namespace FrostyCLI.Core
{
    public struct LinearTransformData
    {
        public float[] Right;   // [rx, ry, rz]
        public float[] Up;      // [ux, uy, uz]
        public float[] Forward; // [fx, fy, fz]
        public float[] Trans;   // [tx, ty, tz]
    }

    public static class TransformBuilder
    {
        public static LinearTransformData Build(string posArg, string rotArg, string scaleArg)
        {
            float[] pos   = ParseVec3(posArg ?? "0,0,0", "pos");
            float   scale = ParseScale(scaleArg ?? "1");

            // Nine numbers are the rotation basis itself (right, up, forward columns), as read from a
            // shipped placement; three are yaw/pitch/roll degrees.
            if (rotArg != null && rotArg.Split(',').Length == 9)
            {
                string[] p9 = rotArg.Split(',');
                float[] m = new float[9];
                for (int i = 0; i < 9; i++) m[i] = float.Parse(p9[i].Trim(), CultureInfo.InvariantCulture) * scale;
                return new LinearTransformData
                {
                    Right = new[] { m[0], m[1], m[2] }, Up = new[] { m[3], m[4], m[5] }, Forward = new[] { m[6], m[7], m[8] },
                    Trans = new[] { pos[0], pos[1], pos[2] }
                };
            }
            float[] euler = ParseVec3(rotArg ?? "0,0,0", "rot");

            // Negate angles for LH convention
            double yaw   = -euler[0] * Math.PI / 180.0;
            double pitch = -euler[1] * Math.PI / 180.0;
            double roll  = -euler[2] * Math.PI / 180.0;

            double cy = Math.Cos(yaw),   sy = Math.Sin(yaw);
            double cx = Math.Cos(pitch), sx = Math.Sin(pitch);
            double cz = Math.Cos(roll),  sz = Math.Sin(roll);

            // M = Ry(yaw) * Rx(pitch) * Rz(roll) — columns give right/up/forward
            float[] right = new float[3]
            {
                (float)(cy * cz + sy * sx * sz),
                (float)(cx * sz),
                (float)(-sy * cz + cy * sx * sz)
            };
            float[] up = new float[3]
            {
                (float)(-cy * sz + sy * sx * cz),
                (float)(cx * cz),
                (float)(sy * sz + cy * sx * cz)
            };
            float[] forward = new float[3]
            {
                (float)(sy * cx),
                (float)(-sx),
                (float)(cy * cx)
            };

            // Apply uniform scale to rotation basis only (not translation)
            for (int i = 0; i < 3; i++)
            {
                right[i]   *= scale;
                up[i]      *= scale;
                forward[i] *= scale;
            }

            return new LinearTransformData
            {
                Right   = right,
                Up      = up,
                Forward = forward,
                Trans   = new[] { pos[0], pos[1], pos[2] }
            };
        }

        private static float[] ParseVec3(string s, string what)
        {
            string[] parts = s.Split(',');
            if (parts.Length != 3)
                throw new ArgumentException("--" + what + " requires three comma-separated floats; got: " + s);
            float[] result = new float[3];
            for (int i = 0; i < 3; i++)
                result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return result;
        }

        private static float ParseScale(string s)
        {
            if (s.IndexOf(',') >= 0)
                throw new ArgumentException("--scale requires a single float (uniform scale); per-axis scale is not supported.");
            return float.Parse(s, CultureInfo.InvariantCulture);
        }
    }
}
