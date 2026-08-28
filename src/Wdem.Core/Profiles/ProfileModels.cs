using Wdem.Core.Execution;
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

public sealed record ProfileResourceReference
{
  public required string Id { get; init; }
  public string? VersionConstraint { get; init; }
  public string? PreferredVersion { get; init; }
  public bool DefaultSelected { get; init; }
}

internal sealed record ProfileDocument
{
  public required string SchemaVersion { get; init; }
  public required DeveloperProfile Profile { get; init; }
}

public sealed record ProfileLoadResult
{
  public DeveloperProfile? Profile { get; init; }
  public IReadOnlyList<StructuredError> Errors { get; init; } = Array.Empty<StructuredError>();
  public required string SourcePath { get; init; }
  public bool IsValid => Profile is not null && Errors.Count == 0;
}

public sealed record ProfileExpansionResult
{
  public DeveloperProfile? Profile { get; init; }
  public IReadOnlyList<StructuredError> Errors { get; init; } = Array.Empty<StructuredError>();
  public bool IsValid => Profile is not null && Errors.Count == 0;
}
