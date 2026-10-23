using System;
using System.Collections.Generic;
namespace FrostyCLI.Commands
{
    public static class CommandRegistry
    {
        private static readonly Dictionary<string, ICommand> _commands
            = new Dictionary<string, ICommand>(StringComparer.OrdinalIgnoreCase);

        public static void Register(ICommand command)
        {
            if (command == null) throw new ArgumentNullException("command");
            _commands[command.Name] = command;
        }

        public static ICommand Get(string name)
        {
            if (name == null) return null;
            ICommand cmd;
            _commands.TryGetValue(name, out cmd);
            return cmd;
        }

        public static IEnumerable<ICommand> All => _commands.Values;
    }
}
