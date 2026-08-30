using System.Text;
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

  public void RegisterSensitiveParameters(
      IEnumerable<KeyValuePair<string, string?>> parameters)
  {
    ArgumentNullException.ThrowIfNull(parameters);
    RegisterSensitiveValues(parameters
        .Where(parameter => SensitiveTextRedactor.IsSensitiveKey(parameter.Key))
        .Select(parameter => parameter.Value)
        .OfType<string>());
  }

  public string Redact(string value)
  {
    ArgumentNullException.ThrowIfNull(value);

    var redacted = RedactSecretBlocks(value);
    redacted = AuthorizationBearerPattern().Replace(redacted, "${prefix}***");
    redacted = StandaloneBearerCandidatePattern().Replace(redacted, RedactStandaloneBearer);
    redacted = SensitiveTextRedactor.RedactAssignments(redacted, "***");

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
        RedactNullable(error.UnderlyingExceptionType),
        RedactNullable(error.UnderlyingExceptionMessage));
  }

  public string? RedactNamedValue(string name, string? value)
  {
    ArgumentNullException.ThrowIfNull(name);
    return value is null
        ? null
        : SensitiveTextRedactor.IsSensitiveKey(name) ? "***" : Redact(value);
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
      @"(?<prefix>\bauthorization\s*[:=]\s*bearer\s+)[^\s,;]+",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex AuthorizationBearerPattern();

  [GeneratedRegex(
      @"(?<prefix>\bbearer[ \t]+)(?<token>[a-z0-9._~+/=-]+)(?<continuation>(?:[ \t]+[a-z0-9]+){0,3})",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
  private static partial Regex StandaloneBearerCandidatePattern();

  private static string RedactStandaloneBearer(Match match)
  {
    var rawToken = match.Groups["token"].Value;
    var token = rawToken.TrimEnd('.');
    if (IsKnownBearerDiagnosticPhrase(match, token))
    {
      return match.Value;
    }

    var trailingPeriods = rawToken[token.Length..];
    return $"{match.Groups["prefix"].Value}***{trailingPeriods}"
        + match.Groups["continuation"].Value;
  }

  private static bool IsKnownBearerDiagnosticPhrase(Match match, string token)
  {
    var phrase = token + match.Groups["continuation"].Value;
    return phrase.Equals("authentication failed", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("token unavailable", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("authorization required", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("OAuth2 authentication enabled", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("RFC6750 support enabled", StringComparison.OrdinalIgnoreCase)
        || phrase.Equals("service ready", StringComparison.OrdinalIgnoreCase);
  }

  private static string RedactSecretBlocks(string value)
  {
    const string beginPrefix = "-----BEGIN ";
    const string markerSuffix = "-----";
    var searchIndex = 0;
    var copyIndex = 0;
    StringBuilder? result = null;
    while (searchIndex < value.Length)
    {
      var beginIndex = value.IndexOf(beginPrefix, searchIndex, StringComparison.OrdinalIgnoreCase);
      if (beginIndex < 0)
      {
        break;
      }

      var typeStart = beginIndex + beginPrefix.Length;
      var typeEnd = value.IndexOf(markerSuffix, typeStart, StringComparison.Ordinal);
      if (typeEnd < 0)
      {
        break;
      }

      var type = value[typeStart..typeEnd];
      if (!IsSecretBlockType(type))
      {
        searchIndex = typeEnd + markerSuffix.Length;
        continue;
      }

      result ??= new StringBuilder(value.Length);
      result.Append(value, copyIndex, beginIndex - copyIndex);
      result.Append("-----BEGIN ").Append(type).Append("-----\n***");

      var endMarker = $"-----END {type}-----";
      var endIndex = value.IndexOf(
          endMarker,
          typeEnd + markerSuffix.Length,
          StringComparison.OrdinalIgnoreCase);
      if (endIndex < 0)
      {
        return result.ToString();
      }

      result.Append('\n').Append(endMarker);
      copyIndex = endIndex + endMarker.Length;
      searchIndex = copyIndex;
    }

    if (result is null)
    {
      return value;
    }

    result.Append(value, copyIndex, value.Length - copyIndex);
    return result.ToString();
  }

  private static bool IsSecretBlockType(string type) =>
      (type.Equals("CERTIFICATE", StringComparison.OrdinalIgnoreCase)
          || type.EndsWith("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
      && type.All(character => char.IsAsciiLetterOrDigit(character) || character == ' ');
}
