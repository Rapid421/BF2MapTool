// VENDORED from BfiiMapTools src\Bfii.Navmesh\Io\NavFormatException.cs (sha256:D21CBA37739AE509).
// Re-sync with tools\sync-navgen.ps1; do not edit here.
using System;

namespace Bfii.Navmesh.Io
{
    /// <summary>
    /// Raised when a navigation image cannot be parsed or cannot be serialized without loss.
    /// </summary>
    /// <remarks>
    /// Every message carries a byte offset or a structural index. A parser that fails without
    /// naming a location is not diagnosable, and this format is exactly the kind where a silent
    /// misparse produces a plausible-but-wrong result.
    /// </remarks>
    public sealed class NavFormatException : Exception
    {
        public NavFormatException(string message)
            : base(message)
        {
        }

        public NavFormatException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
