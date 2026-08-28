using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Wdem.Core.Execution;
using Wdem.Core.Resources;
using Wdem.Core.Versions;

namespace Wdem.Core.Providers;

public enum DetectionOutcome
{
  Succeeded,
  Failed,
  Unsupported,
  Cancelled
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

public enum ApplyOutcome
{
  Succeeded,
  Cancelled,
  NotRequired,
  Failed
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
  private IReadOnlyList<string> _errors = ProviderCollectionSnapshot.EmptyList<string>();
  private IReadOnlyList<StructuredError> _structuredErrors =
      ProviderCollectionSnapshot.EmptyList<StructuredError>();

  public IReadOnlyList<string> Errors
  {
    get => _errors;
    init => _errors = ProviderCollectionSnapshot.List(value);
  }

  public IReadOnlyList<StructuredError> StructuredErrors
  {
    get => _structuredErrors;
    init => _structuredErrors = ProviderCollectionSnapshot.List(value);
  }

  public bool IsValid => Errors.Count == 0 && StructuredErrors.Count == 0;

  public static ProviderValidationResult Valid { get; } =
      new() { Errors = Array.Empty<string>() };

  public static ProviderValidationResult Invalid(params string[] errors) =>
      new() { Errors = errors };

  public static ProviderValidationResult Invalid(
      StructuredError error,
      params StructuredError[] additionalErrors) =>
      new()
      {
        Errors = Array.Empty<string>(),
        StructuredErrors = [error, .. additionalErrors]
      };
}

public sealed record DetectedState
{
  private IReadOnlyList<SemanticVersion> _installedVersions =
      ProviderCollectionSnapshot.EmptyList<SemanticVersion>();
  private IReadOnlyDictionary<string, string> _evidence =
      ProviderCollectionSnapshot.EmptyDictionary();

  public required string ResourceId { get; init; }
  public required DetectionOutcome Outcome { get; init; }
  public bool Exists { get; init; }
  public string? Version { get; init; }
  public IReadOnlyList<SemanticVersion> InstalledVersions
  {
    get => _installedVersions;
    init => _installedVersions = ProviderCollectionSnapshot.List(value);
  }
  public string? ConfigurationHash { get; init; }
  public DateTimeOffset DetectedAtUtc { get; init; } = DateTimeOffset.UtcNow;
  public IReadOnlyDictionary<string, string> Evidence
  {
    get => _evidence;
    init => _evidence = ProviderCollectionSnapshot.Dictionary(value);
  }
  public string? Error { get; init; }
  public StructuredError? StructuredError { get; init; }
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
  private IReadOnlyList<PlanStep> _steps = ProviderCollectionSnapshot.EmptyList<PlanStep>();
  private IReadOnlyList<StructuredError> _structuredErrors =
      ProviderCollectionSnapshot.EmptyList<StructuredError>();

  public required string ResourceId { get; init; }
  public required string ResourceType { get; init; }
  public required string ProviderName { get; init; }
  public required string DesiredStateFingerprint { get; init; }
  public required ComplianceStatus Compliance { get; init; }
  public required bool IsExecutable { get; init; }
  public IReadOnlyList<PlanStep> Steps
  {
    get => _steps;
    init => _steps = ProviderCollectionSnapshot.List(value);
  }
  public string? Error { get; init; }
  public IReadOnlyList<StructuredError> StructuredErrors
  {
    get => _structuredErrors;
    init => _structuredErrors = ProviderCollectionSnapshot.List(value);
  }
  public bool RequiresApply => Steps.Count > 0;
}

public sealed record ProviderProgress
{
  private string _stage = string.Empty;
  private double _percent;
  private string _message = string.Empty;
  private string? _stepId;

  [JsonConstructor]
  public ProviderProgress(
      string stage,
      double percent,
      string message,
      string? stepId = null,
      ProviderLogLevel logLevel = ProviderLogLevel.Info)
  {
    Stage = stage;
    Percent = percent;
    Message = message;
    StepId = stepId;
    LogLevel = logLevel;
  }

  public string Stage
  {
    get => _stage;
    init => _stage = DiagnosticTextSanitizer.Sanitize(
        value ?? throw new ArgumentNullException(nameof(value)));
  }

  public double Percent
  {
    get => _percent;
    init => _percent = NormalizeProgress(value);
  }

  public string Message
  {
    get => _message;
    init => _message = DiagnosticTextSanitizer.Sanitize(
        value ?? throw new ArgumentNullException(nameof(value)));
  }

  public string? StepId
  {
    get => _stepId;
    init => _stepId = value is null ? null : DiagnosticTextSanitizer.Sanitize(value);
  }

  public ProviderLogLevel LogLevel { get; init; } = ProviderLogLevel.Info;

  public void Deconstruct(out string stage, out double percent, out string message)
  {
    stage = Stage;
    percent = Percent;
    message = Message;
  }

  private static double NormalizeProgress(double progress)
  {
    if (double.IsNaN(progress) || double.IsNegativeInfinity(progress))
    {
      return 0;
    }

    if (double.IsPositiveInfinity(progress))
    {
      return 1;
    }

    return Math.Clamp(progress, 0, 1);
  }
}

public sealed record ResourceApplyResult
{
  private IReadOnlyList<ProviderStepResult> _stepResults =
      ProviderCollectionSnapshot.EmptyList<ProviderStepResult>();
  private IReadOnlyList<StructuredError> _diagnostics =
      ProviderCollectionSnapshot.EmptyList<StructuredError>();

  public required string ResourceId { get; init; }
  public required ApplyOutcome Outcome { get; init; }
  public StructuredError? Error { get; init; }
  public IReadOnlyList<ProviderStepResult> StepResults
  {
    get => _stepResults;
    init => _stepResults = ProviderCollectionSnapshot.List(value);
  }

  public IReadOnlyList<StructuredError> Diagnostics
  {
    get => _diagnostics;
    init => _diagnostics = ProviderCollectionSnapshot.List(value);
  }
}

public sealed record ProviderStepResult
{
  private double _progress;
  private string? _message;

  public required string StepId { get; init; }
  public required PlanAction Action { get; init; }
  public double Progress
  {
    get => _progress;
    init => _progress = double.IsNaN(value) || double.IsNegativeInfinity(value)
        ? 0
        : double.IsPositiveInfinity(value) ? 1 : Math.Clamp(value, 0, 1);
  }

  public int? ProcessExitCode { get; init; }
  public string? Message
  {
    get => _message;
    init => _message = value is null ? null : DiagnosticTextSanitizer.Sanitize(value);
  }

  public StructuredError? Error { get; init; }
}

public sealed record VerificationResult
{
  public required string ResourceId { get; init; }
  public required ComplianceStatus Compliance { get; init; }
  public required DetectedState DetectedState { get; init; }
  public string? Message { get; init; }
}

internal static class ProviderCollectionSnapshot
{
  public static IReadOnlyList<T> EmptyList<T>() => Array.AsReadOnly(Array.Empty<T>());

  public static IReadOnlyList<T> List<T>(IReadOnlyList<T>? values)
  {
    ArgumentNullException.ThrowIfNull(values);

    var snapshot = values.ToArray();
    if (snapshot.Any(value => value is null))
    {
      throw new ArgumentException("Provider model collections cannot contain null elements.", nameof(values));
    }

    return Array.AsReadOnly(snapshot);
  }

  public static IReadOnlyDictionary<string, string> EmptyDictionary() =>
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase).ToFrozenDictionary(
          StringComparer.OrdinalIgnoreCase);

  public static IReadOnlyDictionary<string, string> Dictionary(
      IReadOnlyDictionary<string, string>? values)
  {
    ArgumentNullException.ThrowIfNull(values);
    var snapshot = values.ToArray();
    if (snapshot.Any(pair => pair.Key is null || pair.Value is null))
    {
      throw new ArgumentException(
          "Provider model dictionaries cannot contain null keys or values.",
          nameof(values));
    }

    return snapshot.ToFrozenDictionary(
        pair => pair.Key,
        pair => pair.Value,
        StringComparer.OrdinalIgnoreCase);
  }
}
