using Wdem.Domain.Events;

namespace Wdem.Application.Events;

/// <summary>
/// Dispatches post-state-change notifications to in-process observers. Observers are
/// isolated because diagnostics must never change the outcome of an environment Task.
/// </summary>
public sealed class DomainEventPublisher : IDomainEventPublisher
{
  private readonly IReadOnlyList<IDomainEventHandler> _handlers;

  public DomainEventPublisher(IEnumerable<IDomainEventHandler> handlers)
  {
    ArgumentNullException.ThrowIfNull(handlers);
    _handlers = handlers.ToArray();
  }

  public void Publish(IDomainEvent domainEvent)
  {
    ArgumentNullException.ThrowIfNull(domainEvent);

    foreach (var handler in _handlers)
    {
      try
      {
        handler.Handle(domainEvent);
      }
      catch
      {
        // Domain-event observers are deliberately best-effort. Authoritative workflow
        // state and Task outcomes must not depend on logging or telemetry availability.
      }
    }
  }
}
