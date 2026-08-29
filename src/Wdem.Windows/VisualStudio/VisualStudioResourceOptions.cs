using Wdem.Core.Resources;

namespace Wdem.Windows.VisualStudio;

public sealed record VisualStudioResourceOptions
{
  private static readonly HashSet<string> SupportedParameters = new(
      [
        "productId",
        "instanceId",
        "edition",
        "channelId",
        "installPath",
        "workloads",
        "components",
        "vsconfigPath",
        "bootstrapperUri",
        "bootstrapperSha256"
      ],
      StringComparer.OrdinalIgnoreCase);

  public required string ProductId { get; init; }
  public string? InstanceId { get; init; }
  public required string Edition { get; init; }
  public required string ChannelId { get; init; }
  public string? InstallPath { get; init; }
  public IReadOnlyList<string> Workloads { get; init; } = [];
  public IReadOnlyList<string> Components { get; init; } = [];
  public string? VsConfigPath { get; init; }
  public Uri? BootstrapperUri { get; init; }
  public string? BootstrapperSha256 { get; init; }

  public static bool TryParse(
      ResourceDefinition resource,
      out VisualStudioResourceOptions? options,
      out IReadOnlyList<string> errors)
  {
    ArgumentNullException.ThrowIfNull(resource);
    var parseErrors = new List<string>();
    foreach (var parameter in resource.Parameters.Keys.Where(
                 parameter => !SupportedParameters.Contains(parameter)))
    {
      parseErrors.Add($"Parameter '{parameter}' is not supported.");
    }

    var productId = Required(resource, "productId", parseErrors);
    var edition = Required(resource, "edition", parseErrors);
    var channelId = Required(resource, "channelId", parseErrors);
    var instanceId = Optional(resource, "instanceId", parseErrors);
    var installPath = Optional(resource, "installPath", parseErrors);
    var vsConfigPath = Optional(resource, "vsconfigPath", parseErrors);
    if (installPath is not null && !Path.IsPathFullyQualified(installPath))
    {
      parseErrors.Add("Parameter 'installPath' must be an absolute path.");
    }

    if (vsConfigPath is not null && !Path.IsPathFullyQualified(vsConfigPath))
    {
      parseErrors.Add("Parameter 'vsconfigPath' must be an absolute path.");
    }

    var workloads = ParseList(resource, "workloads", parseErrors);
    var components = ParseList(resource, "components", parseErrors);
    var bootstrapper = Get(resource, "bootstrapperUri");
    Uri? bootstrapperUri = null;
    if (resource.Parameters.ContainsKey("bootstrapperUri") && bootstrapper is null)
    {
      parseErrors.Add("Parameter 'bootstrapperUri' cannot be empty.");
    }
    else if (bootstrapper is not null &&
        (!Uri.TryCreate(bootstrapper, UriKind.Absolute, out bootstrapperUri) ||
         !string.Equals(bootstrapperUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
    {
      parseErrors.Add("Parameter 'bootstrapperUri' must be an absolute HTTPS URI.");
      bootstrapperUri = null;
    }

    var bootstrapperSha256 = Optional(resource, "bootstrapperSha256", parseErrors);
    if (bootstrapperSha256 is not null &&
        (bootstrapperSha256.Length != 64 || !bootstrapperSha256.All(Uri.IsHexDigit)))
    {
      parseErrors.Add("Parameter 'bootstrapperSha256' must contain exactly 64 hexadecimal characters.");
    }

    options = new VisualStudioResourceOptions
    {
      ProductId = productId ?? string.Empty,
      InstanceId = instanceId,
      Edition = edition ?? string.Empty,
      ChannelId = channelId ?? string.Empty,
      InstallPath = installPath,
      Workloads = workloads,
      Components = components,
      VsConfigPath = vsConfigPath,
      BootstrapperUri = bootstrapperUri,
      BootstrapperSha256 = bootstrapperSha256
    };
    errors = parseErrors;
    if (parseErrors.Count == 0)
    {
      return true;
    }

    options = null;
    return false;
  }

  private static string? Get(ResourceDefinition resource, string parameter) =>
      resource.Parameters.TryGetValue(parameter, out var value) &&
      !string.IsNullOrWhiteSpace(value)
          ? value.Trim()
          : null;

  private static string? Required(
      ResourceDefinition resource,
      string parameter,
      List<string> errors)
  {
    var value = Get(resource, parameter);
    if (value is null)
    {
      errors.Add($"Parameter '{parameter}' is required and cannot be empty.");
    }

    return value;
  }

  private static string? Optional(
      ResourceDefinition resource,
      string parameter,
      List<string> errors)
  {
    var value = Get(resource, parameter);
    if (resource.Parameters.ContainsKey(parameter) && value is null)
    {
      errors.Add($"Parameter '{parameter}' cannot be empty.");
    }

    return value;
  }

  private static IReadOnlyList<string> ParseList(
      ResourceDefinition resource,
      string parameter,
      List<string> errors)
  {
    if (!resource.Parameters.TryGetValue(parameter, out var value))
    {
      return [];
    }

    if (string.IsNullOrWhiteSpace(value))
    {
      errors.Add($"Parameter '{parameter}' cannot be empty.");
      return [];
    }

    var segments = value.Split([',', ';'], StringSplitOptions.TrimEntries);
    if (segments.Any(string.IsNullOrWhiteSpace))
    {
      errors.Add($"Parameter '{parameter}' must be a comma- or semicolon-separated list of IDs.");
      return [];
    }

    return segments.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  }
}
