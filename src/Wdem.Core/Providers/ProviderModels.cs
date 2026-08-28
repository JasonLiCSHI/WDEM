using System.Text.Json.Serialization;
using Wdem.Core.Resources;

namespace Wdem.Core.Providers;

public enum DetectionOutcome
{
  Succeeded,
  Failed,
  Unsupported
}

public enum ComplianceStatus
{
  Satisfied,
  Missing,
  VersionMismatch,
  ConfigurationMismatch,
  DetectionFailed,
  Unsupported
}

public enum PlanAction
{
  None,
  Install,
  Configure,
  Repair,
  Upgrade
}

public enum ProviderLogLevel
{
  Trace,
  Debug,
  Info,
  Warning,
  Error
}

public enum ApplyOutcome
{
  Succeeded,
  Cancelled,
  NotRequired
}

public sealed record ProviderCapabilities
{
  public bool SupportsSource { get; init; }
  public bool SupportsVersionConstraints { get; init; }
  public bool SupportsInstallerParameters { get; init; }
  public bool SupportsInProgressCancellation { get; init; }
  public int MaxConcurrentOperations { get; init; } = 1;
  public string? ConcurrencyGroup { get; init; }
}

public sealed record ProviderValidationResult
{
  public required IReadOnlyList<string> Errors { get; init; }
  public bool IsValid => Errors.Count == 0;

  public static ProviderValidationResult Valid { get; } =
      new() { Errors = Array.Empty<string>() };

  public static ProviderValidationResult Invalid(params string[] errors) =>
      new() { Errors = errors };
}

public sealed record DetectedState
{
  public required string ResourceId { get; init; }
  public required DetectionOutcome Outcome { get; init; }
  public bool Exists { get; init; }
  public string? Version { get; init; }
  public IReadOnlyDictionary<string, string> Evidence { get; init; } =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
  public string? Error { get; init; }
}

public sealed record PlanStep
{
  public required string Id { get; init; }
  public required string Description { get; init; }
  public required PlanAction Action { get; init; }
  public required PrivilegeRequirement PrivilegeRequirement { get; init; }
  public required RestartPolicy RestartPolicy { get; init; }
}

public sealed record ResourcePlan
{
  public required string ResourceId { get; init; }
  public required string ResourceType { get; init; }
  public required string ProviderName { get; init; }
  public required string DesiredStateFingerprint { get; init; }
  public required ComplianceStatus Compliance { get; init; }
  public required bool IsExecutable { get; init; }
  public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();
  public string? Error { get; init; }
  public bool RequiresApply => Steps.Count > 0;
}

public sealed record ProviderProgress(string Stage, double Percent, string Message)
{
  [JsonConstructor]
  public ProviderProgress(
      string stage,
      double percent,
      string message,
      string? stepId,
      ProviderLogLevel logLevel = ProviderLogLevel.Info)
      : this(stage, percent, message)
  {
    StepId = stepId;
    LogLevel = logLevel;
  }

  public string? StepId { get; init; }
  public ProviderLogLevel LogLevel { get; init; } = ProviderLogLevel.Info;
}

public sealed record ResourceApplyResult
{
  public required string ResourceId { get; init; }
  public required ApplyOutcome Outcome { get; init; }
}

public sealed record VerificationResult
{
  public required string ResourceId { get; init; }
  public required ComplianceStatus Compliance { get; init; }
  public required DetectedState DetectedState { get; init; }
  public string? Message { get; init; }
}
