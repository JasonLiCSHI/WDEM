using System.Globalization;
using System.Text.RegularExpressions;

namespace Wdem.Core.Versions;

public sealed partial class VersionConstraint
{
  private readonly SemanticVersion? _exactVersion;
  private readonly SemanticVersion? _minimumVersion;
  private readonly SemanticVersion? _exclusiveMaximumVersion;
  private readonly int? _wildcardMajor;
  private readonly int? _wildcardMinor;

  private VersionConstraint(
      SemanticVersion? exactVersion = null,
      SemanticVersion? minimumVersion = null,
      SemanticVersion? exclusiveMaximumVersion = null,
      int? wildcardMajor = null,
      int? wildcardMinor = null)
  {
    _exactVersion = exactVersion;
    _minimumVersion = minimumVersion;
    _exclusiveMaximumVersion = exclusiveMaximumVersion;
    _wildcardMajor = wildcardMajor;
    _wildcardMinor = wildcardMinor;
  }

  public static VersionConstraint Parse(string expression)
  {
    if (string.IsNullOrWhiteSpace(expression))
    {
      throw new FormatException("A version constraint cannot be empty.");
    }

    var wildcardMatch = WildcardPattern().Match(expression);
    if (wildcardMatch.Success)
    {
      return new VersionConstraint(
          wildcardMajor: ParseSegment(wildcardMatch.Groups["major"].Value),
          wildcardMinor: ParseSegment(wildcardMatch.Groups["minor"].Value));
    }

    var exactMatch = ExactPattern().Match(expression);
    if (exactMatch.Success)
    {
      return new VersionConstraint(exactVersion: ParseVersion(exactMatch.Groups["version"].Value));
    }

    var rangeMatch = RangePattern().Match(expression);
    if (rangeMatch.Success)
    {
      var minimum = ParseVersion(rangeMatch.Groups["minimum"].Value);
      var maximumGroup = rangeMatch.Groups["maximum"];
      return new VersionConstraint(
          minimumVersion: minimum,
          exclusiveMaximumVersion: maximumGroup.Success
              ? ParseVersion(maximumGroup.Value)
              : null);
    }

    throw new FormatException($"Unsupported version constraint '{expression}'.");
  }

  public bool IsSatisfiedBy(SemanticVersion installedVersion)
  {
    if (_exactVersion is { } exactVersion)
    {
      return installedVersion.CompareTo(exactVersion) == 0;
    }

    if (_wildcardMajor is { } wildcardMajor && _wildcardMinor is { } wildcardMinor)
    {
      return installedVersion.Major == wildcardMajor && installedVersion.Minor == wildcardMinor;
    }

    if (_minimumVersion is { } minimumVersion && installedVersion.CompareTo(minimumVersion) < 0)
    {
      return false;
    }

    return _exclusiveMaximumVersion is not { } maximumVersion ||
        installedVersion.CompareTo(maximumVersion) < 0;
  }

  private static SemanticVersion ParseVersion(string value)
  {
    if (!SemanticVersion.TryParse(value, out var version))
    {
      throw new FormatException($"Invalid semantic version '{value}'.");
    }

    return version;
  }

  private static int ParseSegment(string value)
  {
    if (!int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var segment))
    {
      throw new FormatException($"Invalid semantic version segment '{value}'.");
    }

    return segment;
  }

  [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)\.[xX]$", RegexOptions.CultureInvariant)]
  private static partial Regex WildcardPattern();

  [GeneratedRegex(@"^=\s*(?<version>\d+(?:\.\d+){0,3})$", RegexOptions.CultureInvariant)]
  private static partial Regex ExactPattern();

  [GeneratedRegex(
      @"^>=\s*(?<minimum>\d+(?:\.\d+){0,3})(?:\s+<\s*(?<maximum>\d+(?:\.\d+){0,3}))?$",
      RegexOptions.CultureInvariant)]
  private static partial Regex RangePattern();
}
