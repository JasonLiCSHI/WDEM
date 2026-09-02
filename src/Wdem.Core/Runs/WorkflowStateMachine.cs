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
    WorkflowStateStore state,
    CancellationToken allCancellationToken)
{
  public async Task<RunReport> RunAsync()
  {
    var scheduledTasks = new Dictionary<string, Task<TaskReport>>(StringComparer.Ordinal);

    foreach (var taskId in graph.OrderedTaskIds)
    {
      var task = profile.Tasks[taskId];
      var dependencies = task.DependsOn
          .Select(dependencyId => scheduledTasks[dependencyId])
          .ToArray();
      scheduledTasks.Add(taskId, RunAfterDependenciesAsync(
          task,
          workflows[taskId],
          dependencies,
          taskCancellationSources[taskId].Token));
    }

    var reports = await Task.WhenAll(
        graph.OrderedTaskIds.Select(taskId => scheduledTasks[taskId]));
    return new RunReport(graph.OrderedTaskIds
        .Zip(reports)
        .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal));
  }

  private async Task<TaskReport> RunAfterDependenciesAsync(
      TaskDefinition task,
      TaskWorkflowDefinition workflow,
      IReadOnlyList<Task<TaskReport>> dependencyTasks,
      CancellationToken taskCancellationToken)
  {
    // Allow every ready node in the DAG to be scheduled before any synchronous
    // Activity implementation can occupy the caller's thread.
    await Task.Yield();

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

  private TaskReport CompleteWithoutSteps(string taskId, TaskOutcome outcome)
  {
    var effectiveOutcome = state.CompleteTask(taskId, outcome);
    return new TaskReport(
        taskId,
        effectiveOutcome,
        Steps: Array.Empty<StepReport>(),
        Error: null);
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
