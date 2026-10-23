using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // `man` is pure help -- no game/session needed. These are fast unit checks.
    public class ManTests
    {
        [Test]
        public void Man_NoArgs_ListsCommands_Returns0()
        {
            int rc = new ManCommand().Execute(new string[0], new ExportLogger(quiet: true), new OutputFormatter(false));
            Assert.AreEqual(0, rc);
        }

        [Test]
        public void Man_KnownCommand_Returns0()
        {
            int rc = new ManCommand().Execute(new[] { "bundle" }, new ExportLogger(quiet: true), new OutputFormatter(false));
            Assert.AreEqual(0, rc);
        }

        [Test]
        public void Man_UnknownCommand_Returns1()
        {
            int rc = new ManCommand().Execute(new[] { "frobnicate" }, new ExportLogger(quiet: true), new OutputFormatter(false));
            Assert.AreEqual(1, rc);
        }

        [Test]
        public void Man_Names_Returns0()
        {
            int rc = new ManCommand().Execute(new[] { "--names" }, new ExportLogger(quiet: true), new OutputFormatter(false));
            Assert.AreEqual(0, rc);
        }
    }
}
