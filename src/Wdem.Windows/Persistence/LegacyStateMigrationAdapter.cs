using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Wdem.Core.Runs;

namespace Wdem.Windows.Persistence;

public sealed record LegacyStateMigrationResult(
    bool MigrationPerformed,
    IReadOnlyList<string> ImportedStepNames,
    string MarkerPath);

internal interface ILegacyFileFinalPathResolver
{
  string ResolveFinalPath(FileStream stream, string requestedPath);
}

internal interface ILegacyMigrationFileOperations
{
  void CommitQuarantine(string temporaryPath, string quarantinePath);
  void CommitNewMarker(string temporaryPath, string markerPath);
}

internal interface IMigrationMarkerFinalPathResolver
{
  string ResolveFinalPath(FileStream stream, string requestedPath);
}

internal enum MigrationPathEntryState
{
  Absent,
  Present,
  Inaccessible
}

internal interface IMigrationPathEntryProbe
{
  MigrationPathEntryState Probe(string path);
}

public sealed class LegacyStateMigrationAdapter
{
  internal const int MaximumLegacyFileBytes = 1024 * 1024;
  internal const int MaximumImportedStepNames = 128;
  private const int MaximumCandidateFiles = 3;
  private const int MaximumMarkerBytes = 128 * 1024;
  private const int MaximumStepNameLength = 128;

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

  private static readonly LogRedactor StepNameRedactor = new();

  private static readonly HashSet<string> KnownAmbiguousLegacyPackageIdentifiers = new(
      StringComparer.OrdinalIgnoreCase)
  {
    "com.microsoft.visualstudiobuildtools",
    "ai.openai.chatgptdesktop",
    "visual-studio-build-tools-2022",
    "visual-studio-build-tools-v2022",
    "visual-studio-build-tools-rc1",
    "visual-studio-build-tools-x64",
    "visual-studio-build-tools-win11"
  };

  private static readonly HashSet<string> KnownLegacyManagerNames = new(
      StringComparer.OrdinalIgnoreCase)
  {
    "winget",
    "choco",
    "chocolatey",
    "scoop"
  };

  private enum LegacyNameSource
  {
    AppliedItem,
    StepName,
    StepHistoryKey,
    PersistedMarker
  }

  private readonly string _legacyDirectory;
  private readonly string _markerDirectory;
  private readonly string _markerPath;
  private readonly string _gatePath;
  private readonly string _lockPath;
  private readonly ILegacyFileFinalPathResolver _finalPathResolver;
  private readonly ILegacyMigrationFileOperations _fileOperations;
  private readonly IMigrationMarkerFinalPathResolver _markerFinalPathResolver;
  private readonly IMigrationPathEntryProbe _pathEntryProbe;

  public LegacyStateMigrationAdapter()
      : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
  {
  }

  public LegacyStateMigrationAdapter(string localApplicationData)
      : this(localApplicationData, WindowsLegacyFileFinalPathResolver.Instance)
  {
  }

  internal LegacyStateMigrationAdapter(
      string localApplicationData,
      ILegacyFileFinalPathResolver finalPathResolver)
      : this(
          localApplicationData,
          finalPathResolver,
          SystemLegacyMigrationFileOperations.Instance,
          WindowsMigrationMarkerFinalPathResolver.Instance,
          SystemMigrationPathEntryProbe.Instance)
  {
  }

  internal LegacyStateMigrationAdapter(
      string localApplicationData,
      ILegacyFileFinalPathResolver finalPathResolver,
      ILegacyMigrationFileOperations fileOperations)
      : this(
          localApplicationData,
          finalPathResolver,
          fileOperations,
          WindowsMigrationMarkerFinalPathResolver.Instance,
          SystemMigrationPathEntryProbe.Instance)
  {
  }

  internal LegacyStateMigrationAdapter(
      string localApplicationData,
      ILegacyFileFinalPathResolver finalPathResolver,
      ILegacyMigrationFileOperations fileOperations,
      IMigrationMarkerFinalPathResolver markerFinalPathResolver)
      : this(
          localApplicationData,
          finalPathResolver,
          fileOperations,
          markerFinalPathResolver,
          SystemMigrationPathEntryProbe.Instance)
  {
  }

  internal LegacyStateMigrationAdapter(
      string localApplicationData,
      ILegacyFileFinalPathResolver finalPathResolver,
      ILegacyMigrationFileOperations fileOperations,
      IMigrationMarkerFinalPathResolver markerFinalPathResolver,
      IMigrationPathEntryProbe pathEntryProbe)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
    _finalPathResolver = finalPathResolver ??
        throw new ArgumentNullException(nameof(finalPathResolver));
    _fileOperations = fileOperations ??
        throw new ArgumentNullException(nameof(fileOperations));
    _markerFinalPathResolver = markerFinalPathResolver ??
        throw new ArgumentNullException(nameof(markerFinalPathResolver));
    _pathEntryProbe = pathEntryProbe ??
        throw new ArgumentNullException(nameof(pathEntryProbe));
    var localRoot = Path.GetFullPath(localApplicationData);
    _legacyDirectory = Path.Combine(localRoot, "WinHome");
    _markerDirectory = Path.Combine(localRoot, "WDEM");
    _markerPath = Path.Combine(_markerDirectory, "migration-v1.json");
    _gatePath = Path.Combine(_markerDirectory, ".migration-v1.gate");
    _lockPath = Path.Combine(_markerDirectory, ".migration-v1.lock");
  }

  public async Task<LegacyStateMigrationResult> MigrateAsync(
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Directory.CreateDirectory(_markerDirectory);
    await using var migrationLock = await AcquireLockAsync(cancellationToken)
        .ConfigureAwait(false);
    try
    {
      var markerState = _pathEntryProbe.Probe(_markerPath);
      if (markerState == MigrationPathEntryState.Inaccessible)
      {
        throw CreateInaccessiblePathException(_markerPath);
      }

      var gateState = _pathEntryProbe.Probe(_gatePath);
      if (gateState == MigrationPathEntryState.Inaccessible)
      {
        throw CreateInaccessiblePathException(_gatePath);
      }

      if (markerState == MigrationPathEntryState.Present)
      {
        if (gateState == MigrationPathEntryState.Absent)
        {
          await CreatePersistentGateAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ResolveExistingMarkerAsync(
            includePersistedNames: false,
            cancellationToken).ConfigureAwait(false);
      }

      if (gateState == MigrationPathEntryState.Present)
      {
        return await CommitSentinelFromGateAsync(cancellationToken)
            .ConfigureAwait(false);
      }

      var gateCreated = await CreatePersistentGateAsync(cancellationToken)
          .ConfigureAwait(false);
      if (!gateCreated)
      {
        return await CommitSentinelFromGateAsync(cancellationToken)
            .ConfigureAwait(false);
      }

      var importedStepNames = await DiscoverStepNamesAsync(cancellationToken)
          .ConfigureAwait(false);
      var marker = CreateMarker(importedStepNames);
      return await CommitDiscoveredMarkerAsync(
          marker,
          importedStepNames,
          cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      TryDeleteLockAfterRelease(migrationLock);
    }
  }

  private async Task<bool> CreatePersistentGateAsync(
      CancellationToken cancellationToken)
  {
    var gateBytes = "WDEM legacy migration attempted v1\n"u8.ToArray();
    try
    {
      await using var stream = new FileStream(
          _gatePath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.Read,
          4096,
          FileOptions.Asynchronous | FileOptions.WriteThrough);
      await stream.WriteAsync(gateBytes, cancellationToken).ConfigureAwait(false);
      await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
      stream.Flush(flushToDisk: true);
      return true;
    }
    catch (IOException exception)
    {
      switch (_pathEntryProbe.Probe(_gatePath))
      {
        case MigrationPathEntryState.Present:
          return false;
        case MigrationPathEntryState.Inaccessible:
          throw CreateInaccessiblePathException(_gatePath, exception);
        case MigrationPathEntryState.Absent:
        default:
          ExceptionDispatchInfo.Capture(exception).Throw();
          throw;
      }
    }
  }

  private async Task<LegacyStateMigrationResult> CommitDiscoveredMarkerAsync(
      LegacyMigrationMarker marker,
      IReadOnlyList<string> importedStepNames,
      CancellationToken cancellationToken)
  {
    MarkerWriteResult? lastFailure = null;
    for (var attempt = 0; attempt < 3; attempt++)
    {
      var writeResult = await WriteMarkerAtomicallyAsync(
          marker,
          cancellationToken).ConfigureAwait(false);
      if (writeResult.Outcome == MarkerWriteOutcome.Written)
      {
        return new LegacyStateMigrationResult(true, importedStepNames, _markerPath);
      }

      switch (_pathEntryProbe.Probe(_markerPath))
      {
        case MigrationPathEntryState.Present:
          return await ResolveExistingMarkerAsync(
              includePersistedNames: true,
              cancellationToken).ConfigureAwait(false);
        case MigrationPathEntryState.Inaccessible:
          throw CreateInaccessiblePathException(_markerPath);
      }

      lastFailure = writeResult;
    }

    ThrowIfMarkerWriteFailed(lastFailure!);
    throw new IOException("The migration marker could not be committed.");
  }

  private async Task<LegacyStateMigrationResult> CommitSentinelFromGateAsync(
      CancellationToken cancellationToken)
  {
    MarkerWriteResult? lastFailure = null;
    for (var attempt = 0; attempt < 3; attempt++)
    {
      var writeResult = await WriteMarkerAtomicallyAsync(
          CreateMarker(Array.AsReadOnly(Array.Empty<string>())),
          cancellationToken).ConfigureAwait(false);
      if (writeResult.Outcome == MarkerWriteOutcome.Written)
      {
        return NotPerformed();
      }

      switch (_pathEntryProbe.Probe(_markerPath))
      {
        case MigrationPathEntryState.Present:
          return await ResolveExistingMarkerAsync(
              includePersistedNames: true,
              cancellationToken).ConfigureAwait(false);
        case MigrationPathEntryState.Inaccessible:
          throw CreateInaccessiblePathException(_markerPath);
      }

      lastFailure = writeResult;
    }

    ThrowIfMarkerWriteFailed(lastFailure!);
    throw new IOException("The migration sentinel could not be committed.");
  }

  private LegacyStateMigrationResult NotPerformed() => new(
      false,
      Array.AsReadOnly(Array.Empty<string>()),
      _markerPath);

  private LegacyStateMigrationResult NotPerformed(
      IReadOnlyList<string> importedStepNames) => new(
      false,
      importedStepNames,
      _markerPath);

  private static LegacyMigrationMarker CreateMarker(
      IReadOnlyList<string> importedStepNames) => new(
      1,
      "legacy-step-name-reference",
      "WinHome",
      DateTimeOffset.UtcNow,
      importedStepNames);

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
        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
            .ConfigureAwait(false);
      }
    }
  }

  private async Task<MarkerReadResult> ReadMarkerAsync(
      CancellationToken cancellationToken)
  {
    switch (_pathEntryProbe.Probe(_markerPath))
    {
      case MigrationPathEntryState.Absent:
        return MarkerReadResult.Missing;
      case MigrationPathEntryState.Inaccessible:
        throw CreateInaccessiblePathException(_markerPath);
    }

    byte[] markerBytes;
    try
    {
      if ((File.GetAttributes(_markerPath) & FileAttributes.ReparsePoint) != 0)
      {
        return MarkerReadResult.UnsafePath;
      }

      await using var stream = new FileStream(
          _markerPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          4096,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      var finalPath = _markerFinalPathResolver.ResolveFinalPath(stream, _markerPath);
      if (!IsSameFinalPath(_markerPath, finalPath))
      {
        return MarkerReadResult.UnsafePath;
      }

      if (stream.Length > MaximumMarkerBytes)
      {
        return MarkerReadResult.Oversized;
      }

      markerBytes = new byte[checked((int)stream.Length)];
      await stream.ReadExactlyAsync(markerBytes, cancellationToken).ConfigureAwait(false);
    }
    catch (Exception exception) when (
        exception is IOException or
        UnauthorizedAccessException or
        Win32Exception or
        ArgumentException or
        NotSupportedException)
    {
      return MarkerReadResult.Unreadable;
    }

    try
    {
      using var document = JsonDocument.Parse(
          markerBytes,
          new JsonDocumentOptions { MaxDepth = 16 });
      var root = document.RootElement;
      var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
      {
        "schemaVersion",
        "recordKind",
        "sourceProduct",
        "importedAtUtc",
        "importedStepNames"
      };
      if (root.ValueKind != JsonValueKind.Object ||
          root.EnumerateObject().Count() != expectedProperties.Count ||
          root.EnumerateObject().Any(property => !expectedProperties.Contains(property.Name)) ||
          !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
          schemaVersion.ValueKind != JsonValueKind.Number ||
          !schemaVersion.TryGetInt32(out var version) ||
          version != 1 ||
          !TryGetExactMarkerString(root, "recordKind", "legacy-step-name-reference") ||
          !TryGetExactMarkerString(root, "sourceProduct", "WinHome") ||
          !root.TryGetProperty("importedAtUtc", out var importedAtUtc) ||
          importedAtUtc.ValueKind != JsonValueKind.String ||
          !importedAtUtc.TryGetDateTimeOffset(out _) ||
          !root.TryGetProperty("importedStepNames", out var importedNames) ||
          importedNames.ValueKind != JsonValueKind.Array ||
          importedNames.GetArrayLength() > MaximumImportedStepNames)
      {
        return MarkerReadResult.Invalid(markerBytes);
      }

      var names = new List<string>(importedNames.GetArrayLength());
      foreach (var item in importedNames.EnumerateArray())
      {
        if (item.ValueKind != JsonValueKind.String ||
            item.GetString() is not { } name ||
            !IsSafeMigratedName(name, LegacyNameSource.PersistedMarker) ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
          return MarkerReadResult.Invalid(markerBytes);
        }

        names.Add(name);
      }

      return MarkerReadResult.Valid(Array.AsReadOnly(names.ToArray()));
    }
    catch (JsonException)
    {
      return MarkerReadResult.Invalid(markerBytes);
    }
  }

  private async Task<LegacyStateMigrationResult> ResolveExistingMarkerAsync(
      bool includePersistedNames,
      CancellationToken cancellationToken)
  {
    var marker = await ReadMarkerAsync(cancellationToken).ConfigureAwait(false);
    if (marker.State == MarkerReadState.Valid)
    {
      return includePersistedNames
          ? NotPerformed(marker.ImportedStepNames)
          : NotPerformed();
    }

    if (marker.State == MarkerReadState.Missing)
    {
      return await CommitSentinelFromGateAsync(cancellationToken).ConfigureAwait(false);
    }

    if (marker.State != MarkerReadState.Invalid)
    {
      return NotPerformed();
    }

    await WriteQuarantineAtomicallyAsync(marker.RawBytes!, cancellationToken)
        .ConfigureAwait(false);
    return NotPerformed();
  }

  private static bool TryGetExactMarkerString(
      JsonElement element,
      string propertyName,
      string expected) => element.TryGetProperty(propertyName, out var value) &&
      value.ValueKind == JsonValueKind.String &&
      string.Equals(value.GetString(), expected, StringComparison.Ordinal);

  private async Task WriteQuarantineAtomicallyAsync(
      byte[] markerBytes,
      CancellationToken cancellationToken)
  {
    if (markerBytes.Length > MaximumMarkerBytes)
    {
      throw new InvalidDataException("The migration quarantine exceeded its safe limit.");
    }

    var identifier = Guid.NewGuid().ToString("N");
    var quarantinePath = Path.Combine(
        _markerDirectory,
        $"migration-v1.invalid-{identifier}.json");
    var temporaryPath = Path.Combine(
        _markerDirectory,
        $".migration-v1.invalid-{identifier}.tmp");
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
        await stream.WriteAsync(markerBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
      }

      _fileOperations.CommitQuarantine(temporaryPath, quarantinePath);
      global::System.Diagnostics.Trace.WriteLine(
          "[LegacyMigration] Invalid completion marker was copied to quarantine.");
    }
    catch
    {
      TryDeleteFile(quarantinePath);
      throw;
    }
    finally
    {
      TryDeleteFile(temporaryPath);
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
    var seenCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var redactedItemNumber = 0;
    foreach (var fileName in LegacyStateFileNames.Take(MaximumCandidateFiles))
    {
      if (names.Count >= MaximumImportedStepNames)
      {
        break;
      }

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
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var finalPath = _finalPathResolver.ResolveFinalPath(stream, path);
        if (!IsFinalPathWithinRoot(_legacyDirectory, finalPath) ||
            stream.Length > MaximumLegacyFileBytes)
        {
          global::System.Diagnostics.Trace.WriteLine(
              "[LegacyMigration] A transition-source candidate was skipped by safety limits.");
          continue;
        }

        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { MaxDepth = 32 },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ExtractNames(
            document.RootElement,
            names,
            seenCandidates,
            seenNames,
            ref redactedItemNumber);
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
      catch (Win32Exception)
      {
        // Final-path resolution failures make the candidate untrusted.
      }
      catch (ArgumentException)
      {
        // Invalid canonical paths are not migration evidence.
      }
      catch (NotSupportedException)
      {
        // Unsupported canonical paths are not migration evidence.
      }
    }

    return Array.AsReadOnly(names.ToArray());
  }

  internal static bool IsFinalPathWithinRoot(string rootPath, string finalPath)
  {
    try
    {
      var root = NormalizeFinalPath(rootPath)
          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      var candidate = NormalizeFinalPath(finalPath);
      var prefix = root + Path.DirectorySeparatorChar;
      return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception exception) when (
        exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return false;
    }
  }

  private static string NormalizeFinalPath(string path)
  {
    const string extendedUncPrefix = @"\\?\UNC\";
    const string extendedPrefix = @"\\?\";
    if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
    {
      path = @"\\" + path[extendedUncPrefix.Length..];
    }
    else if (path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
    {
      path = path[extendedPrefix.Length..];
    }

    return Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
  }

  private static bool IsSameFinalPath(string expectedPath, string finalPath)
  {
    try
    {
      return string.Equals(
          NormalizeFinalPath(expectedPath),
          NormalizeFinalPath(finalPath),
          StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception exception) when (
        exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return false;
    }
  }

  private static IOException CreateInaccessiblePathException(
      string path,
      Exception? innerException = null) => new(
      $"The migration path state for '{Path.GetFileName(path)}' could not be inspected safely.",
      innerException);

  private static void ExtractNames(
      JsonElement root,
      List<string> names,
      HashSet<string> seenCandidates,
      HashSet<string> seenNames,
      ref int redactedItemNumber)
  {
    if (root.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in root.EnumerateArray())
      {
        if (names.Count >= MaximumImportedStepNames)
        {
          break;
        }

        if (item.ValueKind == JsonValueKind.String)
        {
          AddName(
              item.GetString(),
              LegacyNameSource.AppliedItem,
              names,
              seenCandidates,
              seenNames,
              ref redactedItemNumber);
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
        if (names.Count >= MaximumImportedStepNames)
        {
          break;
        }

        if (item.ValueKind == JsonValueKind.String)
        {
          AddName(
              item.GetString(),
              LegacyNameSource.AppliedItem,
              names,
              seenCandidates,
              seenNames,
              ref redactedItemNumber);
        }
      }
    }

    if (TryGetProperty(root, "step_history", out var stepHistory) &&
        stepHistory.ValueKind == JsonValueKind.Object)
    {
      recognizedContainer = true;
      ExtractStepDictionary(
          stepHistory,
          names,
          seenCandidates,
          seenNames,
          ref redactedItemNumber);
    }

    if (!recognizedContainer && root.EnumerateObject().All(
            property => property.Value.ValueKind == JsonValueKind.Object))
    {
      ExtractStepDictionary(
          root,
          names,
          seenCandidates,
          seenNames,
          ref redactedItemNumber);
    }
  }

  private static void ExtractStepDictionary(
      JsonElement dictionary,
      List<string> names,
      HashSet<string> seenCandidates,
      HashSet<string> seenNames,
      ref int redactedItemNumber)
  {
    foreach (var property in dictionary.EnumerateObject())
    {
      if (names.Count >= MaximumImportedStepNames)
      {
        break;
      }

      if (property.Value.ValueKind != JsonValueKind.Object)
      {
        continue;
      }

      string? name;
      LegacyNameSource source;
      if (TryGetProperty(property.Value, "stepName", out var stepName) ||
          TryGetProperty(property.Value, "step_name", out stepName))
      {
        if (stepName.ValueKind != JsonValueKind.String)
        {
          continue;
        }

        name = stepName.GetString();
        source = LegacyNameSource.StepName;
      }
      else
      {
        name = property.Name;
        source = LegacyNameSource.StepHistoryKey;
      }

      AddName(
          name,
          source,
          names,
          seenCandidates,
          seenNames,
          ref redactedItemNumber);
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
      LegacyNameSource source,
      List<string> names,
      HashSet<string> seenCandidates,
      HashSet<string> seenNames,
      ref int redactedItemNumber)
  {
    if (names.Count >= MaximumImportedStepNames ||
        string.IsNullOrWhiteSpace(candidate))
    {
      return;
    }

    var sanitized = candidate.Trim();
    var isSafe = IsSafeMigratedName(sanitized, source);
    var classificationKey = (isSafe ? "safe:" : "redacted:") + sanitized;
    if (!seenCandidates.Add(classificationKey))
    {
      return;
    }

    if (isSafe)
    {
      if (seenNames.Add(sanitized))
      {
        names.Add(sanitized);
      }

      return;
    }

    string placeholder;
    do
    {
      redactedItemNumber++;
      placeholder = $"Legacy item {redactedItemNumber} (redacted)";
    }
    while (!seenNames.Add(placeholder));

    if (names.Count < MaximumImportedStepNames)
    {
      names.Add(placeholder);
    }
  }

  private static bool IsSafeMigratedName(string value, LegacyNameSource source)
  {
    if (value.Length is 0 or > MaximumStepNameLength)
    {
      return false;
    }

    var allowsManagedIdentifier = source is
        LegacyNameSource.AppliedItem or
        LegacyNameSource.StepHistoryKey or
        LegacyNameSource.PersistedMarker;
    if (allowsManagedIdentifier &&
        TryGetKnownLegacyManagerSuffix(value, out var suffix))
    {
      return IsSafeUnqualifiedName(
          suffix,
          KnownAmbiguousLegacyPackageIdentifiers.Contains(suffix));
    }

    var allowsKnownAmbiguousPackage =
        source is LegacyNameSource.AppliedItem or LegacyNameSource.PersistedMarker &&
        KnownAmbiguousLegacyPackageIdentifiers.Contains(value);
    return IsSafeUnqualifiedName(value, allowsKnownAmbiguousPackage);
  }

  private static bool TryGetKnownLegacyManagerSuffix(
      string value,
      out string suffix)
  {
    var separatorIndex = value.IndexOf(':');
    if (separatorIndex <= 0 ||
        separatorIndex != value.LastIndexOf(':') ||
        !KnownLegacyManagerNames.Contains(value[..separatorIndex]))
    {
      suffix = string.Empty;
      return false;
    }

    suffix = value[(separatorIndex + 1)..];
    return suffix.Length > 0 &&
        string.Equals(suffix, suffix.Trim(), StringComparison.Ordinal);
  }

  private static bool IsSafeUnqualifiedName(
      string value,
      bool allowsOpaqueValue)
  {
    if (value.Length is 0 or > MaximumStepNameLength ||
        value.Any(character => char.IsControl(character)) ||
        value.Contains("..", StringComparison.Ordinal) ||
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(StepNameRedactor.Redact(value), value, StringComparison.Ordinal) ||
        HasKnownCredentialPrefix(value) ||
        HasExplicitCompoundCredentialKey(value) ||
        LooksLikeJsonWebToken(value))
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
    if (words.Any(word => word.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("password", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("apikey", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("credential", StringComparison.OrdinalIgnoreCase) ||
        word.Equals("authorization", StringComparison.OrdinalIgnoreCase)))
    {
      return false;
    }

    return !LooksLikeUnclassifiedOpaqueToken(value) || allowsOpaqueValue;
  }

  private static bool HasKnownCredentialPrefix(string value) =>
      value.StartsWith("ghp_", StringComparison.OrdinalIgnoreCase) ||
      value.StartsWith("gho_", StringComparison.OrdinalIgnoreCase) ||
      value.StartsWith("ghu_", StringComparison.OrdinalIgnoreCase) ||
      value.StartsWith("ghs_", StringComparison.OrdinalIgnoreCase) ||
      value.StartsWith("ghr_", StringComparison.OrdinalIgnoreCase) ||
      value.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase) ||
      value.StartsWith("AKIA", StringComparison.OrdinalIgnoreCase) ||
      value.StartsWith("ASIA", StringComparison.OrdinalIgnoreCase);

  private static bool HasExplicitCompoundCredentialKey(string value)
  {
    var normalized = new string(value
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());
    return normalized.Contains("accesstoken", StringComparison.Ordinal) ||
        normalized.Contains("authtoken", StringComparison.Ordinal) ||
        normalized.Contains("clienttoken", StringComparison.Ordinal) ||
        normalized.Contains("apikey", StringComparison.Ordinal) ||
        normalized.Contains("clientsecret", StringComparison.Ordinal) ||
        normalized.Contains("credential", StringComparison.Ordinal) ||
        normalized.Contains("authorization", StringComparison.Ordinal) ||
        normalized.Contains("accesskey", StringComparison.Ordinal) ||
        normalized.Contains("privatekey", StringComparison.Ordinal);
  }

  private static bool LooksLikeUnclassifiedOpaqueToken(string value)
  {
    if (Guid.TryParse(value, out _))
    {
      return true;
    }

    var segments = value.Split(
        ['.', '-', '_'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (segments.Any(LooksLikeOpaqueTokenSegment))
    {
      return true;
    }

    var compactValue = string.Concat(segments);
    return LooksLikeOpaqueTokenSegment(compactValue);
  }

  private static bool LooksLikeOpaqueTokenSegment(string value)
  {
    const int MinimumOpaqueTokenLength = 20;
    const int MinimumOpaqueHexLength = 32;
    if (value.Length < MinimumOpaqueTokenLength ||
        !value.All(char.IsAsciiLetterOrDigit))
    {
      return false;
    }

    if (value.Length >= MinimumOpaqueHexLength && value.All(Uri.IsHexDigit))
    {
      return true;
    }

    return CountCamelCaseLexicalWords(value) < 2;
  }

  private static bool LooksLikeJsonWebToken(string value)
  {
    var segments = value.Split('.', StringSplitOptions.None);
    if (segments.Length != 3 ||
        segments.Any(segment =>
            segment.Length < 8 || !segment.All(IsBase64UrlCharacter)))
    {
      return false;
    }

    if (HasRecognizableJsonWebTokenHeader(segments[0]))
    {
      return true;
    }

    return segments[0].StartsWith("eyJ", StringComparison.Ordinal) &&
        segments[1].StartsWith("eyJ", StringComparison.Ordinal);
  }

  private static bool HasRecognizableJsonWebTokenHeader(string segment)
  {
    try
    {
      var base64 = segment.Replace('-', '+').Replace('_', '/');
      base64 += new string('=', (4 - base64.Length % 4) % 4);
      using var document = JsonDocument.Parse(
          Convert.FromBase64String(base64),
          new JsonDocumentOptions { MaxDepth = 4 });
      return document.RootElement.ValueKind == JsonValueKind.Object &&
          (document.RootElement.TryGetProperty("alg", out _) ||
           document.RootElement.TryGetProperty("typ", out _));
    }
    catch (Exception exception) when (
        exception is FormatException or JsonException)
    {
      return false;
    }
  }

  private static bool IsBase64UrlCharacter(char value) =>
      char.IsAsciiLetterOrDigit(value) || value is '-' or '_';

  private static int CountCamelCaseLexicalWords(string value)
  {
    var count = 0;
    for (var index = 0; index < value.Length; index++)
    {
      if (!char.IsAsciiLetterUpper(value[index]))
      {
        continue;
      }

      var wordEnd = index + 1;
      while (wordEnd < value.Length && char.IsAsciiLetterLower(value[wordEnd]))
      {
        wordEnd++;
      }

      if (wordEnd - index >= 3)
      {
        count++;
      }

      index = wordEnd - 1;
    }

    return count;
  }

  private async Task<MarkerWriteResult> WriteMarkerAtomicallyAsync(
      LegacyMigrationMarker marker,
      CancellationToken cancellationToken)
  {
    var markerBytes = JsonSerializer.SerializeToUtf8Bytes(marker, MarkerJsonOptions);
    if (markerBytes.Length > MaximumMarkerBytes)
    {
      throw new InvalidDataException("The migration marker exceeded its safe size limit.");
    }

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
        await stream.WriteAsync(markerBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
      }

      try
      {
        _fileOperations.CommitNewMarker(temporaryPath, _markerPath);
      }
      catch (IOException exception)
      {
        switch (_pathEntryProbe.Probe(_markerPath))
        {
          case MigrationPathEntryState.Present:
            return new MarkerWriteResult(MarkerWriteOutcome.Existing);
          case MigrationPathEntryState.Inaccessible:
            return new MarkerWriteResult(
                MarkerWriteOutcome.Failed,
                CreateInaccessiblePathException(_markerPath, exception));
          case MigrationPathEntryState.Absent:
          default:
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
      }

      return new MarkerWriteResult(MarkerWriteOutcome.Written);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException)
    {
      return new MarkerWriteResult(MarkerWriteOutcome.Failed, exception);
    }
    finally
    {
      TryDeleteFile(temporaryPath);
    }
  }

  private static void TryDeleteFile(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }

  private static void ThrowIfMarkerWriteFailed(MarkerWriteResult result)
  {
    if (result.Outcome != MarkerWriteOutcome.Failed || result.Failure is null)
    {
      return;
    }

    ExceptionDispatchInfo.Capture(result.Failure).Throw();
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

  private sealed record MarkerReadResult(
      MarkerReadState State,
      IReadOnlyList<string> ImportedStepNames,
      byte[]? RawBytes = null)
  {
    public static MarkerReadResult Missing { get; } = Empty(MarkerReadState.Missing);
    public static MarkerReadResult Oversized { get; } = Empty(
        MarkerReadState.Oversized);
    public static MarkerReadResult UnsafePath { get; } = Empty(
        MarkerReadState.UnsafePath);
    public static MarkerReadResult Unreadable { get; } = Empty(
        MarkerReadState.Unreadable);

    public static MarkerReadResult Invalid(byte[] bytes) => new(
        MarkerReadState.Invalid,
        Array.AsReadOnly(Array.Empty<string>()),
        bytes);

    public static MarkerReadResult Valid(IReadOnlyList<string> names) => new(
        MarkerReadState.Valid,
        names);

    private static MarkerReadResult Empty(MarkerReadState state) => new(
        state,
        Array.AsReadOnly(Array.Empty<string>()));
  }

  private sealed record MarkerWriteResult(
      MarkerWriteOutcome Outcome,
      Exception? Failure = null);

  private enum MarkerReadState
  {
    Missing,
    Valid,
    Invalid,
    Oversized,
    UnsafePath,
    Unreadable
  }

  private enum MarkerWriteOutcome
  {
    Written,
    Existing,
    Failed
  }
}

internal sealed class SystemLegacyMigrationFileOperations :
    ILegacyMigrationFileOperations
{
  public static readonly SystemLegacyMigrationFileOperations Instance = new();

  public void CommitQuarantine(string temporaryPath, string quarantinePath) =>
      File.Move(temporaryPath, quarantinePath, overwrite: false);

  public void CommitNewMarker(string temporaryPath, string markerPath) =>
      File.Move(temporaryPath, markerPath, overwrite: false);
}

internal sealed class WindowsMigrationMarkerFinalPathResolver :
    IMigrationMarkerFinalPathResolver
{
  public static readonly WindowsMigrationMarkerFinalPathResolver Instance = new();

  public string ResolveFinalPath(FileStream stream, string requestedPath) =>
      WindowsLegacyFileFinalPathResolver.Instance.ResolveFinalPath(stream, requestedPath);
}

internal sealed class SystemMigrationPathEntryProbe : IMigrationPathEntryProbe
{
  public static readonly SystemMigrationPathEntryProbe Instance = new();

  public MigrationPathEntryState Probe(string path)
  {
    try
    {
      var directory = Path.GetDirectoryName(path);
      var fileName = Path.GetFileName(path);
      if (string.IsNullOrEmpty(directory))
      {
        return MigrationPathEntryState.Absent;
      }

      return Directory.EnumerateFileSystemEntries(
              directory,
              fileName,
              SearchOption.TopDirectoryOnly)
          .Any(entry => string.Equals(
              Path.GetFileName(entry),
              fileName,
              StringComparison.OrdinalIgnoreCase))
          ? MigrationPathEntryState.Present
          : MigrationPathEntryState.Absent;
    }
    catch (DirectoryNotFoundException)
    {
      return MigrationPathEntryState.Absent;
    }
    catch (IOException)
    {
      return MigrationPathEntryState.Inaccessible;
    }
    catch (UnauthorizedAccessException)
    {
      return MigrationPathEntryState.Inaccessible;
    }
  }
}

internal sealed class WindowsLegacyFileFinalPathResolver : ILegacyFileFinalPathResolver
{
  private const uint FileNameNormalized = 0;

  public static readonly WindowsLegacyFileFinalPathResolver Instance = new();

  public string ResolveFinalPath(FileStream stream, string requestedPath)
  {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
    if (!OperatingSystem.IsWindows())
    {
      return Path.GetFullPath(requestedPath);
    }

    var capacity = 512;
    while (true)
    {
      var buffer = new StringBuilder(capacity);
      var result = GetFinalPathNameByHandle(
          stream.SafeFileHandle,
          buffer,
          (uint)buffer.Capacity,
          FileNameNormalized);
      if (result == 0)
      {
        throw new Win32Exception(Marshal.GetLastWin32Error());
      }

      if (result < buffer.Capacity)
      {
        return buffer.ToString();
      }

      capacity = checked((int)result + 1);
    }
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint GetFinalPathNameByHandle(
      SafeFileHandle file,
      StringBuilder filePath,
      uint filePathLength,
      uint flags);
}
