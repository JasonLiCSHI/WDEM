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

  [Fact]
  public async Task RunAsync_ExpandsInstalledAssetDirectoryInExecutableAndArguments()
  {
    var processRunner = new CapturingProcessRunner();
    var runtime = new WindowsTaskRuntime(processRunner, @"C:\Program Files\WDEM");
    var invocation = new CommandInvocation(
        "tool",
        "apply",
        new CommandDefinition(
            @"{appDirectory}\Script\tool.exe",
            [@"{appDirectory}\Settings\tool.json", "{source}", "{preferredVersion}"]),
        Source: "https://vendor.example/tool.exe",
        PreferredVersion: "1.2.3");

    await runtime.RunAsync(invocation, output: null, CancellationToken.None);

    Assert.NotNull(processRunner.Request);
    Assert.Equal(@"C:\Program Files\WDEM\Script\tool.exe", processRunner.Request.FileName);
    Assert.Equal(
        [
          @"C:\Program Files\WDEM\Settings\tool.json",
          "https://vendor.example/tool.exe",
          "1.2.3"
        ],
        processRunner.Request.Arguments);
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

  private sealed class CapturingProcessRunner : IProcessRunner
  {
    public ProcessRequest? Request { get; private set; }

    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutput>? output,
        CancellationToken cancellationToken)
    {
      Request = request;
      return Task.FromResult(new ProcessResult(true, 0, "", ""));
    }
  }

  private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
  {
    public void Report(T value) => callback(value);
  }
}
