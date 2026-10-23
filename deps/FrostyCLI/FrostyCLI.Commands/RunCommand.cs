using System;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public class RunCommand : ICommand
    {
        public string Name => "run";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string pipelinePath = null;
            foreach (var a in args)
            {
                if (!a.StartsWith("--"))
                {
                    pipelinePath = a;
                    break;
                }
            }

            if (pipelinePath == null)
            {
                Console.Error.WriteLine("Usage: frostycli run <pipeline.json>");
                return 1;
            }

            return PipelineRunnerBus.Run(pipelinePath, logger, formatter);
        }
    }
}
