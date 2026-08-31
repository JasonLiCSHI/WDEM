using Wdem.Core.Profiles;
using Wdem.Core.Runtime;

namespace Wdem.Core.Runs;

public static class EnvironmentInspector
{
  public static async Task<InspectReport> InspectAsync(
      EnvironmentProfile profile,
      ITaskRuntime runtime,
      CancellationToken cancellationToken = default)
  {
    return await InspectAsync(profile, runtime, progress: null, cancellationToken);
  }

  public static async Task<InspectReport> InspectAsync(
      EnvironmentProfile profile,
      ITaskRuntime runtime,
      IProgress<WorkflowProgress>? progress,
      CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(runtime);

    var inspections = new Dictionary<string, TaskInspection>(StringComparer.Ordinal);
    foreach (var task in profile.Tasks.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
    {
      cancellationToken.ThrowIfCancellationRequested();
      progress?.Report(new WorkflowProgress(task.Id, TaskExecutionState.Ready, null, 0));
      progress?.Report(new WorkflowProgress(task.Id, TaskExecutionState.Detecting, "detect", 0));

      var invocation = new CommandInvocation(
          task.Id,
          "detect",
          task.Detect,
          task.Source,
          task.PreferredVersion);

      CommandResult result;
      try
      {
        IProgress<CommandOutput>? output = progress is null
            ? null
            : new CallbackProgress<CommandOutput>(line =>
                progress.Report(new WorkflowProgress(
                    task.Id,
                    TaskExecutionState.Detecting,
                    "detect",
                    0,
                    Message: line.Message,
                    OutputStream: line.Stream)));

        result = await runtime.RunAsync(invocation, output, cancellationToken);
      }
      catch (OperationCanceledException)
      {
        progress?.Report(new WorkflowProgress(
            task.Id,
            TaskExecutionState.Cancelled,
            "detect",
            100,
            TaskOutcome.Cancelled));
        throw;
      }

      var step = new StepReport("detect", result.ExitCode, result.Stdout, result.Stderr);
      var detectSucceeded = result.ExitCode == 0;
      var compliance = TaskComplianceEvaluator.Evaluate(task, step);

      inspections.Add(
          task.Id,
          new TaskInspection(
              task.Id,
              detectSucceeded,
              compliance.DetectedVersion,
              compliance.State,
              task.VersionConstraint,
              step));
      progress?.Report(new WorkflowProgress(
          task.Id,
          compliance.State == TaskComplianceState.Satisfied
              ? TaskExecutionState.Satisfied
              : TaskExecutionState.Failed,
          null,
          100));
    }

    return new InspectReport(inspections);
  }
  private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }
}
