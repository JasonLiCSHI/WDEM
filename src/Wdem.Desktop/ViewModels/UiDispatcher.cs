using Microsoft.UI.Dispatching;

namespace Wdem.Desktop.ViewModels;

public interface IUiDispatcher
{
  Task EnqueueAsync(Action action, CancellationToken cancellationToken = default);
}

public sealed class UiDispatcherUnavailableException : InvalidOperationException
{
  public UiDispatcherUnavailableException()
      : base("The desktop dispatcher is shutting down.")
  {
  }
}

internal interface IDispatcherQueueAdapter
{
  bool HasThreadAccess { get; }

  bool TryEnqueue(Action action);
}

internal sealed class WinUiDispatcherQueueAdapter(DispatcherQueue queue)
    : IDispatcherQueueAdapter
{
  public bool HasThreadAccess => queue.HasThreadAccess;

  public bool TryEnqueue(Action action) => queue.TryEnqueue(() => action());
}

public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
  private readonly IDispatcherQueueAdapter _queue;

  public DispatcherQueueUiDispatcher(DispatcherQueue queue)
      : this(new WinUiDispatcherQueueAdapter(
          queue ?? throw new ArgumentNullException(nameof(queue))))
  {
  }

  internal DispatcherQueueUiDispatcher(IDispatcherQueueAdapter queue)
  {
    _queue = queue ?? throw new ArgumentNullException(nameof(queue));
  }

  public Task EnqueueAsync(Action action, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(action);
    cancellationToken.ThrowIfCancellationRequested();
    if (_queue.HasThreadAccess)
    {
      action();
      return Task.CompletedTask;
    }

    var completion = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    if (!_queue.TryEnqueue(() =>
        {
          try
          {
            action();
            completion.TrySetResult();
          }
          catch (Exception exception)
          {
            completion.TrySetException(exception);
          }
        }))
    {
      completion.TrySetException(new UiDispatcherUnavailableException());
    }

    return completion.Task;
  }
}
