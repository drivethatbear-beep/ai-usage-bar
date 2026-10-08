using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIUsageBar.Tests
{
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aiub-" + Guid.NewGuid().ToString("N"));

        public TempDir() { Directory.CreateDirectory(Path); }

        public string Write(string rel, string content)
        {
            var p = System.IO.Path.Combine(Path, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            File.WriteAllText(p, content);
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
        public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> f) { _f = f; }

        public static FakeHandler Status(HttpStatusCode code, string body = "") =>
            new FakeHandler(_ => new HttpResponseMessage(code) { Content = new StringContent(body) });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_f(request));
        }
    }
}
