namespace Wdem.Core.Runs;

public sealed class RunEventHub : IRunEventSink, IDisposable
{
  private readonly object _subscribersGate = new();
  private readonly Dictionary<long, Subscription> _subscriptions = [];
  private readonly Dictionary<Guid, RunGate> _runGates = [];
  private long _nextSubscriptionId;
  private bool _disposed;

  public IDisposable Subscribe(Func<RunEvent, CancellationToken, Task> observer)
      => Subscribe(observer, required: false);

  public IDisposable SubscribeRequired(Func<RunEvent, CancellationToken, Task> observer)
      => Subscribe(observer, required: true);

  private IDisposable Subscribe(
      Func<RunEvent, CancellationToken, Task> observer,
      bool required)
  {
    ArgumentNullException.ThrowIfNull(observer);
    lock (_subscribersGate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      var id = checked(++_nextSubscriptionId);
      var subscription = new Subscription(this, id, observer, required);
      _subscriptions.Add(id, subscription);
      return subscription;
    }
  }

  public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(runEvent);
    var runGate = RetainRunGate(runEvent.RunId);
    var acquired = false;
    try
    {
      await runGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
      acquired = true;
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
        catch (Exception) when (!subscriber.Required)
        {
          // A failed observer must not interrupt persistence or other observers.
        }
      }
    }
    finally
    {
      if (acquired)
      {
        runGate.Semaphore.Release();
      }

      ReleaseRunGate(runEvent.RunId, runGate);
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

  private RunGate RetainRunGate(Guid runId)
  {
    lock (_subscribersGate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      if (!_runGates.TryGetValue(runId, out var runGate))
      {
        runGate = new RunGate();
        _runGates.Add(runId, runGate);
      }

      runGate.Users++;
      return runGate;
    }
  }

  private void ReleaseRunGate(Guid runId, RunGate runGate)
  {
    lock (_subscribersGate)
    {
      runGate.Users--;
      if (runGate.Users == 0)
      {
        _runGates.Remove(runId);
      }
    }
  }

  private sealed class RunGate
  {
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public int Users { get; set; }
  }

  private sealed class Subscription(
      RunEventHub owner,
      long id,
      Func<RunEvent, CancellationToken, Task> observer,
      bool required) : IDisposable
  {
    private RunEventHub? _owner = owner;
    private Func<RunEvent, CancellationToken, Task>? _observer = observer;

    public bool Required { get; } = required;

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
