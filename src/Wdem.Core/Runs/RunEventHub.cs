namespace Wdem.Core.Runs;

public sealed class RunEventHub : IRunEventSink, IDisposable
{
  private const int MaximumConcurrentOptionalDeliveries = 32;
  private readonly object _subscribersGate = new();
  private readonly Dictionary<long, Subscription> _subscriptions = [];
  private readonly Dictionary<Guid, RunGate> _runGates = [];
  private readonly AsyncLocal<Guid?> _operationScope = new();
  private readonly AsyncLocal<long?> _deliveringSubscription = new();
  private readonly AsyncLocal<PublicationContext?> _publicationContext = new();
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

  public void BindCurrentScopeToRun(Guid runId)
  {
    var scopeId = _operationScope.Value;
    if (scopeId is null)
    {
      return;
    }

    lock (_subscribersGate)
    {
      ObjectDisposedException.ThrowIf(_disposed, this);
      foreach (var subscription in _subscriptions.Values.Where(
          subscription => subscription.ScopeId == scopeId))
      {
        subscription.TargetRunId = runId;
      }
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
          _deliveringSubscription);
      _subscriptions.Add(id, subscription);
      return subscription;
    }
  }

  public async Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(runEvent);
    var context = _publicationContext.Value;
    if (context is not null)
    {
      await PublishCoreAsync(
          runEvent,
          cancellationToken,
          context,
          ownsContext: false).ConfigureAwait(false);
      return;
    }

    context = new PublicationContext();
    _publicationContext.Value = context;
    try
    {
      await PublishCoreAsync(
          runEvent,
          cancellationToken,
          context,
          ownsContext: true).ConfigureAwait(false);
    }
    finally
    {
      _publicationContext.Value = null;
    }
  }

  private async Task PublishCoreAsync(
      RunEvent runEvent,
      CancellationToken cancellationToken,
      PublicationContext context,
      bool ownsContext)
  {
    var runGate = RetainRunGate(runEvent.RunId);
    var acquired = false;
    var requiredDeliveries = new List<Task>();
    try
    {
      await runGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
      acquired = true;
      Subscription[] subscribers;
      var scopeId = _operationScope.Value;
      var recursivelyInvokedSubscription = _deliveringSubscription.Value;
      lock (_subscribersGate)
      {
        ObjectDisposedException.ThrowIf(_disposed, this);
        subscribers = _subscriptions.Values
            .Where(subscription => subscription.ScopeId is null ||
                (subscription.ScopeId == scopeId &&
                    subscription.TargetRunId == runEvent.RunId))
            .ToArray();
      }

      foreach (var subscriber in subscribers)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var delivery = subscriber.Enqueue(runEvent, cancellationToken);
        if (subscriber.Required)
        {
          if (subscriber.Id == recursivelyInvokedSubscription)
          {
            context.Defer(delivery);
          }
          else
          {
            requiredDeliveries.Add(delivery);
          }
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

    Exception? failure = null;
    try
    {
      await Task.WhenAll(requiredDeliveries).ConfigureAwait(false);
    }
    catch (Exception exception)
    {
      failure = exception;
    }

    if (ownsContext)
    {
      try
      {
        await context.DrainAsync(cancellationToken).ConfigureAwait(false);
      }
      catch (Exception exception)
      {
        failure ??= exception;
      }
    }

    if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
    {
      throw new OperationCanceledException(cancellationToken);
    }

    if (failure is RequiredRunEventDeliveryException requiredFailure)
    {
      throw requiredFailure;
    }

    if (failure is not null)
    {
      throw new RequiredRunEventDeliveryException(failure);
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

  private sealed class PublicationContext
  {
    private readonly object _gate = new();
    private readonly List<Task> _deferred = [];

    public void Defer(Task delivery)
    {
      lock (_gate)
      {
        _deferred.Add(delivery);
      }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
      Exception? failure = null;
      while (TakeDeferred() is { Count: > 0 } deliveries)
      {
        var completion = Task.WhenAll(deliveries);
        try
        {
          await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
          if (!completion.IsCompleted)
          {
            ObserveFault(completion);
          }

          failure ??= exception;
          if (cancellationToken.IsCancellationRequested)
          {
            break;
          }
        }
      }

      if (failure is not null)
      {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
      }
    }

    private IReadOnlyList<Task> TakeDeferred()
    {
      lock (_gate)
      {
        if (_deferred.Count == 0)
        {
          return [];
        }

        var deliveries = _deferred.ToArray();
        _deferred.Clear();
        return deliveries;
      }
    }
  }

  private sealed class Subscription(
      RunEventHub owner,
      long id,
      Func<RunEvent, CancellationToken, Task> observer,
      bool required,
      Guid? scopeId,
      AsyncLocal<long?> deliveringSubscription) : IDisposable
  {
    private readonly object _queueGate = new();
    private readonly Dictionary<Guid, Task> _tails = [];
    private RunEventHub? _owner = owner;
    private Func<RunEvent, CancellationToken, Task>? _observer = observer;

    public bool Required { get; } = required;
    public long Id { get; } = id;
    public Guid? ScopeId { get; } = scopeId;
    public Guid? TargetRunId { get; set; }

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
        if (!Required &&
            (_tails.ContainsKey(runEvent.RunId) ||
                _tails.Count >= MaximumConcurrentOptionalDeliveries))
        {
          return Task.CompletedTask;
        }

        previous = _tails.GetValueOrDefault(runEvent.RunId, Task.CompletedTask);
        _tails[runEvent.RunId] = completion.Task;
      }

      if (Required)
      {
        _ = DeliverAsync(
            previous,
            completion,
            currentObserver,
            runEvent,
            cancellationToken);
      }
      else
      {
        _ = Task.Run(() => DeliverAsync(
            previous,
            completion,
            currentObserver,
            runEvent,
            cancellationToken));
      }

      return completion.Task;
    }

    public void Dispose()
    {
      var currentOwner = Interlocked.Exchange(ref _owner, null);
      Interlocked.Exchange(ref _observer, null);
      currentOwner?.Unsubscribe(Id);
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

        var previousSubscription = deliveringSubscription.Value;
        deliveringSubscription.Value = Id;
        try
        {
          await currentObserver(runEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
          deliveringSubscription.Value = previousSubscription;
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
