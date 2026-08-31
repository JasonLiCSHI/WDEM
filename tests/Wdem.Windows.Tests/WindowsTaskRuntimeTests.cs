using Wdem.Core.Runtime;
using Wdem.Core.Runs;
using Wdem.Core.Tasks;
using Wdem.Windows.Processes;
using Wdem.Windows.Runtime;
using Xunit;

namespace Wdem.Windows.Tests;

public sealed class WindowsTaskRuntimeTests
{
  [Fact]
  public async Task RunAsync_ForwardsStandardOutputAndErrorWithCorrectStreams()
  {
    var processRunner = new OutputProcessRunner();
    var runtime = new WindowsTaskRuntime(processRunner);
    var output = new List<CommandOutput>();
    var progress = new InlineProgress<CommandOutput>(output.Add);
    var invocation = new CommandInvocation(
        "git",
        "detect",
        new CommandDefinition("git", ["--version"]),
        Source: null,
        PreferredVersion: null);

    await runtime.RunAsync(invocation, progress, CancellationToken.None);

    Assert.Collection(
        output,
        line =>
        {
          Assert.Equal(WorkflowOutputStream.StandardOutput, line.Stream);
          Assert.Equal("normal", line.Message);
        },
        line =>
        {
          Assert.Equal(WorkflowOutputStream.StandardError, line.Stream);
          Assert.Equal("problem", line.Message);
        });
  }

  private sealed class OutputProcessRunner : IProcessRunner
  {
    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutput>? output,
        CancellationToken cancellationToken)
    {
      output?.Report(new ProcessOutput(WorkflowOutputStream.StandardOutput, "normal"));
      output?.Report(new ProcessOutput(WorkflowOutputStream.StandardError, "problem"));
      return Task.FromResult(new ProcessResult(true, 0, "normal", "problem"));
    }
  }

  private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }
}
