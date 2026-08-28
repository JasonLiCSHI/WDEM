using System.Text.Json;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Services.System
{
  /// <summary>Manages persistent state on disk with in-memory caching, atomic writes, and backward compatibility with legacy state format.</summary>
  public class StateService : IStateService
  {
    private readonly string _stateFilePath;
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private StateData _inMemoryState;

    /// <summary>Initializes a new instance of <see cref="StateService"/>.</summary>
    public StateService(ILogger logger)
    {
      _logger = logger;

      var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var wdemDir = Path.Combine(appData, "WDEM");
      var envPath = Environment.GetEnvironmentVariable("WDEM_STATE_PATH");
      _stateFilePath = envPath ?? Path.Combine(wdemDir, ".wdem-state.json");

      var stateDirectory = Path.GetDirectoryName(_stateFilePath);
      if (!string.IsNullOrEmpty(stateDirectory) && !Directory.Exists(stateDirectory))
      {
        Directory.CreateDirectory(stateDirectory);
      }

      _inMemoryState = LoadState();
      MigrateLegacyState();
    }

    private void MigrateLegacyState()
    {
      var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var legacyDirectory = Path.Combine(appData, "WinHome");
      var oldStepPath = Path.Combine(legacyDirectory, ".winhome-state.json");
      var oldStatePath = Path.Combine(legacyDirectory, "state.json");
      var cwdStatePath = Path.Combine(Directory.GetCurrentDirectory(), "winhome.state.json");
      // Migration fallbacks are read once and moved aside; WDEM never writes these paths.
      var legacyEnvStatePath = Environment.GetEnvironmentVariable("WINHOME_STATE_PATH");

      bool oldStepExists = File.Exists(oldStepPath) && !IsCurrentStatePath(oldStepPath);
      bool oldStateExists = File.Exists(oldStatePath) && !IsCurrentStatePath(oldStatePath);
      bool oldCwdExists = File.Exists(cwdStatePath) && !IsCurrentStatePath(cwdStatePath);
      bool legacyEnvExists = !string.IsNullOrWhiteSpace(legacyEnvStatePath)
          && !IsCurrentStatePath(legacyEnvStatePath)
          && File.Exists(legacyEnvStatePath);

      if (!oldStepExists && !oldStateExists && !oldCwdExists && !legacyEnvExists) return;

      lock (_sync)
      {
        var merged = _inMemoryState;
        var backups = new List<(string Path, string Description, string Suffix)>();

        AddLegacyState(cwdStatePath, oldCwdExists, "legacy state", "backup", merged, backups);
        AddLegacyState(legacyEnvStatePath, legacyEnvExists, "migration fallback state", "migration-backup", merged, backups);
        AddLegacyState(oldStatePath, oldStateExists, "migration fallback state", "migration-backup", merged, backups);
        AddLegacySteps(oldStepPath, oldStepExists, merged, backups);

        if (backups.Count == 0) return;

        _inMemoryState = merged;
        if (!TryFlushToDisk())
        {
          _logger.LogWarning("[State] Legacy state was not backed up because the merged state could not be persisted.");
          return;
        }

        foreach (var backup in backups)
        {
          BackupMigratedFile(backup.Path, backup.Description, backup.Suffix);
        }
      }
    }

    private void AddLegacyState(
        string? path,
        bool exists,
        string description,
        string backupSuffix,
        StateData merged,
        List<(string Path, string Description, string Suffix)> backups)
    {
      if (!exists || string.IsNullOrWhiteSpace(path)) return;

      try
      {
        var legacyState = JsonSerializer.Deserialize<StateData>(File.ReadAllText(path));
        if (legacyState == null) return;

        MergeStateData(merged, legacyState);
        backups.Add((path, description, backupSuffix));
      }

      catch (Exception)
      {
        // Malformed legacy files are left in place rather than risking a backup before migration succeeds.
      }
    }

    private bool IsCurrentStatePath(string? candidatePath)
    {
      if (string.IsNullOrWhiteSpace(candidatePath)) return false;

      try
      {
        return string.Equals(
            Path.GetFullPath(candidatePath),
            Path.GetFullPath(_stateFilePath),
            StringComparison.OrdinalIgnoreCase);
      }
      catch (Exception)
      {
        return string.Equals(candidatePath, _stateFilePath, StringComparison.OrdinalIgnoreCase);
      }
    }

    private void AddLegacySteps(
        string path,
        bool exists,
        StateData merged,
        List<(string Path, string Description, string Suffix)> backups)
    {
      if (!exists) return;

      try
      {
        var legacySteps = JsonSerializer.Deserialize<Dictionary<string, StepResult>>(File.ReadAllText(path));
        if (legacySteps == null) return;

        foreach (var step in legacySteps)
        {
          merged.StepHistory.TryAdd(step.Key, step.Value);
        }

        backups.Add((path, "migration fallback step state", "backup"));
      }
      catch (Exception)
      {
        // Malformed legacy files are left in place rather than risking a backup before migration succeeds.
      }
    }

    private static void MergeStateData(StateData destination, StateData source)
    {
      foreach (var item in source.AppliedItems)
      {
        destination.AppliedItems.Add(item);
      }

      foreach (var setting in source.SystemSettingOriginals)
      {
        destination.SystemSettingOriginals.TryAdd(setting.Key, setting.Value);
      }

      foreach (var step in source.StepHistory)
      {
        destination.StepHistory.TryAdd(step.Key, step.Value);
      }
    }

    private void BackupMigratedFile(string path, string description, string suffix)
    {
      try
      {
        var backupPath = path + $".{suffix}.{Guid.NewGuid():N}";
        File.Move(path, backupPath);
        _logger.LogInfo($"[State] Read {description} from {path}, backed up to {backupPath}");
      }
      catch (Exception ex)
      {
        _logger.LogWarning($"[State] Failed to back up {description}: {ex.Message}");
      }
    }

    public StateData LoadState()
    {
      if (!File.Exists(_stateFilePath)) return new StateData();

      string json;
      try
      {
        using var stream = File.Open(_stateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        json = reader.ReadToEnd();
      }
      catch (Exception ex)
      {
        _logger.LogWarning($"[State] Could not read state file '{_stateFilePath}': {ex.Message}");
        return new StateData();
      }

      if (string.IsNullOrWhiteSpace(json))
      {
        return new StateData();
      }

      try
      {
        var stateData = JsonSerializer.Deserialize<StateData>(json);
        if (stateData != null) return stateData;
      }
      catch (JsonException)
      {
      }

      try
      {
        var legacyState = JsonSerializer.Deserialize<HashSet<string>>(json);
        if (legacyState != null)
        {
          return new StateData { AppliedItems = legacyState };
        }
      }
      catch (JsonException ex)
      {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var backupSuffix = Path.GetRandomFileName();
        var backupPath = $"{_stateFilePath}.corrupted.{timestamp}.{backupSuffix}";

        _logger.LogWarning(
            $"[State] State file '{_stateFilePath}' is corrupted: {ex.Message}. " +
            $"Backing up to '{backupPath}' and starting with empty state.");

        try
        {
          File.Move(_stateFilePath, backupPath);
        }
        catch (Exception moveEx)
        {
          _logger.LogWarning($"[State] Could not back up corrupted state file: {moveEx.Message}");
        }

        return new StateData();
      }

      return new StateData();
    }

    public void SaveState(StateData state)
    {
      lock (_sync)
      {
        _inMemoryState = new StateData
        {
          AppliedItems = new HashSet<string>(state.AppliedItems),
          SystemSettingOriginals = new Dictionary<string, object>(state.SystemSettingOriginals),
          StepHistory = new Dictionary<string, StepResult>(state.StepHistory),
        };
        FlushToDisk();
      }
    }

    public void MarkAsApplied(string item)
    {
      lock (_sync)
      {
        if (_inMemoryState.AppliedItems.Add(item))
        {
          FlushToDisk();
        }
      }
    }

    public void RemoveApplied(string item)
    {
      lock (_sync)
      {
        if (_inMemoryState.AppliedItems.Remove(item))
        {
          FlushToDisk();
        }
      }
    }

    public void TrackSystemSettingOriginal(string settingKey, object originalValue)
    {
      lock (_sync)
      {
        _inMemoryState.SystemSettingOriginals[settingKey] = originalValue;
        FlushToDisk();
      }
    }

    public void RemoveSystemSettingOriginal(string settingKey)
    {
      lock (_sync)
      {
        if (_inMemoryState.SystemSettingOriginals.Remove(settingKey))
        {
          FlushToDisk();
        }
      }
    }

    public object? GetSystemSettingOriginal(string settingKey)
    {
      lock (_sync)
      {
        return _inMemoryState.SystemSettingOriginals.TryGetValue(settingKey, out var value) ? value : null;
      }
    }

    public void RecordStep(StepResult result)
    {
      lock (_sync)
      {
        _inMemoryState.StepHistory[result.StepId] = result;
        FlushToDisk();
      }
    }

    public void RemoveStep(string stepId)
    {
      lock (_sync)
      {
        if (_inMemoryState.StepHistory.Remove(stepId))
        {
          FlushToDisk();
        }
      }
    }

    public Dictionary<string, StepResult> ListSteps()
    {
      lock (_sync)
      {
        return new Dictionary<string, StepResult>(_inMemoryState.StepHistory);
      }
    }

    private void FlushToDisk()
    {
      TryFlushToDisk();
    }

    private bool TryFlushToDisk()
    {
      try
      {
        string json = JsonSerializer.Serialize(_inMemoryState, new JsonSerializerOptions { WriteIndented = true });
        string tmpPath = _stateFilePath + ".tmp";

        using (var stream = File.Open(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
          writer.Write(json);
        }

        File.Move(tmpPath, _stateFilePath, overwrite: true);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogWarning($"[State] Could not save state: {ex.Message}");
        return false;
      }
    }

    public void BackupState(string backupPath)
    {
      try
      {
        if (File.Exists(_stateFilePath))
        {
          File.Copy(_stateFilePath, backupPath, true);
          _logger.LogSuccess($"[State] Backup created at: {backupPath}");
        }
        else
        {
          _logger.LogWarning("[State] No state file found to backup.");
        }
      }
      catch (Exception ex)
      {
        _logger.LogError($"[State] Backup failed: {ex.Message}");
      }
    }

    public void RestoreState(string backupPath)
    {
      try
      {
        if (File.Exists(backupPath))
        {
          File.Copy(backupPath, _stateFilePath, true);
          _logger.LogSuccess($"[State] State restored from: {backupPath}");
          _inMemoryState = LoadState();
        }
        else
        {
          _logger.LogError($"[State] Backup file not found: {backupPath}");
        }
      }
      catch (Exception ex)
      {
        _logger.LogError($"[State] Restore failed: {ex.Message}");
      }
    }

    public IEnumerable<string> ListItems()
    {
      lock (_sync)
      {
        return _inMemoryState.AppliedItems.ToList();
      }
    }
  }
}
