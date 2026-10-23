using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace FrostyCLI.Core
{
    // Import is gated on the OBJ being byte-identical to the one validation passed. Without the
    // pin, "I validated it" and "I imported it" can be two different meshes, and the compiler's
    // constraints would have been checked against a file that never shipped. The control plane's
    // /importnavmeshobj endpoint pins the same way.
    public static class NavmeshObjPin
    {
        public static string Compute(string objPath)
        {
            if (string.IsNullOrWhiteSpace(objPath))
                throw new ArgumentException("An OBJ path is required.", "objPath");
            if (!File.Exists(objPath))
                throw new FileNotFoundException("OBJ not found: " + objPath, objPath);
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(objPath))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static void Verify(string objPath, string expectedPin)
        {
            if (string.IsNullOrWhiteSpace(expectedPin))
                throw new ArgumentException("An expected pin is required.", "expectedPin");
            string actual = Compute(objPath);
            if (!string.Equals(actual, expectedPin, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                    "The OBJ changed after it was validated. Validated {0}, found {1}. "
                    + "Re-run 'navmesh validate' on the current file before importing.",
                    expectedPin, actual));
        }
    }
}
