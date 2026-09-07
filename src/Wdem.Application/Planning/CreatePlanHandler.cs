using Wdem.Domain.Planning;
using Wdem.Domain.Profiles;
using Wdem.Domain.Tasks;

namespace Wdem.Application.Planning;

public sealed class CreatePlanHandler
{
  public Plan CreateForSelection(
      EnvironmentProfile profile,
      IReadOnlyCollection<string> selectedOptionalTaskIds)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(selectedOptionalTaskIds);
    return TaskPlanner.CreateForSelection(
        ToPlanningTasks(profile),
        selectedOptionalTaskIds.Select(TaskId.Parse).ToArray());
  }

  public Plan CreateForTasks(
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
