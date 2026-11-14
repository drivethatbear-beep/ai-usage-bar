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
    /// (membership type, e.g. "unified_ultra"). See <see cref="CursorPlanSource"/> for where it comes from.
    /// </summary>
    internal static class CursorPlan
    {
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");

        /// <summary>Cursor's locally cached membership (<c>cursorAuth/stripeMembershipType</c>), or null.</summary>
        public static string LocalMembership() => ReadItem("cursorAuth/stripeMembershipType");

        /// <summary>Cursor's signed-in access token (read-only, never refreshed), or null.</summary>
        public static string AccessToken() => ReadItem("cursorAuth/accessToken");

        private static string ReadItem(string key)
        {
            try { return SqliteRead.ItemTableValue(DefaultPath, key); }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is IOException)
            {
                return null;
            }
        }

        /// <summary>The user id in a Cursor access token's <c>sub</c> claim ("auth0|user_…" → "user_…"), or null.</summary>
        public static string UserIdFromJwt(string jwt)
        {
            var parts = jwt?.Split('.');
            if (parts == null || parts.Length < 2) return null;
            try
            {
                var b64 = parts[1].Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                var sub = Json.Str(Json.Get(Json.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))), "sub"));
                if (string.IsNullOrEmpty(sub)) return null;
                var i = sub.LastIndexOf('|');
                return i >= 0 ? sub.Substring(i + 1) : sub;
            }
            catch (FormatException)
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

    /// <summary>
    /// The unified tier, asked from cursor.com. Cursor only rewrites its local copy of the membership when it signs in
    /// again, so after a move to an X Subscription plan state.vscdb can keep saying "ultra" while the server already
    /// says "unified_ultra". The server answer is kept for 30 minutes; when it cannot be had (offline, signed out)
    /// the last server answer is used, and before any, the local value.
    /// </summary>
    internal sealed class CursorPlanSource
    {
        private const string Url = "https://cursor.com/api/usage-summary";
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

        private readonly Func<string> _token;
        private readonly Func<string> _localMembership;
        private readonly Func<DateTimeOffset> _now;
        private readonly HttpClient _http;
        private string _remoteTier;
        private bool _remoteKnown;
        private DateTimeOffset _attemptAt = DateTimeOffset.MinValue;

        public CursorPlanSource(Func<string> token = null, Func<string> localMembership = null,
            HttpMessageHandler handler = null, Func<DateTimeOffset> now = null)
        {
            _token = token ?? CursorPlan.AccessToken;
            _localMembership = localMembership ?? CursorPlan.LocalMembership;
            _now = now ?? (() => DateTimeOffset.Now);
            // The session cookie is set by hand (UseCookies=true would drop it) and must not follow a redirect.
            _http = Io.Client(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        }

        /// <summary>"Ultra" on a unified (shared-pool) plan; null when Cursor is absent or on its own plan.</summary>
        public async Task<string> TierAsync(CancellationToken ct)
        {
            var now = _now();
            if (now - _attemptAt >= (_remoteKnown ? Ttl : RetryAfter))
            {
                _attemptAt = now;
                var membership = await RemoteMembershipAsync(ct).ConfigureAwait(false);
                if (membership != null)
                {
                    _remoteTier = CursorPlan.TierFrom(membership);
                    _remoteKnown = true;
                }
            }
            if (_remoteKnown) return _remoteTier;
            try { return CursorPlan.TierFrom(_localMembership()); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { return null; }
        }

        private async Task<string> RemoteMembershipAsync(CancellationToken ct)
        {
            string token;
            try { token = _token(); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { return null; }
            var uid = CursorPlan.UserIdFromJwt(token);
            if (string.IsNullOrEmpty(token) || uid == null) return null;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, Url))
                {
                    req.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + uid + "%3A%3A" + token);
                    req.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
                    using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode || resp.Content == null) return null;
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var m = Json.Str(Json.Get(Json.Parse(body), "membershipType"));
                        return string.IsNullOrEmpty(m) ? null : m;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
