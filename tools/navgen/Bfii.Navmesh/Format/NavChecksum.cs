// VENDORED from BfiiMapTools src\Bfii.Navmesh\Format\NavChecksum.cs (sha256:2B639DC68C2DB66E).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using Bfii.Navmesh.Io;

namespace Bfii.Navmesh.Format
{
    /// <summary>
    /// The image checksum: a wrapping 32-bit sum of the payload read as 32-bit words.
    /// </summary>
    /// <remarks>
    /// Scope: everything after the 24-byte resource header, which is the section header plus the
    /// whole navigation set. A trailing partial word is excluded, and an empty payload sums to 0.
    /// COMMUNITY_TESTED (FORMAT-PROVENANCE.md, row H3).
    ///
    /// UNKNOWN, and deliberately not guessed: which byte order the words are gathered in. The
    /// community implementation gathers big-endian words unconditionally, including for images
    /// whose own flag says little endian. That may be correct, or it may be an artefact of every
    /// blob it was tested against sharing one order. Rather than pick, this type computes both and
    /// <see cref="Calibrate"/> reports which one reproduces a stored value. Running the CLI
    /// inspect command on one real donor blob settles it permanently; until then the library
    /// defaults to <see cref="WordOrder.Big"/> because that is the variant with a demonstrated
    /// in-game round trip behind it.
    /// </remarks>
    public static class NavChecksum
    {
        /// <summary>Byte order in which the payload is gathered into 32-bit words.</summary>
        public enum WordOrder
        {
            Big = 0,
            Little = 1,
        }

        /// <summary>
        /// The order used when writing unless a caller overrides it. Big, because that is the
        /// variant behind the only known working in-game round trip.
        /// </summary>
        public const WordOrder DefaultWordOrder = WordOrder.Big;

        public static uint Compute(byte[] data, int offset, int count, WordOrder order)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count, "checksum range lies outside the buffer");
            }

            int words = count / 4;
            uint sum = 0;
            int p = offset;

            if (order == WordOrder.Big)
            {
                for (int i = 0; i < words; i++, p += 4)
                {
                    unchecked
                    {
                        sum += ((uint)data[p] << 24) | ((uint)data[p + 1] << 16) |
                               ((uint)data[p + 2] << 8) | data[p + 3];
                    }
                }
            }
            else
            {
                for (int i = 0; i < words; i++, p += 4)
                {
                    unchecked
                    {
                        sum += ((uint)data[p + 3] << 24) | ((uint)data[p + 2] << 16) |
                               ((uint)data[p + 1] << 8) | data[p];
                    }
                }
            }

            return sum;
        }

        public static uint Compute(byte[] data, int offset, int count, ByteOrder order) =>
            Compute(data, offset, count, order == ByteOrder.Big ? WordOrder.Big : WordOrder.Little);

        /// <summary>
        /// Computes both candidate reductions over a payload and reports which reproduces
        /// <paramref name="storedChecksum"/>. This is the calibrator that converts the open
        /// question in the remarks into an observation the first time a real blob is available.
        /// </summary>
        public static ChecksumCalibration Calibrate(byte[] data, int offset, int count, uint storedChecksum)
        {
            uint big = Compute(data, offset, count, WordOrder.Big);
            uint little = Compute(data, offset, count, WordOrder.Little);
            return new ChecksumCalibration(storedChecksum, big, little);
        }
    }

    /// <summary>Result of running both checksum reductions against a stored value.</summary>
    public sealed class ChecksumCalibration
    {
        public ChecksumCalibration(uint stored, uint bigWordSum, uint littleWordSum)
        {
            Stored = stored;
            BigWordSum = bigWordSum;
            LittleWordSum = littleWordSum;
        }

        public uint Stored { get; }

        public uint BigWordSum { get; }

        public uint LittleWordSum { get; }

        public bool BigMatches => BigWordSum == Stored;

        public bool LittleMatches => LittleWordSum == Stored;

        /// <summary>True when exactly one candidate matches, so the question is settled.</summary>
        public bool IsDecisive => BigMatches ^ LittleMatches;

        public NavChecksum.WordOrder? DecidedOrder =>
            !IsDecisive ? (NavChecksum.WordOrder?)null
                : BigMatches ? NavChecksum.WordOrder.Big : NavChecksum.WordOrder.Little;

        public override string ToString()
        {
            string verdict;
            if (IsDecisive)
            {
                verdict = "DECISIVE: word order is " + DecidedOrder;
            }
            else if (BigMatches && LittleMatches)
            {
                verdict = "AMBIGUOUS: both reductions agree (payload is word-order symmetric)";
            }
            else
            {
                verdict = "NEITHER matches: the reduction is not a wrapping 32-bit word sum";
            }

            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "stored=0x{0:X8} bigWordSum=0x{1:X8} littleWordSum=0x{2:X8} -> {3}",
                Stored, BigWordSum, LittleWordSum, verdict);
        }
    }
}
