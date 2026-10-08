using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    internal interface IUsageProvider
    {
        string Tag { get; }
        Task<ServiceUsage> FetchAsync(CancellationToken ct);
    }

    /// <summary>Shared I/O for providers: shared-read files and a GET with a 10 s timeout.</summary>
    internal static class Io
    {
        public const string UserAgent = "AIUsageBar/1.0";

        static Io()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public static string DefaultHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>Reads a file another process may be writing. Never takes a write lock.</summary>
        public static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs, Encoding.UTF8))
                return r.ReadToEnd();
        }

        /// <summary>
        /// Complete lines from the last <paramref name="maxBytes"/> of a file (shared read). A line cut by the
        /// window's start is dropped; empty lines are skipped. Never loads the whole file.
        /// </summary>
        public static string[] ReadTailLines(string path, int maxBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var start = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                var read = 0;
                while (read < buf.Length)
                {
                    var n = fs.Read(buf, read, buf.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                var text = Encoding.UTF8.GetString(buf, 0, read);
                var lines = text.Split((char)10);
                var list = new System.Collections.Generic.List<string>(lines.Length);
                for (var i = start > 0 ? 1 : 0; i < lines.Length; i++)
                {
                    var l = lines[i].TrimEnd((char)13);
                    if (l.Length > 0) list.Add(l);
                }
                return list.ToArray();
            }
        }

        /// <summary>
        /// Redirects are never followed: the endpoints are fixed and do not redirect, and following one would
        /// carry the bearer token or session cookie to wherever it points. A 3xx shows up as an HTTP error instead.
        /// </summary>
        internal static HttpClientHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false };

        public static HttpClient Client(HttpMessageHandler handler)
        {
            var c = handler == null ? new HttpClient(CreateHandler()) : new HttpClient(handler, disposeHandler: false);
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return c;
        }

        public sealed class Response
        {
            public int Status;
            public string Body;
        }

        public static async Task<Response> GetAsync(HttpClient client, string url, string bearer, Action<HttpRequestHeaders> headers, CancellationToken ct)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                headers?.Invoke(req.Headers);
                using (var resp = await client.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var body = resp.Content == null ? "" : await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new Response { Status = (int)resp.StatusCode, Body = body };
                }
            }
        }

        /// <summary>Maps a non-2xx status to a status-only ServiceUsage.</summary>
        public static ServiceUsage Failure(string service, string tag, int status)
        {
            var u = new ServiceUsage { Service = service, Tag = tag };
            if (status == 401 || status == 403) u.Status = ServiceStatus.NeedsLogin;
            else
            {
                u.Status = ServiceStatus.Error;
                u.Error = status == 429 ? "RATE LIMIT" : "HTTP " + status;
            }
            return u;
        }

        public static ServiceUsage Error(string service, string tag, Exception ex) =>
            new ServiceUsage { Service = service, Tag = tag, Status = ServiceStatus.Error, Error = ex is TaskCanceledException ? "TIMEOUT" : "OFFLINE" };
    }
}
