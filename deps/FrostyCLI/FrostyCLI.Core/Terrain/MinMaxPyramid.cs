#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainDerive\MinMaxPyramid.cs (sha256:E5927A95810DC3A8).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainDerive
{
    /// <summary>
    /// Order in which the min/max levels are laid out in the stack.
    /// <para>
    /// FIFA's <c>writeNodeData</c> emits the pyramid in <b>reverse</b> level order, with an in-source
    /// rationale of "for runtime cache efficiency" (W02 §4.6/§4.7). That is <b>FIFA21_SOURCE and
    /// UNVERIFIED for BFII</b> — master plan risk <b>R-04</b>: "a writer that emits ascending order
    /// produces a silently wrong broadphase". Use <see cref="MinMaxPyramid.DetectLayout"/> against a
    /// shipped node to decide it from bytes rather than from belief.
    /// </para>
    /// </summary>
    public enum MinMaxWriteOrder
    {
        /// <summary>Coarsest level first (FIFA's documented order). FIFA21_SOURCE, unverified for BFII.</summary>
        ReverseLevel = 0,

        /// <summary>Finest level (level 0) first.</summary>
        AscendingLevel = 1,
    }

    /// <summary>Order of the two u16 in a min/max cell. UNKNOWN for BFII; detected, never assumed.</summary>
    public enum MinMaxPairOrder
    {
        MinFirst = 0,
        MaxFirst = 1,
    }

    /// <summary>
    /// Whether a level-0 min/max cell covers 16 samples per side or 17 (including the sample shared
    /// with the neighbouring cell). UNKNOWN for BFII; detected, never assumed.
    /// </summary>
    public enum MinMaxCellSpan
    {
        /// <summary>16 samples per side, non-overlapping cells.</summary>
        Exclusive = 0,

        /// <summary>17 samples per side, cells share their boundary sample.</summary>
        InclusiveSharedEdge = 1,
    }

    public sealed class MinMaxLayout
    {
        public MinMaxWriteOrder WriteOrder { get; set; } = MinMaxWriteOrder.ReverseLevel;
        public MinMaxPairOrder PairOrder { get; set; } = MinMaxPairOrder.MinFirst;
        public MinMaxCellSpan CellSpan { get; set; } = MinMaxCellSpan.Exclusive;

        public override string ToString() => $"{WriteOrder}/{PairOrder}/{CellSpan}";

        public static IEnumerable<MinMaxLayout> AllVariants()
        {
            foreach (MinMaxWriteOrder w in new[] { MinMaxWriteOrder.ReverseLevel, MinMaxWriteOrder.AscendingLevel })
            foreach (MinMaxPairOrder p in new[] { MinMaxPairOrder.MinFirst, MinMaxPairOrder.MaxFirst })
            foreach (MinMaxCellSpan c in new[] { MinMaxCellSpan.Exclusive, MinMaxCellSpan.InclusiveSharedEdge })
                yield return new MinMaxLayout { WriteOrder = w, PairOrder = p, CellSpan = c };
        }
    }

    /// <summary>
    /// The min/max acceleration pyramid over a node's height samples.
    /// <para>
    /// This is the structure the Havok broadphase reads through <c>getCoarseMinMax</c>. Master plan
    /// §7 M4 gate 6: a stale min/max typically survives short-range contact tests and fails
    /// long-range ray and sweep queries — terrain that looks right up close and that long rays miss.
    /// A height edit that does not rebuild this, and every ancestor's copy of it, is wrong even
    /// though it renders correctly (plan §4, edge D5 -&gt; D6).
    /// </para>
    /// <para>
    /// Provenance: the <b>size</b> formula is BFII_EXACT (W01 §4.4.1, two readers agree). The
    /// <b>cell size of 16 samples</b> and the <b>reverse write order</b> are FIFA21_SOURCE
    /// (W02 §4.6/§4.7) and unverified for BFII. Nothing here bakes an unverified rule in.
    /// </para>
    /// </summary>
    public static class MinMaxPyramid
    {
        /// <summary>Total u16 count for a pyramid of <paramref name="levelCount"/> levels.</summary>
        public static long StackSize(int levelCount) => HeightfieldSizing.MinMaxStackSize(levelCount);

        /// <summary>
        /// Build the pyramid from a node's height samples.
        /// </summary>
        /// <param name="samples">Row-major u16 grid, <c>samplesPerSide^2</c> entries.</param>
        /// <param name="samplesPerSide">Grid side including borders.</param>
        /// <param name="borderWidth">Border sample count on each side.</param>
        /// <param name="levelCount">Level count, equal to the header's <c>minMaxStackDepth</c>.</param>
        /// <param name="layout">Layout variant. Do not guess — detect it with <see cref="DetectLayout"/>.</param>
        public static ushort[] Build(ushort[] samples, int samplesPerSide, int borderWidth,
            int levelCount, MinMaxLayout layout)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            if (layout is null) throw new ArgumentNullException(nameof(layout));
            if (samplesPerSide <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerSide));
            if (samples.Length != (long)samplesPerSide * samplesPerSide)
                throw new ArgumentException(
                    $"expected {(long)samplesPerSide * samplesPerSide} sample(s), got {samples.Length}",
                    nameof(samples));
            if (levelCount <= 0) return Array.Empty<ushort>();

            int cells0 = HeightfieldSizing.MinMaxCellsPerSide(levelCount, 0);
            int span = layout.CellSpan == MinMaxCellSpan.Exclusive
                ? HeightfieldSizing.SamplesPerMinMaxCellSide
                : HeightfieldSizing.SamplesPerMinMaxCellSide + 1;

            // levels[0] is the finest. Each entry is a (min,max) pair per cell, in row-major order.
            var levels = new (ushort Min, ushort Max)[levelCount][];
            levels[0] = new (ushort, ushort)[(long)cells0 * cells0];

            for (int cy = 0; cy < cells0; cy++)
            {
                for (int cx = 0; cx < cells0; cx++)
                {
                    ushort min = ushort.MaxValue, max = ushort.MinValue;
                    for (int sy = 0; sy < span; sy++)
                    {
                        int y = borderWidth + cy * HeightfieldSizing.SamplesPerMinMaxCellSide + sy;
                        if (y < 0 || y >= samplesPerSide) continue;
                        int row = y * samplesPerSide;
                        for (int sx = 0; sx < span; sx++)
                        {
                            int x = borderWidth + cx * HeightfieldSizing.SamplesPerMinMaxCellSide + sx;
                            if (x < 0 || x >= samplesPerSide) continue;
                            ushort v = samples[row + x];
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                    }

                    if (min > max) { min = 0; max = 0; } // cell entirely outside the grid
                    levels[0][cy * cells0 + cx] = (min, max);
                }
            }

            for (int l = 1; l < levelCount; l++)
            {
                int cells = HeightfieldSizing.MinMaxCellsPerSide(levelCount, l);
                int prev = HeightfieldSizing.MinMaxCellsPerSide(levelCount, l - 1);
                levels[l] = new (ushort, ushort)[(long)cells * cells];

                for (int cy = 0; cy < cells; cy++)
                {
                    for (int cx = 0; cx < cells; cx++)
                    {
                        ushort min = ushort.MaxValue, max = ushort.MinValue;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            int py = cy * 2 + dy;
                            if (py >= prev) continue;
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int px = cx * 2 + dx;
                                if (px >= prev) continue;
                                var c = levels[l - 1][py * prev + px];
                                if (c.Min < min) min = c.Min;
                                if (c.Max > max) max = c.Max;
                            }
                        }

                        if (min > max) { min = 0; max = 0; }
                        levels[l][cy * cells + cx] = (min, max);
                    }
                }
            }

            var outBuf = new ushort[StackSize(levelCount)];
            int w = 0;

            void EmitLevel(int l)
            {
                foreach (var (mn, mx) in levels[l])
                {
                    if (layout.PairOrder == MinMaxPairOrder.MinFirst) { outBuf[w++] = mn; outBuf[w++] = mx; }
                    else { outBuf[w++] = mx; outBuf[w++] = mn; }
                }
            }

            if (layout.WriteOrder == MinMaxWriteOrder.ReverseLevel)
                for (int l = levelCount - 1; l >= 0; l--) EmitLevel(l);
            else
                for (int l = 0; l < levelCount; l++) EmitLevel(l);

            return outBuf;
        }

        public sealed class LayoutDetection
        {
            public MinMaxLayout Layout { get; set; } = new MinMaxLayout();
            public bool Matches { get; set; }
            public int DifferingEntries { get; set; }
            public int FirstDifferenceIndex { get; set; } = -1;

            public override string ToString()
                => $"{Layout}: {(Matches ? "MATCH" : $"{DifferingEntries} differing u16, first at {FirstDifferenceIndex}")}";
        }

        public sealed class DetectionResult
        {
            public List<LayoutDetection> Candidates { get; } = new List<LayoutDetection>();
            public MinMaxLayout? UniqueMatch { get; set; }
            public int MatchCount { get; set; }

            /// <summary>
            /// A detection is only load-bearing when exactly one variant reproduces the shipped bytes.
            /// Zero matches means the FIFA cell rule does not describe BFII. More than one means the
            /// sample is degenerate (typically a flat node where min == max everywhere) and a
            /// different node must be used.
            /// </summary>
            public Provenance Grade =>
                MatchCount == 1 ? Provenance.BfiiExact
                : MatchCount == 0 ? Provenance.Unknown
                : Provenance.Inferred;
        }

        /// <summary>
        /// Decide the min/max layout from shipped bytes. This is the concrete experiment master plan
        /// risk <b>R-04</b> asks for: "reconstruct the pyramid from the atlas samples and check which
        /// ordering reproduces the shipped bytes". It runs entirely offline against one node of one
        /// M0 dump.
        /// </summary>
        /// <param name="samples">The node's atlas samples.</param>
        /// <param name="samplesPerSide">Grid side including borders.</param>
        /// <param name="borderWidth">Border sample count on each side.</param>
        /// <param name="levelCount">Header <c>minMaxStackDepth</c>.</param>
        /// <param name="shippedMinMax">The node's shipped <c>minMaxStackData</c>, as u16.</param>
        public static DetectionResult DetectLayout(ushort[] samples, int samplesPerSide,
            int borderWidth, int levelCount, ushort[] shippedMinMax)
        {
            var result = new DetectionResult();

            foreach (var layout in MinMaxLayout.AllVariants())
            {
                var det = new LayoutDetection { Layout = layout };
                ushort[] built;
                try
                {
                    built = Build(samples, samplesPerSide, borderWidth, levelCount, layout);
                }
                catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
                {
                    det.Matches = false;
                    det.DifferingEntries = int.MaxValue;
                    result.Candidates.Add(det);
                    continue;
                }

                if (built.Length != shippedMinMax.Length)
                {
                    det.Matches = false;
                    det.DifferingEntries = Math.Abs(built.Length - shippedMinMax.Length);
                    det.FirstDifferenceIndex = Math.Min(built.Length, shippedMinMax.Length);
                    result.Candidates.Add(det);
                    continue;
                }

                int diff = 0, first = -1;
                for (int i = 0; i < built.Length; i++)
                {
                    if (built[i] == shippedMinMax[i]) continue;
                    if (first < 0) first = i;
                    diff++;
                }

                det.Matches = diff == 0;
                det.DifferingEntries = diff;
                det.FirstDifferenceIndex = first;
                result.Candidates.Add(det);
            }

            foreach (var c in result.Candidates)
                if (c.Matches) { result.MatchCount++; result.UniqueMatch ??= c.Layout; }

            if (result.MatchCount != 1) result.UniqueMatch = null;
            return result;
        }
    }
}
