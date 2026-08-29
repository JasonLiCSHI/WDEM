using System.Text.Json;
using Wdem.Core.Execution;
using Wdem.Core.Runs;
using Wdem.Windows.Composition;
using Wdem.Windows.Persistence;

namespace Wdem.Cli;

public sealed class WdemCommandHandler : IWdemCommandHandler
{
  private readonly IEnvironmentRunService _environmentRuns;
  private readonly IExecutionRunStore _runStore;
  private readonly TextWriter _output;
  private readonly TextWriter _error;
  private readonly LogRedactor _redactor;
  private readonly IRunEventSink _eventSink;

  public WdemCommandHandler(
      IEnvironmentRunService environmentRuns,
      IExecutionRunStore runStore,
      TextWriter? output,
      TextWriter? error,
      LogRedactor redactor,
      IRunEventSink eventSink)
  {
    _environmentRuns = environmentRuns ?? throw new ArgumentNullException(nameof(environmentRuns));
    _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    _output = output ?? Console.Out;
    _error = error ?? Console.Error;
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
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
        composition.EnvironmentRuns,
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
          cancellationToken).ConfigureAwait(false);

  public Task<int> ApplyAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.ApplyAsync(request, cancellationToken),
          json,
          cancellationToken);

  public Task<int> RetryAsync(
      Guid runId,
      IReadOnlySet<string> resourceIds,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RetryAsync(runId, resourceIds, cancellationToken),
          json,
          cancellationToken);

  public Task<int> ResumeAsync(
      Guid runId,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RecoverAsync(runId, cancellationToken),
          json,
          cancellationToken,
          replayPersistedEventsWhenSilent: true);

  public async Task<int> ListRunsAsync(
      bool json,
      CancellationToken cancellationToken)
  {
    try
    {
      var runs = await _runStore.ListAsync(cancellationToken).ConfigureAwait(false);
      foreach (var run in runs.OrderByDescending(run => run.StartedAtUtc))
      {
        await WriteEventAsync(new RunEvent(
            run.RunId,
            1,
            run.EndedAtUtc ?? run.StartedAtUtc,
            RunEventKind.RunStateChanged,
            null,
            null,
            null,
            $"{run.Mode} {run.State} {run.Outcome} {run.ProfileId}",
            null), json).ConfigureAwait(false);
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
            diagnostic), json).ConfigureAwait(false);
      }

      return DiagnosticsExitCode(diagnostics);
    }
    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
    {
      await WriteExceptionAsync(exception, json, cancelled: true).ConfigureAwait(false);
      return 130;
    }
    catch (Exception exception)
    {
      await WriteExceptionAsync(exception, json, cancelled: false).ConfigureAwait(false);
      return 1;
    }
  }

  private async Task<int> ExecuteRunAsync(
      Func<Task<ExecutionRun>> operation,
      bool json,
      CancellationToken cancellationToken,
      bool replayPersistedEventsWhenSilent = false)
  {
    try
    {
      var observedEventCount = 0;
      using var subscription = _eventSink.SubscribeRequired(
          async (runEvent, _) =>
          {
            await WriteEventAsync(runEvent, json).ConfigureAwait(false);
            Interlocked.Increment(ref observedEventCount);
          });
      var run = await operation().ConfigureAwait(false);
      if (replayPersistedEventsWhenSilent && Volatile.Read(ref observedEventCount) == 0)
      {
        await ReplayPersistedEventsAsync(run.RunId, json, cancellationToken)
            .ConfigureAwait(false);
      }

      return ExitCode(run);
    }
    catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
    {
      await WriteExceptionAsync(exception, json, cancelled: true).ConfigureAwait(false);
      return 130;
    }
    catch (Exception exception)
    {
      await WriteExceptionAsync(exception, json, cancelled: false).ConfigureAwait(false);
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

        await WriteEventAsync(new RunEvent(
            runId,
            entry.Sequence,
            entry.TimestampUtc,
            entry.Kind ?? RunEventKind.Log,
            entry.ResourceId,
            entry.StepId,
            entry.Progress,
            entry.Message,
            entry.Error), json).ConfigureAwait(false);
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
      TextWriter? writer = null)
  {
    var redacted = _redactor.Redact(runEvent);
    return (writer ?? _output).WriteLineAsync(json
        ? JsonSerializer.Serialize(redacted, WdemJson.Options)
        : FormatEvent(redacted));
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

  internal static Task WriteExceptionEventAsync(
      Exception exception,
      bool json,
      bool cancelled,
      TextWriter writer,
      LogRedactor? redactor = null,
      WdemErrorCode? errorCode = null)
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
    return writer.WriteLineAsync(json
        ? JsonSerializer.Serialize(runEvent, WdemJson.Options)
        : FormatEvent(runEvent));
  }

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
      return 2;
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
