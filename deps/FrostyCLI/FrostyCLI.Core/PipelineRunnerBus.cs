using System;

namespace FrostyCLI.Core
{
    // Lets FrostyCLI.Commands (RunCommand) invoke the pipeline runner without referencing
    // FrostyCLI.Pipeline. Pipeline references Commands (JsonPipelineRunner calls the read/write
    // commands), so a direct Commands->Pipeline reference would form a circular dependency that
    // cannot cold-build. Program.cs wires the concrete runner: PipelineRunnerBus.Instance = new JsonPipelineRunner().
    public interface IPipelineRunner
    {
        int Run(string pipelineJsonPath, ExportLogger logger, OutputFormatter formatter);
    }

    public static class PipelineRunnerBus
    {
        public static IPipelineRunner Instance;

        public static int Run(string pipelineJsonPath, ExportLogger logger, OutputFormatter formatter)
        {
            if (Instance == null)
                throw new InvalidOperationException(
                    "PipelineRunnerBus not wired. Set PipelineRunnerBus.Instance = new JsonPipelineRunner() in Program.cs.");
            return Instance.Run(pipelineJsonPath, logger, formatter);
        }
    }
}
