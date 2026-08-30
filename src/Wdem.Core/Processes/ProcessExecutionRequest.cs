using System.Text.Json.Serialization;

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
  public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(10);

  public ProcessCancellationMode CancellationMode { get; init; } =
      ProcessCancellationMode.ThroughCompletion;

  [JsonIgnore]
  public Action? OnStarted { get; init; }
}
