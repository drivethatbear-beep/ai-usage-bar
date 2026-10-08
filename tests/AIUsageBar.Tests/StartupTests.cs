using AIUsageBar.UI;
using Xunit;

namespace AIUsageBar.Tests
{
    public class StartupTests
    {
        [Fact]
        public void Task_Xml_Runs_At_This_Users_Logon_Without_Delay()
        {
            var xml = Startup.TaskXml(@"C:\Users\a&b\AppData\Local\Programs\AIUsageBar\AIUsageBar.exe", @"PC\a&b");
            Assert.Contains("<LogonTrigger>", xml);
            Assert.Contains(@"<UserId>PC\a&amp;b</UserId>", xml);
            Assert.Contains(@"<Command>C:\Users\a&amp;b\AppData\Local\Programs\AIUsageBar\AIUsageBar.exe</Command>", xml);
            Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
            Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", xml);
            Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
            Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
            Assert.DoesNotContain("<Delay>", xml);
        }

        [Theory]
        [InlineData(new[] { "--startup", "on" }, true)]
        [InlineData(new[] { "--startup", "off" }, false)]
        public void Parses_Startup_Argument(string[] args, bool expected)
        {
            Assert.Equal(expected, Startup.ParseArgs(args));
        }

        [Fact]
        public void Other_Arguments_Are_Not_Startup_Commands()
        {
            Assert.Null(Startup.ParseArgs(new string[0]));
            Assert.Null(Startup.ParseArgs(new[] { "--quit" }));
            Assert.Null(Startup.ParseArgs(new[] { "--startup", "maybe" }));
        }
    }
}
