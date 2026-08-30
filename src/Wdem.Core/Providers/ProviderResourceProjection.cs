using System.Collections.Frozen;
using Wdem.Core.Resources;

namespace Wdem.Core.Providers;

public static class ProviderResourceProjection
{
  public static ResourceDefinition ForCompliance(
      ResourceDefinition resource,
      ProviderCapabilities capabilities)
  {
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(capabilities);

    if (capabilities.AcquisitionOnlyParameters.Count == 0 ||
        !resource.Parameters.Keys.Any(capabilities.AcquisitionOnlyParameters.Contains))
    {
      return resource;
    }

    return resource with
    {
      Parameters = resource.Parameters
          .Where(pair => !capabilities.AcquisitionOnlyParameters.Contains(pair.Key))
          .ToFrozenDictionary(
              pair => pair.Key,
              pair => pair.Value,
              StringComparer.OrdinalIgnoreCase)
    };
  }
}
