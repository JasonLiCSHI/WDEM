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

      bool oldStepExists = File.Exists(oldStepPath);
      bool oldStateExists = File.Exists(oldStatePath);
      bool oldCwdExists = File.Exists(cwdStatePath);
      bool legacyEnvExists = !string.IsNullOrWhiteSpace(legacyEnvStatePath)
          && !string.Equals(legacyEnvStatePath, _stateFilePath, StringComparison.OrdinalIgnoreCase)
          && File.Exists(legacyEnvStatePath);

      if (!oldStepExists && !oldStateExists && !oldCwdExists && !legacyEnvExists) return;

      lock (_sync)
      {
        var merged = _inMemoryState;
        bool changed = false;

        if (oldCwdExists)
        {
          try
          {
            var oldCwdJson = File.ReadAllText(cwdStatePath);
            var oldState = JsonSerializer.Deserialize<StateData>(oldCwdJson);
            if (oldState != null && oldState.AppliedItems.Any())
            {
              foreach (var item in oldState.AppliedItems)
                merged.AppliedItems.Add(item);
              foreach (var kv in oldState.SystemSettingOriginals)
                merged.SystemSettingOriginals.TryAdd(kv.Key, kv.Value);
              changed = true;
            }
          }
          catch (Exception)
          {
            /* Silently skip malformed structural nodes */
          }

          try
          {
            var backupPath = cwdStatePath + $".backup.{Guid.NewGuid():N}";
            File.Move(cwdStatePath, backupPath);
            _logger.LogInfo($"[State] Read migration fallback state from {cwdStatePath}, backed up to {backupPath}");
          }
          catch (Exception ex)
          {
            _logger.LogWarning($"[State] Failed to back up legacy state: {ex.Message}");
          }
        }

        if (legacyEnvExists)
        {
          try
          {
            var legacyEnvJson = File.ReadAllText(legacyEnvStatePath!);
            var legacyState = JsonSerializer.Deserialize<StateData>(legacyEnvJson);
            if (legacyState != null && legacyState.AppliedItems.Any())
            {
              foreach (var item in legacyState.AppliedItems)
                merged.AppliedItems.Add(item);
              foreach (var kv in legacyState.SystemSettingOriginals)
                merged.SystemSettingOriginals.TryAdd(kv.Key, kv.Value);
              changed = true;
            }
          }
          catch (Exception)
          {
            /* Silently skip malformed migration fallback state. */
          }

          try
          {
            var backupPath = legacyEnvStatePath + $".migration-backup.{Guid.NewGuid():N}";
            File.Move(legacyEnvStatePath!, backupPath);
            _logger.LogInfo($"[State] Read migration fallback state from {legacyEnvStatePath}, backed up to {backupPath}");
          }
          catch (Exception ex)
          {
            _logger.LogWarning($"[State] Failed to back up migration fallback state: {ex.Message}");
          }
        }

        if (oldStateExists)
        {
          try
          {
            var oldStateJson = File.ReadAllText(oldStatePath);
            var oldState = JsonSerializer.Deserialize<StateData>(oldStateJson);
            if (oldState != null && oldState.AppliedItems.Any())
            {
              foreach (var item in oldState.AppliedItems)
                merged.AppliedItems.Add(item);
              foreach (var kv in oldState.SystemSettingOriginals)
                merged.SystemSettingOriginals.TryAdd(kv.Key, kv.Value);
              changed = true;
            }
          }
          catch (Exception)
          {
            /* Silently skip malformed migration fallback state. */
          }

          try
          {
            var backupPath = oldStatePath + $".migration-backup.{Guid.NewGuid():N}";
            File.Move(oldStatePath, backupPath);
            _logger.LogInfo($"[State] Read migration fallback state from {oldStatePath}, backed up to {backupPath}");
          }
          catch (Exception ex)
          {
            _logger.LogWarning($"[State] Failed to back up migration fallback state: {ex.Message}");
          }
        }

        if (oldStepExists)
        {
          try
          {
            var oldStepJson = File.ReadAllText(oldStepPath);
            var oldSteps = JsonSerializer.Deserialize<Dictionary<string, StepResult>>(oldStepJson);
            if (oldSteps != null)
            {
              foreach (var kv in oldSteps)
                merged.StepHistory.TryAdd(kv.Key, kv.Value);
              changed = true;
            }
          }
          catch (Exception)
          {
            /* Silently skip malformed structural nodes */
          }

          try
          {
            var backupPath = oldStepPath + $".backup.{Guid.NewGuid():N}";
            File.Move(oldStepPath, backupPath);
            _logger.LogInfo($"[State] Read migration fallback step state from {oldStepPath}, backed up to {backupPath}");
          }
          catch (Exception ex)
          {
            _logger.LogWarning($"[State] Failed to back up legacy step state: {ex.Message}");
          }
        }

        if (changed)
        {
          _inMemoryState = merged;
          FlushToDisk();
        }
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
      }
      catch (Exception ex)
      {
        _logger.LogWarning($"[State] Could not save state: {ex.Message}");
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
