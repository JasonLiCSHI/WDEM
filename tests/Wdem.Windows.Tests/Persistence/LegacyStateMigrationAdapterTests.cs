using System.Text.Json;
using Wdem.Windows.Persistence;
using Xunit;

namespace Wdem.Windows.Tests.Persistence;

public sealed class LegacyStateMigrationAdapterTests : IDisposable
{
  private readonly string _root = Path.Combine(
      Path.GetTempPath(),
      $"wdem-migration-tests-{Guid.NewGuid():N}");

  [Fact]
  public async Task MigrateAsync_ImportsOnlyStepNamesIntoClearlyLabelledMarker()
  {
    WriteLegacy("state.json", """
        {
          "applied_items": ["Git.Git"],
          "system_setting_originals": { "secret": "must-not-copy" },
          "step_history": {
            "install-git": { "stepName": "Install Git", "status": "Succeeded" },
            "configure-shell": { "status": "Failed", "errorMessage": "password=must-not-copy" }
          }
        }
        """);
    var adapter = new LegacyStateMigrationAdapter(_root);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.True(result.MigrationPerformed);
    Assert.Equal(["Git.Git", "Install Git", "configure-shell"], result.ImportedStepNames);
    using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(result.MarkerPath));
    Assert.Equal("legacy-step-name-reference", marker.RootElement.GetProperty("recordKind").GetString());
    Assert.Equal("WinHome", marker.RootElement.GetProperty("sourceProduct").GetString());
    Assert.False(marker.RootElement.TryGetProperty("compliance", out _));
    Assert.DoesNotContain("must-not-copy", marker.RootElement.GetRawText(), StringComparison.Ordinal);
    Assert.True(File.Exists(Path.Combine(_root, "WinHome", "state.json")));
  }

  [Fact]
  public async Task MigrateAsync_SupportsLegacyArrayAndStepDictionaryFormats()
  {
    WriteLegacy(".winhome-state.json", """
        {
          "step-one": { "step_id": "step-one", "step_name": "First step" },
          "step-two": { "StepId": "step-two" }
        }
        """);
    WriteLegacy("winhome.state.json", "[\"array-step\", \"token=super-secret\"]");
    var adapter = new LegacyStateMigrationAdapter(_root);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.Equal(
        ["First step", "step-two", "array-step", "token=[redacted]"],
        result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_MissingOrMalformedLegacyStateStillCompletesOnce()
  {
    WriteLegacy("state.json", "{ malformed");
    var adapter = new LegacyStateMigrationAdapter(_root);

    var first = await adapter.MigrateAsync(CancellationToken.None);
    File.WriteAllText(Path.Combine(_root, "WinHome", "state.json"), "[\"late-step\"]");
    var second = await adapter.MigrateAsync(CancellationToken.None);

    Assert.True(first.MigrationPerformed);
    Assert.Empty(first.ImportedStepNames);
    Assert.False(second.MigrationPerformed);
    Assert.Empty(second.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_ExistingMarkerNeverReadsLegacyDirectory()
  {
    var markerDirectory = Path.Combine(_root, "WDEM");
    Directory.CreateDirectory(markerDirectory);
    var markerPath = Path.Combine(markerDirectory, "migration-v1.json");
    await File.WriteAllTextAsync(markerPath, """
        { "schemaVersion": 1, "recordKind": "legacy-step-name-reference", "sourceProduct": "WinHome", "importedStepNames": [] }
        """);
    WriteLegacy("state.json", "[\"must-not-import\"]");
    var adapter = new LegacyStateMigrationAdapter(_root);

    var result = await adapter.MigrateAsync(CancellationToken.None);

    Assert.False(result.MigrationPerformed);
    Assert.Empty(result.ImportedStepNames);
  }

  [Fact]
  public async Task MigrateAsync_ConcurrentInstancesPerformMigrationOnce()
  {
    WriteLegacy("state.json", "[\"one-step\"]");
    var adapters = Enumerable.Range(0, 8)
        .Select(_ => new LegacyStateMigrationAdapter(_root))
        .ToArray();

    var results = await Task.WhenAll(
        adapters.Select(adapter => adapter.MigrateAsync(CancellationToken.None)));

    Assert.Single(results, result => result.MigrationPerformed);
    Assert.True(File.Exists(results[0].MarkerPath));
    Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "WDEM"), "*.tmp"));
  }

  [Fact]
  public async Task MigrateAsync_AtomicReplaceFailureLeavesNoValidMarker()
  {
    WriteLegacy("state.json", "[\"one-step\"]");
    var markerPath = Path.Combine(_root, "WDEM", "migration-v1.json");
    Directory.CreateDirectory(markerPath);
    var adapter = new LegacyStateMigrationAdapter(_root);

    await Assert.ThrowsAnyAsync<IOException>(() =>
        adapter.MigrateAsync(CancellationToken.None));

    Assert.False(File.Exists(markerPath));
    Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "WDEM"), "*.tmp"));
  }

  [Fact]
  public async Task MigrateAsync_DoesNotFollowReparsePointOutsideLegacyRoot()
  {
    var outside = Path.Combine(Path.GetTempPath(), $"wdem-outside-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outside);
    await File.WriteAllTextAsync(Path.Combine(outside, "state.json"), "[\"outside-step\"]");
    Directory.CreateDirectory(_root);
    try
    {
      Directory.CreateSymbolicLink(Path.Combine(_root, "WinHome"), outside);
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    {
      Directory.Delete(outside, recursive: true);
      return;
    }

    try
    {
      var result = await new LegacyStateMigrationAdapter(_root)
          .MigrateAsync(CancellationToken.None);

      Assert.Empty(result.ImportedStepNames);
    }
    finally
    {
      Directory.Delete(outside, recursive: true);
    }
  }

  private void WriteLegacy(string fileName, string contents)
  {
    var directory = Path.Combine(_root, "WinHome");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, fileName), contents);
  }

  public void Dispose()
  {
    if (Directory.Exists(_root))
    {
      Directory.Delete(_root, recursive: true);
    }
  }
}
