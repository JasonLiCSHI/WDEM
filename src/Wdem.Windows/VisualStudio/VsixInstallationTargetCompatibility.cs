namespace Wdem.Windows.VisualStudio;

internal static class VsixInstallationTargetCompatibility
{
  public static bool IsCompatible(
      IReadOnlyList<VsixInstallationTarget> targets,
      VisualStudioInstance instance)
  {
    if (targets.Count == 0)
    {
      return true;
    }

    var productId = instance.ProductId switch
    {
      var value when Matches(value, "Microsoft.VisualStudio.Product.Community") =>
          "Microsoft.VisualStudio.Community",
      var value when Matches(value, "Microsoft.VisualStudio.Product.Professional") =>
          "Microsoft.VisualStudio.Pro",
      var value when Matches(value, "Microsoft.VisualStudio.Product.Enterprise") =>
          "Microsoft.VisualStudio.Enterprise",
      _ => instance.ProductId.Replace(".Product.", ".", StringComparison.OrdinalIgnoreCase)
    };
    return targets.Any(target =>
        (Matches(target.Id, instance.ProductId) || Matches(target.Id, productId)) &&
        VersionRangeContains(target.VersionRange, instance.InstallationVersion));
  }

  private static bool VersionRangeContains(string? expression, string versionText)
  {
    if (string.IsNullOrWhiteSpace(expression))
    {
      return true;
    }

    if (!Version.TryParse(versionText, out var version))
    {
      return false;
    }

    var range = expression.Trim();
    if (range.Length < 3 || range[0] is not ('[' or '(') ||
        range[^1] is not (']' or ')'))
    {
      return Version.TryParse(range, out var exact) && version == exact;
    }

    var bounds = range[1..^1].Split(',', StringSplitOptions.TrimEntries);
    if (bounds.Length == 1)
    {
      return range[0] == '[' && range[^1] == ']' &&
          Version.TryParse(bounds[0], out var exact) && version == exact;
    }

    if (bounds.Length != 2)
    {
      return false;
    }

    var minimumMatches = string.IsNullOrEmpty(bounds[0]) ||
        (Version.TryParse(bounds[0], out var minimum) &&
         (version > minimum || (range[0] == '[' && version == minimum)));
    var maximumMatches = string.IsNullOrEmpty(bounds[1]) ||
        (Version.TryParse(bounds[1], out var maximum) &&
         (version < maximum || (range[^1] == ']' && version == maximum)));
    return minimumMatches && maximumMatches;
  }

  private static bool Matches(string? left, string? right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
