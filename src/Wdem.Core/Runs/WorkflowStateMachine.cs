using Wdem.Core.Graph;
using Wdem.Core.Profiles;
using Wdem.Core.Runtime;
using Wdem.Core.Tasks;

namespace Wdem.Core.Runs;

/// <summary>
/// Drives task state transitions. Entering an activity state is the only path that
/// invokes its command; the activity result determines the next transition.
/// </summary>
internal sealed class WorkflowStateMachine(
    EnvironmentProfile profile,
    TaskGraph graph,
    ITaskRuntime runtime,
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

      reports[taskId] = await RunTaskAsync(task, taskCancellationSources[taskId].Token);
    }

    return new RunReport(reports);
  }

  private async Task<TaskReport> RunTaskAsync(
      TaskDefinition task,
      CancellationToken taskCancellationToken)
  {
    var steps = new List<StepReport>();
    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
        allCancellationToken,
        taskCancellationToken);
    var token = linkedCancellation.Token;
    var activityIndex = 0;

    try
    {
      if (!state.MakeReady(task.Id))
      {
        token.ThrowIfCancellationRequested();
        throw new OperationCanceledException(token);
      }

      var detect = await EnterAndRunActivityAsync(
          task,
          TaskExecutionState.Detecting,
          "detect",
          task.Detect,
          ++activityIndex,
          token);
      steps.Add(detect);

      if (TaskComplianceEvaluator.Evaluate(task, detect).State == TaskComplianceState.Satisfied)
      {
        var outcome = state.CompleteTask(task.Id, TaskOutcome.NotRequired);
        return new TaskReport(task.Id, outcome, steps, Error: null);
      }

      foreach (var pre in task.Pre)
      {
        var preStep = await EnterAndRunActivityAsync(
            task,
            TaskExecutionState.RunningPre,
            "pre",
            pre,
            ++activityIndex,
            token);
        steps.Add(preStep);
        if (preStep.ExitCode != 0)
        {
          return Fail(task.Id, steps, "Pre step failed.");
        }
      }

      if (task.Apply is null)
      {
        return Fail(task.Id, steps, "Task has no apply command.");
      }

      var apply = await EnterAndRunActivityAsync(
          task,
          TaskExecutionState.Applying,
          "apply",
          task.Apply,
          ++activityIndex,
          token);
      steps.Add(apply);
      if (apply.ExitCode != 0)
      {
        return Fail(task.Id, steps, "Apply step failed.");
      }

      foreach (var post in task.Post)
      {
        var postStep = await EnterAndRunActivityAsync(
            task,
            TaskExecutionState.RunningPost,
            "post",
            post,
            ++activityIndex,
            token);
        steps.Add(postStep);
        if (postStep.ExitCode != 0)
        {
          return Fail(task.Id, steps, "Post step failed.");
        }
      }

      var verify = await EnterAndRunActivityAsync(
          task,
          TaskExecutionState.Verifying,
          "verify",
          task.Detect,
          ++activityIndex,
          token);
      steps.Add(verify);
      if (TaskComplianceEvaluator.Evaluate(task, verify).State != TaskComplianceState.Satisfied)
      {
        return Fail(task.Id, steps, "Verify failed.");
      }

      var succeededOutcome = state.CompleteTask(task.Id, TaskOutcome.Succeeded);
      return new TaskReport(task.Id, succeededOutcome, steps, Error: null);
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

  private async Task<StepReport> EnterAndRunActivityAsync(
      TaskDefinition task,
      TaskExecutionState executionState,
      string phase,
      CommandDefinition command,
      int activityIndex,
      CancellationToken cancellationToken)
  {
    if (!state.EnterActivity(task.Id, executionState, phase, activityIndex))
    {
      cancellationToken.ThrowIfCancellationRequested();
      throw new OperationCanceledException(cancellationToken);
    }

    cancellationToken.ThrowIfCancellationRequested();
    var invocation = new CommandInvocation(
        task.Id,
        phase,
        command,
        task.Source,
        task.PreferredVersion);
    var output = new CallbackProgress<CommandOutput>(line =>
        state.PublishOutput(task.Id, line.Message, line.Stream));
    var result = await runtime.RunAsync(invocation, output, cancellationToken);
    cancellationToken.ThrowIfCancellationRequested();
    return new StepReport(phase, result.ExitCode, result.Stdout, result.Stderr);
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

  private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }
}
