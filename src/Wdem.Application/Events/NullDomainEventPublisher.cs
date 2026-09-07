using Wdem.Domain.Events;

namespace Wdem.Application.Events;

public sealed class NullDomainEventPublisher : IDomainEventPublisher
{
  public static NullDomainEventPublisher Instance { get; } = new();

  private NullDomainEventPublisher()
  {
  }

  public void Publish(IDomainEvent domainEvent) => ArgumentNullException.ThrowIfNull(domainEvent);
}
