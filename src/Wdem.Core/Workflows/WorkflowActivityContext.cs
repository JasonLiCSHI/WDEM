using Wdem.Core.Runtime;
using Wdem.Core.Runs;
using Wdem.Core.Tasks;

namespace Wdem.Core.Workflows;

public sealed class WorkflowActivityContext
{
  private readonly ITaskRuntime _runtime;
  private readonly Action<CommandOutput> _publishOutput;

  internal WorkflowActivityContext(
      TaskDefinition task,
      string stateId,
      WorkflowActivityLocation location,
      ITaskRuntime runtime,
      Action<CommandOutput> publishOutput)
  {
    Task = task;
    StateId = stateId;
    Location = location;
    _runtime = runtime;
    _publishOutput = publishOutput;
  }

  public TaskDefinition Task { get; }

  public string StateId { get; }

  public WorkflowActivityLocation Location { get; }

  public void ReportOutput(string message, WorkflowOutputStream stream = WorkflowOutputStream.StandardOutput) =>
      _publishOutput(new CommandOutput(stream, message));

  public Task<CommandResult> RunCommandAsync(
      string phase,
      CommandDefinition command,
      CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(phase);
    ArgumentNullException.ThrowIfNull(command);
    var invocation = new CommandInvocation(
        Task.Id,
        phase,
        command,
        Task.Source,
        Task.PreferredVersion);
    return _runtime.RunAsync(
        invocation,
        new CallbackProgress<CommandOutput>(_publishOutput),
        cancellationToken);
  }

  private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }
}
