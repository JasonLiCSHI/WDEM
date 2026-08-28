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

  public WdemCommandHandler(
      IEnvironmentRunService environmentRuns,
      IExecutionRunStore runStore,
      TextWriter? output = null,
      TextWriter? error = null)
  {
    _environmentRuns = environmentRuns ?? throw new ArgumentNullException(nameof(environmentRuns));
    _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    _output = output ?? Console.Out;
    _error = error ?? Console.Error;
  }

  public static async Task<WdemCommandHandler> CreateAsync(
      string profilesDirectory,
      WdemDataPaths? paths = null,
      TextWriter? output = null,
      TextWriter? error = null,
      CancellationToken cancellationToken = default)
  {
    var composition = await WdemWindowsFactory.CreateAsync(
        profilesDirectory,
        paths,
        cancellationToken).ConfigureAwait(false);
    return new WdemCommandHandler(
        composition.EnvironmentRuns,
        composition.RunStore,
        output,
        error);
  }

  public async Task<int> InspectAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken) => await ExecuteRunAsync(
          () => _environmentRuns.InspectAsync(request, cancellationToken),
          json).ConfigureAwait(false);

  public Task<int> ApplyAsync(
      RunRequest request,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.ApplyAsync(request, cancellationToken),
          json);

  public Task<int> RetryAsync(
      Guid runId,
      IReadOnlySet<string> resourceIds,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RetryAsync(runId, resourceIds, cancellationToken),
          json);

  public Task<int> ResumeAsync(
      Guid runId,
      bool json,
      CancellationToken cancellationToken) => ExecuteRunAsync(
          () => _environmentRuns.RecoverAsync(runId, cancellationToken),
          json);

  public async Task<int> ListRunsAsync(
      bool json,
      CancellationToken cancellationToken)
  {
    try
    {
      var runs = await _runStore.ListAsync(cancellationToken).ConfigureAwait(false);
      foreach (var run in runs.OrderByDescending(run => run.StartedAtUtc))
      {
        await _output.WriteLineAsync(json
            ? JsonSerializer.Serialize(run)
            : $"{run.RunId:D} {run.Mode} {run.State} {run.Outcome} {run.ProfileId}")
            .ConfigureAwait(false);
      }

      return 0;
    }
    catch (OperationCanceledException)
    {
      return 130;
    }
    catch (Exception exception)
    {
      await _error.WriteLineAsync(exception.Message).ConfigureAwait(false);
      return 1;
    }
  }

  private async Task<int> ExecuteRunAsync(
      Func<Task<ExecutionRun>> operation,
      bool json)
  {
    try
    {
      var run = await operation().ConfigureAwait(false);
      await WriteRunEventsAsync(run, json).ConfigureAwait(false);
      return ExitCode(run);
    }
    catch (OperationCanceledException)
    {
      return 130;
    }
    catch (ArgumentException exception)
    {
      await _error.WriteLineAsync(exception.Message).ConfigureAwait(false);
      return 2;
    }
    catch (Exception exception)
    {
      await _error.WriteLineAsync(exception.Message).ConfigureAwait(false);
      return 1;
    }
  }

  private async Task WriteRunEventsAsync(ExecutionRun run, bool json)
  {
    long sequence = 0;
    foreach (var diagnostic in run.Plan?.Errors ?? [])
    {
      await WriteEventAsync(new RunEvent(
          run.RunId,
          ++sequence,
          run.EndedAtUtc ?? run.StartedAtUtc,
          RunEventKind.Log,
          diagnostic.ResourceId,
          diagnostic.StepId,
          null,
          diagnostic.Summary,
          diagnostic), json).ConfigureAwait(false);
    }

    foreach (var result in run.ResourceResults.Values.OrderBy(
        result => result.ResourceId,
        StringComparer.OrdinalIgnoreCase))
    {
      var runEvent = new RunEvent(
          run.RunId,
          ++sequence,
          result.EndedAtUtc ?? result.StartedAtUtc ?? run.StartedAtUtc,
          RunEventKind.ResourceStateChanged,
          result.ResourceId,
          null,
          result.Progress,
          result.Message ?? result.Error?.Summary ?? result.Outcome?.ToString() ?? result.State.ToString(),
          result.Error);
      await WriteEventAsync(runEvent, json).ConfigureAwait(false);
    }

    await WriteEventAsync(new RunEvent(
        run.RunId,
        ++sequence,
        run.EndedAtUtc ?? DateTimeOffset.UtcNow,
        RunEventKind.Completed,
        null,
        null,
        1,
        run.Outcome?.ToString() ?? run.State.ToString(),
        null), json).ConfigureAwait(false);
  }

  private Task WriteEventAsync(RunEvent runEvent, bool json) => json
      ? _output.WriteLineAsync(JsonSerializer.Serialize(runEvent))
      : _output.WriteLineAsync(FormatEvent(runEvent));

  private static string FormatEvent(RunEvent runEvent)
  {
    var resource = runEvent.ResourceId is null ? string.Empty : $" [{runEvent.ResourceId}]";
    return $"{runEvent.TimestampUtc:O} {runEvent.Kind}{resource}: {runEvent.Message}";
  }

  private static int ExitCode(ExecutionRun run)
  {
    if (run.Outcome == ExecutionOutcome.Cancelled)
    {
      return 130;
    }

    if (run.Plan is { IsExecutable: false } || run.Plan?.Errors.Count > 0)
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
}
