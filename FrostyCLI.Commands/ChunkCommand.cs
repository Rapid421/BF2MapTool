using System;
using System.IO;
using System.Reflection;
using Frosty.Core;
using FrostySdk.Managers;
using FrostyCLI.Core;

namespace FrostyCLI.Commands
{
    public class ChunkCommand : ICommand
    {
        public string Name => "chunk";

        public int Execute(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("Usage: frostycli chunk <export|import> ...");
                return 1;
            }
            switch (args[0].ToLowerInvariant())
            {
                case "export": return ExecuteExport(Sub(args), logger, formatter);
                case "import": return ExecuteImport(Sub(args), logger, formatter);
                default:
                    Console.Error.WriteLine("chunk: unknown subcommand '" + args[0] + "'. Use export or import.");
                    return 1;
            }
        }

        private static string[] Sub(string[] a) { var r = new string[a.Length - 1]; Array.Copy(a, 1, r, 0, r.Length); return r; }

        public static int ExecuteExport(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string guidStr = null;
            string outFile = null;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                else if (!args[i].StartsWith("--"))                  guidStr = args[i];
            }

            if (guidStr == null) { Console.Error.WriteLine("chunk export: missing <id>"); return 1; }
            if (outFile == null) { Console.Error.WriteLine("chunk export: missing --out"); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("chunk export: AssetManager not initialized."); return 1; }

            Guid id;
            if (!Guid.TryParse(guidStr, out id))
            { Console.Error.WriteLine("chunk export: invalid GUID: " + guidStr); return 1; }

            ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(id);
            if (entry == null) { Console.Error.WriteLine("chunk export: chunk not found: " + guidStr); return 1; }

            byte[] data;
            try
            {
                MethodInfo m = App.AssetManager.GetType()
                    .GetMethod("GetChunk", new[] { entry.GetType() });
                object raw = m?.Invoke(App.AssetManager, new object[] { entry });
                if (raw is byte[] bytes)
                    data = bytes;
                else if (raw is Stream stream)
                    { using (var ms = new MemoryStream()) { stream.CopyTo(ms); data = ms.ToArray(); } }
                else
                    { Console.Error.WriteLine("chunk export: unexpected GetChunk return type"); return 1; }
            }
            catch (Exception ex) { Console.Error.WriteLine("chunk export: read failed: " + ex.Message); return 1; }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outFile, data);
            return 0;
        }

        // Imports raw bytes from a file into a chunk. Replaces an existing chunk (--id, ModifyChunk),
        // or creates a new one with --new (AddChunk; optionally at a given --id). ModifyChunk/AddChunk
        // recompress the buffer; pass the decompressed bytes (e.g. from `chunk export`).
        public static int ExecuteImport(string[] args, ExportLogger logger, OutputFormatter formatter)
        {
            string guidStr = null;
            string inFile  = null;
            bool   create  = false;

            for (int i = 0; i < args.Length; i++)
            {
                if      (args[i] == "--id" && i + 1 < args.Length) guidStr = args[++i];
                else if (args[i] == "--new")                       create  = true;
                else if (!args[i].StartsWith("--"))                inFile  = args[i];
            }

            if (inFile == null)  { Console.Error.WriteLine("chunk import: missing <file>"); return 1; }
            if (!create && guidStr == null) { Console.Error.WriteLine("chunk import: missing --id <guid> (or --new to create a chunk)"); return 1; }
            if (!File.Exists(inFile)) { Console.Error.WriteLine("chunk import: file not found: " + inFile); return 1; }
            if (!Session.Project.IsLoaded)
            { Console.Error.WriteLine("chunk import: no project loaded. Run 'project load' or 'project new' first."); return 1; }
            if (App.AssetManager == null) { Console.Error.WriteLine("chunk import: AssetManager not initialized."); return 1; }

            Guid id = Guid.Empty;
            if (guidStr != null && !Guid.TryParse(guidStr, out id))
            { Console.Error.WriteLine("chunk import: invalid GUID: " + guidStr); return 1; }

            byte[] data;
            try { data = File.ReadAllBytes(inFile); }
            catch (Exception ex) { Console.Error.WriteLine("chunk import: read failed: " + ex.Message); return 1; }

            if (DryRun.Active) { DryRun.Report(logger, "import {0} bytes into {1}", data.Length, create ? "a new chunk" : ("chunk " + id)); return 0; }

            if (create)
            {
                Guid newId;
                try { newId = App.AssetManager.AddChunk(data, guidStr != null ? (Guid?)id : null); }
                catch (Exception ex) { Console.Error.WriteLine("chunk import: add failed: " + ex.Message); return 1; }
                if (App.AssetManager.GetChunkEntry(newId) == null)
                { Console.Error.WriteLine("chunk import: AddChunk did not create the chunk"); return 1; }
                formatter.WriteResult("OK", "chunk import",
                    new System.Collections.Generic.Dictionary<string, string> { { "chunk", newId.ToString() }, { "created", "true" }, { "bytes", data.Length.ToString() } });
                return 0;
            }

            if (App.AssetManager.GetChunkEntry(id) == null)
            { Console.Error.WriteLine("chunk import: chunk not found: " + guidStr + " (use --new to create a chunk)"); return 1; }

            bool ok;
            try { ok = App.AssetManager.ModifyChunk(id, data); }
            catch (Exception ex) { Console.Error.WriteLine("chunk import: modify failed: " + ex.Message); return 1; }
            if (!ok) { Console.Error.WriteLine("chunk import: ModifyChunk failed for " + guidStr); return 1; }

            formatter.WriteResult("OK", "chunk import",
                new System.Collections.Generic.Dictionary<string, string> { { "chunk", id.ToString() }, { "bytes", data.Length.ToString() } });
            return 0;
        }
    }
}
