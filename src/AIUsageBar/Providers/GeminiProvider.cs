using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// user status (the same call its model picker uses), and falls back to <c>agy -p "/usage" --output-format json</c>
    /// when the desktop app is closed so CLI-only sessions stay up to date.
    /// </summary>
    internal sealed class GeminiProvider : IUsageProvider
    {
        private const string Path_ = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
        private const string SummaryPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
        private const string Body = "{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}";
        private static readonly TimeSpan CliMinInterval = TimeSpan.FromMinutes(5);

        private readonly Func<AntigravityServer.Endpoint> _find;
        private readonly Func<bool> _installed;
        private readonly Func<CancellationToken, Task<string>> _cliUsage;
        private readonly HttpClient _http;
        private ServiceUsage _lastCli;
        private DateTimeOffset _lastCliAt = DateTimeOffset.MinValue;
        private string _lastPlan;

        public GeminiProvider(
            Func<AntigravityServer.Endpoint> find = null,
            Func<bool> installed = null,
            HttpMessageHandler handler = null,
            Func<CancellationToken, Task<string>> cliUsage = null)
        {
            var isProduction = find == null && handler == null && cliUsage == null;
            _find = find ?? AntigravityServer.Find;
            _installed = installed ?? (isProduction
                ? (Func<bool>)(() => AntigravityServer.IsInstalled() || AntigravityCli.IsInstalled())
                : AntigravityServer.IsInstalled);
            _cliUsage = cliUsage ?? (isProduction ? (Func<CancellationToken, Task<string>>)AntigravityCli.RunUsageAsync : null);
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

            ServiceUsage failure = null;
            if (ep != null)
            {
                foreach (var port in ep.Ports)
                {
                    try
                    {
                        var (sumStatus, sumBody) = await PostAsync(port, ep.Csrf, SummaryPath, ct).ConfigureAwait(false);
                        var summaryUsage = sumStatus >= 200 && sumStatus <= 299
                            ? GeminiParser.ParseSummary(sumBody, _lastPlan, DateTimeOffset.Now)
                            : null;
                        if (summaryUsage != null && _lastPlan != null) return summaryUsage;

                        var (status, body) = await PostAsync(port, ep.Csrf, Path_, ct).ConfigureAwait(false);
                        if (status < 200 || status > 299)
                        {
                            if (summaryUsage != null) return summaryUsage;
                            failure = Io.Failure("Gemini", Tag, status);
                            continue; // the other port is the extension server
                        }
                        var statusUsage = GeminiParser.Parse(body, DateTimeOffset.Now);
                        if (!string.IsNullOrEmpty(statusUsage.Plan)) _lastPlan = statusUsage.Plan;
                        if (summaryUsage != null)
                        {
                            summaryUsage.Plan = _lastPlan;
                            return summaryUsage;
                        }
                        return statusUsage;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is FormatException)
                    {
                        failure = Io.Error("Gemini", Tag, ex);
                    }
                }
            }

            var cli = await FromCliAsync(ct).ConfigureAwait(false);
            if (cli != null) return cli;

            if (failure != null) return failure;
            return _installed()
                ? new ServiceUsage { Service = "Gemini", Tag = Tag, Status = ServiceStatus.Error, Error = "NOT RUNNING" }
                : new ServiceUsage { Service = "Gemini", Tag = Tag, Status = ServiceStatus.NotInstalled };
        }

        private async Task<(int status, string body)> PostAsync(int port, string csrf, string path, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://127.0.0.1:" + port + path))
            {
                req.Headers.TryAddWithoutValidation("X-Codeium-Csrf-Token", csrf);
                req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
                req.Content = new StringContent(Body, Encoding.UTF8, "application/json");
                using (var resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var body = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ((int)resp.StatusCode, body);
                }
            }
        }

        private async Task<ServiceUsage> FromCliAsync(CancellationToken ct)
        {
            if (_cliUsage == null) return null;
            var now = DateTimeOffset.Now;
            if (_lastCli != null && now - _lastCliAt < CliMinInterval) return _lastCli;
            try
            {
                var raw = await _cliUsage(ct).ConfigureAwait(false);
                if (string.IsNullOrEmpty(raw)) return null;
                var parsed = GeminiParser.ParseCli(raw, DateTimeOffset.Now);
                if (parsed == null) return null;
                if (parsed.Plan == null) parsed.Plan = _lastPlan;
                _lastCli = parsed;
                _lastCliAt = DateTimeOffset.Now;
                return parsed;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException) && !ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Finds Antigravity's running language server: its CSRF token (from the process's own command line or its
    /// hub daemon file matching that PID) and the ports that very process listens on, so the token is only ever sent
    /// to Antigravity itself.
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
        private static readonly Regex AppDataArg = new Regex(@"--app_data_dir[ =](\S+)", RegexOptions.CultureInvariant);

        public static bool IsInstalled() => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe"));

        internal static bool IsAntigravityProcess(string exePath, string commandLine) =>
            !string.IsNullOrEmpty(exePath) && !string.IsNullOrEmpty(commandLine) &&
            exePath.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase) && IdeArg.IsMatch(commandLine);

        internal static string AppDataNameFrom(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return "antigravity";
            var m = AppDataArg.Match(commandLine);
            return m.Success ? m.Groups[1].Value.Trim('"') : "antigravity";
        }

        /// <summary>The CSRF token when the process is Antigravity's language server with --csrf_token; null otherwise.</summary>
        internal static string CsrfFrom(string exePath, string commandLine)
        {
            if (!IsAntigravityProcess(exePath, commandLine)) return null;
            var m = CsrfArg.Match(commandLine);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Antigravity 2.31+ (hub mode) omits --csrf_token from the command line and writes
        /// <c>~/.gemini/antigravity/daemon/hub_*.json</c> with <c>pid</c>, <c>httpsPort</c>, <c>httpPort</c> and <c>csrfToken</c>.
        /// Matches only the verified language_server PID and ports that PID actually listens on.
        /// </summary>
        internal static Endpoint EndpointFromDaemon(int pid, IReadOnlyList<int> listeningPorts, string daemonDir)
        {
            if (listeningPorts == null || listeningPorts.Count == 0 || string.IsNullOrEmpty(daemonDir)) return null;
            try
            {
                if (!Directory.Exists(daemonDir)) return null;
                var files = new DirectoryInfo(daemonDir)
                    .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc);
                foreach (var f in files)
                {
                    try
                    {
                        var root = Json.Parse(Io.ReadShared(f.FullName));
                        if ((int?)Json.Num(Json.Get(root, "pid")) != pid) continue;
                        var csrf = Json.Str(Json.Get(root, "csrfToken"));
                        if (string.IsNullOrEmpty(csrf)) continue;
                        var httpsPort = (int?)Json.Num(Json.Get(root, "httpsPort"));
                        var httpPort = (int?)Json.Num(Json.Get(root, "httpPort"));

                        var ordered = new List<int>();
                        if (httpsPort.HasValue && listeningPorts.Contains(httpsPort.Value)) ordered.Add(httpsPort.Value);
                        foreach (var p in listeningPorts)
                            if (p != httpsPort && p != httpPort && !ordered.Contains(p)) ordered.Add(p);
                        if (ordered.Count > 0) return new Endpoint(csrf, ordered);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return null;
        }

        public static Endpoint Find()
        {
            using (var search = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'language_server.exe'"))
            using (var found = search.Get())
                foreach (ManagementObject p in found)
                    using (p)
                    {
                        var exe = p["ExecutablePath"] as string;
                        var cmd = p["CommandLine"] as string;
                        if (!IsAntigravityProcess(exe, cmd)) continue;
                        var pid = Convert.ToInt32(p["ProcessId"]);
                        var ports = ListeningPorts(pid);
                        if (ports.Count == 0) continue;

                        var csrf = CsrfFrom(exe, cmd);
                        if (csrf != null) return new Endpoint(csrf, ports);

                        var daemonDir = Path.Combine(Io.DefaultHome, ".gemini", AppDataNameFrom(cmd), "daemon");
                        var ep = EndpointFromDaemon(pid, ports, daemonDir);
                        if (ep != null) return ep;
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

    /// <summary>Queries Antigravity CLI (<c>agy -p "/usage" --output-format json</c>, zero tokens) when desktop Antigravity is closed.</summary>
    internal static class AntigravityCli
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        public static string ExePath()
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
            if (File.Exists(local)) return local;
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var part in pathEnv.Split(Path.PathSeparator))
            {
                var dir = part.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    var cand = Path.Combine(dir, "agy.exe");
                    if (File.Exists(cand)) return cand;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static bool IsInstalled() => ExePath() != null;

        public static async Task<string> RunUsageAsync(CancellationToken ct)
        {
            var exe = ExePath();
            if (exe == null) return null;
            using (var p = Process.Start(new ProcessStartInfo(exe, "-p \"/usage\" --output-format json")
            {
                WorkingDirectory = Io.DefaultHome,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            }))
            {
                if (p == null) return null;
                _ = p.StandardError.BaseStream.CopyToAsync(Stream.Null);
                var readTask = p.StandardOutput.ReadToEndAsync();
                var done = await Task.WhenAny(readTask, Task.Delay(Timeout, ct)).ConfigureAwait(false);
                if (done != readTask)
                {
                    KillTree(p);
                    return null;
                }
                var output = await readTask.ConfigureAwait(false);
                if (!p.WaitForExit(2000)) KillTree(p);
                return output;
            }
        }

        private static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                using (var kill = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/T /F /PID " + p.Id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }))
                    kill?.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}
