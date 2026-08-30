using System.Collections.Concurrent;
using System.Text.Json;
using Wdem.Core.Execution;
using Wdem.Core.Reporting;
using Wdem.Core.Runs;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;

namespace Wdem.Cli;

public sealed class WdemCommandHandler : IWdemCommandHandler
{
  private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan ReportWriteTimeout = TimeSpan.FromSeconds(30);
  private readonly ICommandLineEnvironmentRunService _environmentRuns;
  private readonly IExecutionRunStore _runStore;
  private readonly TextWriter _output;
  private readonly TextWriter _error;
  private readonly LogRedactor _redactor;
  private readonly IRunEventSink _eventSink;
  private readonly TimeSpan _writeTimeout;
  private readonly IRunReportExporter _reportExporter;

  public WdemCommandHandler(
      ICommandLineEnvironmentRunService environmentRuns,
      IExecutionRunStore runStore,
      TextWriter? output,
      TextWriter? error,
      LogRedactor redactor,
      IRunEventSink eventSink,
      TimeSpan? writeTimeout = null,
      IRunReportExporter? reportExporter = null)
  {
    _environmentRuns = environmentRuns ?? throw new ArgumentNullException(nameof(environmentRuns));
    _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    _output = output ?? Console.Out;
    _error = error ?? Console.Error;
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    _reportExporter = reportExporter ?? new RunReportExporter(redactor);
    _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
    if (_writeTimeout <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(writeTimeout));
    }
  }

  public static async Task<WdemCommandHandler> CreateAsync(
      string profilesDirectory,
      WdemDataPaths? paths = null,
      TextWriter? output = null,
      TextWriter? error = null,
      CancellationToken cancellationToken = default,
      LogRedactor? redactor = null,
      IRunEventSink? eventSink = null)
  {
    redactor ??= new LogRedactor();
    eventSink ??= new RunEventHub();
    var composition = await WdemWindowsFactory.CreateAsync(
        profilesDirectory,
        paths,
        cancellationToken,
        redactor,
        eventSink).ConfigureAwait(false);
    return new WdemCommandHandler(
        composition.CommandLineRuns,
        composition.RunStore,
        output,
        error,
        composition.Redactor,
        composition.RunEvents);
  }

  public async Task<int> InspectAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken) => await ExecuteRunAsync(
          () => _environmentRuns.InspectAsync(request, cancellationToken),
          json,
          reportFile: null,
          cancellationToken).ConfigureAwait(false);

  public Task<int> InspectAsync(
      RunRequest request,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.InspectAsync(request, cancellationToken),
          json,
          reportFile,
          cancellationToken);

  public Task<int> ApplyAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.ApplyAsync(request, cancellationToken),
          json,
          reportFile: null,
          cancellationToken);

  public Task<int> ApplyAsync(
      RunRequest request,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.ApplyAsync(request, cancellationToken),
          json,
          reportFile,
          cancellationToken);

  public Task<int> RetryAsync(
      Guid runId,
      IReadOnlySet<string> resourceIds,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RetryAsync(runId, resourceIds, cancellationToken),
          json,
          reportFile: null,
          cancellationToken);

  public Task<int> RetryAsync(
      Guid runId,
      IReadOnlySet<string> resourceIds,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RetryAsync(runId, resourceIds, cancellationToken),
          json,
          reportFile,
          cancellationToken);

  public Task<int> ResumeAsync(
      Guid runId,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RecoverAsync(runId, cancellationToken),
          json,
          reportFile: null,
          cancellationToken,
          replayPersistedEventsWhenSilent: true);

  public Task<int> ResumeAsync(
      Guid runId,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RecoverAsync(runId, cancellationToken),
          json,
          reportFile,
          cancellationToken,
          replayPersistedEventsWhenSilent: true);

  public async Task<int> AbandonAsync(
      Guid runId,
      bool json,
      CancellationToken cancellationToken)
  {
    try
    {
      await _environmentRuns.AbandonAsync(runId, cancellationToken).ConfigureAwait(false);
      await WriteEventAsync(
          new RunEvent(
              runId,
              1,
              DateTimeOffset.UtcNow,
              RunEventKind.RunStateChanged,
              null,
              null,
              null,
              "Recovery candidate abandoned.",
              null),
          json,
          cancellationToken: cancellationToken).ConfigureAwait(false);
      return 0;
    }
    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
    {
      await TryWriteExceptionAsync(exception, json, cancelled: true).ConfigureAwait(false);
      return 130;
    }
    catch (Exception exception)
    {
      await TryWriteExceptionAsync(exception, json, cancelled: false).ConfigureAwait(false);
      return 1;
    }
  }

  public async Task<int> ListRunsAsync(
      bool json,
      CancellationToken cancellationToken)
  {
    try
    {
      var runs = await _runStore.ListAsync(cancellationToken).ConfigureAwait(false);
      var recoveryCandidates = await _environmentRuns
          .FindRecoveryCandidatesAsync(cancellationToken)
          .ConfigureAwait(false);
      var recoveryByRunId = recoveryCandidates
          .GroupBy(candidate => candidate.RunId)
          .ToDictionary(group => group.Key, group => group.First());
      foreach (var run in runs.OrderByDescending(run => run.StartedAtUtc))
      {
        bool recoverable = recoveryByRunId.TryGetValue(run.RunId, out var candidate);
        string pending = recoverable
            ? string.Join(
                ",",
                candidate!.PendingResourceIds.OrderBy(
                    id => id,
                    StringComparer.OrdinalIgnoreCase))
            : "none";
        await WriteEventAsync(new RunEvent(
            run.RunId,
            1,
            run.EndedAtUtc ?? run.StartedAtUtc,
            RunEventKind.RunStateChanged,
            null,
            null,
            null,
            $"{run.Mode} {run.State} {run.Outcome} {run.ProfileId} " +
                $"recoverable={recoverable.ToString().ToLowerInvariant()} pending={pending}",
            null), json, cancellationToken: cancellationToken).ConfigureAwait(false);
      }

      var diagnostics = _runStore.Diagnostics;
      for (var index = 0; index < diagnostics.Count; index++)
      {
        var diagnostic = diagnostics[index];
        await WriteEventAsync(new RunEvent(
            Guid.Empty,
            index + 1,
            DateTimeOffset.UtcNow,
            RunEventKind.Log,
            diagnostic.ResourceId,
            diagnostic.StepId,
            null,
            diagnostic.Summary,
            diagnostic), json, cancellationToken: cancellationToken).ConfigureAwait(false);
      }

      return DiagnosticsExitCode(diagnostics);
    }
    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
    {
      await TryWriteExceptionAsync(exception, json, cancelled: true).ConfigureAwait(false);
      return 130;
    }
    catch (Exception exception)
    {
      await TryWriteExceptionAsync(exception, json, cancelled: false).ConfigureAwait(false);
      return 1;
    }
  }

  private async Task<int> ExecuteRunAsync(
      Func<Task<ExecutionRun>> operation,
      bool json,
      string? reportFile,
      CancellationToken cancellationToken,
      bool replayPersistedEventsWhenSilent = false)
  {
    try
    {
      if (reportFile is not null)
      {
        reportFile = RunReportExporter.ValidateFilePath(reportFile);
      }

      var observedRunIds = new ConcurrentDictionary<Guid, byte>();
      using var subscription = _eventSink.SubscribeRequiredScoped(
          async (runEvent, observerCancellationToken) =>
          {
            await WriteEventAsync(
                runEvent,
                json,
                cancellationToken: observerCancellationToken).ConfigureAwait(false);
            observedRunIds.TryAdd(runEvent.RunId, 0);
          });
      var run = await operation().ConfigureAwait(false);
      if (reportFile is not null)
      {
        using var reportCancellation = new CancellationTokenSource(ReportWriteTimeout);
        await _reportExporter.ExportAsync(run, reportFile, reportCancellation.Token)
            .ConfigureAwait(false);
      }

      if (replayPersistedEventsWhenSilent && !observedRunIds.ContainsKey(run.RunId))
      {
        await ReplayPersistedEventsAsync(run.RunId, json, cancellationToken)
            .ConfigureAwait(false);
      }

      return ExitCode(run);
    }
    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
    {
      await TryWriteExceptionAsync(exception, json, cancelled: true).ConfigureAwait(false);
      return 130;
    }
    catch (Exception exception)
    {
      await TryWriteExceptionAsync(exception, json, cancelled: false).ConfigureAwait(false);
      return 1;
    }
  }

  private async Task ReplayPersistedEventsAsync(
      Guid runId,
      bool json,
      CancellationToken cancellationToken)
  {
    const int pageSize = 256;
    var afterSequence = 0L;
    while (true)
    {
      var page = await _runStore.ReadLogPageAsync(
          runId,
          afterSequence,
          pageSize,
          cancellationToken).ConfigureAwait(false);
      if (page.Count == 0)
      {
        return;
      }

      foreach (var entry in page)
      {
        if (entry.Sequence <= afterSequence)
        {
          throw new InvalidDataException("Persisted run events are not in sequence order.");
        }

        await WriteEventAsync(
            entry.ToEvent(runId),
            json,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        afterSequence = entry.Sequence;
      }

      if (page.Count < pageSize)
      {
        return;
      }
    }
  }

  private Task WriteEventAsync(
      RunEvent runEvent,
      bool json,
      TextWriter? writer = null,
      CancellationToken cancellationToken = default)
  {
    var redacted = _redactor.Redact(runEvent);
    return WriteLineWithDeadlineAsync(
        writer ?? _output,
        json
            ? JsonSerializer.Serialize(redacted, WdemJson.Options)
            : FormatEvent(redacted),
        cancellationToken,
        _writeTimeout);
  }

  private Task WriteExceptionAsync(
      Exception exception,
      bool json,
      bool cancelled) => WriteExceptionEventAsync(
          exception,
          json,
          cancelled,
          _error,
          _redactor);

  private async Task TryWriteExceptionAsync(
      Exception exception,
      bool json,
      bool cancelled)
  {
    try
    {
      await WriteExceptionEventAsync(
          exception,
          json,
          cancelled,
          _error,
          _redactor,
          writeTimeout: _writeTimeout).ConfigureAwait(false);
    }
    catch (Exception)
    {
      // Failure reporting must not prevent a bounded command exit.
    }
  }

  internal static Task WriteExceptionEventAsync(
      Exception exception,
      bool json,
      bool cancelled,
      TextWriter writer,
      LogRedactor? redactor = null,
      WdemErrorCode? errorCode = null,
      TimeSpan? writeTimeout = null)
  {
    ArgumentNullException.ThrowIfNull(exception);
    ArgumentNullException.ThrowIfNull(writer);
    var error = new StructuredError(
        errorCode ?? (cancelled ? WdemErrorCode.CancellationError : WdemErrorCode.ProviderError),
        cancelled ? "Operation cancelled." : "Unexpected host error.",
        exception.Message)
    {
      IsRetryable = false,
      UnderlyingException = exception
    };
    var runEvent = (redactor ?? new LogRedactor()).Redact(new RunEvent(
        Guid.Empty,
        1,
        DateTimeOffset.UtcNow,
        RunEventKind.Log,
        null,
        null,
        null,
        exception.Message,
        error));
    return WriteLineWithDeadlineAsync(
        writer,
        json
            ? JsonSerializer.Serialize(runEvent, WdemJson.Options)
            : FormatEvent(runEvent),
        CancellationToken.None,
        writeTimeout ?? DefaultWriteTimeout);
  }

  private static async Task WriteLineWithDeadlineAsync(
      TextWriter writer,
      string value,
      CancellationToken cancellationToken,
      TimeSpan timeout)
  {
    var pendingWrite = writer.WriteLineAsync(value.AsMemory(), cancellationToken);
    try
    {
      await pendingWrite.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
    catch
    {
      if (!pendingWrite.IsCompleted)
      {
        ObserveFault(pendingWrite);
      }

      throw;
    }
  }

  private static void ObserveFault(Task task) => _ = task.ContinueWith(
      static completed => _ = completed.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);

  private static string FormatEvent(RunEvent runEvent)
  {
    var resource = runEvent.ResourceId is null ? string.Empty : $" [{runEvent.ResourceId}]";
    return $"{runEvent.TimestampUtc:O} {runEvent.RunId:D} {runEvent.Kind}{resource}: {runEvent.Message}";
  }

  private static int ExitCode(ExecutionRun run)
  {
    if (run.Outcome == ExecutionOutcome.Cancelled)
    {
      return 130;
    }

    var planErrors = run.Plan?.Errors ?? [];
    if (planErrors.Count > 0)
    {
      return DiagnosticsExitCode(planErrors);
    }

    if (run.Plan is { IsExecutable: false })
    {
      return 3;
    }

    if (run.State != ExecutionState.Completed ||
        run.ResourceResults.Values.Any(result =>
            result.State == ExecutionState.Blocked ||
            result.Outcome == ExecutionOutcome.Failed))
    {
      return 3;
    }

    return run.Outcome == ExecutionOutcome.Succeeded ? 0 : 3;
  }

  private static int DiagnosticsExitCode(IReadOnlyList<StructuredError> diagnostics)
  {
    if (diagnostics.Count == 0)
    {
      return 0;
    }

    return diagnostics.Any(diagnostic =>
        diagnostic.Code is not WdemErrorCode.ProfileError and not WdemErrorCode.DependencyError)
        ? 3
        : 2;
  }
}
