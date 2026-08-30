using Wdem.Core.Providers;
using Wdem.Core.Runs;
using Wdem.Windows.Persistence;

namespace Wdem.Windows.Composition;

public sealed record WdemElevatedHostComposition(
    IExecutionRunStore RunStore,
    IResourceProviderRegistry Providers,
    LogRedactor Redactor);

public static class WdemElevatedHostFactory
{
  public static WdemElevatedHostComposition Create(
      WdemDataPaths paths,
      string applicationRoot,
      LogRedactor? redactor = null)
  {
    ArgumentNullException.ThrowIfNull(paths);
    ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
    redactor ??= new LogRedactor();
    var providers = WindowsProviderCompositionFactory.Create(
        logFilePath: null,
        Path.GetFullPath(applicationRoot)).Providers;

    return new WdemElevatedHostComposition(
        new JsonExecutionRunStore(paths, redactor),
        providers,
        redactor);
  }
}
