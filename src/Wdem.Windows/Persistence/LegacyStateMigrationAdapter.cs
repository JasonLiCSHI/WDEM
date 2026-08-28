using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wdem.Windows.Persistence;

public sealed record LegacyStateMigrationResult(
    bool MigrationPerformed,
    IReadOnlyList<string> ImportedStepNames,
    string MarkerPath);

public sealed class LegacyStateMigrationAdapter
{
  private static readonly string[] LegacyStateFileNames =
  [
    ".winhome-state.json",
    "state.json",
    "winhome.state.json"
  ];

  private static readonly JsonSerializerOptions MarkerJsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
  };

  private readonly string _legacyDirectory;
  private readonly string _markerDirectory;
  private readonly string _markerPath;
  private readonly string _lockPath;

  public LegacyStateMigrationAdapter()
      : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
  {
  }

  public LegacyStateMigrationAdapter(string localApplicationData)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
    var localRoot = Path.GetFullPath(localApplicationData);
    _legacyDirectory = Path.Combine(localRoot, "WinHome");
    _markerDirectory = Path.Combine(localRoot, "WDEM");
    _markerPath = Path.Combine(_markerDirectory, "migration-v1.json");
    _lockPath = Path.Combine(_markerDirectory, ".migration-v1.lock");
  }

  public async Task<LegacyStateMigrationResult> MigrateAsync(
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (File.Exists(_markerPath))
    {
      return NotPerformed();
    }

    Directory.CreateDirectory(_markerDirectory);
    await using var migrationLock = await AcquireLockAsync(cancellationToken)
        .ConfigureAwait(false);
    try
    {
      if (File.Exists(_markerPath))
      {
        return NotPerformed();
      }

      var importedStepNames = await DiscoverStepNamesAsync(cancellationToken)
          .ConfigureAwait(false);
      var marker = new LegacyMigrationMarker(
          1,
          "legacy-step-name-reference",
          "WinHome",
          DateTimeOffset.UtcNow,
          importedStepNames);
      await WriteMarkerAtomicallyAsync(marker, cancellationToken).ConfigureAwait(false);
      return new LegacyStateMigrationResult(true, importedStepNames, _markerPath);
    }
    finally
    {
      TryDeleteLockAfterRelease(migrationLock);
    }
  }

  private LegacyStateMigrationResult NotPerformed() => new(
      false,
      Array.AsReadOnly(Array.Empty<string>()),
      _markerPath);

  private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
  {
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return new FileStream(
            _lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
      }
      catch (IOException)
      {
        if (File.Exists(_markerPath))
        {
          return new FileStream(
              _markerPath,
              FileMode.Open,
              FileAccess.Read,
              FileShare.ReadWrite | FileShare.Delete,
              1,
              FileOptions.Asynchronous);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
            .ConfigureAwait(false);
      }
    }
  }

  private async Task<IReadOnlyList<string>> DiscoverStepNamesAsync(
      CancellationToken cancellationToken)
  {
    if (!Directory.Exists(_legacyDirectory) || IsReparsePoint(_legacyDirectory))
    {
      return Array.AsReadOnly(Array.Empty<string>());
    }

    var names = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var fileName in LegacyStateFileNames)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var path = Path.Combine(_legacyDirectory, fileName);
      if (!File.Exists(path) || IsReparsePoint(path))
      {
        continue;
      }

      try
      {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ExtractNames(document.RootElement, names, seen);
      }
      catch (JsonException)
      {
        // Invalid transition-source state is ignored. The marker records no raw data.
      }
      catch (IOException)
      {
        // A file that cannot be read is not migration evidence.
      }
      catch (UnauthorizedAccessException)
      {
        // Access failures do not expose source paths or contents in product diagnostics.
      }
    }

    return Array.AsReadOnly(names.ToArray());
  }

  private static void ExtractNames(
      JsonElement root,
      List<string> names,
      HashSet<string> seen)
  {
    if (root.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in root.EnumerateArray())
      {
        if (item.ValueKind == JsonValueKind.String)
        {
          AddName(item.GetString(), names, seen);
        }
      }

      return;
    }

    if (root.ValueKind != JsonValueKind.Object)
    {
      return;
    }

    var recognizedContainer = false;
    if (TryGetProperty(root, "applied_items", out var appliedItems) &&
        appliedItems.ValueKind == JsonValueKind.Array)
    {
      recognizedContainer = true;
      foreach (var item in appliedItems.EnumerateArray())
      {
        if (item.ValueKind == JsonValueKind.String)
        {
          AddName(item.GetString(), names, seen);
        }
      }
    }

    if (TryGetProperty(root, "step_history", out var stepHistory) &&
        stepHistory.ValueKind == JsonValueKind.Object)
    {
      recognizedContainer = true;
      ExtractStepDictionary(stepHistory, names, seen);
    }

    if (!recognizedContainer && root.EnumerateObject().All(
            property => property.Value.ValueKind == JsonValueKind.Object))
    {
      ExtractStepDictionary(root, names, seen);
    }
  }

  private static void ExtractStepDictionary(
      JsonElement dictionary,
      List<string> names,
      HashSet<string> seen)
  {
    foreach (var property in dictionary.EnumerateObject())
    {
      if (property.Value.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      string? name;
      if (TryGetProperty(property.Value, "stepName", out var stepName) ||
          TryGetProperty(property.Value, "step_name", out stepName))
      {
        if (stepName.ValueKind != JsonValueKind.String)
        {
          continue;
        }

        name = stepName.GetString();
      }
      else
      {
        name = property.Name;
      }

      AddName(name, names, seen);
    }
  }

  private static bool TryGetProperty(
      JsonElement element,
      string name,
      out JsonElement value)
  {
    foreach (var property in element.EnumerateObject())
    {
      if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        value = property.Value;
        return true;
      }
    }

    value = default;
    return false;
  }

  private static void AddName(
      string? candidate,
      List<string> names,
      HashSet<string> seen)
  {
    if (string.IsNullOrWhiteSpace(candidate))
    {
      return;
    }

    var sanitized = candidate.Trim();
    if (IsSafeStepName(sanitized) && seen.Add(sanitized))
    {
      names.Add(sanitized);
    }
  }

  private static bool IsSafeStepName(string value)
  {
    const int MaximumStepNameLength = 128;
    if (value.Length is 0 or > MaximumStepNameLength ||
        value.Any(character => char.IsControl(character)) ||
        value.Contains("..", StringComparison.Ordinal) ||
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
      return false;
    }

    if (value.Any(character =>
            !(char.IsLetterOrDigit(character) ||
              char.IsWhiteSpace(character) ||
              character is '-' or '_' or '.' or '(' or ')' or '+' or '#')))
    {
      return false;
    }

    var words = value.Split(
        [' ', '-', '_', '.', '(', ')', '+', '#'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return !words.Any(word => word.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("password", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("credential", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("authorization", StringComparison.OrdinalIgnoreCase));
  }

  private async Task WriteMarkerAtomicallyAsync(
      LegacyMigrationMarker marker,
      CancellationToken cancellationToken)
  {
    var temporaryPath = Path.Combine(
        _markerDirectory,
        $".migration-v1.{Guid.NewGuid():N}.tmp");
    try
    {
      await using (var stream = new FileStream(
          temporaryPath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          4096,
          FileOptions.Asynchronous | FileOptions.WriteThrough))
      {
        await JsonSerializer.SerializeAsync(
            stream,
            marker,
            MarkerJsonOptions,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
      }

      File.Move(temporaryPath, _markerPath, overwrite: false);
    }
    catch (IOException) when (File.Exists(_markerPath))
    {
      // A different process completed the same migration first.
    }
    finally
    {
      try
      {
        File.Delete(temporaryPath);
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }
    }
  }

  private static bool IsReparsePoint(string path)
  {
    try
    {
      return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }
    catch (IOException)
    {
      return true;
    }
    catch (UnauthorizedAccessException)
    {
      return true;
    }
  }

  private void TryDeleteLockAfterRelease(FileStream migrationLock)
  {
    try
    {
      migrationLock.Dispose();
      File.Delete(_lockPath);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }

  private sealed record LegacyMigrationMarker(
      int SchemaVersion,
      string RecordKind,
      string SourceProduct,
      DateTimeOffset ImportedAtUtc,
      [property: JsonPropertyName("importedStepNames")]
      IReadOnlyList<string> ImportedStepNames);
}
