namespace Wdem.Core.Runs;

public enum TaskExecutionState
{
  NotSelected,
  Pending,
  Ready,
  Detecting,
  RunningPre,
  Applying,
  RunningPost,
  Verifying,
  Running,
  Cancelling,
  Satisfied,
  Succeeded,
  Failed,
  Cancelled,
  Blocked
}
