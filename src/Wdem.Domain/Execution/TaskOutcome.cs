namespace Wdem.Domain.Execution;

public enum TaskOutcome
{
  Succeeded,
  Failed,
  Cancelled,
  NotRequired,
  Skipped,
  Blocked
}
