using System.Security.Cryptography;
using System.Text;

namespace Wdem.Core.Resources;

public static class ResourceDefinitionFingerprint
{
  public static string Create(ResourceDefinition resource)
  {
    ArgumentNullException.ThrowIfNull(resource);

    var canonical = new StringBuilder();
    Append(canonical, resource.Id);
    Append(canonical, resource.Type);
    Append(canonical, resource.Provider);
    Append(canonical, resource.DisplayName);
    Append(canonical, resource.VersionConstraint);
    Append(canonical, resource.PreferredVersion);
    Append(canonical, resource.PrivilegeRequirement.ToString());
    Append(canonical, resource.RestartPolicy.ToString());

    foreach (var dependency in resource.Dependencies.Order(StringComparer.OrdinalIgnoreCase))
    {
      Append(canonical, dependency);
    }

    foreach (var parameter in resource.Parameters.OrderBy(
                 pair => pair.Key,
                 StringComparer.OrdinalIgnoreCase))
    {
      Append(canonical, parameter.Key);
      Append(canonical, parameter.Value);
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
  }

  private static void Append(StringBuilder builder, string? value)
  {
    if (value is null)
    {
      builder.Append("-1:");
      return;
    }

    builder.Append(value.Length).Append(':').Append(value);
  }
}
