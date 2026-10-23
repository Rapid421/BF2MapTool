#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainDerive\Sizing.cs (sha256:EEE2EE5D09C91F65).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainDerive
{
    /// <summary>
    /// How confident we are that a rule reproduces BFII's shipped bytes.
    /// Carried on every derivation result so a caller can never mistake a FIFA-shaped guess for a
    /// measured BFII fact.
    /// </summary>
    public enum Provenance
    {
        /// <summary>Observed in shipped BFII data or computed identically by two BFII readers.</summary>
        BfiiExact,

        /// <summary>Described in FIFA 21 / Frostbite 2019 material. A lead, never a BFII fact.</summary>
        Fifa21Source,

        /// <summary>Reasoned from other facts in this workspace.</summary>
        Inferred,

        /// <summary>Not established. A result carrying this must not be shipped into game data.</summary>
        Unknown,
    }

    /// <summary>
    /// Sizes derived from a heightfield tree header.
    /// <para>
    /// Two independent size formulas exist and agree, so these are BFII_EXACT (W01 §4.4.1):
    /// <c>minMaxStackSize = SUM_(i&lt;depth) (minMaxWidth &gt;&gt; i)^2 * 2</c> with
    /// <c>minMaxWidth = 1 &lt;&lt; (depth-1)</c>, and
    /// <c>occluderGridStackSize = SUM_(i&lt;depth) ((occW &gt;&gt; i) + 1)^2</c> with
    /// <c>occW = 1 &lt;&lt; (depth-1)</c>.
    /// </para>
    /// <para>
    /// The FIFA-described <i>generating</i> rules (W02 §4.6) are separate and are the thing this
    /// class cross-checks: <c>samplesPerSidePot = tileSamplesPerSide - 2*borderWidth - 1</c>,
    /// <c>minMaxLevelZeroSize = samplesPerSidePot / 16</c>,
    /// <c>minMaxLevelCount = log2(minMaxLevelZeroSize) + 1</c>,
    /// <c>occluderGridLevelCount = log2(samplesPerSidePot / 8) + 1</c>.
    /// If a shipped BFII header disagrees with those, the FIFA rule does not hold for BFII and the
    /// disagreement is the finding.
    /// </para>
    /// </summary>
    public static class HeightfieldSizing
    {
        /// <summary>Fixed in FIFA: 16 heightfield samples per min/max cell side. FIFA21_SOURCE.</summary>
        public const int SamplesPerMinMaxCellSide = 16;

        /// <summary>Fixed in FIFA: 8 heightfield samples per occluder-grid cell side. FIFA21_SOURCE.</summary>
        public const int SamplesPerOccluderCellSide = 8;

        /// <summary>Density map resolution ratio. FIFA21_SOURCE (W02 §4.6).</summary>
        public const double DensityMapResolutionRatio = 0.25;

        /// <summary>Density map node border width. FIFA21_SOURCE (W02 §4.6).</summary>
        public const int DensityMapNodeBorderWidth = 1;

        /// <summary>
        /// <c>samplesPerSidePot = tileSamplesPerSide - 2*borderWidth - 1</c>.
        /// The trailing <c>-1</c> is the shared edge sample of a top-left-sampled tile. FIFA21_SOURCE.
        /// </summary>
        public static int SamplesPerSidePot(int tileSamplesPerSide, int borderWidth)
            => tileSamplesPerSide - 2 * borderWidth - 1;

        public static int IntLog2(int value)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), value, "must be positive");
            int r = 0;
            while ((1 << (r + 1)) <= value) r++;
            return r;
        }

        public static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

        /// <summary>Number of min/max cells per side at level 0. FIFA21_SOURCE.</summary>
        public static int MinMaxLevelZeroSize(int samplesPerSidePot)
            => samplesPerSidePot / SamplesPerMinMaxCellSide;

        /// <summary>FIFA21_SOURCE.</summary>
        public static int MinMaxLevelCount(int samplesPerSidePot)
            => IntLog2(MinMaxLevelZeroSize(samplesPerSidePot)) + 1;

        /// <summary>FIFA21_SOURCE.</summary>
        public static int OccluderGridLevelCount(int samplesPerSidePot)
            => IntLog2(samplesPerSidePot / SamplesPerOccluderCellSide) + 1;

        /// <summary>
        /// Count of u16 elements in the min/max stack. BFII_EXACT — both BFII readers compute this.
        /// The factor 2 is the (min,max) pair per cell.
        /// </summary>
        public static long MinMaxStackSize(int levelCount)
        {
            if (levelCount <= 0) return 0;
            long width = 1L << (levelCount - 1);
            long total = 0;
            for (int i = 0; i < levelCount; i++)
            {
                long b = width >> i;
                total += 2 * b * b;
            }
            return total;
        }

        /// <summary>Count of u16 elements in the occluder grid stack. BFII_EXACT.</summary>
        public static long OccluderGridStackSize(int levelCount)
        {
            if (levelCount <= 0) return 0;
            long width = 1L << (levelCount - 1);
            long total = 0;
            for (int i = 0; i < levelCount; i++)
            {
                long b = width >> i;
                total += (b + 1) * (b + 1);
            }
            return total;
        }

        /// <summary>
        /// Cells per side at min/max level <paramref name="level"/>, where level 0 is the finest.
        /// </summary>
        public static int MinMaxCellsPerSide(int levelCount, int level)
            => (1 << (levelCount - 1)) >> level;

        /// <summary>
        /// Grid points per side of occluder level <paramref name="level"/>.
        /// FIFA writes level <c>i</c> as <c>((1 &lt;&lt; i) + 1)^2</c> u16 (W02 §4.6), i.e. level 0 is
        /// the <i>coarsest</i>. W01's size formula sums the same multiset in the opposite order, so
        /// total size agrees and only the ordering is at stake — see <see cref="OccluderGridOrder"/>.
        /// </summary>
        public static int OccluderPointsPerSide(int level) => (1 << level) + 1;

        public static int DensityMapNodeSamplesPerSidePot(int targetSamplesPerSidePot)
            => (int)(targetSamplesPerSidePot * DensityMapResolutionRatio);

        public static int DensityMapNodeSamplesPerSide(int targetSamplesPerSidePot)
            => DensityMapNodeSamplesPerSidePot(targetSamplesPerSidePot) + 2 * DensityMapNodeBorderWidth + 1;

        /// <summary>One line of a cross-check between a shipped header and the FIFA generating rules.</summary>
        public sealed class CrossCheckLine
        {
            public string Quantity { get; set; } = "";
            public long Declared { get; set; }
            public long FifaRulePredicts { get; set; }
            public bool Agrees => Declared == FifaRulePredicts;
            public string Note { get; set; } = "";

            public override string ToString()
                => $"{Quantity}: declared {Declared}, FIFA rule predicts {FifaRulePredicts}" +
                   (Agrees ? " (agrees)" : " (DISAGREES)") + (Note.Length > 0 ? " - " + Note : "");
        }

        /// <summary>
        /// Compare a shipped BFII heightfield header against the FIFA generating rules.
        /// <para>
        /// This is the cheapest available test of whether the FIFA-derived generation rules apply to
        /// BFII at all. A disagreement is a finding, not a bug in this code: it means the FIFA rule
        /// does not carry over and the derived arrays must be re-derived from BFII data.
        /// Master plan §5.1 requires exactly this kind of BFII validation column.
        /// </para>
        /// </summary>
        public static List<CrossCheckLine> CrossCheckAgainstFifaRules(
            int nodeSamplesPerSide, int nodeBorderWidth,
            long declaredMinMaxStackDepth, long declaredOccluderGridStackDepth,
            long declaredDensityMapNodeSamplesPerSide, long declaredDensityMapNodeSamplesPerSidePot,
            long declaredDensityMapBorderWidth)
        {
            var lines = new List<CrossCheckLine>();
            int pot = SamplesPerSidePot(nodeSamplesPerSide, nodeBorderWidth);

            lines.Add(new CrossCheckLine
            {
                Quantity = "samplesPerSidePot",
                Declared = pot,
                FifaRulePredicts = pot,
                Note = $"derived from nodeSamplesPerSide={nodeSamplesPerSide}, borderWidth={nodeBorderWidth}",
            });

            if (pot >= SamplesPerMinMaxCellSide && MinMaxLevelZeroSize(pot) > 0)
            {
                lines.Add(new CrossCheckLine
                {
                    Quantity = "minMaxStackDepth",
                    Declared = declaredMinMaxStackDepth,
                    FifaRulePredicts = MinMaxLevelCount(pot),
                    Note = $"levelZeroSize={MinMaxLevelZeroSize(pot)} = pot/16",
                });
            }
            else
            {
                lines.Add(new CrossCheckLine
                {
                    Quantity = "minMaxStackDepth",
                    Declared = declaredMinMaxStackDepth,
                    FifaRulePredicts = -1,
                    Note = $"pot={pot} is below the 16-sample min/max cell; FIFA rule does not apply",
                });
            }

            if (pot >= SamplesPerOccluderCellSide)
            {
                lines.Add(new CrossCheckLine
                {
                    Quantity = "occluderGridStackDepth",
                    Declared = declaredOccluderGridStackDepth,
                    FifaRulePredicts = OccluderGridLevelCount(pot),
                    Note = "log2(pot/8) + 1",
                });
            }

            if (pot > 0)
            {
                lines.Add(new CrossCheckLine
                {
                    Quantity = "densityMapNodeSamplesPerSidePot",
                    Declared = declaredDensityMapNodeSamplesPerSidePot,
                    FifaRulePredicts = DensityMapNodeSamplesPerSidePot(pot),
                    Note = "pot * 0.25",
                });
                lines.Add(new CrossCheckLine
                {
                    Quantity = "densityMapNodeSamplesPerSide",
                    Declared = declaredDensityMapNodeSamplesPerSide,
                    FifaRulePredicts = DensityMapNodeSamplesPerSide(pot),
                    Note = "pot*0.25 + 2*1 + 1",
                });
                lines.Add(new CrossCheckLine
                {
                    Quantity = "densityMapBorderWidth",
                    Declared = declaredDensityMapBorderWidth,
                    FifaRulePredicts = DensityMapNodeBorderWidth,
                    Note = "fixed 1",
                });
            }

            return lines;
        }
    }

    /// <summary>Utility conversions between a u16 sample array and its raw little-endian bytes.</summary>
    public static class SampleBytes
    {
        public static ushort[] ToSamples(byte[] raw)
        {
            if (raw is null) throw new ArgumentNullException(nameof(raw));
            if ((raw.Length & 1) != 0)
                throw new ArgumentException("u16 block length must be even", nameof(raw));
            var s = new ushort[raw.Length / 2];
            for (int i = 0; i < s.Length; i++) s[i] = (ushort)(raw[2 * i] | (raw[2 * i + 1] << 8));
            return s;
        }

        public static byte[] ToBytes(ushort[] samples)
        {
            if (samples is null) throw new ArgumentNullException(nameof(samples));
            var b = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                b[2 * i] = (byte)(samples[i] & 0xFF);
                b[2 * i + 1] = (byte)(samples[i] >> 8);
            }
            return b;
        }
    }
}
