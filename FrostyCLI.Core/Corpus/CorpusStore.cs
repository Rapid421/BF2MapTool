using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Core.Corpus
{
    // Where the mod corpus lives and how to read it.
    //
    //   <dir>\corpus.db            SQLite aggregates + byte offsets (build_corpus_db.py)
    //   <dir>\asset-index.jsonl    one row per (mod, asset): the flattened scalars a mod ships
    //   <dir>\vanilla-index.jsonl  one row per game asset; written by `corpus dump-vanilla`
    //
    // The directory is --dir, else FROSTYCLI_CORPUS, else a `corpus` folder beside the exe.
    // Only corpus.db is required: every aggregate query runs from it alone.
    public sealed class CorpusStore
    {
        public string Dir { get; }
        public string DbPath => Path.Combine(Dir, "corpus.db");
        public string ModIndexPath => Path.Combine(Dir, "asset-index.jsonl");
        public string VanillaIndexPath => Path.Combine(Dir, "vanilla-index.jsonl");

        public bool HasModIndex => File.Exists(ModIndexPath);
        public bool HasVanillaIndex => File.Exists(VanillaIndexPath);

        public CorpusStore(string dir) { Dir = Path.GetFullPath(dir); }

        public static string ResolveDir(string explicitDir)
        {
            if (!string.IsNullOrEmpty(explicitDir)) return explicitDir;
            string env = Environment.GetEnvironmentVariable("FROSTYCLI_CORPUS");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "corpus");
        }

        public Sqlite OpenDb(bool writable = false)
        {
            if (!File.Exists(DbPath))
                throw new FileNotFoundException(
                    "corpus.db not found in " + Dir + ". Put the corpus folder beside FrostyCLI.exe, " +
                    "set FROSTYCLI_CORPUS, or pass --dir <folder>.");
            return Sqlite.Open(DbPath, writable);
        }

        // One JSONL row, by the byte offset corpus.db recorded for it.
        public static JObject ReadAt(string path, long offset)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var ms = new MemoryStream();
                int b;
                while ((b = fs.ReadByte()) != -1 && b != '\n') ms.WriteByte((byte)b);
                return JObject.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            }
        }
    }
}
