using System;
using System.IO;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    // Import is gated on the OBJ being byte-identical to the one that passed validation. Without
    // that pin, "I validated it" and "I imported it" can silently be two different meshes, and the
    // compiler's constraints would have been checked against a file nobody shipped.
    public class NavmeshObjPinTests
    {
        private static string TempObj(string body)
        {
            string p = Path.Combine(Path.GetTempPath(), "nav_" + Guid.NewGuid().ToString("N") + ".obj");
            File.WriteAllText(p, body);
            return p;
        }

        [Test]
        public void PinIsStableForTheSameBytes()
        {
            string a = TempObj("v 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\n");
            string b = TempObj("v 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\n");
            try { Assert.AreEqual(NavmeshObjPin.Compute(a), NavmeshObjPin.Compute(b)); }
            finally { File.Delete(a); File.Delete(b); }
        }

        [Test]
        public void PinChangesWhenASingleCoordinateChanges()
        {
            string a = TempObj("v 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\n");
            string b = TempObj("v 0 0 0\nv 1 0 0\nv 0 0 2\nf 1 2 3\n");
            try
            {
                string pa = NavmeshObjPin.Compute(a), pb = NavmeshObjPin.Compute(b);
                Assert.IsFalse(pa == pb);
            }
            finally { File.Delete(a); File.Delete(b); }
        }

        [Test]
        public void VerifyAcceptsAnUnchangedFile()
        {
            string a = TempObj("v 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\n");
            try { NavmeshObjPin.Verify(a, NavmeshObjPin.Compute(a)); }
            finally { File.Delete(a); }
        }

        [Test]
        public void VerifyRejectsAFileEditedAfterValidation()
        {
            // The whole point: edit between validate and import and the import must refuse.
            string a = TempObj("v 0 0 0\nv 1 0 0\nv 0 0 1\nf 1 2 3\n");
            try
            {
                string pin = NavmeshObjPin.Compute(a);
                File.WriteAllText(a, "v 0 0 0\nv 1 0 0\nv 0 0 9\nf 1 2 3\n");
                Assert.Throws<InvalidOperationException>(() => NavmeshObjPin.Verify(a, pin));
            }
            finally { File.Delete(a); }
        }

        [Test]
        public void ComputeOnAMissingFileFailsClearly()
        {
            Assert.Throws<FileNotFoundException>(
                () => NavmeshObjPin.Compute(Path.Combine(Path.GetTempPath(), "nav_does_not_exist.obj")));
        }
    }
}
