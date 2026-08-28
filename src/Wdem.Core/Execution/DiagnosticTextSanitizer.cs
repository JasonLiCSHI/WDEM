using System.Text.RegularExpressions;

namespace Wdem.Core.Execution;

internal static partial class DiagnosticTextSanitizer
{
  [GeneratedRegex(
      """\b(?<key>(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|secret|authorization|[a-z][a-z0-9_-]*[_-](?:token|password|secret)))[ \t]*[:=][ \t]*(?:"[^"\r\n]*"|'[^'\r\n]*'|[^\s;,]+)""",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex KeyValueSecretPattern();

  [GeneratedRegex(
      @"\bbearer[ \t]+[^\s,;]+",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex BearerTokenPattern();

  [GeneratedRegex(
      @"\bauthorization[ \t]*[:=][^\r\n]*",
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
    ArgumentNullException.ThrowIfNull(message);

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
