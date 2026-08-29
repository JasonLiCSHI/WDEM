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
    await sink.PublishAsync(Event(2), CancellationToken.None);

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
    using var subscription = sink.Subscribe(async (runEvent, cancellationToken) =>
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

  private static RunEvent Event(long sequence) => new(
      Guid.Parse("74ebec79-51ea-4c67-aa4d-71d542dca987"),
      sequence,
      DateTimeOffset.Parse("2026-08-29T00:00:00Z"),
      RunEventKind.Log,
      null,
      null,
      null,
      $"event-{sequence}",
      null);
}
