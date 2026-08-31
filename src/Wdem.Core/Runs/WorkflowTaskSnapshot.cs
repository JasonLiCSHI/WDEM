namespace Wdem.Core.Runs;

public sealed record WorkflowTaskSnapshot(
    string TaskId,
    TaskExecutionState State,
    string? Stage,
    int Percent,
    TaskOutcome? Outcome,
    bool IsPlanned,
    int ActivityIndex,
    int ActivityCount,
    TaskCapabilities Capabilities)
{
  public bool IsTerminal => State is
      TaskExecutionState.NotSelected or
      TaskExecutionState.Satisfied or
      TaskExecutionState.Succeeded or
      TaskExecutionState.Failed or
      TaskExecutionState.Cancelled or
      TaskExecutionState.Blocked;

  public bool CanStart => Capabilities.CanStart;

  public bool CanCancel => Capabilities.CanCancel;

  public bool CanSelect => Capabilities.CanSelect;
}
