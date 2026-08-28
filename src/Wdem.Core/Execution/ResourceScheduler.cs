using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public sealed class ResourceScheduler : IResourceScheduler
{
  private const int MinimumConcurrency = 1;
  private const int MaximumConcurrency = 32;

  public async Task<SchedulerResult> ExecuteAsync(
      ExecutionPlan plan,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
      int maximumConcurrency,
      CancellationToken cancellationToken)
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
      foreach (var resource in resources)
      {
        results[resource.Definition.Id] = Cancelled(resource.Definition.Id);
      }

      return Snapshot(results);
    }

    var scheduling = CreateSchedulingMetadata(resources, capabilitiesFor);
    using var globalSemaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    var providerSemaphores = scheduling.GroupLimits.ToDictionary(
        pair => pair.Key,
        pair => new SemaphoreSlim(pair.Value, pair.Value),
        StringComparer.OrdinalIgnoreCase);

    try
    {
      var remaining = resources
          .Select(resource => resource.Definition.Id)
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
      var running = new Dictionary<string, Task<CompletedExecution>>(
          StringComparer.OrdinalIgnoreCase);
      var rootFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

      while (remaining.Count > 0 || running.Count > 0)
      {
        if (cancellationToken.IsCancellationRequested)
        {
          CompleteRemainingAsCancelled(resources, remaining, results);
        }
        else
        {
          var madeProgress = BlockFailedDependents(
              resources,
              resourcesById,
              remaining,
              results,
              rootFailures);
          if (cancellationToken.IsCancellationRequested)
          {
            CompleteRemainingAsCancelled(resources, remaining, results);
          }
          else
          {
            var ready = resources
                .Where(resource => remaining.Contains(resource.Definition.Id))
                .Where(resource => DependenciesSucceeded(resource, results))
                .ToArray();
            foreach (var resource in ready)
            {
              if (cancellationToken.IsCancellationRequested)
              {
                break;
              }

              var id = resource.Definition.Id;
              results[id] = Ready(id);
              remaining.Remove(id);
              running.Add(id, ExecuteOneAsync(
                  resource,
                  executeAsync,
                  globalSemaphore,
                  providerSemaphores[scheduling.GroupsByResource[id]],
                  cancellationToken));
            }

            if (cancellationToken.IsCancellationRequested)
            {
              CompleteRemainingAsCancelled(resources, remaining, results);
            }
            else if (ready.Length == 0 && running.Count == 0 && remaining.Count > 0)
            {
              if (madeProgress)
              {
                continue;
              }

              BlockUnsatisfiedResources(resources, remaining, results, rootFailures);
              break;
            }
          }
        }

        if (running.Count == 0)
        {
          continue;
        }

        await Task.WhenAny(running.Values).ConfigureAwait(false);
        var completedIds = resources
            .Select(resource => resource.Definition.Id)
            .Where(id => running.TryGetValue(id, out var execution) && execution.IsCompleted)
            .ToArray();

        foreach (var id in completedIds)
        {
          var execution = await running[id].ConfigureAwait(false);
          running.Remove(id);
          results[id] = execution.Result;
          if (IsBlockingOutcome(execution.Result.Outcome))
          {
            rootFailures[id] = id;
          }
        }
      }

      return Snapshot(results);
    }
    finally
    {
      foreach (var semaphore in providerSemaphores.Values)
      {
        semaphore.Dispose();
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

  private static bool BlockFailedDependents(
      IReadOnlyList<PlannedResource> resources,
      IReadOnlyDictionary<string, PlannedResource> resourcesById,
      ISet<string> remaining,
      IDictionary<string, ResourceResult> results,
      IDictionary<string, string> rootFailures)
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
        results[id] = Blocked(id, rootFailure);
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

  private static void BlockUnsatisfiedResources(
      IReadOnlyList<PlannedResource> resources,
      ISet<string> remaining,
      IDictionary<string, ResourceResult> results,
      IDictionary<string, string> rootFailures)
  {
    foreach (var resource in resources)
    {
      var id = resource.Definition.Id;
      if (!remaining.Remove(id))
      {
        continue;
      }

      var dependency = resource.Dependencies.FirstOrDefault() ?? id;
      results[id] = Blocked(id, dependency);
      rootFailures[id] = dependency;
    }
  }

  private static void CompleteRemainingAsCancelled(
      IReadOnlyList<PlannedResource> resources,
      ISet<string> remaining,
      IDictionary<string, ResourceResult> results)
  {
    foreach (var resource in resources)
    {
      if (remaining.Remove(resource.Definition.Id))
      {
        results[resource.Definition.Id] = Cancelled(resource.Definition.Id);
      }
    }
  }

  private static async Task<CompletedExecution> ExecuteOneAsync(
      PlannedResource resource,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      SemaphoreSlim globalSemaphore,
      SemaphoreSlim providerSemaphore,
      CancellationToken cancellationToken)
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
      var returned = await executeAsync(resource, cancellationToken).ConfigureAwait(false);
      if (returned is null)
      {
        throw new InvalidOperationException("The resource execution delegate returned no result.");
      }

      if (returned.Outcome is not { } outcome || !Enum.IsDefined(outcome))
      {
        return new CompletedExecution(id, Failed(
            id,
            new InvalidOperationException(
                "The resource execution delegate returned an invalid outcome."),
            returned.StartedAtUtc ?? startedAt,
            returned.EndedAtUtc ?? DateTimeOffset.UtcNow,
            returned));
      }

      if (outcome == ExecutionOutcome.Succeeded && HasUnsuccessfulStepEvidence(returned))
      {
        return new CompletedExecution(id, Failed(
            id,
            new InvalidOperationException(
                "The resource execution delegate returned unsuccessful step evidence for a successful outcome."),
            returned.StartedAtUtc ?? startedAt,
            returned.EndedAtUtc ?? DateTimeOffset.UtcNow,
            returned));
      }

      var result = returned with
      {
        ResourceId = id,
        State = ExecutionState.Completed,
        StartedAtUtc = returned.StartedAtUtc ?? startedAt,
        EndedAtUtc = returned.EndedAtUtc ?? DateTimeOffset.UtcNow
      };
      return new CompletedExecution(id, result);
    }
    catch (OperationCanceledException exception)
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

  private static bool HasUnsuccessfulStepEvidence(ResourceResult result) =>
      result.StepResults.Any(step =>
          IsBlockingOutcome(step.Outcome) ||
          step.ProcessExitCode is { } exitCode && exitCode != 0);

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
}
