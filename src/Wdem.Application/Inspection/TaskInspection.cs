using Wdem.Application.Execution;
using Wdem.Domain.Versions;

namespace Wdem.Application.Inspection;

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
