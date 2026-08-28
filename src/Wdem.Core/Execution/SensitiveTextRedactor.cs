using System.Text.RegularExpressions;

namespace Wdem.Core.Execution;

internal static partial class SensitiveTextRedactor
{
  [GeneratedRegex(
      """(?<prefix>(?:^|[^a-z0-9_-])["']?(?<key>(?:password|passwd|pwd|token|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|secret|thumb[_-]?print|authorization|[a-z][a-z0-9_-]*?(?:token|password|secret)))["']?[ \t]*[:=][ \t]*)(?:"(?:\\.|[^"\\\r\n])*"|'(?:\\.|[^'\\\r\n])*'|[^\s;,"']+)""",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
  private static partial Regex AssignmentPattern();

  public static string RedactAssignments(string value, string marker)
  {
    ArgumentNullException.ThrowIfNull(value);
    ArgumentException.ThrowIfNullOrEmpty(marker);
    return AssignmentPattern().Replace(value, match =>
    {
      if (!IsSensitiveKey(match.Groups["key"].Value))
      {
        return match.Value;
      }

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
    var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .ToLowerInvariant();
    return normalized is "password" or "passwd" or "pwd" or "token" or
        "accesstoken" or "refreshtoken" or "clientsecret" or "apikey" or
        "secret" or "thumbprint" or "authorization" ||
        normalized.EndsWith("token", StringComparison.Ordinal) ||
        normalized.EndsWith("password", StringComparison.Ordinal) ||
        normalized.EndsWith("secret", StringComparison.Ordinal);
  }
}
