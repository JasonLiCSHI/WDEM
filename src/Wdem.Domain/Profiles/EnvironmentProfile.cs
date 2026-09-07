using System.Collections.ObjectModel;
using Wdem.Domain.Model;
using Wdem.Domain.Tasks;

namespace Wdem.Domain.Profiles;

/// <summary>
/// Aggregate root for one declared developer environment. It owns the complete
/// Task dependency graph and prevents an invalid graph from entering the Domain.
/// </summary>
public sealed class EnvironmentProfile : IAggregateRoot
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

    if (schemaVersion is not (1 or 2))
    {
      throw new ArgumentOutOfRangeException(
          nameof(schemaVersion),
          schemaVersion,
          "Profile schema version must be 1 or 2.");
    }

    if (tasks.Count == 0)
    {
      throw new ArgumentException("A Profile must declare at least one Task.", nameof(tasks));
    }

    var taskGraph = tasks.ToDictionary(
        pair => pair.Key,
        pair => pair.Value,
        StringComparer.Ordinal);
    if (schemaVersion == 1 && taskGraph.Values.Any(task => task.Workflow is not null))
    {
      throw new ArgumentException(
          "Task workflows require Profile schemaVersion 2.",
          nameof(tasks));
    }
    ValidateTaskGraph(taskGraph);

    Id = id;
    Version = version;
    DisplayName = displayName;
    Description = description;
    Tasks = new ReadOnlyDictionary<string, TaskDefinition>(
        taskGraph);
    SchemaVersion = schemaVersion;
  }

  public string Id { get; }

  public string Version { get; }

  public string DisplayName { get; }

  public string? Description { get; }

  public IReadOnlyDictionary<string, TaskDefinition> Tasks { get; }

  public int SchemaVersion { get; }

  private static void ValidateTaskGraph(IReadOnlyDictionary<string, TaskDefinition> tasks)
  {
    foreach (var (taskId, task) in tasks)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
      ArgumentNullException.ThrowIfNull(task);

      if (!string.Equals(taskId, task.Id, StringComparison.Ordinal))
      {
        throw new ArgumentException(
            $"Task dictionary key '{taskId}' does not match Task id '{task.Id}'.",
            nameof(tasks));
      }

      try
      {
        _ = TaskId.Parse(taskId);
      }
      catch (FormatException exception)
      {
        throw new ArgumentException(exception.Message, nameof(tasks), exception);
      }

      var duplicateDependency = task.DependsOn
          .GroupBy(dependencyId => dependencyId, StringComparer.Ordinal)
          .FirstOrDefault(group => group.Count() > 1)
          ?.Key;
      if (duplicateDependency is not null)
      {
        throw new ArgumentException(
            $"Task '{taskId}' declares dependency '{duplicateDependency}' more than once.",
            nameof(tasks));
      }

      foreach (var dependencyId in task.DependsOn)
      {
        if (string.Equals(taskId, dependencyId, StringComparison.Ordinal))
        {
          throw new ArgumentException(
              $"Task '{taskId}' cannot depend on itself.",
              nameof(tasks));
        }

        if (!tasks.ContainsKey(dependencyId))
        {
          throw new ArgumentException(
              $"Task '{taskId}' depends on undeclared Task '{dependencyId}'.",
              nameof(tasks));
        }
      }
    }

    ValidateAcyclic(tasks);
  }

  private static void ValidateAcyclic(IReadOnlyDictionary<string, TaskDefinition> tasks)
  {
    var visited = new HashSet<string>(StringComparer.Ordinal);
    var visiting = new HashSet<string>(StringComparer.Ordinal);
    var path = new List<string>();

    foreach (var taskId in tasks.Keys.OrderBy(value => value, StringComparer.Ordinal))
    {
      Visit(taskId);
    }

    void Visit(string taskId)
    {
      if (visited.Contains(taskId))
      {
        return;
      }

      if (!visiting.Add(taskId))
      {
        var cycleStart = path.IndexOf(taskId);
        var cycle = path.Skip(cycleStart).Append(taskId);
        throw new ArgumentException(
            $"Profile Task dependency cycle detected: {string.Join(" -> ", cycle)}.",
            nameof(tasks));
      }

      path.Add(taskId);
      foreach (var dependencyId in tasks[taskId].DependsOn)
      {
        Visit(dependencyId);
      }
      path.RemoveAt(path.Count - 1);
      visiting.Remove(taskId);
      visited.Add(taskId);
    }
  }
}
