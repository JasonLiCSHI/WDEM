using System.Text.RegularExpressions;
using Wdem.Core.Versions;

namespace Wdem.Windows.Providers;

internal static partial class CommandVersionParser
{
  public static bool TryParseGit(
      string? output,
      out string detectedVersion,
      out SemanticVersion? comparableVersion)
  {
    detectedVersion = string.Empty;
    comparableVersion = null;
    if (string.IsNullOrWhiteSpace(output))
    {
      return false;
    }

    var match = GitVersionPattern().Match(output.Trim());
    if (!match.Success)
    {
      return false;
    }

    var stableVersion = match.Groups["stable"].Value;
    detectedVersion = match.Groups["unsupported"].Success
        ? stableVersion + match.Groups["unsupported"].Value
        : stableVersion;
    if (!match.Groups["unsupported"].Success &&
        SemanticVersion.TryParse(stableVersion, out var parsed))
    {
      comparableVersion = parsed;
    }

    return true;
  }

  public static bool TryParseDotNetSdks(
      IReadOnlyList<string> output,
      out IReadOnlyList<string> detectedVersions,
      out IReadOnlyList<SemanticVersion> comparableVersions)
  {
    var detected = new List<string>();
    var parsed = new List<SemanticVersion>();
    foreach (var line in output.Where(line => !string.IsNullOrWhiteSpace(line)))
    {
      var firstColumn = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
          .FirstOrDefault();
      if (firstColumn is null || !VersionTokenPattern().IsMatch(firstColumn))
      {
        detectedVersions = [];
        comparableVersions = [];
        return false;
      }

      detected.Add(firstColumn);
      if (SemanticVersion.TryParse(firstColumn, out var version))
      {
        parsed.Add(version);
      }
    }

    detectedVersions = detected;
    comparableVersions = parsed;
    return detected.Count > 0;
  }

  public static bool TryParseWinGetList(
      IReadOnlyList<string> output,
      string packageId,
      out string detectedVersion,
      out SemanticVersion? comparableVersion)
  {
    detectedVersion = string.Empty;
    comparableVersion = null;
    foreach (var line in output)
    {
      var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
      var packageIndex = Array.FindIndex(
          columns,
          column => string.Equals(column, packageId, StringComparison.OrdinalIgnoreCase));
      if (packageIndex >= 0 && packageIndex + 1 < columns.Length &&
          VersionTokenPattern().IsMatch(columns[packageIndex + 1]))
      {
        detectedVersion = columns[packageIndex + 1];
        if (SemanticVersion.TryParse(detectedVersion, out var parsed))
        {
          comparableVersion = parsed;
        }

        return true;
      }
    }

    return false;
  }

  [GeneratedRegex(
      @"\Agit version (?<stable>\d+\.\d+\.\d+)(?:\.windows\.\d+|(?<unsupported>[-+][^\s]+))?\z",
      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
  private static partial Regex GitVersionPattern();

  [GeneratedRegex(
      @"\A\d+(?:\.\d+){0,3}(?:[-+][0-9A-Za-z.-]+)?\z",
      RegexOptions.CultureInvariant)]
  private static partial Regex VersionTokenPattern();
}
