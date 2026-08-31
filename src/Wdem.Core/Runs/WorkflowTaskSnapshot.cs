namespace Wdem.Core.Runs;

using Wdem.Core.Workflows;

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
  public string? RuntimeStateId { get; init; }

  public string? ActivityId { get; init; }

  public WorkflowActivityLocation? ActivityLocation { get; init; }

  public bool IsTerminal => State == TaskExecutionState.NotSelected || Outcome is not null;

  public bool CanStart => Capabilities.CanStart;

  public bool CanCancel => Capabilities.CanCancel;

  public bool CanSelect => Capabilities.CanSelect;
}
