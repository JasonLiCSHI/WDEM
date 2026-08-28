using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Providers;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class DotNetSdkProviderTests
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
  public async Task ApplyAsync_DotNetUsesExactPreferredVersionAndTokenizedArguments()
  {
    var process = new RecordingProcessExecutor();
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x", "10.0.105");
    var plan = InstallPlan(resource);

    await provider.ApplyAsync(resource, plan, null, CancellationToken.None);

    var install = Assert.Single(process.Requests, request =>
        string.Equals(request.FileName, "winget", StringComparison.OrdinalIgnoreCase) &&
        request.Arguments.FirstOrDefault() == "install");
    Assert.Equal(
        ["install", "--id", "Microsoft.DotNet.SDK.10", "--exact", "--version", "10.0.105",
         "--silent", "--accept-package-agreements", "--accept-source-agreements",
         "--disable-interactivity"],
        install.Arguments);
  }

  [Fact]
  public async Task DetectAndPlan_AnyInstalledSdkMaySatisfyConstraint()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = request => request.FileName == "dotnet"
          ? new ProcessExecutionResult(true, 0,
              ["8.0.412 [C:\\dotnet\\sdk]", "10.0.105 [C:\\dotnet\\sdk]"], [])
          : new ProcessExecutionResult(true, 0, [], [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x");

    var state = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, state, CancellationToken.None);

    Assert.Equal(2, state.InstalledVersions.Count);
    Assert.False(plan.RequiresApply);
    Assert.Equal(ComplianceStatus.Satisfied, plan.Compliance);
  }

  [Fact]
  public async Task DetectAsync_MissingExecutableIsSuccessfulMissingState()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = _ => new ProcessExecutionResult(false, null, [], [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(DotNetResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
  }

  [Fact]
  public async Task DetectAsync_LaunchedMalformedOutputIsDetectionFailure()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = _ => new ProcessExecutionResult(true, 0, ["preview build"], [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(DotNetResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
  }

  [Fact]
  public async Task DetectAsync_ProcessErrorOverridesParseableOutputAndIsPreserved()
  {
    var processError = new StructuredError(
        WdemErrorCode.ProviderError,
        ".NET timed out.",
        "The dotnet process timed out.");
    var process = new RecordingProcessExecutor
    {
      Handler = _ => new ProcessExecutionResult(
          true,
          0,
          ["10.0.105 [C:\\dotnet\\sdk]"],
          [],
          processError)
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(DotNetResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Same(processError, state.StructuredError);
  }

  [Fact]
  public async Task DetectAsync_StartFailureWithErrorIsDetectionFailure()
  {
    var processError = new StructuredError(
        WdemErrorCode.ProviderError,
        ".NET could not start.",
        "The dotnet process could not be started.");
    var process = new RecordingProcessExecutor
    {
      Handler = _ => new ProcessExecutionResult(false, null, [], [], processError)
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(DotNetResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Same(processError, state.StructuredError);
  }

  [Theory]
  [InlineData("10.0.105-preview")]
  [InlineData("10.0.105+build.7")]
  public async Task DetectAsync_PrereleaseOrBuildVersionIsNotNormalizedToStable(string version)
  {
    var process = new RecordingProcessExecutor
    {
      Handler = _ => new ProcessExecutionResult(
          true,
          0,
          [$"{version} [C:\\dotnet\\sdk]"],
          [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource("= 10.0.105");

    var state = await provider.DetectAsync(resource, CancellationToken.None);
    var compliance = new ComplianceEvaluator().Evaluate(resource, state);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.True(state.Exists);
    Assert.Equal(version, state.Version);
    Assert.Empty(state.InstalledVersions);
    Assert.Equal(ComplianceStatus.VersionMismatch, compliance.Status);
  }

  [Fact]
  public async Task ValidateAsync_RejectsWrongIdentityAndUnknownParameter()
  {
    var provider = new DotNetSdkProvider(
        new RecordingProcessExecutor(),
        new ComplianceEvaluator());
    var resource = DotNetResource() with
    {
      Type = "other",
      Provider = "other",
      Parameters = new Dictionary<string, string?> { ["unexpected"] = "value" }
    };

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.Errors, error => error.Contains("type", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(validation.Errors, error => error.Contains("provider", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(validation.Errors, error => error.Contains("unexpected", StringComparison.Ordinal));
  }

  [Theory]
  [MemberData(nameof(PlanMismatches))]
  public async Task ApplyAsync_RejectsMismatchedOrStalePlan(string mismatch)
  {
    var process = new RecordingProcessExecutor();
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource();

    var result = await provider.ApplyAsync(
        resource,
        MismatchedPlan(InstallPlan(resource), mismatch),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.ProviderError, result.Error!.Code);
    Assert.Empty(process.Requests);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task DetectAndPlan_EmptySdkListIsSuccessfulMissingState(bool whitespaceLine)
  {
    var process = new RecordingProcessExecutor
    {
      Handler = request => request.FileName == "dotnet"
          ? new ProcessExecutionResult(true, 0, whitespaceLine ? ["   "] : [], [])
          : new ProcessExecutionResult(true, 0, ["available"], [])
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x");

    var state = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, state, CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
    Assert.True(plan.IsExecutable);
    Assert.True(plan.RequiresApply);
  }

  [Fact]
  public async Task ApplyAsync_SuccessfulExitWithoutVerifiedSdkFails()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = request => request.FileName == "dotnet"
          ? new ProcessExecutionResult(false, null, [], [])
          : new ProcessExecutionResult(
              true,
              0,
              request.Arguments.Contains("--versions")
                  ? ["10.0.105"]
                  : ["installed"],
              [])
    };
    var provider = new DotNetSdkProvider(
        process,
        new WinGetCommandClient(process, "test-winget.log"),
        new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x", "10.0.105");

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.InstallationError, result.Error!.Code);
    Assert.Equal(0, result.Error.ProcessExitCode);
    Assert.Equal("test-winget.log", result.Error.LogLocation);
  }

  [Fact]
  public async Task ApplyAsync_MalformedVerificationUsesDetectionErrorAndDoesNotRegressProgress()
  {
    var process = new RecordingProcessExecutor
    {
      Handler = request => request.FileName switch
      {
        "dotnet" => new ProcessExecutionResult(true, 0, ["malformed sdk output"], []),
        _ => new ProcessExecutionResult(
            true,
            0,
            request.Arguments.Contains("--versions") ? ["10.0.105"] : ["installed"],
            [])
      }
    };
    var provider = new DotNetSdkProvider(process, new ComplianceEvaluator());
    var resource = DotNetResource("10.0.x", "10.0.105");

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, result.Error!.Code);
    Assert.Contains(result.Diagnostics, error => error.Code == WdemErrorCode.DetectionError);
    Assert.Equal(0.75, Assert.Single(result.StepResults).Progress);
  }

  private static ResourceDefinition DotNetResource(
      string? constraint = null,
      string? preferredVersion = null) => new()
      {
        Id = "dotnet-sdk",
        Type = "dotnet-sdk",
        Provider = "winget",
        VersionConstraint = constraint,
        PreferredVersion = preferredVersion
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
        Description = "Install .NET SDK.",
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

  private sealed class RecordingProcessExecutor : IProcessExecutor
  {
    public List<ProcessExecutionRequest> Requests { get; } = [];
    public Func<ProcessExecutionRequest, ProcessExecutionResult>? Handler { get; init; }

    public Task<ProcessExecutionResult> ExecuteAsync(
        ProcessExecutionRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      Requests.Add(request);
      var result = Handler?.Invoke(request) ?? request.FileName switch
      {
        "dotnet" => new ProcessExecutionResult(
            true,
            0,
            ["10.0.105 [C:\\Program Files\\dotnet\\sdk]"],
            []),
        _ => new ProcessExecutionResult(
            true,
            0,
            request.Arguments.Contains("--versions") ? ["10.0.105"] : ["available"],
            [])
      };
      return Task.FromResult(result);
    }
  }
}
