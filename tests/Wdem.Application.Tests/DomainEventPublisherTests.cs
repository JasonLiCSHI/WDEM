using Wdem.Application.Events;
using Wdem.Domain.Events;
using Xunit;

namespace Wdem.Application.Tests;

public sealed class DomainEventPublisherTests
{
  [Fact]
  public void Publish_WhenOneHandlerFails_ThenContinuesWithRemainingHandlers()
  {
    var recordingHandler = new RecordingHandler();
    var publisher = new DomainEventPublisher(
        [new ThrowingHandler(), recordingHandler]);
    var domainEvent = new TestDomainEvent("test");

    publisher.Publish(domainEvent);

    Assert.Same(domainEvent, Assert.Single(recordingHandler.Events));
  }

  private sealed record TestDomainEvent(string Value) : IDomainEvent;

  private sealed class ThrowingHandler : IDomainEventHandler
  {
    public void Handle(IDomainEvent domainEvent) =>
        throw new InvalidOperationException("Expected observer failure.");
  }

  private sealed class RecordingHandler : IDomainEventHandler
  {
    public List<IDomainEvent> Events { get; } = [];

    public void Handle(IDomainEvent domainEvent) => Events.Add(domainEvent);
  }
}
