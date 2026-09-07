using System.Xml.Linq;
using Xunit;

namespace Wdem.Architecture.Tests;

public sealed class ArchitectureDependencyTests
{
  [Fact]
  public void InnerLayersExistWithTheDeclaredDependencyDirection()
  {
    var repositoryRoot = FindRepositoryRoot();
    var domain = LoadProject(repositoryRoot, "src", "Wdem.Domain", "Wdem.Domain.csproj");
    var application = LoadProject(repositoryRoot, "src", "Wdem.Application", "Wdem.Application.csproj");

    Assert.Empty(ProjectReferences(domain));
    Assert.Equal(
        [Path.Combine("..", "Wdem.Domain", "Wdem.Domain.csproj")],
        ProjectReferences(application));
  }

  [Theory]
  [InlineData("src/Wdem.Domain", "System.Diagnostics.Process")]
  [InlineData("src/Wdem.Domain", "System.IO.File")]
  [InlineData("src/Wdem.Domain", "System.Net.Http")]
  [InlineData("src/Wdem.Application", "System.Diagnostics.Process")]
  [InlineData("src/Wdem.Application", "System.IO.File")]
  [InlineData("src/Wdem.Application", "System.Net.Http")]
  public void InnerLayersDoNotUsePeripheralApis(string relativeDirectory, string forbiddenText)
  {
    var directory = Path.Combine(
        FindRepositoryRoot(),
        relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

    var offendingFiles = Directory
        .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
        .Where(path => File.ReadAllText(path).Contains(forbiddenText, StringComparison.Ordinal))
        .Select(Path.GetFileName)
        .ToArray();

    Assert.Empty(offendingFiles);
  }

  private static XDocument LoadProject(string root, params string[] path) =>
      XDocument.Load(Path.Combine([root, .. path]));

  private static string[] ProjectReferences(XDocument project) =>
      project.Descendants("ProjectReference")
          .Select(reference => reference.Attribute("Include")?.Value)
          .Where(value => value is not null)
          .Cast<string>()
          .ToArray();

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.slnx")))
      {
        return directory.FullName;
      }
      directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Unable to locate the WDEM repository root.");
  }
}
