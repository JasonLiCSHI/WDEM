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
        MapError(result));
  }

  private static StructuredError? MapError(
      Wdem.LegacySource.Models.ProcessRunResult result)
  {
    if (!result.Started)
    {
      return new StructuredError(
          WdemErrorCode.ProviderError,
          "Process could not be started.",
          "The requested external process could not be started.")
      {
        IsRetryable = false,
        ProcessExitCode = result.ExitCode
      };
    }

    return result.FailureKind switch
    {
      Wdem.LegacySource.Models.ProcessFailureKind.None => null,
      Wdem.LegacySource.Models.ProcessFailureKind.TimedOut => new StructuredError(
          WdemErrorCode.ProviderError,
          "Process execution timed out.",
          "The external process exceeded its execution time limit.")
      {
        IsRetryable = true,
        ProcessExitCode = result.ExitCode
      },
      Wdem.LegacySource.Models.ProcessFailureKind.OutputDrainFailed => new StructuredError(
          WdemErrorCode.ProviderError,
          "Process output collection failed.",
          "The process exited, but its output could not be completely collected.")
      {
        IsRetryable = true,
        ProcessExitCode = result.ExitCode
      },
      _ => new StructuredError(
          WdemErrorCode.ProviderError,
          "Process completion could not be verified.",
          "The process started, but its final completion state could not be verified.")
      {
        IsRetryable = false,
        ProcessExitCode = result.ExitCode
      }
    };
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
