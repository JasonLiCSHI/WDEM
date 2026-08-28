using System.Text.RegularExpressions;
using Wdem.Core.Versions;

namespace Wdem.Windows.Providers;

internal static partial class CommandVersionParser
{
  public static bool TryParseGit(string? output, out SemanticVersion version)
  {
    version = default;
    if (string.IsNullOrWhiteSpace(output))
    {
      return false;
    }

    var match = GitVersionPattern().Match(output.Trim());
    return match.Success && SemanticVersion.TryParse(match.Groups["version"].Value, out version);
  }

  public static bool TryParseDotNetSdks(
      IReadOnlyList<string> output,
      out IReadOnlyList<SemanticVersion> versions)
  {
    var parsed = new List<SemanticVersion>();
    foreach (var line in output.Where(line => !string.IsNullOrWhiteSpace(line)))
    {
      var firstColumn = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
          .FirstOrDefault();
      if (!SemanticVersion.TryParse(firstColumn, out var version))
      {
        versions = [];
        return false;
      }

      parsed.Add(version);
    }

    versions = parsed;
    return parsed.Count > 0;
  }

  public static bool TryParseWinGetList(
      IReadOnlyList<string> output,
      string packageId,
      out SemanticVersion version)
  {
    version = default;
    foreach (var line in output)
    {
      var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      var packageIndex = Array.FindIndex(
          columns,
          column => string.Equals(column, packageId, StringComparison.OrdinalIgnoreCase));
      if (packageIndex >= 0 && packageIndex + 1 < columns.Length &&
          TryParseNumericPrefix(columns[packageIndex + 1], out version))
      {
        return true;
      }
    }

    return false;
  }

  private static bool TryParseNumericPrefix(string text, out SemanticVersion version)
  {
    version = default;
    var match = NumericVersionPrefixPattern().Match(text);
    return match.Success && SemanticVersion.TryParse(match.Groups["version"].Value, out version);
  }

  [GeneratedRegex(
      @"\Agit version (?<version>\d+\.\d+\.\d+)(?:\.[^\s]+)?\z",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
  private static partial Regex GitVersionPattern();

  [GeneratedRegex(
      @"\A(?<version>\d+(?:\.\d+){0,3})(?:[^\d.].*)?\z",
      RegexOptions.CultureInvariant)]
  private static partial Regex NumericVersionPrefixPattern();
}
