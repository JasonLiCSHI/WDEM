using Wdem.Core.Compliance;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Providers;
using Wdem.LegacySource.Services.Bootstrappers;
using Wdem.LegacySource.Services.Logging;
using Wdem.LegacySource.Services.Managers;
using Wdem.LegacySource.Services.System;
using Wdem.Windows.Processes;
using Wdem.Windows.Providers;
using Wdem.Windows.VisualStudio;

namespace Wdem.Windows.Composition;

internal sealed record WindowsProviderComposition(
    ConsoleLogger Logger,
    IProcessRunner LegacyProcessRunner,
    IProcessExecutor ProcessExecutor,
    RuntimeResolver RuntimeResolver,
    WingetService Winget,
    ComplianceEvaluator ComplianceEvaluator,
    IResourceProviderRegistry Providers);

internal static class WindowsProviderCompositionFactory
{
  public static WindowsProviderComposition Create(string? logFilePath)
  {
    var logger = new ConsoleLogger(logFilePath);
    var processRunner = new DefaultProcessRunner();
    var processExecutor = new LegacySourceProcessExecutorAdapter(processRunner);
    var runtimeResolver = new RuntimeResolver(
        logger,
        processRunner,
        new DefaultFileSystem());
    var winget = new WingetService(
        processRunner,
        new WingetBootstrapper(processRunner, logger),
        logger,
        runtimeResolver);
    var complianceEvaluator = new ComplianceEvaluator();
    var winGetCommandClient = new WinGetCommandClient(processExecutor);
    var providers = new ResourceProviderRegistry(
    [
      new LegacyPackageManagerProviderAdapter("winget", winget, supportsSource: true),
      new WinGetPackageProvider(winGetCommandClient, complianceEvaluator),
      new GitProvider(processExecutor, winGetCommandClient, complianceEvaluator),
      new DotNetSdkProvider(processExecutor, winGetCommandClient, complianceEvaluator),
      new VisualStudioProvider(
          new VsWhereVisualStudioDiscovery(processExecutor),
          complianceEvaluator)
    ]);

    return new WindowsProviderComposition(
        logger,
        processRunner,
        processExecutor,
        runtimeResolver,
        winget,
        complianceEvaluator,
        providers);
  }
}
