using Wdem.Core.Profiles;
using Wdem.Domain.Planning;
using Wdem.Domain.Tasks;

namespace Wdem.Core.Planning;

public static class ProfilePlanner
{
  public static Plan CreateForSelection(
      EnvironmentProfile profile,
      IReadOnlyCollection<string> selectedOptionalTaskIds)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(selectedOptionalTaskIds);
    return TaskPlanner.CreateForSelection(
        ToPlanningTasks(profile),
        selectedOptionalTaskIds.Select(TaskId.Parse).ToArray());
  }

  public static Plan CreateForTasks(
      EnvironmentProfile profile,
      IReadOnlyCollection<string> rootTaskIds)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(rootTaskIds);
    return TaskPlanner.CreateForTasks(
        ToPlanningTasks(profile),
        rootTaskIds.Select(TaskId.Parse).ToArray());
  }

  private static IReadOnlyDictionary<TaskId, PlanningTask> ToPlanningTasks(
      EnvironmentProfile profile) =>
      profile.Tasks.Values.ToDictionary(
          task => TaskId.Parse(task.Id),
          task => new PlanningTask(
              TaskId.Parse(task.Id),
              task.Required,
              task.DependsOn.Select(TaskId.Parse)));
}
