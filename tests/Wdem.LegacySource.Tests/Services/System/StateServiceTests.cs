using System.Text.Json;
using Moq;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Services.System;
using Wdem.LegacySource.Models;
using Xunit;

namespace Wdem.LegacySource.Tests.Services.System
{
  [Collection("StateService")]
  public class StateServiceTests : IDisposable
  {
    private readonly string _testDir;
    private readonly string _stateFilePath;
    private readonly Mock<ILogger> _mockLogger;

    private readonly string? _originalEnvPath;

    public StateServiceTests()
    {
      _originalEnvPath = Environment.GetEnvironmentVariable("WDEM_STATE_PATH");
      _testDir = Path.Combine(Path.GetTempPath(), $"Wdem.LegacySourceStateTests_{Guid.NewGuid()}");
      Directory.CreateDirectory(_testDir);
      _stateFilePath = Path.Combine(_testDir, ".wdem-state.json");
      _mockLogger = new Mock<ILogger>();
    }

    public void Dispose()
    {
      Environment.SetEnvironmentVariable("WDEM_STATE_PATH", _originalEnvPath);
      if (Directory.Exists(_testDir))
        Directory.Delete(_testDir, recursive: true);
    }

    /// <summary>Creates a StateService pointing at the test-directory state file.</summary>
    private StateService CreateService()
    {
      Environment.SetEnvironmentVariable("WDEM_STATE_PATH", _stateFilePath);
      return new StateService(_mockLogger.Object);
    }

    // ── Valid state ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadState_LegacyJson_ReturnsExpectedItems()
    {
      var expected = new HashSet<string> { "packageA", "packageB" };
      File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(expected));

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Equal(expected, state.AppliedItems);
      _mockLogger.Verify(l => l.LogWarning(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LoadState_StateDataJson_ReturnsExpectedItems()
    {
      var expectedState = new StateData
      {
        AppliedItems = new HashSet<string> { "pkg1" },
        SystemSettingOriginals = new Dictionary<string, object> { { "setting1", "val1" } }
      };
      File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(expectedState));

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Equal(expectedState.AppliedItems, state.AppliedItems);
      Assert.Equal("val1", state.SystemSettingOriginals["setting1"]?.ToString());
      _mockLogger.Verify(l => l.LogWarning(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LoadState_MissingFile_ReturnsEmptyState()
    {
      // Don't create the file at all
      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Empty(state.AppliedItems);
      _mockLogger.Verify(l => l.LogWarning(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void LegacyStateEnvironment_IsReadOnceAsMigrationFallback()
    {
      var legacyStatePath = Path.Combine(_testDir, ".winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "legacy-package" } }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        var state = CreateService().LoadState();

        Assert.Contains("legacy-package", state.AppliedItems);
        Assert.True(File.Exists(_stateFilePath));
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, ".winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void LegacyStateEnvironment_MigratesSystemSettingOriginalsWithoutAppliedItems()
    {
      var legacyStatePath = Path.Combine(_testDir, ".winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData
          {
            SystemSettingOriginals = new Dictionary<string, object> { ["HideFileExt"] = 1 }
          }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        var state = CreateService().LoadState();

        Assert.Empty(state.AppliedItems);
        Assert.Equal("1", state.SystemSettingOriginals["HideFileExt"].ToString());
        Assert.True(File.Exists(_stateFilePath));
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, ".winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void LegacyStateEnvironment_MigratesStepHistoryWithoutAppliedItems()
    {
      var legacyStatePath = Path.Combine(_testDir, ".winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData
          {
            StepHistory = new Dictionary<string, StepResult>
            {
              ["install-git"] = new() { StepId = "install-git", Status = StepStatus.Succeeded }
            }
          }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        var state = CreateService().LoadState();

        Assert.Empty(state.AppliedItems);
        Assert.Equal(StepStatus.Succeeded, state.StepHistory["install-git"].Status);
        Assert.True(File.Exists(_stateFilePath));
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, ".winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void ConfiguredStatePathMatchingLegacyName_IsNotMovedAsAMigrationBackup()
    {
      var originalDirectory = Directory.GetCurrentDirectory();
      var configuredStatePath = Path.Combine(_testDir, "winhome.state.json");
      File.WriteAllText(configuredStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "configured-package" } }));

      try
      {
        Directory.SetCurrentDirectory(_testDir);
        Environment.SetEnvironmentVariable("WDEM_STATE_PATH", configuredStatePath);

        var state = new StateService(_mockLogger.Object).LoadState();

        Assert.Contains("configured-package", state.AppliedItems);
        Assert.True(File.Exists(configuredStatePath));
      }
      finally
      {
        Directory.SetCurrentDirectory(originalDirectory);
      }
    }

    [Fact]
    public void LegacyStateEnvironment_MalformedJsonIsQuarantinedAndRecorded()
    {
      var legacyStatePath = Path.Combine(_testDir, "malformed-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, "{not-json");

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        CreateService();

        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "malformed-winhome-state.json.invalid.*"));
        Assert.Contains("legacy_migration_sources", File.ReadAllText(_stateFilePath));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void AliasedLegacyStatePaths_AreMigratedAndBackedUpOnce()
    {
      var originalDirectory = Directory.GetCurrentDirectory();
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      var legacyStatePath = Path.Combine(_testDir, "winhome.state.json");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "legacy-package" } }));

      try
      {
        Directory.SetCurrentDirectory(_testDir);
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", Path.Combine(_testDir, ".", "winhome.state.json"));

        var state = CreateService().LoadState();

        Assert.Contains("legacy-package", state.AppliedItems);
        Assert.Single(Directory.GetFiles(_testDir, "winhome.state.json.backup.*"));
        _mockLogger.Verify(l => l.LogWarning(It.Is<string>(message => message.Contains("back up"))), Times.Never);
      }
      finally
      {
        Directory.SetCurrentDirectory(originalDirectory);
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void FailedLegacyBackup_IsRetriedWithoutReplayingTheSource()
    {
      var legacyStatePath = Path.Combine(_testDir, "locked-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "first-package" } }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        using (File.Open(legacyStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
          var firstState = CreateService().LoadState();
          Assert.Contains("first-package", firstState.AppliedItems);
          Assert.True(File.Exists(legacyStatePath));
          Assert.Contains("legacy_migration_sources", File.ReadAllText(_stateFilePath));
        }

        File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
            new StateData { AppliedItems = new HashSet<string> { "first-package", "replayed-package" } }));

        var secondState = CreateService().LoadState();

        Assert.Contains("first-package", secondState.AppliedItems);
        Assert.DoesNotContain("replayed-package", secondState.AppliedItems);
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "locked-winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void SaveState_AfterFailedLegacyBackup_PreservesMigrationLedgerAndPreventsReplay()
    {
      if (!OperatingSystem.IsWindows()) return;

      var legacyStatePath = Path.Combine(_testDir, "save-after-locked-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "first-package" } }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        using (File.Open(legacyStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
          var service = CreateService();
          service.SaveState(new StateData
          {
            AppliedItems = new HashSet<string> { "first-package", "normal-save-package" },
            LegacyMigrationSources = new Dictionary<string, string>
            {
              ["caller-controlled"] = "invalid"
            }
          });

          Assert.True(File.Exists(legacyStatePath));
          var savedState = JsonSerializer.Deserialize<StateData>(File.ReadAllText(_stateFilePath));
          Assert.DoesNotContain("caller-controlled", savedState!.LegacyMigrationSources.Keys);
        }

        File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
            new StateData { AppliedItems = new HashSet<string> { "first-package", "replayed-package" } }));

        var reloadedState = CreateService().LoadState();

        Assert.Contains("normal-save-package", reloadedState.AppliedItems);
        Assert.DoesNotContain("replayed-package", reloadedState.AppliedItems);
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "save-after-locked-winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void LegacyState_CannotPreMarkAnotherMigrationSource()
    {
      var originalDirectory = Directory.GetCurrentDirectory();
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      var injectingStatePath = Path.Combine(_testDir, "winhome.state.json");
      var secondStatePath = Path.Combine(_testDir, "second-winhome-state.json");
      File.WriteAllText(injectingStatePath, JsonSerializer.Serialize(new StateData
      {
        AppliedItems = new HashSet<string> { "first-source-package" },
        LegacyMigrationSources = new Dictionary<string, string>
        {
          [Path.GetFullPath(secondStatePath)] = "migration-backup"
        }
      }));
      File.WriteAllText(secondStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "second-source-package" } }));

      try
      {
        Directory.SetCurrentDirectory(_testDir);
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", secondStatePath);

        var state = CreateService().LoadState();

        Assert.Contains("first-source-package", state.AppliedItems);
        Assert.Contains("second-source-package", state.AppliedItems);
        Assert.Single(Directory.GetFiles(_testDir, "winhome.state.json.backup.*"));
        Assert.Single(Directory.GetFiles(_testDir, "second-winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Directory.SetCurrentDirectory(originalDirectory);
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void LegacyStateEnvironment_SerializedHashSetIsMigratedAndBackedUp()
    {
      var legacyStatePath = Path.Combine(_testDir, "hashset-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new HashSet<string> { "historical-package" }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        var state = CreateService().LoadState();

        Assert.Contains("historical-package", state.AppliedItems);
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "hashset-winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void LegacyStateEnvironment_NullAppliedItemsIsQuarantinedOnceWithOriginalBytes()
    {
      var legacyStatePath = Path.Combine(_testDir, "null-items-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      var originalBytes = "{\"applied_items\":null}"u8.ToArray();
      File.WriteAllBytes(legacyStatePath, originalBytes);

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        CreateService();
        CreateService();

        Assert.False(File.Exists(legacyStatePath));
        var quarantinePath = Assert.Single(
            Directory.GetFiles(_testDir, "null-items-winhome-state.json.invalid.*"));
        Assert.Equal(originalBytes, File.ReadAllBytes(quarantinePath));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void ReloadedMigrationLedger_UsesCaseInsensitivePathLookup()
    {
      if (!OperatingSystem.IsWindows()) return;

      var legacyStatePath = Path.Combine(_testDir, "case-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(new StateData
      {
        AppliedItems = new HashSet<string> { "already-imported-package" },
        LegacyMigrationSources = new Dictionary<string, string>
        {
          [Path.GetFullPath(legacyStatePath).ToLowerInvariant()] = "migration-backup"
        }
      }));
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "replayed-package" } }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath.ToUpperInvariant());

        var state = CreateService().LoadState();

        Assert.Contains("already-imported-package", state.AppliedItems);
        Assert.DoesNotContain("replayed-package", state.AppliedItems);
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "case-winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void FailedMigrationFlush_RollsBackInMemoryStateBeforeEngineSaveAndRestart()
    {
      var legacyStatePath = Path.Combine(_testDir, "flush-failure-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(new StateData
      {
        AppliedItems = new HashSet<string> { "legacy-package" },
        SystemSettingOriginals = new Dictionary<string, object> { ["legacy-setting"] = 7 },
        StepHistory = new Dictionary<string, StepResult>
        {
          ["legacy-step"] = new() { StepId = "legacy-step", Status = StepStatus.Succeeded }
        }
      }));
      Directory.CreateDirectory(_stateFilePath);

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        var serviceWithFailedMigration = CreateService();
        Assert.True(File.Exists(legacyStatePath));

        Directory.Delete(_stateFilePath);
        var engineState = serviceWithFailedMigration.LoadState();
        engineState.AppliedItems.Add("engine-package");
        serviceWithFailedMigration.SaveState(engineState);

        var migratedState = CreateService().LoadState();

        Assert.Contains("engine-package", migratedState.AppliedItems);
        Assert.Contains("legacy-package", migratedState.AppliedItems);
        Assert.Equal("7", migratedState.SystemSettingOriginals["legacy-setting"].ToString());
        Assert.Equal(StepStatus.Succeeded, migratedState.StepHistory["legacy-step"].Status);
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "flush-failure-winhome-state.json.migration-backup.*"));

        File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
            new StateData { AppliedItems = new HashSet<string> { "replayed-package" } }));

        var restartedState = CreateService().LoadState();

        Assert.Contains("legacy-package", restartedState.AppliedItems);
        Assert.DoesNotContain("replayed-package", restartedState.AppliedItems);
        Assert.Equal(2, Directory.GetFiles(_testDir, "flush-failure-winhome-state.json.migration-backup.*").Length);
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    // ── Corrupted JSON ─────────────────────────────────────────────────────────

    [Fact]
    public void LoadState_CorruptedJson_ReturnsEmptyState()
    {
      File.WriteAllText(_stateFilePath, "{this is not valid json");

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Empty(state.AppliedItems);
    }

    [Fact]
    public void LoadState_TruncatedJson_ReturnsEmptyState()
    {
      // Simulates a partial write: the array was started but never finished
      File.WriteAllText(_stateFilePath, "[\"packageA\",");

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Empty(state.AppliedItems);
    }

    [Fact]
    public void LoadState_EmptyFile_ReturnsEmptyState()
    {
      File.WriteAllText(_stateFilePath, string.Empty);

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Empty(state.AppliedItems);
    }

    [Fact]
    public void LoadState_WrongJsonType_ReturnsEmptyState()
    {
      // A valid JSON primitive instead of an object/array — wrong type, will fail both format deserializations and trigger corruption backup
      File.WriteAllText(_stateFilePath, "\"a string, not an object\"");

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Empty(state.AppliedItems);
    }

    // ── Corruption backup ──────────────────────────────────────────────────────

    [Fact]
    public void LoadState_CorruptedJson_CreatesBackupFile()
    {
      File.WriteAllText(_stateFilePath, "CORRUPTED DATA !!!!");

      var svc = CreateService();
      svc.LoadState();

      // The original file should have been renamed to a .corrupted.<timestamp> file
      var backups = Directory.GetFiles(_testDir, ".wdem-state.json.corrupted.*");
      Assert.Single(backups);
    }

    [Fact]
    public void LoadState_BackupFails_LogsWarningAndReturnsEmpty()
    {
      if (!OperatingSystem.IsWindows()) return;

      File.WriteAllText(_stateFilePath, "{ invalid json");

      // Lock the file to force File.Move to throw an exception
      using var lockStream = new FileStream(_stateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Empty(state.AppliedItems);
      _mockLogger.Verify(l => l.LogWarning(It.Is<string>(s => s.Contains("Could not back up corrupted state file"))), Times.AtLeastOnce);
    }

    [Fact]
    public void LoadState_CorruptedJson_LogsWarningWithPath()
    {
      File.WriteAllText(_stateFilePath, "{{BAD}}");

      var svc = CreateService();
      svc.LoadState();

      _mockLogger.Verify(l =>
          l.LogWarning(It.Is<string>(msg =>
              msg.Contains("[State]") &&
              msg.Contains("corrupted") &&
              msg.Contains(".wdem-state.json"))),
          Times.AtLeastOnce);
    }

    [Fact]
    public void LoadState_CorruptedJson_OriginalFileRemovedAfterBackup()
    {
      File.WriteAllText(_stateFilePath, "CORRUPTED DATA !!!!");

      var svc = CreateService();
      svc.LoadState();

      // The original file should no longer exist (it was moved to .corrupted.*)
      Assert.False(File.Exists(_stateFilePath));
    }

    // ── Save & MarkAsApplied ───────────────────────────────────────────────────

    [Fact]
    public void MarkAsApplied_PersistsItemToDisk()
    {
      var svc = CreateService();
      svc.MarkAsApplied("myPackage");

      Assert.True(File.Exists(_stateFilePath));
      var written = JsonSerializer.Deserialize<StateData>(File.ReadAllText(_stateFilePath));
      Assert.Contains("myPackage", written?.AppliedItems!);
    }

    [Fact]
    public void RemoveApplied_RemovesItemFromDisk()
    {
      var svc = CreateService();
      svc.MarkAsApplied("oldPackage");

      svc.RemoveApplied("oldPackage");

      var written = JsonSerializer.Deserialize<StateData>(File.ReadAllText(_stateFilePath));
      Assert.DoesNotContain("oldPackage", written?.AppliedItems!);
    }

    [Fact]
    public void SaveState_WritesAllItemsToDisk()
    {
      var items = new HashSet<string> { "a", "b", "c" };
      var svc = CreateService();
      svc.SaveState(new StateData { AppliedItems = items });

      var written = JsonSerializer.Deserialize<StateData>(File.ReadAllText(_stateFilePath));
      Assert.Equal(items, written?.AppliedItems);
    }

    [Fact]
    public void RestoreState_CraftedLedgerCannotPreMarkAnotherMigrationSource()
    {
      var backupPath = Path.Combine(_testDir, "crafted-backup.json");
      var legacyStatePath = Path.Combine(_testDir, "restore-target-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(backupPath, JsonSerializer.Serialize(new StateData
      {
        AppliedItems = new HashSet<string> { "restored-package" },
        LegacyMigrationSources = new Dictionary<string, string>
        {
          [Path.GetFullPath(legacyStatePath)] = "migration-backup"
        }
      }));
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "legacy-source-package" } }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", null);
        var service = CreateService();
        service.RestoreState(backupPath);

        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);
        var restartedState = CreateService().LoadState();

        Assert.Contains("restored-package", restartedState.AppliedItems);
        Assert.Contains("legacy-source-package", restartedState.AppliedItems);
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "restore-target-winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    [Fact]
    public void RestoreState_OldBackupCannotEraseExistingMigrationDisposition()
    {
      if (!OperatingSystem.IsWindows()) return;

      var backupPath = Path.Combine(_testDir, "old-backup.json");
      var legacyStatePath = Path.Combine(_testDir, "restore-locked-winhome-state.json");
      var originalLegacyPath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");
      File.WriteAllText(backupPath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "restored-package" } }));
      File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(
          new StateData { AppliedItems = new HashSet<string> { "initial-legacy-package" } }));

      try
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", legacyStatePath);

        StateService service;
        using (File.Open(legacyStatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
          service = CreateService();
          Assert.True(File.Exists(legacyStatePath));
        }

        service.RestoreState(backupPath);
        var restoredOnDisk = JsonSerializer.Deserialize<StateData>(File.ReadAllText(_stateFilePath));
        Assert.Contains(Path.GetFullPath(legacyStatePath), restoredOnDisk!.LegacyMigrationSources.Keys);

        File.WriteAllText(legacyStatePath, JsonSerializer.Serialize(new StateData
        {
          AppliedItems = new HashSet<string> { "initial-legacy-package", "replayed-package" }
        }));

        var restartedState = CreateService().LoadState();

        Assert.Contains("restored-package", restartedState.AppliedItems);
        Assert.DoesNotContain("replayed-package", restartedState.AppliedItems);
        Assert.False(File.Exists(legacyStatePath));
        Assert.Single(Directory.GetFiles(_testDir, "restore-locked-winhome-state.json.migration-backup.*"));
      }
      finally
      {
        Environment.SetEnvironmentVariable("WINHOME_STATE_PATH", originalLegacyPath);
      }
    }

    // ── Partial-write / incomplete JSON ───────────────────────────────────────

    [Fact]
    public void LoadState_PartialWrite_TreatedAsCorruption_AndBackupCreated()
    {
      // Simulate a partial write (e.g. process crashed mid-flush)
      File.WriteAllText(_stateFilePath, "[\"item1\", \"item2\", \"item3");

      var svc = CreateService();
      var state = svc.LoadState();

      Assert.Empty(state.AppliedItems);
      var backups = Directory.GetFiles(_testDir, ".wdem-state.json.corrupted.*");
      Assert.Single(backups);
    }

    // ── Round-trip: recover then continue working ─────────────────────────────

    [Fact]
    public void AfterCorruption_CanSaveAndLoadNewState()
    {
      File.WriteAllText(_stateFilePath, "NOT JSON AT ALL");

      var svc = CreateService();
      svc.LoadState(); // triggers recovery

      // Now add an item — should write a fresh valid file
      svc.MarkAsApplied("newItem");

      // Re-create the service to force a fresh load from disk
      var svc2 = CreateService();
      var state = svc2.LoadState();

      Assert.Contains("newItem", state.AppliedItems);
    }
  }
}
