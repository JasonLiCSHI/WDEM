using System.Diagnostics;
using System.Text;
using Wdem.Core.Runs;

namespace Wdem.Windows.Processes;

public sealed class DefaultProcessRunner : IProcessRunner
{
  public async Task<ProcessResult> RunAsync(
      ProcessRequest request,
      IProgress<ProcessOutput>? output,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(request);
    cancellationToken.ThrowIfCancellationRequested();

    var startInfo = new ProcessStartInfo
    {
      FileName = request.FileName,
      WorkingDirectory = request.WorkingDirectory ?? string.Empty,
      UseShellExecute = false,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      CreateNoWindow = true
    };

    foreach (var argument in request.Arguments)
    {
      startInfo.ArgumentList.Add(argument);
    }

    using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

    try
    {
      if (!process.Start())
      {
        return new ProcessResult(
            Started: false,
            ExitCode: -1,
            StandardOutput: "",
            StandardError: $"Failed to start '{request.FileName}'.");
      }
    }
    catch (Exception exception)
    {
      return new ProcessResult(
          Started: false,
          ExitCode: -1,
          StandardOutput: "",
          StandardError: exception.Message);
    }

    var standardOutput = new StringBuilder();
    var standardError = new StringBuilder();

    async Task PumpAsync(
        StreamReader reader,
        StringBuilder buffer,
        WorkflowOutputStream stream)
    {
      while (true)
      {
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null)
        {
          break;
        }
        buffer.AppendLine(line);
        output?.Report(new ProcessOutput(stream, line));
      }
    }

    var stdoutTask = PumpAsync(
        process.StandardOutput,
        standardOutput,
        WorkflowOutputStream.StandardOutput);
    var stderrTask = PumpAsync(
        process.StandardError,
        standardError,
        WorkflowOutputStream.StandardError);

    try
    {
      await process.WaitForExitAsync(cancellationToken);
    }
    catch (OperationCanceledException)
    {
      TryKillProcessTree(process);
      try
      {
        using var terminationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(terminationTimeout.Token);
      }
      catch
      {
        // Cancellation has already been requested. The process-tree kill is best effort.
      }
      throw;
    }
    finally
    {
      try
      {
        await Task.WhenAll(stdoutTask, stderrTask);
      }
      catch
      {
        // Ignore pump errors; exit code and captured output still matter.
      }
    }

    return new ProcessResult(
        Started: true,
        ExitCode: process.ExitCode,
        StandardOutput: standardOutput.ToString(),
        StandardError: standardError.ToString());
  }

  private static void TryKillProcessTree(Process process)
  {
    try
    {
      if (!process.HasExited)
      {
        process.Kill(entireProcessTree: true);
      }
    }
    catch
    {
      // Best effort.
    }
  }
}
