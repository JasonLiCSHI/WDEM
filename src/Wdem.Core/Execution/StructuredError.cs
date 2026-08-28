using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wdem.Core.Execution;

public sealed record StructuredError(
    WdemErrorCode Code,
    string Summary,
    string Detail)
{
  private Exception? _underlyingException;

  public string? ResourceId { get; init; }
  public string? StepId { get; init; }
  public int? ProcessExitCode { get; init; }
  public string? LogLocation { get; init; }
  public string? SuggestedAction { get; init; }
  public bool IsRetryable { get; init; }

  [JsonIgnore]
  public Exception? UnderlyingException
  {
    get => _underlyingException;
    init
    {
      _underlyingException = value;
      if (value is not null)
      {
        UnderlyingExceptionType = value.GetType().FullName ?? value.GetType().Name;
        UnderlyingExceptionMessage = SensitiveDataSanitizer.Sanitize(value.Message);
      }
    }
  }

  [JsonInclude]
  public string? UnderlyingExceptionType { get; private init; }

  [JsonInclude]
  public string? UnderlyingExceptionMessage { get; private init; }
}

internal static partial class SensitiveDataSanitizer
{
  [GeneratedRegex(
      """(?i)\b(password|passwd|pwd|token|access[_-]?token|api[_-]?key|secret|authorization)\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s;,]+)""",
      RegexOptions.CultureInvariant)]
  private static partial Regex KeyValueSecretPattern();

  [GeneratedRegex(
      @"(?i)\bbearer\s+[^\s,;]+",
      RegexOptions.CultureInvariant)]
  private static partial Regex BearerTokenPattern();

  public static string Sanitize(string message)
  {
    var sanitized = BearerTokenPattern().Replace(message, "Bearer [REDACTED]");
    return KeyValueSecretPattern().Replace(sanitized, "$1=[REDACTED]");
  }
}
