using System.Runtime.InteropServices;

namespace MultiClaude;

/// <summary>Small UTF-8, parameterized interface to Windows' built-in SQLite library.</summary>
internal sealed class Sqlite : IDisposable
{
    private const int Ok = 0, Row = 100, Done = 101;
    private IntPtr db;

    private static class Native
    {
        private const string Dll = "winsqlite3.dll";
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close(IntPtr db);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int millis);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text16(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPWStr)] string value, int bytes, IntPtr destructor);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr stmt, int index, long n);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr stmt, int index);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr stmt);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr stmt);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_count(IntPtr stmt);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_type(IntPtr stmt, int i);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text16(IntPtr stmt, int i);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes16(IntPtr stmt, int i);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_last_insert_rowid(IntPtr db);
    }

    public Sqlite(string file, bool readOnly = false)
    {
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" })
            StoragePaths.RequireUnsharedFile(file + suffix);
        try
        {
            Verify(Native.sqlite3_open_v2(file, out db, readOnly ? 1 : 6, IntPtr.Zero));
            Verify(Native.sqlite3_busy_timeout(db, 5000));
            Run("PRAGMA trusted_schema=OFF");
            if (!readOnly) Run("PRAGMA foreign_keys=ON");
        }
        catch { Dispose(); throw; }
    }

    public long LastId => Native.sqlite3_last_insert_rowid(db);

    public T Transaction<T>(Func<T> operation)
    {
        Run("BEGIN IMMEDIATE");
        try
        {
            var result = operation();
            Run("COMMIT");
            return result;
        }
        catch { Run("ROLLBACK"); throw; }
    }

    public void Run(string sql, params object?[] parameters) => Query(sql, parameters);
    public string? Scalar(string sql, params object?[] parameters) => Query(sql, parameters).FirstOrDefault()?[0];

    public List<string?[]> Query(string sql, params object?[] parameters)
    {
        Verify(Native.sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero));
        try
        {
            for (int k = 0; k < parameters.Length; k++)
            {
                int i = k + 1;
                int result = parameters[k] switch
                {
                    null => Native.sqlite3_bind_null(statement, i),
                    bool b => Native.sqlite3_bind_int64(statement, i, b ? 1 : 0),
                    int n => Native.sqlite3_bind_int64(statement, i, n),
                    long n => Native.sqlite3_bind_int64(statement, i, n),
                    string s => Native.sqlite3_bind_text16(statement, i, s, checked(s.Length * 2), new IntPtr(-1)),
                    _ => throw new ArgumentException("Unsupported SQLite parameter.")
                };
                Verify(result);
            }
            var rows = new List<string?[]>();
            int step;
            while ((step = Native.sqlite3_step(statement)) == Row)
            {
                var values = new string?[Native.sqlite3_column_count(statement)];
                for (int i = 0; i < values.Length; i++)
                {
                    if (Native.sqlite3_column_type(statement, i) is 4 or 5) continue;
                    var pointer = Native.sqlite3_column_text16(statement, i);
                    values[i] = Marshal.PtrToStringUni(pointer, Native.sqlite3_column_bytes16(statement, i) / 2);
                }
                rows.Add(values);
            }
            if (step != Done) Verify(step);
            return rows;
        }
        finally { Native.sqlite3_finalize(statement); }
    }

    private void Verify(int result)
    {
        if (result != Ok)
            throw new IOException(db == IntPtr.Zero ? $"SQLite error {result}." : Marshal.PtrToStringUTF8(Native.sqlite3_errmsg(db)));
    }

    public void Dispose()
    {
        if (db != IntPtr.Zero) Native.sqlite3_close(db);
        db = IntPtr.Zero;
    }
}
