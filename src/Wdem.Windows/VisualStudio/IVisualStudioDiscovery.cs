namespace Wdem.Windows.VisualStudio;

public interface IVisualStudioDiscovery
{
  Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(
      IReadOnlyList<string> requestedWorkloads,
      IReadOnlyList<string> requestedComponents,
      CancellationToken cancellationToken);
}
