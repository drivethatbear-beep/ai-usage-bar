using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// Cursor: reads the signed-in access token from Cursor's state database (read-only, never refreshed)
    /// and queries cursor.com/api/usage-summary with Cursor's web session cookie.
    /// </summary>
    internal sealed class CursorProvider : IUsageProvider
    {
        private const string Url = "https://cursor.com/api/usage-summary";
        private readonly Func<string> _token;
        private readonly HttpClient _http;

        public CursorProvider(Func<string> token = null, HttpMessageHandler handler = null)
        {
            _token = token ?? CursorTokenStore.ReadAccessToken;
            _http = Io.Client(handler ?? CreateHandler());
        }

        /// <summary>
        /// A handler that sends our Cookie header as-is (UseCookies=true would silently drop it) and never follows
        /// redirects (the hand-set session cookie would go along to the new address).
        /// </summary>
        internal static HttpClientHandler CreateHandler() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };

        public string Tag => "CU";

        public async Task<ServiceUsage> FetchAsync(CancellationToken ct)
        {
            string token;
            try { token = _token(); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { token = null; }
            var uid = CursorParser.UserIdFromJwt(token);
            if (string.IsNullOrEmpty(token) || uid == null)
                return new ServiceUsage { Service = "Cursor", Tag = Tag, Status = ServiceStatus.NotInstalled };

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, Url))
                {
                    req.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + uid + "%3A%3A" + token);
                    req.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        var status = (int)resp.StatusCode;
                        if (status < 200 || status > 299) return Io.Failure("Cursor", Tag, status);
                        var body = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return CursorParser.Parse(body, DateTimeOffset.Now);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
            {
                return Io.Error("Cursor", Tag, ex);
            }
        }
    }

    /// <summary>
    /// Reads one value from Cursor's VS Code-style state database (%APPDATA%\Cursor\User\globalStorage\state.vscdb)
    /// through Windows' built-in SQLite (winsqlite3.dll), opened read-only. Nothing is ever written.
    /// </summary>
    internal static class CursorTokenStore
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

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");

        public static string ReadAccessToken() => ReadValue(DefaultPath, "cursorAuth/accessToken");

        public static string ReadValue(string dbPath, string key)
        {
            if (!File.Exists(dbPath)) return null;
            if (sqlite3_open_v2(Utf8Z(dbPath), out var db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
            {
                sqlite3_close(db);
                return null;
            }
            try
            {
                sqlite3_busy_timeout(db, 2000);
                // The key is a fixed literal chosen by this app, never user input.
                var sql = Utf8Z("SELECT value FROM ItemTable WHERE key = '" + key.Replace("'", "''") + "'");
                if (sqlite3_prepare_v2(db, sql, -1, out var stmt, IntPtr.Zero) != SQLITE_OK) return null;
                try
                {
                    if (sqlite3_step(stmt) != SQLITE_ROW) return null;
                    var n = sqlite3_column_bytes(stmt, 0);
                    var p = sqlite3_column_blob(stmt, 0);
                    if (n <= 0 || p == IntPtr.Zero) return null;
                    var bytes = new byte[n];
                    Marshal.Copy(p, bytes, 0, n);
                    return Encoding.UTF8.GetString(bytes).Trim().Trim('"');
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

        private static byte[] Utf8Z(string s) => Encoding.UTF8.GetBytes(s + "\0");
    }
}
