using Wdem.Core.Runs;

namespace Wdem.Windows.Processes;

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

public sealed record ProcessResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed record ProcessOutput(
    WorkflowOutputStream Stream,
    string Message);

public interface IProcessRunner
{
  Task<ProcessResult> RunAsync(
      ProcessRequest request,
      IProgress<ProcessOutput>? output,
      CancellationToken cancellationToken);
}
