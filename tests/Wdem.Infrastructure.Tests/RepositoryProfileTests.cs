using System.Text.Json;
using Wdem.Infrastructure.Profiles;
using Wdem.Testing;
using Xunit;

namespace Wdem.Infrastructure.Tests;

public sealed class RepositoryProfileTests
{
  [Fact]
  public void CSharpDeveloperProfile_DeclaresTheTwoSupportedTaskPipelines()
  {
    var repositoryRoot = RepositoryLocator.FindRoot();
    var profilePath = Path.Combine(repositoryRoot, "profiles", "csharp-developer.json");
    var profile = ProfileParser.Parse(File.ReadAllText(profilePath));

    Assert.Equal("2.0.2", profile.Version);
    Assert.Equal("Csharp developer", profile.DisplayName);
    Assert.Equal(2, profile.Tasks.Count);

    var visualStudio = profile.Tasks["visual-studio-professional"];
    Assert.True(visualStudio.Required);
    Assert.Equal(">= 18.9.2", visualStudio.VersionRequirement?.Expression);
    Assert.StartsWith("https://aka.ms/", visualStudio.Source, StringComparison.Ordinal);
    Assert.Single(visualStudio.Pre);
    Assert.NotNull(visualStudio.Apply);
    Assert.Single(visualStudio.Post);

    var reSharper = profile.Tasks["resharper"];
    Assert.True(reSharper.Required);
    Assert.Equal(["visual-studio-professional"], reSharper.DependsOn);
    Assert.Equal(">= 2026.1.0.1", reSharper.VersionRequirement?.Expression);
    Assert.StartsWith("https://download.jetbrains.com/", reSharper.Source, StringComparison.Ordinal);
    Assert.Single(reSharper.Pre);
    Assert.NotNull(reSharper.Apply);
    Assert.Empty(reSharper.Post);

    using var index = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(repositoryRoot, "profiles", "index.json")));
    var entry = Assert.Single(index.RootElement.GetProperty("profiles").EnumerateArray());
    Assert.Equal(profile.Id, entry.GetProperty("id").GetString());
    Assert.Equal(profile.Version, entry.GetProperty("version").GetString());
    Assert.Equal(profile.DisplayName, entry.GetProperty("displayName").GetString());
  }
}
