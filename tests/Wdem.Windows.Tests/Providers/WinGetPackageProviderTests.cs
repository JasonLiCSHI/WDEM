using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Providers;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class WinGetPackageProviderTests
{
  [Fact]
  public async Task Factory_RegistersAllP0WinGetProviders()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-p0-providers-{Guid.NewGuid():N}");
    var profiles = Path.Combine(root, "profiles");
    Directory.CreateDirectory(profiles);
    try
    {
      var composition = await WdemWindowsFactory.CreateAsync(
          profiles,
          new WdemDataPaths(Path.Combine(root, "data")),
          CancellationToken.None);

      Assert.IsType<WinGetPackageProvider>(
          composition.Providers.GetRequired("winget-package", "winget"));
      Assert.IsType<GitProvider>(composition.Providers.GetRequired("git", "winget"));
      Assert.IsType<DotNetSdkProvider>(
          composition.Providers.GetRequired("dotnet-sdk", "winget"));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public async Task DetectAsync_UsesExactTokenizedListAndParsesInstalledVersion()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", ["list", "--id", "Git.Git", "--exact"],
        Success("Name  Id       Version", "Git   Git.Git  2.52.1"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(PackageResource(), CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("2.52.1", state.Version);
  }

  [Fact]
  public async Task PlanAsync_UnavailablePreferredVersionReturnsDownloadError()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "Git.Git", "--exact", "--version", "2.52.1",
         "--accept-source-agreements", "--disable-interactivity"],
        new ProcessExecutionResult(
            true,
            unchecked((int)0x8A150013),
            [],
            ["No applicable package found"]));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator(), "test-winget.log");
    var resource = PackageResource(preferredVersion: "2.52.1");

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    var error = Assert.Single(plan.StructuredErrors);
    Assert.Equal(WdemErrorCode.DownloadError, error.Code);
    Assert.Equal(unchecked((int)0x8A150013), error.ProcessExitCode);
    Assert.Equal("test-winget.log", error.LogLocation);
  }

  [Fact]
  public async Task PlanAsync_SourceCannotSubstituteDifferentVersion()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "Git.Git", "--exact", "--version", "2.52.1",
         "--accept-source-agreements", "--disable-interactivity"],
        Success("Version: 2.51.0"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource(preferredVersion: "2.52.1");

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.DownloadError, Assert.Single(plan.StructuredErrors).Code);
  }

  [Theory]
  [InlineData("Version: 2.52.1-preview")]
  [InlineData("Version: 2.52.1+build.7")]
  [InlineData("Release Notes: fixes for 2.52.1")]
  public async Task PlanAsync_RequiresExactValueFromExplicitVersionField(string sourceOutput)
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "Git.Git", "--exact", "--version", "2.52.1",
         "--accept-source-agreements", "--disable-interactivity"],
        Success(sourceOutput));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource(preferredVersion: "2.52.1");

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.DownloadError, Assert.Single(plan.StructuredErrors).Code);
  }

  [Fact]
  public async Task ApplyAsync_WithoutPreferredVersionOmitsVersionArguments()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "Git.Git", "--exact", "--accept-source-agreements",
         "--disable-interactivity"],
        Success("Git.Git"));
    process.Enqueue(
        "winget",
        ["install", "--id", "Git.Git", "--exact", "--silent",
         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
        Success("Installed"));
    process.Enqueue(
        "winget",
        ["list", "--id", "Git.Git", "--exact"],
        Success("Name  Id       Version", "Git   Git.Git  2.52.1"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource();

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Empty(process.Remaining);
  }

  [Fact]
  public async Task ApplyAsync_VerificationOverridesNonzeroInstallerExit()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", SourceQuery(), Success("Git.Git"));
    process.Enqueue("winget", InstallArguments(),
        new ProcessExecutionResult(true, 1603, [], ["installer exit"]));
    process.Enqueue("winget", ListArguments(),
        Success("Name  Id       Version", "Git   Git.Git  2.52.1"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator(), "test-winget.log");
    var resource = PackageResource();

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(WdemErrorCode.InstallationError, diagnostic.Code);
    Assert.Equal(1603, diagnostic.ProcessExitCode);
    Assert.Equal("test-winget.log", diagnostic.LogLocation);
  }

  [Fact]
  public async Task ApplyAsync_ReportsAllProviderLifecycleStages()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", SourceQuery(), Success("Git.Git"));
    process.Enqueue("winget", InstallArguments(), Success("Installed"));
    process.Enqueue("winget", ListArguments(),
        Success("Name  Id       Version", "Git   Git.Git  2.52.1"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource();
    var stages = new List<string>();

    await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        new ImmediateProgress<ProviderProgress>(report => stages.Add(report.Stage)),
        CancellationToken.None);

    Assert.Equal(["Detect", "Plan", "Apply", "Verify"], stages.Distinct());
  }

  private static IReadOnlyList<string> SourceQuery() =>
      ["show", "--id", "Git.Git", "--exact", "--accept-source-agreements",
       "--disable-interactivity"];

  private static IReadOnlyList<string> InstallArguments() =>
      ["install", "--id", "Git.Git", "--exact", "--silent",
       "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];

  private static IReadOnlyList<string> ListArguments() => ["list", "--id", "Git.Git", "--exact"];

  private static ResourceDefinition PackageResource(string? preferredVersion = null) => new()
  {
    Id = "git-package",
    Type = "winget-package",
    Provider = "winget",
    PreferredVersion = preferredVersion,
    Parameters = new Dictionary<string, string?> { ["packageId"] = "Git.Git" }
  };

  private static DetectedState Missing(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private static ResourcePlan InstallPlan(ResourceDefinition resource) => new()
  {
    ResourceId = resource.Id,
    ResourceType = resource.Type,
    ProviderName = resource.Provider,
    DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
    Compliance = ComplianceStatus.Missing,
    IsExecutable = true,
    Steps =
    [
      new PlanStep
      {
        Id = $"{resource.Id}:install",
        Description = "Install package.",
        Action = PlanAction.Install,
        PrivilegeRequirement = resource.PrivilegeRequirement,
        RestartPolicy = resource.RestartPolicy
      }
    ]
  };

  private static ProcessExecutionResult Success(params string[] output) =>
      new(true, 0, output, []);

  private sealed class ScriptedProcessExecutor : IProcessExecutor
  {
    private readonly Queue<(string FileName, IReadOnlyList<string> Arguments,
        ProcessExecutionResult Result)> _responses = new();

    public IReadOnlyCollection<object> Remaining => _responses.Cast<object>().ToArray();

    public void Enqueue(
        string fileName,
        IReadOnlyList<string> arguments,
        ProcessExecutionResult result) => _responses.Enqueue((fileName, arguments, result));

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var response = _responses.Dequeue();
      Assert.Equal(response.FileName, request.FileName);
      Assert.Equal(response.Arguments, request.Arguments);
      return Task.FromResult(response.Result);
    }
  }

  private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
  {
    public void Report(T value) => report(value);
  }
}
