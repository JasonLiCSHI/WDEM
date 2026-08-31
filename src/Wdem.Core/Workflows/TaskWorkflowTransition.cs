namespace Wdem.Core.Workflows;

public sealed class TaskWorkflowTransition
{
  private readonly Func<TaskWorkflowTransitionContext, bool> _condition;

  public TaskWorkflowTransition(
      string targetStateId,
      Func<TaskWorkflowTransitionContext, bool> condition,
      string? name = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(targetStateId);
    ArgumentNullException.ThrowIfNull(condition);
    TargetStateId = targetStateId;
    Name = string.IsNullOrWhiteSpace(name) ? $"to-{targetStateId}" : name;
    _condition = condition;
  }

  public string TargetStateId { get; }

  public string Name { get; }

  public bool IsMatch(TaskWorkflowTransitionContext context) => _condition(context);

  public static TaskWorkflowTransition Always(string targetStateId) =>
      new(targetStateId, _ => true, "always");

  public static TaskWorkflowTransition WhenActivitiesSucceeded(string targetStateId) =>
      new(targetStateId, context => context.ActivitiesSucceeded, "activities-succeeded");

  public static TaskWorkflowTransition WhenActivitiesFailed(string targetStateId) =>
      new(targetStateId, context => !context.ActivitiesSucceeded, "activities-failed");

  public static TaskWorkflowTransition WhenTaskSatisfied(string targetStateId) =>
      new(targetStateId, context => context.IsTaskSatisfied, "task-satisfied");

  public static TaskWorkflowTransition WhenTaskNotSatisfied(string targetStateId) =>
      new(targetStateId, context => !context.IsTaskSatisfied, "task-not-satisfied");
}
