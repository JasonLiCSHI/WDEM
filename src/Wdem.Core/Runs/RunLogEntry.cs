using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Runs;

public sealed record RunLogEntry(
    long Sequence,
    DateTimeOffset TimestampUtc,
    ProviderLogLevel Level,
    string? ResourceId,
    string? StepId,
    string Message,
    StructuredError? Error = null,
    RunEventKind? Kind = null,
    double? Progress = null,
    ExecutionState? State = null,
    ExecutionOutcome? Outcome = null,
    RestartPolicy? RestartRequirement = null)
{
  public static RunLogEntry FromEvent(RunEvent runEvent, ProviderLogLevel level)
  {
    ArgumentNullException.ThrowIfNull(runEvent);
    return new RunLogEntry(
        runEvent.Sequence,
        runEvent.TimestampUtc,
        level,
        runEvent.ResourceId,
        runEvent.StepId,
        runEvent.Message,
        runEvent.Error,
        runEvent.Kind,
        runEvent.Progress,
        runEvent.State,
        runEvent.Outcome,
        runEvent.RestartRequirement);
  }

  public RunEvent ToEvent(Guid runId) => new(
      runId,
      Sequence,
      TimestampUtc,
      Kind ?? RunEventKind.Log,
      ResourceId,
      StepId,
      Progress,
      Message,
      Error,
      State,
      Outcome,
      RestartRequirement);
}
