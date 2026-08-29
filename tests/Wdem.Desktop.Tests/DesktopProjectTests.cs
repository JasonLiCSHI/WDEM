using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Wdem.Desktop;
using Wdem.Tests;
using Xunit;

namespace Wdem.Desktop.Tests;

public sealed class DesktopProjectTests
{
  [Fact]
  public void AppUsesWinUiApplication()
  {
    Assert.True(typeof(App).IsSubclassOf(typeof(Application)));
  }

  [Fact]
  public void MainWindowUsesWinUiWindow()
  {
    Assert.True(typeof(MainWindow).IsSubclassOf(typeof(Window)));
  }

  [Fact]
  public void DesktopProjectEnablesWinUiWithoutWpf()
  {
    XDocument project = XDocument.Load(GetDesktopProjectPath());
    string? useWinUi = project.Descendants("UseWinUI").SingleOrDefault()?.Value;

    Assert.True(
        string.Equals("true", useWinUi, StringComparison.OrdinalIgnoreCase),
        $"Expected UseWinUI to be true, but found '{useWinUi ?? "<missing>"}'.");
    Assert.Empty(project.Descendants("UseWPF"));
  }

  [Fact]
  public void ElevatedHostIsDeployedBesideDesktopOutput()
  {
    string hostPath = Path.Combine(AppContext.BaseDirectory, "Wdem.ElevatedHost.exe");

    Assert.True(File.Exists(hostPath), $"Missing elevated host at '{hostPath}'.");
  }

  [Fact]
  public void DesktopDoesNotReferenceElevatedHostAssembly()
  {
    Assert.DoesNotContain(
        typeof(App).Assembly.GetReferencedAssemblies(),
        reference => string.Equals(
            reference.Name,
            "Wdem.ElevatedHost",
            StringComparison.Ordinal));
  }

  [Fact]
  public async Task DesktopPublishIncludesRunnableSelfContainedElevatedHost()
  {
    PublishedElevatedHostResult result =
        await PublishedElevatedHostSmoke.PublishAndRunAsync(
            useBundledCliPublishOptions: false,
            "src",
            "Wdem.Desktop",
            "Wdem.Desktop.csproj");

    Assert.True(result.PublishExitCode == 0, result.PublishOutput);
    Assert.Equal(["Wdem.ElevatedHost.exe"], result.HostFiles);
    Assert.Equal(2, result.HostExitCode);
    Assert.Contains(PublishedElevatedHostSmoke.UsageError, result.HostStandardError);
    Assert.DoesNotContain("hostpolicy", result.HostStandardError, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("runtime", result.HostStandardError, StringComparison.OrdinalIgnoreCase);
  }

  private static string GetDesktopProjectPath()
  {
    DirectoryInfo? directory = new(AppContext.BaseDirectory);

    while (directory is not null)
    {
      string candidate = Path.Combine(directory.FullName, "src", "Wdem.Desktop", "Wdem.Desktop.csproj");
      if (File.Exists(candidate))
      {
        return candidate;
      }

      directory = directory.Parent;
    }

    throw new FileNotFoundException("Could not locate the Wdem.Desktop project file.");
  }
}
