using System.Xml.Linq;
using Wdem.Cli;
using Wdem.Windows;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class ProjectBoundaryTests
{
  [Fact]
  public void WindowsAssemblyIsAvailable()
  {
    Assert.NotNull(typeof(WdemWindowsAssemblyMarker).Assembly);
  }

  [Fact]
  public void ElevatedHostIsDeployedBesideCliOutput()
  {
    string hostPath = Path.Combine(AppContext.BaseDirectory, "Wdem.ElevatedHost.exe");

    Assert.True(File.Exists(hostPath), $"Missing elevated host at '{hostPath}'.");
  }

  [Fact]
  public void CliTreatsElevatedHostAsProcessDependencyWithOutputArtifacts()
  {
    Assert.DoesNotContain(
        typeof(WdemCommandHandler).Assembly.GetReferencedAssemblies(),
        reference => string.Equals(
            reference.Name,
            "Wdem.ElevatedHost",
            StringComparison.Ordinal));

    XDocument deployment = XDocument.Load(FindRepositoryFile(
        "src",
        "Wdem.ElevatedHost",
        "Wdem.ElevatedHost.deployment.targets"));
    XElement[] artifacts = deployment.Descendants("Content").ToArray();
    Assert.Equal(4, artifacts.Length);
    Assert.All(artifacts, artifact =>
    {
      Assert.Equal("PreserveNewest", artifact.Attribute("CopyToOutputDirectory")?.Value);
      Assert.Equal("PreserveNewest", artifact.Attribute("CopyToPublishDirectory")?.Value);
      Assert.Equal("true", artifact.Attribute("ExcludeFromSingleFile")?.Value);
    });
  }

  private static string FindRepositoryFile(params string[] segments)
  {
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
      string candidate = Path.Combine([directory.FullName, .. segments]);
      if (File.Exists(candidate))
      {
        return candidate;
      }

      directory = directory.Parent;
    }

    throw new FileNotFoundException("Could not locate the repository file.");
  }
}
