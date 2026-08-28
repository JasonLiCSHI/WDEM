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
  public static TheoryData<string> PlanMismatches => new()
  {
    "not-executable",
    "resource-id",
    "resource-type",
    "provider",
    "fingerprint",
    "step-id",
    "step-action",
    "step-privilege",
    "step-restart",
    "extra-step"
  };

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
        ["show", "--id", "Git.Git", "--exact", "--versions",
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
        ["show", "--id", "Git.Git", "--exact", "--versions",
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
        ["show", "--id", "Git.Git", "--exact", "--versions",
         "--accept-source-agreements", "--disable-interactivity"],
        Success(sourceOutput));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource(preferredVersion: "2.52.1");

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.DownloadError, Assert.Single(plan.StructuredErrors).Code);
  }

  [Fact]
  public async Task PlanAsync_AcceptsExactVersionFromLocalizedVersionsOutput()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "Git.Git", "--exact", "--versions",
         "--accept-source-agreements", "--disable-interactivity"],
        Success("版本", "------", "2.52.1"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource(preferredVersion: "2.52.1");

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.True(plan.IsExecutable);
    Assert.True(plan.RequiresApply);
  }

  [Theory]
  [InlineData("Name: 2.52.1")]
  [InlineData("Id: 2.52.1")]
  [InlineData("其他字段: 2.52.1")]
  public async Task PlanAsync_DoesNotTreatOtherFieldsAsAvailableVersions(string sourceOutput)
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue(
        "winget",
        ["show", "--id", "Git.Git", "--exact", "--versions",
         "--accept-source-agreements", "--disable-interactivity"],
        Success(sourceOutput));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource(preferredVersion: "2.52.1");

    var plan = await provider.PlanAsync(resource, Missing(resource), CancellationToken.None);

    Assert.False(plan.IsExecutable);
    Assert.Equal(WdemErrorCode.DownloadError, Assert.Single(plan.StructuredErrors).Code);
  }

  [Theory]
  [MemberData(nameof(PlanMismatches))]
  public async Task ApplyAsync_RejectsMismatchedOrStalePlan(string mismatch)
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", SourceQuery(), Success("Git.Git"));
    process.Enqueue("winget", InstallArguments(), Success("Installed"));
    process.Enqueue("winget", ListArguments(),
        Success("Name  Id       Version", "Git   Git.Git  2.52.1"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource();

    var result = await provider.ApplyAsync(
        resource,
        MismatchedPlan(InstallPlan(resource), mismatch),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, result.Error!.Code);
    Assert.Equal(3, process.Remaining.Count);
  }

  [Fact]
  public async Task DetectAsync_UnknownNonzeroExitIsDetectionFailure()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", ListArguments(),
        new ProcessExecutionResult(true, 42, [], ["unexpected failure"]));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(PackageResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, state.StructuredError!.Code);
  }

  [Fact]
  public async Task DetectAsync_PackageNotFoundExitIsSuccessfulMissingState()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", ListArguments(),
        new ProcessExecutionResult(true, unchecked((int)0x8A150014), [], []));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(PackageResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
  }

  [Fact]
  public async Task DetectAsync_ProcessErrorOverridesParseableOutputAndIsPreserved()
  {
    var processError = new StructuredError(
        WdemErrorCode.ProviderError,
        "Output drain failed.",
        "The process output could not be collected.");
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", ListArguments(), new ProcessExecutionResult(
        true,
        0,
        ["Name  Id       Version", "Git   Git.Git  2.52.1"],
        [],
        processError));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(PackageResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Same(processError, state.StructuredError);
  }

  [Theory]
  [InlineData("2.52.1-preview")]
  [InlineData("2.52.1+build.7")]
  public async Task DetectAsync_PrereleaseOrBuildVersionIsNotNormalizedToStable(string version)
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", ListArguments(),
        Success("Name  Id       Version", $"Git   Git.Git  {version}"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource() with { VersionConstraint = "= 2.52.1" };

    var state = await provider.DetectAsync(resource, CancellationToken.None);
    var compliance = new ComplianceEvaluator().Evaluate(resource, state);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.True(state.Exists);
    Assert.Equal(version, state.Version);
    Assert.Empty(state.InstalledVersions);
    Assert.Equal(ComplianceStatus.VersionMismatch, compliance.Status);
  }

  [Fact]
  public async Task ValidateAndApply_SourceIsAllowedAndPassedToEveryWinGetCommand()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget",
        ["show", "--id", "Git.Git", "--exact", "--source", "company",
         "--accept-source-agreements", "--disable-interactivity"],
        Success("Git.Git"));
    process.Enqueue("winget",
        ["install", "--id", "Git.Git", "--exact", "--source", "company", "--silent",
         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
        Success("Installed"));
    process.Enqueue("winget",
        ["list", "--id", "Git.Git", "--exact", "--source", "company"],
        Success("Name  Id       Version", "Git   Git.Git  2.52.1"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource() with
    {
      Parameters = new Dictionary<string, string?>
      {
        ["packageId"] = "Git.Git",
        ["source"] = "company"
      }
    };

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);
    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.True(validation.IsValid);
    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Empty(process.Remaining);
  }

  [Fact]
  public async Task ValidateAsync_RejectsUnknownParameter()
  {
    var provider = new WinGetPackageProvider(
        new ScriptedProcessExecutor(),
        new ComplianceEvaluator());
    var resource = PackageResource() with
    {
      Parameters = new Dictionary<string, string?>
      {
        ["packageId"] = "Git.Git",
        ["unexpected"] = "value"
      }
    };

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.Errors, error => error.Contains("unexpected", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ApplyAsync_VerificationMismatchUsesVersionErrorAndDiagnostic()
  {
    var process = new ScriptedProcessExecutor();
    process.Enqueue("winget", SourceQuery(), Success("Git.Git"));
    process.Enqueue("winget", InstallArguments(), Success("Installed"));
    process.Enqueue("winget", ListArguments(),
        Success("Name  Id       Version", "Git   Git.Git  2.51.0"));
    var provider = new WinGetPackageProvider(process, new ComplianceEvaluator());
    var resource = PackageResource() with { VersionConstraint = ">= 2.52.1" };

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.VersionError, result.Error!.Code);
    Assert.Contains(result.Diagnostics, error => error.Code == WdemErrorCode.VersionError);
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

  private static ResourcePlan MismatchedPlan(ResourcePlan plan, string mismatch) => mismatch switch
  {
    "not-executable" => plan with { IsExecutable = false },
    "resource-id" => plan with { ResourceId = "other" },
    "resource-type" => plan with { ResourceType = "other" },
    "provider" => plan with { ProviderName = "other" },
    "fingerprint" => plan with { DesiredStateFingerprint = "stale" },
    "step-id" => plan with { Steps = [plan.Steps[0] with { Id = "other:install" }] },
    "step-action" => plan with { Steps = [plan.Steps[0] with { Action = PlanAction.Configure }] },
    "step-privilege" => plan with
    {
      Steps = [plan.Steps[0] with { PrivilegeRequirement = PrivilegeRequirement.Administrator }]
    },
    "step-restart" => plan with
    {
      Steps = [plan.Steps[0] with { RestartPolicy = RestartPolicy.RestartRequired }]
    },
    "extra-step" => plan with { Steps = [plan.Steps[0], plan.Steps[0]] },
    _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
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
