using System.Diagnostics;
using Wdem.Core.Processes;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Models;
using Wdem.Windows.Processes;
using Xunit;

namespace Wdem.Windows.Tests.Processes;

public sealed class LegacySourceProcessExecutorAdapterTests
{
  [Fact]
  public async Task ExecuteAsync_MapsDetailedEvidenceAndForwardsProgressInObservedOrder()
  {
    var legacy = new RecordingProcessRunner
    {
      Handler = (_, _, _, onOutput, _) =>
      {
        onOutput?.Invoke(new ProcessOutputLine(false, "stdout-1"));
        onOutput?.Invoke(new ProcessOutputLine(true, "stderr-1"));
        onOutput?.Invoke(new ProcessOutputLine(false, "stdout-2"));
        return Task.FromResult(new ProcessRunResult(
            true,
            1603,
            ["stdout-1", "stdout-2"],
            ["stderr-1"]));
      }
    };
    var progress = new RecordingProgress();
    var adapter = new LegacySourceProcessExecutorAdapter(legacy);

    var result = await adapter.ExecuteAsync(
        new ProcessExecutionRequest("winget", ["install", "--id", "Git.Git"]),
        progress,
        CancellationToken.None);

    Assert.True(result.Started);
    Assert.Equal(1603, result.ExitCode);
    Assert.Equal(["stdout-1", "stdout-2"], result.StandardOutput);
    Assert.Equal(["stderr-1"], result.StandardError);
    Assert.Equal(["stdout-1", "stderr-1", "stdout-2"], progress.Lines);
  }

  [Fact]
  public async Task ExecuteAsync_PreservesArgumentTokensAndWorkingDirectory()
  {
    var legacy = new RecordingProcessRunner();
    var adapter = new LegacySourceProcessExecutorAdapter(legacy);
    var arguments = new[] { "", "two words", "quote\"inside", @"ends-in-\\" };
    var workingDirectory = Path.GetTempPath();

    await adapter.ExecuteAsync(
        new ProcessExecutionRequest("tool.exe", arguments, workingDirectory),
        null,
        CancellationToken.None);

    Assert.Equal("tool.exe", legacy.FileName);
    Assert.Equal(arguments, legacy.Arguments);
    Assert.Equal(workingDirectory, legacy.WorkingDirectory);
  }

  [Fact]
  public async Task ExecuteAsync_PropagatesCancellation()
  {
    var legacy = new RecordingProcessRunner
    {
      Handler = async (_, _, _, _, cancellationToken) =>
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new ProcessRunResult(false, null, [], []);
      }
    };
    var adapter = new LegacySourceProcessExecutorAdapter(legacy);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(() => adapter.ExecuteAsync(
        new ProcessExecutionRequest("tool.exe", []),
        null,
        cancellation.Token));
  }

  [Fact]
  public async Task ExecuteAsync_FailedStartReturnsSafeStructuredError()
  {
    var legacy = new RecordingProcessRunner
    {
      Handler = (_, _, _, _, _) => Task.FromResult(
          new ProcessRunResult(false, null, [], []))
    };
    var adapter = new LegacySourceProcessExecutorAdapter(legacy);

    var result = await adapter.ExecuteAsync(
        new ProcessExecutionRequest("secret-token=do-not-copy", []),
        null,
        CancellationToken.None);

    Assert.False(result.Started);
    Assert.Null(result.ExitCode);
    Assert.NotNull(result.Error);
    Assert.DoesNotContain("do-not-copy", result.Error.Detail, StringComparison.Ordinal);
  }

  private sealed class RecordingProgress : IProgress<string>
  {
    public List<string> Lines { get; } = [];

    public void Report(string value) => Lines.Add(value);
  }

  private sealed class RecordingProcessRunner : IProcessRunner
  {
    public Func<string, IEnumerable<string>, string?, Action<ProcessOutputLine>?, CancellationToken,
        Task<ProcessRunResult>> Handler { get; init; } = (_, _, _, _, _) =>
            Task.FromResult(new ProcessRunResult(true, 0, [], []));

    public string? FileName { get; private set; }
    public IReadOnlyList<string>? Arguments { get; private set; }
    public string? WorkingDirectory { get; private set; }

    public Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken) => RunCommandDetailedAsync(
            fileName,
            arguments,
            null,
            onOutput,
            cancellationToken);

    public Task<ProcessRunResult> RunCommandDetailedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<ProcessOutputLine>? onOutput,
        CancellationToken cancellationToken)
    {
      FileName = fileName;
      Arguments = arguments.ToArray();
      WorkingDirectory = workingDirectory;
      return Handler(fileName, Arguments, workingDirectory, onOutput, cancellationToken);
    }

    public bool RunCommand(string fileName, string arguments, bool dryRun, Action<string>? onOutput = null) =>
        throw new NotSupportedException();

    public bool RunCommand(string fileName, IEnumerable<string> arguments, bool dryRun, Action<string>? onOutput = null) =>
        throw new NotSupportedException();

    public Task<bool> RunCommandAsync(string fileName, IEnumerable<string> arguments, bool dryRun, Action<string>? onOutput, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public string RunCommandWithOutput(string fileName, string args) => throw new NotSupportedException();
    public string RunCommandWithOutput(string fileName, IEnumerable<string> args) => throw new NotSupportedException();
    public string RunCommandWithOutput(string fileName, string args, string? standardInput) => throw new NotSupportedException();
    public string RunCommandWithOutput(string fileName, IEnumerable<string> args, string? standardInput) => throw new NotSupportedException();
    public string RunAndCapture(string fileName, string arguments) => throw new NotSupportedException();
    public string RunAndCapture(string fileName, IEnumerable<string> arguments) => throw new NotSupportedException();
    public bool RunProcessWithStartInfo(ProcessStartInfo startInfo) => throw new NotSupportedException();
  }
}
