using Wdem.Domain.Execution;
using Wdem.Domain.Planning;
using Wdem.Domain.Profiles;
using Wdem.Domain.Tasks;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

/// <summary>
/// Schedules selected Tasks according to the Profile DAG and prevents unsafe
/// downstream execution after dependency failure or cancellation.
/// </summary>
internal sealed class WorkflowDagScheduler(
    EnvironmentProfile profile,
    IReadOnlyList<PlannedTask> plannedTasks,
    IReadOnlyDictionary<string, TaskWorkflowDefinition> workflows,
    IReadOnlyDictionary<string, CancellationTokenSource> taskCancellationSources,
    WorkflowStateMachine stateMachine,
    CancellationToken allCancellationToken)
{
  public async Task<RunReport> RunAsync()
  {
    var plannedTaskIds = plannedTasks.Select(task => task.Id.Value).ToArray();
    var scheduledTasks = new Dictionary<string, Task<TaskReport>>(StringComparer.Ordinal);

    foreach (var plannedTask in plannedTasks)
    {
      var taskId = plannedTask.Id.Value;
      var task = profile.Tasks[taskId];
      var dependencies = task.DependsOn
          .Select(dependencyId => scheduledTasks[dependencyId])
          .ToArray();
      scheduledTasks.Add(taskId, RunAfterDependenciesAsync(
          task,
          plannedTask.Action,
          workflows[taskId],
          dependencies,
          taskCancellationSources[taskId].Token));
    }

    var reports = await Task.WhenAll(
        plannedTaskIds.Select(taskId => scheduledTasks[taskId]));
    return new RunReport(plannedTaskIds
        .Zip(reports)
        .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal));
  }

  private async Task<TaskReport> RunAfterDependenciesAsync(
      TaskDefinition task,
      PlannedTaskAction action,
      TaskWorkflowDefinition workflow,
      IReadOnlyList<Task<TaskReport>> dependencyTasks,
      CancellationToken taskCancellationToken)
  {
    // Let every ready DAG node be scheduled before a synchronous Activity can
    // occupy the caller's thread.
    await Task.Yield();

    if (action == PlannedTaskAction.Blocked)
    {
      return stateMachine.CompleteWithoutRunning(
          task.Id,
          TaskOutcome.Blocked,
          "Planning was blocked because detection failed for this Task or one of its dependencies.");
    }

    var dependencies = await Task.WhenAll(dependencyTasks);
    if (allCancellationToken.IsCancellationRequested || taskCancellationToken.IsCancellationRequested)
    {
      return stateMachine.CompleteWithoutRunning(task.Id, TaskOutcome.Cancelled);
    }

    if (IsBlockedByDependency(dependencies))
    {
      return stateMachine.CompleteWithoutRunning(task.Id, TaskOutcome.Blocked);
    }

    return await stateMachine.RunAsync(task, workflow, taskCancellationToken);
  }

  private static bool IsBlockedByDependency(IEnumerable<TaskReport> dependencies) =>
      dependencies.Any(dependency =>
          dependency.Outcome is TaskOutcome.Failed or
              TaskOutcome.Cancelled or
              TaskOutcome.Blocked);
}
