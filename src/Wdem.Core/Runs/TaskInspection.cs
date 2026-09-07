namespace Wdem.Core.Runs;

using Wdem.Domain.Versions;

public sealed record TaskInspection(
    string TaskId,
    bool DetectSucceeded,
    string? DetectedVersion,
    ComplianceStatus Compliance,
    string? VersionRequirement,
    StepReport DetectStep)
{
  public bool IsSatisfied => Compliance == ComplianceStatus.Satisfied;
}
