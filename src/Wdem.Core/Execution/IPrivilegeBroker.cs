using Wdem.Core.Providers;

namespace Wdem.Core.Execution;

public interface IPrivilegeBroker
{
  Task<ResourceApplyResult> ApplyAsync(
      ElevatedResourceRequest request,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken);
}
