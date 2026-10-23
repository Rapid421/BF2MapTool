#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainBuild\HeightfieldBuilder.cs (sha256:AFD5C5373E13BF91).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Linq;
using Bfii.TerrainDerive;
using Bfii.TerrainFormat;

namespace Bfii.TerrainBuild
{
    public sealed class HeightfieldBuildOptions
    {
        /// <summary>Levels above the first data level; BFII_EXACT: 2 on every shipped 137-tile terrain.</summary>
        public int SkipLevels = 2;

        /// <summary>
        /// An explicit set of data nodes (a template, e.g. a shipped terrain's own structure). Null: refine
        /// automatically from <see cref="SkipLevels"/> down to <see cref="TerrainFrame.MaxLevel"/>.
        /// </summary>
        public HashSet<QuadtreeNodeId>? DataNodes;

        /// <summary>Refine a node when its bilinear surface misses the next level's samples by more than this.</summary>
        public double RefineToleranceMetres = 0.05;

        /// <summary>Budget for automatic refinement (persistent data nodes; every one sits in the atlas).</summary>
        public int MaxDataNodes = 1400;

        /// <summary>World rectangles (minX, minZ, maxX, maxZ) refined to the deepest level first.</summary>
        public List<double[]> DetailRegions = new List<double[]>();

        /// <summary>Atlas width in samples; the height grows to hold every persistent node.</summary>
        public int AtlasSampleCountX = 4096;

        /// <summary>Minimum atlas height (e.g. a shipped terrain's, when rebuilding it as a control).</summary>
        public int AtlasSampleCountYMin;

        /// <summary>
        /// Spare slots as a fraction of the persistent nodes. Shipped atlases carry some (Tatooine 4096x1920 holds
        /// 406 slots for 224 nodes); every one satisfies slots >= persistent nodes.
        /// </summary>
        public double AtlasHeadroom = 0.25;

        /// <summary>BFII_EXACT: holes exist only on leaf data nodes; coarser nodes keep ground.</summary>
        public bool HolesOnLeavesOnly = true;

        /// <summary>
        /// Edit a shipped heightfield instead of generating one: every node of this tree that does not reach
        /// <see cref="EditRegion"/> (its border samples included) keeps its payload, box and flags byte for byte; a node
        /// that reaches it starts from its own shipped samples and takes the source's only inside the region (so the
        /// ground DICE stored under holes at coarse levels survives). New nodes are generated from the source.
        /// </summary>
        public HeightfieldTree? BaseTree;

        /// <summary>World rectangle (minX, minZ, maxX, maxZ) that <see cref="BaseTree"/> editing may change.</summary>
        public double[]? EditRegion;

        public MinMaxLayout MinMaxLayout = new MinMaxLayout
        {
            WriteOrder = MinMaxWriteOrder.ReverseLevel,
            PairOrder = MinMaxPairOrder.MinFirst,
            CellSpan = MinMaxCellSpan.InclusiveSharedEdge,
        };

        public bool BuildOccluders = true;

        /// <summary>
        /// Metres the occluder planes are lowered below the fitted samples. DICE's shipped grids sit a median 1.2 m
        /// (99th percentile 20 m) below the ground they cover, partly because they were fitted to a finer raster;
        /// the rendered surface also carries tessellation and detail displacement. A margin keeps a generated grid
        /// on the safe side of both.
        /// </summary>
        public double OccluderMarginMetres = 1.0;

        /// <summary>Lower every node's occluder edges to the neighbouring nodes' (a conservative stand-in for FIFA's matchNodeEdges).</summary>
        public bool MatchOccluderEdges = true;
    }

    public sealed class HeightfieldBuildReport
    {
        public int DataNodes;
        public int SkippedNodes;
        public int EmptyNodes;
        public int HoleNodes;
        public int[] DataNodesPerLevel = new int[16];
        public double MinMetres, MaxMetres;
        public string Atlas = "";
        public int RefineStoppedByBudget;
    }

    /// <summary>
    /// Builds the heightfield raster tree (tag 0) from a height source on a <see cref="TerrainFrame"/>.
    /// Every rule it applies is measured on shipped data (SPEC §2): decimated samples, neighbour borders, clamped
    /// world edges, holes only on leaves, the top <see cref="HeightfieldBuildOptions.SkipLevels"/> levels skipped
    /// with a min/max block, boxes spanning the subtree, atlas capacity.
    /// </summary>
    public static class HeightfieldBuilder
    {
        public static HeightfieldTree Build(IHeightSource source, TerrainFrame frame, HeightfieldBuildOptions options,
            out HeightfieldBuildReport report)
        {
            frame.Validate();
            var rep = new HeightfieldBuildReport();
            report = rep;
            int side = frame.TileSamplesPerSide, border = frame.BorderWidth, pot = frame.Pot;
            bool big = side == 265;

            var dataNodes = options.DataNodes != null
                ? new HashSet<QuadtreeNodeId>(options.DataNodes)
                : Refine(source, frame, options, rep);

            var tree = new HeightfieldTree
            {
                NodeSamplesPerSide = (uint)side,
                BlurrinessLog2 = big ? 0 : 1,
                WorldSizeY = F32.FromValue(frame.WorldSizeY),
                PhysicsMetersPerSample = F32.FromValue(-1f),
                PhysicsCropWidth = -1,
                Unknown0x1DRaw = 1,
                Unknown0x1E = F32.FromValue(0.1f),
                MinMaxStackDepth = (uint)HeightfieldSizing.MinMaxLevelCount(pot),
                OccluderGridStackDepth = (uint)HeightfieldSizing.OccluderGridLevelCount(pot),
                DensityMapNodeSamplesPerSide = (uint)(pot / 4 + 3),
                DensityMapBorderWidth = 1,
                DensityMapNodeSamplesPerSidePot = (uint)(pot / 4),
                DensityMapResolutionRatio = F32.FromValue(0.25f),
                NodeBorderWidth = (uint)border,
                Flow = HeightfieldNodeFlow.Merged,
            };

            var root0 = new QuadtreeNodeId(0, 0, 0);

            // Leaves: data nodes none of whose children are data nodes.
            bool IsLeaf(QuadtreeNodeId id) => !dataNodes.Contains(id.Child(0)) && !dataNodes.Contains(id.Child(1))
                                           && !dataNodes.Contains(id.Child(2)) && !dataNodes.Contains(id.Child(3));

            // Samples per data node, then the derived arrays bottom-up (occluders need the children's grids).
            var samples = new Dictionary<QuadtreeNodeId, ushort[]>();
            var baseNodes = options.BaseTree == null ? null
                : options.BaseTree.EnumerateNodes().Where(n => n.Payload != null).ToDictionary(n => n.Id);
            var untouched = new HashSet<QuadtreeNodeId>();
            foreach (var id in dataNodes)
            {
                bool leafHoles = !options.HolesOnLeavesOnly || IsLeaf(id);
                if (baseNodes != null && baseNodes.TryGetValue(id, out var bn) && options.EditRegion != null)
                {
                    var own = SampleBytes.ToSamples(bn.Payload!.AtlasData);
                    if (!Reaches(frame, id, options.EditRegion)) { samples[id] = own; untouched.Add(id); continue; }
                    samples[id] = Patch(source, frame, id, own, options.EditRegion, leafHoles);
                    continue;
                }
                samples[id] = NodeSamples(source, frame, id, allowHoles: leafHoles);
            }

            var occluders = new Dictionary<QuadtreeNodeId, ushort[][]>();
            int depth = (int)tree.OccluderGridStackDepth;
            if (options.BuildOccluders)
            {
                foreach (var id in dataNodes.OrderByDescending(n => n.Level))
                {
                    var kids = new ushort[]?[4];
                    for (int ci = 0; ci < 4; ci++)
                        kids[ci] = occluders.TryGetValue(id.Child(ci), out var g) ? g[depth - 2] : null;
                    occluders[id] = OccluderPlaneFit.BuildNode(samples[id], side, border, pot, depth,
                        frame.NodeSize(id.Level), frame.MetresPerUnit, kids, options.OccluderMarginMetres);
                }
                if (options.MatchOccluderEdges) MatchEdges(occluders, depth);
            }

            // Min/max, bottom-up (BFII_EXACT, SPEC section 2): a leaf's finest level comes from its own samples,
            // any node with four data children stitches their next-coarser levels, coarser levels merge 2x2. The
            // coarsest cell is the node's subtree range, which is also its box.
            int mmDepth = (int)tree.MinMaxStackDepth;
            var minMax = new Dictionary<QuadtreeNodeId, MinMaxLevels>();
            var skippedIds = new List<QuadtreeNodeId>();
            CollectSkipped(root0, skippedIds);
            foreach (var id in dataNodes.Concat(skippedIds).OrderByDescending(n => n.Level))
            {
                var kids = new MinMaxLevels?[4];
                int present = 0;
                for (int ci = 0; ci < 4; ci++)
                    if (minMax.TryGetValue(id.Child(ci), out var km)) { kids[ci] = km; present++; }
                ushort[]? own = samples.TryGetValue(id, out var sm) ? sm
                              : present < 4 ? NodeSamples(source, frame, id, allowHoles: false) : null;
                minMax[id] = MinMaxLevels.Build(own, side, border, pot, mmDepth, kids);
            }

            tree.Root = BuildNode(root0, null);

            // editing: nodes the region does not reach keep the shipped payload, box and flags exactly
            if (baseNodes != null)
                foreach (var n in tree.EnumerateNodes())
                {
                    if (n.Payload == null || !untouched.Contains(n.Id) || !baseNodes.TryGetValue(n.Id, out var bn)) continue;
                    // a child set that changed below an untouched node would make its min/max stale; untouched means
                    // the region does not reach the node, so it reaches none of its children either
                    n.Payload = bn.Payload;
                    n.BoundingBox = bn.BoundingBox;
                    n.ContainsHolesRaw = bn.ContainsHolesRaw;
                }
            tree.NodeCount = (uint)tree.EnumerateNodes().Count();
            tree.PersistentNodeCount = (uint)rep.DataNodes;
            tree.SkippedNodeCount = (uint)rep.SkippedNodes;
            tree.PersistentDedicatedServerNodeCount = 0;

            SizeAtlas(tree, options, rep);
            return tree;

            HeightfieldNode BuildNode(QuadtreeNodeId id, (ushort Lo, ushort Hi)? parentRange)
            {
                var n = new HeightfieldNode { Id = id };
                bool skipped = id.Level < options.SkipLevels && SubtreeHasData(id);
                bool hasData = !skipped && dataNodes.Contains(id);
                bool anyBelow = skipped ? SubtreeHasData(id) : false;

                (ushort Lo, ushort Hi)? range = minMax.TryGetValue(id, out var mmOwn) ? mmOwn.Range : null;
                n.BoundingBox = Box(frame, id, range ?? parentRange);

                if (!skipped && !hasData)
                {
                    // A no-data node ends after its flags (Merged flow).
                    n.FlagsRead = true;
                    rep.EmptyNodes++;
                    return n;
                }

                n.FlagsRead = true;
                n.TailFlagsRead = true;
                n.HasChildrenFlagRead = true;
                if (skipped)
                {
                    n.SkippedRaw = 1;
                    rep.SkippedNodes++;
                    n.SkippedMinMax = minMax[id].ToBytes();
                    n.HasChildrenRaw = (byte)(anyBelow ? 1 : 0);
                }
                else
                {
                    n.HasDataRaw = 1;
                    n.PersistentRaw = 1;
                    var s = samples[id];
                    bool holes = false;
                    foreach (ushort v in s) if (v == 0) { holes = true; break; }
                    n.ContainsHolesRaw = (byte)(holes ? 1 : 0);
                    if (holes) rep.HoleNodes++;
                    n.Payload = new HeightfieldNodePayload
                    {
                        AtlasData = SampleBytes.ToBytes(s),
                        MinMaxStackData = minMax[id].ToBytes(),
                        OccluderGridStackData = options.BuildOccluders
                            ? OccluderPlaneFit.ToBytes(occluders[id])
                            : new byte[tree.OccluderBytes],
                        DensityData = DensityMapBuilder.BuildNode(s, side, border, pot,
                            (int)tree.DensityMapNodeSamplesPerSide, (int)tree.DensityMapBorderWidth, frame.MetresPerUnit),
                    };
                    rep.DataNodes++;
                    rep.DataNodesPerLevel[id.Level]++;
                    n.HasChildrenRaw = (byte)(IsLeaf(id) ? 0 : 1);
                }

                if (n.HasChildren)
                    for (int ci = 0; ci < 4; ci++) n.Children.Add(BuildNode(id.Child(ci), range ?? parentRange));
                return n;
            }

            bool SubtreeHasData(QuadtreeNodeId id)
            {
                foreach (var d in dataNodes) if (IsAncestorOrSelf(id, d)) return true;
                return false;
            }

            void CollectSkipped(QuadtreeNodeId id, List<QuadtreeNodeId> into)
            {
                if (id.Level >= options.SkipLevels || !SubtreeHasData(id)) return;
                into.Add(id);
                for (int ci = 0; ci < 4; ci++) CollectSkipped(id.Child(ci), into);
            }
        }

        /// <summary>Whether the region reaches a node's samples, borders included.</summary>
        public static bool Reaches(TerrainFrame f, QuadtreeNodeId id, double[] region)
        {
            double size = f.NodeSize(id.Level), pad = f.BorderWidth * size / f.Pot;
            double x0 = f.MinX + id.IndexX * size - pad, z0 = f.MinZ + id.IndexY * size - pad;
            double x1 = x0 + size + 2 * pad, z1 = z0 + size + 2 * pad;
            return x0 <= region[2] && x1 >= region[0] && z0 <= region[3] && z1 >= region[1];
        }

        /// <summary>A shipped node's samples with the source's inside the region.</summary>
        public static ushort[] Patch(IHeightSource source, TerrainFrame f, QuadtreeNodeId id, ushort[] own, double[] region, bool allowHoles)
        {
            int side = f.TileSamplesPerSide, border = f.BorderWidth, pot = f.Pot;
            long step = 1L << (f.MaxLevel - id.Level);
            long ox = (long)id.IndexX * pot, oz = (long)id.IndexY * pot;
            var s = (ushort[])own.Clone();
            const double eps = 1e-6;
            for (int j = 0; j < side; j++)
            {
                long gz = (oz + j - border) * step;
                double z = f.WorldZ(gz);
                if (z < region[1] - eps || z > region[3] + eps) continue;
                for (int i = 0; i < side; i++)
                {
                    long gx = (ox + i - border) * step;
                    double x = f.WorldX(gx);
                    if (x < region[0] - eps || x > region[2] + eps) continue;
                    s[j * side + i] = source.Sample(gx, gz, allowHoles);
                }
            }
            return s;
        }

        public static bool IsAncestorOrSelf(QuadtreeNodeId a, QuadtreeNodeId d)
        {
            if (d.Level < a.Level) return false;
            int shift = d.Level - a.Level;
            return (d.IndexX >> shift) == a.IndexX && (d.IndexY >> shift) == a.IndexY;
        }

        public static AxisAlignedBox Box(TerrainFrame f, QuadtreeNodeId id, (ushort Lo, ushort Hi)? range)
        {
            double size = f.NodeSize(id.Level);
            // Single precision with a float reciprocal: the one formula that reproduces every shipped box
            // (Tatooine, Yavin, Scarif, Naboo - 100% of 2,960 values; plain Y/65535 misses Naboo by one ulp).
            float scale = f.WorldSizeY * (1f / 65535f);
            float lo = range.HasValue ? range.Value.Lo * scale : 0f;
            float hi = range.HasValue ? range.Value.Hi * scale : 0f;
            return new AxisAlignedBox
            {
                MinX = F32.FromValue((float)(f.MinX + id.IndexX * size)),
                MinY = F32.FromValue(lo),
                MinZ = F32.FromValue((float)(f.MinZ + id.IndexY * size)),
                MaxX = F32.FromValue((float)(f.MinX + (id.IndexX + 1) * size)),
                MaxY = F32.FromValue(hi),
                MaxZ = F32.FromValue((float)(f.MinZ + (id.IndexY + 1) * size)),
            };
        }

        /// <summary>A node's tile: the global grid decimated to its level, border samples from the neighbours.</summary>
        public static ushort[] NodeSamples(IHeightSource source, TerrainFrame f, QuadtreeNodeId id, bool allowHoles)
        {
            int side = f.TileSamplesPerSide, border = f.BorderWidth, pot = f.Pot;
            long step = 1L << (f.MaxLevel - id.Level);
            long ox = (long)id.IndexX * pot, oz = (long)id.IndexY * pot;
            var s = new ushort[side * side];
            for (int j = 0; j < side; j++)
            {
                long gz = (oz + j - border) * step;
                for (int i = 0; i < side; i++)
                {
                    long gx = (ox + i - border) * step;
                    bool isBorder = i < border || j < border || i > border + pot || j > border + pot;
                    if (isBorder && !source.Covers(gx, gz))
                    {
                        // Nothing there (a partial shipped tree): repeat the node's own edge sample.
                        long cx = (ox + Math.Min(Math.Max(i - border, 0), pot)) * step;
                        long cz = (oz + Math.Min(Math.Max(j - border, 0), pot)) * step;
                        s[j * side + i] = source.Sample(cx, cz, allowHoles);
                        continue;
                    }
                    s[j * side + i] = source.Sample(gx, gz, allowHoles);
                }
            }
            return s;
        }

        // ---------------------------------------------------------------------------------------------------

        private static HashSet<QuadtreeNodeId> Refine(IHeightSource source, TerrainFrame f, HeightfieldBuildOptions o,
            HeightfieldBuildReport report)
        {
            var set = new HashSet<QuadtreeNodeId>();
            int start = o.SkipLevels;
            int n0 = 1 << start;
            var queue = new List<(double Priority, QuadtreeNodeId Id)>();
            for (int y = 0; y < n0; y++)
                for (int x = 0; x < n0; x++)
                {
                    var id = new QuadtreeNodeId((ushort)x, (ushort)y, (byte)start);
                    set.Add(id);
                    if (start < f.MaxLevel) queue.Add((Priority(id), id));
                }

            while (queue.Count > 0)
            {
                int bi = 0;
                for (int i = 1; i < queue.Count; i++) if (queue[i].Priority > queue[bi].Priority) bi = i;
                var (p, id) = queue[bi];
                queue.RemoveAt(bi);
                if (p <= o.RefineToleranceMetres) break;
                if (set.Count + 4 > o.MaxDataNodes) { report.RefineStoppedByBudget++; continue; }
                for (int ci = 0; ci < 4; ci++)
                {
                    var c = id.Child(ci);
                    set.Add(c);
                    if (c.Level < f.MaxLevel) queue.Add((Priority(c), c));
                }
            }
            return set;

            double Priority(QuadtreeNodeId id)
            {
                if (InDetailRegion(id)) return 1e9 - id.Level;       // forced, coarse first
                return RefineError(source, f, id);
            }

            bool InDetailRegion(QuadtreeNodeId id)
            {
                double size = f.NodeSize(id.Level);
                double x0 = f.MinX + id.IndexX * size, z0 = f.MinZ + id.IndexY * size;
                foreach (var r in o.DetailRegions)
                    if (x0 < r[2] && x0 + size > r[0] && z0 < r[3] && z0 + size > r[1]) return true;
                return false;
            }
        }

        /// <summary>
        /// Max |next level's sample - this level's bilinear surface| over the node, in metres: what refining would
        /// add. Evaluated at the odd positions of the next level (the samples this level does not store).
        /// </summary>
        public static double RefineError(IHeightSource source, TerrainFrame f, QuadtreeNodeId id)
        {
            int pot = f.Pot;
            long stepC = 1L << (f.MaxLevel - id.Level), stepF = stepC / 2;
            long ox = (long)id.IndexX * pot * stepC, oz = (long)id.IndexY * pot * stepC;
            double worst = 0;
            for (int j = 0; j <= 2 * pot; j++)
            {
                for (int i = 0; i <= 2 * pot; i++)
                {
                    if ((i & 1) == 0 && (j & 1) == 0) continue;
                    long gx = ox + i * stepF, gz = oz + j * stepF;
                    double fine = source.Sample(gx, gz, false);
                    int ci = i / 2, cj = j / 2;
                    double a = source.Sample(ox + ci * stepC, oz + cj * stepC, false);
                    double b = source.Sample(ox + (ci + (i & 1)) * stepC, oz + cj * stepC, false);
                    double c = source.Sample(ox + ci * stepC, oz + (cj + (j & 1)) * stepC, false);
                    double d = source.Sample(ox + (ci + (i & 1)) * stepC, oz + (cj + (j & 1)) * stepC, false);
                    double coarse = (a + b + c + d) / 4.0;
                    if ((i & 1) == 1 && (j & 1) == 0) coarse = (a + b) / 2.0;
                    if ((i & 1) == 0 && (j & 1) == 1) coarse = (a + c) / 2.0;
                    double e = Math.Abs(fine - coarse);
                    if (e > worst) worst = e;
                }
            }
            return worst * f.MetresPerUnit;
        }

        /// <summary>
        /// Shared edges between same-level nodes take the lower of the two grids, so no occluder edge sits above the
        /// neighbour's surface. A conservative stand-in for FIFA's matchNodeEdges.
        /// </summary>
        private static void MatchEdges(Dictionary<QuadtreeNodeId, ushort[][]> grids, int depth)
        {
            foreach (var kv in grids)
            {
                var id = kv.Key;
                var right = new QuadtreeNodeId((ushort)(id.IndexX + 1), id.IndexY, id.Level);
                var down = new QuadtreeNodeId(id.IndexX, (ushort)(id.IndexY + 1), id.Level);
                for (int k = 0; k < depth; k++)
                {
                    int pts = (1 << k) + 1;
                    var a = kv.Value[k];
                    if (grids.TryGetValue(right, out var r))
                    {
                        var b = r[k];
                        for (int z = 0; z < pts; z++)
                        {
                            int ia = z * pts + pts - 1, ib = z * pts;
                            ushort m = Math.Min(a[ia], b[ib]);
                            a[ia] = m; b[ib] = m;
                        }
                    }
                    if (grids.TryGetValue(down, out var dn))
                    {
                        var b = dn[k];
                        for (int x = 0; x < pts; x++)
                        {
                            int ia = (pts - 1) * pts + x, ib = x;
                            ushort m = Math.Min(a[ia], b[ib]);
                            a[ia] = m; b[ib] = m;
                        }
                    }
                }
            }
        }

        private static void SizeAtlas(HeightfieldTree tree, HeightfieldBuildOptions o, HeightfieldBuildReport report)
        {
            int side = (int)tree.NodeSamplesPerSide;
            int persistent = (int)tree.PersistentNodeCount;
            int wanted = (int)Math.Ceiling(persistent * (1 + Math.Max(0, o.AtlasHeadroom)));
            int x = o.AtlasSampleCountX;
            int y;
            while (true)
            {
                int cols = x / side;
                int rows = (wanted + cols - 1) / cols;
                y = Math.Max(128, ((rows * side + 127) / 128) * 128);
                if (o.AtlasSampleCountYMin > y) y = o.AtlasSampleCountYMin;
                if (y <= 16384 || x >= 16384) break;
                x *= 2;
            }
            tree.ResourceAtlasSampleCountX = (uint)x;
            tree.ResourceAtlasSampleCountY = (uint)y;
            report.Atlas = x + "x" + y + " (" + (x / side) * (y / side) + " slots for " + persistent + " nodes)";
        }
    }
}
