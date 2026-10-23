#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\HeightmapInput.cs (sha256:3D5F0833AFFC6F16).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// Where a heightmap sits in the world and how its values become metres. The raster is a vertex grid: its corner
    /// samples lie exactly on the rectangle's corners, so sample i is at <c>MinX + i * SizeX / (Width - 1)</c>.
    /// </summary>
    public sealed class HeightmapPlacement
    {
        public double MinX, MinZ, SizeX, SizeZ;

        /// <summary>
        /// False (default): row 0 is the min-Z edge. Frostbite is right-handed Y-up, so Blender (x, y, z) is Frostbite
        /// (x, z, -y) and the top row of a top-down Blender image (max Blender Y) is min Frostbite Z; `frostycli
        /// terrain export-heights` writes min Z first too. True: row 0 is the max-Z edge.
        /// </summary>
        public bool RowZeroIsMaxZ;

        /// <summary>metres = value * Scale + Offset (integer formats arrive as 0..1).</summary>
        public double Scale = 1, Offset;

        /// <summary>Samples below this many metres (after scaling) are holes. NaN: none.</summary>
        public double HoleBelow = double.NaN;

        public double MaxX => MinX + SizeX;
        public double MaxZ => MinZ + SizeZ;
    }

    /// <summary>
    /// A heightmap raster as a height source on a terrain frame: bilinear between raster samples (holes -- NaN or
    /// below <see cref="HeightmapPlacement.HoleBelow"/> -- decided by the nearest sample, as
    /// <see cref="DenseHeightSource"/>). Outside the raster's rectangle the edge is clamped outward, so a frame larger
    /// than the heightmap (one aligned to a host's grid) is filled with a skirt at edge height; with
    /// <see cref="SkirtFadeMetres"/> the skirt blends into the mean edge height over that distance and is flat beyond, so
    /// the edge's relief is not extruded across the frame (TA_S3: an extruded 512 m edge took 1,400 nodes of budget).
    /// </summary>
    public sealed class RasterHeightSource : IHeightSource
    {
        private readonly TerrainFrame _frame;
        private readonly float[] _metres;     // row-major, row 0 = min Z, NaN = hole
        private readonly int _w, _h;
        private readonly HeightmapPlacement _p;

        public double MinMetres { get; }
        public double MaxMetres { get; }
        public int HoleSamples { get; }

        /// <summary>Distance over which the skirt outside the rectangle fades to the mean edge height (0 = no fade).</summary>
        public double SkirtFadeMetres { get; set; }

        /// <summary>Mean height of the rectangle's edge samples (holes left out): what a faded skirt settles at.</summary>
        public double EdgeMeanMetres { get; }

        public RasterHeightSource(TerrainFrame frame, Raster raster, HeightmapPlacement placement)
        {
            _frame = frame; _p = placement; _w = raster.Width; _h = raster.Height;
            if (_w < 2 || _h < 2) throw new ArgumentException("a heightmap needs at least 2 x 2 samples");
            var v = raster.Channel(0);
            _metres = new float[v.Length];
            double lo = double.MaxValue, hi = double.MinValue;
            int holes = 0;
            for (int y = 0; y < _h; y++)
            {
                int srcRow = placement.RowZeroIsMaxZ ? _h - 1 - y : y;
                for (int x = 0; x < _w; x++)
                {
                    double m = v[(long)srcRow * _w + x] * placement.Scale + placement.Offset;
                    bool hole = double.IsNaN(m) || (!double.IsNaN(placement.HoleBelow) && m < placement.HoleBelow);
                    if (hole) { _metres[(long)y * _w + x] = float.NaN; holes++; continue; }
                    _metres[(long)y * _w + x] = (float)m;
                    if (m < lo) lo = m;
                    if (m > hi) hi = m;
                }
            }
            if (lo > hi) throw new ArgumentException("the heightmap is all holes");
            MinMetres = lo; MaxMetres = hi; HoleSamples = holes;
            double edgeSum = 0; long edgeCount = 0;
            for (int x = 0; x < _w; x++) { EdgeAdd(_metres[x]); EdgeAdd(_metres[(long)(_h - 1) * _w + x]); }
            for (int y = 1; y < _h - 1; y++) { EdgeAdd(_metres[(long)y * _w]); EdgeAdd(_metres[(long)y * _w + _w - 1]); }
            EdgeMeanMetres = edgeCount > 0 ? edgeSum / edgeCount : (lo + hi) / 2;

            void EdgeAdd(float m) { if (!float.IsNaN(m)) { edgeSum += m; edgeCount++; } }
        }

        public bool Covers(long gx, long gz) => true;

        /// <summary>
        /// 1 where the heightmap has a hole within <paramref name="dilateMetres"/>, else 0: one float per raster sample,
        /// rows min-Z first (place it with <see cref="HeightmapPlacement.RowZeroIsMaxZ"/> false). The mask the host's
        /// hole layer is painted from; widening it keeps the visible cut over the triangles the physics hole removes.
        /// </summary>
        public float[] HoleMask(double dilateMetres)
        {
            var m = new float[(long)_w * _h];
            double sx = _p.SizeX / (_w - 1), sz = _p.SizeZ / (_h - 1);
            int rx = (int)Math.Ceiling(dilateMetres / sx), rz = (int)Math.Ceiling(dilateMetres / sz);
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                {
                    if (!float.IsNaN(_metres[(long)y * _w + x])) continue;
                    for (int dy = -rz; dy <= rz; dy++)
                        for (int dx = -rx; dx <= rx; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= _w || yy >= _h) continue;
                            if ((dx * sx) * (dx * sx) + (dy * sz) * (dy * sz) > dilateMetres * dilateMetres + 1e-9) continue;
                            m[(long)yy * _w + xx] = 1f;
                        }
                }
            return m;
        }

        public int RasterWidth => _w;
        public int RasterHeight => _h;

        public ushort Sample(long gx, long gz, bool allowHole)
        {
            long n = _frame.GridSize - 1;
            if (gx < 0) gx = 0; else if (gx > n) gx = n;
            if (gz < 0) gz = 0; else if (gz > n) gz = n;
            double wx = _frame.WorldX(gx), wz = _frame.WorldZ(gz);
            double fx = (wx - _p.MinX) / _p.SizeX * (_w - 1);
            double fz = (wz - _p.MinZ) / _p.SizeZ * (_h - 1);
            double outside = 0;
            if (SkirtFadeMetres > 0)
            {
                double ox = Math.Max(0, Math.Max(_p.MinX - wx, wx - (_p.MinX + _p.SizeX)));
                double oz = Math.Max(0, Math.Max(_p.MinZ - wz, wz - (_p.MinZ + _p.SizeZ)));
                outside = Math.Sqrt(ox * ox + oz * oz);
                if (outside >= SkirtFadeMetres) return _frame.ToSample(EdgeMeanMetres);
            }
            if (fx < 0) fx = 0; else if (fx > _w - 1) fx = _w - 1;
            if (fz < 0) fz = 0; else if (fz > _h - 1) fz = _h - 1;
            int x0 = (int)Math.Floor(fx), z0 = (int)Math.Floor(fz);
            if (x0 >= _w - 1) x0 = _w - 2;
            if (z0 >= _h - 1) z0 = _h - 2;
            double tx = fx - x0, tz = fz - z0;
            float a = _metres[(long)z0 * _w + x0], b = _metres[(long)z0 * _w + x0 + 1];
            float c = _metres[(long)(z0 + 1) * _w + x0], d = _metres[(long)(z0 + 1) * _w + x0 + 1];
            float nearest = tx < 0.5 ? (tz < 0.5 ? a : c) : (tz < 0.5 ? b : d);
            if (allowHole && float.IsNaN(nearest)) return 0;
            double w = 0, h = 0;
            Add(a, (1 - tx) * (1 - tz)); Add(b, tx * (1 - tz)); Add(c, (1 - tx) * tz); Add(d, tx * tz);
            double ground = w <= 1e-12 ? NearestGround(x0, z0) : h / w;
            if (outside > 0)
            {
                double t = outside / SkirtFadeMetres;
                t = t * t * (3 - 2 * t);
                ground += (EdgeMeanMetres - ground) * t;
            }
            return _frame.ToSample(ground);

            void Add(float value, double weight)
            {
                if (float.IsNaN(value) || weight <= 0) return;
                h += value * weight; w += weight;
            }
        }

        private double NearestGround(int x0, int z0)
        {
            for (int r = 1; r < Math.Max(_w, _h); r++)
            {
                double sum = 0; int count = 0;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Abs(dx) != r && Math.Abs(dz) != r) continue;
                        int x = x0 + dx, z = z0 + dz;
                        if (x < 0 || z < 0 || x >= _w || z >= _h) continue;
                        float v = _metres[(long)z * _w + x];
                        if (!float.IsNaN(v)) { sum += v; count++; }
                    }
                if (count > 0) return sum / count;
            }
            return MinMetres;
        }
    }

    /// <summary>
    /// Chooses the terrain frame (world square and depth) for a heightmap. With a host frame -- the level the terrain
    /// goes into, whose baked GI lightmaps are cut along its own quadtree (LOG R3) -- the frame is a power-of-two
    /// multiple or fraction of the host's extent placed on the host's grid, so every node of the new tree no larger
    /// than half the host is also a node of the host's tree and sits inside one lightmap tile. Without a host the
    /// frame is the heightmap's own square.
    /// </summary>
    public static class TerrainFramePlanner
    {
        public sealed class Plan
        {
            public TerrainFrame Frame = new TerrainFrame();
            /// <summary>The frame's extent is the host's * 2^Doublings (negative: smaller than the host); null without a host.</summary>
            public int? Doublings;
            public string Note = "";
        }

        /// <param name="rect">world rectangle the heightmap covers</param>
        /// <param name="spacing">wanted finest sample spacing in metres</param>
        /// <param name="host">the host terrain's frame, or null</param>
        /// <param name="minDoublings">smallest k in E = host * 2^k. `terrain create` passes 0: a frame smaller than
        /// its host renders black (TA_S, TA_S2: 512 m in the 8 km test range), so a small terrain takes the host's
        /// square and is only refined where its heightmap is.</param>
        public static Plan Choose(HeightmapPlacement rect, double spacing, TerrainFrame? host, float worldSizeY, int tileSamples = 137, int minDoublings = -8)
        {
            if (spacing <= 0) throw new ArgumentException("spacing must be positive");
            int pot = tileSamples == 265 ? 256 : 128;
            var plan = new Plan();
            double minX, minZ, extent;
            if (host == null)
            {
                extent = Math.Max(rect.SizeX, rect.SizeZ);
                minX = rect.MinX + (rect.SizeX - extent) / 2;
                minZ = rect.MinZ + (rect.SizeZ - extent) / 2;
                plan.Note = "own square " + extent + " m";
            }
            else
            {
                // Any square whose nodes no larger than half the host land on the host's own grid: extent
                // E = host * 2^k, origin on the host grid of spacing g = min(E, host / 2). Then every patch of side
                // <= g inside the host's footprint is one of the host's quadtree nodes, so it lies inside one host
                // lightmap tile whatever level the tiles were cut at (T3 in the town proved tiles are smaller than
                // the host). Smallest E first; the heightmap centred as far as the grid allows.
                const double eps = 1e-6;
                bool found = false;
                minX = minZ = extent = 0;
                for (int k = Math.Max(-8, minDoublings); k <= 6 && !found; k++)
                {
                    double e = k >= 0 ? host.Extent * (1 << k) : host.Extent / (1 << -k);
                    if (e + eps < rect.SizeX || e + eps < rect.SizeZ) continue;
                    double g = Math.Min(e, host.Extent / 2);
                    double ox, oz;
                    if (!Snap(rect.MinX, rect.MaxX, host.MinX, e, g, out ox) || !Snap(rect.MinZ, rect.MaxZ, host.MinZ, e, g, out oz)) continue;
                    minX = ox; minZ = oz; extent = e; found = true; plan.Doublings = k;
                }
                if (!found) throw new ArgumentException("the heightmap does not fit any square on the host's grid up to 64x the host");
                plan.Note = plan.Doublings == 0 ? "host-sized, on the host grid"
                          : plan.Doublings > 0 ? (1 << plan.Doublings.Value) + "x the host, on its grid"
                          : "1/" + (1 << -plan.Doublings!.Value) + " of the host, on its grid";
            }
            plan.Frame = new TerrainFrame
            {
                MinX = minX, MinZ = minZ, Extent = extent, WorldSizeY = worldSizeY,
                TileSamplesPerSide = tileSamples, BorderWidth = 4,
                MaxLevel = Math.Max(2, TerrainFrame.MaxLevelFor(extent, spacing, pot)),
            };
            return plan;
        }

        /// <summary>An origin on <c>grid0 + i * g</c> such that [origin, origin + e] holds [lo, hi], closest to centred.</summary>
        private static bool Snap(double lo, double hi, double grid0, double e, double g, out double origin)
        {
            const double eps = 1e-6;
            double ideal = (lo + hi) / 2 - e / 2;
            long i0 = (long)Math.Round((ideal - grid0) / g);
            for (long d = 0; d < 64; d++)
                foreach (long i in d == 0 ? new[] { i0 } : new[] { i0 - d, i0 + d })
                {
                    double o = grid0 + i * g;
                    if (o <= lo + eps && o + e >= hi - eps) { origin = o; return true; }
                }
            origin = 0;
            return false;
        }

        /// <summary>
        /// World height span the heightfield encodes (0 .. WorldSizeY, sample 0 = hole). Shipped terrains use 256 and
        /// larger; the smallest power of two at or above 256 that holds <paramref name="maxMetres"/> with 2% headroom.
        /// </summary>
        public static float WorldSizeYFor(double maxMetres)
        {
            double need = Math.Max(1, maxMetres * 1.02);
            float y = 256;
            while (y < need && y < 65536) y *= 2;
            return y;
        }
    }
}
