using System;
using System.IO;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class ImportCommandTests
    {
        [Test]
        public void ParseTextureArgs_MissingAs_ReturnsError()
        {
            ImportCommand.Parsed p;
            string err = ImportCommand.ParseTextureArgs(new[] { "some.dds" }, out p);
            Assert.IsNotNull(err);
            Assert.IsTrue(err.Contains("--as"), "Error should mention --as but was: " + err);
        }

        [Test]
        public void ParseTextureArgs_MissingFile_ReturnsError()
        {
            ImportCommand.Parsed p;
            string err = ImportCommand.ParseTextureArgs(new[] { "--as", "s2/x" }, out p);
            Assert.IsNotNull(err);
            Assert.IsTrue(err.Contains("missing <file>"), "Error should mention missing file but was: " + err);
        }

        [Test]
        public void ParseTextureArgs_NonexistentFile_ReturnsError()
        {
            ImportCommand.Parsed p;
            string err = ImportCommand.ParseTextureArgs(new[] { "nope.dds", "--as", "s2/x" }, out p);
            Assert.IsNotNull(err);
            Assert.IsTrue(err.Contains("file not found"), "Error should mention 'file not found' but was: " + err);
        }

        [Test]
        public void ParseTextureArgs_FormatInferredFromExtension()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            try
            {
                ImportCommand.Parsed p;
                string err = ImportCommand.ParseTextureArgs(new[] { tmp, "--as", "s2/x" }, out p);
                Assert.IsNull(err);
                Assert.AreEqual("PNG", p.Format);
                Assert.AreEqual("s2/x", p.Ebx);
                Assert.IsTrue(p.GenerateMipmaps);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseTextureArgs_NoMipmapsAndFormatOverride()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
            File.WriteAllBytes(tmp, new byte[] { 1 });
            try
            {
                ImportCommand.Parsed p;
                string err = ImportCommand.ParseTextureArgs(
                    new[] { tmp, "--as", "s2/x", "--format", "dds", "--no-mipmaps" }, out p);
                Assert.IsNull(err);
                Assert.AreEqual("DDS", p.Format);
                Assert.IsFalse(p.GenerateMipmaps);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseTextureArgs_UnsupportedExtensionNoOverride_ReturnsError()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xyz");
            File.WriteAllBytes(tmp, new byte[] { 1 });
            try
            {
                ImportCommand.Parsed p;
                string err = ImportCommand.ParseTextureArgs(new[] { tmp, "--as", "s2/x" }, out p);
                Assert.IsNotNull(err);
                Assert.IsTrue(err.Contains("unsupported"), "Error should mention 'unsupported' but was: " + err);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseTextureArgs_InvalidFormatOverride_ReturnsError()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xyz");
            File.WriteAllBytes(tmp, new byte[] { 1 });
            try
            {
                ImportCommand.Parsed p;
                string err = ImportCommand.ParseTextureArgs(
                    new[] { tmp, "--as", "s2/x", "--format", "bmp" }, out p);
                Assert.IsNotNull(err);
                Assert.IsTrue(err.Contains("invalid --format"), "Error should mention 'invalid --format' but was: " + err);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseTextureArgs_TgaExtension_InferredCorrectly()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tga");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            try
            {
                ImportCommand.Parsed p;
                string err = ImportCommand.ParseTextureArgs(new[] { tmp, "--as", "s2/tex" }, out p);
                Assert.IsNull(err);
                Assert.AreEqual("TGA", p.Format);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseTextureArgs_HdrExtension_InferredCorrectly()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".hdr");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            try
            {
                ImportCommand.Parsed p;
                string err = ImportCommand.ParseTextureArgs(new[] { tmp, "--as", "s2/tex" }, out p);
                Assert.IsNull(err);
                Assert.AreEqual("HDR", p.Format);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ImportCommand_UnknownSubcommand_ReturnsOne()
        {
            var cmd = new ImportCommand();
            var logger = new FrostyCLI.Core.ExportLogger(false);
            var formatter = new FrostyCLI.Core.OutputFormatter(false);
            int rc = cmd.Execute(new[] { "audio", "file.wav" }, logger, formatter);
            Assert.AreEqual(1, rc);
        }

        [Test]
        public void ImportCommand_NoArgs_ReturnsOne()
        {
            var cmd = new ImportCommand();
            var logger = new FrostyCLI.Core.ExportLogger(false);
            var formatter = new FrostyCLI.Core.OutputFormatter(false);
            int rc = cmd.Execute(new string[0], logger, formatter);
            Assert.AreEqual(1, rc);
        }

        [Test]
        public void ParseMeshArgs_MissingAs_ReturnsError()
        {
            ImportCommand.ParsedMesh p;
            string err = ImportCommand.ParseMeshArgs(new[] { "some.fbx" }, out p);
            Assert.IsNotNull(err);
            Assert.IsTrue(err.Contains("--as"), "Error should mention --as but was: " + err);
        }

        [Test]
        public void ParseMeshArgs_MissingFile_ReturnsError()
        {
            ImportCommand.ParsedMesh p;
            string err = ImportCommand.ParseMeshArgs(new[] { "--as", "s2/x" }, out p);
            Assert.IsNotNull(err);
            Assert.IsTrue(err.Contains("missing <file>"), "Error should mention missing file but was: " + err);
        }

        [Test]
        public void ParseMeshArgs_NonexistentFile_ReturnsError()
        {
            ImportCommand.ParsedMesh p;
            string err = ImportCommand.ParseMeshArgs(new[] { "nope.fbx", "--as", "s2/x" }, out p);
            Assert.IsNotNull(err);
            Assert.IsTrue(err.Contains("file not found"), "Error should mention 'file not found' but was: " + err);
        }

        [Test]
        public void ParseMeshArgs_NonFbxExtension_ReturnsError()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".obj");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            try
            {
                ImportCommand.ParsedMesh p;
                string err = ImportCommand.ParseMeshArgs(new[] { tmp, "--as", "s2/x" }, out p);
                Assert.IsNotNull(err);
                Assert.IsTrue(err.Contains("only FBX"), "Error should mention FBX-only but was: " + err);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseMeshArgs_ValidWithScale_Parses()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fbx");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            try
            {
                ImportCommand.ParsedMesh p;
                string err = ImportCommand.ParseMeshArgs(new[] { tmp, "--as", "s2/mesh", "--scale", "2.5" }, out p);
                Assert.IsNull(err);
                Assert.AreEqual("s2/mesh", p.Ebx);
                Assert.IsTrue(Math.Abs(p.Scale - 2.5f) < 1e-5f, "scale should be 2.5 but was " + p.Scale);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseMeshArgs_DefaultScaleIsOne()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fbx");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            try
            {
                ImportCommand.ParsedMesh p;
                string err = ImportCommand.ParseMeshArgs(new[] { tmp, "--as", "s2/mesh" }, out p);
                Assert.IsNull(err);
                Assert.IsTrue(Math.Abs(p.Scale - 1.0f) < 1e-5f, "default scale should be 1 but was " + p.Scale);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [Test]
        public void ParseMeshArgs_NonPositiveScale_ReturnsError()
        {
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".fbx");
            File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
            try
            {
                ImportCommand.ParsedMesh p;
                string err = ImportCommand.ParseMeshArgs(new[] { tmp, "--as", "s2/mesh", "--scale", "0" }, out p);
                Assert.IsNotNull(err);
                Assert.IsTrue(err.Contains("--scale"), "Error should mention --scale but was: " + err);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }
}
