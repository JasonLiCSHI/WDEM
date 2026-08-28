using System.Text.RegularExpressions;
using Wdem.Core.Execution;

namespace Wdem.Core.Runs;

public sealed partial class LogRedactor
{
  private readonly object _sensitiveValuesGate = new();
  private string[] _sensitiveValues = [];

  public LogRedactor(IEnumerable<string>? sensitiveValues = null)
  {
    if (sensitiveValues is not null)
    {
      RegisterSensitiveValues(sensitiveValues);
    }
  }

  public void RegisterSensitiveValues(IEnumerable<string> sensitiveValues)
  {
    ArgumentNullException.ThrowIfNull(sensitiveValues);
    var additions = sensitiveValues
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    lock (_sensitiveValuesGate)
    {
      _sensitiveValues = _sensitiveValues
          .Concat(additions)
          .Distinct(StringComparer.Ordinal)
          .OrderByDescending(value => value.Length)
          .ToArray();
    }
  }

  public string Redact(string value)
  {
    ArgumentNullException.ThrowIfNull(value);

    var redacted = SecretBlockPattern().Replace(value, match =>
        $"-----BEGIN {match.Groups["type"].Value}-----\n***\n-----END {match.Groups["type"].Value}-----");
    redacted = AuthorizationBearerPattern().Replace(redacted, "${prefix}***");
    redacted = StandaloneBearerPattern().Replace(redacted, "${prefix}***");
    redacted = QuotedAssignmentPattern().Replace(
        redacted,
        "${prefix}${quote}***${quote}");
    redacted = UnquotedAssignmentPattern().Replace(redacted, "${prefix}***");

    string[] sensitiveValues;
    lock (_sensitiveValuesGate)
    {
      sensitiveValues = _sensitiveValues;
    }

    foreach (var sensitiveValue in sensitiveValues)
    {
      redacted = redacted.Replace(sensitiveValue, "***", StringComparison.Ordinal);
    }

    return redacted;
  }

  public StructuredError Redact(StructuredError error)
  {
    ArgumentNullException.ThrowIfNull(error);
    return StructuredError.CreateSnapshot(
        error.Code,
        Redact(error.Summary),
        Redact(error.Detail),
        RedactNullable(error.ResourceId),
        RedactNullable(error.StepId),
        error.ProcessExitCode,
        RedactNullable(error.LogLocation),
        RedactNullable(error.SuggestedAction),
        error.IsRetryable,
        error.UnderlyingExceptionType,
        RedactNullable(error.UnderlyingExceptionMessage));
  }

  public RunLogEntry Redact(RunLogEntry entry)
  {
    ArgumentNullException.ThrowIfNull(entry);
    return entry with
    {
      ResourceId = RedactNullable(entry.ResourceId),
      StepId = RedactNullable(entry.StepId),
      Message = Redact(entry.Message),
      Error = entry.Error is null ? null : Redact(entry.Error)
    };
  }

  public RunEvent Redact(RunEvent runEvent)
  {
    ArgumentNullException.ThrowIfNull(runEvent);
    return runEvent with
    {
      ResourceId = RedactNullable(runEvent.ResourceId),
      StepId = RedactNullable(runEvent.StepId),
      Message = Redact(runEvent.Message),
      Error = runEvent.Error is null ? null : Redact(runEvent.Error)
    };
  }

  private string? RedactNullable(string? value) => value is null ? null : Redact(value);

  [GeneratedRegex(
      @"(?<prefix>\bauthorization\s*:\s*bearer\s+)[^\s,;]+",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex AuthorizationBearerPattern();

  [GeneratedRegex(
      @"(?<prefix>\bbearer[ \t]+)(?:(?=[a-z0-9._~+/=-]{6,})(?=[a-z0-9._~+/=-]*(?:[0-9._~+/-]))[a-z0-9._~+/=-]+|[a-z]{16,})",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex StandaloneBearerPattern();

  [GeneratedRegex(
      "(?<prefix>(?<![a-z0-9_-])[\\\"']?(?:password|token|api[-_]?key|thumbprint)(?![a-z0-9_-])[\\\"']?\\s*[:=]\\s*)(?<quote>[\\\"'])(?:\\\\.|(?!\\k<quote>).)*?\\k<quote>",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex QuotedAssignmentPattern();

  [GeneratedRegex(
      @"(?<prefix>(?<![a-z0-9_-])[""']?(?:password|token|api[-_]?key|thumbprint)(?![a-z0-9_-])[""']?\s*[:=]\s*)[^\s;,""']+",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex UnquotedAssignmentPattern();

  [GeneratedRegex(
      @"-----BEGIN (?<type>(?:[A-Z0-9 ]*PRIVATE KEY|CERTIFICATE))-----[\s\S]*?-----END \k<type>-----",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex SecretBlockPattern();
}
