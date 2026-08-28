using Wdem.Core.Resources;

namespace Wdem.Core.Profiles;

public sealed record DeveloperProfile
{
  private IReadOnlyDictionary<string, ResourceDefinition> _resources =
      new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);

  public required string Id { get; init; }
  public required string Version { get; init; }
  public required string DisplayName { get; init; }
  public required string Description { get; init; }
  public IReadOnlyList<ProfileResourceReference> RequiredResources { get; init; } =
      Array.Empty<ProfileResourceReference>();
  public IReadOnlyList<ProfileResourceReference> OptionalResources { get; init; } =
      Array.Empty<ProfileResourceReference>();
  public IReadOnlyDictionary<string, ResourceDefinition> Resources
  {
    get => _resources;
    init
    {
      ArgumentNullException.ThrowIfNull(value);
      _resources = value.ToDictionary(
          pair => pair.Key,
          pair => pair.Value,
          StringComparer.OrdinalIgnoreCase);
    }
  }
}
