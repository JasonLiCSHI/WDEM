using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.LegacySource.Interfaces;

namespace Wdem.Windows.Processes;

public sealed class LegacySourceProcessExecutorAdapter(IProcessRunner legacy) : IProcessExecutor
{
  private readonly IProcessRunner _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));

  public async Task<ProcessExecutionResult> ExecuteAsync(
      ProcessExecutionRequest request,
      IProgress<string>? output,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
    ArgumentNullException.ThrowIfNull(request.Arguments);
    cancellationToken.ThrowIfCancellationRequested();

    var result = await _legacy.RunCommandDetailedAsync(
        request.FileName,
        request.Arguments,
        request.WorkingDirectory,
        line => ReportSafely(output, line.Text),
        cancellationToken).ConfigureAwait(false);

    return new ProcessExecutionResult(
        result.Started,
        result.ExitCode,
        result.StandardOutput,
        result.StandardError,
        result.Started
            ? null
            : new StructuredError(
                WdemErrorCode.ProviderError,
                "Process could not be started.",
                "The requested external process could not be started.")
            {
              IsRetryable = true
            });
  }

  private static void ReportSafely(IProgress<string>? output, string line)
  {
    try
    {
      output?.Report(line);
    }
    catch (Exception exception)
    {
      global::System.Diagnostics.Trace.WriteLine(
          $"[ProcessExecutor] Progress observer failed: {exception.GetType().Name}");
    }
  }
}
