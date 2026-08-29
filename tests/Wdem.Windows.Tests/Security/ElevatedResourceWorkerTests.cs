using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class ElevatedResourceWorkerTests
{
  [Fact]
  public async Task ApplyAsync_FingerprintMismatch_RefusesWithoutCallingProvider()
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out var approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(run),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            "admin-resource",
            Mutate(approvedFingerprint),
            "pipe"),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task ApplyAsync_ApprovedSnapshot_ResolvesProviderAndRedactsOutput()
  {
    var provider = new RecordingProvider();
    var run = ApprovedRun(provider, out var approvedFingerprint);
    var worker = new ElevatedResourceWorker(
        new StubRunStore(run),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());
    var progress = new RecordingProgress();

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            run.RunId,
            "admin-resource",
            approvedFingerprint,
            "pipe"),
        progress,
        CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ApplyOutcome.Succeeded, result.Outcome);
    Assert.DoesNotContain("hunter2", progress.Items.Single().Message, StringComparison.Ordinal);
    Assert.DoesNotContain(
        "hunter2",
        result.StepResults.Single().Message,
        StringComparison.Ordinal);
    Assert.DoesNotContain(
        "hunter2",
        result.Diagnostics.Single().Detail,
        StringComparison.Ordinal);
    Assert.Equal(23, result.StepResults.Single().ProcessExitCode);
  }

  [Fact]
  public async Task ApplyAsync_CurrentUserSnapshot_RefusesWithoutCallingProvider()
  {
    var provider = new RecordingProvider();
    var approved = ApprovedRun(provider, out var approvedFingerprint);
    var planned = approved.Plan!.Resources.Single();
    var currentUserRun = approved with
    {
      Plan = approved.Plan with
      {
        Resources =
        [
          planned with
          {
            RequiresElevation = false,
            ResourcePlan = planned.ResourcePlan with
            {
              Steps = planned.ResourcePlan.Steps
                  .Select(step => step with
                  {
                    PrivilegeRequirement = PrivilegeRequirement.CurrentUser
                  })
                  .ToArray()
            }
          }
        ]
      }
    };
    var worker = new ElevatedResourceWorker(
        new StubRunStore(currentUserRun),
        new ResourceProviderRegistry([provider]),
        new LogRedactor());

    var result = await worker.ApplyAsync(
        new ElevatedResourceRequest(
            currentUserRun.RunId,
            "admin-resource",
            approvedFingerprint,
            "pipe"),
        null,
        CancellationToken.None);

    Assert.Equal(ApplyOutcome.Failed, result.Outcome);
    Assert.Equal(WdemErrorCode.PermissionError, result.Error!.Code);
    Assert.Equal(0, provider.ApplyCalls);
  }

  private static ExecutionRun ApprovedRun(
      RecordingProvider provider,
      out string fingerprint)
  {
    var definition = new ResourceDefinition
    {
      Id = "admin-resource",
      Type = provider.ResourceType,
      Provider = provider.ProviderName,
      PrivilegeRequirement = PrivilegeRequirement.Administrator
    };
    fingerprint = ResourceDefinitionFingerprint.Create(definition);
    var resourcePlan = new ResourcePlan
    {
      ResourceId = definition.Id,
      ResourceType = definition.Type,
      ProviderName = definition.Provider,
      DesiredStateFingerprint = fingerprint,
      Compliance = ComplianceStatus.Missing,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = "admin-resource:apply",
          Description = "Apply elevated resource.",
          Action = PlanAction.Configure,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart
        }
      ]
    };
    var planned = new PlannedResource
    {
      Definition = definition,
      Origin = ResourceOrigin.Required,
      Dependencies = [],
      ResourcePlan = resourcePlan,
      Status = PlannedResourceStatus.Ready,
      Risk = PlanRisk.Elevated,
      RequiresElevation = true,
      IsDestructive = false,
      RestartPolicy = RestartPolicy.NoRestart
    };
    return new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = RunMode.Apply,
      ProfileSourcePath = "profile.yaml",
      ProfileId = "test",
      ProfileVersion = "1.0.0",
      SelectedOptionalResourceIds = new HashSet<string>(),
      StartedAtUtc = DateTimeOffset.UtcNow,
      State = ExecutionState.Running,
      Machine = new MachineInformation("Windows", "x64", "machine", "user"),
      Plan = new ExecutionPlan
      {
        PlanId = Guid.NewGuid(),
        Fingerprint = new string('B', 64),
        ProfileId = "test",
        ProfileVersion = "1.0.0",
        Layers = [new ResourceGraphLayer(0, [definition.Id])],
        Resources = [planned],
        IsExecutable = true
      }
    };
  }

  private static string Mutate(string fingerprint) =>
      $"{(fingerprint[0] == 'A' ? 'B' : 'A')}{fingerprint[1..]}";

  private sealed class StubRunStore(ExecutionRun run) : IExecutionRunStore
  {
    public IReadOnlyList<StructuredError> Diagnostics => [];

    public Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult<ExecutionRun?>(runId == run.RunId ? run : null);

    public Task CreateAsync(ExecutionRun value, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<ExecutionRun>> ListAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ExecutionRun> SaveAsync(
        ExecutionRun value,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> TrySaveAsync(
        ExecutionRun value,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class RecordingProvider : IResourceProvider
  {
    public string ResourceType => "test";
    public string ProviderName => "test";
    public ProviderCapabilities Capabilities { get; } = new();
    public int ApplyCalls { get; private set; }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      progress?.Report(new ProviderProgress("apply", 0.5, "password=hunter2"));
      return ValueTask.FromResult(new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = plan.Steps.Single().Id,
            Action = PlanAction.Configure,
            Progress = 1,
            ProcessExitCode = 23,
            Message = "token=hunter2"
          }
        ],
        Diagnostics =
        [
          new StructuredError(
              WdemErrorCode.ProviderError,
              "Provider diagnostic.",
              "secret=hunter2")
        ]
      });
    }

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) => throw new NotSupportedException();
  }

  private sealed class RecordingProgress : IProgress<ProviderProgress>
  {
    public List<ProviderProgress> Items { get; } = [];
    public void Report(ProviderProgress value) => Items.Add(value);
  }
}
