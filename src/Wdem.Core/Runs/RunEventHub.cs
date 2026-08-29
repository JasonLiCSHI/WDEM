namespace Wdem.Core.Runs;

public sealed class RunEventHub : IRunEventSink, IDisposable
{
  private readonly object _subscribersGate = new();
  private readonly Dictionary<long, Subscription> _subscriptions = [];
  private readonly Dictionary<Guid, RunGate> _runGates = [];
  private readonly AsyncLocal<Guid?> _operationScope = new();
  private readonly AsyncLocal<int> _deliveryDepth = new();
  private long _nextSubscriptionId;
  private bool _disposed;

  public IDisposable Subscribe(Func<RunEvent, CancellationToken, Task> observer) =>
      Subscribe(observer, required: false, scopeId: null);

  public IDisposable SubscribeRequired(Func<RunEvent, CancellationToken, Task> observer) =>
      Subscribe(observer, required: true, scopeId: null);

  public IDisposable SubscribeRequiredScoped(
      Func<RunEvent, CancellationToken, Task> observer)
  {
    var previousScope = _operationScope.Value;
    var scopeId = Guid.NewGuid();
    _operationScope.Value = scopeId;
    try
    {
      var subscription = Subscribe(observer, required: true, scopeId);
      return new ScopeLease(this, subscription, scopeId, previousScope);
    }
    catch
    {
      _operationScope.Value = previousScope;
      throw;
    }
  }

  private IDisposable Subscribe(
      Func<RunEvent, CancellationToken, Task> observer,
      bool required,
      Guid? scopeId)
  {
    ArgumentNullException.ThrowIfNull(observer);
    lock (_subscribersGate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      var id = checked(++_nextSubscriptionId);
      var subscription = new Subscription(
          this,
          id,
          observer,
          required,
          scopeId,
          _deliveryDepth);
      _subscriptions.Add(id, subscription);
      return subscription;
    }
  }

  public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(runEvent);
    var runGate = RetainRunGate(runEvent.RunId);
    var acquired = false;
    var requiredDeliveries = new List<Task>();
    try
    {
      await runGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
      acquired = true;
      Subscription[] subscribers;
      var scopeId = _operationScope.Value;
      lock (_subscribersGate)
      {
        ObjectDisposedException.ThrowIf(_disposed, this);
        subscribers = _subscriptions.Values
            .Where(subscription => subscription.ScopeId is null ||
                subscription.ScopeId == scopeId)
            .ToArray();
      }

      foreach (var subscriber in subscribers)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var delivery = subscriber.Enqueue(runEvent, cancellationToken);
        if (subscriber.Required)
        {
          requiredDeliveries.Add(delivery);
        }
        else
        {
          ObserveFault(delivery);
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

    if (_deliveryDepth.Value > 0)
    {
      foreach (var delivery in requiredDeliveries)
      {
        ObserveFault(delivery);
      }

      return;
    }

    await Task.WhenAll(requiredDeliveries).ConfigureAwait(false);
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

  private static void ObserveFault(Task task) => _ = task.ContinueWith(
      static completed => _ = completed.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);

  private sealed class RunGate
  {
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public int Users { get; set; }
  }

  private sealed class Subscription(
      RunEventHub owner,
      long id,
      Func<RunEvent, CancellationToken, Task> observer,
      bool required,
      Guid? scopeId,
      AsyncLocal<int> deliveryDepth) : IDisposable
  {
    private readonly object _queueGate = new();
    private readonly Dictionary<Guid, Task> _tails = [];
    private RunEventHub? _owner = owner;
    private Func<RunEvent, CancellationToken, Task>? _observer = observer;

    public bool Required { get; } = required;
    public Guid? ScopeId { get; } = scopeId;

    public Task Enqueue(RunEvent runEvent, CancellationToken cancellationToken)
    {
      var currentObserver = Volatile.Read(ref _observer);
      if (currentObserver is null)
      {
        return Task.CompletedTask;
      }

      Task previous;
      var completion = new TaskCompletionSource(
          TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_queueGate)
      {
        previous = _tails.GetValueOrDefault(runEvent.RunId, Task.CompletedTask);
        _tails[runEvent.RunId] = completion.Task;
      }

      _ = DeliverAsync(
          previous,
          completion,
          currentObserver,
          runEvent,
          cancellationToken);
      return completion.Task;
    }

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

    private async Task DeliverAsync(
        Task previous,
        TaskCompletionSource completion,
        Func<RunEvent, CancellationToken, Task> currentObserver,
        RunEvent runEvent,
        CancellationToken cancellationToken)
    {
      try
      {
        try
        {
          await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
          // A prior delivery reports its own failure and must not poison this queue.
        }

        var previousDepth = deliveryDepth.Value;
        deliveryDepth.Value = checked(previousDepth + 1);
        try
        {
          await currentObserver(runEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
          deliveryDepth.Value = previousDepth;
        }

        completion.TrySetResult();
      }
      catch (OperationCanceledException exception)
      {
        completion.TrySetCanceled(exception.CancellationToken);
      }
      catch (Exception exception)
      {
        completion.TrySetException(exception);
      }
      finally
      {
        lock (_queueGate)
        {
          if (_tails.GetValueOrDefault(runEvent.RunId) == completion.Task)
          {
            _tails.Remove(runEvent.RunId);
          }
        }
      }
    }
  }

  private sealed class ScopeLease(
      RunEventHub owner,
      IDisposable subscription,
      Guid scopeId,
      Guid? previousScope) : IDisposable
  {
    private IDisposable? _subscription = subscription;

    public void Dispose()
    {
      Interlocked.Exchange(ref _subscription, null)?.Dispose();
      if (owner._operationScope.Value == scopeId)
      {
        owner._operationScope.Value = previousScope;
      }
    }
  }
}
