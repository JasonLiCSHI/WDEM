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
      LogRedactor? redactor = null)
  {
    ArgumentNullException.ThrowIfNull(paths);
    redactor ??= new LogRedactor();
    var providers = WindowsProviderCompositionFactory.Create(logFilePath: null).Providers;

    return new WdemElevatedHostComposition(
        new JsonExecutionRunStore(paths, redactor),
        providers,
        redactor);
  }
}
