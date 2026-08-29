using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Runs;
using Wdem.LegacySource.Services.Managers;
using Wdem.LegacySource.Services.Plugins;
using Wdem.LegacySource.Services.System;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
using Wdem.Windows.Processes;
using Wdem.Windows.Providers;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Composition;

public sealed class WdemWindowsFactoryTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-factory-{Guid.NewGuid():N}");
  private readonly string? _originalStatePath =
      Environment.GetEnvironmentVariable("WDEM_STATE_PATH");
  private readonly string? _originalLegacyStatePath =
      Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");

  [Fact]
  public async Task CreateAsync_ComposesAndReusesWindowsTransitionServices()
  {
    var profilesDirectory = Path.Combine(_root, "profiles");
    Directory.CreateDirectory(profilesDirectory);
    Environment.SetEnvironmentVariable(
        "WDEM_STATE_PATH",
        Path.Combine(_root, "legacy-state.json"));
    var paths = new WdemDataPaths(_root);

    var composition = await WdemWindowsFactory.CreateAsync(
        profilesDirectory,
        paths,
        CancellationToken.None);

    Assert.IsType<EnvironmentRunService>(composition.EnvironmentRuns);
    Assert.IsType<DefaultProcessRunner>(composition.LegacyProcessRunner);
    Assert.IsType<LegacySourceProcessExecutorAdapter>(composition.ProcessExecutor);
    Assert.IsType<WingetService>(composition.Winget);
    Assert.IsType<PluginManager>(composition.LegacyPluginManager);
    Assert.IsType<PluginRunner>(composition.LegacyPluginRunner);
    Assert.IsType<StateService>(composition.LegacyState);
    Assert.IsType<JsonExecutionRunStore>(composition.RunStore);
    Assert.IsType<LogRedactor>(composition.Redactor);
    Assert.IsType<RunEventHub>(composition.RunEvents);
    Assert.IsType<NamedPipePrivilegeBroker>(composition.PrivilegeBroker);
    Assert.Equal("winget", composition.Providers.GetRequired("package", "winget").ProviderName);
    Assert.True(File.Exists(Path.Combine(paths.Root, "migration-v1.json")));
  }

  [Fact]
  public async Task CreateAsync_UsesOnlyReadOnlyMarkerMigrationAndIsolatedWdemState()
  {
    var profilesDirectory = Path.Combine(_root, "profiles");
    var legacyDirectory = Path.Combine(_root, "WinHome");
    Directory.CreateDirectory(profilesDirectory);
    Directory.CreateDirectory(legacyDirectory);
    var legacyStatePath = Path.Combine(legacyDirectory, "state.json");
    var legacyContents = """
        {
          "applied_items": ["legacy-success"],
          "step_history": {
            "legacy-step": { "stepName": "Legacy Step", "status": "Succeeded" }
          }
        }
        """;
    await File.WriteAllTextAsync(legacyStatePath, legacyContents);
    var externalStatePath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-forbidden-state-{Guid.NewGuid():N}.json");
    Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);
    Environment.SetEnvironmentVariable("WDEM_STATE_PATH", externalStatePath);
    var paths = new WdemDataPaths(_root);

    var first = await WdemWindowsFactory.CreateAsync(
        profilesDirectory,
        paths,
        CancellationToken.None);
    var firstMarker = await File.ReadAllTextAsync(
        Path.Combine(paths.Root, "migration-v1.json"));
    var second = await WdemWindowsFactory.CreateAsync(
        profilesDirectory,
        paths,
        CancellationToken.None);
    var secondMarker = await File.ReadAllTextAsync(
        Path.Combine(paths.Root, "migration-v1.json"));

    Assert.True(File.Exists(legacyStatePath));
    Assert.Equal(legacyContents, await File.ReadAllTextAsync(legacyStatePath));
    Assert.Empty(first.LegacyState.ListItems());
    Assert.Empty(first.LegacyState.ListSteps());
    Assert.Empty(second.LegacyState.ListItems());
    Assert.Empty(second.LegacyState.ListSteps());
    Assert.Equal(firstMarker, secondMarker);
    Assert.False(File.Exists(externalStatePath));
  }

  [Fact]
  public void CreateElevatedHost_DoesNotRunMigrationOrCreateCurrentUserState()
  {
    Directory.CreateDirectory(_root);
    var legacyStatePath = Path.Combine(_root, "legacy-state.json");
    File.WriteAllText(legacyStatePath, "{}");
    Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);
    var paths = new WdemDataPaths(_root);

    var composition = WdemElevatedHostFactory.Create(paths);

    Assert.IsType<JsonExecutionRunStore>(composition.RunStore);
    Assert.IsType<LogRedactor>(composition.Redactor);
    Assert.Equal("winget", composition.Providers
        .GetRequired("winget-package", "winget").ProviderName);
    var visualStudio = Assert.IsType<VisualStudioProvider>(composition.Providers
        .GetRequired("visual-studio", "visual-studio"));
    Assert.True(visualStudio.Capabilities.SupportsInstallerParameters);
    Assert.False(Directory.Exists(paths.Root));
    Assert.False(File.Exists(Path.Combine(paths.Root, "migration-v1.json")));
    Assert.False(File.Exists(Path.Combine(paths.Root, ".wdem-state.json")));
  }

  public void Dispose()
  {
    Environment.SetEnvironmentVariable("WDEM_STATE_PATH", _originalStatePath);
    Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", _originalLegacyStatePath);
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }
}
