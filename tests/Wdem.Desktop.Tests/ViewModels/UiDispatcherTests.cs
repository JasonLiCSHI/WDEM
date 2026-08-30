using Wdem.Desktop.ViewModels;
using Xunit;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class UiDispatcherTests
{
  [Fact]
  public async Task RejectedQueueReportsDispatcherUnavailableOnce()
  {
    var queue = new RejectingQueue();
    var dispatcher = new DispatcherQueueUiDispatcher(queue);

    await Assert.ThrowsAsync<UiDispatcherUnavailableException>(
        () => dispatcher.EnqueueAsync(() => throw new Xunit.Sdk.XunitException(
            "A rejected callback must not run.")));

    Assert.Equal(1, queue.EnqueueCalls);
  }

  private sealed class RejectingQueue : IDispatcherQueueAdapter
  {
    public bool HasThreadAccess => false;

    public int EnqueueCalls { get; private set; }

    public bool TryEnqueue(Action action)
    {
      EnqueueCalls++;
      return false;
    }
  }
}
