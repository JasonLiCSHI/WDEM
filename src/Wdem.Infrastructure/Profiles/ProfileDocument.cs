namespace Wdem.Infrastructure.Profiles;

internal sealed class ProfileDocument
{
  public int? SchemaVersion { get; init; }

  public string? Id { get; init; }

  public string? Version { get; init; }

  public string? DisplayName { get; init; }

  public string? Description { get; init; }

  public Dictionary<string, TaskDocument?>? Tasks { get; init; }
}

internal sealed class TaskDocument
{
  public string? DisplayName { get; init; }

  public string? Description { get; init; }

  public bool Required { get; init; }

  public List<string?>? DependsOn { get; init; }

  public string? Version { get; init; }

  public string? PreferredVersion { get; init; }

  public string? Source { get; init; }

  public CommandDocument? Detect { get; init; }

  public List<CommandDocument?>? Pre { get; init; }

  public CommandDocument? Apply { get; init; }

  public List<CommandDocument?>? Post { get; init; }

  public TaskWorkflowDocument? Workflow { get; init; }
}

internal sealed class CommandDocument
{
  public string? DisplayName { get; init; }

  public string? Executable { get; init; }

  public List<string?>? Arguments { get; init; }

  public string? VersionPattern { get; init; }

  public List<int>? MissingExitCodes { get; init; }
}

internal sealed class TaskWorkflowDocument
{
  public string? InitialState { get; init; }

  public int? MaxTransitions { get; init; }

  public List<TaskWorkflowStateDocument?>? States { get; init; }
}

internal sealed class TaskWorkflowStateDocument
{
  public string? Id { get; init; }

  public string? DisplayName { get; init; }

  public string? TaskState { get; init; }

  public List<WorkflowActivityDocument?>? Entry { get; init; }

  public List<WorkflowActivityDocument?>? Residence { get; init; }

  public List<WorkflowActivityDocument?>? Exit { get; init; }

  public List<TaskWorkflowTransitionDocument?>? Transitions { get; init; }

  public string? Outcome { get; init; }

  public string? Error { get; init; }
}

internal sealed class WorkflowActivityDocument
{
  public string? Id { get; init; }

  public string? Phase { get; init; }

  public string? DisplayName { get; init; }

  public string? Executable { get; init; }

  public List<string?>? Arguments { get; init; }

  public string? VersionPattern { get; init; }

  public List<int>? MissingExitCodes { get; init; }
}

internal sealed class TaskWorkflowTransitionDocument
{
  public string? Target { get; init; }

  public string? Condition { get; init; }
}
