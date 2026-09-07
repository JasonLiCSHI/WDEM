using System.Text.RegularExpressions;

namespace Wdem.Domain.Versions;

public sealed class SoftwareVersion : IComparable<SoftwareVersion>, IEquatable<SoftwareVersion>
{
  private static readonly Regex VersionPattern = new(
      @"\d+(?:\.\d+)*",
      RegexOptions.CultureInvariant);

  private readonly int[] _parts;

  private SoftwareVersion(int[] parts)
  {
    _parts = parts;
  }

  public static SoftwareVersion Parse(string value) =>
      TryParse(value, out var version)
          ? version
          : throw new FormatException($"Invalid software version '{value}'.");

  public static bool TryParse(string? value, out SoftwareVersion version)
  {
    var match = VersionPattern.Match(value ?? string.Empty);
    if (!match.Success)
    {
      version = null!;
      return false;
    }

    var parts = match.Value.Split('.');
    var parsedParts = new int[parts.Length];
    for (var index = 0; index < parts.Length; index++)
    {
      if (!int.TryParse(parts[index], out parsedParts[index]))
      {
        version = null!;
        return false;
      }
    }

    version = new SoftwareVersion(parsedParts);
    return true;
  }

  public int CompareTo(SoftwareVersion? other)
  {
    if (other is null)
    {
      return 1;
    }

    for (var index = 0; index < Math.Max(_parts.Length, other._parts.Length); index++)
    {
      var comparison = PartAt(index).CompareTo(other.PartAt(index));
      if (comparison != 0)
      {
        return comparison;
      }
    }

    return 0;
  }

  public bool Equals(SoftwareVersion? other) => CompareTo(other) == 0;

  public override bool Equals(object? obj) => obj is SoftwareVersion other && Equals(other);

  public override int GetHashCode()
  {
    var lastMeaningfulPart = _parts.Length - 1;
    while (lastMeaningfulPart > 0 && _parts[lastMeaningfulPart] == 0)
    {
      lastMeaningfulPart--;
    }

    var hash = new HashCode();
    for (var index = 0; index <= lastMeaningfulPart; index++)
    {
      hash.Add(_parts[index]);
    }
    return hash.ToHashCode();
  }

  public override string ToString() => string.Join('.', _parts);

  internal int PartAt(int index) => index < _parts.Length ? _parts[index] : 0;
}
