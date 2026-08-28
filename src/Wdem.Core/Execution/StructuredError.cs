using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wdem.Core.Execution;

public sealed record StructuredError
{
  private Exception? _underlyingException;
  private string _summary;
  private string _detail;
  private string? _underlyingExceptionType;
  private string? _underlyingExceptionMessage;

  [JsonConstructor]
  public StructuredError(WdemErrorCode code, string summary, string detail)
  {
    Code = code;
    _summary = summary ?? throw new ArgumentNullException(nameof(summary));
    _detail = detail ?? throw new ArgumentNullException(nameof(detail));
  }

  public WdemErrorCode Code { get; init; }

  public string Summary
  {
    get => _summary;
    init => _summary = value ?? throw new ArgumentNullException(nameof(value));
  }

  public string Detail
  {
    get => _detail;
    init => _detail = value ?? throw new ArgumentNullException(nameof(value));
  }

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
      UnderlyingExceptionType = value is null
          ? null
          : value.GetType().FullName ?? value.GetType().Name;
      UnderlyingExceptionMessage = value?.Message;
    }
  }

  [JsonInclude]
  public string? UnderlyingExceptionType
  {
    get => _underlyingExceptionType;
    private init => _underlyingExceptionType = value;
  }

  [JsonInclude]
  public string? UnderlyingExceptionMessage
  {
    get => _underlyingExceptionMessage;
    private init => _underlyingExceptionMessage = value is null
        ? null
        : SensitiveDataSanitizer.Sanitize(value);
  }
}

internal static partial class SensitiveDataSanitizer
{
  [GeneratedRegex(
      """\b(?<key>(?:password|passwd|pwd|token|access[_-]?token|api[_-]?key|secret|authorization|[a-z][a-z0-9_-]*[_-](?:token|password|secret)))[ \t]*[:=][ \t]*(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s;,]+)""",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex KeyValueSecretPattern();

  [GeneratedRegex(
      @"\bbearer[ \t]+[^\s,;]+",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex BearerTokenPattern();

  [GeneratedRegex(
      @"\bauthorization[ \t]*[:=][ \t]*(?:basic|bearer|digest|negotiate|ntlm)[ \t]+[^\s,;]+",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex AuthorizationCredentialPattern();

  [GeneratedRegex(
      """(?<prefix>\b[a-z]:\\Users\\)[^\\/:*?"<>|\r\n]+""",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex LocalWindowsUserPathPattern();

  [GeneratedRegex(
      @"(?<prefix>\\\\[^\\\r\n]+\\(?:Users|home)\\)[^\\\r\n]+",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex UncUserPathPattern();

  [GeneratedRegex(
      @"(?<prefix>/(?:home|Users)/)[^/\s]+",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex UnixUserPathPattern();

  public static string Sanitize(string message)
  {
    var sanitized = AuthorizationCredentialPattern().Replace(
        message,
        "Authorization=[REDACTED]");
    sanitized = BearerTokenPattern().Replace(sanitized, "Bearer [REDACTED]");
    sanitized = KeyValueSecretPattern().Replace(sanitized, "${key}=[REDACTED]");
    sanitized = LocalWindowsUserPathPattern().Replace(sanitized, "${prefix}[REDACTED]");
    sanitized = UncUserPathPattern().Replace(sanitized, "${prefix}[REDACTED]");
    return UnixUserPathPattern().Replace(sanitized, "${prefix}[REDACTED]");
  }
}
