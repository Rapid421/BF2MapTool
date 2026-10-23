using System;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class ProjectCommandTests
    {
        [Test]
        public void Project_NoSubcommand_ExitCode2()
        {
            var logger    = new ExportLogger(quiet: true);
            var formatter = new OutputFormatter(json: false);
            var cmd       = new ProjectCommand();
            int result    = cmd.Execute(new string[0], logger, formatter);
            Assert.AreEqual(2, result);
        }

        [Test]
        public void Mod_NoSubcommand_ExitCode2()
        {
            var logger    = new ExportLogger(quiet: true);
            var formatter = new OutputFormatter(json: false);
            var cmd       = new ModBuildCommand();
            int result    = cmd.Execute(new string[0], logger, formatter);
            Assert.AreEqual(2, result);
        }

        [Test]
        public void ProjectLoad_MissingPath_ExitCode2()
        {
            var logger    = new ExportLogger(quiet: true);
            var formatter = new OutputFormatter(json: false);
            var cmd       = new ProjectCommand();
            int result    = cmd.Execute(new[] { "load" }, logger, formatter);
            Assert.AreEqual(2, result);
        }

        [Test]
        public void ProjectLoad_NonexistentFile_ExitCode2()
        {
            var logger    = new ExportLogger(quiet: true);
            var formatter = new OutputFormatter(json: false);
            var cmd       = new ProjectCommand();
            int result    = cmd.Execute(new[] { "load", "C:\\nonexistent_file_xyz.fbproject" }, logger, formatter);
            Assert.AreEqual(2, result);
        }

        [Test]
        public void ProjectSave_NoProject_ExitCode2()
        {
            var logger  = new ExportLogger(quiet: true);
            var project = new HeadlessProject();
            Assert.IsFalse(project.IsLoaded);
            bool ok = project.Save(null, logger);
            Assert.IsFalse(ok);
        }

        [Test]
        public void ProjectSave_NoPathAndNoFilePath_ReturnsFalse()
        {
            var logger  = new ExportLogger(quiet: true);
            var project = new HeadlessProject();
            bool ok = project.Save(null, logger);
            Assert.IsFalse(ok);
        }

        [Test]
        public void ModBuild_NoProject_ExitCode2()
        {
            var logger  = new ExportLogger(quiet: true);
            var project = new HeadlessProject();
            bool ok = project.Build("out.fbmod", new ModSettings { Name = "Test" }, logger);
            Assert.IsFalse(ok);
        }

        [Test]
        public void ModBuild_MissingOut_ExitCode2()
        {
            var logger    = new ExportLogger(quiet: true);
            var formatter = new OutputFormatter(json: false);
            var cmd       = new ModBuildCommand();
            int result    = cmd.Execute(new[] { "build", "--mod-name", "TestMod" }, logger, formatter);
            Assert.AreEqual(2, result);
        }

        [Test]
        public void ModBuild_MissingModName_ExitCode2()
        {
            var logger    = new ExportLogger(quiet: true);
            var formatter = new OutputFormatter(json: false);
            var cmd       = new ModBuildCommand();
            int result    = cmd.Execute(new[] { "build", "--out", "out.fbmod" }, logger, formatter);
            Assert.AreEqual(2, result);
        }

        [Test]
        public void ModBuild_ParsesAllFlags()
        {
            var logger    = new ExportLogger(quiet: true);
            var formatter = new OutputFormatter(json: false);
            var cmd       = new ModBuildCommand();
            int noOut  = cmd.Execute(new[] { "build", "--mod-name", "N", "--mod-author", "A", "--mod-version", "1.0", "--mod-description", "D" }, logger, formatter);
            int noName = cmd.Execute(new[] { "build", "--out", "x.fbmod", "--mod-author", "A" }, logger, formatter);
            Assert.AreEqual(2, noOut);
            Assert.AreEqual(2, noName);
        }
    }
}
