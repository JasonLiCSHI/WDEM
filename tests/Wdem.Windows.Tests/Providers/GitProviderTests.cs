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

  private static ProcessExecutionResult Success(params string[] output) =>
      new(true, 0, output, []);

  private sealed class QueueProcessExecutor : IProcessExecutor
  {
    private readonly Queue<(string FileName, IReadOnlyList<string> Arguments,
        ProcessExecutionResult Result)> _responses = new();

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
