using System;
using System.IO;
using System.Reflection;

namespace ShaderDataPlugin
{
    /// <summary>
    /// Dead-simple, dependency-free logger for tracking down the SWBF2 2017
    /// loading issue. Writes to a plain text file sitting right next to
    /// ShaderDataPlugin.dll, completely independent of Frosty's own ILogger/
    /// log panel plumbing - so it works even if that plumbing is what's
    /// silently swallowing things.
    ///
    /// File path: <FrostyEditor folder>\Plugins\ShaderDataPlugin_debug.log
    ///
    /// Every call is wrapped so a logging failure can never itself crash or
    /// interrupt the plugin.
    /// </summary>
    internal static class DebugFileLog
    {
        private static readonly object sync = new object();

        private static string LogPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, "ShaderDataPlugin_debug.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (sync)
                {
                    File.AppendAllText(LogPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // never let logging itself break anything
            }
        }
    }
}
