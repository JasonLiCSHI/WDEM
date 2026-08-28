using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Runs;

public sealed record ResourceResult
{
  private double _progress;
  private IReadOnlyList<StepResult> _stepResults = [];

  public required string ResourceId { get; init; }
  public required ExecutionState State { get; init; }
  public ExecutionOutcome? Outcome { get; init; }
  public ComplianceStatus? FinalCompliance { get; init; }
  public DetectedState? DetectedBefore { get; init; }
  public DetectedState? DetectedAfter { get; init; }
  public double Progress
  {
    get => _progress;
    init => _progress = RunProgress.Normalize(value);
  }

  public string? Message { get; init; }
  public DateTimeOffset? StartedAtUtc { get; init; }
  public DateTimeOffset? EndedAtUtc { get; init; }
  public StructuredError? Error { get; init; }
  public RestartPolicy RestartRequirement { get; init; }
  public IReadOnlyList<StepResult> StepResults
  {
    get => _stepResults;
    init => _stepResults = Array.AsReadOnly(
        (value ?? throw new ArgumentNullException(nameof(value))).ToArray());
  }
}

internal static class RunProgress
{
  public static double Normalize(double value) => double.IsNaN(value) || double.IsNegativeInfinity(value)
      ? 0
      : double.IsPositiveInfinity(value) ? 1 : Math.Clamp(value, 0, 1);
}
