using System.Text.Json;
using Wdem.Core.Resources;

namespace Wdem.Core.Profiles;

internal static class ProfileDocumentMapper
{
  public static ProfileDocument Map(JsonElement root)
  {
    var profileElement = root.GetProperty("profile");
    var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in root.GetProperty("resources").EnumerateObject())
    {
      var resource = property.Value;
      if (resources.ContainsKey(property.Name))
      {
        continue;
      }

      resources.Add(property.Name, new ResourceDefinition
      {
        Id = property.Name,
        Type = resource.GetProperty("type").GetString()!,
        Provider = resource.GetProperty("provider").GetString()!,
        VersionConstraint = GetOptionalString(resource, "versionConstraint"),
        PreferredVersion = GetOptionalString(resource, "preferredVersion"),
        Dependencies = GetStringArray(resource, "dependsOn"),
        Parameters = GetParameters(resource)
      });
    }

    var profile = new DeveloperProfile
    {
      Id = profileElement.GetProperty("id").GetString()!,
      Version = profileElement.GetProperty("version").GetString()!,
      DisplayName = profileElement.GetProperty("displayName").GetString()!,
      Description = profileElement.GetProperty("description").GetString()!,
      RequiredResources = GetReferences(profileElement, "requiredResources"),
      OptionalResources = GetReferences(profileElement, "optionalResources"),
      Resources = resources
    };

    return new ProfileDocument
    {
      SchemaVersion = root.GetProperty("schemaVersion").GetString()!,
      Profile = profile,
      Resources = resources
    };
  }

  private static IReadOnlyList<ProfileResourceReference> GetReferences(
      JsonElement profile,
      string propertyName)
  {
    if (!profile.TryGetProperty(propertyName, out var references))
    {
      return Array.Empty<ProfileResourceReference>();
    }

    return references.EnumerateArray().Select(reference => new ProfileResourceReference
    {
      Id = reference.GetProperty("id").GetString()!,
      VersionConstraint = GetOptionalString(reference, "versionConstraint"),
      PreferredVersion = GetOptionalString(reference, "preferredVersion"),
      DefaultSelected = reference.TryGetProperty("defaultSelected", out var selected) && selected.GetBoolean()
    }).ToArray();
  }

  private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName) =>
      element.TryGetProperty(propertyName, out var array)
          ? array.EnumerateArray().Select(item => item.GetString()!).ToArray()
          : Array.Empty<string>();

  private static IReadOnlyDictionary<string, string?> GetParameters(JsonElement resource)
  {
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (!resource.TryGetProperty("parameters", out var parameters))
    {
      return result;
    }

    foreach (var property in parameters.EnumerateObject())
    {
      result.TryAdd(
          property.Name,
          property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString());
    }

    return result;
  }

  private static string? GetOptionalString(JsonElement element, string propertyName) =>
      element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
}
