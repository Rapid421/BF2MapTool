#nullable enable
// VENDORED from BfiiMapTools src\Bfii.TerrainFormat\MaskStride.cs (sha256:192D97FC885AB021).
// Re-sync with tools\sync-terrain-format.ps1; do not edit here.
using System;
using System.Collections.Generic;

namespace Bfii.TerrainFormat
{
    /// <summary>
    /// The mask subtile coverage-grid stride.
    /// <para>
    /// This is the one field of the subtile record that the format does not state, and it is the
    /// single defect that produced the worst known BFII terrain bug: the upstream community reader
    /// gates <c>0xAB</c> on DataVersion 20151117 only, so BFII (20171117) falls through to the
    /// default 11 and 1,048 of 1,064 subtiles are silently lost (W01 §4.4.2, evidence row 57).
    /// </para>
    /// <para>
    /// It is <b>not</b> a constant. The local BFII-corrected fork that measured <c>0xAB</c> on one
    /// level states in its own source that the stride "evidently is not constant across levels" and
    /// ships a 256-candidate brute-force calibrator (W01 §8.1 B2, evidence row 31).
    /// </para>
    /// <para>
    /// Master plan §6.3 / §10 C-02 supplies the generating formula, so the search space is eight
    /// candidates rather than 256:
    /// <c>stride(L) = ceil( ((4^L - 1) / 3) / 8 )</c>, where <c>L</c> is the
    /// <c>HighResolutionCoverageGridStackLevelCount</c>. The subtile record is
    /// <c>19 + stride(L)</c> bytes. <c>L = 6</c> yields 171 = <c>0xAB</c>, exactly the value verified
    /// by byte accounting on BFII; <c>L = 4</c> yields 11, exactly the wrong upstream default.
    /// </para>
    /// <para>
    /// Provenance: the formula is FIFA21_SOURCE (W06 §3.4 / plan §5.1, <c>TerrainMaskTreeNode.h</c>);
    /// the BFII value 171 is BFII_EXACT on one level (W01 §4.4.2). This class therefore <b>fits</b>
    /// L per terrain and records it. Never hard-code a byte.
    /// </para>
    /// </summary>
    public static class MaskStride
    {
        /// <summary>Fixed part of a subtile record: u8 layerIndex + AABB2 (16 B) + u8 isLeaf + u8 flags.</summary>
        public const int SubtileFixedBytes = 19;

        public const int MinLevel = 1;
        public const int MaxLevel = 8;

        /// <summary>
        /// The value the BFII fork measured by exact byte accounting on one level
        /// (4,080,313 B / 45 nodes / 1,064 subtiles). Used only as the first candidate tried,
        /// never as a default.
        /// </summary>
        public const int BfiiObservedStride = 0xAB;

        /// <summary>Level whose stride equals <see cref="BfiiObservedStride"/>.</summary>
        public const int BfiiExpectedLevel = 6;

        /// <summary>
        /// <c>stride(L) = ceil( ((4^L - 1) / 3) / 8 )</c> — the number of bytes needed to hold the
        /// packed coverage-grid bit stack of an <c>L</c>-level quadtree stack.
        /// </summary>
        public static int ForLevel(int level)
        {
            if (level < MinLevel || level > MaxLevel)
                throw new ArgumentOutOfRangeException(nameof(level), level,
                    $"coverage-grid stack level count must be in [{MinLevel},{MaxLevel}]");

            // (4^L - 1) / 3 is exact in integer arithmetic: 4^L - 1 is always divisible by 3.
            long bits = ((1L << (2 * level)) - 1) / 3;
            return (int)((bits + 7) / 8);
        }

        public static int SubtileRecordBytes(int level) => SubtileFixedBytes + ForLevel(level);

        /// <summary>
        /// The eight candidate levels, ordered so the BFII-expected level is tried first.
        /// Ordering only affects how quickly a fit is found; acceptance is unconditional on order
        /// because every candidate must satisfy all four acceptance conditions exactly.
        /// </summary>
        public static IReadOnlyList<int> CandidateLevels { get; } = BuildCandidates();

        private static int[] BuildCandidates()
        {
            var list = new List<int> { BfiiExpectedLevel };
            for (int l = MinLevel; l <= MaxLevel; l++)
                if (l != BfiiExpectedLevel)
                    list.Add(l);
            return list.ToArray();
        }

        /// <summary>Inverse lookup: the level whose stride equals <paramref name="stride"/>, or null.</summary>
        public static int? LevelForStride(int stride)
        {
            for (int l = MinLevel; l <= MaxLevel; l++)
                if (ForLevel(l) == stride) return l;
            return null;
        }
    }
}
