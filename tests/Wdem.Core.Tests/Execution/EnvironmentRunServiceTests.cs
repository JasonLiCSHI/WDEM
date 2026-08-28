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
  public async Task InspectAsync_InvalidProfilePreservesValidationDiagnostics()
  {
    var error = new StructuredError(
        WdemErrorCode.ProfileError,
        "Profile validation failed.",
        "The profile version is required.");
    var provider = new ScriptedProvider(Missing("git"));
    var catalog = new FixedProfileCatalog(new ProfileLoadResult
    {
      SourcePath = CanonicalProfilePath,
      Errors = [error]
    });
    var (service, store) = CreateService(provider, catalog: catalog);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(error, Assert.Single(Assert.IsType<ExecutionPlan>(run.Plan).Errors));
    var stored = await store.GetAsync(
        run.RunId,
        CancellationToken.None);
    Assert.Equal(error, Assert.Single(Assert.IsType<ExecutionPlan>(stored!.Plan).Errors));
  }

  [Fact]
  public async Task InspectAsync_InvalidProfilePathPersistsCatalogFailureWithoutThrowing()
  {
    const string invalidPath = "invalid\0profile.yaml";
    var provider = new ScriptedProvider(Missing("git"));
    var catalog = new DirectoryProfileCatalog(
        Path.GetTempPath(),
        new ResourceProviderRegistry([provider]));
    var (service, store) = CreateService(provider, catalog: catalog);

    var run = await service.InspectAsync(
        new RunRequest(
            invalidPath,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
        CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(invalidPath, run.ProfileSourcePath);
    Assert.Equal(WdemErrorCode.ProfileError, Assert.Single(run.Plan!.Errors).Code);
    Assert.Equal(run, await store.GetAsync(run.RunId, CancellationToken.None));
  }

  [Fact]
  public async Task InspectAsync_InvalidDagPreservesDependencyDiagnostics()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var profile = Profile() with
    {
      Resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = Profile().Resources["git"] with { Dependencies = ["missing"] }
      }
    };
    var (service, _) = CreateService(provider, profile);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    var error = Assert.Single(Assert.IsType<ExecutionPlan>(run.Plan).Errors);
    Assert.Equal(WdemErrorCode.DependencyError, error.Code);
    Assert.Contains("undefined resource 'missing'", error.Detail, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InspectAsync_PreparationFailurePlanIdentityIsContentAddressed()
  {
    var error = new StructuredError(
        WdemErrorCode.ProfileError,
        "Profile validation failed.",
        "The profile version is required.");
    var provider = new ScriptedProvider(Missing("git"));
    var catalog = new FixedProfileCatalog(new ProfileLoadResult
    {
      SourcePath = CanonicalProfilePath,
      Errors = [error]
    });
    var (service, _) = CreateService(provider, catalog: catalog);

    var first = (await service.InspectAsync(Request(), CancellationToken.None)).Plan!;
    var second = (await service.InspectAsync(Request(), CancellationToken.None)).Plan!;
    var changedCatalog = new FixedProfileCatalog(new ProfileLoadResult
    {
      SourcePath = CanonicalProfilePath,
      Errors = [error with { Detail = "The profile id is required." }]
    });
    var (changedService, _) = CreateService(provider, catalog: changedCatalog);
    var changed = (await changedService.InspectAsync(Request(), CancellationToken.None)).Plan!;

    Assert.Equal(64, first.Fingerprint.Length);
    Assert.Equal(first.Fingerprint, second.Fingerprint);
    Assert.Equal(first.PlanId, second.PlanId);
    Assert.Equal(
        new Guid(Convert.FromHexString(first.Fingerprint).AsSpan(0, 16)),
        first.PlanId);
    Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
    Assert.NotEqual(first.PlanId, changed.PlanId);
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
  public async Task RetryAsync_PlansOnlyRequestedResourcesAndTheirDependencies()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git"
          ? Missing("git")
          : new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Failed,
            Error = "unrelated detection failed"
          }
    };
    var (service, store) = CreateService(provider, Profile(includeBrokenResource: true));
    var prior = FailedRun("git", "broken");
    await store.CreateAsync(prior, CancellationToken.None);

    var retried = await service.RetryAsync(
        prior.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, retried.ResourceResults["git"].Outcome);
    Assert.Equal(["git"], provider.DetectedResourceIds);
    Assert.Equal(["git"], retried.Plan!.Resources.Select(resource => resource.Definition.Id));
    Assert.Equal(1, provider.ApplyCalls);
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
  public async Task ApplyAsync_NotRequiredCannotSucceedWhenResourceStillNeedsRemediation()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.NotRequired
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.Equal(ComplianceStatus.Missing, run.ResourceResults["git"].FinalCompliance);
  }

  [Fact]
  public async Task ApplyAsync_UnexecutablePlanPreservesSatisfiedResourceEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => resource.Id == "git"
          ? Satisfied("git", "2.52.1")
          : new DetectedState
          {
            ResourceId = resource.Id,
            Outcome = DetectionOutcome.Failed,
            Error = "detection failed"
          }
    };
    var (service, _) = CreateService(provider, Profile(includeBrokenResource: true));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.NotRequired, run.ResourceResults["git"].Outcome);
    Assert.Equal(ComplianceStatus.Satisfied, run.ResourceResults["git"].FinalCompliance);
    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["broken"].Outcome);
    Assert.Equal(ComplianceStatus.DetectionFailed, run.ResourceResults["broken"].FinalCompliance);
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
  public async Task ApplyAsync_PersistsSchedulerReadyAndBlockedTransitions()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      DetectState = resource => Missing(resource.Id),
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        Error = ProviderError("git", "apply failed")
      }
    };
    var (service, store) = CreateService(provider, Profile(includeDependentResource: true));

    await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Contains(store.SavedSnapshots, snapshot =>
        snapshot.State == ExecutionState.Running &&
        snapshot.ResourceResults["git"].State == ExecutionState.Ready);
    Assert.Contains(store.SavedSnapshots, snapshot =>
        snapshot.State == ExecutionState.Running &&
        snapshot.ResourceResults["dependent"].State == ExecutionState.Blocked);
  }

  [Fact]
  public async Task RunTransitions_FailedSaveLeavesCurrentAtLastPersistedSnapshot()
  {
    var initial = InterruptedRun() with
    {
      State = ExecutionState.Running,
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = new()
        {
          ResourceId = "git",
          State = ExecutionState.Pending,
          DetectedBefore = Missing("git")
        }
      }
    };
    var store = new InMemoryRunStore
    {
      SaveOperation = (_, _) => Task.FromException(
          new InvalidOperationException("snapshot failed"))
    };
    await store.CreateAsync(initial, CancellationToken.None);
    var transitionsType = typeof(EnvironmentRunService).GetNestedType(
        "RunTransitions",
        System.Reflection.BindingFlags.NonPublic)!;
    var transitions = Activator.CreateInstance(
        transitionsType,
        System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic,
        binder: null,
        args: [store, initial],
        culture: null)!;
    var setResource = transitionsType.GetMethod(
        "SetResourceAsync",
        System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public)!;
    var save = Assert.IsAssignableFrom<Task>(setResource.Invoke(
        transitions,
        [new ResourceResult { ResourceId = "git", State = ExecutionState.Ready },
         CancellationToken.None]));

    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => save);

    Assert.Equal("snapshot failed", error.Message);
    var current = Assert.IsType<ExecutionRun>(transitionsType.GetProperty("Current")!.GetValue(
        transitions));
    Assert.Same(initial, current);
    Assert.Same(initial, await store.GetAsync(initial.RunId, CancellationToken.None));
  }

  [Fact]
  public async Task ApplyAsync_PersistsCancelledTerminalStateWithIndependentIoToken()
  {
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = _ =>
      {
        cancellation.Cancel();
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled
        });
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, run.ResourceResults["git"].Outcome);
    Assert.Equal(run, await store.GetAsync(run.RunId, CancellationToken.None));
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
  public async Task RecoverAsync_SuccessConsumesIncompletePriorAcrossServices()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var interrupted = InterruptedRun();
    await firstStore.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    var secondStore = new InMemoryRunStore(sharedRuns);
    var (secondService, _) = CreateService(provider, store: secondStore);
    var persistedPrior = await secondStore.GetAsync(interrupted.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        secondService.RecoverAsync(interrupted.RunId, CancellationToken.None));
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(ExecutionState.Completed, persistedPrior!.State);
    Assert.Equal(ExecutionOutcome.Cancelled, persistedPrior.Outcome);
    Assert.NotNull(persistedPrior.EndedAtUtc);
    Assert.False(persistedPrior.ResourceResults["git"].DetectedBefore!.Exists);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == interrupted.RunId);
    Assert.Contains("not eligible for recovery", error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(ApplyOutcome.Failed)]
  [InlineData(ApplyOutcome.Cancelled)]
  public async Task RecoverAsync_UnsuccessfulAttemptKeepsIncompletePriorAcrossServices(
      ApplyOutcome applyOutcome)
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = applyOutcome,
        Error = applyOutcome == ApplyOutcome.Failed
            ? ProviderError("git", "apply failed")
            : null
      }
    };
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var interrupted = InterruptedRun();
    await firstStore.CreateAsync(interrupted, CancellationToken.None);

    var recovered = await service.RecoverAsync(interrupted.RunId, CancellationToken.None);

    var secondStore = new InMemoryRunStore(sharedRuns);
    var (secondService, _) = CreateService(provider, store: secondStore);
    var persistedPrior = await secondStore.GetAsync(interrupted.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    Assert.Equal(
        applyOutcome == ApplyOutcome.Cancelled
            ? ExecutionOutcome.Cancelled
            : ExecutionOutcome.Failed,
        recovered.Outcome);
    Assert.Equal(ExecutionState.Running, persistedPrior!.State);
    Assert.Null(persistedPrior.Outcome);
    Assert.Null(persistedPrior.EndedAtUtc);
    Assert.Empty(persistedPrior.AcknowledgedRestartResourceIds);
    Assert.Contains(candidates, candidate => candidate.RunId == interrupted.RunId);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_IncludesCompletedRunWithPendingRestart()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    var candidate = Assert.Single(candidates);
    Assert.Equal(pendingRestart.RunId, candidate.RunId);
    Assert.Equal(["git"], candidate.PendingResourceIds);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_CompletedRunListsOnlyPendingRestartResources()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    var unrelatedFailure = FailedRun("broken").ResourceResults["broken"];
    await store.CreateAsync(pendingRestart with
    {
      ResourceResults = pendingRestart.ResourceResults
          .Append(new KeyValuePair<string, ResourceResult>("broken", unrelatedFailure))
          .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
    }, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);

    var candidate = Assert.Single(candidates);
    Assert.Equal(["git"], candidate.PendingResourceIds);
  }

  [Fact]
  public async Task RecoverAsync_SuccessAcknowledgesPriorRestartCandidate()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    var recovered = await service.RecoverAsync(
        pendingRestart.RunId,
        CancellationToken.None);
    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var persistedPrior = await store.GetAsync(pendingRestart.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == pendingRestart.RunId);
    Assert.Equal(
        RestartPolicy.RestartRequired,
        persistedPrior!.ResourceResults["git"].RestartRequirement);
    Assert.Equal([RestartPolicy.RestartRequired], persistedPrior.RestartRequirements);
  }

  [Theory]
  [InlineData(ApplyOutcome.Failed)]
  [InlineData(ApplyOutcome.Cancelled)]
  public async Task RecoverAsync_UnsuccessfulAttemptKeepsPriorRestartCandidate(
      ApplyOutcome applyOutcome)
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = applyOutcome,
        Error = applyOutcome == ApplyOutcome.Failed
            ? ProviderError("git", "apply failed")
            : null
      }
    };
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    var recovered = await service.RecoverAsync(
        pendingRestart.RunId,
        CancellationToken.None);
    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var persistedPrior = await store.GetAsync(pendingRestart.RunId, CancellationToken.None);

    Assert.Contains(candidates, candidate => candidate.RunId == pendingRestart.RunId);
    Assert.Empty(persistedPrior!.AcknowledgedRestartResourceIds);
    Assert.Equal(
        applyOutcome == ApplyOutcome.Cancelled
            ? ExecutionOutcome.Cancelled
            : ExecutionOutcome.Failed,
        recovered.Outcome);
  }

  [Fact]
  public async Task FindRecoveryCandidatesAsync_DiscoversRunPersistedBeforeProviderCompletes()
  {
    var applyEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseApply = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = async cancellationToken =>
      {
        applyEntered.SetResult();
        await releaseApply.Task.WaitAsync(cancellationToken);
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Succeeded
        };
      }
    };
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var applyTask = service.ApplyAsync(Request(), CancellationToken.None);

    try
    {
      await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

      var secondStore = new InMemoryRunStore(sharedRuns);
      var (recoveryService, _) = CreateService(
          new ScriptedProvider(Missing("git")),
          store: secondStore);
      var persisted = Assert.Single(
          await secondStore.ListAsync(CancellationToken.None));
      var candidates = await recoveryService.FindRecoveryCandidatesAsync(
          CancellationToken.None);

      Assert.Equal(ExecutionState.Running, persisted.State);
      Assert.Equal(ExecutionState.Running, persisted.ResourceResults["git"].State);
      var candidate = Assert.Single(candidates);
      Assert.Equal(persisted.RunId, candidate.RunId);
      Assert.Equal(["git"], candidate.PendingResourceIds);
    }
    finally
    {
      releaseApply.TrySetResult();
      await applyTask;
    }
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

  [Fact]
  public async Task AbandonAsync_IncompleteRestartRunIsNotRecoverableAcrossServices()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var sharedRuns = new Dictionary<Guid, ExecutionRun>();
    var firstStore = new InMemoryRunStore(sharedRuns);
    var (service, _) = CreateService(provider, store: firstStore);
    var interrupted = IncompleteRestartRun();
    await firstStore.CreateAsync(interrupted, CancellationToken.None);

    await service.AbandonAsync(interrupted.RunId, CancellationToken.None);

    var secondStore = new InMemoryRunStore(sharedRuns);
    var (secondService, _) = CreateService(provider, store: secondStore);
    var persisted = await secondStore.GetAsync(interrupted.RunId, CancellationToken.None);
    var candidates = await secondService.FindRecoveryCandidatesAsync(CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Equal(ExecutionOutcome.Cancelled, persisted.Outcome);
    Assert.Contains("git", persisted.AcknowledgedRestartResourceIds);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == interrupted.RunId);
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task AbandonAsync_AcknowledgesCompletedRestartCandidateWithoutApplying()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"));
    var (service, store) = CreateService(provider);
    var pendingRestart = RestartPendingRun();
    await store.CreateAsync(pendingRestart, CancellationToken.None);

    await service.AbandonAsync(pendingRestart.RunId, CancellationToken.None);

    var candidates = await service.FindRecoveryCandidatesAsync(CancellationToken.None);
    var persisted = await store.GetAsync(pendingRestart.RunId, CancellationToken.None);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == pendingRestart.RunId);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(
        RestartPolicy.RestartRequired,
        persisted!.ResourceResults["git"].RestartRequirement);
    Assert.Equal([RestartPolicy.RestartRequired], persisted.RestartRequirements);
  }

  private static (EnvironmentRunService Service, InMemoryRunStore Store) CreateService(
      ScriptedProvider provider,
      DeveloperProfile? profile = null,
      IProfileCatalog? catalog = null,
      InMemoryRunStore? store = null)
  {
    catalog ??= new FakeProfileCatalog(profile ?? Profile(), CanonicalProfilePath);
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    store ??= new InMemoryRunStore();
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

  private static DeveloperProfile Profile(
      bool includeBrokenResource = false,
      bool includeDependentResource = false)
  {
    var required = new List<ProfileResourceReference>
    {
      new() { Id = "git" }
    };
    var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase)
    {
      ["git"] = new ResourceDefinition
      {
        Id = "git",
        Type = "package",
        Provider = "fake",
        VersionConstraint = ">=2.50.0"
      }
    };
    if (includeBrokenResource)
    {
      required.Add(new ProfileResourceReference { Id = "broken" });
      resources.Add("broken", new ResourceDefinition
      {
        Id = "broken",
        Type = "package",
        Provider = "fake"
      });
    }

    if (includeDependentResource)
    {
      required.Add(new ProfileResourceReference { Id = "dependent" });
      resources.Add("dependent", new ResourceDefinition
      {
        Id = "dependent",
        Type = "package",
        Provider = "fake",
        Dependencies = ["git"]
      });
    }

    return new DeveloperProfile
    {
      Id = "developer",
      Version = "1.0.0",
      DisplayName = "Developer",
      Description = "Developer workstation",
      RequiredResources = required,
      Resources = resources
    };
  }

  private static ExecutionRun FailedRun(params string[] resourceIds)
  {
    var endedAt = DateTimeOffset.UtcNow;
    return new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = RunMode.Apply,
      ProfileSourcePath = CanonicalProfilePath,
      ProfileId = "developer",
      ProfileVersion = "1.0.0",
      SelectedOptionalResourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
      StartedAtUtc = endedAt.AddMinutes(-1),
      EndedAtUtc = endedAt,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      Machine = new MachineInformation("test", "x64", "test", "test"),
      ResourceResults = resourceIds.ToDictionary(
          id => id,
          id => new ResourceResult
          {
            ResourceId = id,
            State = ExecutionState.Completed,
            Outcome = ExecutionOutcome.Failed,
            FinalCompliance = ComplianceStatus.Missing,
            EndedAtUtc = endedAt
          },
          StringComparer.OrdinalIgnoreCase)
    };
  }

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

  private static ExecutionRun RestartPendingRun()
  {
    var completedAt = DateTimeOffset.UtcNow;
    return FailedRun("git") with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Succeeded,
      EndedAtUtc = completedAt,
      RestartRequirements = [RestartPolicy.RestartRequired],
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = new()
        {
          ResourceId = "git",
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          FinalCompliance = ComplianceStatus.Satisfied,
          EndedAtUtc = completedAt,
          RestartRequirement = RestartPolicy.RestartRequired
        }
      }
    };
  }

  private static ExecutionRun IncompleteRestartRun()
  {
    var completedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
    return InterruptedRun() with
    {
      RestartRequirements = [RestartPolicy.RestartRequired],
      ResourceResults = new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
      {
        ["git"] = new()
        {
          ResourceId = "git",
          State = ExecutionState.Completed,
          Outcome = ExecutionOutcome.Succeeded,
          FinalCompliance = ComplianceStatus.Satisfied,
          EndedAtUtc = completedAt,
          RestartRequirement = RestartPolicy.RestartRequired
        },
        ["pending"] = new()
        {
          ResourceId = "pending",
          State = ExecutionState.Running,
          DetectedBefore = Missing("pending")
        }
      }
    };
  }

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

  private sealed class FixedProfileCatalog(ProfileLoadResult result) : IProfileCatalog
  {
    public Task<ProfileLoadResult> LoadAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        LoadFileAsync(result.SourcePath, cancellationToken);

    public Task<ProfileLoadResult> LoadFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ProfileLoadResult>> LoadAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProfileLoadResult>>([result]);
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
    public Func<ResourceDefinition, DetectedState>? DetectState { get; init; }
    public Func<CancellationToken, ValueTask<ResourceApplyResult>>? ApplyOperation { get; init; }
    public List<string> DetectedResourceIds { get; } = [];
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
      DetectedResourceIds.Add(resource.Id);
      if (DetectState is not null)
      {
        return ValueTask.FromResult(DetectState(resource));
      }

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
      var detectionSucceeded = currentState.Outcome == DetectionOutcome.Succeeded;
      var satisfied = detectionSucceeded && currentState.Exists;
      return ValueTask.FromResult(new ResourcePlan
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = !detectionSucceeded
            ? ComplianceStatus.DetectionFailed
            : satisfied ? ComplianceStatus.Satisfied : ComplianceStatus.Missing,
        IsExecutable = detectionSucceeded,
        Steps = satisfied || !detectionSucceeded
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
      return ApplyOperation?.Invoke(cancellationToken) ?? ValueTask.FromResult(ApplyResult);
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
    private readonly Dictionary<Guid, ExecutionRun> _runs;

    public InMemoryRunStore(Dictionary<Guid, ExecutionRun>? runs = null)
    {
      _runs = runs ?? [];
    }

    public IReadOnlyList<StructuredError> Diagnostics => [];
    public List<ExecutionRun> SavedSnapshots { get; } = [];
    public Func<ExecutionRun, CancellationToken, Task>? SaveOperation { get; init; }

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

    public Task<IReadOnlyList<ExecutionRun>> ListAsync(CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      return Task.FromResult<IReadOnlyList<ExecutionRun>>(_runs.Values.ToArray());
    }

    public Task SaveAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (SaveOperation is not null)
      {
        return SaveOperation(run, cancellationToken);
      }

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
