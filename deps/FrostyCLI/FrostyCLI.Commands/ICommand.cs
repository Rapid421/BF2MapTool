using FrostyCLI.Core;
namespace FrostyCLI.Commands
{
    public interface ICommand
    {
        string Name { get; }
        int Execute(string[] args, ExportLogger logger, OutputFormatter formatter);
    }
}
