using System.Collections.Concurrent;
using Wdem.Core.Runs;
using Wdem.Core.Runtime;

namespace Wdem.Core.Tests.TestDoubles;

public sealed class FakeRuntime : ITaskRuntime
{
  private readonly ConcurrentDictionary<
      (string taskId, string phase),
      Func<IProgress<CommandOutput>?, CancellationToken, Task<CommandResult>>> _handlers = new();
  private readonly ConcurrentDictionary<
      (string taskId, string phase),
      TaskCompletionSource> _startSignals = new();
  private readonly ConcurrentQueue<(string taskId, string phase)> _invocations = new();
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

  public FakeRuntime WithApplyThatWaitsFor(
      string taskId,
      Task completion,
      int exitCode = 0) =>
      With(taskId, "apply", async (_, token) =>
      {
        await completion.WaitAsync(token);
        return new CommandResult(exitCode, Stdout: "", Stderr: "");
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

  public Task WaitForCommandStartAsync(string taskId, string phase)
  {
    var key = (taskId, phase);
    if (_invocations.Contains(key))
    {
      return Task.CompletedTask;
    }

    var signal = _startSignals.GetOrAdd(
        key,
        _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    if (_invocations.Contains(key))
    {
      signal.TrySetResult();
    }

    return signal.Task;
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
    var key = (invocation.TaskId, invocation.Phase);
    _invocations.Enqueue(key);
    _startSignals.GetOrAdd(
        key,
        _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
        .TrySetResult();
    _onInvocation?.Invoke(invocation);

    if (_handlers.TryGetValue((invocation.TaskId, invocation.Phase), out var handler))
    {
      return handler(output, cancellationToken);
    }

    return Task.FromResult(new CommandResult(0, Stdout: "", Stderr: ""));
  }

  public IReadOnlyList<(string taskId, string phase)> Invocations => _invocations.ToArray();
}
