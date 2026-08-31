namespace Wdem.Core.Runs;

public sealed record TaskReport(
    string TaskId,
    TaskOutcome Outcome,
    IReadOnlyList<StepReport> Steps,
    string? Error)
{
  public TaskExecutionState State => Outcome switch
  {
    TaskOutcome.Succeeded => TaskExecutionState.Succeeded,
    TaskOutcome.NotRequired => TaskExecutionState.Satisfied,
    TaskOutcome.Failed => TaskExecutionState.Failed,
    TaskOutcome.Cancelled => TaskExecutionState.Cancelled,
    TaskOutcome.Blocked => TaskExecutionState.Blocked,
    _ => TaskExecutionState.NotSelected
  };
}
