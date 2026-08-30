using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Wdem.Core.Compliance;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public sealed class EnvironmentRunService :
    IEnvironmentRunService,
    IEnvironmentRunFinalizationService,
    ICommandLineEnvironmentRunService,
    IReviewedPlanEnvironmentRunService
{
  private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
  private static readonly TimeSpan DefaultPersistenceTimeout = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan CancellationProgressDrainGrace =
      TimeSpan.FromMilliseconds(200);
  private const int MaximumRetainedFinalizations = 64;
  private readonly ConcurrentDictionary<Guid, Task> _runFinalizations = new();
  private readonly object _runFinalizationsGate = new();
  private readonly LinkedList<(Guid RunId, Task Finalization)> _runFinalizationOrder = [];
  private readonly IProfileCatalog _profiles;
  private readonly ResourceGraphBuilder _graphBuilder;
  private readonly IResourceProviderRegistry _providers;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly IExecutionPlanner _planner;
  private readonly IResourceScheduler _scheduler;
  private readonly IExecutionRunStore _runStore;
  private readonly IResourceApplyDispatcher _dispatcher;
  private readonly TimeProvider _timeProvider;
  private readonly IRunEventSink _eventSink;
  private readonly LogRedactor _redactor;
  private readonly TimeSpan _persistenceTimeout;
  private readonly TimeSpan _cancellationDrainTimeout;

  public EnvironmentRunService(
      IProfileCatalog profiles,
      ResourceGraphBuilder graphBuilder,
      IResourceProviderRegistry providers,
      IComplianceEvaluator complianceEvaluator,
      IExecutionPlanner planner,
      IResourceScheduler scheduler,
      IExecutionRunStore runStore,
      IResourceApplyDispatcher dispatcher,
      TimeProvider? timeProvider,
      IRunEventSink eventSink,
      LogRedactor redactor,
      TimeSpan? persistenceTimeout = null)
  {
    _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    _graphBuilder = graphBuilder ?? throw new ArgumentNullException(nameof(graphBuilder));
    _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    _cancellationDrainTimeout = scheduler.CancellationDrainTimeout;
    _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    _timeProvider = timeProvider ?? TimeProvider.System;
    _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    _persistenceTimeout = persistenceTimeout ?? DefaultPersistenceTimeout;
    if (_persistenceTimeout <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(persistenceTimeout));
    }
  }

  public Task<ExecutionRun> InspectAsync(
      RunRequest request,
      CancellationToken cancellationToken)
  {
    return ExecuteFreshAsync(
        request,
        RunMode.Inspect,
        resourceFilter: null,
        retriedFromRunId: null,
        recoveredFromRunId: null,
        approvalSource: null,
        reviewedPlanFingerprint: null,
        cancellationToken);
  }

  public Task<ExecutionRun> ApplyAsync(
      RunRequest request,
      CancellationToken cancellationToken)
  {
    return ExecuteFreshAsync(
        request,
        RunMode.Apply,
        resourceFilter: null,
        retriedFromRunId: null,
        recoveredFromRunId: null,
        approvalSource: PlanApprovalSource.ExplicitApplyRequest,
        reviewedPlanFingerprint: null,
        cancellationToken);
  }

  Task<ExecutionRun> ICommandLineEnvironmentRunService.ApplyAsync(
      RunRequest request,
      CancellationToken cancellationToken) => ExecuteFreshAsync(
        request,
        RunMode.Apply,
        resourceFilter: null,
        retriedFromRunId: null,
        recoveredFromRunId: null,
        approvalSource: PlanApprovalSource.CommandLine,
        reviewedPlanFingerprint: null,
        cancellationToken);

  Task<ExecutionRun> IReviewedPlanEnvironmentRunService.ApplyAsync(
      RunRequest request,
      string reviewedPlanFingerprint,
      CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(reviewedPlanFingerprint);
    return ExecuteFreshAsync(
        request,
        RunMode.Apply,
        resourceFilter: null,
        retriedFromRunId: null,
        recoveredFromRunId: null,
        approvalSource: PlanApprovalSource.DesktopReviewedPlan,
        reviewedPlanFingerprint: reviewedPlanFingerprint,
        cancellationToken);
  }

  public async Task<ExecutionRun> WaitForRunFinalizationAsync(
      Guid runId,
      CancellationToken cancellationToken)
  {
    if (runId == Guid.Empty)
    {
      throw new ArgumentException("An execution run identifier is required.", nameof(runId));
    }

    cancellationToken.ThrowIfCancellationRequested();
    if (_runFinalizations.TryGetValue(runId, out var finalization))
    {
      try
      {
        await finalization.WaitAsync(cancellationToken).ConfigureAwait(false);
        RemoveRunFinalization(runId, finalization);
      }
      catch (OperationCanceledException) when (
          cancellationToken.IsCancellationRequested && !finalization.IsCanceled)
      {
        throw;
      }
      catch (Exception)
      {
        RemoveRunFinalization(runId, finalization);
        throw;
      }
    }

    return await GetRequiredRunAsync(runId, cancellationToken).ConfigureAwait(false);
  }

  public async Task<ExecutionRun> RetryAsync(
      Guid priorRunId,
      IReadOnlySet<string> resourceIds,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resourceIds);
    var prior = await GetRequiredRunAsync(priorRunId, cancellationToken).ConfigureAwait(false);
    if (prior.Mode != RunMode.Apply)
    {
      throw new InvalidOperationException(
          "Only a previously approved apply run can be retried.");
    }

    if (!TryCreateApprovalBoundary(prior, out var approvalBoundary))
    {
      throw new InvalidOperationException(
          "The prior apply run does not contain a valid immutable plan approval.");
    }

    var retryable = prior.ResourceResults.Values
        .Where(result => result.Outcome == ExecutionOutcome.Failed ||
            result.State == ExecutionState.Blocked)
        .Select(result => result.ResourceId)
        .ToHashSet(IdComparer);
    var requested = resourceIds.ToHashSet(IdComparer);
    if (requested.Count == 0 || !requested.IsSubsetOf(retryable))
    {
      throw new ArgumentException(
          "Only failed or blocked resources from the prior run can be retried.",
          nameof(resourceIds));
    }

    var request = new RunRequest(
        prior.ProfileSourcePath,
        prior.SelectedOptionalResourceIds);
    return await ExecuteFreshAsync(
            request,
            RunMode.Apply,
            requested,
            prior.RunId,
            recoveredFromRunId: null,
            approvalSource: PlanApprovalSource.Retry,
            reviewedPlanFingerprint: null,
            cancellationToken,
            approvalBoundary)
        .ConfigureAwait(false);
  }

  public async Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
      CancellationToken cancellationToken)
  {
    var runs = await _runStore.ListAsync(cancellationToken).ConfigureAwait(false);
    var candidates = new List<RecoveryCandidate>();
    foreach (var run in runs.Where(IsRecoverableState))
    {
      var pending = PendingResourceIds(run);
      if (pending.Count == 0)
      {
        continue;
      }

      await using var operation = await _runStore.TryAcquireRecoveryOperationAsync(
          run.RunId,
          cancellationToken).ConfigureAwait(false);
      if (operation is null)
      {
        continue;
      }

      var current = await _runStore.GetAsync(run.RunId, cancellationToken)
          .ConfigureAwait(false);
      if (current is null || !IsRecoverableState(current))
      {
        continue;
      }

      pending = PendingResourceIds(current);
      if (pending.Count == 0)
      {
        continue;
      }

      candidates.Add(new RecoveryCandidate
      {
        RunId = current.RunId,
        ProfileSourcePath = current.ProfileSourcePath,
        StartedAtUtc = current.StartedAtUtc,
        PendingResourceIds = pending
      });
    }

    return candidates
        .OrderBy(candidate => candidate.StartedAtUtc)
        .ToArray();
  }

  public async Task<ExecutionRun> RecoverAsync(
    Guid priorRunId,
    CancellationToken cancellationToken)
  {
    await using var operation = await _runStore.TryAcquireRecoveryOperationAsync(
        priorRunId,
        cancellationToken).ConfigureAwait(false);
    if (operation is null)
    {
      throw new InvalidOperationException(
          $"Execution run '{priorRunId:D}' already has an active recovery operation.");
    }

    var prior = await GetRequiredRunAsync(priorRunId, cancellationToken).ConfigureAwait(false);
    var now = _timeProvider.GetUtcNow();
    if (!IsRecoverableState(prior))
    {
      var associatedReplacement = await FindSuccessfulRecoveryReplacementAsync(
          prior.RunId,
          cancellationToken).ConfigureAwait(false);
      if (associatedReplacement is not null)
      {
        return associatedReplacement;
      }

      throw new InvalidOperationException(
          $"Execution run '{priorRunId:D}' is not eligible for recovery.");
    }

    if (prior.Mode != RunMode.Apply ||
        !TryCreateApprovalBoundary(prior, out var approvalBoundary))
    {
      throw new InvalidOperationException(
          "The prior apply run does not contain a valid immutable plan approval.");
    }

    var remaining = PendingResourceIds(prior);
    if (remaining.Count == 0)
    {
      throw new InvalidOperationException(
          $"Execution run '{priorRunId:D}' has no remaining resources to recover.");
    }

    var claimed = prior with
    {
      Revision = checked(prior.Revision + 1),
      RecoveryClaimId = Guid.NewGuid(),
      RecoveryClaimedAtUtc = now
    };
    if (!await _runStore.TrySaveAsync(
            claimed,
            prior.Revision,
            prior.RecoveryClaimId,
            cancellationToken).ConfigureAwait(false))
    {
      throw new InvalidOperationException(
          $"Execution run '{priorRunId:D}' is no longer available for recovery.");
    }

    var request = new RunRequest(
        claimed.ProfileSourcePath,
        claimed.SelectedOptionalResourceIds);
    ExecutionRun recovered;
    try
    {
      var associatedReplacement = await FindSuccessfulRecoveryReplacementAsync(
          prior.RunId,
          cancellationToken).ConfigureAwait(false);
      recovered = associatedReplacement is not null &&
          remaining.All(associatedReplacement.ResourceResults.ContainsKey)
          ? associatedReplacement
          :
          await ExecuteFreshAsync(
              request,
              RunMode.Apply,
              remaining,
              claimed.RunId,
              recoveredFromRunId: claimed.RunId,
              approvalSource: PlanApprovalSource.Retry,
              reviewedPlanFingerprint: null,
              cancellationToken,
              approvalBoundary).ConfigureAwait(false);
    }
    catch (Exception executionException)
    {
      try
      {
        await ReleaseRecoveryClaimAsync(claimed).ConfigureAwait(false);
      }
      catch (Exception releaseException)
      {
        executionException.Data["RecoveryClaimReleaseException"] = releaseException;
      }

      throw;
    }

    if (recovered.State == ExecutionState.Completed &&
        recovered.Outcome == ExecutionOutcome.Succeeded)
    {
      await CompleteRecoveryClaimAsync(claimed, recovered.RunId, remaining)
          .ConfigureAwait(false);
    }
    else
    {
      await ReleaseRecoveryClaimAsync(claimed).ConfigureAwait(false);
    }

    return recovered;
  }

  public async Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken)
  {
    await using var operation = await _runStore.TryAcquireRecoveryOperationAsync(
        priorRunId,
        cancellationToken).ConfigureAwait(false);
    if (operation is null)
    {
      throw new InvalidOperationException(
          $"Execution run '{priorRunId:D}' already has an active recovery operation.");
    }

    var prior = await GetRequiredRunAsync(priorRunId, cancellationToken).ConfigureAwait(false);
    if (prior.State == ExecutionState.Completed)
    {
      var pending = PendingResourceIds(prior);
      var acknowledged = prior.AcknowledgedRestartResourceIds
          .Concat(prior.ResourceResults.Values
              .Where(result => pending.Contains(result.ResourceId) &&
                  result.RestartRequirement != RestartPolicy.NoRestart)
              .Select(result => result.ResourceId))
          .ToHashSet(IdComparer);
      if (acknowledged.SetEquals(prior.AcknowledgedRestartResourceIds) &&
          prior.RecoveryClaimId is null)
      {
        return;
      }

      var updated = prior with
      {
        Revision = checked(prior.Revision + 1),
        AcknowledgedRestartResourceIds = acknowledged,
        RecoveryClaimId = null,
        RecoveryClaimedAtUtc = null
      };
      if (!await TryPersistClaimTransitionAsync(
              updated,
              prior.Revision,
              prior.RecoveryClaimId)
          .ConfigureAwait(false))
      {
        throw new InvalidOperationException(
            $"Execution run '{priorRunId:D}' is no longer available to abandon.");
      }

      return;
    }

    var endedAt = DateTimeOffset.UtcNow;
    var results = prior.ResourceResults.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.State == ExecutionState.Completed
            ? pair.Value
            : pair.Value with
            {
              State = ExecutionState.Completed,
              Outcome = ExecutionOutcome.Cancelled,
              EndedAtUtc = endedAt,
              Error = new StructuredError(
                  WdemErrorCode.CancellationError,
                  "Recovery was abandoned.",
                  $"Resource '{pair.Key}' was cancelled when run recovery was abandoned.")
              {
                ResourceId = pair.Key,
                IsRetryable = false
              }
            },
        IdComparer);
    var acknowledgedRestartResourceIds = prior.AcknowledgedRestartResourceIds
        .Concat(prior.ResourceResults.Values
            .Where(result => result.RestartRequirement != RestartPolicy.NoRestart)
            .Select(result => result.ResourceId))
        .ToHashSet(IdComparer);
    var abandoned = prior with
    {
      Revision = checked(prior.Revision + 1),
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Cancelled,
      EndedAtUtc = endedAt,
      ResourceResults = results,
      AcknowledgedRestartResourceIds = acknowledgedRestartResourceIds,
      RecoveryClaimId = null,
      RecoveryClaimedAtUtc = null
    };
    if (!await TryPersistClaimTransitionAsync(
            abandoned,
            prior.Revision,
            prior.RecoveryClaimId)
        .ConfigureAwait(false))
    {
      throw new InvalidOperationException(
          $"Execution run '{priorRunId:D}' is no longer available to abandon.");
    }
  }

  private async Task<ExecutionRun> ExecuteFreshAsync(
      RunRequest request,
      RunMode mode,
      IReadOnlySet<string>? resourceFilter,
      Guid? retriedFromRunId,
      Guid? recoveredFromRunId,
      PlanApprovalSource? approvalSource,
      string? reviewedPlanFingerprint,
      CancellationToken cancellationToken,
      PlanApprovalBoundary? approvalBoundary = null)
  {
    ValidateRequest(request);
    cancellationToken.ThrowIfCancellationRequested();

    var loaded = await _profiles.LoadFileAsync(request.ProfilePath, cancellationToken)
        .ConfigureAwait(false);
    if (loaded.Profile is { } materializedProfile)
    {
      _redactor.RegisterSensitiveParameters(materializedProfile.Resources.Values.SelectMany(
          resource => resource.Parameters));
    }

    if (!loaded.IsValid || loaded.Profile is null)
    {
      var diagnostics = loaded.Errors.Count > 0
          ? loaded.Errors
          :
          [
            new StructuredError(
                WdemErrorCode.ProfileError,
                "Profile validation failed.",
                "The profile catalog did not return a valid profile.")
          ];
      return await PersistPreparationFailureAsync(
          request,
          mode,
          loaded,
          diagnostics,
          retriedFromRunId,
          recoveredFromRunId,
          cancellationToken)
          .ConfigureAwait(false);
    }

    var sourcePath = CanonicalizeOrPreservePath(loaded.SourcePath);
    var profile = loaded.Profile;
    var graphResult = _graphBuilder.TryBuild(
        profile,
        new ProfileSelection(request.SelectedOptionalResourceIds));
    if (graphResult.Errors.Count > 0 || graphResult.Graph is null)
    {
      return await PersistPreparationFailureAsync(
          request,
          mode,
          loaded with { SourcePath = sourcePath },
          graphResult.Errors,
          retriedFromRunId,
          recoveredFromRunId,
          cancellationToken).ConfigureAwait(false);
    }

    var approvedSelectionChanged = approvalBoundary is not null &&
        resourceFilter is not null &&
        resourceFilter.Any(id => !graphResult.Graph.Nodes.ContainsKey(id));
    var currentResourceFilter = approvedSelectionChanged
        ? resourceFilter!.Where(graphResult.Graph.Nodes.ContainsKey).ToHashSet(IdComparer)
        : resourceFilter;
    var graph = currentResourceFilter is null
        ? graphResult.Graph
        : FilterGraph(graphResult.Graph, currentResourceFilter);
    var detected = await DetectAsync(graph, cancellationToken).ConfigureAwait(false);
    var compliance = graph.Nodes.ToDictionary(
        pair => pair.Key,
        pair => EvaluateCompliance(pair.Value.Definition, detected[pair.Key]),
        IdComparer);
    var plan = await _planner.CreateAsync(
        graph,
        detected,
        profile.Id,
        profile.Version,
        cancellationToken).ConfigureAwait(false);
    var reviewedPlanRejected = mode == RunMode.Apply &&
        reviewedPlanFingerprint is { } approvedFingerprint &&
        !string.Equals(approvedFingerprint, plan.Fingerprint, StringComparison.Ordinal);
    var freshPlanExceedsApproval = mode == RunMode.Apply &&
        approvalBoundary is not null &&
        (approvedSelectionChanged || !IsPlanWithinApproval(plan, approvalBoundary));
    var planApprovalRejected = reviewedPlanRejected || freshPlanExceedsApproval;
    if (planApprovalRejected)
    {
      var approvalError = new StructuredError(
          WdemErrorCode.ConfigurationError,
          freshPlanExceedsApproval
              ? "The execution plan exceeds its prior approval."
              : "The reviewed execution plan has changed.",
          freshPlanExceedsApproval
              ? "The fresh plan is not a refinement of the prior approved plan, so no resources " +
                "were executed. Review the refreshed plan before applying it."
              : "The environment or configuration changed after plan review, so no resources " +
                "were executed.")
      {
        SuggestedAction = "Review the refreshed plan before applying it.",
        IsRetryable = true
      };
      plan = ExecutionPlanner.CreatePlan(
          profile.Id,
          profile.Version,
          plan.Layers,
          plan.Resources,
          plan.Errors.Append(approvalError).ToArray());
    }
    var approval = mode == RunMode.Apply && !planApprovalRejected
        ? CreatePlanApproval(
            plan,
            approvalSource ?? throw new InvalidOperationException(
                "An apply run requires a controlled approval source."))
        : null;

    var initialResults = CreateInitialResults(mode, plan, detected, compliance);
    var run = new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = mode,
      ProfileSourcePath = sourcePath,
      ProfileId = profile.Id,
      ProfileVersion = profile.Version,
      SelectedOptionalResourceIds = request.SelectedOptionalResourceIds,
      StartedAtUtc = DateTimeOffset.UtcNow,
      State = ExecutionState.Ready,
      RetriedFromRunId = retriedFromRunId,
      RecoveredFromRunId = recoveredFromRunId,
      Machine = CurrentMachine(),
      Graph = graph,
      Plan = plan,
      PlanApproval = approval,
      ResourceResults = initialResults
    };
    _eventSink.BindCurrentScopeToRun(run.RunId);
    await using var operation = mode == RunMode.Apply
        ? await _runStore.TryAcquireRecoveryOperationAsync(run.RunId, cancellationToken)
            .ConfigureAwait(false)
        : null;
    if (mode == RunMode.Apply && operation is null)
    {
      throw new InvalidOperationException(
          $"Execution run '{run.RunId:D}' already has an active operation.");
    }

    var approvedResources = mode == RunMode.Apply
        ? CreateApprovedResourceSeals(graph, plan)
        : [];
    await _runStore.CreateAsync(run, approvedResources, cancellationToken)
        .ConfigureAwait(false);
    var events = new RunEventPublisher(
        _runStore,
        _eventSink,
        _redactor,
        _timeProvider,
        run.RunId);
    await events.PublishRunStateAsync(run, cancellationToken).ConfigureAwait(false);
    await events.PublishPlanDiagnosticsAsync(run.Plan, cancellationToken).ConfigureAwait(false);
    foreach (var result in run.ResourceResults.Values.OrderBy(
        result => result.ResourceId,
        IdComparer))
    {
      await events.PublishResourceAsync(result, cancellationToken).ConfigureAwait(false);
    }

    if (mode == RunMode.Inspect)
    {
      var outcome = plan.Errors.Count > 0 || !plan.IsExecutable
          ? ExecutionOutcome.Failed
          : ExecutionOutcome.Succeeded;
      var completed = run with
      {
        State = ExecutionState.Completed,
        Outcome = outcome,
        EndedAtUtc = DateTimeOffset.UtcNow
      };
      return await PersistTerminalAsync(completed, events).ConfigureAwait(false);
    }

    if (!plan.IsExecutable)
    {
      return await CompleteUnexecutableAsync(run, events, cancellationToken).ConfigureAwait(false);
    }

    var maximumPotentialFinalization = plan.Resources
        .Select(planned => _providers.GetRequired(
            planned.Definition.Type,
            planned.Definition.Provider).Capabilities.CancellationFinalizationTimeout)
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();
    using var cancellationDeadline = new CancellationDrainDeadline(
        _cancellationDrainTimeout,
        cancellationToken,
        maximumPotentialFinalization);
    var transitions = new RunTransitions(_runStore, events, run, _persistenceTimeout);
    await transitions.SetRunningAsync(cancellationToken).ConfigureAwait(false);
    var progressPumps = new RunProgressPumpCoordinator();
    SchedulerResult scheduled;
    StructuredError? cleanupError = null;
    try
    {
      scheduled = await _scheduler.ExecuteAsync(
          plan,
          (planned, token) => ReplanAndExecuteResourceAsync(
              run.RunId,
              graph.Nodes[planned.Definition.Id],
              planned,
              detected[planned.Definition.Id],
              compliance[planned.Definition.Id],
              transitions,
              events,
              progressPumps,
              cancellationDeadline,
              token),
          planned => _providers.GetRequired(
              planned.Definition.Type,
              planned.Definition.Provider).Capabilities,
          request.MaximumConcurrency,
          cancellationToken,
          transitions.PersistSchedulerTransitionAsync,
          cancellationDeadline,
          finalization => TrackRunFinalization(run.RunId, finalization)).ConfigureAwait(false);
      TrackRunFinalization(run.RunId, scheduled.UndrainedCompletion);
    }
    finally
    {
      Task? cleanupTask = null;
      try
      {
        if (!cancellationDeadline.IsStarted)
        {
          await _dispatcher.CompleteRunAsync(run.RunId, CancellationToken.None)
              .ConfigureAwait(false);
        }
        else
        {
          var remaining = cancellationDeadline.Remaining;
          using var cleanupCancellation = new CancellationTokenSource(remaining);
          cleanupTask = _dispatcher.CompleteRunAsync(
              run.RunId,
              cleanupCancellation.Token);
          await cleanupTask.WaitAsync(cancellationDeadline.Remaining).ConfigureAwait(false);
        }
      }
      catch (Exception exception)
      {
        if (cleanupTask is not null && !cleanupTask.IsCompleted)
        {
          ObserveFault(cleanupTask);
        }

        cleanupError = new StructuredError(
            WdemErrorCode.PermissionError,
            "Elevated host cleanup failed.",
            "The execution completed, but its elevated host could not be cleaned up normally.")
        {
          UnderlyingException = exception,
          IsRetryable = false
        };
      }
    }
    if (cancellationToken.IsCancellationRequested)
    {
      await progressPumps.SealAsync(cancellationDeadline.Remaining).ConfigureAwait(false);
    }

    var terminalResults = MergeTerminalResults(
        transitions.Current.ResourceResults,
        scheduled.Results);
    var completedRun = WithTerminalResourceResults(transitions.Current with
    {
      State = ExecutionState.Completed,
      EndedAtUtc = DateTimeOffset.UtcNow,
    }, terminalResults);
    return await PersistTerminalAsync(
        completedRun,
        events,
        cleanupError,
        transitions).ConfigureAwait(false);
  }

  private static IReadOnlyDictionary<string, ResourceResult> MergeTerminalResults(
      IReadOnlyDictionary<string, ResourceResult> persisted,
      IReadOnlyDictionary<string, ResourceResult> scheduled)
  {
    var merged = scheduled.ToDictionary(
        pair => pair.Key,
        pair => pair.Value,
        IdComparer);
    foreach (var pair in persisted)
    {
      if (pair.Value.State == ExecutionState.Completed &&
          (pair.Value.Outcome == ExecutionOutcome.Failed ||
           pair.Value.Outcome is ExecutionOutcome.Succeeded or ExecutionOutcome.NotRequired &&
           merged.TryGetValue(pair.Key, out var provisional) &&
           provisional.Outcome == ExecutionOutcome.Cancelled))
      {
        merged[pair.Key] = pair.Value;
      }
    }

    return merged;
  }

  private static ExecutionRun WithTerminalResourceResults(
      ExecutionRun run,
      IReadOnlyDictionary<string, ResourceResult> results) => run with
      {
        Outcome = RunOutcome(results),
        ResourceResults = results,
        RestartRequirements = results.Values
            .Select(result => result.RestartRequirement)
            .Where(requirement => requirement != RestartPolicy.NoRestart)
            .Distinct()
            .OrderBy(requirement => requirement)
            .ToArray(),
        RestartReasons = results.Values
            .Where(result => result.RestartRequirement != RestartPolicy.NoRestart)
            .Select(result => $"Resource '{result.ResourceId}' requires a restart.")
            .ToArray()
      };

  private async Task<ResourceResult> ReplanAndExecuteResourceAsync(
      Guid runId,
      ResolvedResource resolved,
      PlannedResource planned,
      DetectedState detectedBefore,
      ComplianceResult complianceBefore,
      RunTransitions transitions,
      RunEventPublisher events,
      RunProgressPumpCoordinator progressPumps,
      CancellationDrainDeadline cancellationDeadline,
      CancellationToken cancellationToken)
  {
    if (planned.Status != PlannedResourceStatus.Deferred)
    {
      return await ExecuteResourceAsync(
          runId,
          resolved.Definition,
          planned,
          detectedBefore,
          complianceBefore,
          events,
          progressPumps,
          cancellationDeadline,
          cancellationToken).ConfigureAwait(false);
    }

    var definition = resolved.Definition;
    var approvedFingerprint = planned.ResourcePlan.DesiredStateFingerprint;
    if (!string.Equals(
            ResourceDefinitionFingerprint.Create(definition),
            approvedFingerprint,
            StringComparison.Ordinal))
    {
      return DeferredPlanningFailure(
          definition.Id,
          detectedBefore,
          complianceBefore.Status,
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "The deferred resource definition changed after plan approval.",
              "The resource must be reviewed again before it can be executed.")
          {
            ResourceId = definition.Id,
            SuggestedAction = "Create and approve a new execution plan."
          });
    }

    cancellationToken.ThrowIfCancellationRequested();
    var provider = _providers.GetRequired(definition.Type, definition.Provider);
    DetectedState freshDetected;
    try
    {
      freshDetected = await provider.DetectAsync(definition, cancellationToken)
          .ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return DeferredPlanningFailure(
          definition.Id,
          detectedBefore,
          ComplianceStatus.DetectionFailed,
          DeferredPlanningError(definition.Id, exception));
    }

    ComplianceResult freshCompliance;
    try
    {
      freshCompliance = EvaluateCompliance(provider, definition, freshDetected);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return DeferredPlanningFailure(
          definition.Id,
          freshDetected,
          ComplianceStatus.DetectionFailed,
          DeferredPlanningError(definition.Id, exception));
    }

    PlannedResource freshPlan;
    try
    {
      freshPlan = await _planner.ReplanResourceAsync(
          resolved,
          freshDetected,
          approvedFingerprint,
          cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return DeferredPlanningFailure(
          definition.Id,
          freshDetected,
          freshCompliance.Status,
          DeferredPlanningError(definition.Id, exception));
    }

    if (freshPlan.Status is not (PlannedResourceStatus.Ready or
            PlannedResourceStatus.AlreadySatisfied) ||
        !freshPlan.ResourcePlan.IsExecutable ||
        !string.Equals(
            freshPlan.ResourcePlan.DesiredStateFingerprint,
            approvedFingerprint,
            StringComparison.Ordinal))
    {
      var error = freshPlan.Diagnostics.FirstOrDefault() ?? new StructuredError(
          WdemErrorCode.DependencyError,
          "The deferred resource is still unavailable.",
          "A declared dependency completed, but the provider still could not create an executable plan.")
      {
        ResourceId = definition.Id,
        IsRetryable = true
      };
      return DeferredPlanningFailure(
          definition.Id,
          freshDetected,
          freshCompliance.Status,
          error);
    }

    var refinementError = ValidateDeferredRefinement(planned, freshPlan);
    if (refinementError is not null)
    {
      return DeferredPlanningFailure(
          definition.Id,
          freshDetected,
          freshCompliance.Status,
          refinementError);
    }

    await transitions.ReplacePlannedResourceAsync(
        planned,
        freshPlan,
        cancellationToken).ConfigureAwait(false);
    try
    {
      if (freshPlan.RequiresElevation)
      {
        await _runStore.SealApprovedResourceAsync(
            runId,
            new ApprovedResourceSeal(definition, freshPlan.ResourcePlan),
            cancellationToken).ConfigureAwait(false);
      }
    }
    catch (Exception sealException)
    {
      var remaining = cancellationDeadline.Remaining;
      var rollbackBudget = remaining < _persistenceTimeout
          ? remaining
          : _persistenceTimeout;
      using var rollbackTimeout = new CancellationTokenSource(rollbackBudget);
      try
      {
        await transitions.ReplacePlannedResourceAsync(
            freshPlan,
            planned,
            rollbackTimeout.Token).ConfigureAwait(false);
      }
      catch (Exception rollbackException)
      {
        throw new AggregateException(
            "Deferred approval sealing failed and its plan rollback could not be persisted.",
            sealException,
            rollbackException);
      }

      throw;
    }

    return await ExecuteResourceAsync(
        runId,
        definition,
        freshPlan,
        freshDetected,
        freshCompliance,
        events,
        progressPumps,
        cancellationDeadline,
        cancellationToken).ConfigureAwait(false);
  }

  private static StructuredError DeferredPlanningError(string resourceId, Exception exception) =>
      new(
          WdemErrorCode.ProviderError,
          "Deferred resource planning failed.",
          "The resource could not be safely replanned after its dependencies completed.")
      {
        ResourceId = resourceId,
        IsRetryable = true,
        UnderlyingException = exception
      };

  private static StructuredError? ValidateDeferredRefinement(
      PlannedResource deferred,
      PlannedResource fresh)
  {
    var authorization = deferred.DeferredAuthorization;
    var definitionMatches = authorization is not null &&
        string.Equals(
            deferred.ResourcePlan.DesiredStateFingerprint,
            fresh.ResourcePlan.DesiredStateFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(deferred.Definition.Id, fresh.Definition.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(deferred.Definition.Type, fresh.Definition.Type, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(deferred.Definition.Provider, fresh.Definition.Provider, StringComparison.OrdinalIgnoreCase) &&
        deferred.Dependencies.Count == fresh.Dependencies.Count &&
        deferred.Dependencies.All(dependency =>
            fresh.Dependencies.Contains(dependency, IdComparer));
    if (!definitionMatches)
    {
      return DeferredAuthorizationError(
          deferred.Definition.Id,
          "The concrete plan does not match the approved deferred resource identity.");
    }

    var exceedsAuthorization = fresh.DeferredAuthorization is not null ||
        fresh.Origin != deferred.Origin ||
        RiskRank(fresh.Risk) > RiskRank(authorization!.MaximumRisk) ||
        fresh.RequiresElevation &&
            authorization.MaximumPrivilege != PrivilegeRequirement.Administrator ||
        fresh.IsDestructive && !authorization.AllowDestructive ||
        fresh.RestartPolicy > authorization.MaximumRestartPolicy ||
        fresh.ResourcePlan.Steps.Any(step =>
            !PlanStepAuthorizationPolicy.IsWithinBoundary(
                step,
                authorization.AllowedActions,
                authorization.MaximumPrivilege,
                authorization.MaximumRestartPolicy,
                authorization.AllowDestructive));
    return exceedsAuthorization
        ? DeferredAuthorizationError(
            deferred.Definition.Id,
            "The concrete plan exceeds the approved action, privilege, restart, or risk boundary.")
        : null;
  }

  private static int RiskRank(PlanRisk risk) => risk switch
  {
    PlanRisk.None => 0,
    PlanRisk.Standard => 1,
    PlanRisk.Elevated => 2,
    PlanRisk.Destructive => 3,
    _ => int.MaxValue
  };

  private static StructuredError DeferredAuthorizationError(string resourceId, string detail) =>
      new(
          WdemErrorCode.ConfigurationError,
          "The deferred plan exceeds its reviewed authorization.",
          detail)
      {
        ResourceId = resourceId,
        SuggestedAction = "Review and approve a new execution plan."
      };

  private static ResourceResult DeferredPlanningFailure(
      string resourceId,
      DetectedState detectedBefore,
      ComplianceStatus compliance,
      StructuredError error) => new()
      {
        ResourceId = resourceId,
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.Failed,
        FinalCompliance = compliance,
        DetectedBefore = detectedBefore,
        Progress = 0,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Error = error with { ResourceId = resourceId }
      };

  private async Task<ResourceResult> ExecuteResourceAsync(
      Guid runId,
      ResourceDefinition definition,
      PlannedResource planned,
      DetectedState detectedBefore,
      ComplianceResult complianceBefore,
      RunEventPublisher events,
      RunProgressPumpCoordinator progressPumps,
      CancellationDrainDeadline cancellationDeadline,
      CancellationToken cancellationToken)
  {
    var id = definition.Id;
    if ((planned.Status == PlannedResourceStatus.AlreadySatisfied ||
         !planned.ResourcePlan.RequiresApply) &&
        planned.ResourcePlan.ExecutionPreconditionFingerprint is null)
    {
      return CompletedNotRequired(id, detectedBefore, complianceBefore.Status);
    }

    var startedAt = DateTimeOffset.UtcNow;
    var provider = _providers.GetRequired(definition.Type, definition.Provider);
    ResourceApplyResult? applied = null;
    var progressBuffer = new ProviderProgressBuffer(
        planned.ResourcePlan.Steps.Select(step => step.Id));
    using var progressPersistence = new CancellationTokenSource();
    using var progressCancellationRegistration = cancellationToken.UnsafeRegister(
        static state =>
        {
          var (persistence, deadline) =
              ((CancellationTokenSource, CancellationDrainDeadline))state!;
          deadline.Start();
          var remaining = deadline.Remaining;
          var grace = TimeSpan.FromTicks(Math.Min(
              CancellationProgressDrainGrace.Ticks,
              remaining.Ticks / 2));
          if (grace <= TimeSpan.Zero)
          {
            persistence.Cancel();
          }
          else
          {
            persistence.CancelAfter(grace);
          }
        },
        (progressPersistence, cancellationDeadline));
    var progressPump = PumpProgressAsync(
        progressBuffer,
        events,
        id,
        progressPersistence.Token);
    using var progressRegistration = progressPumps.Register(
        progressBuffer,
        progressPersistence,
        progressPump);
    using var applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken);
    Exception? progressPersistenceFailure = null;
    try
    {
      cancellationToken.ThrowIfCancellationRequested();
      var applyTask = _dispatcher.ApplyAsync(
          runId,
          provider,
          definition,
          planned.ResourcePlan,
          new InlineProgress<ProviderProgress>(progressBuffer.Report),
          applyCancellation.Token,
          cancellationDeadline);
      if (!applyTask.IsCompleted)
      {
        var cancellationSignal = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completion = await Task.WhenAny(applyTask, progressPump, cancellationSignal)
            .ConfigureAwait(false);
        if (completion == progressPump)
        {
          try
          {
            await progressPump.ConfigureAwait(false);
          }
          catch (Exception exception) when (!progressPersistence.IsCancellationRequested)
          {
            progressPersistenceFailure = exception;
          }

          if (progressPersistenceFailure is not null)
          {
            cancellationDeadline.Start();
            progressBuffer.StopAccepting();
            ObserveFault(applyCancellation.CancelAsync());
            try
            {
              applied = await applyTask.WaitAsync(cancellationDeadline.Remaining)
                  .ConfigureAwait(false);
            }
            catch (Exception)
            {
              if (!applyTask.IsCompleted)
              {
                ObserveFault(applyTask);
              }

              ExceptionDispatchInfo.Capture(progressPersistenceFailure).Throw();
              throw;
            }
          }
        }

        if (completion == cancellationSignal)
        {
          progressBuffer.StopAccepting();
          try
          {
            await applyTask.WaitAsync(cancellationDeadline.Remaining).ConfigureAwait(false);
          }
          catch (TimeoutException)
          {
            ObserveFault(applyTask);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
          }
        }
      }

      if (progressPersistenceFailure is null)
      {
        applied = await applyTask.ConfigureAwait(false);
      }
    }
    catch (Exception) when (progressPersistenceFailure is not null)
    {
      ExceptionDispatchInfo.Capture(progressPersistenceFailure).Throw();
      throw;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return FailedApply(id, detectedBefore, planned, startedAt, exception);
    }
    finally
    {
      if (cancellationToken.IsCancellationRequested)
      {
        progressBuffer.StopAccepting();
      }
      else
      {
        progressBuffer.Complete();
        progressPersistence.CancelAfter(_persistenceTimeout);
      }

      try
      {
        var progressTimeout = cancellationToken.IsCancellationRequested
            ? cancellationDeadline.Remaining
            : _persistenceTimeout;
        if (progressTimeout > TimeSpan.Zero)
        {
          await progressPump.WaitAsync(progressTimeout).ConfigureAwait(false);
        }
        else
        {
          progressPersistence.Cancel();
          ObserveFault(progressPump);
        }
      }
      catch (OperationCanceledException) when (
          progressPersistence.IsCancellationRequested)
      {
        if (!cancellationToken.IsCancellationRequested)
        {
          progressPersistenceFailure = new TimeoutException(
              "Timed out while persisting provider progress.");
        }
      }
      catch (TimeoutException exception)
      {
        ObserveFault(progressPersistence.CancelAsync());
        ObserveFault(progressPump);
        if (!cancellationToken.IsCancellationRequested)
        {
          progressPersistenceFailure = new TimeoutException(
              "Timed out while persisting provider progress.",
              exception);
        }
      }
      catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
      {
        progressPersistenceFailure = exception;
      }
    }

    if (applied is null)
    {
      throw new InvalidOperationException(
          "The resource apply operation completed without a result.");
    }

    var stepResults = ToStepResults(planned, applied, startedAt);
    if (progressPersistenceFailure is not null)
    {
      throw new ResourceScheduler.ResourceExecutionEvidenceException(
          progressPersistenceFailure,
          AppliedEvidenceFailure(
              id,
              detectedBefore,
              planned,
              applied,
              stepResults,
              startedAt,
              progressPersistenceFailure));
    }

    var cancelled = cancellationToken.IsCancellationRequested;
    using var evidencePersistence = cancelled
        ? new CancellationTokenSource(cancellationDeadline.Remaining)
        : null;
    var evidenceToken = evidencePersistence?.Token ?? cancellationToken;
    try
    {
      foreach (var diagnostic in applied.Diagnostics)
      {
        await events.PublishLogAsync(
            id,
            diagnostic.StepId,
            diagnostic.Summary,
            diagnostic,
            evidenceToken).ConfigureAwait(false);
      }

      for (var index = 0; index < applied.StepResults.Count; index++)
      {
        ProviderStepResult step = applied.StepResults[index];
        StepResult stepResult = stepResults[index];
        await events.PublishStepAsync(
            id,
            step.StepId,
            step.Progress,
            step.Message ?? step.StepId,
            step.Error,
            evidenceToken,
            stepResult.State,
            stepResult.Outcome).ConfigureAwait(false);
      }
    }
    catch (OperationCanceledException) when (
        cancelled && evidencePersistence!.IsCancellationRequested)
    {
      // The structured result below remains the durable cancellation evidence.
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      throw new ResourceScheduler.ResourceExecutionEvidenceException(
          exception,
          AppliedEvidenceFailure(
              id,
              detectedBefore,
              planned,
              applied,
              stepResults,
              startedAt,
              exception));
    }

    if (applied.Outcome == ApplyOutcome.Succeeded)
    {
      var verified = await VerifySuccessfulApplyAsync(
          provider,
          definition,
          planned,
          applied,
          stepResults,
          detectedBefore,
          startedAt,
          cancellationToken).ConfigureAwait(false);
      return verified;
    }

    var outcome = applied.Outcome switch
    {
      ApplyOutcome.Cancelled => ExecutionOutcome.Cancelled,
      ApplyOutcome.NotRequired when complianceBefore.Status == ComplianceStatus.Satisfied =>
        ExecutionOutcome.NotRequired,
      ApplyOutcome.NotRequired => ExecutionOutcome.Failed,
      _ => ExecutionOutcome.Failed
    };
    var failedVerification = applied.Outcome == ApplyOutcome.Failed
        ? applied.FinalVerification
        : null;
    var completed = new ResourceResult
    {
      ResourceId = id,
      State = ExecutionState.Completed,
      Outcome = outcome,
      FinalCompliance = failedVerification?.Compliance ?? complianceBefore.Status,
      DetectedBefore = detectedBefore,
      DetectedAfter = failedVerification?.DetectedState,
      Progress = outcome == ExecutionOutcome.NotRequired ? 1 : 0,
      Message = failedVerification?.Message,
      StartedAtUtc = startedAt,
      EndedAtUtc = DateTimeOffset.UtcNow,
      Error = outcome is ExecutionOutcome.Failed or ExecutionOutcome.Cancelled
          ? applied.Error ?? failedVerification?.DetectedState.StructuredError ??
              (applied.Outcome == ApplyOutcome.NotRequired
              ? VerificationError(
                  id,
                  $"The provider reported no work was required, but compliance remained '{complianceBefore.Status}'.")
              : ApplyError(id, outcome))
          : null,
      RestartRequirement = applied.RestartRequirement ?? RestartPolicy.NoRestart,
      StepResults = stepResults
    };
    return completed;
  }

  private async Task<ResourceResult> VerifySuccessfulApplyAsync(
      IResourceProvider provider,
      ResourceDefinition definition,
      PlannedResource planned,
      ResourceApplyResult applied,
      IReadOnlyList<StepResult> stepResults,
      DetectedState detectedBefore,
      DateTimeOffset startedAt,
      CancellationToken cancellationToken)
  {
    try
    {
      var verification = TryGetAuthoritativeFinalVerification(
          provider,
          definition,
          applied,
          out var suppliedVerification)
              ? suppliedVerification
              : await provider.VerifyAsync(definition, cancellationToken).ConfigureAwait(false);
      var evaluated = EvaluateCompliance(provider, definition, verification.DetectedState);
      var verified = verification.Compliance == ComplianceStatus.Satisfied &&
          evaluated.Status == ComplianceStatus.Satisfied;
      var processStep = stepResults.LastOrDefault(step => step.ProcessExitCode is not null);
      var verificationError = verified
          ? null
          : (evaluated.Error ?? VerificationError(definition.Id, verification.Message)) with
          {
            ResourceId = definition.Id,
            StepId = evaluated.Error?.StepId ?? processStep?.StepId,
            ProcessExitCode = evaluated.Error?.ProcessExitCode ??
                processStep?.ProcessExitCode
          };
      return new ResourceResult
      {
        ResourceId = definition.Id,
        State = ExecutionState.Completed,
        Outcome = verified ? ExecutionOutcome.Succeeded : ExecutionOutcome.Failed,
        FinalCompliance = evaluated.Status,
        DetectedBefore = detectedBefore,
        DetectedAfter = verification.DetectedState,
        Progress = verified ? 1 : 0,
        Message = verification.Message,
        StartedAtUtc = startedAt,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Error = verificationError,
        RestartRequirement = applied.RestartRequirement ?? planned.RestartPolicy,
        StepResults = stepResults
      };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      var completedStep = stepResults.LastOrDefault();
      return new ResourceResult
      {
        ResourceId = definition.Id,
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.Cancelled,
        FinalCompliance = planned.ResourcePlan.Compliance,
        DetectedBefore = detectedBefore,
        Progress = completedStep?.Progress ?? 0,
        StartedAtUtc = startedAt,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Error = new StructuredError(
            WdemErrorCode.CancellationError,
            "Final resource verification was cancelled.",
            "Resource application completed, but cancellation was requested during final verification.")
        {
          ResourceId = definition.Id,
          StepId = completedStep?.StepId,
          ProcessExitCode = completedStep?.ProcessExitCode,
          IsRetryable = true
        },
        RestartRequirement = applied.RestartRequirement ?? planned.RestartPolicy,
        StepResults = stepResults
      };
    }
    catch (Exception exception)
    {
      var processStep = stepResults.LastOrDefault(step => step.ProcessExitCode is not null);
      return new ResourceResult
      {
        ResourceId = definition.Id,
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.Failed,
        FinalCompliance = ComplianceStatus.DetectionFailed,
        DetectedBefore = detectedBefore,
        StartedAtUtc = startedAt,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Error = VerificationError(definition.Id, exception.Message) with
        {
          StepId = processStep?.StepId,
          ProcessExitCode = processStep?.ProcessExitCode,
          UnderlyingException = exception
        },
        RestartRequirement = applied.RestartRequirement ?? planned.RestartPolicy,
        StepResults = stepResults
      };
    }
  }

  private bool TryGetAuthoritativeFinalVerification(
      IResourceProvider provider,
      ResourceDefinition definition,
      ResourceApplyResult applied,
      out VerificationResult verification)
  {
    verification = null!;
    var candidate = applied.FinalVerification;
    if (!applied.FinalizeAfterCancellation ||
        candidate is null ||
        candidate.Compliance != ComplianceStatus.Satisfied ||
        candidate.DetectedState.Outcome != DetectionOutcome.Succeeded ||
        !string.IsNullOrWhiteSpace(candidate.DetectedState.Error) ||
        candidate.DetectedState.StructuredError is not null ||
        !string.Equals(applied.ResourceId, definition.Id, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(candidate.ResourceId, definition.Id, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            candidate.DetectedState.ResourceId,
            definition.Id,
            StringComparison.OrdinalIgnoreCase) ||
        EvaluateCompliance(provider, definition, candidate.DetectedState).Status !=
            ComplianceStatus.Satisfied)
    {
      return false;
    }

    verification = candidate;
    return true;
  }

  private ComplianceResult EvaluateCompliance(
      ResourceDefinition definition,
      DetectedState state) => EvaluateCompliance(
          _providers.GetRequired(definition.Type, definition.Provider),
          definition,
          state);

  private ComplianceResult EvaluateCompliance(
      IResourceProvider provider,
      ResourceDefinition definition,
      DetectedState state) => _complianceEvaluator.Evaluate(
          ProviderResourceProjection.ForCompliance(definition, provider.Capabilities),
          state);

  private async Task<IReadOnlyDictionary<string, DetectedState>> DetectAsync(
      ResourceGraph graph,
      CancellationToken cancellationToken)
  {
    var result = new Dictionary<string, DetectedState>(IdComparer);
    foreach (var layer in graph.TopologicalLayers.OrderBy(layer => layer.Index))
    {
      foreach (var id in layer.ResourceIds)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var definition = graph.Nodes[id].Definition;
        try
        {
          var provider = _providers.GetRequired(definition.Type, definition.Provider);
          result[id] = await provider.DetectAsync(definition, cancellationToken)
              .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception exception)
        {
          result[id] = new DetectedState
          {
            ResourceId = id,
            Outcome = DetectionOutcome.Failed,
            Exists = false,
            Error = exception.Message,
            StructuredError = new StructuredError(
                WdemErrorCode.DetectionError,
                "Resource detection failed.",
                $"Resource '{id}' could not be detected.")
            {
              ResourceId = id,
              IsRetryable = true,
              UnderlyingException = exception
            }
          };
        }
      }
    }

    return result.ToFrozenDictionary(IdComparer);
  }

  private static IReadOnlyDictionary<string, ResourceResult> CreateInitialResults(
      RunMode mode,
      ExecutionPlan plan,
      IReadOnlyDictionary<string, DetectedState> detected,
      IReadOnlyDictionary<string, ComplianceResult> compliance)
  {
    return plan.Resources.ToDictionary(
        planned => planned.Definition.Id,
        planned => mode == RunMode.Inspect
            ? new ResourceResult
            {
              ResourceId = planned.Definition.Id,
              State = ExecutionState.Completed,
              Outcome = compliance[planned.Definition.Id].Status == ComplianceStatus.Satisfied
                  ? ExecutionOutcome.NotRequired
                  : ExecutionOutcome.Skipped,
              FinalCompliance = compliance[planned.Definition.Id].Status,
              DetectedBefore = detected[planned.Definition.Id],
              Progress = 1,
              EndedAtUtc = DateTimeOffset.UtcNow
            }
            : new ResourceResult
            {
              ResourceId = planned.Definition.Id,
              State = ExecutionState.Pending,
              FinalCompliance = compliance[planned.Definition.Id].Status,
              DetectedBefore = detected[planned.Definition.Id]
            },
        IdComparer);
  }

  private async Task<ExecutionRun> CompleteUnexecutableAsync(
      ExecutionRun run,
      RunEventPublisher events,
      CancellationToken cancellationToken)
  {
    var endedAt = DateTimeOffset.UtcNow;
    var results = run.ResourceResults.ToDictionary(
        pair => pair.Key,
        pair => pair.Value with
        {
          State = ExecutionState.Completed,
          Outcome = pair.Value.FinalCompliance == ComplianceStatus.Satisfied
              ? ExecutionOutcome.NotRequired
              : ExecutionOutcome.Failed,
          EndedAtUtc = endedAt,
          Error = pair.Value.FinalCompliance == ComplianceStatus.Satisfied
              ? null
              : new StructuredError(
                  WdemErrorCode.ProviderError,
                  "The execution plan is not executable.",
                  $"Resource '{pair.Key}' could not be planned for execution.")
              {
                ResourceId = pair.Key,
                IsRetryable = false
              }
        },
        IdComparer);
    var completed = run with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      EndedAtUtc = endedAt,
      ResourceResults = results
    };
    return await PersistTerminalAsync(completed, events).ConfigureAwait(false);
  }

  private async Task<ExecutionRun> PersistTerminalAsync(
      ExecutionRun run,
      RunEventPublisher events,
      StructuredError? cleanupError = null,
      RunTransitions? transitions = null)
  {
    using var saveTimeout = new CancellationTokenSource(_persistenceTimeout);
    var saved = transitions is null
        ? await _runStore.SaveAsync(run, saveTimeout.Token).ConfigureAwait(false)
        : await transitions.PersistTerminalAsync(run, saveTimeout.Token).ConfigureAwait(false);
    if (cleanupError is not null)
    {
      try
      {
        using var diagnosticTimeout = new CancellationTokenSource(_persistenceTimeout);
        await events.PublishLogAsync(
            null,
            null,
            cleanupError.Summary,
            cleanupError,
            diagnosticTimeout.Token,
            ProviderLogLevel.Warning).ConfigureAwait(false);
      }
      catch (Exception)
      {
        // Terminal state is durable; cleanup diagnostics are best effort.
      }
    }

    using var completionTimeout = new CancellationTokenSource(_persistenceTimeout);
    await events.PublishCompletedAsync(saved, completionTimeout.Token).ConfigureAwait(false);
    return saved;
  }

  private async Task<ExecutionRun> PersistPreparationFailureAsync(
      RunRequest request,
      RunMode mode,
      ProfileLoadResult loaded,
      IReadOnlyList<StructuredError> diagnostics,
      Guid? retriedFromRunId,
      Guid? recoveredFromRunId,
      CancellationToken cancellationToken)
  {
    var now = DateTimeOffset.UtcNow;
    var profileId = loaded.Profile?.Id ?? Path.GetFileNameWithoutExtension(request.ProfilePath);
    var profileVersion = loaded.Profile?.Version ?? "unknown";
    var run = new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = mode,
      ProfileSourcePath = CanonicalizeOrPreservePath(loaded.SourcePath),
      ProfileId = profileId,
      ProfileVersion = profileVersion,
      SelectedOptionalResourceIds = request.SelectedOptionalResourceIds,
      StartedAtUtc = now,
      EndedAtUtc = now,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      RetriedFromRunId = retriedFromRunId,
      RecoveredFromRunId = recoveredFromRunId,
      Machine = CurrentMachine(),
      Plan = ExecutionPlanner.CreatePlan(profileId, profileVersion, [], [], diagnostics),
      ResourceResults = new Dictionary<string, ResourceResult>(IdComparer)
    };
    _eventSink.BindCurrentScopeToRun(run.RunId);
    await _runStore.CreateAsync(run, cancellationToken).ConfigureAwait(false);
    var events = new RunEventPublisher(
        _runStore,
        _eventSink,
        _redactor,
        _timeProvider,
        run.RunId);
    await events.PublishRunStateAsync(run, cancellationToken).ConfigureAwait(false);
    await events.PublishPlanDiagnosticsAsync(run.Plan, cancellationToken).ConfigureAwait(false);
    await events.PublishCompletedAsync(run, cancellationToken).ConfigureAwait(false);
    return run;
  }

  private static ResourceGraph FilterGraph(
      ResourceGraph graph,
      IReadOnlySet<string> requestedIds)
  {
    var included = new HashSet<string>(requestedIds, IdComparer);
    var pending = new Stack<string>(included);
    while (pending.Count > 0)
    {
      var id = pending.Pop();
      if (!graph.Nodes.TryGetValue(id, out var resource))
      {
        throw new ArgumentException($"Resource '{id}' is not present in the current profile.");
      }

      foreach (var dependency in resource.Definition.Dependencies)
      {
        if (included.Add(dependency))
        {
          pending.Push(dependency);
        }
      }
    }

    var nodes = graph.Nodes
        .Where(pair => included.Contains(pair.Key))
        .ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
              RequiredBy = pair.Value.RequiredBy
                  .Where(included.Contains)
                  .ToFrozenSet(IdComparer)
            },
            IdComparer);
    var layers = graph.TopologicalLayers
        .Select(layer => layer with
        {
          ResourceIds = layer.ResourceIds.Where(included.Contains).ToArray()
        })
        .Where(layer => layer.ResourceIds.Count > 0)
        .ToArray();
    return new ResourceGraph(nodes, layers);
  }

  private static IReadOnlyList<ApprovedResourceSeal> CreateApprovedResourceSeals(
      ResourceGraph graph,
      ExecutionPlan plan) => plan.Resources
      .Where(resource =>
          resource.Status == PlannedResourceStatus.Ready &&
          resource.RequiresElevation &&
          resource.ResourcePlan.IsExecutable)
      .Select(resource => new ApprovedResourceSeal(
          graph.Nodes[resource.Definition.Id].Definition,
          resource.ResourcePlan))
      .ToArray();

  private static string CanonicalizeOrPreservePath(string path)
  {
    try
    {
      return Path.GetFullPath(path);
    }
    catch (Exception exception) when (
        exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return path;
    }
  }

  private static IReadOnlySet<string> PendingResourceIds(ExecutionRun run)
  {
    var ids = run.ResourceResults.Values
        .Where(result => run.State != ExecutionState.Completed &&
            (result.State != ExecutionState.Completed ||
             result.Outcome is ExecutionOutcome.Failed or
                 ExecutionOutcome.Cancelled or
                 ExecutionOutcome.Skipped) ||
            result.RestartRequirement != RestartPolicy.NoRestart &&
            !run.AcknowledgedRestartResourceIds.Contains(result.ResourceId))
        .Select(result => result.ResourceId)
        .ToHashSet(IdComparer);
    if (ids.Count == 0 &&
        run.State != ExecutionState.Completed &&
        run.Plan is not null)
    {
      foreach (var resource in run.Plan.Resources)
      {
        if (!run.ResourceResults.ContainsKey(resource.Definition.Id))
        {
          ids.Add(resource.Definition.Id);
        }
      }
    }

    return ids.ToFrozenSet(IdComparer);
  }

  private static ExecutionRun CompleteSupersededRun(
      ExecutionRun run,
      Guid replacementRunId)
  {
    var endedAt = DateTimeOffset.UtcNow;
    var results = run.ResourceResults.ToDictionary(
        pair => pair.Key,
        pair => pair.Value.State == ExecutionState.Completed
            ? pair.Value
            : pair.Value with
            {
              State = ExecutionState.Completed,
              Outcome = ExecutionOutcome.Cancelled,
              EndedAtUtc = endedAt,
              Error = new StructuredError(
                  WdemErrorCode.CancellationError,
                  "Execution continued in a recovery run.",
                  $"Resource '{pair.Key}' was superseded by recovery run " +
                      $"'{replacementRunId:D}'.")
              {
                ResourceId = pair.Key,
                IsRetryable = false
              }
            },
        IdComparer);
    var acknowledgedRestartResourceIds = run.AcknowledgedRestartResourceIds
        .Concat(run.ResourceResults.Values
            .Where(result => result.RestartRequirement != RestartPolicy.NoRestart)
            .Select(result => result.ResourceId))
        .ToHashSet(IdComparer);
    return run with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Cancelled,
      EndedAtUtc = endedAt,
      ResourceResults = results,
      AcknowledgedRestartResourceIds = acknowledgedRestartResourceIds
    };
  }

  private async Task CompleteRecoveryClaimAsync(
      ExecutionRun claimed,
      Guid replacementRunId,
      IReadOnlySet<string> resourceIds)
  {
    var terminal = claimed.State == ExecutionState.Completed
        ? claimed with
        {
          AcknowledgedRestartResourceIds = claimed.AcknowledgedRestartResourceIds
              .Concat(claimed.ResourceResults.Values
                  .Where(result => resourceIds.Contains(result.ResourceId) &&
                      result.RestartRequirement != RestartPolicy.NoRestart)
                  .Select(result => result.ResourceId))
              .ToHashSet(IdComparer)
        }
        : CompleteSupersededRun(claimed, replacementRunId);
    terminal = terminal with
    {
      Revision = checked(claimed.Revision + 1),
      RecoveryClaimId = null,
      RecoveryClaimedAtUtc = null
    };
    if (!await TryPersistClaimTransitionAsync(
            terminal,
            claimed.Revision,
            claimed.RecoveryClaimId)
        .ConfigureAwait(false))
    {
      throw new InvalidOperationException(
          $"Recovery claim for run '{claimed.RunId:D}' is no longer current.");
    }
  }

  private async Task<ExecutionRun?> FindSuccessfulRecoveryReplacementAsync(
      Guid priorRunId,
      CancellationToken cancellationToken)
  {
    var runs = await _runStore.ListAsync(cancellationToken).ConfigureAwait(false);
    return runs
        .Where(run => run.RecoveredFromRunId == priorRunId &&
            run.Mode == RunMode.Apply &&
            run.State == ExecutionState.Completed &&
            run.Outcome == ExecutionOutcome.Succeeded)
        .OrderBy(run => run.StartedAtUtc)
        .ThenBy(run => run.RunId)
        .FirstOrDefault();
  }

  private async Task ReleaseRecoveryClaimAsync(ExecutionRun claimed)
  {
    var released = claimed with
    {
      Revision = checked(claimed.Revision + 1),
      RecoveryClaimId = null,
      RecoveryClaimedAtUtc = null
    };
    await TryPersistClaimTransitionAsync(
        released,
        claimed.Revision,
        claimed.RecoveryClaimId).ConfigureAwait(false);
  }

  private async Task<bool> TryPersistClaimTransitionAsync(
      ExecutionRun run,
      long expectedRevision,
      Guid? expectedRecoveryClaimId)
  {
    using var timeout = new CancellationTokenSource(_persistenceTimeout);
    return await _runStore.TrySaveAsync(
        run,
        expectedRevision,
        expectedRecoveryClaimId,
        timeout.Token)
        .ConfigureAwait(false);
  }

  private static bool IsRecoverableState(ExecutionRun run) =>
      run.State is ExecutionState.Pending or ExecutionState.Ready or ExecutionState.Running ||
      run.RestartRequirements.Count > 0 && PendingResourceIds(run).Count > 0;

  private async Task<ExecutionRun> GetRequiredRunAsync(
      Guid runId,
      CancellationToken cancellationToken) =>
      await _runStore.GetAsync(runId, cancellationToken).ConfigureAwait(false) ??
      throw new KeyNotFoundException($"Execution run '{runId:D}' does not exist.");

  private static void ValidateRequest(RunRequest request)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfilePath);
    ArgumentNullException.ThrowIfNull(request.SelectedOptionalResourceIds);
    if (request.MaximumConcurrency is < 1 or > 32)
    {
      throw new ArgumentOutOfRangeException(
          nameof(request),
          request.MaximumConcurrency,
          "Maximum concurrency must be between 1 and 32.");
    }
  }

  private PlanApproval CreatePlanApproval(
      ExecutionPlan plan,
      PlanApprovalSource source)
  {
    return new PlanApproval
    {
      InitialPlanFingerprint = plan.Fingerprint,
      ConfirmedAtUtc = _timeProvider.GetUtcNow(),
      Source = source,
      DeferredAuthorizations = plan.Resources
          .Where(resource => resource.Status == PlannedResourceStatus.Deferred)
          .Select(resource =>
          {
            var authorization = resource.DeferredAuthorization ??
                throw new InvalidOperationException(
                    "A deferred resource is missing its authorization boundary.");
            return new DeferredAuthorizationProof
            {
              ResourceId = resource.Definition.Id,
              ResourceType = resource.Definition.Type,
              ProviderName = resource.Definition.Provider,
              DefinitionFingerprint = resource.ResourcePlan.DesiredStateFingerprint,
              Origin = resource.Origin,
              Dependencies = resource.Dependencies,
              AllowedActions = authorization.AllowedActions,
              MaximumPrivilege = authorization.MaximumPrivilege,
              MaximumRestartPolicy = authorization.MaximumRestartPolicy,
              MaximumRisk = authorization.MaximumRisk,
              AllowDestructive = authorization.AllowDestructive
            };
          })
          .ToArray()
    };
  }

  private static bool TryCreateApprovalBoundary(
      ExecutionRun prior,
      out PlanApprovalBoundary? boundary)
  {
    boundary = null;
    var plan = prior.Plan;
    var approval = prior.PlanApproval;
    var canonicalPlan = plan is null
        ? null
        : ExecutionPlanner.CreatePlan(
            plan.ProfileId,
            plan.ProfileVersion,
            plan.Layers,
            plan.Resources,
            plan.Errors);
    if (plan is null || approval is null ||
        canonicalPlan is null ||
        plan.PlanId != canonicalPlan.PlanId ||
        !string.Equals(plan.Fingerprint, canonicalPlan.Fingerprint, StringComparison.Ordinal) ||
        !IsSha256(approval.InitialPlanFingerprint) ||
        approval.ConfirmedAtUtc == default ||
        !Enum.IsDefined(approval.Source))
    {
      return false;
    }

    var proofIds = new HashSet<string>(IdComparer);
    foreach (var proof in approval.DeferredAuthorizations)
    {
      if (string.IsNullOrWhiteSpace(proof.ResourceId) ||
          string.IsNullOrWhiteSpace(proof.ResourceType) ||
          string.IsNullOrWhiteSpace(proof.ProviderName) ||
          !IsSha256(proof.DefinitionFingerprint) ||
          !Enum.IsDefined(proof.Origin) ||
          !Enum.IsDefined(proof.MaximumPrivilege) ||
          !Enum.IsDefined(proof.MaximumRestartPolicy) ||
          !Enum.IsDefined(proof.MaximumRisk) ||
          !proofIds.Add(proof.ResourceId) ||
          proof.AllowedActions.Count != 1 ||
          proof.AllowedActions.Any(action =>
              action == PlanAction.None || !Enum.IsDefined(action)) ||
          plan.Resources.Count(resource => string.Equals(
              resource.Definition.Id,
              proof.ResourceId,
              StringComparison.OrdinalIgnoreCase)) != 1)
      {
        return false;
      }

      var persistedResource = plan.Resources.Single(resource => string.Equals(
          resource.Definition.Id,
          proof.ResourceId,
          StringComparison.OrdinalIgnoreCase));
      if (!IsWithinApprovalBoundary(
              persistedResource,
              ResourceApprovalBoundary.From(proof)))
      {
        return false;
      }
    }

    var approvedResources = plan.Resources.Select(resource =>
    {
      var proof = approval.DeferredAuthorizations.SingleOrDefault(candidate => string.Equals(
          candidate.ResourceId,
          resource.Definition.Id,
          StringComparison.OrdinalIgnoreCase));
      return proof is null
          ? resource
          : RestoreApprovedDeferredResource(
              resource,
              ResourceApprovalBoundary.From(proof));
    }).ToArray();
    var approvedPlan = ExecutionPlanner.CreatePlan(
        plan.ProfileId,
        plan.ProfileVersion,
        plan.Layers,
        approvedResources,
        plan.Errors);
    if (!string.Equals(
            approval.InitialPlanFingerprint,
            approvedPlan.Fingerprint,
            StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    boundary = new PlanApprovalBoundary(approvedPlan, approval);
    return true;
  }

  private static bool IsPlanWithinApproval(
      ExecutionPlan freshPlan,
      PlanApprovalBoundary boundary)
  {
    foreach (var fresh in freshPlan.Resources)
    {
      var priorMatches = boundary.Plan.Resources.Where(resource => string.Equals(
          resource.Definition.Id,
          fresh.Definition.Id,
          StringComparison.OrdinalIgnoreCase)).ToArray();
      if (priorMatches.Length != 1)
      {
        return false;
      }

      var proof = boundary.Approval.DeferredAuthorizations.SingleOrDefault(candidate =>
          string.Equals(
              candidate.ResourceId,
              fresh.Definition.Id,
              StringComparison.OrdinalIgnoreCase));
      if (proof is not null)
      {
        if (!IsWithinApprovalBoundary(
                fresh,
                ResourceApprovalBoundary.From(proof)))
        {
          return false;
        }

        continue;
      }

      var prior = priorMatches[0];
      if (!IsWithinApprovalBoundary(
              fresh,
              ResourceApprovalBoundary.From(prior)) ||
          !AreStepsWithinApprovedPlan(fresh.ResourcePlan.Steps, prior.ResourcePlan.Steps))
      {
        return false;
      }
    }

    return true;
  }

  private static bool IsWithinApprovalBoundary(
      PlannedResource fresh,
      ResourceApprovalBoundary boundary)
  {
    var deferredStatusIsAllowed = boundary.AllowDeferredStatus &&
        fresh.Status == PlannedResourceStatus.Deferred &&
        !fresh.ResourcePlan.IsExecutable &&
        IsDeferredAuthorizationWithinBoundary(
            fresh.DeferredAuthorization,
            boundary);
    if (!string.Equals(
            fresh.Definition.Type,
            boundary.ResourceType,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            fresh.Definition.Provider,
            boundary.ProviderName,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(fresh.ResourcePlan.ResourceId, fresh.Definition.Id, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            fresh.ResourcePlan.ResourceType,
            boundary.ResourceType,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            fresh.ResourcePlan.ProviderName,
            boundary.ProviderName,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            ResourceDefinitionFingerprint.Create(fresh.Definition),
            boundary.DefinitionFingerprint,
            StringComparison.Ordinal) ||
        !string.Equals(
            fresh.ResourcePlan.DesiredStateFingerprint,
            boundary.DefinitionFingerprint,
            StringComparison.Ordinal) ||
        fresh.Origin != boundary.Origin ||
        fresh.Dependencies.Count != boundary.Dependencies.Count ||
        fresh.Dependencies.Any(dependency =>
            !boundary.Dependencies.Contains(dependency, IdComparer)) ||
        fresh.Status is not (PlannedResourceStatus.Ready or
            PlannedResourceStatus.AlreadySatisfied) && !deferredStatusIsAllowed ||
        RiskRank(fresh.Risk) > RiskRank(boundary.MaximumRisk) ||
        fresh.RequiresElevation &&
            boundary.MaximumPrivilege != PrivilegeRequirement.Administrator ||
        fresh.IsDestructive && !boundary.AllowDestructive ||
        fresh.RestartPolicy > boundary.MaximumRestartPolicy)
    {
      return false;
    }

    return fresh.ResourcePlan.Steps.All(step =>
        PlanStepAuthorizationPolicy.IsWithinBoundary(
            step,
            boundary.AllowedActions,
            boundary.MaximumPrivilege,
            boundary.MaximumRestartPolicy,
            boundary.AllowDestructive));
  }

  private static PlannedResource RestoreApprovedDeferredResource(
      PlannedResource resource,
      ResourceApprovalBoundary boundary)
  {
    var authorization = boundary.CreateDeferredAuthorization();
    var expectedAction = boundary.AllowedActions.Single();
    var approvedCompliance = resource.ResourcePlan.Compliance == ComplianceStatus.Satisfied
        ? expectedAction switch
        {
          PlanAction.Install => ComplianceStatus.Missing,
          PlanAction.Upgrade => ComplianceStatus.VersionMismatch,
          PlanAction.Configure when resource.Definition.Type.EndsWith(
              "settings",
              StringComparison.OrdinalIgnoreCase) => ComplianceStatus.Missing,
          _ => ComplianceStatus.ConfigurationMismatch
        }
        : resource.ResourcePlan.Compliance;
    var approvedIdentity = resource with
    {
      Origin = boundary.Origin,
      Dependencies = boundary.Dependencies,
      ResourcePlan = resource.ResourcePlan with
      {
        ResourceType = boundary.ResourceType,
        ProviderName = boundary.ProviderName,
        DesiredStateFingerprint = boundary.DefinitionFingerprint,
        ExecutionPreconditionFingerprint = null
      }
    };
    return ExecutionPlanner.CreateDeferredPlaceholder(
        approvedIdentity,
        authorization,
        approvedCompliance);
  }

  private static bool IsDeferredAuthorizationWithinBoundary(
      DeferredPlanAuthorization? authorization,
      ResourceApprovalBoundary boundary) =>
      authorization is not null &&
      authorization.AllowedActions.Count > 0 &&
      authorization.AllowedActions.All(action =>
          action != PlanAction.None && boundary.AllowedActions.Contains(action)) &&
      (authorization.MaximumPrivilege != PrivilegeRequirement.Administrator ||
          boundary.MaximumPrivilege == PrivilegeRequirement.Administrator) &&
      authorization.MaximumRestartPolicy <= boundary.MaximumRestartPolicy &&
      RiskRank(authorization.MaximumRisk) <= RiskRank(boundary.MaximumRisk) &&
      (!authorization.AllowDestructive || boundary.AllowDestructive);

  private static bool AreStepsWithinApprovedPlan(
      IReadOnlyList<PlanStep> freshSteps,
      IReadOnlyList<PlanStep> approvedSteps) =>
      freshSteps.All(freshStep =>
      {
        var matches = approvedSteps.Where(approvedStep => string.Equals(
            approvedStep.Id,
            freshStep.Id,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
          return false;
        }

        PlanStep approvedStep = matches[0];
        return PlanStepAuthorizationPolicy.IsWithinBoundary(
            freshStep,
            [approvedStep.Action],
            approvedStep.PrivilegeRequirement,
            approvedStep.RestartPolicy,
            approvedStep.IsDestructive);
      });

  private static bool IsSha256(string value) =>
      !string.IsNullOrWhiteSpace(value) &&
      value.Length == 64 &&
      value.All(Uri.IsHexDigit);

  private static ExecutionOutcome RunOutcome(
      IReadOnlyDictionary<string, ResourceResult> results) =>
      results.Values.Any(result => result.Outcome == ExecutionOutcome.Cancelled)
          ? ExecutionOutcome.Cancelled
          : results.Values.Any(result => result.Outcome is ExecutionOutcome.Failed or
              ExecutionOutcome.Skipped || result.State == ExecutionState.Blocked)
              ? ExecutionOutcome.Failed
              : ExecutionOutcome.Succeeded;

  private static ResourceResult CompletedNotRequired(
      string id,
      DetectedState detected,
      ComplianceStatus compliance) => new()
      {
        ResourceId = id,
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.NotRequired,
        FinalCompliance = compliance,
        DetectedBefore = detected,
        Progress = 1,
        EndedAtUtc = DateTimeOffset.UtcNow
      };

  private static ResourceResult FailedApply(
      string id,
      DetectedState detected,
      PlannedResource planned,
      DateTimeOffset startedAt,
      Exception exception) => new()
      {
        ResourceId = id,
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.Failed,
        FinalCompliance = planned.ResourcePlan.Compliance,
        DetectedBefore = detected,
        StartedAtUtc = startedAt,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Error = new StructuredError(
            WdemErrorCode.ProviderError,
            "Resource application failed.",
            $"The provider failed while applying resource '{id}'.")
        {
          ResourceId = id,
          IsRetryable = true,
          UnderlyingException = exception
        }
      };

  private static ResourceResult AppliedEvidenceFailure(
      string id,
      DetectedState detected,
      PlannedResource planned,
      ResourceApplyResult applied,
      IReadOnlyList<StepResult> stepResults,
      DateTimeOffset startedAt,
      Exception exception) => new()
      {
        ResourceId = id,
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.Failed,
        FinalCompliance = planned.ResourcePlan.Compliance,
        DetectedBefore = detected,
        Progress = stepResults.Count == 0 ? 0 : stepResults.Max(step => step.Progress),
        StartedAtUtc = startedAt,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Error = new StructuredError(
            WdemErrorCode.ProviderError,
            "Applied resource evidence could not be fully published.",
            $"The provider returned applied evidence for resource '{id}', but durable event delivery failed.")
        {
          ResourceId = id,
          IsRetryable = true,
          UnderlyingException = exception
        },
        RestartRequirement = applied.RestartRequirement ?? RestartPolicy.NoRestart,
        StepResults = stepResults
      };

  private static IReadOnlyList<StepResult> ToStepResults(
      PlannedResource planned,
      ResourceApplyResult applied,
      DateTimeOffset startedAt)
  {
    var descriptions = planned.ResourcePlan.Steps.ToDictionary(
        step => step.Id,
        step => step.Description,
        IdComparer);
    var endedAt = DateTimeOffset.UtcNow;
    return applied.StepResults.Select(step =>
    {
      var failed = step.Error is not null ||
          step.Succeeded == false ||
          step.Succeeded is null && step.ProcessExitCode is { } exitCode && exitCode != 0;
      return new StepResult
      {
        StepId = step.StepId,
        Name = descriptions.GetValueOrDefault(step.StepId, step.StepId),
        State = ExecutionState.Completed,
        Outcome = failed ? ExecutionOutcome.Failed : ExecutionOutcome.Succeeded,
        Progress = step.Progress,
        ProcessExitCode = step.ProcessExitCode,
        ProcessSucceeded = step.Succeeded,
        StartedAtUtc = startedAt,
        EndedAtUtc = endedAt,
        Error = step.Error
      };
    }).ToArray();
  }

  private static StructuredError ApplyError(string id, ExecutionOutcome outcome) => new(
      outcome == ExecutionOutcome.Cancelled
          ? WdemErrorCode.CancellationError
          : WdemErrorCode.ProviderError,
      outcome == ExecutionOutcome.Cancelled
          ? "Resource application was cancelled."
          : "Resource application failed.",
      $"The provider did not successfully apply resource '{id}'.")
  {
    ResourceId = id,
    IsRetryable = outcome == ExecutionOutcome.Failed
  };

  private static StructuredError VerificationError(string id, string? message) => new(
      WdemErrorCode.VerificationError,
      "Applied resource did not verify successfully.",
      string.IsNullOrWhiteSpace(message)
          ? $"Resource '{id}' was not satisfied after apply."
          : message)
  {
    ResourceId = id,
    IsRetryable = true
  };

  private static MachineInformation CurrentMachine() => new(
      RuntimeInformation.OSDescription,
      RuntimeInformation.OSArchitecture.ToString(),
      Environment.MachineName,
      Environment.UserName);

  private static async Task PumpProgressAsync(
      ProviderProgressBuffer buffer,
      RunEventPublisher events,
      string resourceId,
      CancellationToken cancellationToken)
  {
    await foreach (var progress in buffer.ReadAllAsync(cancellationToken).ConfigureAwait(false))
    {
      await events.PublishStepAsync(
          resourceId,
          progress.StepId,
          progress.Percent,
          progress.Message,
          null,
          cancellationToken).ConfigureAwait(false);
      await events.PublishLogAsync(
          resourceId,
          progress.StepId,
          progress.Message,
          null,
          cancellationToken,
          progress.LogLevel).ConfigureAwait(false);
    }
  }

  private static void ObserveFault(Task task) => _ = task.ContinueWith(
      static completed => _ = completed.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);

  private void TrackRunFinalization(Guid runId, Task finalization)
  {
    ArgumentNullException.ThrowIfNull(finalization);
    if (finalization.IsCompletedSuccessfully)
    {
      return;
    }

    (Guid RunId, Task Finalization)? evicted = null;
    lock (_runFinalizationsGate)
    {
      for (var node = _runFinalizationOrder.First; node is not null;)
      {
        var next = node.Next;
        if (node.Value.Finalization.IsCompletedSuccessfully)
        {
          RemoveRunFinalizationCore(node.Value.RunId, node.Value.Finalization);
          _runFinalizationOrder.Remove(node);
        }

        node = next;
      }

      if (_runFinalizations.ContainsKey(runId))
      {
        throw new InvalidOperationException(
            $"Run '{runId:D}' already has a registered finalization.");
      }

      if (_runFinalizations.Count >= MaximumRetainedFinalizations)
      {
        evicted = _runFinalizationOrder.First!.Value;
        _runFinalizationOrder.RemoveFirst();
        RemoveRunFinalizationCore(evicted.Value.RunId, evicted.Value.Finalization);
      }

      if (!_runFinalizations.TryAdd(runId, finalization))
      {
        throw new InvalidOperationException(
            $"Run '{runId:D}' already has a registered finalization.");
      }

      _runFinalizationOrder.AddLast((runId, finalization));
    }

    _ = finalization.ContinueWith(
        static (completed, state) =>
        {
          var registration = ((WeakReference<EnvironmentRunService> Service, Guid RunId))state!;
          if (completed.IsCompletedSuccessfully &&
              registration.Service.TryGetTarget(out var service))
          {
            service.RemoveRunFinalization(registration.RunId, completed);
          }
        },
        (new WeakReference<EnvironmentRunService>(this), runId),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    if (evicted is not null)
    {
      ObserveFault(evicted.Value.Finalization);
      Trace.TraceWarning(
          "Evicted finalization tracking for run '{0:D}' to preserve the registry bound.",
          evicted.Value.RunId);
    }
  }

  private void RemoveRunFinalization(Guid runId, Task finalization)
  {
    lock (_runFinalizationsGate)
    {
      RemoveRunFinalizationCore(runId, finalization);
      for (var node = _runFinalizationOrder.First; node is not null; node = node.Next)
      {
        if (node.Value.RunId == runId &&
            ReferenceEquals(node.Value.Finalization, finalization))
        {
          _runFinalizationOrder.Remove(node);
          break;
        }
      }
    }
  }

  private void RemoveRunFinalizationCore(Guid runId, Task finalization)
  {
    ((ICollection<KeyValuePair<Guid, Task>>)_runFinalizations).Remove(
        new KeyValuePair<Guid, Task>(runId, finalization));
  }

  private sealed record PlanApprovalBoundary(
      ExecutionPlan Plan,
      PlanApproval Approval);

  private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
  {
    public void Report(T value) => report(value);
  }

  private sealed class ProviderProgressBuffer
  {
    private const int Capacity = 32;
    private const string UnknownStep = "\0";
    private readonly object _gate = new();
    private readonly Channel<ProviderProgress> _channel = Channel.CreateBounded<ProviderProgress>(
        new BoundedChannelOptions(Capacity)
        {
          SingleReader = true,
          SingleWriter = false,
          AllowSynchronousContinuations = false,
          FullMode = BoundedChannelFullMode.Wait
        });
    private readonly HashSet<string> _knownSteps;
    private readonly Dictionary<string, ProviderProgress> _coalesced = [];
    private readonly HashSet<string> _terminalSteps = [];
    private bool _completed;
    private bool _discardReports;

    public ProviderProgressBuffer(IEnumerable<string> knownSteps)
    {
      _knownSteps = knownSteps.ToHashSet(IdComparer);
    }

    public void Report(ProviderProgress progress)
    {
      ArgumentNullException.ThrowIfNull(progress);
      var key = progress.StepId is not null && _knownSteps.Contains(progress.StepId)
          ? progress.StepId
          : UnknownStep;
      lock (_gate)
      {
        if (_discardReports)
        {
          return;
        }

        if (_completed)
        {
          throw new InvalidOperationException("Provider progress was reported after completion.");
        }

        if (_terminalSteps.Contains(key) && progress.Percent < 1)
        {
          return;
        }

        if (progress.Percent >= 1)
        {
          _terminalSteps.Add(key);
        }

        if (_channel.Writer.TryWrite(progress))
        {
          _coalesced.Remove(key);
        }
        else
        {
          _coalesced[key] = progress;
        }
      }
    }

    public void StopAccepting()
    {
      lock (_gate)
      {
        if (_completed)
        {
          return;
        }

        _discardReports = true;
        _completed = true;
        _channel.Writer.TryComplete();
      }
    }

    public void Complete()
    {
      lock (_gate)
      {
        if (_completed)
        {
          return;
        }

        _completed = true;
        _channel.Writer.TryComplete();
      }
    }

    public async IAsyncEnumerable<ProviderProgress> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
      while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
      {
        while (_channel.Reader.TryRead(out var progress))
        {
          yield return progress;
        }

        foreach (var progress in DrainCoalesced())
        {
          yield return progress;
        }
      }

      foreach (var progress in DrainCoalesced())
      {
        yield return progress;
      }
    }

    private IReadOnlyList<ProviderProgress> DrainCoalesced()
    {
      lock (_gate)
      {
        if (_coalesced.Count == 0)
        {
          return [];
        }

        var progress = _coalesced.Values.ToArray();
        _coalesced.Clear();
        return progress;
      }
    }
  }

  private sealed class RunProgressPumpCoordinator
  {
    private readonly object _gate = new();
    private readonly HashSet<ProgressPumpSession> _sessions = [];

    public IDisposable Register(
        ProviderProgressBuffer buffer,
        CancellationTokenSource persistence,
        Task pump)
    {
      var session = new ProgressPumpSession(buffer, persistence, pump);
      lock (_gate)
      {
        _sessions.Add(session);
      }

      return new ProgressPumpRegistration(this, session);
    }

    public async Task SealAsync(TimeSpan timeout)
    {
      ProgressPumpSession[] sessions;
      lock (_gate)
      {
        sessions = [.. _sessions];
      }

      await Task.WhenAll(sessions.Select(session =>
          session.SealAsync(timeout))).ConfigureAwait(false);
    }

    private void Unregister(ProgressPumpSession session)
    {
      lock (_gate)
      {
        _sessions.Remove(session);
      }
    }

    private sealed class ProgressPumpRegistration(
        RunProgressPumpCoordinator owner,
        ProgressPumpSession session) : IDisposable
    {
      private RunProgressPumpCoordinator? _owner = owner;

      public void Dispose()
      {
        var currentOwner = Interlocked.Exchange(ref _owner, null);
        if (currentOwner is null)
        {
          return;
        }

        session.Deactivate();
        currentOwner.Unregister(session);
      }
    }

    private sealed class ProgressPumpSession(
        ProviderProgressBuffer buffer,
        CancellationTokenSource persistence,
        Task pump)
    {
      private readonly object _gate = new();
      private bool _active = true;

      public async Task SealAsync(TimeSpan timeout)
      {
        lock (_gate)
        {
          if (!_active)
          {
            return;
          }

          buffer.StopAccepting();
          persistence.Cancel();
        }

        if (timeout <= TimeSpan.Zero)
        {
          ObserveFault(pump);
          return;
        }

        try
        {
          await pump.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
          // Sealing cancels in-flight persistence after the scheduler's drain window.
        }
        catch (TimeoutException)
        {
          ObserveFault(pump);
        }
      }

      public void Deactivate()
      {
        lock (_gate)
        {
          _active = false;
        }
      }
    }
  }

  private sealed class RunEventPublisher(
      IExecutionRunStore store,
      IRunEventSink sink,
      LogRedactor redactor,
      TimeProvider timeProvider,
      Guid runId)
  {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _sequence;

    public Task PublishRunStateAsync(
        ExecutionRun run,
        CancellationToken cancellationToken) => PublishAsync(
            RunEventKind.RunStateChanged,
            null,
            null,
            null,
            $"{run.Mode} {run.State}",
            null,
            ProviderLogLevel.Info,
            cancellationToken,
            run.State,
            run.Outcome);

    public async Task PublishPlanDiagnosticsAsync(
        ExecutionPlan? plan,
        CancellationToken cancellationToken)
    {
      foreach (var diagnostic in plan?.Errors ?? [])
      {
        await PublishLogAsync(
            diagnostic.ResourceId,
            diagnostic.StepId,
            diagnostic.Summary,
            diagnostic,
            cancellationToken,
            ProviderLogLevel.Error).ConfigureAwait(false);
      }
    }

    public Task PublishResourceAsync(
        ResourceResult result,
        CancellationToken cancellationToken) => PublishAsync(
            RunEventKind.ResourceStateChanged,
            result.ResourceId,
            null,
            result.Progress,
            result.Message ?? result.Error?.Summary ?? result.Outcome?.ToString() ??
                result.State.ToString(),
            result.Error,
            result.Error is null ? ProviderLogLevel.Info : ProviderLogLevel.Error,
            cancellationToken,
            result.State,
            result.Outcome,
            result.RestartRequirement);

    public Task PublishStepAsync(
        string resourceId,
        string? stepId,
        double progress,
        string message,
        StructuredError? error,
        CancellationToken cancellationToken,
        ExecutionState state = ExecutionState.Running,
        ExecutionOutcome? outcome = null) => PublishAsync(
            RunEventKind.StepProgress,
            resourceId,
            stepId,
            progress,
            message,
            error,
            error is null ? ProviderLogLevel.Info : ProviderLogLevel.Error,
            cancellationToken,
            state,
            outcome);

    public Task PublishLogAsync(
        string? resourceId,
        string? stepId,
        string message,
        StructuredError? error,
        CancellationToken cancellationToken,
        ProviderLogLevel level = ProviderLogLevel.Info) => PublishAsync(
            RunEventKind.Log,
            resourceId,
            stepId,
            null,
            message,
            error,
            level,
            cancellationToken);

    public Task PublishCompletedAsync(
        ExecutionRun run,
        CancellationToken cancellationToken) => PublishAsync(
            RunEventKind.Completed,
            null,
            null,
            1,
            run.Outcome?.ToString() ?? run.State.ToString(),
            null,
            run.Outcome == ExecutionOutcome.Succeeded
                ? ProviderLogLevel.Info
                : ProviderLogLevel.Error,
            cancellationToken,
            run.State,
            run.Outcome);

    private async Task PublishAsync(
        RunEventKind kind,
        string? resourceId,
        string? stepId,
        double? progress,
        string message,
        StructuredError? error,
        ProviderLogLevel level,
        CancellationToken cancellationToken,
        ExecutionState? state = null,
        ExecutionOutcome? outcome = null,
        RestartPolicy? restartRequirement = null)
    {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        var sequence = checked(_sequence + 1);
        var runEvent = redactor.Redact(new RunEvent(
            runId,
            sequence,
            timeProvider.GetUtcNow(),
            kind,
            resourceId,
            stepId,
            progress,
            message,
            error,
            state,
            outcome,
            restartRequirement));
        await store.AppendLogAsync(
            runId,
            RunLogEntry.FromEvent(runEvent, level),
            cancellationToken).ConfigureAwait(false);
        _sequence = sequence;
        await sink.PublishAsync(runEvent, cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        _gate.Release();
      }
    }
  }

  private sealed class RunTransitions(
      IExecutionRunStore store,
      RunEventPublisher events,
      ExecutionRun initial,
      TimeSpan persistenceTimeout)
  {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ExecutionRun _current = initial;

    public RunTransitions(IExecutionRunStore store, ExecutionRun initial)
        : this(
            store,
            new RunEventPublisher(
                store,
                new RunEventHub(),
                new LogRedactor(),
                TimeProvider.System,
                initial.RunId),
            initial,
            DefaultPersistenceTimeout)
    {
    }

    public ExecutionRun Current => _current;

    public async Task SetRunningAsync(CancellationToken cancellationToken)
    {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        var next = _current with { State = ExecutionState.Running };
        _current = await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        await events.PublishRunStateAsync(_current, cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        _gate.Release();
      }
    }

    public async Task SetResourceAsync(
        ResourceResult result,
        CancellationToken cancellationToken)
    {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        var results = _current.ResourceResults.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            IdComparer);
        var previous = results.GetValueOrDefault(result.ResourceId);
        results[result.ResourceId] = result with
        {
          DetectedBefore = result.DetectedBefore ?? previous?.DetectedBefore
        };
        var next = _current with { ResourceResults = results };
        if (next.State == ExecutionState.Completed)
        {
          next = WithTerminalResourceResults(next, results);
        }

        _current = await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        await events.PublishResourceAsync(
            _current.ResourceResults[result.ResourceId],
            cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        _gate.Release();
      }
    }

    public async Task ReplacePlannedResourceAsync(
        PlannedResource expected,
        PlannedResource replacement,
        CancellationToken cancellationToken)
    {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        var plan = _current.Plan ?? throw new InvalidOperationException(
            "The execution run has no plan to update.");
        var matches = plan.Resources.Where(resource => string.Equals(
            resource.Definition.Id,
            expected.Definition.Id,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 ||
            matches[0].Status != expected.Status ||
            !string.Equals(
                matches[0].ResourcePlan.DesiredStateFingerprint,
                expected.ResourcePlan.DesiredStateFingerprint,
                StringComparison.Ordinal))
        {
          throw new InvalidOperationException(
              "The persisted deferred plan changed before it could be updated.");
        }

        var resources = plan.Resources
            .Select(resource => string.Equals(
                resource.Definition.Id,
                expected.Definition.Id,
                StringComparison.OrdinalIgnoreCase)
                    ? replacement
                    : resource)
            .ToArray();
        var updatedPlan = ExecutionPlanner.CreatePlan(
            plan.ProfileId,
            plan.ProfileVersion,
            plan.Layers,
            resources,
            plan.Errors);
        _current = await store.SaveAsync(
            _current with { Plan = updatedPlan },
            cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        _gate.Release();
      }
    }

    public async Task PersistSchedulerTransitionAsync(ResourceResult result)
    {
      using var timeout = new CancellationTokenSource(persistenceTimeout);
      await SetResourceAsync(result, timeout.Token).ConfigureAwait(false);
    }

    public async Task<ExecutionRun> PersistTerminalAsync(
        ExecutionRun terminal,
        CancellationToken cancellationToken)
    {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        var results = MergeTerminalResults(
            _current.ResourceResults,
            terminal.ResourceResults);
        var next = WithTerminalResourceResults(terminal with
        {
          Revision = _current.Revision,
          RecoveryClaimId = _current.RecoveryClaimId
        }, results);
        _current = await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        return _current;
      }
      finally
      {
        _gate.Release();
      }
    }
  }
}
