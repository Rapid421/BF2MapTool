using System;
using System.IO;
using FrostyCLI.Commands;
using FrostyCLI.Core;

namespace FrostyCLI.Tests
{
    public class ExportBatchCommandTests
    {
        // ── Path building ─────────────────────────────────────────────────

        [Test]
        public void BuildOutputPath_ForwardSlashName_ProducesNestedPath()
        {
            string result = ExportBatchCommand.BuildOutputPath(
                @"C:\out", "xp2/objects/foo_mesh", "fbx");
            // EBX forward-slashes must become directory separators on disk
            string expected = Path.Combine(@"C:\out", "xp2", "objects", "foo_mesh.fbx");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void BuildOutputPath_TopLevelName_ProducesFileInRoot()
        {
            string result = ExportBatchCommand.BuildOutputPath(
                @"C:\out", "someAsset_mesh", "fbx");
            string expected = Path.Combine(@"C:\out", "someAsset_mesh.fbx");
            Assert.AreEqual(expected, result);
        }

        [Test]
        public void BuildOutputPath_ObjFormat_UsesObjExtension()
        {
            string result = ExportBatchCommand.BuildOutputPath(
                @"C:\out", "xp2/foo_mesh", "obj");
            Assert.IsTrue(result.EndsWith(".obj"),
                "Expected .obj extension but got: " + result);
        }

        [Test]
        public void BuildOutputPath_BackslashInName_Normalised()
        {
            // Some EBX names might use backslashes; both must be treated as separators
            string result = ExportBatchCommand.BuildOutputPath(
                @"C:\out", @"xp2\objects\bar_mesh", "fbx");
            string expected = Path.Combine(@"C:\out", "xp2", "objects", "bar_mesh.fbx");
            Assert.AreEqual(expected, result);
        }

        // ── Arg validation ────────────────────────────────────────────────

        [Test]
        public void Execute_MissingOut_ReturnsOne()
        {
            var cmd    = new ExportBatchCommand();
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            // --out is missing; the check fires before touching App.AssetManager
            int result = cmd.Execute(new[] { "--filter", "xp2/objects" }, logger, fmt);
            Assert.AreEqual(1, result);
        }

        [Test]
        public void Execute_NoAssetManager_ReturnsOne()
        {
            // App.AssetManager is null in unit test context — command must detect this
            var cmd    = new ExportBatchCommand();
            var logger = new ExportLogger(quiet: true);
            var fmt    = new OutputFormatter(json: false);
            int result = cmd.Execute(
                new[] { "--out", @"C:\tmp\out", "--filter", "xp2" }, logger, fmt);
            Assert.AreEqual(1, result);
        }
    }
}
