namespace Wdem.Core.Runs;

public interface IRunEventSink
{
  // Callers publish only after persistence succeeds and pass the same redacted event
  // that was durably stored.
  Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken);
}
