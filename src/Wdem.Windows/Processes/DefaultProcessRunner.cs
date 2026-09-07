using System.Diagnostics;
using System.Text;
using Wdem.Application.Runtime;

namespace Wdem.Windows.Processes;

public sealed class DefaultProcessRunner : IProcessRunner
{
  private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(10);
  private readonly IProcessTreeTerminator _processTreeTerminator;

  public DefaultProcessRunner()
      : this(DefaultProcessTreeTerminator.Instance)
  {
  }

  internal DefaultProcessRunner(IProcessTreeTerminator processTreeTerminator)
  {
    _processTreeTerminator = processTreeTerminator ??
        throw new ArgumentNullException(nameof(processTreeTerminator));
  }

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
      await _processTreeTerminator.TerminateAsync(process, TerminationTimeout);
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
}
