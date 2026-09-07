using Wdem.Core.Profiles;
using Wdem.Domain.Planning;
using Wdem.Application.Runtime;
using Wdem.Core.Workflows;

namespace Wdem.Core.Runs;

public static class EnvironmentManager
{
  public static WorkflowSnapshot CreateReadySnapshot(
      EnvironmentProfile profile,
      ITaskWorkflowProvider? workflowProvider = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    var workflows = CreateWorkflows(profile, workflowProvider);
    return WorkflowStateStore.CreateReadySnapshot(profile, workflows);
  }

  public static EnvironmentRun StartApply(
      EnvironmentProfile profile,
      Plan plan,
      ITaskRuntime runtime,
      IProgress<WorkflowProgress>? progress = null,
      IProgress<WorkflowUpdate>? updates = null,
      ITaskWorkflowProvider? workflowProvider = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(runtime);

    var workflows = CreateWorkflows(profile, workflowProvider);

    var plannedTaskIds = plan.Tasks.Select(task => task.Id.Value).ToArray();
    var perTaskCts = plannedTaskIds.ToDictionary(
        taskId => taskId,
        _ => new CancellationTokenSource(),
        StringComparer.Ordinal);
    var allCts = new CancellationTokenSource();
    var state = new WorkflowStateStore(profile, plannedTaskIds, workflows, progress, updates);
    var machine = new WorkflowStateMachine(
        profile,
        plannedTaskIds,
        runtime,
        workflows,
        perTaskCts,
        state,
        allCts.Token);

    return new EnvironmentRun(
        machine.RunAsync(),
        allCts,
        perTaskCts,
        state);
  }

  private static Dictionary<string, TaskWorkflowDefinition> CreateWorkflows(
      EnvironmentProfile profile,
      ITaskWorkflowProvider? workflowProvider)
  {
    var provider = workflowProvider ?? DefaultTaskWorkflowProvider.Instance;
    return profile.Tasks.Values.ToDictionary(
        task => task.Id,
        task => provider.Create(task) ??
            throw new InvalidOperationException($"Workflow provider returned no definition for task '{task.Id}'."),
        StringComparer.Ordinal);
  }
}
