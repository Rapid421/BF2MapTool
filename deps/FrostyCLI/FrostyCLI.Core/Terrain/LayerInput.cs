#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\LayerInput.cs (sha256:8B7110143ED42BE6).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bfii.TerrainBuild
{
    /// <summary>
    /// A float raster over a world rectangle (rows min-Z first after construction), sampled bilinearly from a box mip
    /// chain so a coarse texel averages what it covers; outside the rectangle the edge is clamped.
    /// </summary>
    public sealed class WorldGrid
    {
        private readonly HeightmapPlacement _rect;
        private readonly List<(float[] Data, int W, int H)> _mips = new List<(float[], int, int)>();

        public int Width => _mips[0].W;
        public int Height => _mips[0].H;

        /// <param name="values">one channel, row-major, row 0 as the file stores it</param>
        public WorldGrid(float[] values, int width, int height, HeightmapPlacement rect)
        {
            if (values.Length != (long)width * height) throw new ArgumentException("values must be width*height");
            _rect = rect;
            var d = new float[values.Length];
            for (int y = 0; y < height; y++)
                Array.Copy(values, (long)(rect.RowZeroIsMaxZ ? height - 1 - y : y) * width, d, (long)y * width, width);
            _mips.Add((d, width, height));
            int w = width, h = height;
            while (w > 1 || h > 1)
            {
                int nw = (w + 1) / 2, nh = (h + 1) / 2;
                var next = new float[(long)nw * nh];
                var cur = _mips[_mips.Count - 1].Data;
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        double sum = 0; int cnt = 0;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = 2 * x + dx, sy = 2 * y + dy;
                                if (sx >= w || sy >= h) continue;
                                sum += cur[(long)sy * w + sx]; cnt++;
                            }
                        next[(long)y * nw + x] = (float)(sum / cnt);
                    }
                _mips.Add((next, nw, nh));
                w = nw; h = nh;
            }
        }

        public static WorldGrid FromRaster(Raster r, int channel, HeightmapPlacement rect) => new WorldGrid(r.Channel(channel), r.Width, r.Height, rect);

        /// <summary>Value at (x, z) for a texel of <paramref name="texel"/> metres (picks the mip whose texel is not coarser).</summary>
        public float Sample(double x, double z, double texel)
        {
            double texel0 = Math.Max(_rect.SizeX / Math.Max(1, Width - 1), _rect.SizeZ / Math.Max(1, Height - 1));
            int level = 0;
            while (level + 1 < _mips.Count && texel0 * (1 << (level + 1)) <= texel * 1.0001) level++;
            var (data, w, h) = _mips[level];
            // The base grid is a vertex grid; mip texel k of level l averages base samples k*2^l .. (k+1)*2^l - 1,
            // so it sits at base coordinate k*2^l + (2^l - 1)/2.
            double half = ((1 << level) - 1) / 2.0;
            double fx = ((x - _rect.MinX) / _rect.SizeX * (Width - 1) - half) / (1 << level);
            double fz = ((z - _rect.MinZ) / _rect.SizeZ * (Height - 1) - half) / (1 << level);
            if (fx < 0) fx = 0; else if (fx > w - 1) fx = w - 1;
            if (fz < 0) fz = 0; else if (fz > h - 1) fz = h - 1;
            int x0 = (int)fx, z0 = (int)fz;
            int x1 = Math.Min(x0 + 1, w - 1), z1 = Math.Min(z0 + 1, h - 1);
            double tx = fx - x0, tz = fz - z0;
            return (float)(data[(long)z0 * w + x0] * (1 - tx) * (1 - tz) + data[(long)z0 * w + x1] * tx * (1 - tz)
                         + data[(long)z1 * w + x0] * (1 - tx) * tz + data[(long)z1 * w + x1] * tx * tz);
        }
    }

    /// <summary>One authored terrain layer: which of the host's terrain layers it is, its weight map, its material.</summary>
    public sealed class LayerInput
    {
        public string Name = "";
        /// <summary>Index into the host TerrainData's TerrainLayers (the mask stores this index).</summary>
        public int HostLayer;
        /// <summary>Weight map over the placement rectangle (0..1); null = whatever weight the other layers leave.</summary>
        public WorldGrid? Weights;
        /// <summary>Index into the material declarations (0..15) where this layer dominates.</summary>
        public int Material;
    }

    /// <summary>
    /// Terrain layers from weight maps. <c>Splat</c>: the maps are blend weights (normalised per texel here; a layer
    /// without a map takes 1 - the others), converted to the renderer's ordered alpha stack exactly as
    /// <see cref="DenseLayerSource.FromSplatWeights"/>: the lowest layer is opaque, layer i gets
    /// <c>w_i / (w_0 + ... + w_i)</c>. <c>Alpha</c>: the maps already are stack alphas (the lowest layer is forced opaque).
    /// </summary>
    public sealed class WeightMapLayerSource : ILayerSource
    {
        private readonly LayerInput[] _layers;      // sorted by host layer
        private readonly int[] _slotOfHostLayer;
        private readonly bool _splat;

        public int LayerCount { get; }
        public IReadOnlyList<LayerInput> Layers => _layers;

        public WeightMapLayerSource(IEnumerable<LayerInput> layers, bool splat = true)
        {
            _layers = layers.OrderBy(l => l.HostLayer).ToArray();
            if (_layers.Length == 0) throw new ArgumentException("at least one layer is needed");
            if (_layers.Select(l => l.HostLayer).Distinct().Count() != _layers.Length) throw new ArgumentException("two layers name the same host layer");
            if (_layers[0].HostLayer < 0 || _layers[_layers.Length - 1].HostLayer > 254) throw new ArgumentException("host layer indices must be 0..254");
            if (_layers.Count(l => l.Weights == null) > 1) throw new ArgumentException("only one layer may omit its weight map (it takes the remainder)");
            foreach (var l in _layers) if (l.Material < 0 || l.Material > 15) throw new ArgumentException("layer material index must be 0..15");
            _splat = splat;
            LayerCount = _layers[_layers.Length - 1].HostLayer + 1;
            _slotOfHostLayer = Enumerable.Repeat(-1, LayerCount).ToArray();
            for (int i = 0; i < _layers.Length; i++) _slotOfHostLayer[_layers[i].HostLayer] = i;
        }

        // The mask builder asks layer by layer for the same texels; remember the last node's worth of weights.
        private readonly Dictionary<(double, double, double), double[]> _cache = new Dictionary<(double, double, double), double[]>();
        private const int CacheLimit = 1 << 18;

        /// <summary>Normalised weights of every listed layer at (x, z).</summary>
        public double[] WeightsAt(double x, double z, double texel)
        {
            var key = (x, z, texel);
            if (_cache.TryGetValue(key, out var hit)) return hit;
            if (_cache.Count >= CacheLimit) _cache.Clear();
            var w = Compute(x, z, texel);
            _cache[key] = w;
            return w;
        }

        private double[] Compute(double x, double z, double texel)
        {
            var w = new double[_layers.Length];
            double sum = 0; int remainder = -1;
            for (int i = 0; i < _layers.Length; i++)
            {
                if (_layers[i].Weights == null) { remainder = i; continue; }
                double v = _layers[i].Weights!.Sample(x, z, texel);
                w[i] = v < 0 ? 0 : v;
                sum += w[i];
            }
            if (remainder >= 0) { w[remainder] = Math.Max(0, 1 - sum); sum += w[remainder]; }
            if (sum <= 1e-9) { w[0] = 1; return w; }         // nothing painted: the lowest layer
            if (_splat) for (int i = 0; i < w.Length; i++) w[i] /= sum;
            return w;
        }

        public byte Alpha(int layer, double x, double z, double texel)
        {
            if (layer < 0 || layer >= LayerCount) return 0;
            int slot = _slotOfHostLayer[layer];
            if (slot < 0) return 0;
            if (slot == 0) return 255;
            var w = WeightsAt(x, z, texel);
            double a;
            if (_splat)
            {
                double acc = 0;
                for (int i = 0; i <= slot; i++) acc += w[i];
                a = acc <= 1e-9 ? 0 : w[slot] / acc;
            }
            else a = w[slot];
            double v = Math.Floor(a * 255 + 0.5);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        /// <summary>The material of the dominant layer at (x, z).</summary>
        public byte DominantMaterial(double x, double z, double texel)
        {
            var w = WeightsAt(x, z, texel);
            int best = 0;
            for (int i = 1; i < w.Length; i++) if (w[i] > w[best]) best = i;
            return (byte)_layers[best].Material;
        }
    }

    /// <summary>Material indices from the dominant layer of a <see cref="WeightMapLayerSource"/>.</summary>
    public sealed class DominantLayerMaterialSource : IMaterialSource
    {
        private readonly WeightMapLayerSource _layers;
        private readonly double _texel;
        public DominantLayerMaterialSource(WeightMapLayerSource layers, double texelMetres) { _layers = layers; _texel = texelMetres; }
        public byte Index(double x, double z) => _layers.DominantMaterial(x, z, _texel);
    }

    /// <summary>
    /// Material indices from a raster over a world rectangle (pixel = one cell, pixel centres at (i + 0.5) / width):
    /// nearest cell, clamped at the rectangle's edge; values are 4-bit indices into the host's material declarations.
    /// `terrain export-layers` writes such a raster (material.png, 8-bit grey) at the tree's native size.
    /// </summary>
    public sealed class RectMaterialSource : IMaterialSource
    {
        private readonly byte[] _idx;
        private readonly int _w, _h;
        private readonly HeightmapPlacement _rect;
        public RectMaterialSource(Raster raster, HeightmapPlacement rect)
        {
            _w = raster.Width; _h = raster.Height; _rect = rect;
            _idx = new byte[_w * _h];
            for (int j = 0; j < _h; j++)
            {
                int row = rect.RowZeroIsMaxZ ? _h - 1 - j : j;
                for (int i = 0; i < _w; i++)
                {
                    float v = raster.Data[(row * _w + i) * raster.Channels];
                    int index = raster.IsFloat ? (int)Math.Round(v) : (int)Math.Round(v * (raster.BitDepth == 16 ? 65535 : 255));
                    _idx[j * _w + i] = (byte)(index < 0 ? 0 : index > 15 ? 15 : index);
                }
            }
        }

        public byte Index(double x, double z)
        {
            int i = (int)Math.Floor((x - _rect.MinX) / _rect.SizeX * _w), j = (int)Math.Floor((z - _rect.MinZ) / _rect.SizeZ * _h);
            if (i < 0) i = 0; else if (i >= _w) i = _w - 1;
            if (j < 0) j = 0; else if (j >= _h) j = _h - 1;
            return _idx[j * _w + i];
        }
    }

    /// <summary>An RGB colour map over a world rectangle (0..1 floats or 8-bit), for the colour tree.</summary>
    public sealed class RasterColorSource : IColorSource
    {
        private readonly WorldGrid _r, _g, _b;
        public RasterColorSource(Raster raster, HeightmapPlacement rect)
        {
            if (raster.Channels < 3) throw new ArgumentException("a colour map needs three channels (RGB)");
            _r = WorldGrid.FromRaster(raster, 0, rect); _g = WorldGrid.FromRaster(raster, 1, rect); _b = WorldGrid.FromRaster(raster, 2, rect);
        }

        public (byte R, byte G, byte B) Color(double x, double z, double texel) =>
            (ToByte(_r.Sample(x, z, texel)), ToByte(_g.Sample(x, z, texel)), ToByte(_b.Sample(x, z, texel)));

        private static byte ToByte(float v)
        {
            double b = Math.Floor(v * 255 + 0.5);
            return (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
        }
    }

    /// <summary>
    /// Any layer source with the host's hole layer painted over the holes: the renderer clips the terrain where the
    /// first layer with physics disabled (VisualTerrain holesEnabled) is opaque (TerrainTextureGenerator holesLayerIndex),
    /// while the heightfield's zero samples are the physics hole. Alpha = the hole fraction of the texel.
    /// </summary>
    public sealed class HoleLayerSource : ILayerSource
    {
        private readonly ILayerSource _inner;
        private readonly WorldGrid _holes;
        private readonly int _layer;

        public HoleLayerSource(ILayerSource inner, WorldGrid holes, int holeLayer)
        {
            _inner = inner; _holes = holes; _layer = holeLayer;
        }

        public int LayerCount => Math.Max(_inner.LayerCount, _layer + 1);

        public byte HoleAlpha(double x, double z, double texel)
        {
            double v = Math.Floor(_holes.Sample(x, z, texel) * 255 + 0.5);
            return (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
        }

        public byte Alpha(int layer, double x, double z, double texel)
        {
            byte a = layer < _inner.LayerCount ? _inner.Alpha(layer, x, z, texel) : (byte)0;
            if (layer != _layer) return a;
            byte h = HoleAlpha(x, z, texel);
            return h > a ? h : a;
        }
    }
}
