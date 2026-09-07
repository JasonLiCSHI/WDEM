using System.Collections.ObjectModel;
using Wdem.Core.Workflows;
using Wdem.Domain.Tasks;

namespace Wdem.Core.Profiles;

public sealed class EnvironmentProfile
{
  public EnvironmentProfile(
      string id,
      string version,
      string displayName,
      string? description,
      IReadOnlyDictionary<string, TaskDefinition> tasks,
      int schemaVersion = 1,
      IReadOnlyDictionary<string, TaskWorkflowDefinition>? workflows = null)
  {
    Id = id;
    Version = version;
    DisplayName = displayName;
    Description = description;
    Tasks = tasks;
    SchemaVersion = schemaVersion;
    Workflows = workflows ?? new ReadOnlyDictionary<string, TaskWorkflowDefinition>(
        new Dictionary<string, TaskWorkflowDefinition>(StringComparer.Ordinal));
  }

  public string Id { get; }

  public string Version { get; }

  public string DisplayName { get; }

  public string? Description { get; }

  public IReadOnlyDictionary<string, TaskDefinition> Tasks { get; }

  public int SchemaVersion { get; }

  public IReadOnlyDictionary<string, TaskWorkflowDefinition> Workflows { get; }
}
