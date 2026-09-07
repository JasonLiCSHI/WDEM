namespace Wdem.Application.Runtime;

public interface ITaskRuntime
{
  Task<CommandResult> RunAsync(
      CommandInvocation invocation,
      IProgress<CommandOutput>? output,
      CancellationToken cancellationToken);
}
