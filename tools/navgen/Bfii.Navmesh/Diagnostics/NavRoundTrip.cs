// VENDORED from BfiiMapTools src\Bfii.Navmesh\Diagnostics\NavRoundTrip.cs (sha256:27287DA4075C43D3).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Io;

namespace Bfii.Navmesh.Diagnostics
{
    /// <summary>Result of parsing a payload and serializing it straight back.</summary>
    public sealed class NavRoundTripResult
    {
        public NavRoundTripResult(
            int originalLength,
            int rewrittenLength,
            int firstDifference,
            IReadOnlyList<int> differingOffsets,
            ChecksumCalibration? checksum,
            NavMeshImage image)
        {
            OriginalLength = originalLength;
            RewrittenLength = rewrittenLength;
            FirstDifference = firstDifference;
            DifferingOffsets = differingOffsets;
            Checksum = checksum;
            Image = image;
        }

        public int OriginalLength { get; }

        public int RewrittenLength { get; }

        /// <summary>First differing byte offset, or -1 when the two buffers are identical.</summary>
        public int FirstDifference { get; }

        public IReadOnlyList<int> DifferingOffsets { get; }

        /// <summary>Which checksum reduction reproduced the stored value, if any did.</summary>
        public ChecksumCalibration? Checksum { get; }

        public NavMeshImage Image { get; }

        public bool IsByteExact => FirstDifference < 0 && OriginalLength == RewrittenLength;

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format(
                CultureInfo.InvariantCulture,
                "{0} bytes in, {1} bytes out: {2}",
                OriginalLength,
                RewrittenLength,
                IsByteExact ? "BYTE EXACT" : "DIFFERS"));

            if (!IsByteExact)
            {
                sb.Append(string.Format(
                    CultureInfo.InvariantCulture,
                    "; first difference at offset {0} ({1} differing byte(s) sampled)",
                    FirstDifference,
                    DifferingOffsets.Count));
            }

            if (Checksum != null)
            {
                sb.Append("; checksum ");
                sb.Append(Checksum.ToString());
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// The round-trip gate: parse a payload, write it back, and compare byte for byte.
    /// </summary>
    /// <remarks>
    /// Two modes, and the difference matters.
    ///
    /// Preserving mode re-emits any structure that was not modified from the exact bytes it was
    /// read from. On an untouched donor that makes the result byte exact by construction, which is
    /// the property an editing tool needs: whatever is not deliberately changed is not changed.
    ///
    /// Reserialize mode rebuilds every structure from the parsed model, including the KD tree,
    /// and is the real proof that the reader and the writer are inverses. A difference here is a
    /// finding, not a failure of the tool: it means the donor lays a structure out differently
    /// from the way this library emits it, and the reported offset says where.
    /// </remarks>
    public static class NavRoundTrip
    {
        public static NavRoundTripResult Run(byte[] payload, bool forceReserialize = false, int maxDifferences = 64)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            NavMeshImage image = NavMeshImage.Read(payload);

            NavChecksum.WordOrder wordOrder =
                image.ChecksumCalibration != null && image.ChecksumCalibration.DecidedOrder.HasValue
                    ? image.ChecksumCalibration.DecidedOrder.Value
                    : NavChecksum.DefaultWordOrder;

            if (forceReserialize)
            {
                for (int i = 0; i < image.NavSet.Graphs.Count; i++)
                {
                    NavGraph graph = image.NavSet.Graphs[i];

                    // Drop the preserved KD image so the writer has to rebuild the region from the
                    // parsed node tree. The tree itself is not rebuilt: that would be a different
                    // test, because a different tree is legitimately a different byte sequence.
                    graph.KdTree.SourceImage = null;
                }
            }

            byte[] rewritten = image.Write(wordOrder);

            int first = BlobWriter.FirstDifference(payload, rewritten);
            IReadOnlyList<int> offsets = first < 0
                ? Array.Empty<int>()
                : BlobWriter.Differences(payload, rewritten, maxDifferences);

            return new NavRoundTripResult(
                payload.Length, rewritten.Length, first, offsets, image.ChecksumCalibration, image);
        }
    }
}
