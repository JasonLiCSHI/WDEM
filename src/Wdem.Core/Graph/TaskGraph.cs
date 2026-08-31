using Wdem.Core.Profiles;

namespace Wdem.Core.Graph;

public sealed class TaskGraph
{
  private TaskGraph(IReadOnlyList<string> orderedTaskIds)
  {
    OrderedTaskIds = orderedTaskIds;
  }

  public IReadOnlyList<string> OrderedTaskIds { get; }

  public static TaskGraph BuildForSelection(
      EnvironmentProfile profile,
      IReadOnlyCollection<string> selectedOptionalTaskIds)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(selectedOptionalTaskIds);

    var required = profile.Tasks.Values
        .Where(task => task.Required)
        .Select(task => task.Id)
        .ToArray();

    foreach (var optional in selectedOptionalTaskIds)
    {
      if (!profile.Tasks.ContainsKey(optional))
      {
        throw new FormatException($"Unknown task id '{optional}'.");
      }
      if (profile.Tasks[optional].Required)
      {
        continue;
      }
    }

    var roots = required
        .Concat(selectedOptionalTaskIds)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    return Build(profile, roots);
  }

  public static TaskGraph Build(EnvironmentProfile profile, IReadOnlyCollection<string> rootTaskIds)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(rootTaskIds);

    foreach (var root in rootTaskIds)
    {
      if (!profile.Tasks.ContainsKey(root))
      {
        throw new FormatException($"Unknown task id '{root}'.");
      }
    }

    var included = new HashSet<string>(StringComparer.Ordinal);
    foreach (var root in rootTaskIds)
    {
      IncludeDependencies(profile, root, included);
    }

    var ordered = TopologicalSort(profile, included);
    return new TaskGraph(ordered);
  }

  private static void IncludeDependencies(
      EnvironmentProfile profile,
      string taskId,
      HashSet<string> included)
  {
    if (!included.Add(taskId))
    {
      return;
    }

    var task = profile.Tasks[taskId];
    foreach (var dependency in task.DependsOn)
    {
      IncludeDependencies(profile, dependency, included);
    }
  }

  private static IReadOnlyList<string> TopologicalSort(
      EnvironmentProfile profile,
      HashSet<string> included)
  {
    var permanent = new HashSet<string>(StringComparer.Ordinal);
    var temporary = new HashSet<string>(StringComparer.Ordinal);
    var ordered = new List<string>(capacity: included.Count);
    var stack = new Stack<string>();

    foreach (var taskId in included.OrderBy(value => value, StringComparer.Ordinal))
    {
      Visit(taskId);
    }

    return ordered;

    void Visit(string taskId)
    {
      if (permanent.Contains(taskId))
      {
        return;
      }

      if (!temporary.Add(taskId))
      {
        var cyclePath = stack.Reverse().Concat([taskId]).ToArray();
        throw new InvalidOperationException(
            $"Dependency cycle detected: {string.Join(" -> ", cyclePath)}.");
      }

      stack.Push(taskId);
      var task = profile.Tasks[taskId];
      foreach (var dependency in task.DependsOn.OrderBy(value => value, StringComparer.Ordinal))
      {
        if (included.Contains(dependency))
        {
          Visit(dependency);
        }
      }
      stack.Pop();

      temporary.Remove(taskId);
      permanent.Add(taskId);
      ordered.Add(taskId);
    }
  }
}
