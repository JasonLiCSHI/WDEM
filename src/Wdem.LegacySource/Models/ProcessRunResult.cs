namespace Wdem.LegacySource.Models;

public sealed record ProcessRunResult(
    bool Started,
    int? ExitCode,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError)
{
  public ProcessFailureKind FailureKind { get; init; }

  public string? FailureMessage { get; init; }
}

public enum ProcessFailureKind
{
  None,
  StartFailed,
  TimedOut,
  OutputDrainFailed,
  PostStartFailed,
  ExecutableNotFound
}
