using Wdem.Core.Runs;

namespace Wdem.Core.Workflows;

public sealed class TaskWorkflowState
{
  public TaskWorkflowState(
      string id,
      TaskExecutionState taskState,
      IEnumerable<WorkflowActivity>? entryActivities = null,
      IEnumerable<WorkflowActivity>? residenceActivities = null,
      IEnumerable<WorkflowActivity>? exitActivities = null,
      IEnumerable<TaskWorkflowTransition>? transitions = null,
      TaskOutcome? terminalOutcome = null,
      string? displayName = null,
      string? terminalError = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    Id = id;
    DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
    TaskState = taskState;
    EntryActivities = (entryActivities ?? []).ToArray();
    ResidenceActivities = (residenceActivities ?? []).ToArray();
    ExitActivities = (exitActivities ?? []).ToArray();
    Transitions = (transitions ?? []).ToArray();
    TerminalOutcome = terminalOutcome;
    TerminalError = terminalError;
  }

  public string Id { get; }

  public string DisplayName { get; }

  public TaskExecutionState TaskState { get; }

  public IReadOnlyList<WorkflowActivity> EntryActivities { get; }

  public IReadOnlyList<WorkflowActivity> ResidenceActivities { get; }

  public IReadOnlyList<WorkflowActivity> ExitActivities { get; }

  public IReadOnlyList<TaskWorkflowTransition> Transitions { get; }

  public TaskOutcome? TerminalOutcome { get; }

  public string? TerminalError { get; }

  public bool IsTerminal => TerminalOutcome is not null;

  public int ActivityCount =>
      EntryActivities.Count + ResidenceActivities.Count + ExitActivities.Count;
}
