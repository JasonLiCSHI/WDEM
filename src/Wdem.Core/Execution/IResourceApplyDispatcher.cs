using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Execution;

public interface IResourceApplyDispatcher
{
  Task<ResourceApplyResult> ApplyAsync(
      Guid runId,
      IResourceProvider provider,
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken);

  Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken) =>
      Task.CompletedTask;
}
