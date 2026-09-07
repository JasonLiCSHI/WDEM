using System.Collections.ObjectModel;
using Wdem.Domain.Tasks;

namespace Wdem.Domain.Profiles;

public sealed record EnvironmentProfile
{
  public EnvironmentProfile(
      string id,
      string version,
      string displayName,
      string? description,
      IReadOnlyDictionary<string, TaskDefinition> tasks,
      int schemaVersion = 1)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    ArgumentException.ThrowIfNullOrWhiteSpace(version);
    ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
    ArgumentNullException.ThrowIfNull(tasks);

    Id = id;
    Version = version;
    DisplayName = displayName;
    Description = description;
    Tasks = new ReadOnlyDictionary<string, TaskDefinition>(
        tasks.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    SchemaVersion = schemaVersion;
  }

  public string Id { get; }

  public string Version { get; }

  public string DisplayName { get; }

  public string? Description { get; }

  public IReadOnlyDictionary<string, TaskDefinition> Tasks { get; }

  public int SchemaVersion { get; }
}
