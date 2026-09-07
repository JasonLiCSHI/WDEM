using Wdem.Application.Execution;
using Wdem.Application.Events;
using Wdem.Application.Runtime;
using Wdem.Application.Workflows;
using Wdem.Domain.Tasks;
using Wdem.Domain.Planning;
using Wdem.Domain.Execution;
using Wdem.Domain.Events;
using Wdem.Domain.Profiles;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

/// <summary>
/// Executes arbitrary task state graphs. Runtime state always changes before an
/// Entry, Residence, or Exit Activity runs, and is projected to Task state by the workflow.
/// </summary>
internal sealed class WorkflowStateMachine(
    EnvironmentProfile profile,
    IReadOnlyList<PlannedTask> plannedTasks,
    ITaskRuntime runtime,
    IWorkflowActivityExecutor activityExecutor,
    IReadOnlyDictionary<string, TaskWorkflowDefinition> workflows,
    IReadOnlyDictionary<string, CancellationTokenSource> taskCancellationSources,
    WorkflowStateStore state,
    IDomainEventPublisher domainEvents,
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
    // Allow every ready node in the DAG to be scheduled before any synchronous
    // Activity implementation can occupy the caller's thread.
    await Task.Yield();

    if (action == PlannedTaskAction.Blocked)
    {
      return CompleteWithoutSteps(
          task.Id,
          TaskOutcome.Blocked,
          "Planning was blocked because detection failed for this Task or one of its dependencies.");
    }

    var dependencies = await Task.WhenAll(dependencyTasks);
    if (allCancellationToken.IsCancellationRequested || taskCancellationToken.IsCancellationRequested)
    {
      return CompleteWithoutSteps(task.Id, TaskOutcome.Cancelled);
    }

    if (IsBlockedByDependency(dependencies))
    {
      return CompleteWithoutSteps(task.Id, TaskOutcome.Blocked);
    }

    return await RunTaskAsync(task, workflow, taskCancellationToken);
  }

  private async Task<TaskReport> RunTaskAsync(
      TaskDefinition task,
      TaskWorkflowDefinition workflow,
      CancellationToken taskCancellationToken)
  {
    var steps = new List<StepReport>();
    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
        allCancellationToken,
        taskCancellationToken);
    var token = linkedCancellation.Token;
    var activityCounter = new ActivityCounter();
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
          profile.Id,
          task.Id,
          workflow.InitialStateId));

      while (true)
      {
        token.ThrowIfCancellationRequested();
        var runtimeState = workflow.States[runtimeStateId];
        var hasLifecycleActivities = runtimeState.ActivityCount > 0;

        if (runtimeState.IsTerminal && !hasLifecycleActivities)
        {
          var terminalError = runtimeState.TerminalOutcome == TaskOutcome.Failed
              ? runtimeState.TerminalError
              : null;
          var terminalOutcome = CompleteTask(
              task.Id,
              runtimeState.TerminalOutcome!.Value,
              runtimeState.Id,
              terminalError);
          return new TaskReport(
              task.Id,
              terminalOutcome,
              steps,
              terminalOutcome == TaskOutcome.Failed ? terminalError : null);
        }

        if (!state.EnterState(
            task.Id,
            runtimeState.Id,
            runtimeState.TaskState,
            runtimeState.DisplayName))
        {
          token.ThrowIfCancellationRequested();
          throw new OperationCanceledException(token);
        }
        domainEvents.Publish(new TaskWorkflowStateEntered(
            profile.Id,
            task.Id,
            runtimeState.Id,
            runtimeState.TaskState));

        var activityResults = new List<WorkflowActivityResult>();
        var entered = await RunActivitiesAsync(
            task,
            runtimeState,
            runtimeState.EntryActivities,
            WorkflowActivityLocation.Entry,
            activityCounter,
            activityResults,
            steps,
            token);
        if (entered)
        {
          await RunActivitiesAsync(
              task,
              runtimeState,
              runtimeState.ResidenceActivities,
              WorkflowActivityLocation.Residence,
              activityCounter,
              activityResults,
              steps,
              token);
        }

        if (runtimeState.IsTerminal)
        {
          var exited = await RunActivitiesAsync(
              task,
              runtimeState,
              runtimeState.ExitActivities,
              WorkflowActivityLocation.Exit,
              activityCounter,
              activityResults,
              steps,
              token);
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
              steps,
              outcome == TaskOutcome.Failed ? terminalError : null);
        }

        var transitionContext = new TaskWorkflowTransitionContext(
            ActivitiesSucceeded: activityResults.All(result => result.Succeeded),
            IsTaskSatisfied: activityResults
                .LastOrDefault(result => result.IsTaskSatisfied is not null)
                ?.IsTaskSatisfied == true);
        var transition = runtimeState.Transitions.FirstOrDefault(candidate =>
            candidate.IsMatch(transitionContext));
        if (transition is null)
        {
          return Fail(
              task.Id,
              steps,
              $"No transition matched workflow state '{runtimeState.Id}'.");
        }

        transitionsTaken++;
        if (transitionsTaken > workflow.MaxTransitions)
        {
          return Fail(
              task.Id,
              steps,
              $"Workflow exceeded the transition limit of {workflow.MaxTransitions}.");
        }

        var exitResults = new List<WorkflowActivityResult>();
        var exitedState = await RunActivitiesAsync(
            task,
            runtimeState,
            runtimeState.ExitActivities,
            WorkflowActivityLocation.Exit,
            activityCounter,
            exitResults,
            steps,
            token);
        if (!exitedState)
        {
          return Fail(
              task.Id,
              steps,
              exitResults.LastOrDefault(result => !result.Succeeded)?.Error ??
                  $"Exit Activity failed in workflow state '{runtimeState.Id}'.");
        }

        var previousStateId = runtimeState.Id;
        runtimeStateId = transition.TargetStateId;
        domainEvents.Publish(new TaskWorkflowTransitioned(
            profile.Id,
            task.Id,
            previousStateId,
            runtimeStateId,
            transition.Name));
      }
    }
    catch (OperationCanceledException)
    {
      var outcome = CompleteTask(task.Id, TaskOutcome.Cancelled, runtimeStateId);
      return new TaskReport(task.Id, outcome, steps, Error: null);
    }
    catch (Exception exception)
    {
      return Fail(task.Id, steps, exception.Message);
    }
  }

  private async Task<bool> RunActivitiesAsync(
      TaskDefinition task,
      TaskWorkflowState runtimeState,
      IReadOnlyList<WorkflowActivity> activities,
      WorkflowActivityLocation location,
      ActivityCounter activityCounter,
      List<WorkflowActivityResult> results,
      List<StepReport> steps,
      CancellationToken cancellationToken)
  {
    foreach (var activity in activities)
    {
      activityCounter.Value++;
      if (!state.BeginActivity(
          task.Id,
          runtimeState.Id,
          runtimeState.TaskState,
          activity,
          location,
          activityCounter.Value))
      {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
      }
      domainEvents.Publish(new TaskWorkflowActivityStarted(
          profile.Id,
          task.Id,
          runtimeState.Id,
          activity.Id,
          location,
          activityCounter.Value));

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
            profile.Id,
            task.Id,
            runtimeState.Id,
            activity.Id,
            location,
            activityCounter.Value,
            Succeeded: false,
            IsTaskSatisfied: null,
            exception.Message));
        throw;
      }
      if (result.Step is { } step)
      {
        steps.Add(step);
      }
      results.Add(result);
      domainEvents.Publish(new TaskWorkflowActivityCompleted(
          profile.Id,
          task.Id,
          runtimeState.Id,
          activity.Id,
          location,
          activityCounter.Value,
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

  private TaskReport Fail(string taskId, IReadOnlyList<StepReport> steps, string error)
  {
    var outcome = CompleteTask(taskId, TaskOutcome.Failed, error: error);
    return new TaskReport(
        taskId,
        outcome,
        steps,
        outcome == TaskOutcome.Failed ? error : null);
  }

  private TaskReport CompleteWithoutSteps(
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

  private TaskOutcome CompleteTask(
      string taskId,
      TaskOutcome outcome,
      string? runtimeStateId = null,
      string? error = null)
  {
    var effectiveOutcome = state.CompleteTask(taskId, outcome, runtimeStateId);
    domainEvents.Publish(new TaskWorkflowFinished(
        profile.Id,
        taskId,
        runtimeStateId,
        effectiveOutcome,
        effectiveOutcome is TaskOutcome.Failed or TaskOutcome.Blocked ? error : null));
    return effectiveOutcome;
  }

  private static bool IsBlockedByDependency(IEnumerable<TaskReport> dependencies) =>
      dependencies.Any(dependency =>
          dependency.Outcome is TaskOutcome.Failed or
              TaskOutcome.Cancelled or
              TaskOutcome.Blocked);

  private sealed class ActivityCounter
  {
    public int Value { get; set; }
  }
}
