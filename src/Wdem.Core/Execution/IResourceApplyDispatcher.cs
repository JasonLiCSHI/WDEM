using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Execution;

public interface IResourceApplyDispatcher
{
  Task<ResourceApplyResult> ApplyAsync(
      IResourceProvider provider,
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken);
}
