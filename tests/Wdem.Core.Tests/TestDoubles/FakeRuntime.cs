using System.Collections.Concurrent;
using Wdem.Core.Runs;
using Wdem.Core.Runtime;

namespace Wdem.Core.Tests.TestDoubles;

public sealed class FakeRuntime : ITaskRuntime
{
  private readonly ConcurrentDictionary<
      (string taskId, string phase),
      Func<IProgress<CommandOutput>?, CancellationToken, Task<CommandResult>>> _handlers = new();
  private readonly ConcurrentQueue<(string taskId, string phase)> _invocations = new();
  private readonly object _gate = new();
  private readonly SemaphoreSlim _startSignal = new(0);
  private (string taskId, string phase)? _lastStart;
  private Action<CommandInvocation>? _onInvocation;

  public FakeRuntime WithDetect(string taskId, int exitCode, string stdout = "", string stderr = "") =>
      With(taskId, "detect", (_, _) =>
          Task.FromResult(new CommandResult(exitCode, Stdout: stdout, Stderr: stderr)));

  public FakeRuntime WithApply(string taskId, int exitCode, string stdout = "", string stderr = "") =>
      With(taskId, "apply", (_, _) =>
          Task.FromResult(new CommandResult(exitCode, Stdout: stdout, Stderr: stderr)));

  public FakeRuntime WithPre(string taskId, int exitCode, string stdout = "", string stderr = "") =>
      With(taskId, "pre", (_, _) =>
          Task.FromResult(new CommandResult(exitCode, Stdout: stdout, Stderr: stderr)));

  public FakeRuntime WithPost(string taskId, int exitCode, string stdout = "", string stderr = "") =>
      With(taskId, "post", (_, _) =>
          Task.FromResult(new CommandResult(exitCode, Stdout: stdout, Stderr: stderr)));

  public FakeRuntime OnInvocation(Action<CommandInvocation> observer)
  {
    _onInvocation = observer;
    return this;
  }

  public FakeRuntime WithApplyOutput(
      string taskId,
      string message,
      WorkflowOutputStream stream) =>
      With(taskId, "apply", (output, _) =>
      {
        output?.Report(new CommandOutput(stream, message));
        return Task.FromResult(new CommandResult(0, Stdout: message, Stderr: ""));
      });

  public FakeRuntime WithApplyThatWaitsForCancellation(string taskId) =>
      With(taskId, "apply", async (_, token) =>
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new CommandResult(0, Stdout: "", Stderr: "");
      });

  public FakeRuntime WithApplyThatReturnsAfterCancellation(string taskId) =>
      With(taskId, "apply", async (_, token) =>
      {
        while (!token.IsCancellationRequested)
        {
          await Task.Yield();
        }
        return new CommandResult(0, Stdout: "", Stderr: "");
      });

  public FakeRuntime WithDetectThatWaitsForCancellation(string taskId) =>
      With(taskId, "detect", async (_, token) =>
      {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new CommandResult(0, Stdout: "", Stderr: "");
      });

  public async Task WaitForCommandStartAsync(string taskId, string phase)
  {
    while (true)
    {
      (string taskId, string phase)? current;
      lock (_gate)
      {
        current = _lastStart;
      }
      if (current is not null && current.Value.taskId == taskId && current.Value.phase == phase)
      {
        return;
      }
      await _startSignal.WaitAsync();
    }
  }

  private FakeRuntime With(
      string taskId,
      string phase,
      Func<IProgress<CommandOutput>?, CancellationToken, Task<CommandResult>> handler)
  {
    _handlers[(taskId, phase)] = handler;
    return this;
  }

  public Task<CommandResult> RunAsync(
      CommandInvocation invocation,
      IProgress<CommandOutput>? output,
      CancellationToken cancellationToken)
  {
    _invocations.Enqueue((invocation.TaskId, invocation.Phase));
    lock (_gate)
    {
      _lastStart = (invocation.TaskId, invocation.Phase);
    }
    _startSignal.Release();
    _onInvocation?.Invoke(invocation);

    if (_handlers.TryGetValue((invocation.TaskId, invocation.Phase), out var handler))
    {
      return handler(output, cancellationToken);
    }

    return Task.FromResult(new CommandResult(0, Stdout: "", Stderr: ""));
  }

  public IReadOnlyList<(string taskId, string phase)> Invocations => _invocations.ToArray();
}
