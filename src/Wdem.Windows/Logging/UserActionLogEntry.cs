namespace Wdem.Windows.Logging;

public enum UserActionOutcome
{
  Requested,
  Accepted,
  Rejected,
  Completed,
  Cancelled,
  Failed
}

public sealed record UserActionLogEntry(
    string Operation,
    string Outcome,
    string? ProfileId,
    IReadOnlyList<string> TaskIds);
