using Wdem.Core.Runs;
using Xunit;

namespace Wdem.Core.Tests.Runs;

public sealed class RunEventHubTests
{
  [Fact]
  public async Task PublishAsync_DeliversEventsToSubscriberInOrder()
  {
    var received = new List<long>();
    IRunEventSink sink = new RunEventHub();
    using var subscription = sink.Subscribe((runEvent, _) =>
    {
      received.Add(runEvent.Sequence);
      return Task.CompletedTask;
    });

    await sink.PublishAsync(Event(1), CancellationToken.None);
    await AssertEventuallyAsync(() => received.Count == 1);
    await sink.PublishAsync(Event(2), CancellationToken.None);
    await AssertEventuallyAsync(() => received.Count == 2);

    Assert.Equal([1L, 2L], received);
  }

  [Fact]
  public async Task PublishAsync_ContinuesDeliveringWhenAnObserverThrows()
  {
    var received = new List<long>();
    IRunEventSink sink = new RunEventHub();
    using var failing = sink.Subscribe((_, _) =>
        throw new InvalidOperationException("observer failed"));
    using var healthy = sink.Subscribe((runEvent, _) =>
    {
      received.Add(runEvent.Sequence);
      return Task.CompletedTask;
    });

    await sink.PublishAsync(Event(1), CancellationToken.None);

    Assert.Equal([1L], received);
  }

  [Fact]
  public async Task PublishAsync_PropagatesRequiredObserverFailure()
  {
    IRunEventSink sink = new RunEventHub();
    using var required = sink.SubscribeRequired((_, _) =>
        throw new IOException("output failed"));

    var error = await Assert.ThrowsAsync<RequiredRunEventDeliveryException>(() =>
        sink.PublishAsync(Event(1), CancellationToken.None));

    Assert.Equal("output failed", error.Cause.Message);
  }

  [Fact]
  public async Task DisposedSubscription_StopsReceivingEvents()
  {
    var received = new List<long>();
    IRunEventSink sink = new RunEventHub();
    var subscription = sink.Subscribe((runEvent, _) =>
    {
      received.Add(runEvent.Sequence);
      return Task.CompletedTask;
    });
    await sink.PublishAsync(Event(1), CancellationToken.None);
    await AssertEventuallyAsync(() => received.Count == 1);

    subscription.Dispose();
    await sink.PublishAsync(Event(2), CancellationToken.None);

    Assert.Equal([1L], received);
  }

  [Fact]
  public async Task PublishAsync_SerializesConcurrentPublications()
  {
    var received = new List<long>();
    var firstEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    IRunEventSink sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      received.Add(runEvent.Sequence);
      if (runEvent.Sequence == 1)
      {
        firstEntered.SetResult();
        await releaseFirst.Task.WaitAsync(cancellationToken);
      }
    });

    var first = sink.PublishAsync(Event(1), CancellationToken.None);
    await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = sink.PublishAsync(Event(2), CancellationToken.None);

    Assert.False(second.IsCompleted);
    Assert.Equal([1L], received);
    releaseFirst.SetResult();
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal([1L, 2L], received);
  }

  [Fact]
  public async Task PublishAsync_ReentrantPublicationDoesNotDeadlock()
  {
    var received = new List<long>();
    IRunEventSink sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      received.Add(runEvent.Sequence);
      if (runEvent.Sequence == 1)
      {
        await sink.PublishAsync(Event(2), cancellationToken);
      }
    });

    await sink.PublishAsync(Event(1), CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(5));

    await AssertEventuallyAsync(() => received.Count == 2);
    Assert.Equal([1L, 2L], received);
  }

  [Fact]
  public async Task PublishAsync_ReentrantRequiredFailurePropagatesToOuterPublication()
  {
    IRunEventSink sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      if (runEvent.Sequence == 1)
      {
        await sink.PublishAsync(Event(2), cancellationToken);
      }
      else
      {
        throw new IOException("recursive output failed");
      }
    });

    var error = await Assert.ThrowsAsync<RequiredRunEventDeliveryException>(() =>
        sink.PublishAsync(Event(1), CancellationToken.None));

    Assert.Equal("recursive output failed", error.Cause.Message);
  }

  [Fact]
  public async Task PublishAsync_WaitsForReentrantRequiredDeliveryBeforeReturning()
  {
    var deferredEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseDeferred = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    IRunEventSink sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      if (runEvent.Sequence == 1)
      {
        await sink.PublishAsync(Event(2), cancellationToken);
      }
      else
      {
        deferredEntered.SetResult();
        await releaseDeferred.Task;
      }
    });
    var publication = sink.PublishAsync(Event(1), CancellationToken.None);
    await deferredEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.False(publication.IsCompleted);
    releaseDeferred.SetResult();
    await publication.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task PublishAsync_CancellationBoundsHangingReentrantRequiredDelivery()
  {
    using var cancellation = new CancellationTokenSource();
    var deferredEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseDeferred = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    IRunEventSink sink = new RunEventHub();
    using var subscription = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      if (runEvent.Sequence == 1)
      {
        await sink.PublishAsync(Event(2), cancellationToken);
      }
      else
      {
        deferredEntered.SetResult();
        await releaseDeferred.Task;
      }
    });
    var publication = sink.PublishAsync(Event(1), cancellation.Token);
    await deferredEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    try
    {
      await cancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
          publication.WaitAsync(TimeSpan.FromSeconds(1)));
    }
    finally
    {
      releaseDeferred.TrySetResult();
    }
  }

  [Fact]
  public async Task PublishAsync_HangingOptionalObserverDoesNotBlockRequiredDelivery()
  {
    var optionalEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var neverCompletes = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var received = new List<long>();
    IRunEventSink sink = new RunEventHub();
    using var optional = sink.Subscribe(async (_, cancellationToken) =>
    {
      optionalEntered.TrySetResult();
      await neverCompletes.Task.WaitAsync(cancellationToken);
    });
    using var required = sink.SubscribeRequired((runEvent, _) =>
    {
      received.Add(runEvent.Sequence);
      return Task.CompletedTask;
    });

    await sink.PublishAsync(Event(1), CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(1));
    await optionalEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await sink.PublishAsync(Event(2), CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(1));

    Assert.Equal([1L, 2L], received);
  }

  [Fact]
  public async Task PublishAsync_DoesNotInvokeBlockingOptionalObserverUnderRunGate()
  {
    using var releaseOptional = new ManualResetEventSlim();
    var requiredObserved = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    IRunEventSink sink = new RunEventHub();
    using var optional = sink.Subscribe((_, _) =>
    {
      releaseOptional.Wait();
      return Task.CompletedTask;
    });
    using var required = sink.SubscribeRequired((_, _) =>
    {
      requiredObserved.SetResult();
      return Task.CompletedTask;
    });

    var publication = Task.Run(() => sink.PublishAsync(Event(1), CancellationToken.None));
    try
    {
      await requiredObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
      await publication.WaitAsync(TimeSpan.FromSeconds(1));
    }
    finally
    {
      releaseOptional.Set();
    }
  }

  [Fact]
  public async Task PublishAsync_DropsQueuedEventsForHangingOptionalObserver()
  {
    var entered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var received = new List<long>();
    IRunEventSink sink = new RunEventHub();
    using var optional = sink.Subscribe(async (runEvent, cancellationToken) =>
    {
      received.Add(runEvent.Sequence);
      entered.TrySetResult();
      await release.Task.WaitAsync(cancellationToken);
    });
    await sink.PublishAsync(Event(1), CancellationToken.None);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

    for (var sequence = 2; sequence <= 100; sequence++)
    {
      await sink.PublishAsync(Event(sequence), CancellationToken.None)
          .WaitAsync(TimeSpan.FromSeconds(1));
    }

    release.SetResult();
    await AssertEventuallyAsync(() => received.Count == 1);
    Assert.Equal([1L], received);
  }

  [Fact]
  public async Task ScopedRequiredSubscriptionsOnlyReceiveTheirOperationPublications()
  {
    IRunEventSink sink = new RunEventHub();
    using var ready = new CountdownEvent(2);
    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstReceived = new List<long>();
    var secondReceived = new List<long>();
    var first = Task.Run(() => PublishInScopeAsync(
        sink,
        Event(1),
        firstReceived,
        ready,
        start.Task,
        throwOnDelivery: true));
    var second = Task.Run(() => PublishInScopeAsync(
        sink,
        Event(2),
        secondReceived,
        ready,
        start.Task,
        throwOnDelivery: false));
    Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));

    start.SetResult();

    var error = await Assert.ThrowsAsync<RequiredRunEventDeliveryException>(() => first);
    await second.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.Equal("scoped output failed", error.Cause.Message);
    Assert.Equal([1L], firstReceived);
    Assert.Equal([2L], secondReceived);
  }

  [Fact]
  public async Task NestedPublish_StillAwaitsOtherRequiredSubscriber()
  {
    IRunEventSink sink = new RunEventHub();
    var recursiveDelivery = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var recursive = sink.SubscribeRequired(async (runEvent, cancellationToken) =>
    {
      if (runEvent.Sequence == 1)
      {
        await sink.PublishAsync(Event(2, runEvent.RunId), cancellationToken);
      }
      else
      {
        recursiveDelivery.SetResult();
      }
    });
    using var failing = sink.SubscribeRequired((runEvent, _) =>
        runEvent.Sequence == 2
            ? Task.FromException(new IOException("nested required delivery failed"))
            : Task.CompletedTask);

    var error = await Assert.ThrowsAsync<RequiredRunEventDeliveryException>(
        () => sink.PublishAsync(Event(1), CancellationToken.None));

    Assert.Equal("nested required delivery failed", error.Cause.Message);
    await recursiveDelivery.Task.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task PublishAsync_SlowObserverForOneRunDoesNotBlockAnotherRun()
  {
    var firstRun = Guid.Parse("fc1e441a-2db0-4a70-81dc-f6948ce07813");
    var secondRun = Guid.Parse("328194b6-7c0a-4102-8904-a5ce351219d3");
    var firstEntered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var secondObserved = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    IRunEventSink sink = new RunEventHub();
    using var subscription = sink.Subscribe(async (runEvent, cancellationToken) =>
    {
      if (runEvent.RunId == firstRun)
      {
        firstEntered.SetResult();
        await releaseFirst.Task.WaitAsync(cancellationToken);
      }
      else
      {
        secondObserved.SetResult();
      }
    });
    var first = sink.PublishAsync(Event(1, firstRun), CancellationToken.None);
    await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    try
    {
      await sink.PublishAsync(Event(1, secondRun), CancellationToken.None)
          .WaitAsync(TimeSpan.FromSeconds(1));
      await secondObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }
    finally
    {
      releaseFirst.SetResult();
      await first.WaitAsync(TimeSpan.FromSeconds(5));
    }
  }

  [Fact]
  public async Task Dispose_DoesNotCorruptPublicationAlreadyInProgress()
  {
    var entered = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var sink = new RunEventHub();
    using var subscription = sink.Subscribe(async (_, cancellationToken) =>
    {
      entered.SetResult();
      await release.Task.WaitAsync(cancellationToken);
    });
    var publication = sink.PublishAsync(Event(1), CancellationToken.None);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    sink.Dispose();
    release.SetResult();

    await publication.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task ScopedOptionalObserverFailureDoesNotFailPublisher()
  {
    using var sink = new RunEventHub();
    var runId = Guid.NewGuid();
    var deliveries = 0;
    using var subscription = sink.SubscribeScoped((_, _) =>
    {
      deliveries++;
      return Task.FromException(new InvalidOperationException("UI dispatcher stopped"));
    });
    sink.BindCurrentScopeToRun(runId);

    await sink.PublishAsync(Event(1, runId), CancellationToken.None);

    Assert.Equal(1, deliveries);
  }

  [Fact]
  public async Task ScopedOptionalObserverCancellationStillCancelsPublisher()
  {
    using var sink = new RunEventHub();
    using var cancellation = new CancellationTokenSource();
    var runId = Guid.NewGuid();
    using var subscription = sink.SubscribeScoped((_, cancellationToken) =>
    {
      cancellation.Cancel();
      return Task.FromCanceled(cancellationToken);
    });
    sink.BindCurrentScopeToRun(runId);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        sink.PublishAsync(Event(1, runId), cancellation.Token));
  }

  private static RunEvent Event(long sequence, Guid? runId = null) => new(
      runId ?? Guid.Parse("74ebec79-51ea-4c67-aa4d-71d542dca987"),
      sequence,
      DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
      RunEventKind.Log,
      null,
      null,
      null,
      $"event-{sequence}",
      null);

  private static async Task PublishInScopeAsync(
      IRunEventSink sink,
      RunEvent runEvent,
      List<long> received,
      CountdownEvent ready,
      Task start,
      bool throwOnDelivery)
  {
    using var subscription = sink.SubscribeRequiredScoped((observed, _) =>
    {
      received.Add(observed.Sequence);
      return throwOnDelivery
          ? Task.FromException(new IOException("scoped output failed"))
          : Task.CompletedTask;
    });
    sink.BindCurrentScopeToRun(runEvent.RunId);
    ready.Signal();
    await start;
    await sink.PublishAsync(runEvent, CancellationToken.None);
  }

  private static async Task AssertEventuallyAsync(Func<bool> condition)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition())
    {
      await Task.Delay(10, timeout.Token);
    }
  }
}
