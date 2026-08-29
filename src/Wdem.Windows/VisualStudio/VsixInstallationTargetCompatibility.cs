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

    return VsixVersionRange.TryParse(expression, out var range) && range.Contains(versionText);
  }

  private static bool Matches(string? left, string? right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
