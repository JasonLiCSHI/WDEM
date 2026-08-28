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

  public EnvironmentRunService(
      IProfileCatalog profiles,
      ResourceGraphBuilder graphBuilder,
      IResourceProviderRegistry providers,
      IComplianceEvaluator complianceEvaluator,
      IExecutionPlanner planner,
      IResourceScheduler scheduler,
      IExecutionRunStore runStore,
      IResourceApplyDispatcher dispatcher)
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
    return runs
        .Where(IsRecoverableState)
        .Select(run => new RecoveryCandidate
        {
          RunId = run.RunId,
          ProfileSourcePath = run.ProfileSourcePath,
          StartedAtUtc = run.StartedAtUtc,
          PendingResourceIds = PendingResourceIds(run)
        })
        .Where(candidate => candidate.PendingResourceIds.Count > 0)
        .OrderBy(candidate => candidate.StartedAtUtc)
        .ToArray();
  }

  public async Task<ExecutionRun> RecoverAsync(
      Guid priorRunId,
      CancellationToken cancellationToken)
  {
    var prior = await GetRequiredRunAsync(priorRunId, cancellationToken).ConfigureAwait(false);
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

    var request = new RunRequest(
        prior.ProfileSourcePath,
        prior.SelectedOptionalResourceIds,
        RetriedFromRunId: prior.RunId);
    return await ExecuteFreshAsync(request, RunMode.Apply, remaining, cancellationToken)
        .ConfigureAwait(false);
  }

  public async Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken)
  {
    var prior = await GetRequiredRunAsync(priorRunId, cancellationToken).ConfigureAwait(false);
    if (prior.State == ExecutionState.Completed)
    {
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
    await _runStore.SaveAsync(prior with
    {
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Cancelled,
      EndedAtUtc = endedAt,
      ResourceResults = results
    }, cancellationToken).ConfigureAwait(false);
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

    var sourcePath = Path.GetFullPath(loaded.SourcePath);
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
      await PersistTerminalAsync(completed).ConfigureAwait(false);
      return completed;
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
    await PersistTerminalAsync(completedRun).ConfigureAwait(false);
    return completedRun;
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
    await PersistTerminalAsync(completed).ConfigureAwait(false);
    return completed;
  }

  private async Task PersistTerminalAsync(ExecutionRun run)
  {
    using var timeout = new CancellationTokenSource(PersistenceTimeout);
    await _runStore.SaveAsync(run, timeout.Token).ConfigureAwait(false);
  }

  private async Task<ExecutionRun> PersistPreparationFailureAsync(
      RunRequest request,
      RunMode mode,
      ProfileLoadResult loaded,
      IReadOnlyList<StructuredError> diagnostics,
      CancellationToken cancellationToken)
  {
    var now = DateTimeOffset.UtcNow;
    var planId = Guid.NewGuid();
    var run = new ExecutionRun
    {
      RunId = Guid.NewGuid(),
      Mode = mode,
      ProfileSourcePath = Path.GetFullPath(loaded.SourcePath),
      ProfileId = loaded.Profile?.Id ?? Path.GetFileNameWithoutExtension(request.ProfilePath),
      ProfileVersion = loaded.Profile?.Version ?? "unknown",
      SelectedOptionalResourceIds = request.SelectedOptionalResourceIds,
      StartedAtUtc = now,
      EndedAtUtc = now,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      RetriedFromRunId = request.RetriedFromRunId,
      Machine = CurrentMachine(),
      Plan = new ExecutionPlan
      {
        PlanId = planId,
        Fingerprint = planId.ToString("N").ToUpperInvariant(),
        ProfileId = loaded.Profile?.Id ?? Path.GetFileNameWithoutExtension(request.ProfilePath),
        ProfileVersion = loaded.Profile?.Version ?? "unknown",
        Layers = [],
        Resources = [],
        IsExecutable = false,
        Errors = diagnostics
      },
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

  private static IReadOnlySet<string> PendingResourceIds(ExecutionRun run)
  {
    var ids = run.ResourceResults.Values
        .Where(result => result.State != ExecutionState.Completed ||
            result.Outcome is ExecutionOutcome.Failed or
                ExecutionOutcome.Cancelled or
                ExecutionOutcome.Skipped ||
            result.RestartRequirement != RestartPolicy.NoRestart)
        .Select(result => result.ResourceId)
        .ToHashSet(IdComparer);
    if (ids.Count == 0 && run.Plan is not null)
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
        _current = _current with { State = ExecutionState.Running };
        await store.SaveAsync(_current, cancellationToken).ConfigureAwait(false);
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
        _current = _current with { ResourceResults = results };
        await store.SaveAsync(_current, cancellationToken).ConfigureAwait(false);
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
