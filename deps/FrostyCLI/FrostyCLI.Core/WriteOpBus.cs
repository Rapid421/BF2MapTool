using System;

namespace FrostyCLI.Core
{
    public interface IWriteOpBus
    {
        int Execute(string op, string[] args, ExportLogger logger, OutputFormatter formatter);
    }

    public static class WriteOpBus
    {
        public static IWriteOpBus Instance;

        public static int Execute(string op, string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (Instance == null)
                throw new InvalidOperationException(
                    "WriteOpBus not wired. Set WriteOpBus.Instance = new CommandBus() in Program.cs.");
            return Instance.Execute(op, args, logger, formatter);
        }
    }
}
