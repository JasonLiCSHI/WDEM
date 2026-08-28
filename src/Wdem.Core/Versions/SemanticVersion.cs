using System.Globalization;

namespace Wdem.Core.Versions;

public readonly record struct SemanticVersion(
    int Major,
    int Minor,
    int Patch,
    int Revision = 0) : IComparable<SemanticVersion>
{
  public static bool TryParse(string? text, out SemanticVersion version)
  {
    version = default;
    if (string.IsNullOrEmpty(text))
    {
      return false;
    }

    var segments = text.Split('.', StringSplitOptions.None);
    if (segments.Length is < 1 or > 4)
    {
      return false;
    }

    Span<int> values = stackalloc int[4];
    for (var index = 0; index < segments.Length; index++)
    {
      if (!int.TryParse(
              segments[index],
              NumberStyles.None,
              CultureInfo.InvariantCulture,
              out values[index]))
      {
        return false;
      }
    }

    version = new SemanticVersion(values[0], values[1], values[2], values[3]);
    return true;
  }

  public int CompareTo(SemanticVersion other)
  {
    var comparison = Major.CompareTo(other.Major);
    if (comparison != 0)
    {
      return comparison;
    }

    comparison = Minor.CompareTo(other.Minor);
    if (comparison != 0)
    {
      return comparison;
    }

    comparison = Patch.CompareTo(other.Patch);
    return comparison != 0 ? comparison : Revision.CompareTo(other.Revision);
  }
}
