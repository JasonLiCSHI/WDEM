using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Core.Versions;
using Wdem.Windows.Persistence;
using Xunit;

namespace Wdem.Windows.Tests.Execution;

public sealed class EnvironmentRunServiceConcurrencyTests : IDisposable
{
  private readonly string _directory = Path.Combine(
      Path.GetTempPath(), $"wdem-recovery-concurrency-{Guid.NewGuid():N}");

  [Fact]
  public async Task RecoverAsync_ConcurrentJsonStoresOnlyOneExecutesReplacement()
  {
    var provider = new GatedProvider();
    var firstStore = CreateStore();
    var secondStore = CreateStore();
    var firstService = CreateService(provider, firstStore);
    var secondService = CreateService(provider, secondStore);
    var prior = InterruptedRun();
    await firstStore.CreateAsync(prior, CancellationToken.None);
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var attempts = new[]
    {
      AttemptRecoveryAsync(firstService, prior.RunId, start.Task),
      AttemptRecoveryAsync(secondService, prior.RunId, start.Task)
    };
    start.SetResult();
    await provider.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Task.WhenAny(
        provider.SecondApplyEntered.Task,
        Task.WhenAny(attempts)).WaitAsync(TimeSpan.FromSeconds(5));
    provider.ReleaseApply.TrySetResult();
    var completed = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5));

    var runs = await CreateStore().ListAsync(CancellationToken.None);
    var persistedPrior = Assert.Single(runs, run => run.RunId == prior.RunId);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Single(completed, attempt => attempt.Run is not null);
    Assert.Single(completed, attempt => attempt.Error is InvalidOperationException);
    Assert.Single(runs, run => run.RetriedFromRunId == prior.RunId);
    Assert.Equal(ExecutionState.Completed, persistedPrior.State);
  }

  [Fact]
  public async Task AbandonAsync_CannotTerminatePriorClaimedByRecovery()
  {
    var provider = new GatedProvider();
    var firstStore = CreateStore();
    var secondStore = CreateStore();
    var recoveryService = CreateService(provider, firstStore);
    var abandonService = CreateService(provider, secondStore);
    var prior = InterruptedRun();
    await firstStore.CreateAsync(prior, CancellationToken.None);
    var recovery = recoveryService.RecoverAsync(prior.RunId, CancellationToken.None);
    await provider.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Exception? abandonError = null;
    try
    {
      await abandonService.AbandonAsync(prior.RunId, CancellationToken.None);
    }
    catch (Exception exception)
    {
      abandonError = exception;
    }

    var whileApplying = await CreateStore().GetAsync(prior.RunId, CancellationToken.None);
    provider.ReleaseApply.TrySetResult();
    await recovery.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.IsType<InvalidOperationException>(abandonError);
    Assert.Equal(ExecutionState.Running, whileApplying!.State);
    Assert.NotNull(whileApplying.RecoveryClaimId);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task RecoverAsync_DoesNotApplyAfterConcurrentAbandonWins()
  {
    var provider = new GatedProvider();
    var recoveryStore = new GatedRecoveryOperationStore(CreateStore());
    var recoveryService = CreateService(provider, recoveryStore);
    var abandonService = CreateService(provider, CreateStore());
    var prior = InterruptedRun();
    await CreateStore().CreateAsync(prior, CancellationToken.None);
    var recovery = AttemptRecoveryAsync(
        recoveryService,
        prior.RunId,
        Task.CompletedTask);
    await recoveryStore.AcquireEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await abandonService.AbandonAsync(prior.RunId, CancellationToken.None);
    recoveryStore.ReleaseAcquire.TrySetResult();
    var attempt = await recovery.WaitAsync(TimeSpan.FromSeconds(5));

    var persisted = await CreateStore().GetAsync(prior.RunId, CancellationToken.None);
    Assert.IsType<InvalidOperationException>(attempt.Error);
    Assert.Null(attempt.Run);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
  }

  [Theory]
  [InlineData(ApplyOutcome.Failed, ExecutionOutcome.Failed)]
  [InlineData(ApplyOutcome.Cancelled, ExecutionOutcome.Cancelled)]
  public async Task RecoverAsync_UnsuccessfulReplacementReleasesClaimAcrossJsonStores(
      ApplyOutcome applyOutcome,
      ExecutionOutcome executionOutcome)
  {
    var provider = new GatedProvider
    {
      Outcome = applyOutcome,
      WaitForRelease = false
    };
    var firstStore = CreateStore();
    var firstService = CreateService(provider, firstStore);
    var prior = InterruptedRun();
    await firstStore.CreateAsync(prior, CancellationToken.None);

    var recovered = await firstService.RecoverAsync(prior.RunId, CancellationToken.None);

    var secondStore = CreateStore();
    var secondService = CreateService(provider, secondStore);
    var persisted = await secondStore.GetAsync(prior.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    Assert.Equal(executionOutcome, recovered.Outcome);
    Assert.Equal(ExecutionState.Running, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.Null(persisted.RecoveryClaimedAtUtc);
    Assert.Contains(candidates, candidate => candidate.RunId == prior.RunId);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_HidesActiveRecoveryOperation()
  {
    var store = CreateStore();
    var service = CreateService(new GatedProvider(), store);
    var claimed = InterruptedRun() with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    await using var operation = await store.TryAcquireRecoveryOperationAsync(
        claimed.RunId,
        CancellationToken.None);
    Assert.NotNull(operation);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    Assert.DoesNotContain(candidates, candidate => candidate.RunId == claimed.RunId);
  }

  [Fact]
  public async Task RecoverAsync_ReclaimsOrphanedClaimAcrossJsonStores()
  {
    var provider = new GatedProvider { WaitForRelease = false };
    var firstStore = CreateStore();
    var claimed = InterruptedRun() with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await firstStore.CreateAsync(claimed, CancellationToken.None);
    var service = CreateService(provider, CreateStore());
    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    var recovered = await service.RecoverAsync(claimed.RunId, CancellationToken.None);

    var persisted = await CreateStore().GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Contains(candidates, candidate => candidate.RunId == claimed.RunId);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.True(persisted.Revision > claimed.Revision);
  }

  [Fact]
  public async Task RecoverAsync_ReclaimsMaxValueTimestampClaimWithoutOverflow()
  {
    var clock = new MutableTimeProvider(DateTimeOffset.MaxValue);
    var provider = new GatedProvider { WaitForRelease = false };
    var store = CreateStore(clock);
    var claimed = InterruptedRun() with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.MaxValue
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    var service = CreateService(provider, CreateStore(clock), clock);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var recovered = await service.RecoverAsync(claimed.RunId, CancellationToken.None);

    var persisted = await CreateStore(clock).GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Contains(candidates, candidate => candidate.RunId == claimed.RunId);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.Null(persisted.RecoveryClaimedAtUtc);
  }

  [Fact]
  public async Task AbandonAsync_ClearsOrphanedRecoveryClaim()
  {
    var store = CreateStore();
    var claimed = InterruptedRun() with
    {
      Revision = 4,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = DateTimeOffset.UtcNow
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    var service = CreateService(new GatedProvider(), CreateStore());

    await service.AbandonAsync(claimed.RunId, CancellationToken.None);

    var persisted = await CreateStore().GetAsync(claimed.RunId, CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Null(persisted.RecoveryClaimId);
    Assert.Null(persisted.RecoveryClaimedAtUtc);
  }

  [Fact]
  public async Task RecoveryClaim_BecomesImmediatelyRecoverableWhenOperationLeaseIsDisposed()
  {
    var clock = new MutableTimeProvider(
        new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
    var provider = new GatedProvider { WaitForRelease = false };
    var store = CreateStore(clock);
    var claimed = InterruptedRun() with
    {
      Revision = 1,
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = clock.GetUtcNow()
    };
    await store.CreateAsync(claimed, CancellationToken.None);
    var service = CreateService(provider, CreateStore(clock), clock);
    var operation = await store.TryAcquireRecoveryOperationAsync(
        claimed.RunId,
        CancellationToken.None);
    Assert.NotNull(operation);

    Assert.Empty(await service.FindRecoveryCandidatesAsync(CancellationToken.None));
    await operation!.DisposeAsync();
    Assert.Contains(
        await service.FindRecoveryCandidatesAsync(CancellationToken.None),
        candidate => candidate.RunId == claimed.RunId);
    var recovered = await service.RecoverAsync(claimed.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(1, provider.ApplyCalls);
  }

  [Fact]
  public async Task ActiveRecoveryOperationCannotBeStolenAfterClaimTimestampAges()
  {
    var clock = new MutableTimeProvider(
        new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
    var provider = new GatedProvider();
    var firstStore = CreateStore(clock);
    var prior = InterruptedRun();
    await firstStore.CreateAsync(prior, CancellationToken.None);
    var firstService = CreateService(provider, firstStore, clock);
    var secondService = CreateService(provider, CreateStore(clock), clock);
    var firstRecovery = AttemptRecoveryAsync(
        firstService,
        prior.RunId,
        Task.CompletedTask);
    await provider.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    clock.Advance(TimeSpan.FromMinutes(6));
    try
    {
      Assert.DoesNotContain(
          await secondService.FindRecoveryCandidatesAsync(CancellationToken.None),
          candidate => candidate.RunId == prior.RunId);
      var secondRecovery = AttemptRecoveryAsync(
          secondService,
          prior.RunId,
          Task.CompletedTask);
      var abandon = AttemptAbandonAsync(secondService, prior.RunId);
      await Task.WhenAll((Task)secondRecovery, abandon)
          .WaitAsync(TimeSpan.FromSeconds(2));
      Assert.IsType<InvalidOperationException>((await secondRecovery).Error);
      Assert.IsType<InvalidOperationException>(await abandon);
      Assert.Equal(1, provider.ApplyCalls);
      var persisted = await CreateStore(clock).GetAsync(prior.RunId, CancellationToken.None);
      Assert.Equal(ExecutionState.Running, persisted!.State);
    }
    finally
    {
      provider.ReleaseApply.TrySetResult();
    }

    var owner = await firstRecovery.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Null(owner.Error);
    Assert.Equal(ExecutionOutcome.Succeeded, owner.Run!.Outcome);
  }

  [Fact]
  public async Task RecoverAsync_ImmediatelyReconcilesAfterClaimFinalizationFailure()
  {
    var clock = new MutableTimeProvider(
        new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
    var provider = new GatedProvider { WaitForRelease = false };
    var backingStore = CreateStore(clock);
    var prior = InterruptedRun();
    await backingStore.CreateAsync(prior, CancellationToken.None);
    var faultingStore = new ThrowOnTrySaveCallStore(backingStore, throwOnCall: 2);
    var firstService = CreateService(provider, faultingStore, clock);

    await Assert.ThrowsAsync<IOException>(() =>
        firstService.RecoverAsync(prior.RunId, CancellationToken.None));

    var runsAfterFailure = await CreateStore(clock).ListAsync(CancellationToken.None);
    var replacement = Assert.Single(
        runsAfterFailure,
        run => run.RetriedFromRunId == prior.RunId &&
            run.Outcome == ExecutionOutcome.Succeeded);
    var claimedPrior = Assert.Single(runsAfterFailure, run => run.RunId == prior.RunId);
    Assert.NotNull(claimedPrior.RecoveryClaimId);
    var secondService = CreateService(provider, CreateStore(clock), clock);
    Assert.Contains(
        await secondService.FindRecoveryCandidatesAsync(CancellationToken.None),
        candidate => candidate.RunId == prior.RunId);
    var recovered = await secondService.RecoverAsync(prior.RunId, CancellationToken.None);

    var persistedPrior = await CreateStore(clock).GetAsync(prior.RunId, CancellationToken.None);
    Assert.Equal(replacement.RunId, recovered.RunId);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persistedPrior!.State);
    Assert.Null(persistedPrior.RecoveryClaimId);
  }

  [Fact]
  public async Task RecoverAsync_PreservesRecoveryFailureWhenClaimReleaseAlsoFails()
  {
    var provider = new GatedProvider { WaitForRelease = false };
    var backingStore = CreateStore();
    var prior = InterruptedRun();
    await backingStore.CreateAsync(prior, CancellationToken.None);
    var faultingStore = new ThrowOnTrySaveCallStore(
        backingStore,
        throwOnCall: 2,
        listException: new InvalidDataException("Injected recovery lookup failure."));
    var service = CreateService(provider, faultingStore);

    var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
        service.RecoverAsync(prior.RunId, CancellationToken.None));

    Assert.IsType<IOException>(error.Data["RecoveryClaimReleaseException"]);
    Assert.Equal(0, provider.ApplyCalls);
    var persistedPrior = await CreateStore().GetAsync(prior.RunId, CancellationToken.None);
    Assert.NotNull(persistedPrior!.RecoveryClaimId);
  }

  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }

  private JsonExecutionRunStore CreateStore(TimeProvider? timeProvider = null) => new(
      new WdemDataPaths(_directory),
      new LogRedactor(),
      timeProvider);

  private static EnvironmentRunService CreateService(
      IResourceProvider provider,
      IExecutionRunStore store,
      TimeProvider? timeProvider = null)
  {
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    return new EnvironmentRunService(
        new FixedProfileCatalog(Profile()),
        new ResourceGraphBuilder(),
        registry,
        compliance,
        new ExecutionPlanner(registry, compliance),
        new ResourceScheduler(),
        store,
        new DirectResourceApplyDispatcher(),
        timeProvider ?? TimeProvider.System);
  }

  private static async Task<RecoveryAttempt> AttemptRecoveryAsync(
      IEnvironmentRunService service,
      Guid runId,
      Task start)
  {
    await start;
    try
    {
      return new RecoveryAttempt(
          await service.RecoverAsync(runId, CancellationToken.None),
          null);
    }
    catch (Exception exception)
    {
      return new RecoveryAttempt(null, exception);
    }
  }

  private static async Task<Exception?> AttemptAbandonAsync(
      IEnvironmentRunService service,
      Guid runId)
  {
    try
    {
      await service.AbandonAsync(runId, CancellationToken.None);
      return null;
    }
    catch (Exception exception)
    {
      return exception;
    }
  }

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
    ProfileSourcePath = Path.GetFullPath("input/profile.yaml"),
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
        DetectedBefore = Missing()
      }
    }
  };

  private static DetectedState Missing() => new()
  {
    ResourceId = "git",
    Outcome = DetectionOutcome.Succeeded,
    Exists = false
  };

  private sealed record RecoveryAttempt(ExecutionRun? Run, Exception? Error);

  private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
  {
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
  }

  private sealed class GatedRecoveryOperationStore(IExecutionRunStore inner) :
      ForwardingRunStore(inner)
  {
    public TaskCompletionSource AcquireEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseAcquire { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
      AcquireEntered.TrySetResult();
      await ReleaseAcquire.Task.WaitAsync(cancellationToken);
      return await Inner.TryAcquireRecoveryOperationAsync(runId, cancellationToken);
    }
  }

  private sealed class ThrowOnTrySaveCallStore(
      IExecutionRunStore inner,
      int throwOnCall,
      Exception? listException = null) : ForwardingRunStore(inner)
  {
    private int _trySaveCalls;
    private Exception? _listException = listException;

    public override Task<IReadOnlyList<ExecutionRun>> ListAsync(
        CancellationToken cancellationToken)
    {
      var exception = Interlocked.Exchange(ref _listException, null);
      if (exception is not null)
      {
        throw exception;
      }

      return Inner.ListAsync(cancellationToken);
    }

    public override Task<bool> TrySaveAsync(
        ExecutionRun run,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken)
    {
      if (Interlocked.Increment(ref _trySaveCalls) == throwOnCall)
      {
        throw new IOException("Injected recovery claim persistence failure.");
      }

      return Inner.TrySaveAsync(
          run,
          expectedRevision,
          expectedRecoveryClaimId,
          cancellationToken);
    }
  }

  private abstract class ForwardingRunStore(IExecutionRunStore inner) : IExecutionRunStore
  {
    protected IExecutionRunStore Inner { get; } = inner;
    public IReadOnlyList<StructuredError> Diagnostics => Inner.Diagnostics;

    public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken) =>
        Inner.CreateAsync(run, cancellationToken);

    public Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Inner.GetAsync(runId, cancellationToken);

    public virtual Task<IReadOnlyList<ExecutionRun>> ListAsync(
        CancellationToken cancellationToken) =>
        Inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
        CancellationToken cancellationToken) =>
        Inner.ListIncompleteAsync(cancellationToken);

    public virtual Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        Inner.TryAcquireRecoveryOperationAsync(runId, cancellationToken);

    public Task<ExecutionRun> SaveAsync(
        ExecutionRun run,
        CancellationToken cancellationToken) =>
        Inner.SaveAsync(run, cancellationToken);

    public virtual Task<bool> TrySaveAsync(
        ExecutionRun run,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken) =>
        Inner.TrySaveAsync(
            run,
            expectedRevision,
            expectedRecoveryClaimId,
            cancellationToken);

    public Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken) =>
        Inner.AppendLogAsync(runId, entry, cancellationToken);

    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) =>
        Inner.ReadLogPageAsync(runId, afterSequence, take, cancellationToken);
  }

  private sealed class FixedProfileCatalog(DeveloperProfile profile) : IProfileCatalog
  {
    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(id, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(new ProfileLoadResult
      {
        Profile = profile,
        SourcePath = Path.GetFullPath(path)
      });
    }

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([]);
  }

  private sealed class GatedProvider : IResourceProvider
  {
    private int _applyCalls;

    public string ResourceType => "package";
    public string ProviderName => "fake";
    public ProviderCapabilities Capabilities { get; } = new()
    {
      MaxConcurrentOperations = 2
    };
    public int ApplyCalls => Volatile.Read(ref _applyCalls);
    public ApplyOutcome Outcome { get; init; } = ApplyOutcome.Succeeded;
    public bool WaitForRelease { get; init; } = true;
    public TaskCompletionSource FirstApplyEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SecondApplyEntered { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseApply { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<ProviderValidationResult> ValidateAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ProviderValidationResult.Valid);

    public ValueTask<DetectedState> DetectAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Missing());

    public ValueTask<ResourcePlan> PlanAsync(
        ResourceDefinition resource,
        DetectedState currentState,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ResourcePlan
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
              Id = "install",
              Description = "Install git",
              Action = PlanAction.Install,
              PrivilegeRequirement = PrivilegeRequirement.CurrentUser,
              RestartPolicy = RestartPolicy.NoRestart
            }
          ]
        });

    public async ValueTask<ResourceApplyResult> ApplyAsync(
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken)
    {
      var call = Interlocked.Increment(ref _applyCalls);
      (call == 1 ? FirstApplyEntered : SecondApplyEntered).TrySetResult();
      if (WaitForRelease)
      {
        await ReleaseApply.Task.WaitAsync(cancellationToken);
      }

      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = Outcome,
        Error = Outcome == ApplyOutcome.Failed
            ? new StructuredError(
                WdemErrorCode.ProviderError,
                "Provider failed.",
                "apply failed")
            {
              ResourceId = resource.Id,
              IsRetryable = true
            }
            : null
      };
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new VerificationResult
        {
          ResourceId = resource.Id,
          Compliance = ComplianceStatus.Satisfied,
          DetectedState = new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Succeeded,
            Exists = true,
            Version = "2.52.1",
            InstalledVersions = [new SemanticVersion(2, 52, 1)]
          }
        });
  }
}
