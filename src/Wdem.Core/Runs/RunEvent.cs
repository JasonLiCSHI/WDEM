using Wdem.Core.Execution;
using Wdem.Core.Resources;

namespace Wdem.Core.Runs;

public enum RunEventKind
{
  RunStateChanged,
  ResourceStateChanged,
  StepProgress,
  Log,
  Completed
}

public sealed record RunEvent(
    Guid RunId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    RunEventKind Kind,
    string? ResourceId,
    string? StepId,
    double? Progress,
    string Message,
    StructuredError? Error,
    ExecutionState? State = null,
    ExecutionOutcome? Outcome = null,
    RestartPolicy? RestartRequirement = null);
