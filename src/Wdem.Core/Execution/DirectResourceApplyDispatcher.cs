using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Execution;

public sealed class DirectResourceApplyDispatcher : IResourceApplyDispatcher
{
  public async Task<ResourceApplyResult> ApplyAsync(
      IResourceProvider provider,
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);

    return await provider.ApplyAsync(resource, plan, progress, cancellationToken)
        .ConfigureAwait(false);
  }
}
