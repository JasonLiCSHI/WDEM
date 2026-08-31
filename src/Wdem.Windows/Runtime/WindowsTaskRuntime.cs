using Wdem.Core.Runtime;
using Wdem.Core.Runs;
using Wdem.Windows.Processes;

namespace Wdem.Windows.Runtime;

public sealed class WindowsTaskRuntime : ITaskRuntime
{
  private readonly IProcessRunner _processRunner;

  public WindowsTaskRuntime(IProcessRunner processRunner)
  {
    _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
  }

  public async Task<CommandResult> RunAsync(
      CommandInvocation invocation,
      IProgress<CommandOutput>? output,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(invocation);

    var command = invocation.Command;
    var arguments = command.Arguments
        .Select(argument => Expand(argument, invocation.Source, invocation.PreferredVersion))
        .ToArray();

    var result = await _processRunner.RunAsync(
        new ProcessRequest(command.Executable, arguments),
        output is null
            ? null
            : new OutputProgress(line => output.Report(new CommandOutput(
                line.Stream,
                line.Message))),
        cancellationToken);

    if (!result.Started)
    {
      return new CommandResult(ExitCode: result.ExitCode, Stdout: result.StandardOutput, Stderr: result.StandardError);
    }

    return new CommandResult(ExitCode: result.ExitCode, Stdout: result.StandardOutput, Stderr: result.StandardError);
  }

  private static string Expand(string value, string? source, string? preferredVersion)
  {
    if (string.IsNullOrEmpty(value))
    {
      return value;
    }

    return value
        .Replace("{source}", source ?? string.Empty, StringComparison.Ordinal)
        .Replace("{preferredVersion}", preferredVersion ?? string.Empty, StringComparison.Ordinal);
  }

  private sealed class OutputProgress(Action<ProcessOutput> callback) : IProgress<ProcessOutput>
  {
    public void Report(ProcessOutput value) => callback(value);
  }
}
