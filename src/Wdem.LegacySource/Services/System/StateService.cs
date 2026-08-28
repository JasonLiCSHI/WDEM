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

    private enum LegacyStateFormat
    {
      StateData,
      StepHistory
    }

    private sealed record LegacyStateCandidate(
        string Path,
        string Description,
        string BackupSuffix,
        LegacyStateFormat Format);

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

      var candidates = new Dictionary<string, LegacyStateCandidate>(StringComparer.OrdinalIgnoreCase);
      AddCandidate(candidates, cwdStatePath, "legacy state", "backup", LegacyStateFormat.StateData);
      AddCandidate(candidates, oldStatePath, "migration fallback state", "migration-backup", LegacyStateFormat.StateData);
      AddCandidate(candidates, oldStepPath, "migration fallback step state", "backup", LegacyStateFormat.StepHistory);
      AddCandidate(candidates, legacyEnvStatePath, "migration fallback state", "migration-backup", LegacyStateFormat.StateData);

      if (candidates.Count == 0) return;

      lock (_sync)
      {
        var stateBeforeMigration = CloneStateData(_inMemoryState);
        var pendingMoves = new Dictionary<string, (string Description, string Suffix)>(StringComparer.OrdinalIgnoreCase);
        bool stateChanged = false;

        foreach (var candidate in candidates.Values.Where(candidate => File.Exists(candidate.Path)))
        {
          if (_inMemoryState.LegacyMigrationSources.TryGetValue(candidate.Path, out var recordedSuffix))
          {
            pendingMoves[candidate.Path] = (candidate.Description, recordedSuffix);
            continue;
          }

          try
          {
            if (candidate.Format == LegacyStateFormat.StateData)
            {
              var legacyState = DeserializeLegacyStateData(File.ReadAllText(candidate.Path));
              MergeStateData(_inMemoryState, legacyState);
            }
            else
            {
              var legacySteps = JsonSerializer.Deserialize<Dictionary<string, StepResult>>(File.ReadAllText(candidate.Path))
                  ?? throw new JsonException("Legacy step history contained JSON null.");
              foreach (var step in legacySteps) _inMemoryState.StepHistory.TryAdd(step.Key, step.Value);
            }

            _inMemoryState.LegacyMigrationSources[candidate.Path] = candidate.BackupSuffix;
            pendingMoves[candidate.Path] = (candidate.Description, candidate.BackupSuffix);
            stateChanged = true;
          }
          catch (JsonException ex)
          {
            const string invalidSuffix = "invalid";
            _logger.LogWarning($"[State] Legacy state '{candidate.Path}' is invalid and will be quarantined: {ex.Message}");
            _inMemoryState.LegacyMigrationSources[candidate.Path] = invalidSuffix;
            pendingMoves[candidate.Path] = ("invalid legacy state", invalidSuffix);
            stateChanged = true;
          }
          catch (Exception ex)
          {
            _logger.LogWarning($"[State] Could not read legacy state '{candidate.Path}': {ex.Message}");
          }
        }

        if (stateChanged && !TryFlushToDisk())
        {
          _inMemoryState = stateBeforeMigration;
          _logger.LogWarning("[State] Legacy state was not moved because the migration result could not be persisted.");
          return;
        }

        foreach (var pendingMove in pendingMoves)
        {
          BackupMigratedFile(pendingMove.Key, pendingMove.Value.Description, pendingMove.Value.Suffix);
        }
      }
    }

    private static StateData CloneStateData(StateData state)
    {
      return new StateData
      {
        AppliedItems = new HashSet<string>(state.AppliedItems),
        SystemSettingOriginals = new Dictionary<string, object>(state.SystemSettingOriginals),
        StepHistory = new Dictionary<string, StepResult>(state.StepHistory),
        LegacyMigrationSources = new Dictionary<string, string>(
            state.LegacyMigrationSources,
            StringComparer.OrdinalIgnoreCase)
      };
    }

    private static StateData DeserializeLegacyStateData(string json)
    {
      JsonException? stateDataError;
      try
      {
        var state = JsonSerializer.Deserialize<StateData>(json)
            ?? throw new JsonException("Legacy state contained JSON null.");
        ValidateLegacyStateData(state);
        return state;
      }
      catch (JsonException ex)
      {
        stateDataError = ex;
      }

      try
      {
        var appliedItems = JsonSerializer.Deserialize<HashSet<string>>(json)
            ?? throw new JsonException("Legacy applied-items state contained JSON null.");
        return new StateData { AppliedItems = appliedItems };
      }
      catch (JsonException ex)
      {
        throw new JsonException(
            $"Legacy state did not match a supported format: {stateDataError.Message}", ex);
      }
    }

    private static void ValidateLegacyStateData(StateData state)
    {
      ValidateBusinessStateData(state, "Legacy state");
      if (state.LegacyMigrationSources is null)
        throw new JsonException("Legacy state property 'legacy_migration_sources' must be a collection.");
    }

    private static StateData DeserializeRestoreStateData(string json)
    {
      JsonException? stateDataError;
      try
      {
        var state = JsonSerializer.Deserialize<StateData>(json)
            ?? throw new JsonException("State backup contained JSON null.");
        ValidateBusinessStateData(state, "State backup");
        return state;
      }
      catch (JsonException ex)
      {
        stateDataError = ex;
      }

      try
      {
        var appliedItems = JsonSerializer.Deserialize<HashSet<string>>(json)
            ?? throw new JsonException("State backup applied-items data contained JSON null.");
        return new StateData { AppliedItems = appliedItems };
      }
      catch (JsonException ex)
      {
        throw new JsonException(
            $"State backup did not match a supported format: {stateDataError.Message}", ex);
      }
    }

    private static void ValidateBusinessStateData(StateData state, string description)
    {
      if (state.AppliedItems is null)
        throw new JsonException($"{description} property 'applied_items' must be a collection.");
      if (state.SystemSettingOriginals is null)
        throw new JsonException($"{description} property 'system_setting_originals' must be a collection.");
      if (state.StepHistory is null)
        throw new JsonException($"{description} property 'step_history' must be a collection.");
    }

    private void AddCandidate(
        Dictionary<string, LegacyStateCandidate> candidates,
        string? path,
        string description,
        string backupSuffix,
        LegacyStateFormat format)
    {
      if (string.IsNullOrWhiteSpace(path)) return;

      try
      {
        var canonicalPath = Path.GetFullPath(path);
        if (string.Equals(canonicalPath, Path.GetFullPath(_stateFilePath), StringComparison.OrdinalIgnoreCase)) return;
        candidates.TryAdd(canonicalPath, new LegacyStateCandidate(canonicalPath, description, backupSuffix, format));
      }
      catch (Exception ex)
      {
        _logger.LogWarning($"[State] Ignoring invalid legacy state path '{path}': {ex.Message}");
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
        if (stateData != null) return NormalizeLoadedState(stateData);
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

    private static StateData NormalizeLoadedState(StateData state)
    {
      state.AppliedItems ??= new HashSet<string>();
      state.SystemSettingOriginals ??= new Dictionary<string, object>();
      state.StepHistory ??= new Dictionary<string, StepResult>();
      state.LegacyMigrationSources = state.LegacyMigrationSources is null
          ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
          : new Dictionary<string, string>(state.LegacyMigrationSources, StringComparer.OrdinalIgnoreCase);
      return state;
    }

    public void SaveState(StateData state)
    {
      lock (_sync)
      {
        var legacyMigrationSources = new Dictionary<string, string>(
            _inMemoryState.LegacyMigrationSources,
            StringComparer.OrdinalIgnoreCase);
        _inMemoryState = new StateData
        {
          AppliedItems = new HashSet<string>(state.AppliedItems),
          SystemSettingOriginals = new Dictionary<string, object>(state.SystemSettingOriginals),
          StepHistory = new Dictionary<string, StepResult>(state.StepHistory),
          LegacyMigrationSources = legacyMigrationSources,
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
      string? tmpPath = null;
      try
      {
        string json = JsonSerializer.Serialize(_inMemoryState, new JsonSerializerOptions { WriteIndented = true });
        string stateDirectory = Path.GetDirectoryName(Path.GetFullPath(_stateFilePath))
            ?? throw new InvalidOperationException("Could not determine the state directory.");
        tmpPath = Path.Combine(stateDirectory, $".{Path.GetFileName(_stateFilePath)}.{Guid.NewGuid():N}.tmp");

        using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
          writer.Write(json);
          writer.Flush();
          stream.Flush(flushToDisk: true);
        }

        File.Move(tmpPath, _stateFilePath, overwrite: true);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogWarning($"[State] Could not save state: {ex.Message}");
        return false;
      }
      finally
      {
        if (tmpPath is not null && File.Exists(tmpPath))
        {
          try { File.Delete(tmpPath); } catch { }
        }
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
      lock (_sync)
      {
        try
        {
          if (!File.Exists(backupPath))
          {
            _logger.LogError($"[State] Backup file not found: {backupPath}");
            return;
          }

          var restoredState = DeserializeRestoreStateData(File.ReadAllText(backupPath));
          var stateBeforeRestore = CloneStateData(_inMemoryState);
          restoredState.LegacyMigrationSources = new Dictionary<string, string>(
              stateBeforeRestore.LegacyMigrationSources,
              StringComparer.OrdinalIgnoreCase);
          _inMemoryState = CloneStateData(restoredState);

          if (!TryFlushToDisk())
          {
            _inMemoryState = stateBeforeRestore;
            _logger.LogError($"[State] Restore failed: Could not persist backup '{backupPath}'.");
            return;
          }

          _logger.LogSuccess($"[State] State restored from: {backupPath}");
        }
        catch (Exception ex)
        {
          _logger.LogError($"[State] Restore failed: {ex.Message}");
        }
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
