using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Processes;
using Wdem.Core.Profiles;
using Wdem.Core.Providers;
using Wdem.Core.Runs;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Services.Bootstrappers;
using Wdem.LegacySource.Services.Managers;
using Wdem.LegacySource.Services.Plugins;
using Wdem.LegacySource.Services.System;
using Wdem.Windows.Execution;
using Wdem.Windows.Persistence;
using Wdem.Windows.Security;

namespace Wdem.Windows.Composition;

public sealed record WdemWindowsComposition(
    IEnvironmentRunService EnvironmentRuns,
    IProfileCatalog Profiles,
    IResourceProviderRegistry Providers,
    IExecutionRunStore RunStore,
    IPrivilegeBroker PrivilegeBroker,
    LogRedactor Redactor,
    IRunEventSink RunEvents,
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
      CancellationToken cancellationToken = default,
      LogRedactor? redactor = null,
      IRunEventSink? runEvents = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(profilesDirectory);
    paths ??= new WdemDataPaths();

    var localApplicationData = Path.GetDirectoryName(paths.Root)
        ?? throw new ArgumentException(
            "The WDEM data root must have a parent directory.",
            nameof(paths));
    var migration = new LegacyStateMigrationAdapter(localApplicationData);
    await migration.MigrateAsync(cancellationToken).ConfigureAwait(false);

    var fullProfilesDirectory = Path.GetFullPath(profilesDirectory);
    var applicationRoot = Path.GetDirectoryName(fullProfilesDirectory)
        ?? throw new ArgumentException(
            "The profiles directory must have a parent directory.",
            nameof(profilesDirectory));

    var providerComposition = WindowsProviderCompositionFactory.Create(
        Path.Combine(paths.Root, "wdem.log"),
        applicationRoot,
        fullProfilesDirectory);
    var logger = providerComposition.Logger;
    var processRunner = providerComposition.LegacyProcessRunner;
    var processExecutor = providerComposition.ProcessExecutor;
    var runtimeResolver = providerComposition.RuntimeResolver;
    var winget = providerComposition.Winget;
    var complianceEvaluator = providerComposition.ComplianceEvaluator;
    var providerRegistry = providerComposition.Providers;

    var pluginManager = new PluginManager(
        new UvBootstrapper(processRunner),
        new BunBootstrapper(processRunner),
        logger,
        Path.Combine(paths.Root, "plugins"),
        runtimeResolver);
    var pluginRunner = new PluginRunner(logger, runtimeResolver);
    var state = new StateService(
        logger,
        Path.Combine(paths.Root, ".wdem-state.json"),
        migrateLegacy: false);
    redactor ??= new LogRedactor();
    runEvents ??= new RunEventHub();
    var runStore = new JsonExecutionRunStore(paths, redactor);
    var profiles = new DirectoryProfileCatalog(fullProfilesDirectory, providerRegistry);
    var privilegeBroker = new NamedPipePrivilegeBroker(new ElevatedHostLauncher(
        Path.Combine(AppContext.BaseDirectory, "Wdem.ElevatedHost.exe"),
        localApplicationData));
    var environmentRuns = new EnvironmentRunService(
        profiles,
        new ResourceGraphBuilder(),
        providerRegistry,
        complianceEvaluator,
        new ExecutionPlanner(providerRegistry, complianceEvaluator),
        new ResourceScheduler(),
        runStore,
        new PrivilegeAwareResourceApplyDispatcher(
            new DirectResourceApplyDispatcher(),
            privilegeBroker),
        timeProvider: null,
        runEvents,
        redactor);

    return new WdemWindowsComposition(
        environmentRuns,
        profiles,
        providerRegistry,
        runStore,
        privilegeBroker,
        redactor,
        runEvents,
        processExecutor,
        processRunner,
        winget,
        pluginManager,
        pluginRunner,
        state,
        migration);
  }
}
