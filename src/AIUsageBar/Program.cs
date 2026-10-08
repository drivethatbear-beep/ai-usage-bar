using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AIUsageBar.UI;

namespace AIUsageBar
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--quit")
            {
                // Used by the installer before upgrade/uninstall: ask a running copy to exit cleanly.
                QuitSignal.Signal(QuitSignal.DefaultName);
                return;
            }
            var startup = Startup.ParseArgs(args);
            if (startup.HasValue)
            {
                // Used by the installer: register / remove the sign-in task, then exit.
                try { Environment.ExitCode = Startup.Set(startup.Value) ? 0 : 1; }
                catch (Exception ex) { Log(ex); Environment.ExitCode = 1; }
                return;
            }
            using (var mutex = new Mutex(true, @"Local\AIUsageBar", out var first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);
                Application.Run(new BarApp());
            }
        }

        /// <summary>Appends to %APPDATA%\AIUsageBar\error.log; never throws.</summary>
        public static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                var dir = Path.GetDirectoryName(Settings.DefaultPath);
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
