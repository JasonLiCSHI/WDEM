namespace Wdem.Core.Profiles;

public sealed record ProfileResourceReference
{
  public required string Id { get; init; }
  public string? VersionConstraint { get; init; }
  public string? PreferredVersion { get; init; }
  public bool DefaultSelected { get; init; }
}
