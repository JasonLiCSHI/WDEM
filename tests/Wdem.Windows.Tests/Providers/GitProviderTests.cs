using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Providers;
using Xunit;

namespace Wdem.Windows.Tests.Providers;

public sealed class GitProviderTests
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
  public async Task DetectAsync_GitVersionSatisfiesConstraintWithoutPlanningUpgrade()
  {
    var process = new QueueProcessExecutor();
    process.Enqueue("git", ["--version"], Success("git version 2.52.1.windows.1"));
    var provider = new GitProvider(process, new ComplianceEvaluator());
    var resource = GitResource(">= 2.50");

    var state = await provider.DetectAsync(resource, CancellationToken.None);
    var plan = await provider.PlanAsync(resource, state, CancellationToken.None);

    Assert.True(state.Exists);
    Assert.Equal("2.52.1", state.Version);
    Assert.False(plan.RequiresApply);
  }

  [Fact]
  public async Task DetectAsync_MissingExecutableIsSuccessfulMissingState()
  {
    var process = new QueueProcessExecutor();
    process.Enqueue("git", ["--version"], new ProcessExecutionResult(false, null, [], []));
    var provider = new GitProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(GitResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.False(state.Exists);
  }

  [Fact]
  public async Task DetectAsync_LaunchedMalformedOutputIsDetectionFailure()
  {
    var process = new QueueProcessExecutor();
    process.Enqueue("git", ["--version"], Success("unexpected output"));
    var provider = new GitProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(GitResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.Equal(WdemErrorCode.DetectionError, state.StructuredError!.Code);
  }

  [Fact]
  public async Task DetectAsync_ProcessErrorOverridesParseableOutputAndIsPreserved()
  {
    var processError = new StructuredError(
        WdemErrorCode.ProviderError,
        "Git timed out.",
        "The Git process timed out.");
    var process = new QueueProcessExecutor();
    process.Enqueue("git", ["--version"], new ProcessExecutionResult(
        true,
        0,
        ["git version 2.52.1.windows.1"],
        [],
        processError));
    var provider = new GitProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(GitResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.NotSame(processError, state.StructuredError);
    Assert.Equal(processError.Code, state.StructuredError!.Code);
    Assert.Equal(processError.Detail, state.StructuredError.Detail);
    Assert.Equal("git", state.StructuredError.ResourceId);
    Assert.Equal(0, state.StructuredError.ProcessExitCode);
  }

  [Fact]
  public async Task DetectAsync_StartFailureWithErrorIsDetectionFailure()
  {
    var processError = new StructuredError(
        WdemErrorCode.ProviderError,
        "Git could not start.",
        "The Git process could not be started.");
    var process = new QueueProcessExecutor();
    process.Enqueue("git", ["--version"],
        new ProcessExecutionResult(false, null, [], [], processError));
    var provider = new GitProvider(process, new ComplianceEvaluator());

    var state = await provider.DetectAsync(GitResource(), CancellationToken.None);

    Assert.Equal(DetectionOutcome.Failed, state.Outcome);
    Assert.NotSame(processError, state.StructuredError);
    Assert.Equal(processError.Code, state.StructuredError!.Code);
    Assert.Equal(processError.Detail, state.StructuredError.Detail);
    Assert.Equal("git", state.StructuredError.ResourceId);
    Assert.Null(state.StructuredError.ProcessExitCode);
  }

  [Theory]
  [InlineData("2.52.1-preview")]
  [InlineData("2.52.1+build.7")]
  public async Task DetectAsync_PrereleaseOrBuildVersionIsNotNormalizedToStable(string version)
  {
    var process = new QueueProcessExecutor();
    process.Enqueue("git", ["--version"], Success($"git version {version}"));
    var provider = new GitProvider(process, new ComplianceEvaluator());
    var resource = GitResource("= 2.52.1");

    var state = await provider.DetectAsync(resource, CancellationToken.None);
    var compliance = new ComplianceEvaluator().Evaluate(resource, state);

    Assert.Equal(DetectionOutcome.Succeeded, state.Outcome);
    Assert.True(state.Exists);
    Assert.Equal(version, state.Version);
    Assert.Empty(state.InstalledVersions);
    Assert.Equal(ComplianceStatus.VersionMismatch, compliance.Status);
  }

  [Fact]
  public async Task ValidateAsync_RejectsUnknownParameter()
  {
    var provider = new GitProvider(new QueueProcessExecutor(), new ComplianceEvaluator());
    var resource = GitResource() with
    {
      Parameters = new Dictionary<string, string?> { ["unexpected"] = "value" }
    };

    var validation = await provider.ValidateAsync(resource, CancellationToken.None);

    Assert.False(validation.IsValid);
    Assert.Contains(validation.Errors, error => error.Contains("unexpected", StringComparison.Ordinal));
  }

  [Theory]
  [MemberData(nameof(PlanMismatches))]
  public async Task ApplyAsync_RejectsMismatchedOrStalePlan(string mismatch)
  {
    var process = new QueueProcessExecutor();
    process.Enqueue("winget", SourceQuery(), Success("Git.Git"));
    process.Enqueue("winget", InstallArguments(), Success("Installed"));
    process.Enqueue("git", ["--version"], Success("git version 2.52.1.windows.1"));
    var provider = new GitProvider(process, new ComplianceEvaluator());
    var resource = GitResource();

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
  public async Task ApplyAsync_VerificationMismatchTakesPriorityAndRetainsInstallerDiagnostic()
  {
    var process = new QueueProcessExecutor();
    process.Enqueue("winget", SourceQuery(), Success("Git.Git"));
    process.Enqueue("winget", InstallArguments(),
        new ProcessExecutionResult(true, 1603, [], ["installer failed"]));
    process.Enqueue("git", ["--version"], Success("git version 2.51.0.windows.1"));
    var provider = new GitProvider(
        process,
        new WinGetCommandClient(process, "test-winget.log"),
        new ComplianceEvaluator());
    var resource = GitResource(">= 2.52.1");

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.VersionError, result.Error!.Code);
    Assert.Contains(result.Diagnostics, error => error.Code == WdemErrorCode.VersionError);
    Assert.Contains(result.Diagnostics, error => error.Code == WdemErrorCode.InstallationError);
  }

  [Fact]
  public async Task ApplyAsync_UsesGitPackageAndReportsLifecycleStages()
  {
    var process = new QueueProcessExecutor();
    process.Enqueue("winget", SourceQuery(), Success("Git.Git"));
    process.Enqueue("winget", InstallArguments(), Success("Installed"));
    process.Enqueue("git", ["--version"], Success("git version 2.52.1.windows.1"));
    var provider = new GitProvider(process, new ComplianceEvaluator());
    var resource = GitResource(">= 2.50");
    var stages = new List<string>();

    var result = await provider.ApplyAsync(
        resource,
        InstallPlan(resource),
        new ImmediateProgress<ProviderProgress>(report => stages.Add(report.Stage)),
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.Equal(["Detect", "Plan", "Apply", "Verify"], stages.Distinct());
  }

  private static ResourceDefinition GitResource(string? constraint = null) => new()
  {
    Id = "git",
    Type = "git",
    Provider = "winget",
    VersionConstraint = constraint
  };

  private static IReadOnlyList<string> SourceQuery() =>
      ["show", "--id", "Git.Git", "--exact", "--accept-source-agreements",
       "--disable-interactivity"];

  private static IReadOnlyList<string> InstallArguments() =>
      ["install", "--id", "Git.Git", "--exact", "--silent",
       "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"];

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
        Description = "Install Git.",
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

  private sealed class QueueProcessExecutor : IProcessExecutor
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
