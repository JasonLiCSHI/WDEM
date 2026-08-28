using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
using Xunit;

namespace Wdem.Core.Tests.Execution;

public sealed class EnvironmentRunServiceTests
{
  [Fact]
  public async Task InspectAsync_DetectsAndPlansButNeverCallsApply()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, _) = CreateService(provider);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(RunMode.Inspect, run.Mode);
    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ComplianceStatus.Missing, run.ResourceResults["git"].FinalCompliance);
    Assert.NotNull(run.Plan);
  }

  [Fact]
  public async Task RetryAsync_CreatesNewRunAndRedetectsBeforePlanning()
  {
    var provider = new ScriptedProvider(
        Missing("git"),
        Satisfied("git", "2.52.1"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        Error = ProviderError("git", "Installation failed.")
      }
    };
    var (service, _) = CreateService(provider);
    var failed = await service.ApplyAsync(Request(), CancellationToken.None);

    var retried = await service.RetryAsync(
        failed.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, failed.Outcome);
    Assert.NotEqual(failed.RunId, retried.RunId);
    Assert.Equal(failed.RunId, retried.RetriedFromRunId);
    Assert.True(provider.DetectCalls >= 2);
    Assert.Equal(ExecutionOutcome.NotRequired, retried.ResourceResults["git"].Outcome);
    Assert.Equal(ComplianceStatus.Satisfied, retried.ResourceResults["git"].FinalCompliance);
  }

  [Fact]
  public async Task ApplyAsync_RequiresSatisfiedVerificationBeforeRecordingSuccess()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.Missing,
        DetectedState = Missing("git"),
        Message = "git was still missing"
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(1, provider.VerifyCalls);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.Equal(WdemErrorCode.VerificationError, run.ResourceResults["git"].Error?.Code);
    Assert.Equal(ComplianceStatus.Missing, run.ResourceResults["git"].FinalCompliance);
  }

  [Fact]
  public async Task ApplyAsync_PersistsVerifiedResultBeforeCompletingRun()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, run.ResourceResults["git"].Outcome);
    Assert.Contains(store.SavedSnapshots, snapshot =>
        snapshot.State == ExecutionState.Running &&
        snapshot.ResourceResults["git"].State == ExecutionState.Completed &&
        snapshot.ResourceResults["git"].Outcome == ExecutionOutcome.Succeeded);
  }

  [Fact]
  public async Task Recovery_RedetectsAndReplansFromAnIncompleteSnapshot()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var interrupted = InterruptedRun();
    await store.CreateAsync(interrupted, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    var candidate = Assert.Single(candidates);
    Assert.Equal(interrupted.RunId, candidate.RunId);
    Assert.Contains("git", candidate.PendingResourceIds);
    Assert.NotEqual(interrupted.RunId, recovered.RunId);
    Assert.Equal(interrupted.RunId, recovered.RetriedFromRunId);
    Assert.True(provider.DetectCalls >= 1);
    Assert.Equal(ExecutionOutcome.NotRequired, recovered.ResourceResults["git"].Outcome);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task AbandonAsync_CompletesInterruptedRunAsCancelledWithoutApplying()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var interrupted = InterruptedRun();
    await store.CreateAsync(interrupted, CancellationToken.None);

    await service.AbandonAsync(interrupted.RunId, CancellationToken.None);

    var abandoned = await store.GetAsync(interrupted.RunId, CancellationToken.None);
    Assert.NotNull(abandoned);
    Assert.Equal(ExecutionState.Completed, abandoned.State);
    Assert.Equal(ExecutionOutcome.Cancelled, abandoned.Outcome);
    Assert.Equal(0, provider.ApplyCalls);
  }

  private static (EnvironmentRunService Service, InMemoryRunStore Store) CreateService(
      ScriptedProvider provider)
  {
    var catalog = new FakeProfileCatalog(Profile(), CanonicalProfilePath);
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var store = new InMemoryRunStore();
    return (
        new EnvironmentRunService(
            catalog,
            new ResourceGraphBuilder(),
            registry,
            compliance,
            new ExecutionPlanner(registry, compliance),
            new ResourceScheduler(),
            store,
            new DirectResourceApplyDispatcher()),
        store);
  }

  private static RunRequest Request() => new(
      "input/profile.yaml",
      new HashSet<string>(StringComparer.OrdinalIgnoreCase));

  private static DeveloperProfile Profile() => new()
  {
    Id = "developer",
    Version = "1.0.0",
    DisplayName = "Developer",
    Description = "Developer workstation",
    RequiredResources = [new ProfileResourceReference { Id = "git" }],
    Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        Id = "git",
        Type = "package",
        Provider = "fake",
        VersionConstraint = ">=2.50.0"
      }
    }
  };

  private static ExecutionRun InterruptedRun() => new()
  {
    RunId = Guid.NewGuid(),
    Mode = RunMode.Apply,
    ProfileSourcePath = CanonicalProfilePath,
    ProfileId = "developer",
    ProfileVersion = "1.0.0",
    SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
    State = ExecutionState.Running,
    Machine = new MachineInformation("test", "x64", "test", "test"),
    ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new()
      {
        ResourceId = "git",
        State = ExecutionState.Running,
        DetectedBefore = Missing("git")
      }
    }
  };

  private static DetectedState Missing(string id) => new()
  {
    ResourceId = id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private static DetectedState Satisfied(string id, string version)
  {
    Assert.True(SemanticVersion.TryParse(version, out var parsed));
    return new DetectedState
    {
      ResourceId = id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = version,
      InstalledVersions = [parsed]
    };
  }

  private static StructuredError ProviderError(string id, string detail) => new(
      WdemErrorCode.ProviderError,
      "Provider failed.",
      detail)
  {
    ResourceId = id,
    IsRetryable = true
  };

  private static string CanonicalProfilePath { get; } =
      Path.GetFullPath("input/profile.yaml");

  private sealed class FakeProfileCatalog(
      DeveloperProfile profile,
      string sourcePath) : IProfileCatalog
  {
    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(sourcePath, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(new ProfileLoadResult
      {
        Profile = profile,
        SourcePath = sourcePath
      });
    }

    public async Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        [await LoadFileAsync(sourcePath, cancellationToken)];
  }

  private sealed class ScriptedProvider(params DetectedState[] states) : IResourceProvider
  {
    private readonly Queue<DetectedState> _states = new(states);

    public string ResourceType => "package";
    public string ProviderName => "fake";
    public ProviderCapabilities Capabilities { get; } = new()
    {
      MaxConcurrentOperations = 1
    };
    public int DetectCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public ResourceApplyResult ApplyResult { get; init; } = new()
    {
      ResourceId = "git",
      Outcome = ApplyOutcome.Succeeded
    };
    public VerificationResult VerificationResult { get; init; } = new()
    {
      ResourceId = "git",
      Compliance = ComplianceStatus.Satisfied,
      DetectedState = new DetectedState
      {
        ResourceId = "git",
        Outcome = DetectionOutcome.Succeeded,
        Exists = true,
        Version = "2.52.1",
        InstalledVersions = [new SemanticVersion(2, 52, 1)]
      }
    };

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      DetectCalls++;
      if (_states.Count > 1)
      {
        return ValueTask.FromResult(_states.Dequeue());
      }

      return ValueTask.FromResult(_states.Peek());
    }

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken)
    {
      var satisfied = currentState.Exists;
      return ValueTask.FromResult(new ResourcePlan
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = satisfied ? ComplianceStatus.Satisfied : ComplianceStatus.Missing,
        IsExecutable = true,
        Steps = satisfied
            ? []
            :
            [
              new PlanStep
              {
                Id = "install",
                Description = "Install git",
                Action = PlanAction.Install,
                PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
                RestartPolicy = RestartPolicy.NoRestart
              }
            ]
      });
    }

    public ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      ApplyCalls++;
      return ValueTask.FromResult(ApplyResult);
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken)
    {
      VerifyCalls++;
      return ValueTask.FromResult(VerificationResult);
    }
  }

  private sealed class InMemoryRunStore : IExecutionRunStore
  {
    private readonly Dictionary<Guid, ExecutionRun> _runs = [];

    public IReadOnlyList<StructuredError> Diagnostics => [];
    public List<ExecutionRun> SavedSnapshots { get; } = [];

    public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _runs.Add(run.RunId, run);
      SavedSnapshots.Add(run);
      return Task.CompletedTask;
    }

    public Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(_runs.GetValueOrDefault(runId));
    }

    public Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      IReadOnlyList<ExecutionRun> result = _runs.Values
          .Where(run => run.State != ExecutionState.Completed)
          .ToArray();
      return Task.FromResult(result);
    }

    public Task SaveAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _runs[run.RunId] = run;
      SavedSnapshots.Add(run);
      return Task.CompletedTask;
    }

    public Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunLogEntry>>([]);
  }
}
