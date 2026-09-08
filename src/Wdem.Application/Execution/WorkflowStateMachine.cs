using Wdem.Application.Events;
using Wdem.Application.Workflows;
using Wdem.Domain.Events;
using Wdem.Domain.Execution;
using Wdem.Domain.Tasks;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

/// <summary>
/// Executes one Task's state graph. Runtime state changes before the state's
/// Entry, Residence, or Exit Activities run and is then projected to Task state.
/// </summary>
internal sealed class WorkflowStateMachine(
    string profileId,
    WorkflowActivityRunner activityRunner,
    WorkflowStateStore state,
    IDomainEventPublisher domainEvents,
    CancellationToken allCancellationToken)
{
  public async Task<TaskReport> RunAsync(
      TaskDefinition task,
      TaskWorkflowDefinition workflow,
      CancellationToken taskCancellationToken)
  {
    var journal = new TaskWorkflowJournal();
    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
        allCancellationToken,
        taskCancellationToken);
    var token = linkedCancellation.Token;
    var transitionsTaken = 0;
    var runtimeStateId = workflow.InitialStateId;

    try
    {
      if (!state.MakeReady(task.Id))
      {
        token.ThrowIfCancellationRequested();
        throw new OperationCanceledException(token);
      }
      domainEvents.Publish(new TaskWorkflowStarted(
          profileId,
          task.Id,
          workflow.InitialStateId));

      while (true)
      {
        token.ThrowIfCancellationRequested();
        var runtimeState = workflow.States[runtimeStateId];
        var hasLifecycleActivities = runtimeState.ActivityCount > 0;

        if (runtimeState.IsTerminal && !hasLifecycleActivities)
        {
          return CompleteTerminalState(task.Id, runtimeState, journal.Steps);
        }

        EnterState(task.Id, runtimeState, token);

        var activityResults = new List<WorkflowActivityResult>();
        var entered = await activityRunner.RunAsync(
            task,
            runtimeState,
            runtimeState.EntryActivities,
            WorkflowActivityLocation.Entry,
            journal,
            activityResults,
            token);
        if (entered)
        {
          await activityRunner.RunAsync(
              task,
              runtimeState,
              runtimeState.ResidenceActivities,
              WorkflowActivityLocation.Residence,
              journal,
              activityResults,
              token);
        }

        if (runtimeState.IsTerminal)
        {
          return await CompleteTerminalStateAsync(
              task,
              runtimeState,
              journal,
              activityResults,
              token);
        }

        var transition = SelectTransition(runtimeState, activityResults);
        if (transition is null)
        {
          return Fail(
              task.Id,
              journal.Steps,
              $"No transition matched workflow state '{runtimeState.Id}'.");
        }

        transitionsTaken++;
        if (transitionsTaken > workflow.MaxTransitions)
        {
          return Fail(
              task.Id,
              journal.Steps,
              $"Workflow exceeded the transition limit of {workflow.MaxTransitions}.");
        }

        var exitResults = new List<WorkflowActivityResult>();
        var exitedState = await activityRunner.RunAsync(
            task,
            runtimeState,
            runtimeState.ExitActivities,
            WorkflowActivityLocation.Exit,
            journal,
            exitResults,
            token);
        if (!exitedState)
        {
          return Fail(
              task.Id,
              journal.Steps,
              exitResults.LastOrDefault(result => !result.Succeeded)?.Error ??
                  $"Exit Activity failed in workflow state '{runtimeState.Id}'.");
        }

        var previousStateId = runtimeState.Id;
        runtimeStateId = transition.TargetStateId;
        domainEvents.Publish(new TaskWorkflowTransitioned(
            profileId,
            task.Id,
            previousStateId,
            runtimeStateId,
            transition.Name));
      }
    }
    catch (OperationCanceledException)
    {
      var outcome = CompleteTask(task.Id, TaskOutcome.Cancelled, runtimeStateId);
      return new TaskReport(task.Id, outcome, journal.Steps, Error: null);
    }
    catch (Exception exception)
    {
      return Fail(task.Id, journal.Steps, exception.Message);
    }
  }

  public TaskReport CompleteWithoutRunning(
      string taskId,
      TaskOutcome outcome,
      string? error = null)
  {
    var effectiveOutcome = CompleteTask(taskId, outcome, error: error);
    return new TaskReport(
        taskId,
        effectiveOutcome,
        Steps: Array.Empty<StepReport>(),
        Error: error);
  }

  private void EnterState(
      string taskId,
      TaskWorkflowState runtimeState,
      CancellationToken cancellationToken)
  {
    if (!state.EnterState(
        taskId,
        runtimeState.Id,
        runtimeState.TaskState,
        runtimeState.DisplayName))
    {
      cancellationToken.ThrowIfCancellationRequested();
      throw new OperationCanceledException(cancellationToken);
    }
    domainEvents.Publish(new TaskWorkflowStateEntered(
        profileId,
        taskId,
        runtimeState.Id,
        runtimeState.TaskState));
  }

  private TaskReport CompleteTerminalState(
      string taskId,
      TaskWorkflowState runtimeState,
      IReadOnlyList<StepReport> steps)
  {
    var terminalError = runtimeState.TerminalOutcome == TaskOutcome.Failed
        ? runtimeState.TerminalError
        : null;
    var outcome = CompleteTask(
        taskId,
        runtimeState.TerminalOutcome!.Value,
        runtimeState.Id,
        terminalError);
    return new TaskReport(
        taskId,
        outcome,
        steps,
        outcome == TaskOutcome.Failed ? terminalError : null);
  }

  private async Task<TaskReport> CompleteTerminalStateAsync(
      TaskDefinition task,
      TaskWorkflowState runtimeState,
      TaskWorkflowJournal journal,
      List<WorkflowActivityResult> activityResults,
      CancellationToken cancellationToken)
  {
    var exited = await activityRunner.RunAsync(
        task,
        runtimeState,
        runtimeState.ExitActivities,
        WorkflowActivityLocation.Exit,
        journal,
        activityResults,
        cancellationToken);
    var failedResult = activityResults.LastOrDefault(result => !result.Succeeded);
    var requestedOutcome = exited && failedResult is null
        ? runtimeState.TerminalOutcome!.Value
        : TaskOutcome.Failed;
    var terminalError = requestedOutcome == TaskOutcome.Failed
        ? failedResult?.Error ?? runtimeState.TerminalError ?? "Terminal state Activity failed."
        : null;
    var outcome = CompleteTask(
        task.Id,
        requestedOutcome,
        runtimeState.Id,
        terminalError);
    return new TaskReport(
        task.Id,
        outcome,
        journal.Steps,
        outcome == TaskOutcome.Failed ? terminalError : null);
  }

  private TaskReport Fail(string taskId, IReadOnlyList<StepReport> steps, string error)
  {
    var outcome = CompleteTask(taskId, TaskOutcome.Failed, error: error);
    return new TaskReport(
        taskId,
        outcome,
        steps,
        outcome == TaskOutcome.Failed ? error : null);
  }

  private TaskOutcome CompleteTask(
      string taskId,
      TaskOutcome outcome,
      string? runtimeStateId = null,
      string? error = null)
  {
    var effectiveOutcome = state.CompleteTask(taskId, outcome, runtimeStateId);
    domainEvents.Publish(new TaskWorkflowFinished(
        profileId,
        taskId,
        runtimeStateId,
        effectiveOutcome,
        effectiveOutcome is TaskOutcome.Failed or TaskOutcome.Blocked ? error : null));
    return effectiveOutcome;
  }

  private static TaskWorkflowTransition? SelectTransition(
      TaskWorkflowState runtimeState,
      IReadOnlyList<WorkflowActivityResult> activityResults)
  {
    var transitionContext = new TaskWorkflowTransitionContext(
        ActivitiesSucceeded: activityResults.All(result => result.Succeeded),
        IsTaskSatisfied: activityResults
            .LastOrDefault(result => result.IsTaskSatisfied is not null)
            ?.IsTaskSatisfied == true);
    return runtimeState.Transitions.FirstOrDefault(candidate =>
        candidate.IsMatch(transitionContext));
  }
}
