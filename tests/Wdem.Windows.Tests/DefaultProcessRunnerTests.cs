using System.Diagnostics;
using Wdem.Core.Runs;
using Wdem.Windows.Processes;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class DefaultProcessRunnerTests
{
  [Fact]
  public async Task RunAsync_CancellationTerminatesTheStartedProcessTree()
  {
    var childProcessId = new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var output = new InlineProgress<ProcessOutput>(line =>
    {
      if (line.Stream == WorkflowOutputStream.StandardOutput &&
          int.TryParse(line.Message, out var processId))
      {
        childProcessId.TrySetResult(processId);
      }
    });
    var script =
        "$child = Start-Process -FilePath $env:ComSpec " +
        "-ArgumentList '/d','/c','ping -t 127.0.0.1' -WindowStyle Hidden -PassThru; " +
        "[Console]::Out.WriteLine($child.Id); Wait-Process -Id $child.Id";
    var request = new ProcessRequest(
        "powershell.exe",
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script]);
    using var cancellation = new CancellationTokenSource();
    var runner = new DefaultProcessRunner();

    var running = runner.RunAsync(request, output, cancellation.Token);
    var childId = await childProcessId.Task.WaitAsync(TimeSpan.FromSeconds(15));
    cancellation.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    Assert.True(
        await WaitUntilExitedAsync(childId, TimeSpan.FromSeconds(10)),
        $"Child process {childId} was still running after cancellation.");
  }

  [Fact]
  public async Task RunAsync_PreCancelledTokenDoesNotStartAProcess()
  {
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var runner = new DefaultProcessRunner();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        runner.RunAsync(
            new ProcessRequest("this-command-must-never-start.exe", []),
            output: null,
            cancellation.Token));
  }

  private static async Task<bool> WaitUntilExitedAsync(int processId, TimeSpan timeout)
  {
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
      try
      {
        using var process = Process.GetProcessById(processId);
        if (process.HasExited)
        {
          return true;
        }
      }
      catch (ArgumentException)
      {
        return true;
      }

      await Task.Delay(100);
    }

    return false;
  }

  private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }
}
