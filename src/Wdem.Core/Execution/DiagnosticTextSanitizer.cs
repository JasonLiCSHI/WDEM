using System.Text.RegularExpressions;

namespace Wdem.Core.Execution;

internal static partial class DiagnosticTextSanitizer
{
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
    sanitized = SensitiveTextRedactor.RedactAssignments(sanitized, "[REDACTED]");
    sanitized = LocalWindowsUserPathPattern().Replace(sanitized, "${prefix}[REDACTED]");
    sanitized = UncUserPathPattern().Replace(sanitized, "${prefix}[REDACTED]");
    return UnixUserPathPattern().Replace(sanitized, "${prefix}[REDACTED]");
  }
}
