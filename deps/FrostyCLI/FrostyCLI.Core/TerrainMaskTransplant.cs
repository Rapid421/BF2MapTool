using System;
using System.Collections.Generic;
using System.Linq;
using Bfii.TerrainBuild;
using Bfii.TerrainFormat;

namespace FrostyCLI.Core
{
    // `terrain retarget --masks-from-source`: rebuild a terrain's layer masks from ANOTHER terrain's masks, sampled the
    // way the renderer walks them, instead of keeping the donor's own painting. Written for the SWBF 2015 -> BFII Hoth
    // port: BFII's Hoth_01 terrain is DICE's port of 2015 Arctic_01 (same 8192 m square, the same 9 renderer layers in
    // the same order, the same layer textures), but DICE repainted every mask, and the 2015 mask record cannot be copied
    // because its tiles differ (66 samples with a 1-texel border and 2x2 subtiles per node, against BFII's 72 / 4 / 4x4).
    // Layers keep their indices, so the source's layer i is painted into the target's layer i.
    //
    // A patch (subcell) whose layer set no layer combination of the target terrain contains draws nothing (terrain SPEC
    // §8), and 2015's painting uses mixes Hoth_01's 105 combinations never pair (layer 7 with 2 or 6). FilterCombinations
    // drops the weakest offending layer of such a patch until its set is covered.
    public static class TerrainMaskTransplant
    {
        /// <summary>A source mask tree as an <see cref="ILayerSource"/>: each query walks the source like the renderer.</summary>
        public sealed class SourceMaskLayers : ILayerSource
        {
            private readonly MaskTree _tree;
            private readonly Dictionary<MaskSubtile, byte[]> _tiles = new Dictionary<MaskSubtile, byte[]>();
            private readonly Dictionary<MaskNode, double[]> _bounds = new Dictionary<MaskNode, double[]>();
            private readonly Dictionary<(double, double, int), MaskSubtile[]> _hits = new Dictionary<(double, double, int), MaskSubtile[]>();
            private readonly int _t, _b, _pot, _perSide, _deepest;
            private readonly double _extent, _dx, _dz;

            public SourceMaskLayers(MaskTree source, double offsetX = 0, double offsetZ = 0)
            {
                _tree = source ?? throw new ArgumentNullException(nameof(source));
                if (source.Root == null) throw new ArgumentException("source mask tree has no root");
                _t = (int)source.TileSamplesPerSide;
                _b = (_t - Pot(_t)) / 2;
                _pot = _t - 2 * _b;
                _perSide = (int)Math.Max(1, source.TilesPerNodeSide);
                _extent = source.TreeCoverage.MaxX.Value - source.TreeCoverage.MinX.Value;
                _dx = offsetX; _dz = offsetZ;
                int maxLayer = -1, deepest = 0;
                foreach (var n in source.EnumerateNodes())
                {
                    int k = 0;
                    foreach (var st in n.Subtiles)
                    {
                        if (!st.IsTrivial && k < n.Tiles.Count) _tiles[st] = n.Tiles[k++];
                        if (st.TerrainLayerIndex > maxLayer) maxLayer = st.TerrainLayerIndex;
                    }
                    if (n.Subtiles.Count > 0 && n.Id.Level > deepest) deepest = n.Id.Level;
                    if (n.Subtiles.Count > 0)
                    {
                        double x0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, z1 = double.MinValue;
                        foreach (var st in n.Subtiles)
                        {
                            x0 = Math.Min(x0, st.WorldCoverage.MinX.Value); x1 = Math.Max(x1, st.WorldCoverage.MaxX.Value);
                            z0 = Math.Min(z0, st.WorldCoverage.MinY.Value); z1 = Math.Max(z1, st.WorldCoverage.MaxY.Value);
                        }
                        _bounds[n] = new[] { x0, z0, x1, z1 };
                    }
                }
                LayerCount = maxLayer + 1;
                _deepest = deepest;
            }

            public int LayerCount { get; }

            /// <summary>Deepest source level that carries subtiles.</summary>
            public int DeepestLevel => _deepest;

            /// <summary>Subtile texel of the source at <paramref name="level"/>, in metres.</summary>
            public double TexelAt(int level) => _extent / (1 << level) / _perSide / _pot;

            public byte Alpha(int layer, double x, double z, double texel)
            {
                x -= _dx; z -= _dz;
                // the coarsest source level whose texel is not coarser than 1.5x the wanted one (else the deepest)
                int level = _deepest;
                for (int l = 0; l <= _deepest; l++) if (TexelAt(l) <= texel * 1.5) { level = l; break; }
                var key = (x, z, level);
                MaskSubtile[] hits;
                if (!_hits.TryGetValue(key, out hits))
                {
                    if (_hits.Count > 600000) _hits.Clear();
                    var list = new List<MaskSubtile>(4);
                    Collect(_tree.Root, x, z, level, list);
                    hits = list.ToArray();
                    _hits[key] = hits;
                }
                byte best = 0;
                foreach (var st in hits)
                    if (st.TerrainLayerIndex == layer) { byte a = AlphaOf(st, x, z); if (a > best) best = a; }
                return best;
            }

            // MaskReader.Collect: from the root down to the data level, any subtile there, leaf subtiles on the way.
            private void Collect(MaskNode node, double x, double z, int dataLevel, List<MaskSubtile> hits)
            {
                if (node == null) return;
                bool atData = node.Id.Level >= dataLevel || !node.HasChildren || node.Children.Count != 4;
                foreach (var st in node.Subtiles)
                {
                    if (!(atData || st.IsLeafRaw != 0)) continue;
                    var c = st.WorldCoverage;
                    if (x >= c.MinX.Value && x < c.MaxX.Value && z >= c.MinY.Value && z < c.MaxY.Value) hits.Add(st);
                }
                if (atData) return;
                foreach (var child in node.Children)
                {
                    if (child.Subtiles.Count == 0 && !child.HasChildren) continue;
                    double[] b;
                    if (_bounds.TryGetValue(child, out b) && !(x >= b[0] && x < b[2] && z >= b[1] && z < b[3])) continue;
                    Collect(child, x, z, dataLevel, hits);
                }
            }

            private byte AlphaOf(MaskSubtile st, double x, double z)
            {
                byte[] tile;
                if (st.IsTrivial || !_tiles.TryGetValue(st, out tile)) return 255;
                var c = st.WorldCoverage;
                double texel = (c.MaxX.Value - c.MinX.Value) / _pot;
                double fx = (x - c.MinX.Value) / texel - 0.5 + _b, fz = (z - c.MinY.Value) / texel - 0.5 + _b;
                if (fx < 0) fx = 0; else if (fx > _t - 1) fx = _t - 1;
                if (fz < 0) fz = 0; else if (fz > _t - 1) fz = _t - 1;
                int ix = (int)fx, iz = (int)fz, ix1 = Math.Min(ix + 1, _t - 1), iz1 = Math.Min(iz + 1, _t - 1);
                double tx = fx - ix, tz = fz - iz;
                double v = tile[iz * _t + ix] * (1 - tx) * (1 - tz) + tile[iz * _t + ix1] * tx * (1 - tz)
                         + tile[iz1 * _t + ix] * (1 - tx) * tz + tile[iz1 * _t + ix1] * tx * tz;
                return (byte)Math.Floor(v + 0.5);
            }
        }

        public sealed class ConvertReport
        {
            public int Nodes, Subtiles, Tiled, Full, SourceRootSubtilesDropped, BorderSamples, MaxLevel;
            public string Atlas = "";
        }

        /// <summary>
        /// True when <paramref name="src"/>'s subtiles can be carried into <paramref name="layout"/>'s tile format one for
        /// one: the same interior texel count and twice the subtiles per node side. SWBF 2015 (66 samples, border 1, 2x2
        /// subtiles) -> BFII (72, border 4, 4x4): a 2015 level-(m+1) subtile covers exactly a BFII level-m subcell with the
        /// same 64 texels, so every 2015 subtile below the root has an exact BFII home and 2015's own adaptive depth and
        /// subtile budget carry over unchanged.
        /// </summary>
        public static bool CanConvertStructure(MaskTree src, MaskTree layout)
        {
            int Ts = (int)src.TileSamplesPerSide, T = (int)layout.TileSamplesPerSide;
            if (Ts <= 0 || T <= 0) return false;
            int pots = Pot(Ts), pot = Pot(T);
            // the interior is the largest power of two that leaves an even border
            return pots == pot && (Ts - pots) % 2 == 0 && (T - pot) % 2 == 0 && T >= Ts &&
                   layout.TilesPerNodeSide == 2 * src.TilesPerNodeSide &&
                   Math.Abs(src.TreeCoverage.MinX.Value - layout.TreeCoverage.MinX.Value) < 1e-3 &&
                   Math.Abs(src.TreeCoverage.MinY.Value - layout.TreeCoverage.MinY.Value) < 1e-3 &&
                   Math.Abs(src.TreeCoverage.MaxX.Value - layout.TreeCoverage.MaxX.Value) < 1e-3 &&
                   Math.Abs(src.TreeCoverage.MaxY.Value - layout.TreeCoverage.MaxY.Value) < 1e-3;
        }

        /// <summary>
        /// The source mask tree re-expressed in the layout tree's tile format (see <see cref="CanConvertStructure"/>):
        /// BFII node (m, x, z) holds the subtiles of the four 2015 nodes (m+1, 2x+dx, 2z+dz) with their layer, coverage,
        /// leaf and full flags; the inner tile samples are copied texel for texel (same positions), the wider BFII border
        /// is sampled from the neighbouring source subtiles, and coverage grids are recomputed. The 2015 root's own
        /// four coarse subtiles have no BFII level and are dropped (BFII's root holds 2015's level 1).
        /// </summary>
        public static MaskTree ConvertStructure(MaskTree src, MaskTree layout, out ConvertReport report)
        {
            if (!CanConvertStructure(src, layout)) throw new ArgumentException("mask layouts are not one-to-one convertible");
            var rep = new ConvertReport();
            report = rep;
            int Ts = (int)src.TileSamplesPerSide, Bs = (Ts - Pot(Ts)) / 2;
            int T = (int)layout.TileSamplesPerSide, B = (T - Pot(T)) / 2, pot = T - 2 * B;
            int perT = (int)layout.TilesPerNodeSide, shift = B - Bs;
            int levels = (int)layout.CoverageGridLevelCount;
            int stride = MaskStride.ForLevel(levels);
            double x0 = layout.TreeCoverage.MinX.Value, z0 = layout.TreeCoverage.MinY.Value;
            double ext = layout.TreeCoverage.MaxX.Value - x0;
            var cells = MaskBuilder.SubcellOrder((int)layout.SubtileRelativeLevel);
            var reader = new SourceMaskLayers(src);

            var srcNodes = new Dictionary<(int, int, int), MaskNode>();
            foreach (var n in src.EnumerateNodes()) srcNodes[(n.Id.Level, n.Id.IndexX, n.Id.IndexY)] = n;
            if (src.Root != null) rep.SourceRootSubtilesDropped = src.Root.Subtiles.Count;

            var tree = new MaskTree
            {
                TileSamplesPerSide = (uint)T,
                TilesPerNodeSide = (uint)perT,
                SubtileRelativeLevel = layout.SubtileRelativeLevel,
                BlurrinessLog2 = layout.BlurrinessLog2,
                TreeCoverage = layout.TreeCoverage,
                CoverageGridLevelCount = (uint)levels,
                FittedStrideLevel = levels,
            };
            tree.Root = Build(new QuadtreeNodeId(0, 0, 0));
            tree.NodeCount = (uint)rep.Nodes;
            RecountNode(tree.Root);
            var all = tree.EnumerateNodes().ToList();
            tree.SubtileCount = (uint)all.Sum(n => n.Subtiles.Count);
            tree.PersistentNonTrivialSubtileCount = (uint)all.Where(n => n.Persistent).Sum(n => n.Tiles.Count);

            // atlas as MaskBuilder sizes it (25% headroom, 4096 wide until the tallest shipped atlas, then 8192)
            int wanted = (int)Math.Ceiling(rep.Tiled * 1.25) + 1;
            int ax = (int)layout.ResourceAtlasSampleCountX, cols = ax / T;
            int rows = (wanted + cols - 1) / cols;
            if (rows * T > 7424 && ax < 8192) { ax = 8192; cols = ax / T; rows = (wanted + cols - 1) / cols; }
            int ay = Math.Max(128, ((rows * T + 127) / 128) * 128);
            tree.ResourceAtlasSampleCountX = (uint)ax;
            tree.ResourceAtlasSampleCountY = (uint)ay;
            rep.Atlas = ax + "x" + ay + " (" + cols * (ay / T) + " tiles for " + rep.Tiled + ")";
            return tree;

            MaskNode Build(QuadtreeNodeId id)
            {
                var n = new MaskNode { Id = id };
                rep.Nodes++;
                if (id.Level > rep.MaxLevel) rep.MaxLevel = id.Level;
                double size = ext / (1 << id.Level), sub = size / perT, texel = sub / pot;
                double nx = x0 + id.IndexX * size, nz = z0 + id.IndexY * size;
                var gathered = new List<(MaskSubtile St, byte[] Tile, int Rank)>();
                bool deeper = false;
                for (int ci = 0; ci < 4; ci++)
                {
                    var cid = id.Child(ci);
                    MaskNode sn;
                    if (!srcNodes.TryGetValue((cid.Level, cid.IndexX, cid.IndexY), out sn)) continue;
                    if (sn.HasChildren && sn.Children.Count == 4 && sn.Children.Any(c => c.Subtiles.Count > 0)) deeper = true;
                    int k = 0;
                    foreach (var st in sn.Subtiles)
                    {
                        byte[] tile = null;
                        if (!st.IsTrivial && k < sn.Tiles.Count) tile = sn.Tiles[k++];
                        int cx = (int)Math.Round((st.WorldCoverage.MinX.Value - nx) / sub);
                        int cz = (int)Math.Round((st.WorldCoverage.MinY.Value - nz) / sub);
                        int rank = cells.IndexOf((cx, cz));
                        if (rank < 0) throw new InvalidOperationException("source subtile outside its BFII node at " + id);
                        gathered.Add((st, tile, rank));
                    }
                }
                foreach (var g in gathered.OrderBy(g => g.St.TerrainLayerIndex).ThenBy(g => g.Rank))
                {
                    var ns = new MaskSubtile
                    {
                        TerrainLayerIndex = g.St.TerrainLayerIndex,
                        WorldCoverage = g.St.WorldCoverage,
                        IsLeafRaw = g.St.IsLeafRaw,
                        MaskDataFullRaw = g.St.MaskDataFullRaw,
                    };
                    if (g.St.IsTrivial || g.Tile == null)
                    {
                        ns.MaskDataFullRaw = 1;
                        ns.HighResolutionCoverageGridStack = FullGrid(levels, stride);
                        rep.Full++;
                    }
                    else
                    {
                        var t = new byte[T * T];
                        double sx0 = g.St.WorldCoverage.MinX.Value, sz0 = g.St.WorldCoverage.MinY.Value;
                        for (int j = 0; j < T; j++)
                            for (int i = 0; i < T; i++)
                            {
                                int si = i - shift, sj = j - shift;
                                if (si >= 0 && si < Ts && sj >= 0 && sj < Ts) { t[j * T + i] = g.Tile[sj * Ts + si]; continue; }
                                double wx = Clamp(sx0 + (i - B + 0.5) * texel, x0 + texel * 0.5, x0 + ext - texel * 0.5);
                                double wz = Clamp(sz0 + (j - B + 0.5) * texel, z0 + texel * 0.5, z0 + ext - texel * 0.5);
                                t[j * T + i] = reader.Alpha(g.St.TerrainLayerIndex, wx, wz, texel);
                                rep.BorderSamples++;
                            }
                        ns.HighResolutionCoverageGridStack = MaskBuilder.CoverageGrid(t, T, B, levels, stride);
                        n.Tiles.Add(t);
                        rep.Tiled++;
                    }
                    n.Subtiles.Add(ns);
                    rep.Subtiles++;
                }
                n.SubtileCount = checked((ushort)n.Subtiles.Count);
                n.NonTrivialSubtileCount = checked((ushort)n.Tiles.Count);
                if (n.Subtiles.Count == 0) return n;      // an empty node has no body, hence no children
                n.BodyPresent = true;
                n.PersistentRaw = 1;
                n.TileDataBytes = (uint)(n.Tiles.Count * T * T);
                if (deeper)
                {
                    n.HasChildrenRaw = 1;
                    for (int ci = 0; ci < 4; ci++) n.Children.Add(Build(id.Child(ci)));
                }
                return n;
            }
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        private static byte[] FullGrid(int levels, int stride)
        {
            var g = new byte[stride];
            int bits = (int)((Math.Pow(4, levels) - 1) / 3);
            for (int b = 0; b < bits; b++) g[b >> 3] |= (byte)(1 << (b & 7));
            return g;
        }

        public sealed class FilterReport
        {
            public int PatchesChecked, PatchesFixed, PatchesUnresolved, SubtilesDropped;
            public SortedDictionary<string, int> FixedSets = new SortedDictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Drop layers from every patch whose layer set no allowed set contains: the weakest layer (least summed alpha;
        /// a full subtile weighs 255 per texel) goes first, never the lowest layer present and never a protected layer
        /// (the hole layer: dropping it fills a hole with ground). Counts, tiles and the atlas are re-derived afterwards
        /// as <see cref="MaskEditor"/> does.
        /// </summary>
        public static FilterReport FilterCombinations(MaskTree tree, IList<int[]> allowedSets, ICollection<int> protectedLayers = null)
        {
            if (tree == null) throw new ArgumentNullException(nameof(tree));
            var rep = new FilterReport();
            var allowed = allowedSets.Select(s => new HashSet<int>(s)).ToList();
            int T = (int)tree.TileSamplesPerSide;
            foreach (var node in tree.EnumerateNodes())
            {
                if (node.Subtiles.Count == 0) continue;
                // tiles belong to the non-trivial subtiles in order
                var tileOf = new Dictionary<MaskSubtile, byte[]>();
                int k = 0;
                foreach (var st in node.Subtiles) if (!st.IsTrivial && k < node.Tiles.Count) tileOf[st] = node.Tiles[k++];

                var byPatch = node.Subtiles.GroupBy(s => (Math.Round(s.WorldCoverage.MinX.Value, 3), Math.Round(s.WorldCoverage.MinY.Value, 3)));
                var drop = new HashSet<MaskSubtile>();
                foreach (var patch in byPatch)
                {
                    rep.PatchesChecked++;
                    var subs = patch.ToList();
                    var set = new SortedSet<int>(subs.Select(s => (int)s.TerrainLayerIndex));
                    if (Covered(set, allowed)) continue;
                    string before = "{" + string.Join(",", set) + "}";
                    int lowest = set.Min;
                    while (!Covered(set, allowed) && set.Count > 1)
                    {
                        int weakest = -1; double weakestWeight = double.MaxValue;
                        foreach (int layer in set)
                        {
                            if (layer == lowest) continue;
                            if (protectedLayers != null && protectedLayers.Contains(layer)) continue;
                            double w = 0;
                            foreach (var st in subs.Where(s => s.TerrainLayerIndex == layer && !drop.Contains(s)))
                            {
                                byte[] tile;
                                w += st.IsTrivial || !tileOf.TryGetValue(st, out tile) ? 255.0 * T * T : tile.Sum(b => (double)b);
                            }
                            if (w < weakestWeight) { weakestWeight = w; weakest = layer; }
                        }
                        if (weakest < 0) break;
                        foreach (var st in subs.Where(s => s.TerrainLayerIndex == weakest)) drop.Add(st);
                        set.Remove(weakest);
                    }
                    if (Covered(set, allowed)) rep.PatchesFixed++; else rep.PatchesUnresolved++;
                    string key = before + " -> {" + string.Join(",", set) + "}" + (Covered(set, allowed) ? "" : " (still undrawable)");
                    int c; rep.FixedSets.TryGetValue(key, out c); rep.FixedSets[key] = c + 1;
                }
                if (drop.Count == 0) continue;
                rep.SubtilesDropped += drop.Count;
                var keepSubs = node.Subtiles.Where(s => !drop.Contains(s)).ToList();
                var keepTiles = keepSubs.Where(s => !s.IsTrivial && tileOf.ContainsKey(s)).Select(s => tileOf[s]).ToList();
                node.Subtiles.Clear(); node.Subtiles.AddRange(keepSubs);
                node.Tiles.Clear(); node.Tiles.AddRange(keepTiles);
                node.SubtileCount = checked((ushort)node.Subtiles.Count);
                node.NonTrivialSubtileCount = checked((ushort)node.Tiles.Count);
                node.TileDataBytes = (uint)(node.Tiles.Count * T * T);
            }
            if (rep.SubtilesDropped > 0) Recount(tree);
            return rep;
        }

        private static bool Covered(ICollection<int> set, List<HashSet<int>> allowed)
        {
            foreach (var a in allowed) if (set.All(a.Contains)) return true;
            return false;
        }

        /// <summary>Recursive non-trivial counts, header totals and atlas height (the MaskEditor bookkeeping).</summary>
        public static void Recount(MaskTree tree)
        {
            int T = (int)tree.TileSamplesPerSide;
            RecountNode(tree.Root);
            var all = tree.EnumerateNodes().ToList();
            tree.SubtileCount = (uint)all.Sum(n => n.Subtiles.Count);
            tree.PersistentNonTrivialSubtileCount = (uint)all.Where(n => n.Persistent).Sum(n => n.Tiles.Count);
            int cols = (int)(tree.ResourceAtlasSampleCountX / (uint)T);
            int need = (int)tree.PersistentNonTrivialSubtileCount + 2;
            int rows = (need + cols - 1) / cols;
            uint y = (uint)Math.Max(128, ((rows * T + 127) / 128) * 128);
            if (y > tree.ResourceAtlasSampleCountY) tree.ResourceAtlasSampleCountY = y;
        }

        private static int RecountNode(MaskNode n)
        {
            if (n == null) return 0;
            int total = n.Persistent ? n.Tiles.Count : 0;
            foreach (var c in n.Children) total += RecountNode(c);
            if (n.BodyPresent || n.Subtiles.Count > 0) n.NonTrivialPersistentSubtileCountRecursive = (ushort)Math.Min(total, ushort.MaxValue);
            return total;
        }

        /// <summary>
        /// The layer sets of a TerrainLayerCombinations resource (res type 2722002395, the `.layercombinations_win32`
        /// beside a streaming tree): u64 name offset, u32 count, u64 records offset; 88-byte records led by u32 layer
        /// count and u64 offset of that many layer bytes. Offsets are from the start of the payload.
        /// </summary>
        public static List<int[]> ReadLayerCombinations(byte[] payload)
        {
            if (payload == null || payload.Length < 20) throw new ArgumentException("layer combinations payload too short");
            int count = BitConverter.ToInt32(payload, 8);
            long ptr = BitConverter.ToInt64(payload, 12);
            if (count < 0 || count > 4096 || ptr < 0 || ptr + 88L * count > payload.Length)
                throw new ArgumentException("not a layer combinations payload (count " + count + ", records at " + ptr + ")");
            var sets = new List<int[]>(count);
            for (int i = 0; i < count; i++)
            {
                long o = ptr + 88L * i;
                int n = BitConverter.ToInt32(payload, (int)o);
                long lp = BitConverter.ToInt64(payload, (int)o + 4);
                if (n < 0 || n > 32 || lp < 0 || lp + n > payload.Length) throw new ArgumentException("bad combination record " + i);
                var set = new int[n];
                for (int j = 0; j < n; j++) set[j] = payload[lp + j];
                sets.Add(set);
            }
            return sets;
        }

        /// <summary>Target mask build options from the target's own mask tree, so the rebuilt tree keeps its layout.</summary>
        public static MaskBuildOptions OptionsLike(MaskTree target, int maxLevel, int refineTolerance, int maxTiledSubtiles)
        {
            int T = (int)target.TileSamplesPerSide;
            return new MaskBuildOptions
            {
                MaxLevel = maxLevel,
                RefineTolerance = refineTolerance,
                MaxTiledSubtiles = maxTiledSubtiles,
                TileSamplesPerSide = T,
                TileBorder = (T - Pot(T)) / 2,
                SubtileRelativeLevel = (int)target.SubtileRelativeLevel,
                CoverageGridLevels = (int)target.CoverageGridLevelCount,
                BlurrinessLog2 = target.BlurrinessLog2,
                AtlasSampleCountX = (int)target.ResourceAtlasSampleCountX,
            };
        }

        private static int Pot(int v) { int p = 1; while (p * 2 <= v) p *= 2; return p; }
    }
}
