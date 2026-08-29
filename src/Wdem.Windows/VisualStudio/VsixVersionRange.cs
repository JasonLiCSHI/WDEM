namespace Wdem.Windows.VisualStudio;

internal readonly record struct VsixVersionRange(
    Version? Minimum,
    bool IncludesMinimum,
    Version? Maximum,
    bool IncludesMaximum,
    Version? Exact)
{
  public static bool TryParse(string? expression, out VsixVersionRange range)
  {
    if (expression is null)
    {
      range = new VsixVersionRange(null, false, null, false, null);
      return true;
    }

    var text = expression.Trim();
    if (Version.TryParse(text, out var exact))
    {
      range = new VsixVersionRange(null, false, null, false, exact);
      return true;
    }

    if (text.Length < 3 || text[0] is not ('[' or '(') || text[^1] is not (']' or ')'))
    {
      range = default;
      return false;
    }

    var bounds = text[1..^1].Split(',', StringSplitOptions.TrimEntries);
    if (bounds.Length == 1)
    {
      if (text[0] == '[' && text[^1] == ']' && Version.TryParse(bounds[0], out exact))
      {
        range = new VsixVersionRange(null, false, null, false, exact);
        return true;
      }

      range = default;
      return false;
    }

    if (bounds.Length != 2 || bounds.All(string.IsNullOrEmpty))
    {
      range = default;
      return false;
    }

    Version? minimum = null;
    Version? maximum = null;
    if (bounds[0].Length > 0 && !Version.TryParse(bounds[0], out minimum) ||
        bounds[1].Length > 0 && !Version.TryParse(bounds[1], out maximum))
    {
      range = default;
      return false;
    }

    if (minimum is not null && maximum is not null)
    {
      var comparison = minimum.CompareTo(maximum);
      if (comparison > 0 || comparison == 0 && (text[0] != '[' || text[^1] != ']'))
      {
        range = default;
        return false;
      }
    }

    range = new VsixVersionRange(
        minimum,
        text[0] == '[',
        maximum,
        text[^1] == ']',
        null);
    return true;
  }

  public bool Contains(string versionText)
  {
    if (!Version.TryParse(versionText, out var version))
    {
      return false;
    }

    if (Exact is not null)
    {
      return version == Exact;
    }

    var minimumMatches = Minimum is null || version > Minimum ||
        IncludesMinimum && version == Minimum;
    var maximumMatches = Maximum is null || version < Maximum ||
        IncludesMaximum && version == Maximum;
    return minimumMatches && maximumMatches;
  }
}
