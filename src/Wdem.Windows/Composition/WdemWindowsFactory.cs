using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Processes;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Runs;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Providers;
using Wdem.LegacySource.Services.Bootstrappers;
using Wdem.LegacySource.Services.Logging;
using Wdem.LegacySource.Services.Managers;
using Wdem.LegacySource.Services.Plugins;
using Wdem.LegacySource.Services.System;
using Wdem.Windows.Persistence;
using Wdem.Windows.Processes;

namespace Wdem.Windows.Composition;

public sealed record WdemWindowsComposition(
    IEnvironmentRunService EnvironmentRuns,
    IProfileCatalog Profiles,
    IResourceProviderRegistry Providers,
    IExecutionRunStore RunStore,
    IProcessExecutor ProcessExecutor,
    IProcessRunner LegacyProcessRunner,
    WingetService Winget,
    IPluginManager LegacyPluginManager,
    IPluginRunner LegacyPluginRunner,
    IStateService LegacyState,
    LegacyStateMigrationAdapter LegacyMigration);

public static class WdemWindowsFactory
{
  public static async Task<WdemWindowsComposition> CreateAsync(
      string profilesDirectory,
      WdemDataPaths? paths = null,
      CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(profilesDirectory);
    paths ??= new WdemDataPaths();

    var localApplicationData = Path.GetDirectoryName(paths.Root)
        ?? throw new ArgumentException(
            "The WDEM data root must have a parent directory.",
            nameof(paths));
    var migration = new LegacyStateMigrationAdapter(localApplicationData);
    await migration.MigrateAsync(cancellationToken).ConfigureAwait(false);

    var logger = new ConsoleLogger(Path.Combine(paths.Root, "wdem.log"));
    var processRunner = new DefaultProcessRunner();
    var processExecutor = new LegacySourceProcessExecutorAdapter(processRunner);
    var fileSystem = new DefaultFileSystem();
    var runtimeResolver = new RuntimeResolver(logger, processRunner, fileSystem);

    var winget = new WingetService(
        processRunner,
        new WingetBootstrapper(processRunner, logger),
        logger,
        runtimeResolver);
    var providerRegistry = new ResourceProviderRegistry(
    [
      new LegacyPackageManagerProviderAdapter("winget", winget, supportsSource: true)
    ]);

    var pluginManager = new PluginManager(
        new UvBootstrapper(processRunner),
        new BunBootstrapper(processRunner),
        logger,
        Path.Combine(paths.Root, "plugins"),
        runtimeResolver);
    var pluginRunner = new PluginRunner(logger, runtimeResolver);
    var state = new StateService(logger);
    var runStore = new JsonExecutionRunStore(paths, new LogRedactor());
    var profiles = new DirectoryProfileCatalog(profilesDirectory, providerRegistry);
    var complianceEvaluator = new ComplianceEvaluator();
    var environmentRuns = new EnvironmentRunService(
        profiles,
        new ResourceGraphBuilder(),
        providerRegistry,
        complianceEvaluator,
        new ExecutionPlanner(providerRegistry, complianceEvaluator),
        new ResourceScheduler(),
        runStore,
        new DirectResourceApplyDispatcher());

    return new WdemWindowsComposition(
        environmentRuns,
        profiles,
        providerRegistry,
        runStore,
        processExecutor,
        processRunner,
        winget,
        pluginManager,
        pluginRunner,
        state,
        migration);
  }
}
