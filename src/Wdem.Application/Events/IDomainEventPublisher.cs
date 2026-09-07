using Wdem.Domain.Events;

namespace Wdem.Application.Events;

public interface IDomainEventPublisher
{
  void Publish(IDomainEvent domainEvent);
}
