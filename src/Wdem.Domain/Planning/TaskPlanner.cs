using Wdem.Domain.Tasks;
using Wdem.Domain.Versions;

namespace Wdem.Domain.Planning;

public static class TaskPlanner
{
  public static Plan CreateForSelection(
      IReadOnlyDictionary<TaskId, PlanningTask> tasks,
      IReadOnlyCollection<TaskId> selectedOptionalTaskIds)
  {
    ArgumentNullException.ThrowIfNull(tasks);
    ArgumentNullException.ThrowIfNull(selectedOptionalTaskIds);

    ValidateKnownTasks(tasks, selectedOptionalTaskIds);
    var roots = tasks.Values
        .Where(task => task.IsRequired)
        .Select(task => task.Id)
        .Concat(selectedOptionalTaskIds)
        .Distinct()
        .ToArray();

    return CreateForTasks(tasks, roots);
  }

  public static Plan CreateForTasks(
      IReadOnlyDictionary<TaskId, PlanningTask> tasks,
      IReadOnlyCollection<TaskId> rootTaskIds)
  {
    ArgumentNullException.ThrowIfNull(tasks);
    ArgumentNullException.ThrowIfNull(rootTaskIds);
    ValidateKnownTasks(tasks, rootTaskIds);

    var included = new HashSet<TaskId>();
    foreach (var root in rootTaskIds)
    {
      IncludeDependencies(tasks, root, included);
    }

    var planned = new List<PlannedTask>(included.Count);
    var actions = new Dictionary<TaskId, PlannedTaskAction>(included.Count);
    foreach (var id in TopologicalSort(tasks, included))
    {
      var task = tasks[id];
      var blockedByDependency = task.Dependencies.Any(dependency =>
          actions.TryGetValue(dependency, out var dependencyAction) &&
              dependencyAction == PlannedTaskAction.Blocked);
      var action = blockedByDependency
          ? PlannedTaskAction.Blocked
          : ToAction(task.Compliance);
      planned.Add(new PlannedTask(id, action));
      actions.Add(id, action);
    }

    return new Plan(planned);
  }

  private static PlannedTaskAction ToAction(ComplianceStatus compliance) => compliance switch
  {
    ComplianceStatus.Satisfied => PlannedTaskAction.NoOp,
    ComplianceStatus.Missing => PlannedTaskAction.Install,
    ComplianceStatus.UpgradeRequired or ComplianceStatus.VersionMismatch => PlannedTaskAction.Upgrade,
    ComplianceStatus.DetectionFailed => PlannedTaskAction.Blocked,
    _ => throw new ArgumentOutOfRangeException(nameof(compliance), compliance, null)
  };

  private static void ValidateKnownTasks(
      IReadOnlyDictionary<TaskId, PlanningTask> tasks,
      IEnumerable<TaskId> taskIds)
  {
    foreach (var taskId in taskIds)
    {
      if (!tasks.ContainsKey(taskId))
      {
        throw new FormatException($"Unknown task id '{taskId.Value}'.");
      }
    }
  }

  private static void IncludeDependencies(
      IReadOnlyDictionary<TaskId, PlanningTask> tasks,
      TaskId taskId,
      HashSet<TaskId> included)
  {
    if (!included.Add(taskId))
    {
      return;
    }

    var task = tasks[taskId];
    ValidateKnownTasks(tasks, task.Dependencies);
    foreach (var dependency in task.Dependencies)
    {
      IncludeDependencies(tasks, dependency, included);
    }
  }

  private static IReadOnlyList<TaskId> TopologicalSort(
      IReadOnlyDictionary<TaskId, PlanningTask> tasks,
      HashSet<TaskId> included)
  {
    var permanent = new HashSet<TaskId>();
    var temporary = new HashSet<TaskId>();
    var ordered = new List<TaskId>(included.Count);
    var stack = new Stack<TaskId>();

    foreach (var taskId in included.OrderBy(id => id.Value, StringComparer.Ordinal))
    {
      Visit(taskId);
    }

    return ordered;

    void Visit(TaskId taskId)
    {
      if (permanent.Contains(taskId))
      {
        return;
      }

      if (!temporary.Add(taskId))
      {
        var cyclePath = stack.Reverse().Append(taskId).Select(id => id.Value);
        throw new InvalidOperationException(
            $"Dependency cycle detected: {string.Join(" -> ", cyclePath)}.");
      }

      stack.Push(taskId);
      foreach (var dependency in tasks[taskId].Dependencies
                   .Where(included.Contains)
                   .OrderBy(id => id.Value, StringComparer.Ordinal))
      {
        Visit(dependency);
      }
      stack.Pop();

      temporary.Remove(taskId);
      permanent.Add(taskId);
      ordered.Add(taskId);
    }
  }
}
