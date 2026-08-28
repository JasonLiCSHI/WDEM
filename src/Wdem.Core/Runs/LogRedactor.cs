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
    redacted = StandaloneBearerCandidatePattern().Replace(redacted, RedactStandaloneBearer);
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
      @"(?<prefix>\bbearer[ \t]+)(?<token>[a-z0-9._~+/=-]+)",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
  private static partial Regex StandaloneBearerCandidatePattern();

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

  private static string RedactStandaloneBearer(Match match)
  {
    var rawToken = match.Groups["token"].Value;
    var token = rawToken.TrimEnd('.');
    if (!LooksLikeStandaloneBearerToken(token))
    {
      return match.Value;
    }

    var trailingPeriods = rawToken[token.Length..];
    return $"{match.Groups["prefix"].Value}***{trailingPeriods}";
  }

  private static bool LooksLikeStandaloneBearerToken(string token)
  {
    var jwtSegments = token.Split('.');
    if (jwtSegments.Length >= 3 && jwtSegments.All(IsBase64UrlSegment))
    {
      return true;
    }

    var hasDigit = token.Any(char.IsAsciiDigit);
    var strongTokenPunctuationKinds = token
        .Where(character => character is '-' or '_' or '~' or '+' or '/' or '=')
        .Distinct()
        .Count();
    var hasTokenFeatures = strongTokenPunctuationKinds >= 2
        || (hasDigit && strongTokenPunctuationKinds == 1);
    // RFC 6750 syntax cannot distinguish an opaque all-letter token from prose.
    // Prefer redacting long candidates; short protocol terms remain diagnostic text.
    return (token.Length >= 12 && hasTokenFeatures)
        || token.Length >= 16;
  }

  private static bool IsBase64UrlSegment(string segment) =>
      segment.Length > 0
      && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
