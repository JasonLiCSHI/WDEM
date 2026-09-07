using Wdem.Application.Runtime;
using Wdem.Application.Profiles;
using Wdem.Application.Workflows;
using Wdem.Domain.Planning;
using Wdem.Domain.Profiles;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

public sealed class ApplyPlanHandler(
    ITaskRuntime runtime,
    IWorkflowActivityExecutor activityExecutor,
    ITaskWorkflowProvider workflowProvider,
    ProfileExecutionAuthorizer authorizer)
{
  public WorkflowSnapshot CreateReadySnapshot(EnvironmentProfile profile)
  {
    ArgumentNullException.ThrowIfNull(profile);
    var workflows = CreateWorkflows(profile);
    return WorkflowStateStore.CreateReadySnapshot(profile, workflows);
  }

  public EnvironmentRun Start(
      LoadedProfile loadedProfile,
      Plan plan,
      IProgress<WorkflowProgress>? progress = null,
      IProgress<WorkflowUpdate>? updates = null)
  {
    ArgumentNullException.ThrowIfNull(loadedProfile);
    ArgumentNullException.ThrowIfNull(plan);
    ArgumentNullException.ThrowIfNull(runtime);
    authorizer.EnsureTrusted(loadedProfile);
    var profile = loadedProfile.Profile;

    var workflows = CreateWorkflows(profile);

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
        activityExecutor,
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

  private Dictionary<string, TaskWorkflowDefinition> CreateWorkflows(
      EnvironmentProfile profile)
  {
    return profile.Tasks.Values.ToDictionary(
        task => task.Id,
        task => workflowProvider.Create(task) ??
            throw new InvalidOperationException($"Workflow provider returned no definition for task '{task.Id}'."),
        StringComparer.Ordinal);
  }
}
