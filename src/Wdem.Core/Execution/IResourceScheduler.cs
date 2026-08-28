using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public interface IResourceScheduler
{
  Task<SchedulerResult> ExecuteAsync(
      ExecutionPlan plan,
      Func<PlannedResource, CancellationToken, Task<ResourceResult>> executeAsync,
      Func<PlannedResource, ProviderCapabilities> capabilitiesFor,
      int maximumConcurrency,
      CancellationToken cancellationToken,
      Func<ResourceResult, Task>? transitionAsync = null);
}
