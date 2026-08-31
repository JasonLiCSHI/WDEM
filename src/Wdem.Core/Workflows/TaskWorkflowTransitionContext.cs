using Wdem.Core.Runs;
using Wdem.Core.Tasks;

namespace Wdem.Core.Workflows;

public sealed class TaskWorkflowTransitionContext
{
  internal TaskWorkflowTransitionContext(
      TaskDefinition task,
      TaskWorkflowState state,
      IReadOnlyList<WorkflowActivityResult> activityResults)
  {
    Task = task;
    State = state;
    ActivityResults = activityResults;
  }

  public TaskDefinition Task { get; }

  public TaskWorkflowState State { get; }

  public IReadOnlyList<WorkflowActivityResult> ActivityResults { get; }

  public WorkflowActivityResult? LastResult => ActivityResults.LastOrDefault();

  public StepReport? LastStep => ActivityResults.LastOrDefault(result => result.Step is not null)?.Step;

  public bool ActivitiesSucceeded => ActivityResults.All(result => result.Succeeded);

  public bool IsTaskSatisfied =>
      ActivityResults.LastOrDefault(result => result.IsTaskSatisfied is not null)
          ?.IsTaskSatisfied == true;
}
