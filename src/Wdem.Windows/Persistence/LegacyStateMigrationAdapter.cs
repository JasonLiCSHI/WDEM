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
  void CopyMarkerForQuarantine(string sourcePath, string destinationPath);
  void CommitNewMarker(string temporaryPath, string markerPath);
  void ReplaceMarker(string temporaryPath, string markerPath);
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
  private readonly string _lockPath;
  private readonly ILegacyFileFinalPathResolver _finalPathResolver;
  private readonly ILegacyMigrationFileOperations _fileOperations;

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
          SystemLegacyMigrationFileOperations.Instance)
  {
  }

  internal LegacyStateMigrationAdapter(
      string localApplicationData,
      ILegacyFileFinalPathResolver finalPathResolver,
      ILegacyMigrationFileOperations fileOperations)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
    _finalPathResolver = finalPathResolver ??
        throw new ArgumentNullException(nameof(finalPathResolver));
    _fileOperations = fileOperations ??
        throw new ArgumentNullException(nameof(fileOperations));
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
    Directory.CreateDirectory(_markerDirectory);
    await using var migrationLock = await AcquireLockAsync(cancellationToken)
        .ConfigureAwait(false);
    try
    {
      if (File.Exists(_markerPath))
      {
        return await ResolveExistingMarkerAsync(
            includePersistedNames: false,
            cancellationToken).ConfigureAwait(false);
      }

      var importedStepNames = await DiscoverStepNamesAsync(cancellationToken)
          .ConfigureAwait(false);
      var marker = CreateMarker(importedStepNames);
      var writeResult = await WriteMarkerAtomicallyAsync(
          marker,
          MarkerWriteMode.CreateNew,
          cancellationToken).ConfigureAwait(false);
      ThrowIfMarkerWriteFailed(writeResult);
      return writeResult.Outcome == MarkerWriteOutcome.Written
          ? new LegacyStateMigrationResult(true, importedStepNames, _markerPath)
          : await ResolveExistingMarkerAsync(
              includePersistedNames: true,
              cancellationToken).ConfigureAwait(false);
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
    if (!File.Exists(_markerPath))
    {
      return MarkerReadResult.Invalid;
    }

    try
    {
      await using var stream = new FileStream(
          _markerPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          4096,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      if (stream.Length is 0 or > MaximumMarkerBytes)
      {
        return MarkerReadResult.Invalid;
      }

      using var document = await JsonDocument.ParseAsync(
          stream,
          new JsonDocumentOptions { MaxDepth = 16 },
          cancellationToken).ConfigureAwait(false);
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
        return MarkerReadResult.Invalid;
      }

      var names = new List<string>(importedNames.GetArrayLength());
      foreach (var item in importedNames.EnumerateArray())
      {
        if (item.ValueKind != JsonValueKind.String ||
            item.GetString() is not { } name ||
            !IsSafeMigratedName(name, LegacyNameSource.PersistedMarker) ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal))
        {
          return MarkerReadResult.Invalid;
        }

        names.Add(name);
      }

      return new MarkerReadResult(true, Array.AsReadOnly(names.ToArray()));
    }
    catch (JsonException)
    {
      return MarkerReadResult.Invalid;
    }
    catch (IOException)
    {
      return MarkerReadResult.Invalid;
    }
    catch (UnauthorizedAccessException)
    {
      return MarkerReadResult.Invalid;
    }
  }

  private async Task<LegacyStateMigrationResult> ResolveExistingMarkerAsync(
      bool includePersistedNames,
      CancellationToken cancellationToken)
  {
    var marker = await ReadMarkerAsync(cancellationToken).ConfigureAwait(false);
    if (marker.IsValid)
    {
      return includePersistedNames
          ? NotPerformed(marker.ImportedStepNames)
          : NotPerformed();
    }

    CopyInvalidMarkerForQuarantine();
    var writeResult = await WriteMarkerAtomicallyAsync(
        CreateMarker(Array.AsReadOnly(Array.Empty<string>())),
        MarkerWriteMode.ReplaceExisting,
        cancellationToken).ConfigureAwait(false);
    ThrowIfMarkerWriteFailed(writeResult);
    return NotPerformed();
  }

  private static bool TryGetExactMarkerString(
      JsonElement element,
      string propertyName,
      string expected) => element.TryGetProperty(propertyName, out var value) &&
      value.ValueKind == JsonValueKind.String &&
      string.Equals(value.GetString(), expected, StringComparison.Ordinal);

  private void CopyInvalidMarkerForQuarantine()
  {
    if (!File.Exists(_markerPath))
    {
      return;
    }

    var quarantinePath = Path.Combine(
        _markerDirectory,
        $"migration-v1.invalid-{Guid.NewGuid():N}.json");
    _fileOperations.CopyMarkerForQuarantine(_markerPath, quarantinePath);
    global::System.Diagnostics.Trace.WriteLine(
        "[LegacyMigration] Invalid completion marker was copied to quarantine.");
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
      MarkerWriteMode mode,
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

      if (mode == MarkerWriteMode.ReplaceExisting)
      {
        _fileOperations.ReplaceMarker(temporaryPath, _markerPath);
      }
      else
      {
        try
        {
          _fileOperations.CommitNewMarker(temporaryPath, _markerPath);
        }
        catch (IOException) when (File.Exists(_markerPath))
        {
          return new MarkerWriteResult(MarkerWriteOutcome.Existing);
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
      bool IsValid,
      IReadOnlyList<string> ImportedStepNames)
  {
    public static MarkerReadResult Invalid { get; } = new(
        false,
        Array.AsReadOnly(Array.Empty<string>()));
  }

  private sealed record MarkerWriteResult(
      MarkerWriteOutcome Outcome,
      Exception? Failure = null);

  private enum MarkerWriteMode
  {
    CreateNew,
    ReplaceExisting
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

  public void CopyMarkerForQuarantine(string sourcePath, string destinationPath) =>
      File.Copy(sourcePath, destinationPath, overwrite: false);

  public void CommitNewMarker(string temporaryPath, string markerPath) =>
      File.Move(temporaryPath, markerPath, overwrite: false);

  public void ReplaceMarker(string temporaryPath, string markerPath) =>
      File.Move(temporaryPath, markerPath, overwrite: true);
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
