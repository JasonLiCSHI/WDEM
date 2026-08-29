using System.Xml.Linq;
using Wdem.Cli;
using Wdem.Tests;
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
  public void CliTreatsElevatedHostAsProcessDependencyWithSeparateBuildAndPublishArtifacts()
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
      Assert.Equal("Never", artifact.Attribute("CopyToPublishDirectory")?.Value);
    });

    XElement publishedHost = deployment.Descendants("ResolvedFileToPublish").Single();
    Assert.Equal("Wdem.ElevatedHost.exe", publishedHost.Element("RelativePath")?.Value);
    Assert.Equal("PreserveNewest", publishedHost.Element("CopyToPublishDirectory")?.Value);
    Assert.Equal("true", publishedHost.Element("ExcludeFromSingleFile")?.Value);
  }

  [Fact]
  public async Task CliPublishIncludesRunnableSelfContainedElevatedHost()
  {
    PublishedElevatedHostResult result =
        await PublishedElevatedHostSmoke.PublishAndRunAsync(
            useBundledCliPublishOptions: true,
            "src",
            "Wdem.Cli",
            "Wdem.Cli.csproj");

    Assert.True(result.PublishExitCode == 0, result.PublishOutput);
    Assert.Equal(["Wdem.ElevatedHost.exe"], result.HostFiles);
    Assert.Equal(2, result.HostExitCode);
    Assert.Contains(PublishedElevatedHostSmoke.UsageError, result.HostStandardError);
    Assert.DoesNotContain("hostpolicy", result.HostStandardError, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("runtime", result.HostStandardError, StringComparison.OrdinalIgnoreCase);
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
