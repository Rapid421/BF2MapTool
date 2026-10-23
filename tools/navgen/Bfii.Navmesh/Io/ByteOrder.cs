// VENDORED from BfiiMapTools src\Bfii.Navmesh\Io\ByteOrder.cs (sha256:E230F96F1D090835).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
namespace Bfii.Navmesh.Io
{
    /// <summary>
    /// Byte order of the multi-byte scalars in a navigation image.
    /// </summary>
    /// <remarks>
    /// The image announces its own order with a 32-bit flag that is all ones for big endian and
    /// all zeros for little endian. The flag is byte-order agnostic by construction, which is why
    /// it can be read before the order is known.
    /// COMMUNITY_TESTED (see evidence\navmesh\FORMAT-PROVENANCE.md, row H1).
    /// </remarks>
    public enum ByteOrder
    {
        Little = 0,
        Big = 1,
    }
}
