namespace Wdem.LegacySource.Tests;

public class RepositorySafetyTests
{
  [Theory]
  [InlineData(".gitignore")]
  [InlineData(".dockerignore")]
  public void StateAndMigrationBackups_AreExcludedFromSourceAndImages(string ignoreFile)
  {
    var contents = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ignoreFile));

    Assert.Contains("winhome.state.json", contents);
    Assert.Contains(".winhome-state.json", contents);
    Assert.Contains("*.migrated-*", contents);
    Assert.Contains("*.migration-backup.*", contents);
    Assert.Contains("*.backup.*", contents);
  }

  [Theory]
  [InlineData("test-data/run-test.ps1")]
  [InlineData("test-data/run-test-full.ps1")]
  [InlineData("test-data/run-test-gha.ps1")]
  [InlineData("test-data/run-test-container.ps1")]
  [InlineData("testing/infrastructure/start-sandbox.ps1")]
  public void UnavailableIntegrationScripts_FailRatherThanClaimingUnitValidation(string scriptPath)
  {
    var contents = File.ReadAllText(Path.Combine(GetRepositoryRoot(), scriptPath));

    Assert.Contains("throw", contents, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("Wdem.Cli", contents, StringComparison.Ordinal);
    Assert.DoesNotContain("dotnet build", contents, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("dotnet test", contents, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ReleaseValidation_ThrowsWhenANativeDotnetCommandFails()
  {
    var contents = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "tests", "release_validation.ps1"));

    Assert.Contains("function Invoke-NativeCommand", contents);
    Assert.Contains("$LASTEXITCODE -ne 0", contents);
    Assert.Contains("throw", contents, StringComparison.OrdinalIgnoreCase);
  }

  private static string GetRepositoryRoot()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "Wdem.sln"))) return directory.FullName;
    }

    throw new DirectoryNotFoundException("Could not locate the repository root.");
  }
}
