namespace Wdem.Domain.Versions;

public enum ComplianceStatus
{
  Missing,
  UpgradeRequired,
  VersionMismatch,
  Satisfied
}

public sealed record ComplianceResult(
    ComplianceStatus Status,
    SoftwareVersion? InstalledVersion,
    VersionRequirement Requirement)
{
  public static ComplianceResult Missing(VersionRequirement requirement)
  {
    ArgumentNullException.ThrowIfNull(requirement);
    return new ComplianceResult(ComplianceStatus.Missing, null, requirement);
  }
}
