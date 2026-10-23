// VENDORED from BfiiMapTools src\Bfii.Navmesh\Primitives\Vec3.cs (sha256:32C225356064A8C2).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Globalization;

namespace Bfii.Navmesh.Primitives
{
    /// <summary>
    /// Minimal 3-component single-precision vector.
    /// </summary>
    /// <remarks>
    /// Exists so that Bfii.Navmesh has no package references at all. Component order in memory is
    /// X, Y, Z and that order is load bearing: it is the serialized order of every position in the
    /// navigation image.
    /// </remarks>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static Vec3 Zero => new Vec3(0f, 0f, 0f);

        /// <summary>Component by axis index: 0 = X, 1 = Y, 2 = Z.</summary>
        public float this[int axis]
        {
            get
            {
                switch (axis)
                {
                    case 0: return X;
                    case 1: return Y;
                    case 2: return Z;
                    default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "axis must be 0, 1 or 2");
                }
            }
        }

        public float this[Axis axis] => this[(int)axis];

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public static float Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
            (a.Y * b.Z) - (a.Z * b.Y),
            (a.Z * b.X) - (a.X * b.Z),
            (a.X * b.Y) - (a.Y * b.X));

        public float Length() => (float)Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

        public Vec3 Normalized()
        {
            float len = Length();
            return len <= 0f ? Zero : this / len;
        }

        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length();

        /// <summary>
        /// Foot of the perpendicular from point onto the infinite line through lineStart and
        /// lineEnd. Used by basis-vertex selection. A degenerate line returns lineStart.
        /// </summary>
        public static Vec3 PerpendicularFoot(Vec3 point, Vec3 lineStart, Vec3 lineEnd)
        {
            Vec3 dir = lineEnd - lineStart;
            float len = dir.Length();
            if (len <= 0f)
            {
                return lineStart;
            }

            dir = dir / len;
            float t = Dot(point - lineStart, dir);
            return lineStart + (dir * t);
        }

        /// <summary>Exact bitwise component equality. Deliberately not tolerance based.</summary>
        public bool Equals(Vec3 other) =>
            X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);

        public override bool Equals(object? obj) => obj is Vec3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X.GetHashCode();
                hash = (hash * 397) ^ Y.GetHashCode();
                hash = (hash * 397) ^ Z.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture, "({0:R}, {1:R}, {2:R})", X, Y, Z);
    }
}
