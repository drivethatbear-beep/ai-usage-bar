using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    /// <summary>
    /// Gemini quota as shown in Google Antigravity: asks Antigravity's own language server on 127.0.0.1 for the
    /// user status (the same call its model picker uses). Nothing leaves this PC from here, no token is read from
    /// disk, and nothing is written. Works while Antigravity is running; otherwise the last values stay up.
    /// </summary>
    internal sealed class GeminiProvider : IUsageProvider
    {
        private const string Path_ = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
        private const string Body = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}";

        private readonly Func<AntigravityServer.Endpoint> _find;
        private readonly Func<bool> _installed;
        private readonly HttpClient _http;

        public GeminiProvider(Func<AntigravityServer.Endpoint> find = null, Func<bool> installed = null, HttpMessageHandler handler = null)
        {
            _find = find ?? AntigravityServer.Find;
            _installed = installed ?? AntigravityServer.IsInstalled;
            _http = Io.Client(handler ?? CreateHandler());
        }

        public string Tag => "GM";

        /// <summary>
        /// The language server uses a self-signed certificate on 127.0.0.1, so certificate errors are accepted for
        /// loopback addresses only. Redirects are never followed (the CSRF token would go along).
        /// </summary>
        internal static HttpClientHandler CreateHandler() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (req, cert, chain, errors) => CertificateOk(req.RequestUri, errors),
        };

        internal static bool CertificateOk(Uri uri, SslPolicyErrors errors) => errors == SslPolicyErrors.None || (uri != null && uri.IsLoopback);

        public async Task<ServiceUsage> FetchAsync(CancellationToken ct)
        {
            AntigravityServer.Endpoint ep;
            try { ep = _find(); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { ep = null; }
            if (ep == null)
                return _installed()
                    ? new ServiceUsage { Service = "Gemini", Tag = Tag, Status = ServiceStatus.Error, Error = "NOT RUNNING" }
                    : new ServiceUsage { Service = "Gemini", Tag = Tag, Status = ServiceStatus.NotInstalled };

            ServiceUsage failure = null;
            foreach (var port in ep.Ports)
            {
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Post, "https://127.0.0.1:" + port + Path_))
                    {
                        req.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", ep.Csrf);
                        req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                        req.Content = new StringContent(Body, Encoding.UTF8, "application/json");
                        using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            var status = (int)resp.StatusCode;
                            if (status < 200 || status > 299)
                            {
                                failure = Io.Failure("Gemini", Tag, status);
                                continue; // the other port is the extension server
                            }
                            var body = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            return GeminiParser.Parse(body, DateTimeOffset.Now);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
                {
                    failure = Io.Error("Gemini", Tag, ex);
                }
            }
            return failure ?? new ServiceUsage { Service = "Gemini", Tag = Tag, Status = ServiceStatus.Error, Error = "NOT RUNNING" };
        }
    }

    /// <summary>
    /// Finds Antigravity's running language server: its CSRF token (from the process's own command line) and the
    /// ports that very process listens on, so the token is only ever sent to Antigravity itself.
    /// </summary>
    internal static class AntigravityServer
    {
        internal sealed class Endpoint
        {
            public Endpoint(string csrf, IReadOnlyList<int> ports)
            {
                Csrf = csrf;
                Ports = ports;
            }

            public string Csrf { get; }
            public IReadOnlyList<int> Ports { get; }
        }

        private const string ExeSuffix = @"\antigravity\resources\bin\language_server.exe";
        private static readonly Regex CsrfArg = new Regex(@"--csrf_token[ =](\S+)", RegexOptions.CultureInvariant);
        private static readonly Regex IdeArg = new Regex(@"--override_ide_name[ =]antigravity(\s|$)", RegexOptions.CultureInvariant);

        public static bool IsInstalled() => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe"));

        /// <summary>The CSRF token when the process is Antigravity's language server; null for anything else.</summary>
        internal static string CsrfFrom(string exePath, string commandLine)
        {
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(commandLine)) return null;
            if (!exePath.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase) || !IdeArg.IsMatch(commandLine)) return null;
            var m = CsrfArg.Match(commandLine);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static Endpoint Find()
        {
            using (var search = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'language_server.exe'"))
            using (var found = search.Get())
                foreach (ManagementObject p in found)
                    using (p)
                    {
                        var csrf = CsrfFrom(p["ExecutablePath"] as string, p["CommandLine"] as string);
                        if (csrf == null) continue;
                        var ports = ListeningPorts(Convert.ToInt32(p["ProcessId"]));
                        if (ports.Count > 0) return new Endpoint(csrf, ports);
                    }
            return null;
        }

        // ---- IPv4 listening ports owned by a process (iphlpapi) ----
        private const int AF_INET = 2, TCP_TABLE_OWNER_PID_LISTENER = 3, ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, int reserved);

        private static List<int> ListeningPorts(int pid)
        {
            var ports = new List<int>();
            var size = 0;
            if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != ERROR_INSUFFICIENT_BUFFER) return ports;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return ports;
                // MIB_TCPTABLE_OWNER_PID: DWORD count, then rows of 6 DWORDs
                // (state, localAddr, localPort, remoteAddr, remotePort, owningPid).
                var count = Marshal.ReadInt32(buf);
                for (var i = 0; i < count; i++)
                {
                    var row = buf + 4 + i * 24;
                    if (Marshal.ReadInt32(row, 20) != pid) continue;
                    var raw = Marshal.ReadInt32(row, 8);
                    var port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF); // network byte order
                    if (!ports.Contains(port)) ports.Add(port);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            ports.Sort();
            return ports;
        }
    }
}
