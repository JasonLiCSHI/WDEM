using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runtime;

namespace Wdem.Core.Runs;

public static class EnvironmentManager
{
  public static WorkflowSnapshot CreateReadySnapshot(EnvironmentProfile profile) =>
      WorkflowStateStore.CreateReadySnapshot(profile);

  public static EnvironmentRun StartApply(
      EnvironmentProfile profile,
      TaskGraph graph,
      ITaskRuntime runtime,
      IProgress<WorkflowProgress>? progress = null,
      IProgress<WorkflowUpdate>? updates = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(graph);
    ArgumentNullException.ThrowIfNull(runtime);

    var perTaskCts = graph.OrderedTaskIds.ToDictionary(
        taskId => taskId,
        _ => new CancellationTokenSource(),
        StringComparer.Ordinal);
    var allCts = new CancellationTokenSource();
    var state = new WorkflowStateStore(profile, graph, progress, updates);
    var machine = new WorkflowStateMachine(
        profile,
        graph,
        runtime,
        perTaskCts,
        allCts.Token,
        state);

    return new EnvironmentRun(
        machine.RunAsync(),
        allCts,
        perTaskCts,
        state);
  }
}
