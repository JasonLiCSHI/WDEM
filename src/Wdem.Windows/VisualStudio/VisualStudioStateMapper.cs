using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Versions;

namespace Wdem.Windows.VisualStudio;

internal static class VisualStudioStateMapper
{
  public static DetectedState Create(
      string resourceId,
      VisualStudioInstance instance,
      string? configurationPath = null,
      string? configurationSha256 = null)
  {
    var installedVersions = SemanticVersion.TryParse(
        instance.ProductDisplayVersion,
        out var displayVersion)
        ? new[] { displayVersion }
        : SemanticVersion.TryParse(instance.InstallationVersion, out var installationVersion)
            ? [installationVersion]
            : [];
    if (installedVersions.Length == 0)
    {
      var error = new StructuredError(
          WdemErrorCode.DetectionError,
          "Visual Studio version could not be determined.",
          "The selected Visual Studio instance did not report a valid product or installation version.")
      {
        ResourceId = resourceId
      };
      return new DetectedState
      {
        ResourceId = resourceId,
        Outcome = DetectionOutcome.Failed,
        Exists = true,
        Error = error.Detail,
        StructuredError = error
      };
    }

    var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["instanceId"] = instance.InstanceId,
      ["installationPath"] = instance.InstallationPath,
      ["productId"] = instance.ProductId,
      ["productPath"] = instance.ProductPath,
      ["productDisplayVersion"] = instance.ProductDisplayVersion,
      ["installationVersion"] = instance.InstallationVersion,
      ["edition"] = instance.Edition,
      ["channel"] = instance.ChannelId,
      ["isComplete"] = instance.IsComplete.ToString().ToLowerInvariant(),
      ["isLaunchable"] = instance.IsLaunchable.ToString().ToLowerInvariant(),
      ["workloads"] = JoinIds(instance.Workloads),
      ["components"] = JoinIds(instance.Components)
    };
    if (configurationPath is not null && configurationSha256 is not null)
    {
      var fullPath = Path.GetFullPath(configurationPath);
      evidence["vsconfigPath"] = fullPath;
      evidence["vsconfigSource"] = fullPath;
      evidence["vsconfigSha256"] = configurationSha256;
    }

    return new DetectedState
    {
      ResourceId = resourceId,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = instance.ProductDisplayVersion,
      InstalledVersions = installedVersions,
      ConfigurationHash = configurationSha256,
      Evidence = evidence
    };
  }

  private static string JoinIds(IEnumerable<string> ids) => string.Join(
      ';',
      ids.Order(StringComparer.OrdinalIgnoreCase));
}
