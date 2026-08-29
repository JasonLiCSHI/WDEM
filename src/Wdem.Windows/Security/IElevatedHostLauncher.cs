using Wdem.Core.Execution;
using Wdem.Core.Providers;

namespace Wdem.Windows.Security;

public interface IElevatedHostLauncher
{
  Task<IElevatedHostSession> StartAsync(
      Guid runId,
      string pipeName,
      CancellationToken cancellationToken);
}

public interface IElevatedHostSession : IAsyncDisposable
{
  Task<ResourceApplyResult> ApplyAsync(
      ElevatedResourceRequest request,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken);

  Task TerminateAsync(CancellationToken cancellationToken);
}
