using System.Runtime.ExceptionServices;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public sealed class ResourceScheduler : IResourceScheduler
{
  private const int MinimumConcurrency = 1;
  private const int MaximumConcurrency = 32;
  private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(2);
  private readonly TimeSpan _drainTimeout;

  public ResourceScheduler(TimeSpan? drainTimeout = null)
  {
    _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
    if (_drainTimeout <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(drainTimeout));
    }
  }

  public TimeSpan CancellationDrainTimeout => _drainTimeout;

  public async Task<SchedulerResult> ExecuteAsync(
      ExecutionPlan plan,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
      int maximumConcurrency,
      CancellationToken cancellationToken,
      Func<ResourceResult, Task>? transitionAsync = null,
      CancellationDrainDeadline? cancellationDeadline = null)
  {
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(executeAsync);
    ArgumentNullException.ThrowIfNull(capabilitiesFor);
    if (maximumConcurrency is < MinimumConcurrency or > MaximumConcurrency)
    {
      throw new ArgumentOutOfRangeException(
          nameof(maximumConcurrency),
          maximumConcurrency,
          $"Concurrency must be between {MinimumConcurrency} and {MaximumConcurrency}.");
    }

    var resources = OrderResources(plan);
    var resourcesById = resources.ToDictionary(
        resource => resource.Definition.Id,
        StringComparer.OrdinalIgnoreCase);
    var results = resources.ToDictionary(
        resource => resource.Definition.Id,
        resource => Pending(resource.Definition.Id),
        StringComparer.OrdinalIgnoreCase);

    if (resources.Count == 0)
    {
      return Snapshot(results);
    }

    if (cancellationToken.IsCancellationRequested)
    {
      try
      {
        foreach (var resource in resources)
        {
          var cancelled = Cancelled(resource.Definition.Id);
          await NotifyTransitionAsync(transitionAsync, cancelled).ConfigureAwait(false);
          results[resource.Definition.Id] = cancelled;
        }
      }
      catch (TransitionObserverException exception)
      {
        ExceptionDispatchInfo.Capture(exception.Cause).Throw();
      }

      return Snapshot(results);
    }

    using var ownedCancellationDeadline = cancellationDeadline is null
        ? new CancellationDrainDeadline(_drainTimeout, cancellationToken)
        : null;
    cancellationDeadline ??= ownedCancellationDeadline!;

    var scheduling = CreateSchedulingMetadata(resources, capabilitiesFor);
    var globalSemaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    var providerSemaphores = scheduling.GroupLimits.ToDictionary(
        pair => pair.Key,
        pair => new SemaphoreSlim(pair.Value, pair.Value),
        StringComparer.OrdinalIgnoreCase);
    using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
        cancellationToken);
    var executionToken = executionCancellation.Token;
    var running = new Dictionary<string, Task<CompletedExecution>>(
        StringComparer.OrdinalIgnoreCase);
    var cancellationSignal = Task.Delay(Timeout.InfiniteTimeSpan, executionToken);
    var semaphoreDisposalDeferred = false;

    try
    {
      var remaining = resources
          .Select(resource => resource.Definition.Id)
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
      var rootFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      while (remaining.Count > 0 || running.Count > 0)
      {
        if (executionToken.IsCancellationRequested)
        {
          await CompleteRemainingAsCancelledAsync(
              resources,
              remaining,
              results,
              transitionAsync).ConfigureAwait(false);
        }
        else
        {
          var madeProgress = await BlockFailedDependentsAsync(
              resources,
              resourcesById,
              remaining,
              results,
              rootFailures,
              transitionAsync).ConfigureAwait(false);
          if (executionToken.IsCancellationRequested)
          {
            await CompleteRemainingAsCancelledAsync(
                resources,
                remaining,
                results,
                transitionAsync).ConfigureAwait(false);
          }
          else
          {
            var ready = resources
                .Where(resource => remaining.Contains(resource.Definition.Id))
                .Where(resource => DependenciesSucceeded(resource, results))
                .ToArray();
            foreach (var resource in ready)
            {
              if (executionToken.IsCancellationRequested)
              {
                break;
              }

              var id = resource.Definition.Id;
              var readyResult = Ready(id);
              await NotifyTransitionAsync(transitionAsync, readyResult).ConfigureAwait(false);
              results[id] = readyResult;
              remaining.Remove(id);
              running.Add(id, ExecuteOneAsync(
                  resource,
                  executeAsync,
                  globalSemaphore,
                  providerSemaphores[scheduling.GroupsByResource[id]],
                  executionToken,
                  transitionAsync));
            }

            if (executionToken.IsCancellationRequested)
            {
              await CompleteRemainingAsCancelledAsync(
                  resources,
                  remaining,
                  results,
                  transitionAsync).ConfigureAwait(false);
            }
            else if (ready.Length == 0 && running.Count == 0 && remaining.Count > 0)
            {
              if (madeProgress)
              {
                continue;
              }

              await BlockUnsatisfiedResourcesAsync(
                  resources,
                  remaining,
                  results,
                  rootFailures,
                  transitionAsync).ConfigureAwait(false);
              break;
            }
          }
        }

        if (running.Count == 0)
        {
          continue;
        }

        var resourceCompletion = Task.WhenAny(running.Values);
        var activity = await Task.WhenAny(resourceCompletion, cancellationSignal)
            .ConfigureAwait(false);
        if (activity == cancellationSignal)
        {
          var runningTasks = running.Values.ToArray();
          var drained = await CancelAndDrainAsync(
              executionCancellation,
              runningTasks,
              cancellationDeadline.Remaining).ConfigureAwait(false);
          if (!drained)
          {
            semaphoreDisposalDeferred = true;
            DisposeSemaphoresAfterCompletion(
                runningTasks,
                globalSemaphore,
                providerSemaphores.Values.ToArray());
          }

          foreach (var id in running.Keys.ToArray())
          {
            var execution = running[id];
            ObserveFault(execution);
            var result = execution.IsCompletedSuccessfully
                ? execution.Result.Result
                : Cancelled(id);
            await NotifyTransitionAsync(transitionAsync, result).ConfigureAwait(false);
            results[id] = result;
            if (IsBlockingOutcome(result.Outcome))
            {
              rootFailures[id] = id;
            }
          }

          running.Clear();
          continue;
        }

        await resourceCompletion.ConfigureAwait(false);
        var completedIds = resources
            .Select(resource => resource.Definition.Id)
            .Where(id => running.TryGetValue(id, out var execution) && execution.IsCompleted)
            .ToArray();

        foreach (var id in completedIds)
        {
          var execution = await running[id].ConfigureAwait(false);
          running.Remove(id);
          await NotifyTransitionAsync(transitionAsync, execution.Result).ConfigureAwait(false);
          results[id] = execution.Result;
          if (IsBlockingOutcome(execution.Result.Outcome))
          {
            rootFailures[id] = id;
          }
        }
      }

      return Snapshot(results);
    }
    catch (Exception exception)
    {
      if (!semaphoreDisposalDeferred)
      {
        var runningTasks = running.Values.ToArray();
        var drained = await CancelAndDrainAsync(
            executionCancellation,
            runningTasks,
            cancellationDeadline.Remaining).ConfigureAwait(false);
        if (!drained)
        {
          semaphoreDisposalDeferred = true;
          DisposeSemaphoresAfterCompletion(
              runningTasks,
              globalSemaphore,
              providerSemaphores.Values.ToArray());
        }
      }

      if (exception is TransitionObserverException transitionException)
      {
        ExceptionDispatchInfo.Capture(transitionException.Cause).Throw();
      }

      throw;
    }
    finally
    {
      if (!semaphoreDisposalDeferred)
      {
        globalSemaphore.Dispose();
        foreach (var semaphore in providerSemaphores.Values)
        {
          semaphore.Dispose();
        }
      }
    }
  }

  private static SchedulingMetadata CreateSchedulingMetadata(
      IReadOnlyList<PlannedResource> resources,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor)
  {
    var groupsByResource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var groupLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    foreach (var resource in resources)
    {
      var capabilities = capabilitiesFor(resource) ?? throw new InvalidOperationException(
          $"No provider capabilities were returned for resource '{resource.Definition.Id}'.");
      if (capabilities.MaxConcurrentOperations < MinimumConcurrency)
      {
        throw new ArgumentOutOfRangeException(
            nameof(ProviderCapabilities.MaxConcurrentOperations),
            capabilities.MaxConcurrentOperations,
            $"Provider concurrency for resource '{resource.Definition.Id}' must be positive.");
      }

      var group = capabilities.ConcurrencyGroup ??
          $"{resource.Definition.Type}\0{resource.Definition.Provider}";
      groupsByResource.Add(resource.Definition.Id, group);
      groupLimits[group] = groupLimits.TryGetValue(group, out var existing)
          ? Math.Min(existing, capabilities.MaxConcurrentOperations)
          : capabilities.MaxConcurrentOperations;
    }

    return new SchedulingMetadata(groupsByResource, groupLimits);
  }

  private static IReadOnlyList<PlannedResource> OrderResources(ExecutionPlan plan)
  {
    var resources = plan.Resources.ToArray();
    var resourcesById = resources.ToDictionary(
        resource => resource.Definition.Id,
        StringComparer.OrdinalIgnoreCase);
    var ordered = new List<PlannedResource>(resources.Length);
    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var layer in plan.Layers.OrderBy(layer => layer.Index))
    {
      foreach (var id in layer.ResourceIds)
      {
        if (resourcesById.TryGetValue(id, out var resource) && added.Add(id))
        {
          ordered.Add(resource);
        }
      }
    }

    foreach (var resource in resources)
    {
      if (added.Add(resource.Definition.Id))
      {
        ordered.Add(resource);
      }
    }

    return ordered;
  }

  private static async Task<bool> BlockFailedDependentsAsync(
      IReadOnlyList<PlannedResource> resources,
      IReadOnlyDictionary<string, PlannedResource> resourcesById,
      ISet<string> remaining,
      IDictionary<string, ResourceResult> results,
      IDictionary<string, string> rootFailures,
      Func<ResourceResult, Task>? transitionAsync)
  {
    var madeProgress = false;
    bool blockedInPass;
    do
    {
      blockedInPass = false;
      foreach (var resource in resources)
      {
        var id = resource.Definition.Id;
        if (!remaining.Contains(id))
        {
          continue;
        }

        var failedDependency = resource.Dependencies.FirstOrDefault(dependency =>
            !resourcesById.ContainsKey(dependency) ||
            results.TryGetValue(dependency, out var result) &&
            (result.State == ExecutionState.Blocked || IsBlockingOutcome(result.Outcome)));
        if (failedDependency is null)
        {
          continue;
        }

        var rootFailure = rootFailures.TryGetValue(failedDependency, out var root)
            ? root
            : failedDependency;
        var blocked = Blocked(id, rootFailure);
        await NotifyTransitionAsync(transitionAsync, blocked).ConfigureAwait(false);
        results[id] = blocked;
        rootFailures[id] = rootFailure;
        remaining.Remove(id);
        madeProgress = true;
        blockedInPass = true;
      }
    }
    while (blockedInPass);

    return madeProgress;
  }

  private static bool DependenciesSucceeded(
      PlannedResource resource,
      IReadOnlyDictionary<string, ResourceResult> results) =>
      resource.Dependencies.All(dependency =>
          results.TryGetValue(dependency, out var result) &&
          result.State == ExecutionState.Completed &&
          result.Outcome is ExecutionOutcome.Succeeded or ExecutionOutcome.NotRequired);

  private static async Task BlockUnsatisfiedResourcesAsync(
      IReadOnlyList<PlannedResource> resources,
      ISet<string> remaining,
      IDictionary<string, ResourceResult> results,
      IDictionary<string, string> rootFailures,
      Func<ResourceResult, Task>? transitionAsync)
  {
    foreach (var resource in resources)
    {
      var id = resource.Definition.Id;
      if (!remaining.Remove(id))
      {
        continue;
      }

      var dependency = resource.Dependencies.FirstOrDefault() ?? id;
      var blocked = Blocked(id, dependency);
      await NotifyTransitionAsync(transitionAsync, blocked).ConfigureAwait(false);
      results[id] = blocked;
      rootFailures[id] = dependency;
    }
  }

  private static async Task CompleteRemainingAsCancelledAsync(
      IReadOnlyList<PlannedResource> resources,
      ISet<string> remaining,
      IDictionary<string, ResourceResult> results,
      Func<ResourceResult, Task>? transitionAsync)
  {
    foreach (var resource in resources)
    {
      if (remaining.Remove(resource.Definition.Id))
      {
        var cancelled = Cancelled(resource.Definition.Id);
        await NotifyTransitionAsync(transitionAsync, cancelled).ConfigureAwait(false);
        results[resource.Definition.Id] = cancelled;
      }
    }
  }

  private static async Task<CompletedExecution> ExecuteOneAsync(
      PlannedResource resource,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      SemaphoreSlim globalSemaphore,
      SemaphoreSlim providerSemaphore,
      CancellationToken cancellationToken,
      Func<ResourceResult, Task>? transitionAsync)
  {
    var providerAcquired = false;
    var globalAcquired = false;
    DateTimeOffset? startedAt = null;
    var id = resource.Definition.Id;
    try
    {
      await providerSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
      providerAcquired = true;
      await globalSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
      globalAcquired = true;
      cancellationToken.ThrowIfCancellationRequested();

      startedAt = DateTimeOffset.UtcNow;
      await NotifyTransitionAsync(transitionAsync, Running(id, startedAt.Value)).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      var returned = await executeAsync(resource, cancellationToken).ConfigureAwait(false);
      if (returned is null)
      {
        throw new InvalidOperationException("The resource execution delegate returned no result.");
      }

      var endedAt = DateTimeOffset.UtcNow;
      if (returned.Outcome is not { } outcome || !Enum.IsDefined(outcome))
      {
        return new CompletedExecution(id, Failed(
            id,
            new InvalidOperationException(
                "The resource execution delegate returned an invalid outcome."),
            startedAt,
            endedAt,
            returned));
      }

      if (outcome is ExecutionOutcome.Succeeded or ExecutionOutcome.NotRequired &&
          HasIncoherentStepEvidence(returned))
      {
        return new CompletedExecution(id, Failed(
            id,
            new InvalidOperationException(
                "The resource execution delegate returned incoherent step evidence for a dependency-satisfying outcome."),
            startedAt,
            endedAt,
            returned));
      }

      var result = returned with
      {
        ResourceId = id,
        State = ExecutionState.Completed,
        StartedAtUtc = startedAt,
        EndedAtUtc = endedAt
      };
      return new CompletedExecution(id, result);
    }
    catch (TransitionObserverException)
    {
      throw;
    }
    catch (RequiredRunEventDeliveryException)
    {
      throw;
    }
    catch (OperationCanceledException exception)
        when (cancellationToken.IsCancellationRequested)
    {
      return new CompletedExecution(id, Cancelled(id, exception, startedAt));
    }
    catch (Exception exception)
    {
      return new CompletedExecution(id, Failed(id, exception, startedAt));
    }
    finally
    {
      if (globalAcquired)
      {
        globalSemaphore.Release();
      }

      if (providerAcquired)
      {
        providerSemaphore.Release();
      }
    }
  }

  private static bool IsBlockingOutcome(ExecutionOutcome? outcome) =>
      outcome is ExecutionOutcome.Failed or
          ExecutionOutcome.Cancelled or
          ExecutionOutcome.Skipped;

  private static bool HasIncoherentStepEvidence(ResourceResult result) =>
      result.StepResults.Any(step =>
          step is null ||
          step.State != ExecutionState.Completed ||
          step.Outcome is not (ExecutionOutcome.Succeeded or ExecutionOutcome.NotRequired) ||
          step.ProcessExitCode is { } exitCode && exitCode != 0 &&
          step.ProcessSucceeded != true);

  private static ResourceResult Pending(string id) => new()
  {
    ResourceId = id,
    State = ExecutionState.Pending
  };

  private static ResourceResult Ready(string id) => new()
  {
    ResourceId = id,
    State = ExecutionState.Ready
  };

  private static ResourceResult Running(string id, DateTimeOffset startedAtUtc) => new()
  {
    ResourceId = id,
    State = ExecutionState.Running,
    StartedAtUtc = startedAtUtc
  };

  private static Task NotifyTransitionAsync(
      Func<ResourceResult, Task>? transitionAsync,
      ResourceResult result) => ObserveTransitionAsync(transitionAsync, result);

  private static async Task ObserveTransitionAsync(
      Func<ResourceResult, Task>? transitionAsync,
      ResourceResult result)
  {
    if (transitionAsync is null)
    {
      return;
    }

    try
    {
      await transitionAsync(result).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
      throw new TransitionObserverException(exception);
    }
  }

  private static async Task<bool> CancelAndDrainAsync(
      CancellationTokenSource cancellation,
      IReadOnlyList<Task<CompletedExecution>> running,
      TimeSpan timeout)
  {
    var runningCompletion = Task.WhenAll(running);
    var cancellationCompletion = IgnoreFailureAsync(cancellation.CancelAsync());
    var combined = Task.WhenAll(runningCompletion, cancellationCompletion);
    try
    {
      await combined.WaitAsync(timeout).ConfigureAwait(false);
    }
    catch (Exception)
    {
      // The initiating exception remains the scheduler failure.
    }

    ObserveFault(combined);
    ObserveFault(runningCompletion);
    return runningCompletion.IsCompleted;
  }

  private static async Task IgnoreFailureAsync(Task task)
  {
    try
    {
      await task.ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Cancellation callback failures do not replace the initiating exception.
    }
  }

  private static void DisposeSemaphoresAfterCompletion(
      IReadOnlyList<Task<CompletedExecution>> running,
      SemaphoreSlim globalSemaphore,
      IReadOnlyList<SemaphoreSlim> providerSemaphores)
  {
    var completion = Task.WhenAll(running);
    ObserveFault(completion);
    _ = completion.ContinueWith(
        static (_, state) =>
        {
          var owned = ((SemaphoreSlim Global, IReadOnlyList<SemaphoreSlim> Providers))state!;
          owned.Global.Dispose();
          foreach (var semaphore in owned.Providers)
          {
            semaphore.Dispose();
          }
        },
        (globalSemaphore, providerSemaphores),
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
  }

  private static void ObserveFault(Task task) => _ = task.ContinueWith(
      static completed => _ = completed.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);

  private static ResourceResult Blocked(string id, string failedDependency) => new()
  {
    ResourceId = id,
    State = ExecutionState.Blocked,
    Outcome = ExecutionOutcome.Skipped,
    EndedAtUtc = DateTimeOffset.UtcNow,
    Error = new StructuredError(
        WdemErrorCode.DependencyError,
        "Resource blocked by a failed dependency.",
        $"Resource '{id}' was not started because dependency '{failedDependency}' did not succeed.")
    {
      ResourceId = id,
      IsRetryable = false
    }
  };

  private static ResourceResult Cancelled(
      string id,
      Exception? exception = null,
      DateTimeOffset? startedAtUtc = null) => new()
      {
        ResourceId = id,
        State = ExecutionState.Completed,
        Outcome = ExecutionOutcome.Cancelled,
        StartedAtUtc = startedAtUtc,
        EndedAtUtc = DateTimeOffset.UtcNow,
        Error = new StructuredError(
        WdemErrorCode.CancellationError,
        "Resource execution was cancelled.",
        $"Resource '{id}' did not complete because execution was cancelled.")
        {
          ResourceId = id,
          IsRetryable = false,
          UnderlyingException = exception
        }
      };

  private static ResourceResult Failed(
      string id,
      Exception exception,
      DateTimeOffset? startedAtUtc = null,
      DateTimeOffset? endedAtUtc = null,
      ResourceResult? returned = null)
  {
    var result = returned ?? new ResourceResult
    {
      ResourceId = id,
      State = ExecutionState.Completed
    };
    return result with
    {
      ResourceId = id,
      State = ExecutionState.Completed,
      Outcome = ExecutionOutcome.Failed,
      StartedAtUtc = startedAtUtc,
      EndedAtUtc = endedAtUtc ?? DateTimeOffset.UtcNow,
      Error = new StructuredError(
          WdemErrorCode.ProviderError,
          "Resource execution failed.",
          $"The provider failed while executing resource '{id}'.")
      {
        ResourceId = id,
        IsRetryable = false,
        UnderlyingException = exception
      }
    };
  }

  private static SchedulerResult Snapshot(
      IReadOnlyDictionary<string, ResourceResult> results) => new()
      {
        Results = results
      };

  private sealed record SchedulingMetadata(
      IReadOnlyDictionary<string, string> GroupsByResource,
      IReadOnlyDictionary<string, int> GroupLimits);

  private sealed record CompletedExecution(string ResourceId, ResourceResult Result);

  private sealed class TransitionObserverException(Exception cause) : Exception(
      "The resource transition observer failed.",
      cause)
  {
    public Exception Cause { get; } = cause;
  }
}
