using System;
using FrostySdk.Interfaces;

namespace FrostyCLI.Core
{
    public class ExportLogger : ILogger
    {
        private readonly bool _quiet;

        public ExportLogger(bool quiet = false)
        {
            _quiet = quiet;
        }

        public void Log(string text, params object[] vars)
        {
            if (text != null && text.StartsWith("progress:", StringComparison.OrdinalIgnoreCase))
                return;
            if (!_quiet)
                Console.Error.WriteLine("[" + Timestamp() + "] " + SafeFormat(text, vars));
        }

        public void LogWarning(string text, params object[] vars)
        {
            Console.Error.WriteLine("[" + Timestamp() + "] WARN " + SafeFormat(text, vars));
        }

        public void LogError(string text, params object[] vars)
        {
            Console.Error.WriteLine("[" + Timestamp() + "] ERR  " + SafeFormat(text, vars));
        }

        private static string SafeFormat(string text, object[] vars)
        {
            try { return vars != null && vars.Length > 0 ? string.Format(text, vars) : text; }
            catch { return text; }
        }

        private static string Timestamp() => DateTime.Now.ToString("HH:mm:ss");
    }
}
