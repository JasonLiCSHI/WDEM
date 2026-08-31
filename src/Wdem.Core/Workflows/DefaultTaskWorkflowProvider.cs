using Wdem.Core.Runs;
using Wdem.Core.Tasks;

namespace Wdem.Core.Workflows;

public sealed class DefaultTaskWorkflowProvider : ITaskWorkflowProvider
{
  public static DefaultTaskWorkflowProvider Instance { get; } = new();

  private DefaultTaskWorkflowProvider()
  {
  }

  public TaskWorkflowDefinition Create(TaskDefinition task)
  {
    ArgumentNullException.ThrowIfNull(task);
    if (task.Workflow is not null)
    {
      return task.Workflow;
    }

    var states = new List<TaskWorkflowState>();
    var afterDetect = task.Pre.Count > 0 ? "pre" : "apply";
    states.Add(new TaskWorkflowState(
        "detect",
        TaskExecutionState.Detecting,
        residenceActivities:
        [
          new CommandWorkflowActivity("detect", "detect", task.Detect)
        ],
        transitions:
        [
          TaskWorkflowTransition.WhenTaskSatisfied("satisfied"),
          TaskWorkflowTransition.Always(afterDetect)
        ],
        displayName: "detect"));

    if (task.Pre.Count > 0)
    {
      states.Add(new TaskWorkflowState(
          "pre",
          TaskExecutionState.RunningPre,
          residenceActivities: task.Pre.Select((command, index) =>
              new CommandWorkflowActivity($"pre-{index + 1}", "pre", command)),
          transitions:
          [
            TaskWorkflowTransition.WhenActivitiesSucceeded("apply"),
            TaskWorkflowTransition.Always("failed")
          ],
          displayName: "pre"));
    }

    WorkflowActivity applyActivity = task.Apply is null
        ? new MissingApplyActivity()
        : new CommandWorkflowActivity("apply", "apply", task.Apply);
    var afterApply = task.Post.Count > 0 ? "post" : "verify";
    states.Add(new TaskWorkflowState(
        "apply",
        TaskExecutionState.Applying,
        residenceActivities: [applyActivity],
        transitions:
        [
          TaskWorkflowTransition.WhenActivitiesSucceeded(afterApply),
          TaskWorkflowTransition.Always("failed")
        ],
        displayName: "apply"));

    if (task.Post.Count > 0)
    {
      states.Add(new TaskWorkflowState(
          "post",
          TaskExecutionState.RunningPost,
          residenceActivities: task.Post.Select((command, index) =>
              new CommandWorkflowActivity($"post-{index + 1}", "post", command)),
          transitions:
          [
            TaskWorkflowTransition.WhenActivitiesSucceeded("verify"),
            TaskWorkflowTransition.Always("failed")
          ],
          displayName: "post"));
    }

    states.Add(new TaskWorkflowState(
        "verify",
        TaskExecutionState.Verifying,
        residenceActivities:
        [
          new CommandWorkflowActivity("verify", "verify", task.Detect)
        ],
        transitions:
        [
          TaskWorkflowTransition.WhenTaskSatisfied("succeeded"),
          TaskWorkflowTransition.Always("failed")
        ],
        displayName: "verify"));
    states.Add(new TaskWorkflowState(
        "satisfied",
        TaskExecutionState.Satisfied,
        terminalOutcome: TaskOutcome.NotRequired,
        displayName: "satisfied"));
    states.Add(new TaskWorkflowState(
        "succeeded",
        TaskExecutionState.Succeeded,
        terminalOutcome: TaskOutcome.Succeeded,
        displayName: "succeeded"));
    states.Add(new TaskWorkflowState(
        "failed",
        TaskExecutionState.Failed,
        terminalOutcome: TaskOutcome.Failed,
        displayName: "failed",
        terminalError: "Task workflow failed."));
    return new TaskWorkflowDefinition("detect", states);
  }

  private sealed class MissingApplyActivity()
      : WorkflowActivity("apply", "apply")
  {
    public override Task<WorkflowActivityResult> ExecuteAsync(
        WorkflowActivityContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(WorkflowActivityResult.Failure("Task has no apply command."));
  }
}
