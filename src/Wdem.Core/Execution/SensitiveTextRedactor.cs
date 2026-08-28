using System.Text.RegularExpressions;

namespace Wdem.Core.Execution;

internal static partial class SensitiveTextRedactor
{
  [GeneratedRegex(
      """(?<prefix>(?:^|[^a-z0-9_-])["']?(?<key>(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|secret|thumbprint|authorization|[a-z][a-z0-9_-]*[_-](?:token|password|secret)))["']?[ \t]*[:=][ \t]*)(?:"(?:\\.|[^"\\\r\n])*"|'(?:\\.|[^'\\\r\n])*'|[^\s;,"']+)""",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex AssignmentPattern();

  [GeneratedRegex(
      """^(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|secret|thumbprint|authorization|[a-z][a-z0-9_-]*[_-](?:token|password|secret))$""",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex SensitiveKeyPattern();

  public static string RedactAssignments(string value, string marker)
  {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentException.ThrowIfNullOrEmpty(marker);
    return AssignmentPattern().Replace(value, match =>
    {
      var prefix = match.Groups["prefix"].Value;
      var assignedValue = match.Value.AsSpan(prefix.Length);
      if (match.Groups["key"].Value.Equals("authorization", StringComparison.OrdinalIgnoreCase)
          && assignedValue.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
      {
        return match.Value;
      }

      if (assignedValue.Length >= 2
          && assignedValue[0] is '"' or '\''
          && assignedValue[^1] == assignedValue[0])
      {
        return $"{prefix}{assignedValue[0]}{marker}{assignedValue[0]}";
      }

      return $"{prefix}{marker}";
    });
  }

  public static bool IsSensitiveKey(string key)
  {
    ArgumentNullException.ThrowIfNull(key);
    return SensitiveKeyPattern().IsMatch(key);
  }
}
