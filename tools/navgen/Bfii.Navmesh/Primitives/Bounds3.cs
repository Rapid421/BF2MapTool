// VENDORED from BfiiMapTools src\Bfii.Navmesh\Primitives\Bounds3.cs (sha256:DEFB6B581262713C).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Bfii.Navmesh.Primitives
{
    /// <summary>
    /// Axis-aligned bounding box, serialized as min followed by max, each a Vec3: 24 bytes total.
    /// </summary>
    public readonly struct Bounds3 : IEquatable<Bounds3>
    {
        public readonly Vec3 Min;
        public readonly Vec3 Max;

        public Bounds3(Vec3 min, Vec3 max)
        {
            Min = min;
            Max = max;
        }

        public Vec3 Center => (Min + Max) * 0.5f;

        public static Bounds3 FromPoints(IReadOnlyList<Vec3> points)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            if (points.Count == 0)
            {
                throw new ArgumentException("cannot build bounds from zero points", nameof(points));
            }

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            for (int i = 0; i < points.Count; i++)
            {
                Vec3 p = points[i];
                if (p.X < minX) { minX = p.X; }
                if (p.Y < minY) { minY = p.Y; }
                if (p.Z < minZ) { minZ = p.Z; }
                if (p.X > maxX) { maxX = p.X; }
                if (p.Y > maxY) { maxY = p.Y; }
                if (p.Z > maxZ) { maxZ = p.Z; }
            }

            return new Bounds3(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
        }

        public static Bounds3 Union(Bounds3 a, Bounds3 b) => new Bounds3(
            new Vec3(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
            new Vec3(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)));

        /// <summary>Returns this box with one component of Max replaced.</summary>
        public Bounds3 WithMaxComponent(Axis axis, float value) =>
            new Bounds3(Min, ReplaceComponent(Max, axis, value));

        /// <summary>Returns this box with one component of Min replaced.</summary>
        public Bounds3 WithMinComponent(Axis axis, float value) =>
            new Bounds3(ReplaceComponent(Min, axis, value), Max);

        private static Vec3 ReplaceComponent(Vec3 v, Axis axis, float value)
        {
            switch (axis)
            {
                case Axis.X: return new Vec3(value, v.Y, v.Z);
                case Axis.Y: return new Vec3(v.X, value, v.Z);
                case Axis.Z: return new Vec3(v.X, v.Y, value);
                default: throw new ArgumentOutOfRangeException(nameof(axis), axis, "axis must be X, Y or Z");
            }
        }

        public bool Equals(Bounds3 other) => Min.Equals(other.Min) && Max.Equals(other.Max);

        public override bool Equals(object? obj) => obj is Bounds3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Min.GetHashCode() * 397) ^ Max.GetHashCode();
            }
        }

        public override string ToString() => string.Format(
            CultureInfo.InvariantCulture, "[{0} .. {1}]", Min, Max);
    }
}
