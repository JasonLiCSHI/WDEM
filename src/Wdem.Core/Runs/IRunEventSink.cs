namespace Wdem.Core.Runs;

public interface IRunEventSink
{
  IDisposable Subscribe(Func<RunEvent, CancellationToken, Task> observer);

  IDisposable SubscribeRequired(Func<RunEvent, CancellationToken, Task> observer);

  IDisposable SubscribeRequiredScoped(Func<RunEvent, CancellationToken, Task> observer);

  void BindCurrentScopeToRun(Guid runId);

  // Callers publish only after persistence succeeds and pass the same redacted event
  // that was durably stored.
  Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken);
}
