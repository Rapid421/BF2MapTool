using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace FrostyCLI.Core.Corpus
{
    // A minimal SQLite binding over winsqlite3.dll, the SQLite that ships in System32 on
    // Windows 10 and 11. Using it keeps the release free of a native dependency: no
    // System.Data.SQLite, no SQLite.Interop.dll beside the exe, nothing to restore.
    //
    // Only what the corpus needs: open, prepare, bind text/int64, step, read columns, exec.
    public sealed class Sqlite : IDisposable
    {
        private const string Lib = "winsqlite3.dll";
        private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 0x1, SQLITE_OPEN_READWRITE = 0x2, SQLITE_OPEN_URI = 0x40;
        private const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_NULL = 5;
        private static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_reset(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern double sqlite3_column_double(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_bind_text(IntPtr stmt, int idx, byte[] text, int n, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long value);
        [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_bind_null(IntPtr stmt, int idx);

        private IntPtr _db;

        private Sqlite(IntPtr db) { _db = db; }

        public static Sqlite Open(string path, bool writable)
        {
            IntPtr db;
            int flags = writable ? SQLITE_OPEN_READWRITE : SQLITE_OPEN_READONLY;
            int rc = sqlite3_open_v2(Utf8(path), out db, flags, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                string msg = db != IntPtr.Zero ? Marshal.PtrToStringAnsi(sqlite3_errmsg(db)) : "rc " + rc;
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                throw new InvalidOperationException("cannot open " + path + ": " + msg);
            }
            return new Sqlite(db);
        }

        // Every row as an array of column values: long, double, string or null.
        public List<object[]> Query(string sql, params object[] args)
        {
            var rows = new List<object[]>();
            ForEach(sql, args, r => rows.Add(r));
            return rows;
        }

        public void ForEach(string sql, object[] args, Action<object[]> onRow)
        {
            IntPtr stmt = Prepare(sql);
            try
            {
                Bind(stmt, args);
                int cols = sqlite3_column_count(stmt);
                while (true)
                {
                    int rc = sqlite3_step(stmt);
                    if (rc == SQLITE_DONE) break;
                    if (rc != SQLITE_ROW) throw Error("step");
                    var row = new object[cols];
                    for (int c = 0; c < cols; c++) row[c] = Column(stmt, c);
                    onRow(row);
                }
            }
            finally { sqlite3_finalize(stmt); }
        }

        public object Scalar(string sql, params object[] args)
        {
            List<object[]> rows = Query(sql, args);
            return rows.Count > 0 && rows[0].Length > 0 ? rows[0][0] : null;
        }

        public void Exec(string sql, params object[] args)
        {
            IntPtr stmt = Prepare(sql);
            try
            {
                Bind(stmt, args);
                int rc = sqlite3_step(stmt);
                if (rc != SQLITE_DONE && rc != SQLITE_ROW) throw Error("exec");
            }
            finally { sqlite3_finalize(stmt); }
        }

        // One prepared statement run once per row, inside a single transaction.
        public void ExecMany(string sql, IEnumerable<object[]> rows)
        {
            Exec("BEGIN");
            IntPtr stmt = Prepare(sql);
            try
            {
                foreach (object[] args in rows)
                {
                    sqlite3_reset(stmt);
                    Bind(stmt, args);
                    int rc = sqlite3_step(stmt);
                    if (rc != SQLITE_DONE) throw Error("insert");
                }
            }
            finally { sqlite3_finalize(stmt); }
            Exec("COMMIT");
        }

        private IntPtr Prepare(string sql)
        {
            IntPtr stmt;
            byte[] b = Utf8(sql);
            if (sqlite3_prepare_v2(_db, b, b.Length, out stmt, IntPtr.Zero) != SQLITE_OK) throw Error("prepare: " + sql);
            return stmt;
        }

        private static void Bind(IntPtr stmt, object[] args)
        {
            if (args == null) return;
            for (int i = 0; i < args.Length; i++)
            {
                object a = args[i];
                if (a == null) sqlite3_bind_null(stmt, i + 1);
                else if (a is string s) { byte[] b = Encoding.UTF8.GetBytes(s); sqlite3_bind_text(stmt, i + 1, b, b.Length, SQLITE_TRANSIENT); }
                else sqlite3_bind_int64(stmt, i + 1, Convert.ToInt64(a));
            }
        }

        private static object Column(IntPtr stmt, int c)
        {
            switch (sqlite3_column_type(stmt, c))
            {
                case SQLITE_NULL: return null;
                case SQLITE_INTEGER: return sqlite3_column_int64(stmt, c);
                case SQLITE_FLOAT: return sqlite3_column_double(stmt, c);
                default:
                    IntPtr p = sqlite3_column_text(stmt, c);
                    int n = sqlite3_column_bytes(stmt, c);
                    if (p == IntPtr.Zero || n == 0) return "";
                    var buf = new byte[n];
                    Marshal.Copy(p, buf, 0, n);
                    return Encoding.UTF8.GetString(buf);
            }
        }

        private Exception Error(string what)
        {
            return new InvalidOperationException("sqlite " + what + ": " + Marshal.PtrToStringAnsi(sqlite3_errmsg(_db)));
        }

        private static byte[] Utf8(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);   // NUL terminator
            return b;
        }

        public void Dispose()
        {
            if (_db != IntPtr.Zero) { sqlite3_close_v2(_db); _db = IntPtr.Zero; }
        }
    }
}
