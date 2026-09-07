using Wdem.Domain.Events;

namespace Wdem.Application.Events;

public interface IDomainEventHandler
{
  void Handle(IDomainEvent domainEvent);
}
