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
  /// <summary>
  /// Runs a sequence of workflow activities in order, stopping on first failure.
  /// </summary>
  /// <param name="task">The task definition being executed.</param>
  /// <param name="runtimeState">The current workflow runtime state.</param>
  /// <param name="activities">The sequence of activities to execute.</param>
  /// <param name="location">The activity location (Entry/Residence/Exit).</param>
  /// <param name="journal">The execution journal recording all activity results.</param>
  /// <param name="results">Output list to record all activity results.</param>
  /// <param name="cancellationToken">Token to stop execution if requested.</param>
  /// <returns>True if all activities succeeded; false if any activity failed.</returns>
  /// <exception cref="OperationCanceledException">Thrown if execution is cancelled.</exception>
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
      catch (OperationCanceledException)
      {
        // Cancellation is handled by the state machine; re-throw without modification.
        throw;
      }
      catch (Exception exception)
      {
        // Log the activity failure and publish the event before re-throwing.
        // This ensures diagnostics are recorded even when activity execution fails.
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

/// <summary>
/// Records the journal of activities executed during a task's workflow.
/// </summary>
internal sealed class TaskWorkflowJournal
{
  private readonly List<StepReport> _steps = [];
  private int _activityIndex;

  /// <summary>Gets the immutable list of recorded steps.</summary>
  public IReadOnlyList<StepReport> Steps => _steps;

  /// <summary>
  /// Increments the activity counter and returns the new index.
  /// </summary>
  /// <returns>The 1-based activity index.</returns>
  public int BeginActivity() => ++_activityIndex;

  /// <summary>
  /// Records an activity result in the journal if it is not null.
  /// </summary>
  /// <param name="step">The step report to record, or null to skip recording.</param>
  public void Record(StepReport? step)
  {
    if (step is not null)
    {
      _steps.Add(step);
    }
  }
}
