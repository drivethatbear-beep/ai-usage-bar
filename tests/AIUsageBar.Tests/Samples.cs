using System;
using System.IO;

namespace AIUsageBar.Tests
{
    internal static class Samples
    {
        public static string Read(string name) =>
            File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", name));
    }
}
