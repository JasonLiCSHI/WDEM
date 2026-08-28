using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Windows.Persistence;

public sealed class JsonExecutionRunStore : IExecutionRunStore
{
  private const int MaximumLogPageSize = 1000;
  private static readonly UTF8Encoding Utf8WithoutBom = new(false);
  private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _runLocks = new();
  private readonly object _diagnosticsGate = new();
  private readonly List<StructuredError> _diagnostics = [];
  private readonly WdemDataPaths _paths;
  private readonly LogRedactor _redactor;
  private readonly JsonSerializerOptions _snapshotJsonOptions;
  private readonly JsonSerializerOptions _logJsonOptions;

  public JsonExecutionRunStore(WdemDataPaths paths, LogRedactor redactor)
  {
    _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    _snapshotJsonOptions = CreateJsonOptions(writeIndented: true);
    _logJsonOptions = CreateJsonOptions(writeIndented: false);
  }

  public IReadOnlyList<StructuredError> Diagnostics
  {
    get
    {
      lock (_diagnosticsGate)
      {
        return _diagnostics.ToArray();
      }
    }
  }

  public string SnapshotPath(Guid runId) =>
      Path.Combine(_paths.RunsDirectory, $"{runId:D}.json");

  public string LogPath(Guid runId) =>
      Path.Combine(_paths.RunsDirectory, $"{runId:D}.ndjson");

  public async Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    cancellationToken.ThrowIfCancellationRequested();
    var gate = GetRunLock(run.RunId);
    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var path = SnapshotPath(run.RunId);
      if (File.Exists(path))
      {
        throw new InvalidOperationException($"Execution run '{run.RunId:D}' already exists.");
      }

      Directory.CreateDirectory(_paths.RunsDirectory);
      await WriteSnapshotAsync(path, Redact(run), cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      gate.Release();
    }
  }

  public async Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var gate = GetRunLock(runId);
    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      return await ReadSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      gate.Release();
    }
  }

  public async Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!Directory.Exists(_paths.RunsDirectory))
    {
      return [];
    }

    var runIds = Directory.EnumerateFiles(_paths.RunsDirectory, "*.json")
        .Select(Path.GetFileNameWithoutExtension)
        .Select(name => Guid.TryParse(name, out var runId) ? runId : (Guid?)null)
        .Where(runId => runId.HasValue)
        .Select(runId => runId!.Value)
        .OrderBy(runId => runId)
        .ToArray();
    var incomplete = new List<ExecutionRun>();
    foreach (var runId in runIds)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var run = await GetAsync(runId, cancellationToken).ConfigureAwait(false);
      if (run is not null && run.State != ExecutionState.Completed)
      {
        incomplete.Add(run);
      }
    }

    return incomplete;
  }

  public async Task SaveAsync(ExecutionRun run, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    cancellationToken.ThrowIfCancellationRequested();
    var gate = GetRunLock(run.RunId);
    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      var path = SnapshotPath(run.RunId);
      EnsureRunExists(run.RunId, path);
      await WriteSnapshotAsync(path, Redact(run), cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      gate.Release();
    }
  }

  public async Task AppendLogAsync(
      Guid runId,
      RunLogEntry entry,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(entry);
    cancellationToken.ThrowIfCancellationRequested();
    var gate = GetRunLock(runId);
    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      EnsureRunExists(runId, SnapshotPath(runId));
      var logPath = LogPath(runId);
      var lastSequence = await ReadLastSequenceAsync(logPath, cancellationToken)
          .ConfigureAwait(false);
      if (entry.Sequence <= lastSequence)
      {
        throw new InvalidOperationException(
            $"Log sequence {entry.Sequence} must be greater than {lastSequence} for run '{runId:D}'.");
      }

      var persistedEntry = _redactor.Redact(entry);
      var line = JsonSerializer.Serialize(persistedEntry, _logJsonOptions);
      await using var stream = new FileStream(
          logPath,
          FileMode.Append,
          FileAccess.Write,
          FileShare.Read,
          4096,
          FileOptions.Asynchronous | FileOptions.WriteThrough);
      await using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true);
      await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
      await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
      await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
      stream.Flush(flushToDisk: true);
    }
    finally
    {
      gate.Release();
    }
  }

  public async Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
      Guid runId,
      long afterSequence,
      int take,
      CancellationToken cancellationToken)
  {
    if (afterSequence < 0)
    {
      throw new ArgumentOutOfRangeException(nameof(afterSequence));
    }

    if (take is < 1 or > MaximumLogPageSize)
    {
      throw new ArgumentOutOfRangeException(nameof(take));
    }

    cancellationToken.ThrowIfCancellationRequested();
    var gate = GetRunLock(runId);
    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      EnsureRunExists(runId, SnapshotPath(runId));
      var logPath = LogPath(runId);
      if (!File.Exists(logPath))
      {
        return [];
      }

      var entries = new List<RunLogEntry>(take);
      await using var stream = new FileStream(
          logPath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.ReadWrite,
          4096,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      using var reader = new StreamReader(stream, Encoding.UTF8);
      while (entries.Count < take)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
          break;
        }

        var entry = DeserializeLogEntry(line, logPath);
        if (entry.Sequence > afterSequence)
        {
          entries.Add(entry);
        }
      }

      return entries;
    }
    finally
    {
      gate.Release();
    }
  }

  private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
  {
    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      PropertyNameCaseInsensitive = true,
      WriteIndented = writeIndented
    };
    options.Converters.Add(new JsonStringEnumConverter());
    options.Converters.Add(new ReadOnlyStringSetJsonConverter());
    return options;
  }

  private SemaphoreSlim GetRunLock(Guid runId) =>
      _runLocks.GetOrAdd(runId, static _ => new SemaphoreSlim(1, 1));

  private async Task<ExecutionRun?> ReadSnapshotAsync(
      Guid runId,
      CancellationToken cancellationToken)
  {
    var path = SnapshotPath(runId);
    if (!File.Exists(path))
    {
      return null;
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
      var run = await JsonSerializer.DeserializeAsync<ExecutionRun>(
          stream,
          _snapshotJsonOptions,
          cancellationToken).ConfigureAwait(false);
      if (run is null || run.RunId != runId)
      {
        throw new JsonException("The execution run snapshot has no matching run identifier.");
      }

      return run;
    }
    catch (Exception exception) when (
        exception is JsonException or NotSupportedException or InvalidOperationException)
    {
      PreserveCorruptedSnapshot(runId, path, exception);
      return null;
    }
  }

  private void PreserveCorruptedSnapshot(Guid runId, string path, Exception exception)
  {
    var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'");
    var preservedPath = $"{path}.corrupted.{timestamp}.{Guid.NewGuid():N}";
    string preservation;
    try
    {
      File.Move(path, preservedPath);
      preservation = $"Preserved as '{preservedPath}'.";
    }
    catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
    {
      preservation = $"Could not preserve the corrupt snapshot: {moveException.Message}";
    }

    var diagnostic = new StructuredError(
        WdemErrorCode.DetectionError,
        "Execution run snapshot is malformed.",
        $"Run '{runId:D}' could not be loaded: {exception.Message} {preservation}")
    {
      IsRetryable = false
    };
    lock (_diagnosticsGate)
    {
      _diagnostics.Add(diagnostic);
    }
  }

  private async Task WriteSnapshotAsync(
      string path,
      ExecutionRun run,
      CancellationToken cancellationToken)
  {
    var temporaryPath = path + ".tmp";
    try
    {
      var bytes = JsonSerializer.SerializeToUtf8Bytes(run, _snapshotJsonOptions);
      await using (var stream = new FileStream(
          temporaryPath,
          FileMode.Create,
          FileAccess.Write,
          FileShare.None,
          4096,
          FileOptions.Asynchronous | FileOptions.WriteThrough))
      {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (File.Exists(path))
      {
        File.Replace(temporaryPath, path, destinationBackupFileName: null);
      }
      else
      {
        File.Move(temporaryPath, path);
      }
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private async Task<long> ReadLastSequenceAsync(
      string logPath,
      CancellationToken cancellationToken)
  {
    if (!File.Exists(logPath))
    {
      return 0;
    }

    long lastSequence = 0;
    await using var stream = new FileStream(
        logPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        4096,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    using var reader = new StreamReader(stream, Encoding.UTF8);
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
      if (line is null)
      {
        return lastSequence;
      }

      lastSequence = DeserializeLogEntry(line, logPath).Sequence;
    }
  }

  private RunLogEntry DeserializeLogEntry(string line, string logPath)
  {
    try
    {
      return JsonSerializer.Deserialize<RunLogEntry>(line, _logJsonOptions)
          ?? throw new JsonException("The log entry was null.");
    }
    catch (JsonException exception)
    {
      throw new InvalidDataException($"Run log '{logPath}' contains malformed NDJSON.", exception);
    }
  }

  private static void EnsureRunExists(Guid runId, string path)
  {
    if (!File.Exists(path))
    {
      throw new KeyNotFoundException($"Execution run '{runId:D}' does not exist.");
    }
  }

  private ExecutionRun Redact(ExecutionRun run) => run with
  {
    ProfileSourcePath = _redactor.Redact(run.ProfileSourcePath),
    ProfileId = _redactor.Redact(run.ProfileId),
    ProfileVersion = _redactor.Redact(run.ProfileVersion),
    SelectedOptionalResourceIds = run.SelectedOptionalResourceIds
        .Select(_redactor.Redact)
        .ToHashSet(StringComparer.OrdinalIgnoreCase),
    Machine = run.Machine with
    {
      OperatingSystem = _redactor.Redact(run.Machine.OperatingSystem),
      Architecture = _redactor.Redact(run.Machine.Architecture),
      ComputerName = _redactor.Redact(run.Machine.ComputerName),
      UserName = _redactor.Redact(run.Machine.UserName)
    },
    Graph = run.Graph is null ? null : Redact(run.Graph),
    Plan = run.Plan is null ? null : Redact(run.Plan),
    ResourceResults = run.ResourceResults.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => Redact(pair.Value),
        StringComparer.OrdinalIgnoreCase),
    RestartReasons = run.RestartReasons.Select(_redactor.Redact).ToArray()
  };

  private ResourceResult Redact(ResourceResult result) => result with
  {
    ResourceId = _redactor.Redact(result.ResourceId),
    DetectedBefore = result.DetectedBefore is null ? null : Redact(result.DetectedBefore),
    DetectedAfter = result.DetectedAfter is null ? null : Redact(result.DetectedAfter),
    Message = result.Message is null ? null : _redactor.Redact(result.Message),
    Error = result.Error is null ? null : _redactor.Redact(result.Error),
    StepResults = result.StepResults.Select(Redact).ToArray()
  };

  private StepResult Redact(StepResult result) => result with
  {
    StepId = _redactor.Redact(result.StepId),
    Name = _redactor.Redact(result.Name),
    Error = result.Error is null ? null : _redactor.Redact(result.Error)
  };

  private DetectedState Redact(DetectedState state) => state with
  {
    ResourceId = _redactor.Redact(state.ResourceId),
    Version = state.Version is null ? null : _redactor.Redact(state.Version),
    ConfigurationHash = state.ConfigurationHash is null
        ? null
        : _redactor.Redact(state.ConfigurationHash),
    Evidence = state.Evidence.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => _redactor.Redact(pair.Value),
        StringComparer.OrdinalIgnoreCase),
    Error = state.Error is null ? null : _redactor.Redact(state.Error),
    StructuredError = state.StructuredError is null ? null : _redactor.Redact(state.StructuredError)
  };

  private ResourceGraph Redact(ResourceGraph graph) => graph with
  {
    Nodes = graph.Nodes.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => pair.Value with
        {
          Definition = Redact(pair.Value.Definition),
          RequiredBy = pair.Value.RequiredBy.Select(_redactor.Redact)
              .ToHashSet(StringComparer.OrdinalIgnoreCase)
        },
        StringComparer.OrdinalIgnoreCase),
    TopologicalLayers = graph.TopologicalLayers.Select(layer => layer with
    {
      ResourceIds = layer.ResourceIds.Select(_redactor.Redact).ToArray()
    }).ToArray()
  };

  private ExecutionPlan Redact(ExecutionPlan plan) => plan with
  {
    Fingerprint = _redactor.Redact(plan.Fingerprint),
    ProfileId = _redactor.Redact(plan.ProfileId),
    ProfileVersion = _redactor.Redact(plan.ProfileVersion),
    Layers = plan.Layers.Select(layer => layer with
    {
      ResourceIds = layer.ResourceIds.Select(_redactor.Redact).ToArray()
    }).ToArray(),
    Resources = plan.Resources.Select(resource => resource with
    {
      Definition = Redact(resource.Definition),
      Dependencies = resource.Dependencies.Select(_redactor.Redact).ToArray(),
      ResourcePlan = Redact(resource.ResourcePlan),
      Reason = resource.Reason is null ? null : _redactor.Redact(resource.Reason),
      BlockedBy = resource.BlockedBy.Select(_redactor.Redact).ToArray(),
      Diagnostics = resource.Diagnostics.Select(_redactor.Redact).ToArray()
    }).ToArray(),
    Errors = plan.Errors.Select(_redactor.Redact).ToArray()
  };

  private ResourcePlan Redact(ResourcePlan plan) => plan with
  {
    ResourceId = _redactor.Redact(plan.ResourceId),
    ResourceType = _redactor.Redact(plan.ResourceType),
    ProviderName = _redactor.Redact(plan.ProviderName),
    DesiredStateFingerprint = _redactor.Redact(plan.DesiredStateFingerprint),
    Steps = plan.Steps.Select(step => step with
    {
      Id = _redactor.Redact(step.Id),
      Description = _redactor.Redact(step.Description),
      Reason = step.Reason is null ? null : _redactor.Redact(step.Reason)
    }).ToArray(),
    Error = plan.Error is null ? null : _redactor.Redact(plan.Error),
    StructuredErrors = plan.StructuredErrors.Select(_redactor.Redact).ToArray()
  };

  private ResourceDefinition Redact(ResourceDefinition definition) => definition with
  {
    Id = _redactor.Redact(definition.Id),
    Type = _redactor.Redact(definition.Type),
    Provider = _redactor.Redact(definition.Provider),
    DisplayName = definition.DisplayName is null ? null : _redactor.Redact(definition.DisplayName),
    VersionConstraint = definition.VersionConstraint is null
        ? null
        : _redactor.Redact(definition.VersionConstraint),
    PreferredVersion = definition.PreferredVersion is null
        ? null
        : _redactor.Redact(definition.PreferredVersion),
    Dependencies = definition.Dependencies.Select(_redactor.Redact).ToArray(),
    Parameters = definition.Parameters.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => pair.Value is null ? null : _redactor.Redact(pair.Value),
        StringComparer.OrdinalIgnoreCase)
  };

  private sealed class ReadOnlyStringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
  {
    public override IReadOnlySet<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
      if (reader.TokenType != JsonTokenType.StartArray)
      {
        throw new JsonException("A string array was expected.");
      }

      var values = new List<string>();
      while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
      {
        if (reader.TokenType != JsonTokenType.String)
        {
          throw new JsonException("A string set cannot contain non-string values.");
        }

        values.Add(reader.GetString()!);
      }

      if (reader.TokenType != JsonTokenType.EndArray)
      {
        throw new JsonException("The string array was not terminated.");
      }

      return values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlySet<string> value,
        JsonSerializerOptions options)
    {
      writer.WriteStartArray();
      foreach (var item in value)
      {
        writer.WriteStringValue(item);
      }

      writer.WriteEndArray();
    }
  }
}
