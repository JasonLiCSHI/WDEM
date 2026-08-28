using Wdem.Core.Execution;

namespace Wdem.Core.Runs;

public sealed record StepResult
{
  private double _progress;

  public required string StepId { get; init; }
  public required string Name { get; init; }
  public required ExecutionState State { get; init; }
  public ExecutionOutcome? Outcome { get; init; }
  public double Progress
  {
    get => _progress;
    init => _progress = RunProgress.Normalize(value);
  }

  public long FirstLogSequence { get; init; }
  public long LastLogSequence { get; init; }
  public int? ProcessExitCode { get; init; }
  public DateTimeOffset? StartedAtUtc { get; init; }
  public DateTimeOffset? EndedAtUtc { get; init; }
  public StructuredError? Error { get; init; }
}
