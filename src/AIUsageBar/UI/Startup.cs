using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AIUsageBar.UI
{
    /// <summary>
    /// "Start with Windows" as a Task Scheduler task that fires at this user's sign-in. Unlike the HKCU Run key,
    /// logon tasks are not held back by Windows' startup-app throttling (the Run key started this app ~4 min
    /// after boot on a machine with many startup apps). Needs no admin rights: the task runs as the same user.
    /// </summary>
    internal static class Startup
    {
        public const string TaskName = "AIUsageBar";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsEnabled() => Schtasks("/Query /TN \"" + TaskName + "\"") == 0;

        public static bool Set(bool enabled)
        {
            RemoveLegacyRunValue();
            if (!enabled) return Schtasks("/Delete /TN \"" + TaskName + "\" /F") == 0 || !IsEnabled();

            var xmlPath = Path.Combine(Path.GetTempPath(), "AIUsageBar-startup-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xmlPath, TaskXml(Application.ExecutablePath, WindowsIdentity.GetCurrent().Name), Encoding.Unicode);
                return Schtasks("/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F") == 0;
            }
            finally
            {
                try { File.Delete(xmlPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>"--startup on|off" (used by the installer); null for any other command line.</summary>
        public static bool? ParseArgs(string[] args)
        {
            if (args == null || args.Length < 2 || args[0] != "--startup") return null;
            if (args[1] == "on") return true;
            if (args[1] == "off") return false;
            return null;
        }

        internal static string TaskXml(string exePath, string userId)
        {
            string E(string v) => SecurityElement.Escape(v);
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>AI Usage Bar: start at sign-in</Description></RegistrationInfo>\r\n" +
                "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + E(userId) + "</UserId></LogonTrigger></Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + E(userId) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>5</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + E(exePath) + "</Command></Exec></Actions>\r\n" +
                "</Task>\r\n";
        }

        /// <summary>Earlier versions used the Run key; drop it so the app is not started twice.</summary>
        private static void RemoveLegacyRunValue()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                    if (k?.GetValue(TaskName) != null) k.DeleteValue(TaskName);
            }
            catch (Exception ex) when (ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException)
            {
            }
        }

        private static int Schtasks(string args)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                return p.WaitForExit(15000) ? p.ExitCode : -1;
            }
        }
    }
}
