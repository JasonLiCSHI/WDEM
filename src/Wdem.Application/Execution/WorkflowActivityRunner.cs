using Wdem.Application.Events;
using Wdem.Application.Runtime;
using Wdem.Application.Workflows;
using Wdem.Domain.Events;
using Wdem.Domain.Tasks;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

/// <summary>
/// Executes one ordered Activity collection and records its observable effects.
/// </summary>
internal sealed class WorkflowActivityRunner(
    string profileId,
    ITaskRuntime runtime,
    IWorkflowActivityExecutor activityExecutor,
    WorkflowStateStore state,
    IDomainEventPublisher domainEvents)
{
  public async Task<bool> RunAsync(
      TaskDefinition task,
      TaskWorkflowState runtimeState,
      IReadOnlyList<WorkflowActivity> activities,
      WorkflowActivityLocation location,
      TaskWorkflowJournal journal,
      List<WorkflowActivityResult> results,
      CancellationToken cancellationToken)
  {
    foreach (var activity in activities)
    {
      var activityIndex = journal.BeginActivity();
      if (!state.BeginActivity(
          task.Id,
          runtimeState.Id,
          runtimeState.TaskState,
          activity,
          location,
          activityIndex))
      {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
      }
      domainEvents.Publish(new TaskWorkflowActivityStarted(
          profileId,
          task.Id,
          runtimeState.Id,
          activity.Id,
          location,
          activityIndex));

      cancellationToken.ThrowIfCancellationRequested();
      var context = new WorkflowActivityContext(
          task,
          runtimeState.Id,
          location,
          runtime,
          output => state.PublishOutput(task.Id, output.Message, output.Stream));
      WorkflowActivityResult result;
      try
      {
        result = await activityExecutor.ExecuteAsync(activity, context, cancellationToken)
            ?? throw new InvalidOperationException($"Activity '{activity.Id}' returned no result.");
      }
      catch (Exception exception) when (exception is not OperationCanceledException)
      {
        domainEvents.Publish(new TaskWorkflowActivityCompleted(
            profileId,
            task.Id,
            runtimeState.Id,
            activity.Id,
            location,
            activityIndex,
            Succeeded: false,
            IsTaskSatisfied: null,
            exception.Message));
        throw;
      }

      journal.Record(result.Step);
      results.Add(result);
      domainEvents.Publish(new TaskWorkflowActivityCompleted(
          profileId,
          task.Id,
          runtimeState.Id,
          activity.Id,
          location,
          activityIndex,
          result.Succeeded,
          result.IsTaskSatisfied,
          result.Error));
      cancellationToken.ThrowIfCancellationRequested();
      if (!result.Succeeded)
      {
        return false;
      }
    }

    return true;
  }
}

internal sealed class TaskWorkflowJournal
{
  private readonly List<StepReport> _steps = [];
  private int _activityIndex;

  public IReadOnlyList<StepReport> Steps => _steps;

  public int BeginActivity() => ++_activityIndex;

  public void Record(StepReport? step)
  {
    if (step is not null)
    {
      _steps.Add(step);
    }
  }
}
