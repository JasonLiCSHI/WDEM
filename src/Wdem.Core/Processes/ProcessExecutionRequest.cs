namespace Wdem.Core.Processes;

public enum ProcessCancellationMode
{
  ThroughCompletion,
  LaunchOnly
}

public sealed record ProcessExecutionRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null)
{
  public ProcessCancellationMode CancellationMode { get; init; } =
      ProcessCancellationMode.ThroughCompletion;
}
