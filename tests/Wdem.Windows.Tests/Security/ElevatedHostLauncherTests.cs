using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class ElevatedHostLauncherTests
{
  [Fact]
  public void CreateStartInfo_UsesRunAsAndOnlyBootstrapArguments()
  {
    var runId = Guid.Parse("ed98e997-03ad-4ef4-ae81-11d885ec69bd");

    var startInfo = ElevatedHostLauncher.CreateStartInfo(
        @"C:\Program Files\WDEM\Wdem.ElevatedHost.exe",
        "wdem-pipe",
        runId,
        @"C:\Program Files\WDEM\profiles",
        @"C:\Users\user\AppData\Local");

    Assert.True(startInfo.UseShellExecute);
    Assert.Equal("runas", startInfo.Verb);
    Assert.Equal(
        [
          "--pipe", "wdem-pipe",
          "--run-id", runId.ToString("D"),
          "--profiles", @"C:\Program Files\WDEM\profiles",
          "--local-app-data", @"C:\Users\user\AppData\Local"
        ],
        startInfo.ArgumentList);
    Assert.DoesNotContain(
        startInfo.ArgumentList,
        argument => argument.Contains("powershell", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void BootstrapOptions_ParseRejectsAdditionalArguments()
  {
    var runId = Guid.NewGuid();
    var arguments = new[]
    {
      "--pipe", "wdem-pipe",
      "--run-id", runId.ToString("D"),
      "--profiles", @"C:\Program Files\WDEM\profiles",
      "--local-app-data", @"C:\Users\user\AppData\Local",
      "--command", "powershell.exe"
    };

    var error = Assert.Throws<ArgumentException>(() =>
        ElevatedHostBootstrapOptions.Parse(arguments));

    Assert.DoesNotContain("powershell.exe", error.Message, StringComparison.Ordinal);
  }
}
