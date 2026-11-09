using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// Reads one value from another app's SQLite database through Windows' built-in SQLite (winsqlite3.dll),
    /// opened read-only. Nothing is ever written. Used for Cursor's state.vscdb and OpenCode's opencode.db.
    /// </summary>
    internal static class SqliteRead
    {
        private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_OPEN_READONLY = 1;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        /// <summary>
        /// The first column of the first row as UTF-8 text, or null (no file, no row, or any SQLite error).
        /// <paramref name="sql"/> is always a fixed statement written in this app, never user input.
        /// </summary>
        public static string FirstValue(string dbPath, string sql)
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath)) return null;
            if (sqlite3_open_v2(Utf8Z(dbPath), out var db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
            {
                sqlite3_close(db);
                return null;
            }
            try
            {
                sqlite3_busy_timeout(db, 2000);
                if (sqlite3_prepare_v2(db, Utf8Z(sql), -1, out var stmt, IntPtr.Zero) != SQLITE_OK) return null;
                try
                {
                    if (sqlite3_step(stmt) != SQLITE_ROW) return null;
                    var n = sqlite3_column_bytes(stmt, 0);
                    var p = sqlite3_column_blob(stmt, 0);
                    if (n <= 0 || p == IntPtr.Zero) return null;
                    var bytes = new byte[n];
                    Marshal.Copy(p, bytes, 0, n);
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close(db);
            }
        }

        /// <summary>A value from a VS Code-style <c>ItemTable</c> (key/value), with surrounding quotes removed.</summary>
        public static string ItemTableValue(string dbPath, string key) =>
            FirstValue(dbPath, "SELECT value FROM ItemTable WHERE key = '" + key.Replace("'", "''") + "'")?.Trim().Trim('"');

        private static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");
    }

    /// <summary>
    /// Cursor's plan name. Since SpaceX's Cursor acquisition (Aug 2026) the X Subscription plans give Grok, Grok Build
    /// and Cursor one shared weekly pool, so Cursor's usage is shown inside the Grok item; only the tier is read here
    /// (<c>cursorAuth/stripeMembershipType</c>, e.g. "unified_ultra").
    /// </summary>
    internal static class CursorPlan
    {
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");

        /// <summary>"Ultra" for a unified (shared-pool) membership; null when Cursor is absent or on its own plan.</summary>
        public static string UnifiedTier()
        {
            try { return TierFrom(SqliteRead.ItemTableValue(DefaultPath, "cursorAuth/stripeMembershipType")); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is IOException)
            {
                return null;
            }
        }

        /// <summary>"unified_ultra" → "Ultra", "unified_pro_plus" → "Pro Plus"; anything else → null.</summary>
        public static string TierFrom(string membership)
        {
            const string prefix = "unified_";
            if (string.IsNullOrEmpty(membership) || !membership.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var parts = membership.Substring(prefix.Length).Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            for (var i = 0; i < parts.Length; i++) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1).ToLowerInvariant();
            return string.Join(" ", parts);
        }
    }
}
