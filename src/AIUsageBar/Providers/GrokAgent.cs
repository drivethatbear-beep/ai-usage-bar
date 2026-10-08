using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIUsageBar.Core;

namespace AIUsageBar.Providers
{
    internal interface IGrokRpc
    {
        /// <summary>Calls a parameterless ACP method; returns the raw JSON-RPC response line.</summary>
        Task<string> CallAsync(string method, CancellationToken ct);
    }

    /// <summary>
    /// One resident <c>grok agent stdio</c> process (≈40 MB, no children, ≈0.2 s per call). The CLI keeps
    /// handling its own sign-in; we only send JSON-RPC requests. Closing stdin ends it, so it also exits
    /// if this app dies. Restarted on demand after any failure.
    /// </summary>
    internal sealed class GrokAgent : IGrokRpc, IDisposable
    {
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

        private readonly string _exe;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private Process _p;
        private int _nextId;
        private int _waitingId;
        private TaskCompletionSource<string> _waiting;

        public GrokAgent(string exe)
        {
            _exe = exe;
        }

        public static bool IsResponse(string line, int id)
        {
            try
            {
                var root = Json.Parse(line);
                return Json.Num(Json.Get(root, "id")) == id && (Json.Get(root, "result") != null || Json.Get(root, "error") != null);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public async Task<string> CallAsync(string method, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_p == null || _p.HasExited)
                {
                    Start();
                    await SendAsync("initialize", "{\"protocolVersion\":1,\"clientCapabilities\":{}}", ct).ConfigureAwait(false);
                }
                return await SendAsync(method, "{}", ct).ConfigureAwait(false);
            }
            catch
            {
                Stop(); // a fresh process next time
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        private void Start()
        {
            Stop();
            var p = Process.Start(new ProcessStartInfo(_exe, "agent stdio")
            {
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            _p = p;
            _ = p.StandardError.BaseStream.CopyToAsync(Stream.Null); // drain so the child never blocks
            _ = ReadLoopAsync(p);
        }

        private async Task ReadLoopAsync(Process p)
        {
            try
            {
                string line;
                while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    var w = Volatile.Read(ref _waiting);
                    if (w != null && IsResponse(line, Volatile.Read(ref _waitingId))) w.TrySetResult(line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
            {
            }
            if (ReferenceEquals(p, _p)) Volatile.Read(ref _waiting)?.TrySetException(new IOException("grok agent exited"));
        }

        private async Task<string> SendAsync(string method, string paramsJson, CancellationToken ct)
        {
            var id = ++_nextId;
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _waitingId, id);
            Volatile.Write(ref _waiting, tcs);
            var msg = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}";
            await _p.StandardInput.WriteLineAsync(msg).ConfigureAwait(false);
            await _p.StandardInput.FlushAsync().ConfigureAwait(false);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(CallTimeout, ct)).ConfigureAwait(false);
            if (done != tcs.Task) throw new TimeoutException("grok agent did not answer " + method);
            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>Closing stdin lets the agent exit on its own; anything left is killed with its children.</summary>
        private void Stop()
        {
            var p = _p;
            _p = null;
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    try { p.StandardInput.Close(); } catch (IOException) { }
                    if (!p.WaitForExit(2000))
                        using (var kill = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/T /F /PID " + p.Id)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }))
                            kill?.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        public void Dispose()
        {
            _gate.Wait(TimeSpan.FromSeconds(5));
            Stop();
        }
    }
}
