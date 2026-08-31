using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runtime;
using Wdem.Core.Tasks;
using Wdem.Core.Workflows;

namespace Wdem.Core.Runs;

/// <summary>
/// Executes arbitrary task state graphs. Runtime state always changes before an
/// Entry, Residence, or Exit Activity runs, and is projected to Task state by the graph.
/// </summary>
internal sealed class WorkflowStateMachine(
    EnvironmentProfile profile,
    TaskGraph graph,
    ITaskRuntime runtime,
    IReadOnlyDictionary<string, TaskWorkflowDefinition> workflows,
    IReadOnlyDictionary<string, CancellationTokenSource> taskCancellationSources,
    CancellationToken allCancellationToken,
    WorkflowStateStore state)
{
  public async Task<RunReport> RunAsync()
  {
    var reports = new Dictionary<string, TaskReport>(StringComparer.Ordinal);

    foreach (var taskId in graph.OrderedTaskIds)
    {
      var task = profile.Tasks[taskId];
      if (allCancellationToken.IsCancellationRequested ||
          taskCancellationSources[taskId].IsCancellationRequested)
      {
        state.CompleteTask(taskId, TaskOutcome.Cancelled);
        reports[taskId] = new TaskReport(
            taskId,
            TaskOutcome.Cancelled,
            Steps: Array.Empty<StepReport>(),
            Error: null);
        continue;
      }

      if (IsBlockedByDependency(task, reports))
      {
        state.CompleteTask(taskId, TaskOutcome.Blocked);
        reports[taskId] = new TaskReport(
            taskId,
            TaskOutcome.Blocked,
            Steps: Array.Empty<StepReport>(),
            Error: null);
        continue;
      }

      reports[taskId] = await RunTaskAsync(
          task,
          workflows[taskId],
          taskCancellationSources[taskId].Token);
    }

    return new RunReport(reports);
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

      while (true)
      {
        token.ThrowIfCancellationRequested();
        var runtimeState = workflow.States[runtimeStateId];
        var hasLifecycleActivities = runtimeState.ActivityCount > 0;

        if (runtimeState.IsTerminal && !hasLifecycleActivities)
        {
          var terminalOutcome = state.CompleteTask(
              task.Id,
              runtimeState.TerminalOutcome!.Value,
              runtimeState.Id);
          return new TaskReport(
              task.Id,
              terminalOutcome,
              steps,
              terminalOutcome == TaskOutcome.Failed ? runtimeState.TerminalError : null);
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
          var outcome = state.CompleteTask(task.Id, requestedOutcome, runtimeState.Id);
          return new TaskReport(
              task.Id,
              outcome,
              steps,
              outcome == TaskOutcome.Failed
                  ? failedResult?.Error ?? runtimeState.TerminalError ?? "Terminal state Activity failed."
                  : null);
        }

        var transitionContext = new TaskWorkflowTransitionContext(
            task,
            runtimeState,
            activityResults);
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

        runtimeStateId = transition.TargetStateId;
      }
    }
    catch (OperationCanceledException)
    {
      var outcome = state.CompleteTask(task.Id, TaskOutcome.Cancelled);
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
      ICollection<WorkflowActivityResult> results,
      ICollection<StepReport> steps,
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

      cancellationToken.ThrowIfCancellationRequested();
      var context = new WorkflowActivityContext(
          task,
          runtimeState.Id,
          location,
          runtime,
          output => state.PublishOutput(task.Id, output.Message, output.Stream));
      var result = await activity.ExecuteAsync(context, cancellationToken)
          ?? throw new InvalidOperationException($"Activity '{activity.Id}' returned no result.");
      if (result.Step is { } step)
      {
        steps.Add(step);
      }
      results.Add(result);
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
    var outcome = state.CompleteTask(taskId, TaskOutcome.Failed);
    return new TaskReport(
        taskId,
        outcome,
        steps,
        outcome == TaskOutcome.Failed ? error : null);
  }

  private static bool IsBlockedByDependency(
      TaskDefinition task,
      IReadOnlyDictionary<string, TaskReport> reports) =>
      task.DependsOn.Any(dependencyId =>
          reports.TryGetValue(dependencyId, out var dependency) &&
          dependency.Outcome is TaskOutcome.Failed or
              TaskOutcome.Cancelled or
              TaskOutcome.Blocked);

  private sealed class ActivityCounter
  {
    public int Value { get; set; }
  }
}
