namespace Wdem.Core.Resources;

public enum PrivilegeRequirement
{
  CurrentUser,
  Administrator
}

public enum RestartPolicy
{
  NoRestart,
  RestartRecommended,
  RestartRequired
}

public sealed record ResourceDefinition
{
  public required string Id { get; init; }
  public required string Type { get; init; }
  public required string Provider { get; init; }
  public string? DisplayName { get; init; }
  public string? Description { get; init; }
  public string? VersionConstraint { get; init; }
  public string? PreferredVersion { get; init; }
  public string? ProfileSourcePath { get; init; }
  public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
  public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
      new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
  public PrivilegeRequirement PrivilegeRequirement { get; init; }
  public RestartPolicy RestartPolicy { get; init; }
}
