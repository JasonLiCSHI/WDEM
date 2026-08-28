using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.LegacySource.Services.Managers;
using Wdem.LegacySource.Services.Plugins;
using Wdem.LegacySource.Services.System;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;
using Wdem.Windows.Processes;
using Xunit;

namespace Wdem.Windows.Tests.Composition;

public sealed class WdemWindowsFactoryTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-factory-{Guid.NewGuid():N}");
  private readonly string? _originalStatePath =
      Environment.GetEnvironmentVariable("WDEM_STATE_PATH");

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
    Assert.Equal("winget", composition.Providers.GetRequired("package", "winget").ProviderName);
    Assert.True(File.Exists(Path.Combine(paths.Root, "migration-v1.json")));
  }

  public void Dispose()
  {
    Environment.SetEnvironmentVariable("WDEM_STATE_PATH", _originalStatePath);
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }
}
