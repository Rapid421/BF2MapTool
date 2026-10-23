// VENDORED from BfiiMapTools src\Bfii.Navmesh\Primitives\Axis.cs (sha256:05ED019214829542).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
namespace Bfii.Navmesh.Primitives
{
    /// <summary>
    /// Axis identifier. The numeric values are serialized (graph build-up axis, KD split axis) and
    /// are also used as component indices into <see cref="Vec3"/>, so they must stay 0, 1, 2.
    /// </summary>
    public enum Axis : uint
    {
        X = 0,
        Y = 1,
        Z = 2,
    }
}
