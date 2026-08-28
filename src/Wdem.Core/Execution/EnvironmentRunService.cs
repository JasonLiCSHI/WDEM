using System.Collections.Frozen;
using System.Runtime.InteropServices;
using Wdem.Core.Compliance;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public sealed class EnvironmentRunService : IEnvironmentRunService
{
  private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
  private static readonly TimeSpan PersistenceTimeout = TimeSpan.FromSeconds(10);
  private readonly IProfileCatalog _profiles;
  private readonly ResourceGraphBuilder _graphBuilder;
  private readonly IResourceProviderRegistry _providers;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly IExecutionPlanner _planner;
  private readonly IResourceScheduler _scheduler;
  private readonly IExecutionRunStore _runStore;
  private readonly IResourceApplyDispatcher _dispatcher;
  private readonly TimeProvider _timeProvider;

  public EnvironmentRunService(
      IProfileCatalog profiles,
      ResourceGraphBuilder graphBuilder,
      IResourceProviderRegistry providers,
      IComplianceEvaluator complianceEvaluator,
      IExecutionPlanner planner,
      IResourceScheduler scheduler,
      IExecutionRunStore runStore,
      IResourceApplyDispatcher dispatcher,
      TimeProvider? timeProvider = null)
  {
    _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    _graphBuilder = graphBuilder ?? throw new ArgumentNullException(nameof(graphBuilder));
    _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    _planner = planner ?? throw new ArgumentNullException(nameof(planner));
    _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    _timeProvider = timeProvider ?? TimeProvider.System;
  }

  public Task<ExecutionRun> InspectAsync(
      RunRequest request,
      CancellationToken cancellationToken) =>
      ExecuteFreshAsync(request, RunMode.Inspect, resourceFilter: null, cancellationToken);

  public Task<ExecutionRun> ApplyAsync(
      RunRequest request,
      CancellationToken cancellationToken) =>
      ExecuteFreshAsync(request, RunMode.Apply, resourceFilter: null, cancellationToken);

  public async Task<ExecutionRun> RetryAsync(
      Guid priorRunId,
      IReadOnlySet<string> resourceIds,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resourceIds);
    var prior = await GetRequiredRunAsync(priorRunId, cancellationToken).ConfigureAwait(false);
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
        prior.SelectedOptionalResourceIds,
        RetriedFromRunId: prior.RunId);
    return await ExecuteFreshAsync(request, RunMode.Apply, requested, cancellationToken)
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

      candidates.Add(new RecoveryCandidate
      {
        RunId = run.RunId,
        ProfileSourcePath = run.ProfileSourcePath,
        StartedAtUtc = run.StartedAtUtc,
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
      throw new InvalidOperationException(
          $"Execution run '{priorRunId:D}' is not eligible for recovery.");
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
        claimed.SelectedOptionalResourceIds,
        RetriedFromRunId: claimed.RunId);
    ExecutionRun recovered;
    try
    {
      recovered = await FindSuccessfulRecoveryReplacementAsync(
          claimed.RunId,
          remaining,
          cancellationToken).ConfigureAwait(false) ??
          await ExecuteFreshAsync(
              request,
              RunMode.Apply,
              remaining,
              cancellationToken).ConfigureAwait(false);
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
      CancellationToken cancellationToken)
  {
    ValidateRequest(request);
    cancellationToken.ThrowIfCancellationRequested();

    var loaded = await _profiles.LoadFileAsync(request.ProfilePath, cancellationToken)
        .ConfigureAwait(false);
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
          cancellationToken).ConfigureAwait(false);
    }

    var graph = resourceFilter is null
        ? graphResult.Graph
        : FilterGraph(graphResult.Graph, resourceFilter);
    var detected = await DetectAsync(graph, cancellationToken).ConfigureAwait(false);
    var compliance = graph.Nodes.ToDictionary(
        pair => pair.Key,
        pair => _complianceEvaluator.Evaluate(pair.Value.Definition, detected[pair.Key]),
        IdComparer);
    var plan = await _planner.CreateAsync(
        graph,
        detected,
        profile.Id,
        profile.Version,
        cancellationToken).ConfigureAwait(false);
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
      RetriedFromRunId = request.RetriedFromRunId,
      Machine = CurrentMachine(),
      Graph = graph,
      Plan = plan,
      ResourceResults = initialResults
    };
    await _runStore.CreateAsync(run, cancellationToken).ConfigureAwait(false);

    if (mode == RunMode.Inspect)
    {
      var completed = run with
      {
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.Succeeded,
        EndedAtUtc = DateTimeOffset.UtcNow
      };
      return await PersistTerminalAsync(completed).ConfigureAwait(false);
    }

    if (!plan.IsExecutable)
    {
      return await CompleteUnexecutableAsync(run, cancellationToken).ConfigureAwait(false);
    }

    var transitions = new RunTransitions(_runStore, run);
    await transitions.SetRunningAsync(cancellationToken).ConfigureAwait(false);
    var scheduled = await _scheduler.ExecuteAsync(
        plan,
        (planned, token) => ExecuteResourceAsync(
            graph.Nodes[planned.Definition.Id].Definition,
            planned,
            detected[planned.Definition.Id],
            compliance[planned.Definition.Id],
            token),
        planned => _providers.GetRequired(
            planned.Definition.Type,
            planned.Definition.Provider).Capabilities,
        request.MaximumConcurrency,
        cancellationToken,
        transitions.PersistSchedulerTransitionAsync).ConfigureAwait(false);

    var completedRun = transitions.Current with
    {
      State = ExecutionState.Completed,
      Outcome = RunOutcome(scheduled.Results),
      EndedAtUtc = DateTimeOffset.UtcNow,
      ResourceResults = scheduled.Results,
      RestartRequirements = scheduled.Results.Values
          .Select(result => result.RestartRequirement)
          .Where(requirement => requirement != RestartPolicy.NoRestart)
          .Distinct()
          .OrderBy(requirement => requirement)
          .ToArray(),
      RestartReasons = scheduled.Results.Values
          .Where(result => result.RestartRequirement != RestartPolicy.NoRestart)
          .Select(result => $"Resource '{result.ResourceId}' requires a restart.")
          .ToArray()
    };
    return await PersistTerminalAsync(completedRun).ConfigureAwait(false);
  }

  private async Task<ResourceResult> ExecuteResourceAsync(
      ResourceDefinition definition,
      PlannedResource planned,
      DetectedState detectedBefore,
      ComplianceResult complianceBefore,
      CancellationToken cancellationToken)
  {
    var id = definition.Id;
    if (planned.Status == PlannedResourceStatus.AlreadySatisfied ||
        !planned.ResourcePlan.RequiresApply)
    {
      return CompletedNotRequired(id, detectedBefore, complianceBefore.Status);
    }

    var startedAt = DateTimeOffset.UtcNow;
    var provider = _providers.GetRequired(definition.Type, definition.Provider);
    ResourceApplyResult applied;
    try
    {
      applied = await _dispatcher.ApplyAsync(
          provider,
          definition,
          planned.ResourcePlan,
          progress: null,
          cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return FailedApply(id, detectedBefore, planned, startedAt, exception);
    }

    var stepResults = ToStepResults(planned, applied, startedAt);
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
    var completed = new ResourceResult
    {
      ResourceId = id,
      State = ExecutionState.Completed,
      Outcome = outcome,
      FinalCompliance = complianceBefore.Status,
      DetectedBefore = detectedBefore,
      Progress = outcome == ExecutionOutcome.NotRequired ? 1 : 0,
      StartedAtUtc = startedAt,
      EndedAtUtc = DateTimeOffset.UtcNow,
      Error = outcome is ExecutionOutcome.Failed or ExecutionOutcome.Cancelled
          ? applied.Error ?? (applied.Outcome == ApplyOutcome.NotRequired
              ? VerificationError(
                  id,
                  $"The provider reported no work was required, but compliance remained '{complianceBefore.Status}'.")
              : ApplyError(id, outcome))
          : null,
      RestartRequirement = RestartPolicy.NoRestart,
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
      var verification = await provider.VerifyAsync(definition, cancellationToken)
          .ConfigureAwait(false);
      var evaluated = _complianceEvaluator.Evaluate(definition, verification.DetectedState);
      var verified = verification.Compliance == ComplianceStatus.Satisfied &&
          evaluated.Status == ComplianceStatus.Satisfied;
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
        Error = verified ? null : VerificationError(definition.Id, verification.Message),
        RestartRequirement = verified ? planned.RestartPolicy : RestartPolicy.NoRestart,
        StepResults = stepResults
      };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
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
          UnderlyingException = exception
        },
        StepResults = stepResults
      };
    }
  }

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
    return await PersistTerminalAsync(completed).ConfigureAwait(false);
  }

  private async Task<ExecutionRun> PersistTerminalAsync(ExecutionRun run)
  {
    using var timeout = new CancellationTokenSource(PersistenceTimeout);
    return await _runStore.SaveAsync(run, timeout.Token).ConfigureAwait(false);
  }

  private async Task<ExecutionRun> PersistPreparationFailureAsync(
      RunRequest request,
      RunMode mode,
      ProfileLoadResult loaded,
      IReadOnlyList<StructuredError> diagnostics,
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
      RetriedFromRunId = request.RetriedFromRunId,
      Machine = CurrentMachine(),
      Plan = ExecutionPlanner.CreatePlan(profileId, profileVersion, [], [], diagnostics),
      ResourceResults = new Dictionary<string, ResourceResult>(IdComparer)
    };
    await _runStore.CreateAsync(run, cancellationToken).ConfigureAwait(false);
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
      IReadOnlySet<string> resourceIds,
      CancellationToken cancellationToken)
  {
    var runs = await _runStore.ListAsync(cancellationToken).ConfigureAwait(false);
    return runs
        .Where(run => run.RetriedFromRunId == priorRunId &&
            run.State == ExecutionState.Completed &&
            run.Outcome == ExecutionOutcome.Succeeded &&
            resourceIds.All(run.ResourceResults.ContainsKey))
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
    using var timeout = new CancellationTokenSource(PersistenceTimeout);
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
      var failed = step.Error is not null || step.ProcessExitCode is { } exitCode && exitCode != 0;
      return new StepResult
      {
        StepId = step.StepId,
        Name = descriptions.GetValueOrDefault(step.StepId, step.StepId),
        State = ExecutionState.Completed,
        Outcome = failed ? ExecutionOutcome.Failed : ExecutionOutcome.Succeeded,
        Progress = step.Progress,
        ProcessExitCode = step.ProcessExitCode,
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

  private sealed class RunTransitions(IExecutionRunStore store, ExecutionRun initial)
  {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ExecutionRun _current = initial;

    public ExecutionRun Current => _current;

    public async Task SetRunningAsync(CancellationToken cancellationToken)
    {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
        var next = _current with { State = ExecutionState.Running };
        _current = await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
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
        _current = await store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
      }
      finally
      {
        _gate.Release();
      }
    }

    public async Task PersistSchedulerTransitionAsync(ResourceResult result)
    {
      using var timeout = new CancellationTokenSource(PersistenceTimeout);
      await SetResourceAsync(result, timeout.Token).ConfigureAwait(false);
    }
  }
}
