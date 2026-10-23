using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace FrostyCLI.Core
{
    public class OutputFormatter
    {
        private readonly bool _json;
        private readonly TextWriter _out;

        public OutputFormatter(bool json, TextWriter output = null)
        {
            _json = json;
            _out = output ?? Console.Out;
        }

        public void WriteResult(string status, string op, IDictionary<string, string> fields)
        {
            if (_json)
            {
                var obj = new Dictionary<string, object>();
                obj["ts"]     = DateTime.UtcNow.ToString("O");
                obj["status"] = status;
                obj["op"]     = op;
                if (fields != null)
                    foreach (var kv in fields)
                        obj[kv.Key] = kv.Value;
                _out.WriteLine(JsonConvert.SerializeObject(obj));
            }
            else
            {
                string ts = DateTime.Now.ToString("HH:mm:ss");
                string primary = "";
                if (fields != null)
                {
                    string tmp;
                    if (fields.TryGetValue("file", out tmp)) primary = tmp;
                    else if (fields.TryGetValue("value", out tmp)) primary = tmp;
                }
                _out.WriteLine("[" + ts + "] " + status.PadRight(6) + "  " + op + " -> " + primary);

                if (fields != null)
                {
                    string pad = new string(' ', 11);
                    foreach (var kv in fields)
                    {
                        if (kv.Key == "file" || kv.Key == "value") continue;
                        _out.WriteLine(pad + kv.Key + ": " + kv.Value);
                    }
                }
            }
        }

        /// <summary>
        /// Writes one line verbatim to this formatter's output.
        ///
        /// A command that emits a whole document -- a JSON report, a table -- has no per-field
        /// structure for WriteResult to render, and writing to Console.Out instead bypasses the
        /// writer a caller supplied. That makes such a command untestable except by redirecting the
        /// process's console, so it goes through the formatter like everything else.
        /// </summary>
        public void WriteLine(string line)
        {
            _out.WriteLine(line);
        }

        public void WriteInfo(string message)
        {
            if (!_json)
                Console.Error.WriteLine(message);
        }
    }
}
