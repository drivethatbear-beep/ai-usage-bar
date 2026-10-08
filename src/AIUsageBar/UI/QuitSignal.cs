using System;
using System.Threading;

namespace AIUsageBar.UI
{
    /// <summary>
    /// Named event used by the installer (via <c>AIUsageBar.exe --quit</c>) to ask a running copy to exit
    /// cleanly, so it can restore the Win10 task band before being replaced or removed.
    /// </summary>
    internal static class QuitSignal
    {
        public const string DefaultName = @"Local\AIUsageBar.Quit";

        public static IDisposable Listen(string name, Action onQuit)
        {
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            var reg = ThreadPool.RegisterWaitForSingleObject(ev, (s, timedOut) => onQuit(), null, Timeout.Infinite, executeOnlyOnce: true);
            return new Registration(ev, reg);
        }

        /// <summary>Returns false when no running copy is listening.</summary>
        public static bool Signal(string name)
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var ev)) return false;
            using (ev) return ev.Set();
        }

        private sealed class Registration : IDisposable
        {
            private readonly EventWaitHandle _ev;
            private readonly RegisteredWaitHandle _reg;

            public Registration(EventWaitHandle ev, RegisteredWaitHandle reg)
            {
                _ev = ev;
                _reg = reg;
            }

            public void Dispose()
            {
                _reg.Unregister(null);
                _ev.Dispose();
            }
        }
    }
}
