using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runtime;
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
      TaskGraph graph,
      ITaskRuntime runtime,
      IProgress<WorkflowProgress>? progress = null,
      IProgress<WorkflowUpdate>? updates = null,
      ITaskWorkflowProvider? workflowProvider = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(graph);
    ArgumentNullException.ThrowIfNull(runtime);

    var workflows = CreateWorkflows(profile, workflowProvider);

    var perTaskCts = graph.OrderedTaskIds.ToDictionary(
        taskId => taskId,
        _ => new CancellationTokenSource(),
        StringComparer.Ordinal);
    var allCts = new CancellationTokenSource();
    var state = new WorkflowStateStore(profile, graph, workflows, progress, updates);
    var machine = new WorkflowStateMachine(
        profile,
        graph,
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
