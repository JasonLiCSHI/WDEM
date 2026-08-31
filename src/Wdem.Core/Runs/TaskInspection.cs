namespace Wdem.Core.Runs;

public sealed record TaskInspection(
    string TaskId,
    bool DetectSucceeded,
    string? DetectedVersion,
    TaskComplianceState Compliance,
    string? VersionRequirement,
    StepReport DetectStep)
{
  public bool IsSatisfied => Compliance == TaskComplianceState.Satisfied;
}
