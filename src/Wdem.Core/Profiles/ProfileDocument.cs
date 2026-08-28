using Wdem.Core.Resources;

namespace Wdem.Core.Profiles;

internal sealed record ProfileDocument
{
  public required string SchemaVersion { get; init; }
  public required DeveloperProfile Profile { get; init; }
  public required IReadOnlyDictionary<string, ResourceDefinition> Resources { get; init; }
}
