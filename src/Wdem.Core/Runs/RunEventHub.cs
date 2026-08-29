namespace Wdem.Core.Runs;

public sealed class RunEventHub : IRunEventSink, IDisposable
{
  private readonly object _subscribersGate = new();
  private readonly SemaphoreSlim _publishGate = new(1, 1);
  private readonly Dictionary<long, Subscription> _subscriptions = [];
  private long _nextSubscriptionId;
  private bool _disposed;

  public IDisposable Subscribe(Func<RunEvent, CancellationToken, Task> observer)
  {
    ArgumentNullException.ThrowIfNull(observer);
    lock (_subscribersGate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      var id = checked(++_nextSubscriptionId);
      var subscription = new Subscription(this, id, observer);
      _subscriptions.Add(id, subscription);
      return subscription;
    }
  }

  public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(runEvent);
    await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
      Subscription[] subscribers;
      lock (_subscribersGate)
      {
        ObjectDisposedException.ThrowIf(_disposed, this);
        subscribers = _subscriptions.Values.ToArray();
      }

      foreach (var subscriber in subscribers)
      {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
          await subscriber.InvokeAsync(runEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception)
        {
          // A failed observer must not interrupt persistence or other observers.
        }
      }
    }
    finally
    {
      _publishGate.Release();
    }
  }

  public void Dispose()
  {
    lock (_subscribersGate)
    {
      if (_disposed)
      {
        return;
      }

      _disposed = true;
      foreach (var subscription in _subscriptions.Values)
      {
        subscription.Detach();
      }

      _subscriptions.Clear();
    }
  }

  private void Unsubscribe(long id)
  {
    lock (_subscribersGate)
    {
      _subscriptions.Remove(id);
    }
  }

  private sealed class Subscription(
      RunEventHub owner,
      long id,
      Func<RunEvent, CancellationToken, Task> observer) : IDisposable
  {
    private RunEventHub? _owner = owner;
    private Func<RunEvent, CancellationToken, Task>? _observer = observer;

    public Task InvokeAsync(RunEvent runEvent, CancellationToken cancellationToken) =>
        Volatile.Read(ref _observer)?.Invoke(runEvent, cancellationToken) ?? Task.CompletedTask;

    public void Dispose()
    {
      var currentOwner = Interlocked.Exchange(ref _owner, null);
      Interlocked.Exchange(ref _observer, null);
      currentOwner?.Unsubscribe(id);
    }

    public void Detach()
    {
      Interlocked.Exchange(ref _owner, null);
      Interlocked.Exchange(ref _observer, null);
    }
  }
}
