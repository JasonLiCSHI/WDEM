using System.Diagnostics;
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
  public async Task ApplyAsync_AbsentActualRestartEvidenceRetainsPlannedRestartPolicy()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      PlannedRestartPolicy = RestartPolicy.RestartRequired
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(
        RestartPolicy.RestartRequired,
        run.ResourceResults["git"].RestartRequirement);
    Assert.Contains(RestartPolicy.RestartRequired, run.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_UsesActualProviderRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = RestartPolicy.RestartRecommended
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(
        RestartPolicy.RestartRecommended,
        run.ResourceResults["git"].RestartRequirement);
    Assert.Contains(RestartPolicy.RestartRecommended, run.RestartRequirements);
  }

  [Theory]
  [InlineData(3010, RestartPolicy.RestartRecommended)]
  [InlineData(1641, RestartPolicy.RestartRequired)]
  public async Task ApplyAsync_SuccessfulRestartExitRemainsSuccessful(
      int exitCode,
      RestartPolicy restartRequirement)
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = restartRequirement,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = exitCode,
            Succeeded = true,
            Message = $"restartRequirement={restartRequirement}"
          }
        ]
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Succeeded, run.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, resource.Outcome);
    Assert.Equal(ExecutionOutcome.Succeeded, step.Outcome);
    Assert.Equal(exitCode, step.ProcessExitCode);
    Assert.True(step.ProcessSucceeded);
    Assert.Equal(restartRequirement, resource.RestartRequirement);
    Assert.Contains(restartRequirement, persisted!.RestartRequirements);
  }

  [Theory]
  [InlineData(3010, RestartPolicy.RestartRecommended)]
  [InlineData(1641, RestartPolicy.RestartRequired)]
  public async Task ApplyAsync_CancelledDuringFinalVerifyPreservesCompletedStepEvidence(
      int exitCode,
      RestartPolicy restartRequirement)
  {
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = restartRequirement,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = exitCode,
            Succeeded = true,
            Message = $"restartRequirement={restartRequirement}"
          }
        ]
      },
      VerificationOperation = token =>
      {
        cancellation.Cancel();
        return ValueTask.FromCanceled<VerificationResult>(token);
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Cancelled, run.Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(restartRequirement, resource.RestartRequirement);
    Assert.Equal(exitCode, step.ProcessExitCode);
    Assert.True(step.ProcessSucceeded);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(restartRequirement, persisted.RestartRequirements);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(1, provider.VerifyCalls);
  }

  [Fact]
  public async Task ApplyAsync_FailedProviderRetainsAndPersistsRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Failed,
        RestartRequirement = RestartPolicy.RestartRequired,
        Error = ProviderError("git", "Post-install verification failed.")
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, run.ResourceResults["git"].RestartRequirement);
    Assert.Equal([RestartPolicy.RestartRequired], persisted!.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_CoreVerificationFailureRetainsRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = RestartPolicy.RestartRecommended
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.Missing,
        DetectedState = Missing("git")
      }
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionOutcome.Failed, run.ResourceResults["git"].Outcome);
    Assert.Equal(
        RestartPolicy.RestartRecommended,
        run.ResourceResults["git"].RestartRequirement);
    Assert.Contains(RestartPolicy.RestartRecommended, run.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_EnrichesSuppliedFinalVerificationErrorWithProcessEvidence()
  {
    var suppliedError = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Supplied final verification failure.",
        "Keep this safe verification detail.")
    {
      SuggestedAction = "Keep this safe suggested action.",
      IsRetryable = true
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = 3010,
            Succeeded = true
          }
        ]
      },
      VerificationResult = new VerificationResult
      {
        ResourceId = "git",
        Compliance = ComplianceStatus.ConfigurationMismatch,
        DetectedState = Missing("git")
      }
    };
    var (service, _) = CreateService(
        provider,
        complianceEvaluator: new FinalErrorComplianceEvaluator(suppliedError));

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var error = run.ResourceResults["git"].Error!;
    Assert.Equal(WdemErrorCode.ConfigurationError, error.Code);
    Assert.Equal(suppliedError.Summary, error.Summary);
    Assert.Equal(suppliedError.Detail, error.Detail);
    Assert.Equal(suppliedError.SuggestedAction, error.SuggestedAction);
    Assert.True(error.IsRetryable);
    Assert.Equal("git", error.ResourceId);
    Assert.Equal("install", error.StepId);
    Assert.Equal(3010, error.ProcessExitCode);
  }

  [Fact]
  public async Task ApplyAsync_VerificationExceptionRetainsRestartEvidence()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        RestartRequirement = RestartPolicy.RestartRequired,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            ProcessExitCode = 1641,
            Succeeded = true
          }
        ]
      },
      VerificationOperation = _ => throw new IOException("verification unavailable")
    };
    var (service, _) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    var resource = run.ResourceResults["git"];
    Assert.Equal(ExecutionOutcome.Failed, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, resource.RestartRequirement);
    Assert.Equal("git", resource.Error!.ResourceId);
    Assert.Equal("install", resource.Error.StepId);
    Assert.Equal(1641, resource.Error.ProcessExitCode);
    Assert.Equal(typeof(IOException).FullName, resource.Error.UnderlyingExceptionType);
  }

  [Fact]
  public async Task ApplyAsync_PublishesPersistedRunEventsInSequenceWithoutDroppingDetails()
  {
    var provider = new ScriptedProvider(Missing("git"))
    {
      ProgressEvents =
      [
        new ProviderProgress("install", 0.4, "installing", "install"),
        new ProviderProgress("install", 0.8, "provider-log hunter2", "install")
      ],
      ApplyResult = new ResourceApplyResult
      {
        ResourceId = "git",
        Outcome = ApplyOutcome.Succeeded,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = "install",
            Action = PlanAction.Install,
            Progress = 1,
            Message = "step-complete"
          }
        ],
        Diagnostics =
        [
          new StructuredError(
              WdemErrorCode.ProviderError,
              "provider diagnostic hunter2",
              "provider diagnostic detail hunter2")
          {
            UnderlyingException = new InvalidOperationException("hunter2")
          }
        ]
      }
    };
    var sink = new RunEventHub();
    var events = new List<RunEvent>();
    var profile = Profile();
    profile = profile with
    {
      Resources = profile.Resources.ToDictionary(
          pair => pair.Key,
          pair => pair.Value with
          {
            Parameters = new Dictionary<string, string?> { ["access_token"] = "hunter2" }
          },
          StringComparer.OrdinalIgnoreCase)
    };
    var (service, store) = CreateService(provider, profile, eventSink: sink);
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      Assert.NotNull(await store.GetAsync(runEvent.RunId, cancellationToken));
      events.Add(runEvent);
    });

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.NotEmpty(events);
    Assert.All(events, runEvent => Assert.Equal(run.RunId, runEvent.RunId));
    Assert.Equal(
        Enumerable.Range(1, events.Count).Select(value => (long)value),
        events.Select(runEvent => runEvent.Sequence));
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.RunStateChanged);
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.ResourceStateChanged);
    Assert.Contains(events, runEvent => runEvent.Kind == RunEventKind.StepProgress);
    Assert.Contains(events, runEvent =>
        runEvent.Kind == RunEventKind.Log && runEvent.Message == "provider-log ***");
    Assert.DoesNotContain(events, runEvent => runEvent.Message.Contains("hunter2", StringComparison.Ordinal));
    Assert.DoesNotContain(events, runEvent =>
        runEvent.Error?.Detail.Contains("hunter2", StringComparison.Ordinal) == true ||
        runEvent.Error?.UnderlyingExceptionMessage?.Contains("hunter2", StringComparison.Ordinal) == true);
    Assert.Equal(RunEventKind.Completed, events[^1].Kind);
  }

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

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task ApplyAsync_CleanupDiagnosticFailure_PersistsTerminalRun(
      bool failDurableAppend)
  {
    var provider = new ScriptedProvider(Missing("git"));
    var store = new InMemoryRunStore();
    if (failDurableAppend)
    {
      store.AppendLogOperation = (_, entry, _) =>
          entry.Error?.Summary == "Elevated host cleanup failed."
              ? Task.FromException(new IOException("cleanup log unavailable"))
              : Task.CompletedTask;
    }

    var sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired((runEvent, _) =>
        !failDurableAppend && runEvent.Message == "Elevated host cleanup failed."
            ? Task.FromException(new IOException("cleanup observer unavailable"))
            : Task.CompletedTask);
    var (service, _) = CreateService(
        provider,
        store: store,
        eventSink: sink,
        dispatcher: new CleanupFailingDispatcher());

    var run = await service.ApplyAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, run.State);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(ExecutionState.Completed, persisted!.State);
    Assert.Equal(run.Outcome, persisted.Outcome);
  }

  [Fact]
  public async Task InspectAsync_AnyAcceptedPlanErrorFailsRunAndSkipsResource()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = new ComplianceEvaluator();
    var error = new StructuredError(
        WdemErrorCode.ConfigurationError,
        "Configuration cannot be applied.",
        "The accepted plan contains an invalid setting.");
    var planner = new TransformingPlanner(
        new ExecutionPlanner(registry, compliance),
        plan => plan with { Errors = [error] });
    var (service, _) = CreateService(provider, planner: planner);

    var run = await service.InspectAsync(Request(), CancellationToken.None);

    Assert.Equal(ExecutionState.Completed, run.State);
    Assert.Equal(ExecutionOutcome.Failed, run.Outcome);
    Assert.Equal(ExecutionOutcome.Skipped, run.ResourceResults["git"].Outcome);
    Assert.Equal(error, Assert.Single(run.Plan!.Errors));
    Assert.Equal(0, provider.ApplyCalls);
  }

  [Fact]
  public async Task InspectAsync_RejectsCallerSuppliedRecoveryProvenance()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);

    var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
        service.InspectAsync(
            Request() with { RetriedFromRunId = Guid.NewGuid() },
            CancellationToken.None));

    Assert.Equal("request", exception.ParamName);
    Assert.Empty(await store.ListAsync(CancellationToken.None));
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
  public async Task ApplyAsync_RejectsCallerSuppliedRecoveryProvenance()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var prior = InterruptedRun();
    await store.CreateAsync(prior, CancellationToken.None);

    var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
        service.ApplyAsync(
            Request() with { RetriedFromRunId = prior.RunId },
            CancellationToken.None));

    Assert.Equal("request", exception.ParamName);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(
        [prior.RunId],
        (await store.ListAsync(CancellationToken.None)).Select(run => run.RunId));
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
    Assert.Equal(
        Enumerable.Range(0, store.SavedSnapshots.Count).Select(revision => (long)revision),
        store.SavedSnapshots.Select(snapshot => snapshot.Revision));
    Assert.Equal(store.SavedSnapshots[^1].Revision, run.Revision);
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
  public async Task ApplyAsync_CancellationWaitsForProviderEvidenceAndPersistsIt()
  {
    using var cancellation = new CancellationTokenSource();
    var applyCancelled = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseApply = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = async _ =>
      {
        cancellation.Cancel();
        applyCancelled.SetResult();
        await releaseApply.Task;
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRecommended,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.5,
              ProcessExitCode = 3010,
              Message = "The update completed before cancellation."
            }
          ]
        };
      }
    };
    var (service, store) = CreateService(provider);

    var apply = service.ApplyAsync(Request(), cancellation.Token);
    await applyCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    releaseApply.SetResult();
    var run = await apply;
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRecommended, resource.RestartRequirement);
    Assert.Equal(3010, step.ProcessExitCode);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(RestartPolicy.RestartRecommended, persisted.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_CancellationPreservesProviderEvidenceBeyondSchedulerDrainDefault()
  {
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = async _ =>
      {
        cancellation.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRecommended,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.75,
              ProcessExitCode = 3010,
              Message = "The update completed before cancellation."
            }
          ]
        };
      }
    };
    var (service, store) = CreateService(provider);

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);

    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRecommended, resource.RestartRequirement);
    Assert.Equal(3010, step.ProcessExitCode);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(RestartPolicy.RestartRecommended, persisted.RestartRequirements);
  }

  [Fact]
  public async Task ApplyAsync_CancellationUsesOneDeadlineAndPreservesLateProviderEvidence()
  {
    var drainBudget = TimeSpan.FromMilliseconds(600);
    using var cancellation = new CancellationTokenSource();
    var store = new InMemoryRunStore
    {
      AppendLogOperation = async (_, entry, token) =>
      {
        if (entry.Message == "blocked progress")
        {
          await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
      }
    };
    var provider = new ScriptedProvider(Missing("git"))
    {
      ProgressEvents =
      [
        new ProviderProgress("install", 0.25, "blocked progress")
      ],
      ApplyOperation = async _ =>
      {
        cancellation.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(450));
        return new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRequired,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.8,
              ProcessExitCode = 1641,
              Error = new StructuredError(
                  WdemErrorCode.ProviderError,
                  "Installer cancellation recorded.",
                  "The installer initiated a restart before cancellation.")
            }
          ]
        };
      }
    };
    var (service, persistedStore) = CreateService(
        provider,
        store: store,
        scheduler: new ResourceScheduler(drainBudget),
        persistenceTimeout: TimeSpan.FromSeconds(5));
    var stopwatch = Stopwatch.StartNew();

    var run = await service.ApplyAsync(Request(), cancellation.Token);
    var persisted = await persistedStore.GetAsync(run.RunId, CancellationToken.None);

    stopwatch.Stop();
    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, resource.RestartRequirement);
    Assert.Equal(1641, step.ProcessExitCode);
    Assert.Equal(
        "The installer initiated a restart before cancellation.",
        step.Error!.Detail);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.Contains(RestartPolicy.RestartRequired, persisted.RestartRequirements);
    Assert.True(
        stopwatch.Elapsed < TimeSpan.FromMilliseconds(900),
        $"Cancellation took {stopwatch.Elapsed} for a {drainBudget} drain budget.");
  }

  [Fact]
  public async Task ApplyAsync_CancellationBoundsRunCleanupAndPreservesProviderEvidence()
  {
    var drainBudget = TimeSpan.FromMilliseconds(600);
    using var cancellation = new CancellationTokenSource();
    var provider = new ScriptedProvider(Missing("git"))
    {
      ApplyOperation = _ =>
      {
        cancellation.Cancel();
        return ValueTask.FromResult(new ResourceApplyResult
        {
          ResourceId = "git",
          Outcome = ApplyOutcome.Cancelled,
          RestartRequirement = RestartPolicy.RestartRequired,
          StepResults =
          [
            new ProviderStepResult
            {
              StepId = "install",
              Action = PlanAction.Install,
              Progress = 0.9,
              ProcessExitCode = 1641,
              Error = new StructuredError(
                  WdemErrorCode.ProviderError,
                  "Installer cancellation recorded.",
                  "The installer initiated a restart before cleanup began.")
            }
          ]
        });
      }
    };
    var dispatcher = new BlockingCleanupDispatcher();
    var (service, store) = CreateService(
        provider,
        dispatcher: dispatcher,
        scheduler: new ResourceScheduler(drainBudget));
    var stopwatch = Stopwatch.StartNew();
    var apply = service.ApplyAsync(Request(), cancellation.Token);

    await dispatcher.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    ExecutionRun run;
    try
    {
      run = await apply.WaitAsync(TimeSpan.FromMilliseconds(1200));
    }
    finally
    {
      dispatcher.ReleaseCleanup.TrySetResult();
    }

    stopwatch.Stop();
    var resource = run.ResourceResults["git"];
    var step = Assert.Single(resource.StepResults);
    var persisted = await store.GetAsync(run.RunId, CancellationToken.None);
    Assert.Equal(ExecutionOutcome.Cancelled, resource.Outcome);
    Assert.Equal(RestartPolicy.RestartRequired, resource.RestartRequirement);
    Assert.Equal(1641, step.ProcessExitCode);
    Assert.Equal(
        "The installer initiated a restart before cleanup began.",
        step.Error!.Detail);
    Assert.Equal(resource, persisted!.ResourceResults["git"]);
    Assert.NotNull(dispatcher.ApplyDeadline);
    Assert.True(dispatcher.ApplyDeadline.IsStarted);
    Assert.True(dispatcher.CleanupToken.CanBeCanceled);
    Assert.True(
        stopwatch.Elapsed < TimeSpan.FromMilliseconds(1000),
        $"Cancellation took {stopwatch.Elapsed} for a {drainBudget} drain budget.");
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
  public async Task RecoverAsync_IgnoresSuccessfulInspectLinkedToPriorRun()
  {
    var provider = new ScriptedProvider(Missing("git"));
    var (service, store) = CreateService(provider);
    var interrupted = InterruptedRun();
    await store.CreateAsync(interrupted, CancellationToken.None);
    var inspect = await service.InspectAsync(Request(), CancellationToken.None);
    inspect = await store.SaveAsync(
        inspect with { RetriedFromRunId = interrupted.RunId },
        CancellationToken.None);

    var recovered = await service.RecoverAsync(
        interrupted.RunId,
        CancellationToken.None);

    var persistedPrior = await store.GetAsync(interrupted.RunId, CancellationToken.None);
    Assert.Equal(RunMode.Inspect, inspect.Mode);
    Assert.Equal(ExecutionOutcome.Succeeded, inspect.Outcome);
    Assert.Equal(RunMode.Apply, recovered.Mode);
    Assert.NotEqual(inspect.RunId, recovered.RunId);
    Assert.Equal(1, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persistedPrior!.State);
  }

  [Fact]
  public async Task RecoverAsync_SuccessIsIdempotentAcrossServices()
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
    var recoveredAgain = await secondService.RecoverAsync(
        interrupted.RunId,
        CancellationToken.None);
    Assert.Equal(ExecutionOutcome.Succeeded, recovered.Outcome);
    Assert.Equal(recovered.RunId, recoveredAgain.RunId);
    Assert.Equal(0, provider.ApplyCalls);
    Assert.Equal(ExecutionState.Completed, persistedPrior!.State);
    Assert.Equal(ExecutionOutcome.Cancelled, persistedPrior.Outcome);
    Assert.NotNull(persistedPrior.EndedAtUtc);
    Assert.False(persistedPrior.ResourceResults["git"].DetectedBefore!.Exists);
    Assert.DoesNotContain(candidates, candidate => candidate.RunId == interrupted.RunId);
  }

  [Fact]
  public async Task RecoverAsync_DoesNotReuseEarlierPartialOrdinaryRetry()
  {
    var provider = new ScriptedProvider(Satisfied("git", "2.52.1"))
    {
      DetectState = resource => Satisfied(resource.Id, "2.52.1")
    };
    var (service, store) = CreateService(provider, Profile(includeBrokenResource: true));
    var prior = FailedRun("git", "broken") with
    {
      State = ExecutionState.Running,
      Outcome = null,
      EndedAtUtc = null
    };
    await store.CreateAsync(prior, CancellationToken.None);
    var ordinaryRetry = await service.RetryAsync(
        prior.RunId,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git" },
        CancellationToken.None);

    var recovery = await service.RecoverAsync(prior.RunId, CancellationToken.None);
    var resumedAgain = await service.RecoverAsync(prior.RunId, CancellationToken.None);

    Assert.Equal(["broken", "git"], recovery.ResourceResults.Keys.Order());
    Assert.NotEqual(ordinaryRetry.RunId, recovery.RunId);
    Assert.Null(ordinaryRetry.RecoveredFromRunId);
    Assert.Equal(prior.RunId, recovery.RetriedFromRunId);
    Assert.Equal(prior.RunId, recovery.RecoveredFromRunId);
    Assert.Equal(recovery.RunId, resumedAgain.RunId);
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
  public async Task FindRecoveryCandidatesAsync_HidesRunWhileProviderIsApplying()
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

      var (recoveryService, _) = CreateService(
          new ScriptedProvider(Missing("git")),
          store: firstStore);
      var persisted = Assert.Single(
          await firstStore.ListAsync(CancellationToken.None));
      var candidates = await recoveryService.FindRecoveryCandidatesAsync(
          CancellationToken.None);

      Assert.Equal(ExecutionState.Running, persisted.State);
      Assert.Equal(ExecutionState.Running, persisted.ResourceResults["git"].State);
      Assert.Empty(candidates);
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
      InMemoryRunStore? store = null,
      IRunEventSink? eventSink = null,
      IExecutionPlanner? planner = null,
      IResourceApplyDispatcher? dispatcher = null,
      IResourceScheduler? scheduler = null,
      TimeSpan? persistenceTimeout = null,
      IComplianceEvaluator? complianceEvaluator = null)
  {
    catalog ??= new FakeProfileCatalog(profile ?? Profile(), CanonicalProfilePath);
    var registry = new ResourceProviderRegistry([provider]);
    var compliance = complianceEvaluator ?? new ComplianceEvaluator();
    store ??= new InMemoryRunStore();
    eventSink ??= new RunEventHub();
    return (
        new EnvironmentRunService(
            catalog,
            new ResourceGraphBuilder(),
            registry,
            compliance,
            planner ?? new ExecutionPlanner(registry, compliance),
            scheduler ?? new ResourceScheduler(),
            store,
            dispatcher ?? new DirectResourceApplyDispatcher(),
            timeProvider: null,
            eventSink,
            new LogRedactor(),
            persistenceTimeout),
        store);
  }

  private sealed class FinalErrorComplianceEvaluator(StructuredError finalError)
      : IComplianceEvaluator
  {
    private readonly ComplianceEvaluator _inner = new();
    private int _calls;

    public ComplianceResult Evaluate(ResourceDefinition desired, DetectedState current)
    {
      if (Interlocked.Increment(ref _calls) <= 2)
      {
        return _inner.Evaluate(desired, current);
      }

      return new ComplianceResult(
          ComplianceStatus.ConfigurationMismatch,
          finalError.Summary,
          finalError);
    }
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
    public Func<CancellationToken, ValueTask<VerificationResult>>? VerificationOperation { get; init; }
    public IReadOnlyList<ProviderProgress> ProgressEvents { get; init; } = [];
    public RestartPolicy PlannedRestartPolicy { get; init; }
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
                RestartPolicy = PlannedRestartPolicy
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
      foreach (var progressEvent in ProgressEvents)
      {
        progress?.Report(progressEvent);
      }

      return ApplyOperation?.Invoke(cancellationToken) ?? ValueTask.FromResult(ApplyResult);
    }

    public ValueTask<VerificationResult> VerifyAsync(
        ResourceDefinition resource,
        CancellationToken cancellationToken)
    {
      VerifyCalls++;
      return VerificationOperation?.Invoke(cancellationToken) ??
          ValueTask.FromResult(VerificationResult);
    }
  }

  private sealed class TransformingPlanner(
      IExecutionPlanner inner,
      Func<ExecutionPlan, ExecutionPlan> transform) : IExecutionPlanner
  {
    public async Task<ExecutionPlan> CreateAsync(
        ResourceGraph graph,
        IReadOnlyDictionary<string, DetectedState> detectedStates,
        string profileId,
        string profileVersion,
        CancellationToken cancellationToken) => transform(await inner.CreateAsync(
            graph,
            detectedStates,
            profileId,
            profileVersion,
            cancellationToken));
  }

  private sealed class InMemoryRunStore : IExecutionRunStore
  {
    private readonly Dictionary<Guid, bool> _recoveryOperations = [];
    private readonly Dictionary<Guid, ExecutionRun> _runs;

    public InMemoryRunStore(Dictionary<Guid, ExecutionRun>? runs = null)
    {
      _runs = runs ?? [];
    }

    public IReadOnlyList<StructuredError> Diagnostics => [];
    public List<ExecutionRun> SavedSnapshots { get; } = [];
    public Func<ExecutionRun, CancellationToken, Task>? SaveOperation { get; init; }
    public Func<Guid, RunLogEntry, CancellationToken, Task>? AppendLogOperation { get; set; }

    public Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      _runs.Add(run.RunId, run);
      SavedSnapshots.Add(run);
      return Task.CompletedTask;
    }

    public Task CreateAsync(
        ExecutionRun run,
        IReadOnlyList<ApprovedResourceSeal> approvedResources,
        CancellationToken cancellationToken) => CreateAsync(run, cancellationToken);

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

    public Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      lock (_recoveryOperations)
      {
        if (_recoveryOperations.ContainsKey(runId))
        {
          return Task.FromResult<IAsyncDisposable?>(null);
        }

        _recoveryOperations.Add(runId, true);
        return Task.FromResult<IAsyncDisposable?>(
            new AsyncAction(() =>
            {
              lock (_recoveryOperations)
              {
                _recoveryOperations.Remove(runId);
              }
            }));
      }
    }

    public async Task<ExecutionRun> SaveAsync(
        ExecutionRun run,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (SaveOperation is not null)
      {
        await SaveOperation(run, cancellationToken);
      }

      lock (_runs)
      {
        if (!_runs.TryGetValue(run.RunId, out var current) ||
            current.Revision != run.Revision ||
            current.RecoveryClaimId != run.RecoveryClaimId)
        {
          throw new InvalidOperationException("The execution run snapshot is stale.");
        }

        var saved = run with { Revision = checked(run.Revision + 1) };
        _runs[run.RunId] = saved;
        SavedSnapshots.Add(saved);
        return saved;
      }
    }

    public Task<bool> TrySaveAsync(
        ExecutionRun run,
        long expectedRevision,
        Guid? expectedRecoveryClaimId,
        CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      lock (_runs)
      {
        if (!_runs.TryGetValue(run.RunId, out var current))
        {
          throw new KeyNotFoundException(
              $"Execution run '{run.RunId:D}' does not exist.");
        }

        if (current.Revision != expectedRevision ||
            current.RecoveryClaimId != expectedRecoveryClaimId)
        {
          return Task.FromResult(false);
        }

        _runs[run.RunId] = run;
        SavedSnapshots.Add(run);
        return Task.FromResult(true);
      }
    }

    public async Task AppendLogAsync(
        Guid runId,
        RunLogEntry entry,
        CancellationToken cancellationToken)
    {
      if (AppendLogOperation is not null)
      {
        await AppendLogOperation(runId, entry, cancellationToken);
      }
    }

    public Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
        Guid runId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunLogEntry>>([]);

    private sealed class AsyncAction(Action action) : IAsyncDisposable
    {
      public ValueTask DisposeAsync()
      {
        action();
        return ValueTask.CompletedTask;
      }
    }
  }

  private sealed class CleanupFailingDispatcher : IResourceApplyDispatcher
  {
    private readonly DirectResourceApplyDispatcher _direct = new();

    public Task<ResourceApplyResult> ApplyAsync(
        Guid runId,
        IResourceProvider provider,
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => _direct.ApplyAsync(
            runId,
            provider,
            resource,
            plan,
            progress,
            cancellationToken);

    public Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromException(new IOException("elevated cleanup unavailable"));
  }

  private sealed class BlockingCleanupDispatcher : IResourceApplyDispatcher
  {
    private readonly DirectResourceApplyDispatcher _direct = new();

    public TaskCompletionSource CleanupStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseCleanup { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationDrainDeadline? ApplyDeadline { get; private set; }
    public CancellationToken CleanupToken { get; private set; }

    public Task<ResourceApplyResult> ApplyAsync(
        Guid runId,
        IResourceProvider provider,
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken) => _direct.ApplyAsync(
            runId,
            provider,
            resource,
            plan,
            progress,
            cancellationToken);

    public Task<ResourceApplyResult> ApplyAsync(
        Guid runId,
        IResourceProvider provider,
        ResourceDefinition resource,
        ResourcePlan plan,
        IProgress<ProviderProgress>? progress,
        CancellationToken cancellationToken,
        CancellationDrainDeadline? cancellationDeadline)
    {
      ApplyDeadline = cancellationDeadline;
      return ApplyAsync(
          runId,
          provider,
          resource,
          plan,
          progress,
          cancellationToken);
    }

    public async Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken)
    {
      CleanupToken = cancellationToken;
      CleanupStarted.TrySetResult();
      await ReleaseCleanup.Task;
    }
  }
}
