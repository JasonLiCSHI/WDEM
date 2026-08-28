namespace Wdem.Core.Processes;

public interface IProcessExecutor
{
  Task<ProcessExecutionResult> ExecuteAsync(
      ProcessExecutionRequest request,
      IProgress<string>? output,
      CancellationToken cancellationToken);
}
