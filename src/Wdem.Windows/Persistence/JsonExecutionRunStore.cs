using System.Buffers.Binary;
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
  private const int SharingViolationHResult = unchecked((int)0x80070020);
  private const int LockViolationHResult = unchecked((int)0x80070021);
  private const int MaximumLogPageSize = 1000;
  private const int LogIndexRecordSize = sizeof(long) * 3;
  private static readonly TimeSpan MaximumClaimClockSkew = TimeSpan.FromMinutes(5);
  private static readonly UTF8Encoding Utf8WithoutBom = new(false);
  private readonly object _diagnosticsGate = new();
  private readonly object _logIndexGate = new();
  private readonly List<StructuredError> _diagnostics = [];
  private readonly Dictionary<string, ValidatedLogIndex> _validatedLogIndexes =
      new(StringComparer.OrdinalIgnoreCase);
  private readonly WdemDataPaths _paths;
  private readonly LogRedactor _redactor;
  private readonly JsonSerializerOptions _snapshotJsonOptions;
  private readonly JsonSerializerOptions _logJsonOptions;
  private readonly Func<string, IAsyncDisposable> _recoveryLockOpener;
  private readonly TimeProvider _timeProvider;

  public JsonExecutionRunStore(
      WdemDataPaths paths,
      LogRedactor redactor,
      TimeProvider? timeProvider = null)
      : this(paths, redactor, OpenRecoveryLock, timeProvider)
  {
  }

  internal JsonExecutionRunStore(
      WdemDataPaths paths,
      LogRedactor redactor,
      Func<string, IAsyncDisposable> recoveryLockOpener,
      TimeProvider? timeProvider = null)
  {
    _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    _recoveryLockOpener = recoveryLockOpener ??
        throw new ArgumentNullException(nameof(recoveryLockOpener));
    _snapshotJsonOptions = CreateJsonOptions(writeIndented: true);
    _logJsonOptions = CreateJsonOptions(writeIndented: false);
    _timeProvider = timeProvider ?? TimeProvider.System;
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

  public string LogIndexPath(Guid runId) => LogPath(runId) + ".index";

  public async Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    ValidateRunForPersistence(run);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockAsync(run.RunId, cancellationToken)
        .ConfigureAwait(false);
    var path = SnapshotPath(run.RunId);
    if (File.Exists(path))
    {
      throw new InvalidOperationException($"Execution run '{run.RunId:D}' already exists.");
    }

    await WriteSnapshotAsync(path, Redact(run), cancellationToken).ConfigureAwait(false);
  }

  public async Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!Directory.Exists(_paths.RunsDirectory))
    {
      return null;
    }

    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken)
        .ConfigureAwait(false);
    return await ReadSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
  }

  public async Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(
      CancellationToken cancellationToken)
  {
    var runs = await ListAsync(cancellationToken).ConfigureAwait(false);
    return runs.Where(run => run.State != ExecutionState.Completed).ToArray();
  }

  public async Task<IReadOnlyList<ExecutionRun>> ListAsync(
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
    var runs = new List<ExecutionRun>();
    foreach (var runId in runIds)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var run = await GetAsync(runId, cancellationToken).ConfigureAwait(false);
      if (run is not null)
      {
        runs.Add(run);
      }
    }

    return runs;
  }

  public Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
      Guid runId,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    Directory.CreateDirectory(_paths.RunsDirectory);
    var lockPath = Path.Combine(_paths.RunsDirectory, $"{runId:D}.recovery.lock");
    try
    {
      var lease = _recoveryLockOpener(lockPath);
      return Task.FromResult<IAsyncDisposable?>(lease);
    }
    catch (IOException exception) when (IsRecoveryLockBusy(exception))
    {
      return Task.FromResult<IAsyncDisposable?>(null);
    }
    catch (IOException exception)
    {
      var diagnostic = new StructuredError(
          WdemErrorCode.DetectionError,
          "Recovery operation lock could not be acquired.",
          $"Run '{runId:D}' recovery lock failed: {exception.Message}")
      {
        IsRetryable = false
      };
      lock (_diagnosticsGate)
      {
        _diagnostics.Add(diagnostic);
      }

      throw;
    }
  }

  private static bool IsRecoveryLockBusy(IOException exception) =>
      exception.HResult is SharingViolationHResult or LockViolationHResult;

  private static IAsyncDisposable OpenRecoveryLock(string lockPath) =>
      new FileStream(
          lockPath,
          FileMode.OpenOrCreate,
          FileAccess.ReadWrite,
          FileShare.None,
          1,
          FileOptions.Asynchronous);

  public async Task<ExecutionRun> SaveAsync(
      ExecutionRun run,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    ValidateRunForPersistence(run);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        run.RunId,
        cancellationToken)
        .ConfigureAwait(false);
    var path = SnapshotPath(run.RunId);
    EnsureRunExists(run.RunId, path);
    var current = await ReadSnapshotAsync(run.RunId, cancellationToken).ConfigureAwait(false) ??
        throw new KeyNotFoundException($"Execution run '{run.RunId:D}' does not exist.");
    if (current.Revision != run.Revision ||
        current.RecoveryClaimId != run.RecoveryClaimId)
    {
      throw new InvalidOperationException(
          $"Execution run '{run.RunId:D}' revision or recovery claim is stale.");
    }

    var saved = run with { Revision = checked(run.Revision + 1) };
    await WriteSnapshotAsync(path, Redact(saved), cancellationToken).ConfigureAwait(false);
    return saved;
  }

  public async Task<bool> TrySaveAsync(
      ExecutionRun run,
      long expectedRevision,
      Guid? expectedRecoveryClaimId,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(run);
    if (expectedRevision < 0 ||
        expectedRevision == long.MaxValue ||
        run.Revision != expectedRevision + 1)
    {
      throw new ArgumentOutOfRangeException(
          nameof(expectedRevision),
          expectedRevision,
          "The replacement run revision must immediately follow the expected revision.");
    }

    ValidateRunForPersistence(run);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        run.RunId,
        cancellationToken)
        .ConfigureAwait(false);
    var path = SnapshotPath(run.RunId);
    EnsureRunExists(run.RunId, path);
    var current = await ReadSnapshotAsync(run.RunId, cancellationToken).ConfigureAwait(false) ??
        throw new KeyNotFoundException($"Execution run '{run.RunId:D}' does not exist.");
    if (current.Revision != expectedRevision ||
        current.RecoveryClaimId != expectedRecoveryClaimId)
    {
      return false;
    }

    await WriteSnapshotAsync(path, Redact(run), cancellationToken).ConfigureAwait(false);
    return true;
  }

  public async Task AppendLogAsync(
      Guid runId,
      RunLogEntry entry,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(entry);
    ValidateLogEntryForPersistence(entry);
    cancellationToken.ThrowIfCancellationRequested();
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken)
        .ConfigureAwait(false);
    EnsureRunExists(runId, SnapshotPath(runId));
    var logPath = LogPath(runId);
    var index = await ReconcileLogIndexAsync(logPath, cancellationToken)
        .ConfigureAwait(false);
    if (entry.Sequence <= index.LastSequence)
    {
      throw new InvalidOperationException(
          $"Log sequence {entry.Sequence} must be greater than {index.LastSequence} for run '{runId:D}'.");
    }

    var persistedEntry = _redactor.Redact(entry);
    var line = JsonSerializer.Serialize(persistedEntry, _logJsonOptions) + "\n";
    var bytes = Utf8WithoutBom.GetBytes(line);
    await using var stream = new FileStream(
        logPath,
        FileMode.Append,
        FileAccess.Write,
        FileShare.Read,
        4096,
        FileOptions.Asynchronous | FileOptions.WriteThrough);
    var startOffset = stream.Length;
    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    stream.Flush(flushToDisk: true);
    await AppendLogIndexRecordAsync(
        logPath,
        new LogIndexRecord(entry.Sequence, startOffset, stream.Length),
        cancellationToken).ConfigureAwait(false);
    RememberValidatedLogIndex(
        logPath,
        new LogIndexState(index.Count + 1, entry.Sequence, stream.Length));
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
    await using var runLock = await AcquireRunLockForExistingSnapshotAsync(
        runId,
        cancellationToken)
        .ConfigureAwait(false);
    EnsureRunExists(runId, SnapshotPath(runId));
    var logPath = LogPath(runId);
    if (!File.Exists(logPath))
    {
      return [];
    }

    var index = await ReconcileLogIndexAsync(logPath, cancellationToken)
        .ConfigureAwait(false);
    return await ReadIndexedLogPageAsync(
        logPath,
        index.Count,
        afterSequence,
        take,
        cancellationToken).ConfigureAwait(false);
  }

  private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
  {
    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      PropertyNameCaseInsensitive = true,
      RespectNullableAnnotations = true,
      RespectRequiredConstructorParameters = true,
      WriteIndented = writeIndented
    };
    options.Converters.Add(new JsonStringEnumConverter(
        namingPolicy: null,
        allowIntegerValues: false));
    options.Converters.Add(new ReadOnlyStringSetJsonConverter());
    return options;
  }

  private async Task<FileStream> AcquireRunLockAsync(
      Guid runId,
      CancellationToken cancellationToken,
      bool deleteOnClose = false)
  {
    Directory.CreateDirectory(_paths.RunsDirectory);
    var lockPath = Path.Combine(_paths.RunsDirectory, $"{runId:D}.lock");
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.Asynchronous
                | (deleteOnClose ? FileOptions.DeleteOnClose : FileOptions.None));
      }
      catch (IOException)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
            .ConfigureAwait(false);
      }
    }
  }

  private Task<FileStream> AcquireRunLockForExistingSnapshotAsync(
      Guid runId,
      CancellationToken cancellationToken) =>
      AcquireRunLockAsync(
          runId,
          cancellationToken,
          deleteOnClose: !File.Exists(SnapshotPath(runId)));

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

      ValidateRun(run);
      return SnapshotRestoredRun(run);
    }
    catch (Exception exception) when (
        exception is JsonException
            or NotSupportedException
            or InvalidOperationException
            or ArgumentException)
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
    var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
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

  private async Task<LogIndexState> ReconcileLogIndexAsync(
      string logPath,
      CancellationToken cancellationToken)
  {
    if (!File.Exists(logPath))
    {
      return new LogIndexState(0, 0, 0);
    }

    await using var log = new FileStream(
        logPath,
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.Read,
        4096,
        FileOptions.Asynchronous | FileOptions.WriteThrough);
    var indexPath = logPath + ".index";
    var state = await ValidateLogIndexAsync(log, indexPath, cancellationToken)
        .ConfigureAwait(false);
    if (state is null)
    {
      return await RebuildLogIndexAsync(log, indexPath, cancellationToken)
          .ConfigureAwait(false);
    }

    log.Position = state.Value.IndexedLength;
    await using var index = new FileStream(
        indexPath,
        FileMode.OpenOrCreate,
        FileAccess.Write,
        FileShare.Read,
        4096,
        FileOptions.Asynchronous | FileOptions.WriteThrough);
    index.Position = state.Value.Count * LogIndexRecordSize;
    var reconciled = await AppendLogTailToIndexAsync(
        log,
        index,
        state.Value,
        logPath,
        cancellationToken).ConfigureAwait(false);
    await FlushLogAndIndexAsync(log, index, cancellationToken).ConfigureAwait(false);
    RememberValidatedLogIndex(logPath, reconciled);
    return reconciled;
  }

  private async Task<LogIndexState?> ValidateLogIndexAsync(
      FileStream log,
      string indexPath,
      CancellationToken cancellationToken)
  {
    if (!File.Exists(indexPath))
    {
      return new LogIndexState(0, 0, 0);
    }

    var fingerprint = GetLogIndexFingerprint(indexPath);
    lock (_logIndexGate)
    {
      if (_validatedLogIndexes.TryGetValue(indexPath, out var validated)
          && validated.Fingerprint == fingerprint
          && validated.State.IndexedLength <= log.Length)
      {
        return validated.State;
      }
    }

    if (fingerprint.Length % LogIndexRecordSize != 0)
    {
      return null;
    }

    await using var index = new FileStream(
        indexPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        4096,
        FileOptions.Asynchronous | FileOptions.RandomAccess);
    var count = index.Length / LogIndexRecordSize;
    var lastSequence = 0L;
    var indexedLength = 0L;
    for (var ordinal = 0L; ordinal < count; ordinal++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var record = await ReadLogIndexRecordAsync(index, ordinal, cancellationToken)
          .ConfigureAwait(false);
      if (record.Sequence <= lastSequence
          || record.StartOffset != indexedLength
          || record.EndOffset <= record.StartOffset
          || record.EndOffset > log.Length
          || record.EndOffset - record.StartOffset > int.MaxValue)
      {
        return null;
      }

      var bytes = new byte[(int)(record.EndOffset - record.StartOffset)];
      log.Position = record.StartOffset;
      await log.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
      if (bytes[^1] != (byte)'\n')
      {
        return null;
      }

      RunLogEntry entry;
      try
      {
        entry = DeserializeLogEntry(DecodeLogLine(bytes), log.Name);
      }
      catch (InvalidDataException)
      {
        return null;
      }

      if (entry.Sequence != record.Sequence)
      {
        return null;
      }

      lastSequence = record.Sequence;
      indexedLength = record.EndOffset;
    }

    return new LogIndexState(count, lastSequence, indexedLength);
  }

  private async Task<LogIndexState> RebuildLogIndexAsync(
      FileStream log,
      string indexPath,
      CancellationToken cancellationToken)
  {
    var temporaryPath = $"{indexPath}.{Guid.NewGuid():N}.tmp";
    try
    {
      log.Position = 0;
      LogIndexState rebuilt;
      await using (var temporaryIndex = new FileStream(
          temporaryPath,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          4096,
          FileOptions.Asynchronous | FileOptions.WriteThrough))
      {
        rebuilt = await AppendLogTailToIndexAsync(
            log,
            temporaryIndex,
            new LogIndexState(0, 0, 0),
            log.Name,
            cancellationToken).ConfigureAwait(false);
        await FlushLogAndIndexAsync(log, temporaryIndex, cancellationToken)
            .ConfigureAwait(false);
      }

      cancellationToken.ThrowIfCancellationRequested();
      if (File.Exists(indexPath))
      {
        File.Replace(temporaryPath, indexPath, destinationBackupFileName: null);
      }
      else
      {
        File.Move(temporaryPath, indexPath);
      }

      RememberValidatedLogIndex(log.Name, rebuilt);
      return rebuilt;
    }
    finally
    {
      if (File.Exists(temporaryPath))
      {
        File.Delete(temporaryPath);
      }
    }
  }

  private async Task<LogIndexState> AppendLogTailToIndexAsync(
      FileStream log,
      FileStream index,
      LogIndexState initialState,
      string logPath,
      CancellationToken cancellationToken)
  {
    var count = initialState.Count;
    var lastSequence = initialState.LastSequence;
    var indexedLength = initialState.IndexedLength;
    while (log.Position < log.Length)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var startOffset = log.Position;
      var (lineBytes, terminated) = ReadRawLogLine(log, cancellationToken);
      if (lineBytes.Length == 0 && !terminated)
      {
        break;
      }

      RunLogEntry entry;
      try
      {
        entry = DeserializeLogEntry(DecodeLogLine(lineBytes), logPath);
      }
      catch (InvalidDataException) when (!terminated)
      {
        log.SetLength(startOffset);
        break;
      }

      if (entry.Sequence <= lastSequence)
      {
        throw new InvalidDataException(
            $"Run log '{logPath}' contains a non-increasing sequence.");
      }

      if (!terminated)
      {
        log.Position = log.Length;
        await log.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
      }

      var record = new LogIndexRecord(entry.Sequence, startOffset, log.Position);
      await WriteLogIndexRecordAsync(index, record, cancellationToken).ConfigureAwait(false);
      count++;
      lastSequence = entry.Sequence;
      indexedLength = record.EndOffset;
    }

    return new LogIndexState(count, lastSequence, indexedLength);
  }

  private static async Task FlushLogAndIndexAsync(
      FileStream log,
      FileStream index,
      CancellationToken cancellationToken)
  {
    await log.FlushAsync(cancellationToken).ConfigureAwait(false);
    log.Flush(flushToDisk: true);
    await index.FlushAsync(cancellationToken).ConfigureAwait(false);
    index.Flush(flushToDisk: true);
  }

  private void RememberValidatedLogIndex(string logPath, LogIndexState state)
  {
    var indexPath = logPath + ".index";
    var validated = new ValidatedLogIndex(GetLogIndexFingerprint(indexPath), state);
    lock (_logIndexGate)
    {
      _validatedLogIndexes[indexPath] = validated;
    }
  }

  private static LogIndexFingerprint GetLogIndexFingerprint(string indexPath)
  {
    var info = new FileInfo(indexPath);
    info.Refresh();
    return new LogIndexFingerprint(info.Length, info.LastWriteTimeUtc.Ticks);
  }

  private async Task<IReadOnlyList<RunLogEntry>> ReadIndexedLogPageAsync(
      string logPath,
      long recordCount,
      long afterSequence,
      int take,
      CancellationToken cancellationToken)
  {
    await using var index = new FileStream(
        logPath + ".index",
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        4096,
        FileOptions.Asynchronous | FileOptions.RandomAccess);
    var low = 0L;
    var high = recordCount;
    while (low < high)
    {
      var middle = low + ((high - low) / 2);
      var record = await ReadLogIndexRecordAsync(index, middle, cancellationToken)
          .ConfigureAwait(false);
      if (record.Sequence <= afterSequence)
      {
        low = middle + 1;
      }
      else
      {
        high = middle;
      }
    }

    var entries = new List<RunLogEntry>(take);
    await using var log = new FileStream(
        logPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        4096,
        FileOptions.Asynchronous | FileOptions.RandomAccess);
    for (var ordinal = low; ordinal < recordCount && entries.Count < take; ordinal++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var record = await ReadLogIndexRecordAsync(index, ordinal, cancellationToken)
          .ConfigureAwait(false);
      var byteCount = record.EndOffset - record.StartOffset;
      if (byteCount is <= 0 or > int.MaxValue)
      {
        throw new InvalidDataException($"Run log index for '{logPath}' is invalid.");
      }

      var bytes = new byte[(int)byteCount];
      log.Position = record.StartOffset;
      await log.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
      var entry = DeserializeLogEntry(DecodeLogLine(bytes), logPath);
      if (entry.Sequence != record.Sequence)
      {
        throw new InvalidDataException($"Run log index for '{logPath}' is inconsistent.");
      }

      entries.Add(entry);
    }

    return entries;
  }

  private static (byte[] Bytes, bool Terminated) ReadRawLogLine(
      FileStream stream,
      CancellationToken cancellationToken)
  {
    using var line = new MemoryStream();
    while (true)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var value = stream.ReadByte();
      if (value < 0)
      {
        return (line.ToArray(), false);
      }

      if (value == '\n')
      {
        return (line.ToArray(), true);
      }

      line.WriteByte((byte)value);
    }
  }

  private static string DecodeLogLine(byte[] bytes)
  {
    var length = bytes.Length;
    while (length > 0 && bytes[length - 1] is (byte)'\r' or (byte)'\n')
    {
      length--;
    }

    return Utf8WithoutBom.GetString(bytes, 0, length);
  }

  private static async Task AppendLogIndexRecordAsync(
      string logPath,
      LogIndexRecord record,
      CancellationToken cancellationToken)
  {
    await using var index = new FileStream(
        logPath + ".index",
        FileMode.Append,
        FileAccess.Write,
        FileShare.Read,
        4096,
        FileOptions.Asynchronous | FileOptions.WriteThrough);
    await WriteLogIndexRecordAsync(index, record, cancellationToken).ConfigureAwait(false);
    await index.FlushAsync(cancellationToken).ConfigureAwait(false);
    index.Flush(flushToDisk: true);
  }

  private static async Task<LogIndexRecord> ReadLogIndexRecordAsync(
      FileStream index,
      long ordinal,
      CancellationToken cancellationToken)
  {
    var bytes = new byte[LogIndexRecordSize];
    index.Position = ordinal * LogIndexRecordSize;
    await index.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
    return new LogIndexRecord(
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0, sizeof(long))),
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(sizeof(long), sizeof(long))),
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(sizeof(long) * 2, sizeof(long))));
  }

  private static async Task WriteLogIndexRecordAsync(
      FileStream index,
      LogIndexRecord record,
      CancellationToken cancellationToken)
  {
    var bytes = new byte[LogIndexRecordSize];
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0, sizeof(long)), record.Sequence);
    BinaryPrimitives.WriteInt64LittleEndian(
        bytes.AsSpan(sizeof(long), sizeof(long)),
        record.StartOffset);
    BinaryPrimitives.WriteInt64LittleEndian(
        bytes.AsSpan(sizeof(long) * 2, sizeof(long)),
        record.EndOffset);
    await index.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
  }

  private RunLogEntry DeserializeLogEntry(string line, string logPath)
  {
    try
    {
      var entry = JsonSerializer.Deserialize<RunLogEntry>(line, _logJsonOptions)
          ?? throw new JsonException("The log entry was null.");
      ValidateLogEntryForPersistence(entry);
      return entry;
    }
    catch (Exception exception) when (exception is JsonException or ArgumentException)
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

  private void ValidateRunForPersistence(ExecutionRun run)
  {
    ValidateRun(run);

    if (run.RecoveryClaimedAtUtc is { } claimedAt)
    {
      var now = _timeProvider.GetUtcNow();
      if (claimedAt > now && claimedAt - now > MaximumClaimClockSkew)
      {
        throw new ArgumentException(
            "A recovery claim timestamp cannot be excessively far in the future.",
            nameof(run));
      }
    }
  }

  private static void ValidateRun(ExecutionRun run)
  {
    if (run.Revision < 0)
    {
      throw new ArgumentException(
          "An execution run revision cannot be negative.",
          nameof(run));
    }

    if (run.RecoveryClaimId.HasValue != run.RecoveryClaimedAtUtc.HasValue)
    {
      throw new ArgumentException(
          "A recovery claim requires both an identifier and a claim timestamp.",
          nameof(run));
    }

    ValidateEnum(run.Mode, "run mode");
    ValidateEnum(run.State, "run state");
    ValidateOptionalEnum(run.Outcome, "run outcome");
    ValidateElements(run.SelectedOptionalResourceIds, "selected optional resource identifiers");
    ValidateElements(
        run.AcknowledgedRestartResourceIds,
        "acknowledged restart resource identifiers");
    ValidateElements(run.ResourceResults.Values, "resource results");
    ValidateElements(run.RestartReasons, "restart reasons");
    foreach (var restartRequirement in run.RestartRequirements)
    {
      ValidateEnum(restartRequirement, "restart requirement");
    }

    if (run.Graph is not null)
    {
      ValidateGraph(run.Graph);
    }

    if (run.Plan is not null)
    {
      ValidatePlan(run.Plan);
    }

    foreach (var result in run.ResourceResults.Values)
    {
      ValidateResourceResult(result);
    }

    if (run.State == ExecutionState.Completed
        && (run.Outcome is null || run.EndedAtUtc is null))
    {
      throw new ArgumentException(
          "A completed execution run requires both an outcome and an end timestamp.",
          nameof(run));
    }

    if (run.State != ExecutionState.Completed
        && (run.Outcome is not null || run.EndedAtUtc is not null))
    {
      throw new ArgumentException(
          "An incomplete execution run cannot have an outcome or end timestamp.",
          nameof(run));
    }
  }

  private static void ValidateGraph(ResourceGraph graph)
  {
    ArgumentNullException.ThrowIfNull(graph.Nodes);
    ValidateElements(graph.Nodes.Values, "graph nodes");
    ValidateElements(graph.TopologicalLayers, "graph layers");
    foreach (var node in graph.Nodes.Values)
    {
      ValidateEnum(node.Origin, "resource origin");
      ValidateElements(node.RequiredBy, "resource dependants");
      ValidateDefinition(node.Definition);
    }

    foreach (var layer in graph.TopologicalLayers)
    {
      ValidateElements(layer.ResourceIds, "graph layer resource identifiers");
    }
  }

  private static void ValidatePlan(ExecutionPlan plan)
  {
    ValidateElements(plan.Layers, "plan layers");
    ValidateElements(plan.Resources, "planned resources");
    ValidateElements(plan.Errors, "plan errors");
    foreach (var layer in plan.Layers)
    {
      ValidateElements(layer.ResourceIds, "plan layer resource identifiers");
    }

    foreach (var resource in plan.Resources)
    {
      ValidateDefinition(resource.Definition);
      ValidateEnum(resource.Origin, "planned resource origin");
      ValidateEnum(resource.Status, "planned resource status");
      ValidateEnum(resource.Risk, "plan risk");
      ValidateEnum(resource.RestartPolicy, "planned resource restart policy");
      ValidateElements(resource.Dependencies, "planned resource dependencies");
      ValidateElements(resource.BlockedBy, "blocking resource identifiers");
      ValidateElements(resource.Diagnostics, "planned resource diagnostics");
      foreach (var diagnostic in resource.Diagnostics)
      {
        ValidateStructuredError(diagnostic);
      }

      ValidateResourcePlan(resource.ResourcePlan);
    }

    foreach (var error in plan.Errors)
    {
      ValidateStructuredError(error);
    }
  }

  private static void ValidateResourcePlan(ResourcePlan plan)
  {
    ArgumentNullException.ThrowIfNull(plan);
    ValidateEnum(plan.Compliance, "resource plan compliance");
    ValidateElements(plan.Steps, "plan steps");
    ValidateElements(plan.StructuredErrors, "resource plan errors");
    foreach (var step in plan.Steps)
    {
      ValidateEnum(step.Action, "plan action");
      ValidateEnum(step.PrivilegeRequirement, "plan step privilege requirement");
      ValidateEnum(step.RestartPolicy, "plan step restart policy");
    }

    foreach (var error in plan.StructuredErrors)
    {
      ValidateStructuredError(error);
    }
  }

  private static void ValidateDefinition(ResourceDefinition definition)
  {
    ArgumentNullException.ThrowIfNull(definition);
    ValidateEnum(definition.PrivilegeRequirement, "resource privilege requirement");
    ValidateEnum(definition.RestartPolicy, "resource restart policy");
    ValidateElements(definition.Dependencies, "resource dependencies");
  }

  private static void ValidateResourceResult(ResourceResult result)
  {
    ValidateEnum(result.State, "resource result state");
    ValidateOptionalEnum(result.Outcome, "resource result outcome");
    ValidateOptionalEnum(result.FinalCompliance, "final compliance");
    ValidateEnum(result.RestartRequirement, "resource restart requirement");
    ValidateElements(result.StepResults, "step results");
    if (result.DetectedBefore is not null)
    {
      ValidateDetectedState(result.DetectedBefore);
    }

    if (result.DetectedAfter is not null)
    {
      ValidateDetectedState(result.DetectedAfter);
    }

    if (result.Error is not null)
    {
      ValidateStructuredError(result.Error);
    }

    foreach (var step in result.StepResults)
    {
      ValidateEnum(step.State, "step result state");
      ValidateOptionalEnum(step.Outcome, "step result outcome");
      if (step.Error is not null)
      {
        ValidateStructuredError(step.Error);
      }
    }
  }

  private static void ValidateDetectedState(DetectedState state)
  {
    ValidateEnum(state.Outcome, "detection outcome");
    ValidateElements(state.InstalledVersions, "installed versions");
    if (state.StructuredError is not null)
    {
      ValidateStructuredError(state.StructuredError);
    }
  }

  private static void ValidateStructuredError(StructuredError error) =>
      ValidateEnum(error.Code, "structured error code");

  private static void ValidateLogEntryForPersistence(RunLogEntry entry)
  {
    ValidateEnum(entry.Level, "log level");
    if (entry.Error is not null)
    {
      ValidateStructuredError(entry.Error);
    }
  }

  private static void ValidateEnum<TEnum>(TEnum value, string field)
      where TEnum : struct, Enum
  {
    if (!Enum.IsDefined(value))
    {
      throw new ArgumentException($"The persisted {field} has an undefined value.");
    }
  }

  private static void ValidateOptionalEnum<TEnum>(TEnum? value, string field)
      where TEnum : struct, Enum
  {
    if (value is TEnum defined)
    {
      ValidateEnum(defined, field);
    }
  }

  private static void ValidateElements<T>(IEnumerable<T> values, string field)
  {
    ArgumentNullException.ThrowIfNull(values);
    if (values.Any(value => value is null))
    {
      throw new ArgumentException($"The persisted {field} cannot contain null elements.");
    }
  }

  private static ExecutionRun SnapshotRestoredRun(ExecutionRun run) => run with
  {
    Graph = run.Graph is null ? null : SnapshotRestoredGraph(run.Graph),
    Plan = run.Plan is null ? null : SnapshotRestoredPlan(run.Plan)
  };

  private static ResourceGraph SnapshotRestoredGraph(ResourceGraph graph) => graph with
  {
    Nodes = graph.Nodes.ToFrozenDictionary(
        pair => pair.Key,
        pair => pair.Value with
        {
          Definition = SnapshotRestoredDefinition(pair.Value.Definition),
          RequiredBy = pair.Value.RequiredBy.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
        },
        StringComparer.OrdinalIgnoreCase),
    TopologicalLayers = SnapshotRestoredLayers(graph.TopologicalLayers)
  };

  private static ExecutionPlan SnapshotRestoredPlan(ExecutionPlan plan) => plan with
  {
    Layers = SnapshotRestoredLayers(plan.Layers),
    Resources = ReadOnly(plan.Resources.Select(SnapshotRestoredResource)),
    Errors = ReadOnly(plan.Errors)
  };

  private static PlannedResource SnapshotRestoredResource(PlannedResource resource) =>
      resource with
      {
        Definition = SnapshotRestoredDefinition(resource.Definition),
        Dependencies = ReadOnly(resource.Dependencies),
        ResourcePlan = resource.ResourcePlan with
        {
          Steps = ReadOnly(resource.ResourcePlan.Steps),
          StructuredErrors = ReadOnly(resource.ResourcePlan.StructuredErrors)
        },
        BlockedBy = ReadOnly(resource.BlockedBy),
        Diagnostics = ReadOnly(resource.Diagnostics)
      };

  private static IReadOnlyList<ResourceGraphLayer> SnapshotRestoredLayers(
      IEnumerable<ResourceGraphLayer> layers) =>
      ReadOnly(layers.Select(layer => layer with
      {
        ResourceIds = ReadOnly(layer.ResourceIds)
      }));

  private static ResourceDefinition SnapshotRestoredDefinition(ResourceDefinition definition) =>
      definition with
      {
        Dependencies = ReadOnly(definition.Dependencies),
        Parameters = definition.Parameters.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
      };

  private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
      Array.AsReadOnly(values.ToArray());

  private ExecutionRun Redact(ExecutionRun run) => run with
  {
    ProfileSourcePath = _redactor.Redact(run.ProfileSourcePath),
    ProfileId = _redactor.Redact(run.ProfileId),
    ProfileVersion = _redactor.Redact(run.ProfileVersion),
    SelectedOptionalResourceIds = run.SelectedOptionalResourceIds
        .Select(_redactor.Redact)
        .ToHashSet(StringComparer.OrdinalIgnoreCase),
    AcknowledgedRestartResourceIds = run.AcknowledgedRestartResourceIds
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
        pair => _redactor.RedactNamedValue(pair.Key, pair.Value),
        StringComparer.OrdinalIgnoreCase)
  };

  private readonly record struct LogIndexState(
      long Count,
      long LastSequence,
      long IndexedLength);

  private readonly record struct LogIndexFingerprint(long Length, long LastWriteTicks);

  private readonly record struct ValidatedLogIndex(
      LogIndexFingerprint Fingerprint,
      LogIndexState State);

  private readonly record struct LogIndexRecord(
      long Sequence,
      long StartOffset,
      long EndOffset);

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
