namespace Wdem.Core.Runs;

public sealed record WorkflowSnapshot(
    long Revision,
    WorkflowRunState State,
    IReadOnlyDictionary<string, WorkflowTaskSnapshot> Tasks)
{
  public bool IsCompleted => State == WorkflowRunState.Completed;

  public bool CanStartAny => Tasks.Values.Any(task => task.CanStart);

  public bool CanCancelAny => Tasks.Values.Any(task => task.CanCancel);

  public bool CanSelectAny => Tasks.Values.Any(task => task.CanSelect);
}
