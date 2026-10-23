// VENDORED from BfiiMapTools tests\Bfii.Navmesh.Tests\ChecksumTests.cs (sha256:A3022B5C2709A486).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using Bfii.Navmesh.Diagnostics;
using Bfii.Navmesh.Format;
using Bfii.Navmesh.Io;
using Xunit;

namespace Bfii.Navmesh.Tests
{
    /// <summary>
    /// The image checksum, and the calibrator that settles which word order it is gathered in.
    /// </summary>
    public sealed class ChecksumTests
    {
        [Fact]
        public void EmptyPayloadSumsToZero()
        {
            Assert.Equal(0u, NavChecksum.Compute(new byte[0], 0, 0, NavChecksum.WordOrder.Big));
        }

        [Fact]
        public void TrailingPartialWordIsExcluded()
        {
            byte[] data = { 0x00, 0x00, 0x00, 0x01, 0xFF, 0xFF };

            Assert.Equal(1u, NavChecksum.Compute(data, 0, data.Length, NavChecksum.WordOrder.Big));
        }

        [Fact]
        public void BigAndLittleReductionsDifferOnAsymmetricData()
        {
            byte[] data = { 0x01, 0x02, 0x03, 0x04 };

            Assert.Equal(0x01020304u, NavChecksum.Compute(data, 0, 4, NavChecksum.WordOrder.Big));
            Assert.Equal(0x04030201u, NavChecksum.Compute(data, 0, 4, NavChecksum.WordOrder.Little));
        }

        [Fact]
        public void SumWrapsRatherThanThrowing()
        {
            byte[] data = { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x02 };

            Assert.Equal(1u, NavChecksum.Compute(data, 0, data.Length, NavChecksum.WordOrder.Big));
        }

        [Fact]
        public void WrittenImageCarriesAChecksumOverEverythingAfterTheResourceHeader()
        {
            NavMeshImage image = SyntheticImage.Grid(3, 3);
            byte[] payload = image.Write(NavChecksum.WordOrder.Big);

            NavMeshImage parsed = NavMeshImage.Read(payload);

            uint expected = NavChecksum.Compute(
                payload,
                NavMeshImage.ResourceHeaderSize,
                payload.Length - NavMeshImage.ResourceHeaderSize,
                NavChecksum.WordOrder.Big);

            Assert.Equal(expected, parsed.SourceChecksum);
        }

        [Fact]
        public void CalibrationIdentifiesTheWordOrderTheImageWasWrittenWith()
        {
            NavMeshImage image = SyntheticImage.Grid(4, 3);

            NavMeshImage big = NavMeshImage.Read(image.Write(NavChecksum.WordOrder.Big));
            NavMeshImage little = NavMeshImage.Read(image.Write(NavChecksum.WordOrder.Little));

            Assert.NotNull(big.ChecksumCalibration);
            Assert.NotNull(little.ChecksumCalibration);

            Assert.True(big.ChecksumCalibration!.BigMatches, big.ChecksumCalibration.ToString());
            Assert.True(little.ChecksumCalibration!.LittleMatches, little.ChecksumCalibration.ToString());

            // The two reductions differ on this payload, so each result is decisive.
            Assert.True(big.ChecksumCalibration.IsDecisive, big.ChecksumCalibration.ToString());
            Assert.True(little.ChecksumCalibration.IsDecisive, little.ChecksumCalibration.ToString());
            Assert.Equal(NavChecksum.WordOrder.Big, big.ChecksumCalibration.DecidedOrder);
            Assert.Equal(NavChecksum.WordOrder.Little, little.ChecksumCalibration.DecidedOrder);
        }

        [Fact]
        public void CalibrationReportsWhenNeitherReductionMatches()
        {
            NavMeshImage image = SyntheticImage.Grid(3, 3);
            byte[] payload = image.Write();

            // Corrupt the stored checksum without touching the payload it covers.
            int checksumOffset = 12;
            payload[checksumOffset] ^= 0xAA;

            NavMeshImage parsed = NavMeshImage.Read(payload);

            Assert.NotNull(parsed.ChecksumCalibration);
            Assert.False(parsed.ChecksumCalibration!.BigMatches);
            Assert.False(parsed.ChecksumCalibration.LittleMatches);
            Assert.False(parsed.ChecksumCalibration.IsDecisive);
            Assert.Contains("NEITHER", parsed.ChecksumCalibration.ToString());
        }

        [Fact]
        public void RoundTripUsesWhicheverReductionTheSourceProved()
        {
            // A donor written with the little-endian reduction must round-trip byte exactly, which
            // it only can if the writer follows the calibration rather than the library default.
            NavMeshImage image = SyntheticImage.Grid(3, 3, 1f, ByteOrder.Little);
            byte[] payload = image.Write(NavChecksum.WordOrder.Little);

            NavRoundTripResult result = NavRoundTrip.Run(payload);

            Assert.True(result.IsByteExact, result.ToString());
        }
    }
}
