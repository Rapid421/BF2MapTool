#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainDerive\Derivations.cs (sha256:D0E3F42745213A25).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainDerive
{
    // =======================================================================================
    // Occluder grid stack
    // =======================================================================================

    /// <summary>
    /// How a coarse occluder grid point is reduced from the height samples it covers.
    /// <b>UNKNOWN for BFII.</b> W02 §4.6 records only that there are 8 heightfield samples per
    /// occluder-grid sample side and that <c>generateOccluderGrid</c> exists; it does not record the
    /// reduction. There is deliberately no default: a caller must state the choice, and
    /// <see cref="OccluderGrid.DetectLayout"/> decides it from shipped bytes.
    /// </summary>
    public enum OccluderReduction
    {
        /// <summary>Lowest sample in the covered window. Conservative for occlusion.</summary>
        Min,

        /// <summary>Highest sample in the covered window.</summary>
        Max,

        /// <summary>The sample at the grid point itself, no reduction.</summary>
        Point,
    }

    /// <summary>
    /// Level ordering of the occluder grid stack.
    /// <para>
    /// W02 §4.6 gives FIFA's write loop as "occluder grids, level 0..N-1: <c>u16[((1&lt;&lt;i)+1)^2]</c>",
    /// i.e. coarsest first. W01 §4.4.1's BFII size formula sums
    /// <c>((occW &gt;&gt; i) + 1)^2</c> for <c>i &lt; depth</c>, which enumerates the same multiset in the
    /// opposite order. <b>Total size therefore agrees between the two reports and only the ordering
    /// is at stake.</b> This is logged as a genuine open item; it is not a contradiction about size.
    /// </para>
    /// </summary>
    public enum OccluderGridOrder
    {
        /// <summary>Level 0 (coarsest, 2x2 points) first — FIFA's documented write loop.</summary>
        CoarsestFirst = 0,

        /// <summary>Finest level first — the order W01's size formula enumerates.</summary>
        FinestFirst = 1,
    }

    public sealed class OccluderLayout
    {
        public OccluderReduction Reduction { get; set; }
        public OccluderGridOrder Order { get; set; }

        public override string ToString() => $"{Reduction}/{Order}";

        public static IEnumerable<OccluderLayout> AllVariants()
        {
            foreach (OccluderReduction r in new[] { OccluderReduction.Min, OccluderReduction.Max, OccluderReduction.Point })
            foreach (OccluderGridOrder o in new[] { OccluderGridOrder.CoarsestFirst, OccluderGridOrder.FinestFirst })
                yield return new OccluderLayout { Reduction = r, Order = o };
        }
    }

    /// <summary>
    /// The per-node occluder grid stack.
    /// <para>
    /// Endor ships <c>OccluderEnable = True</c>, <c>OccluderPatchFacesPerSide = 4</c>,
    /// <c>OccluderLodScale = 0.5</c> (W01 §3.3), so this is live data on the representative donor and
    /// a height edit invalidates it for the edited node and every ancestor (W01 §7.2 item 3).
    /// </para>
    /// <para>
    /// Do not confuse this with an authored occluder <i>mesh</i>: <c>ResourceType.OccluderMesh</c>
    /// is ordinary level content and must never enter a terrain closure (W01 §3.7b).
    /// </para>
    /// </summary>
    public static class OccluderGrid
    {
        public static long StackSize(int levelCount) => HeightfieldSizing.OccluderGridStackSize(levelCount);

        /// <summary>
        /// Build the occluder stack. <paramref name="layout"/> is required and has no default,
        /// because the reduction is <see cref="Provenance.Unknown"/> for BFII.
        /// </summary>
        public static ushort[] Build(ushort[] samples, int samplesPerSide, int borderWidth,
            int levelCount, OccluderLayout layout)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            if (layout is null) throw new ArgumentNullException(nameof(layout));
            if (levelCount <= 0) return Array.Empty<ushort>();
            if (samples.Length != (long)samplesPerSide * samplesPerSide)
                throw new ArgumentException("sample grid is not samplesPerSide^2", nameof(samples));

            int pot = samplesPerSide - 2 * borderWidth - 1;
            if (pot <= 0) throw new ArgumentException("border width leaves no interior samples", nameof(borderWidth));

            var levels = new ushort[levelCount][];
            for (int l = 0; l < levelCount; l++)
            {
                int points = HeightfieldSizing.OccluderPointsPerSide(l);
                int cells = points - 1;                 // cells per side at this level
                var grid = new ushort[(long)points * points];

                for (int py = 0; py < points; py++)
                {
                    for (int px = 0; px < points; px++)
                    {
                        // Grid point (px,py) sits at pot-space position px*pot/cells.
                        int p0x = (int)((long)px * pot / cells);
                        int p0y = (int)((long)py * pot / cells);

                        ushort v;
                        if (layout.Reduction == OccluderReduction.Point)
                        {
                            v = SampleAt(samples, samplesPerSide, borderWidth + p0x, borderWidth + p0y);
                        }
                        else
                        {
                            // Reduce over the half-cell window on each side of the grid point, which
                            // is the window a vertex-centred grid covers.
                            int half = Math.Max(1, pot / cells / 2);
                            ushort acc = layout.Reduction == OccluderReduction.Min ? ushort.MaxValue : ushort.MinValue;
                            for (int dy = -half; dy <= half; dy++)
                            {
                                int y = borderWidth + Clamp(p0y + dy, 0, pot);
                                for (int dx = -half; dx <= half; dx++)
                                {
                                    int x = borderWidth + Clamp(p0x + dx, 0, pot);
                                    ushort s = SampleAt(samples, samplesPerSide, x, y);
                                    if (layout.Reduction == OccluderReduction.Min) { if (s < acc) acc = s; }
                                    else if (s > acc) acc = s;
                                }
                            }
                            v = acc;
                        }

                        grid[py * points + px] = v;
                    }
                }

                levels[l] = grid;
            }

            var outBuf = new ushort[StackSize(levelCount)];
            int w = 0;
            if (layout.Order == OccluderGridOrder.CoarsestFirst)
                for (int l = 0; l < levelCount; l++) { Array.Copy(levels[l], 0, outBuf, w, levels[l].Length); w += levels[l].Length; }
            else
                for (int l = levelCount - 1; l >= 0; l--) { Array.Copy(levels[l], 0, outBuf, w, levels[l].Length); w += levels[l].Length; }

            return outBuf;

            static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

            static ushort SampleAt(ushort[] s, int side, int x, int y)
            {
                if (x < 0) x = 0; else if (x >= side) x = side - 1;
                if (y < 0) y = 0; else if (y >= side) y = side - 1;
                return s[y * side + x];
            }
        }

        public sealed class DetectionResult
        {
            public List<(OccluderLayout Layout, bool Matches, int DifferingEntries)> Candidates { get; }
                = new List<(OccluderLayout, bool, int)>();

            public OccluderLayout? UniqueMatch { get; set; }
            public int MatchCount { get; set; }

            public Provenance Grade =>
                MatchCount == 1 ? Provenance.BfiiExact
                : MatchCount == 0 ? Provenance.Unknown
                : Provenance.Inferred;
        }

        /// <summary>
        /// Decide the occluder reduction and ordering from a shipped node, offline. Zero matches is a
        /// legitimate and useful outcome: it means neither the FIFA-shaped reduction nor either
        /// ordering describes BFII, and the grid must be re-derived from BFII data before any writer
        /// emits one.
        /// </summary>
        public static DetectionResult DetectLayout(ushort[] samples, int samplesPerSide,
            int borderWidth, int levelCount, ushort[] shipped)
        {
            var result = new DetectionResult();

            foreach (var layout in OccluderLayout.AllVariants())
            {
                ushort[] built;
                try
                {
                    built = Build(samples, samplesPerSide, borderWidth, levelCount, layout);
                }
                catch (ArgumentException)
                {
                    result.Candidates.Add((layout, false, int.MaxValue));
                    continue;
                }

                if (built.Length != shipped.Length)
                {
                    result.Candidates.Add((layout, false, Math.Abs(built.Length - shipped.Length)));
                    continue;
                }

                int diff = 0;
                for (int i = 0; i < built.Length; i++) if (built[i] != shipped[i]) diff++;
                result.Candidates.Add((layout, diff == 0, diff));
            }

            foreach (var c in result.Candidates)
                if (c.Matches) { result.MatchCount++; result.UniqueMatch ??= c.Layout; }

            if (result.MatchCount != 1) result.UniqueMatch = null;
            return result;
        }
    }

    // =======================================================================================
    // Density map
    // =======================================================================================

    /// <summary>
    /// The per-node scattering density raster: <c>densityMapNodeSamplesPerSide^2</c> <b>bytes</b>,
    /// one byte per sample, the last block of a node's inline payload (W01 §4.4.1).
    /// <para>
    /// Sizing is FIFA21_SOURCE and cross-checkable against a shipped header
    /// (<see cref="HeightfieldSizing.CrossCheckAgainstFifaRules"/>). The <b>content</b> rule —
    /// what <c>generateDensityMapData</c> and <c>applyUserDensityMap</c> actually compute — is
    /// <see cref="Provenance.Unknown"/>, so this class deliberately offers only sizing, a uniform
    /// fill, and a box resample. It does not pretend to reproduce shipped density bytes.
    /// </para>
    /// <para>
    /// W01 §7.2 item 6 classifies density as "probably unaffected by a height edit, but must be
    /// asserted rather than assumed" — so the useful operation for M2 is
    /// <see cref="AssertUnchanged"/>, not regeneration.
    /// </para>
    /// </summary>
    public static class DensityMap
    {
        public static int SamplesPerSide(int targetSamplesPerSidePot)
            => HeightfieldSizing.DensityMapNodeSamplesPerSide(targetSamplesPerSidePot);

        public static int ByteCount(int densityMapNodeSamplesPerSide)
            => densityMapNodeSamplesPerSide * densityMapNodeSamplesPerSide;

        public static byte[] Uniform(int samplesPerSide, byte value)
        {
            var b = new byte[ByteCount(samplesPerSide)];
            if (value != 0) for (int i = 0; i < b.Length; i++) b[i] = value;
            return b;
        }

        /// <summary>Box-average resample of a source density raster. Provenance: <see cref="Provenance.Unknown"/>.</summary>
        public static byte[] ResampleBox(byte[] source, int sourceSide, int targetSide)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (source.Length != sourceSide * sourceSide)
                throw new ArgumentException("source is not sourceSide^2", nameof(source));

            var dst = new byte[targetSide * targetSide];
            for (int y = 0; y < targetSide; y++)
            {
                int sy0 = (int)((long)y * sourceSide / targetSide);
                int sy1 = Math.Max(sy0 + 1, (int)((long)(y + 1) * sourceSide / targetSide));
                for (int x = 0; x < targetSide; x++)
                {
                    int sx0 = (int)((long)x * sourceSide / targetSide);
                    int sx1 = Math.Max(sx0 + 1, (int)((long)(x + 1) * sourceSide / targetSide));

                    int sum = 0, n = 0;
                    for (int sy = sy0; sy < sy1 && sy < sourceSide; sy++)
                        for (int sx = sx0; sx < sx1 && sx < sourceSide; sx++)
                        { sum += source[sy * sourceSide + sx]; n++; }

                    dst[y * targetSide + x] = n == 0 ? (byte)0 : (byte)(sum / n);
                }
            }
            return dst;
        }

        /// <summary>M2's actual requirement: the density block must be byte-equal after a height edit.</summary>
        public static bool AssertUnchanged(byte[] before, byte[] after, out int firstDifference)
        {
            firstDifference = -1;
            if (before.Length != after.Length) { firstDifference = Math.Min(before.Length, after.Length); return false; }
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i]) { firstDifference = i; return false; }
            return true;
        }
    }

    // =======================================================================================
    // Hole guard
    // =======================================================================================

    /// <summary>
    /// The hole marker and the guard filter that keeps a painted height from accidentally reading as
    /// a hole.
    /// <para>
    /// Consumption is confirmed for BFII: <b>Havok treats height sample 0 as a hole, per quad,
    /// propagated to all four corners</b> (W07 §3.3, cited in plan §5.1 and §7 M4). That makes the
    /// hard constraint in plan §7 M4 non-negotiable: <b>never write a non-hole sample of exactly
    /// 0</b>.
    /// </para>
    /// <para>
    /// The guard's constants are FIFA21_SOURCE (W02 §4.6): <c>HoleHeightMarker = 0.1 m</c>,
    /// <c>HoleHeightMarkerSample = 0</c>, and
    /// <c>epsilon = u16(0.1 * 0xffff / maxHeight) + 2</c>. FIFA applies a
    /// <c>heightfieldMaxFilter(root, epsilon)</c>; whether that filter has a neighbourhood or is a
    /// per-sample clamp is <see cref="Provenance.Unknown"/>, so this implements the per-sample clamp
    /// and says so rather than inventing a kernel.
    /// </para>
    /// </summary>
    public static class HoleGuard
    {
        public const double HoleHeightMarkerMetres = 0.1;
        public const ushort HoleHeightMarkerSample = 0;

        public static ushort EpsilonFor(double maxHeightMetres)
        {
            if (maxHeightMetres <= 0) throw new ArgumentOutOfRangeException(nameof(maxHeightMetres));
            double raw = HoleHeightMarkerMetres * 0xffff / maxHeightMetres;
            long eps = (long)raw + 2;
            if (eps < 1) eps = 1;
            if (eps > ushort.MaxValue) eps = ushort.MaxValue;
            return (ushort)eps;
        }

        /// <summary>
        /// Raise every non-hole sample to at least <paramref name="epsilon"/>, in place.
        /// Samples equal to <see cref="HoleHeightMarkerSample"/> are intentional holes and are left
        /// alone. Returns the number of samples changed.
        /// </summary>
        public static int ApplyGuard(ushort[] samples, ushort epsilon)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            int changed = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                if (samples[i] == HoleHeightMarkerSample) continue;
                if (samples[i] < epsilon) { samples[i] = epsilon; changed++; }
            }
            return changed;
        }

        /// <summary>
        /// Guard for the plan §7 M4 hard constraint. Returns the indices of samples that are exactly
        /// 0 but were not declared as holes — every one of them is a hole the game will honour.
        /// </summary>
        public static List<int> FindAccidentalHoles(ushort[] samples, Func<int, bool>? isIntentionalHole = null)
        {
            var hits = new List<int>();
            for (int i = 0; i < samples.Length; i++)
                if (samples[i] == HoleHeightMarkerSample && (isIntentionalHole is null || !isIntentionalHole(i)))
                    hits.Add(i);
            return hits;
        }
    }

    // =======================================================================================
    // Edge clamp
    // =======================================================================================

    [Flags]
    public enum EdgeFlags
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        All = Left | Right | Top | Bottom,
    }

    /// <summary>
    /// Border replication on quadtree-boundary nodes: the outermost <c>fixupWidth</c> rows/columns
    /// are replaced by a copy of the first interior sample, per edge, so garbage cannot bleed in from
    /// outside the world. Provenance: FIFA21_SOURCE — <c>clampToEdge&lt;T&gt;</c> (W02 §4.4).
    /// Unverified for BFII, but it is a boundary-only operation and is checkable against shipped
    /// border samples with <see cref="IsClamped"/>.
    /// </summary>
    public static class EdgeClamp
    {
        public static void Apply(ushort[] samples, int samplesPerSide, int fixupWidth, EdgeFlags edges)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            if (samples.Length != (long)samplesPerSide * samplesPerSide)
                throw new ArgumentException("sample grid is not samplesPerSide^2", nameof(samples));
            if (fixupWidth <= 0 || edges == EdgeFlags.None) return;
            if (2 * fixupWidth >= samplesPerSide)
                throw new ArgumentOutOfRangeException(nameof(fixupWidth), "clamp width leaves no interior");

            if (edges.HasFlag(EdgeFlags.Top))
                for (int y = 0; y < fixupWidth; y++)
                    Array.Copy(samples, fixupWidth * samplesPerSide, samples, y * samplesPerSide, samplesPerSide);

            if (edges.HasFlag(EdgeFlags.Bottom))
            {
                int src = (samplesPerSide - 1 - fixupWidth) * samplesPerSide;
                for (int y = samplesPerSide - fixupWidth; y < samplesPerSide; y++)
                    Array.Copy(samples, src, samples, y * samplesPerSide, samplesPerSide);
            }

            if (edges.HasFlag(EdgeFlags.Left))
                for (int y = 0; y < samplesPerSide; y++)
                {
                    ushort v = samples[y * samplesPerSide + fixupWidth];
                    for (int x = 0; x < fixupWidth; x++) samples[y * samplesPerSide + x] = v;
                }

            if (edges.HasFlag(EdgeFlags.Right))
                for (int y = 0; y < samplesPerSide; y++)
                {
                    ushort v = samples[y * samplesPerSide + samplesPerSide - 1 - fixupWidth];
                    for (int x = samplesPerSide - fixupWidth; x < samplesPerSide; x++)
                        samples[y * samplesPerSide + x] = v;
                }
        }

        /// <summary>Offline check: are the border rows/columns already edge-clamped in shipped data?</summary>
        public static bool IsClamped(ushort[] samples, int samplesPerSide, int fixupWidth, EdgeFlags edges)
        {
            var copy = (ushort[])samples.Clone();
            Apply(copy, samplesPerSide, fixupWidth, edges);
            for (int i = 0; i < samples.Length; i++) if (samples[i] != copy[i]) return false;
            return true;
        }
    }

    // =======================================================================================
    // Cross-LOD height snap
    // =======================================================================================

    /// <summary>
    /// The cross-LOD power-of-two height snap.
    /// <para>
    /// FIFA snaps lower-resolution tile samples to coarser power-of-two intervals at load time so a
    /// LOD transition lands on the same values from both sides:
    /// <c>snap = 1 &lt;&lt; (levelMax - nodeLevel); if (snap &gt; 1) sample = (sample + snap/2) &amp; -snap;</c>
    /// (W02 §4.6, <c>fixupRasterTileData</c>).
    /// </para>
    /// <para>
    /// <b>Master plan risk R-03: FIFA-only and unverified for BFII.</b> Getting it wrong produces
    /// cracks visible only at LOD transitions — the most expensive class of late discovery. It is
    /// therefore <b>off by default</b>, and <see cref="Verify"/> implements R-03's stated mitigation
    /// exactly: "for a node at level l, check whether its samples are multiples of
    /// <c>1 &lt;&lt; (levelMax - l)</c>. Purely offline, decidable from the M0 dump."
    /// </para>
    /// </summary>
    public static class CrossLodSnap
    {
        public static int SnapFor(int levelMax, int nodeLevel)
        {
            int shift = levelMax - nodeLevel;
            if (shift <= 0) return 1;
            if (shift > 16) shift = 16;
            return 1 << shift;
        }

        /// <summary>Apply the snap in place. Returns the number of samples changed.</summary>
        public static int Apply(ushort[] samples, int snap)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            if (snap <= 1) return 0;

            int changed = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                int v = (samples[i] + snap / 2) & -snap;
                if (v < 0) v = 0;
                if (v > ushort.MaxValue) v = ushort.MaxValue & -snap;
                if (v != samples[i]) { samples[i] = (ushort)v; changed++; }
            }
            return changed;
        }

        public sealed class VerifyResult
        {
            public int Snap { get; set; }
            public int SampleCount { get; set; }
            public int NonMultipleCount { get; set; }
            public int FirstNonMultipleIndex { get; set; } = -1;

            /// <summary>
            /// True when every sample is already a multiple of <see cref="Snap"/>. For a node at a
            /// level where <c>snap &gt; 1</c>, that is what the FIFA rule would have produced.
            /// </summary>
            public bool AllMultiples => NonMultipleCount == 0;

            /// <summary>
            /// A snap of 1 constrains nothing, so the test says nothing about BFII either way.
            /// </summary>
            public bool Decisive => Snap > 1 && SampleCount > 0;

            public override string ToString()
                => Decisive
                    ? $"snap={Snap}: {NonMultipleCount}/{SampleCount} sample(s) are not multiples" +
                      (AllMultiples ? " -> consistent with the FIFA snap" : " -> BFII does NOT apply this snap at this level")
                    : $"snap={Snap}: not decisive";
        }

        /// <summary>
        /// R-03's decidable offline test. Run it on a node at a level where <c>snap &gt; 1</c>; a
        /// single non-multiple sample falsifies the rule for BFII at that level.
        /// </summary>
        public static VerifyResult Verify(ushort[] samples, int levelMax, int nodeLevel)
        {
            int snap = SnapFor(levelMax, nodeLevel);
            var r = new VerifyResult { Snap = snap, SampleCount = samples.Length };
            if (snap <= 1) return r;

            for (int i = 0; i < samples.Length; i++)
            {
                if (samples[i] % snap == 0) continue;
                if (r.FirstNonMultipleIndex < 0) r.FirstNonMultipleIndex = i;
                r.NonMultipleCount++;
            }
            return r;
        }
    }
}
