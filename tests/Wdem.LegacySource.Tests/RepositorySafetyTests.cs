using System.Diagnostics;

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
    Assert.Contains("*.invalid.*", contents);
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

  [Fact]
  public void ProvenanceRemoteScript_NormalizesEveryRemoteToOneFetchAndPushUrl()
  {
    var temporaryRepository = Path.Combine(Path.GetTempPath(), $"wdem-remotes-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryRepository);

    try
    {
      Run("git", "init", temporaryRepository);
      SeedRemote(temporaryRepository, "origin");
      SeedRemote(temporaryRepository, "winhome-source");
      SeedRemote(temporaryRepository, "winhome-fork");

      Run(
          "powershell.exe",
          "-NoProfile",
          "-ExecutionPolicy",
          "Bypass",
          "-File",
          Path.Combine(GetRepositoryRoot(), "tools", "Configure-WinHomeProvenanceRemotes.ps1"),
          "-RepositoryPath",
          temporaryRepository);

      AssertRemote(temporaryRepository, "origin", "https://github.com/JasonLiCSHI/WDEM.git", "https://github.com/JasonLiCSHI/WDEM.git");
      AssertRemote(temporaryRepository, "winhome-source", "https://github.com/DotDev262/WinHome.git", "DISABLED");
      AssertRemote(temporaryRepository, "winhome-fork", "https://github.com/JasonLiCSHI/WinHome.git", "DISABLED");
    }
    finally
    {
      Directory.Delete(temporaryRepository, recursive: true);
    }
  }

  [Fact]
  public void LegacyLibrary_DoesNotContainOrRegisterASelfUpdater()
  {
    var repositoryRoot = GetRepositoryRoot();

    Assert.False(File.Exists(Path.Combine(repositoryRoot, "src", "Wdem.LegacySource", "Interfaces", "IUpdateService.cs")));
    Assert.False(File.Exists(Path.Combine(repositoryRoot, "src", "Wdem.LegacySource", "Services", "System", "UpdateService.cs")));

    var appHost = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Wdem.LegacySource", "Infrastructure", "AppHost.cs"));
    Assert.DoesNotContain("IUpdateService", appHost);
    Assert.DoesNotContain("UpdateService", appHost);
  }

  [Theory]
  [InlineData("README.md")]
  [InlineData("docs/getting-started.md")]
  [InlineData("docs/troubleshooting.md")]
  public void MigrationDocumentation_NamesOnlyTheDeliberateLegacyStateInput(string relativePath)
  {
    var contents = File.ReadAllText(Path.Combine(GetRepositoryRoot(), relativePath));

    Assert.Contains("WINHOME_STATE_PATH", contents);
    Assert.DoesNotContain("WINHOME_*", contents);
  }

  private static void SeedRemote(string repositoryPath, string remoteName)
  {
    Run("git", "-C", repositoryPath, "remote", "add", remoteName, $"https://invalid.example/{remoteName}-one.git");
    Run("git", "-C", repositoryPath, "config", "--add", $"remote.{remoteName}.url", $"https://invalid.example/{remoteName}-two.git");
    Run("git", "-C", repositoryPath, "config", "--add", $"remote.{remoteName}.pushurl", $"https://invalid.example/{remoteName}-push-one.git");
    Run("git", "-C", repositoryPath, "config", "--add", $"remote.{remoteName}.pushurl", $"https://invalid.example/{remoteName}-push-two.git");
  }

  private static void AssertRemote(string repositoryPath, string remoteName, string expectedFetchUrl, string expectedPushUrl)
  {
    Assert.Equal([expectedFetchUrl], Run("git", "-C", repositoryPath, "remote", "get-url", "--all", remoteName));
    Assert.Equal([expectedPushUrl], Run("git", "-C", repositoryPath, "remote", "get-url", "--push", "--all", remoteName));
  }

  private static string[] Run(string executable, params string[] arguments)
  {
    var startInfo = new ProcessStartInfo(executable)
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Assert.True(process.ExitCode == 0, $"{executable} exited with {process.ExitCode}: {error}");
    return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
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
