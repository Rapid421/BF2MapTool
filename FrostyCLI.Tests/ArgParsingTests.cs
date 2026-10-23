using System;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    public class ArgParsingTests
    {
        [Test]
        public void DefaultGame_IsSwbf2()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(new[] { "ls" }, out remaining);
            Assert.AreEqual("swbf2", opts.Game);
        }

        [Test]
        public void GameFlag_OverridesDefault()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(new[] { "--game", "swbf1", "ls" }, out remaining);
            Assert.AreEqual("swbf1", opts.Game);
        }

        [Test]
        public void JsonFlag_SetsJsonTrue()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(new[] { "--json", "ls" }, out remaining);
            Assert.IsTrue(opts.Json);
        }

        [Test]
        public void QuietFlag_SetsQuietTrue()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(new[] { "--quiet", "ls" }, out remaining);
            Assert.IsTrue(opts.Quiet);
        }

        [Test]
        public void ParseGlobal_TrailingValueFlag_DoesNotThrow()
        {
            string[] remaining;
            // `frostycli --game-path` (value-flag as the last token) must not IndexOutOfRange.
            var opts = ArgParser.ParseGlobal(new[] { "ls", "--game-path" }, out remaining);
            Assert.IsTrue(opts.GamePath == null);
        }

        [Test]
        public void ParseGlobal_GameValue_Lowercased()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(new[] { "--game", "SWBF1", "ls" }, out remaining);
            Assert.AreEqual("swbf1", opts.Game);
        }

        [Test]
        public void ResolveDefaults_EnvGamePath_FillsWhenUnset()
        {
            string key = "FROSTYCLI_GAME_PATH_SWBF2";
            string prev = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, @"C:\FromEnv\SWBF2");
            try
            {
                string[] remaining;
                var opts = ArgParser.ParseGlobal(new[] { "ls" }, out remaining);
                opts = ArgParser.ResolveDefaults(opts);
                Assert.AreEqual(@"C:\FromEnv\SWBF2", opts.GamePath);
            }
            finally { Environment.SetEnvironmentVariable(key, prev); }
        }

        [Test]
        public void ResolveDefaults_ExplicitFlag_WinsOverEnv()
        {
            string key = "FROSTYCLI_GAME_PATH_SWBF2";
            string prev = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, @"C:\FromEnv\SWBF2");
            try
            {
                string[] remaining;
                var opts = ArgParser.ParseGlobal(new[] { "--game-path", @"C:\Explicit", "ls" }, out remaining);
                opts = ArgParser.ResolveDefaults(opts);
                Assert.AreEqual(@"C:\Explicit", opts.GamePath);
            }
            finally { Environment.SetEnvironmentVariable(key, prev); }
        }

        [Test]
        public void GamePath_ParsedCorrectly()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(
                new[] { "--game-path", @"C:\Games\SWBF2", "export", "foo" }, out remaining);
            Assert.AreEqual(@"C:\Games\SWBF2", opts.GamePath);
        }

        [Test]
        public void RemainingArgs_ContainCommandName()
        {
            string[] remaining;
            ArgParser.ParseGlobal(new[] { "--json", "ls", "--type", "RigidMeshAsset" }, out remaining);
            Assert.AreEqual("ls", remaining[0]);
            Assert.AreEqual("--type", remaining[1]);
            Assert.AreEqual("RigidMeshAsset", remaining[2]);
        }

        [Test]
        public void EmptyArgs_RemainingIsEmpty()
        {
            string[] remaining;
            ArgParser.ParseGlobal(new string[0], out remaining);
            Assert.AreEqual(0, remaining.Length);
        }

        [Test]
        public void NoGamePath_IsNull()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(new[] { "ls" }, out remaining);
            Assert.IsNull(opts.GamePath);
        }

        [Test]
        public void PluginsDir_ParsedCorrectly()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(
                new[] { "--plugins-dir", @"C:\Plugins", "ls" }, out remaining);
            Assert.AreEqual(@"C:\Plugins", opts.PluginsDir);
        }

        [Test]
        public void AdaptersDir_ParsedCorrectly()
        {
            string[] remaining;
            var opts = ArgParser.ParseGlobal(
                new[] { "--adapters-dir", @"C:\Adapters", "ls" }, out remaining);
            Assert.AreEqual(@"C:\Adapters", opts.AdaptersDir);
        }

        [Test]
        public void ParseGlobal_SaveFlags()
        {
            string[] rest;
            var o = ArgParser.ParseGlobal(new[] { "--save", "level", "list", "x" }, out rest);
            Assert.IsTrue(o.Save);
            Assert.IsNull(o.SaveAs);
            Assert.AreEqual(3, rest.Length);

            var o2 = ArgParser.ParseGlobal(new[] { "--save-as", "out.fbproject", "level", "list", "x" }, out rest);
            Assert.IsFalse(o2.Save);
            Assert.AreEqual("out.fbproject", o2.SaveAs);
            Assert.AreEqual(3, rest.Length);
        }

        [Test]
        public void ParseGlobal_SaveAsWithoutValue_LeavesItUnset()
        {
            string[] rest;
            var o = ArgParser.ParseGlobal(new[] { "level", "list", "x", "--save-as" }, out rest);
            Assert.IsNull(o.SaveAs);
        }

    }
}
