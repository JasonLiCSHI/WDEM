using Wdem.Application.Inspection;
using Wdem.Domain.Planning;
using Wdem.Domain.Profiles;
using Wdem.Domain.Tasks;
using Wdem.Domain.Versions;

namespace Wdem.Application.Planning;

public sealed class CreatePlanHandler
{
  public Plan CreateForSelection(
      EnvironmentProfile profile,
      IReadOnlyCollection<string> selectedOptionalTaskIds,
      InspectReport? inspection = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(selectedOptionalTaskIds);
    return TaskPlanner.CreateForSelection(
        ToPlanningTasks(profile, inspection),
        selectedOptionalTaskIds.Select(TaskId.Parse).ToArray());
  }

  public Plan CreateForTasks(
      EnvironmentProfile profile,
      IReadOnlyCollection<string> rootTaskIds,
      InspectReport? inspection = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(rootTaskIds);
    return TaskPlanner.CreateForTasks(
        ToPlanningTasks(profile, inspection),
        rootTaskIds.Select(TaskId.Parse).ToArray());
  }

  private static IReadOnlyDictionary<TaskId, PlanningTask> ToPlanningTasks(
      EnvironmentProfile profile,
      InspectReport? inspection) =>
      profile.Tasks.Values.ToDictionary(
          task => TaskId.Parse(task.Id),
          task => new PlanningTask(
              TaskId.Parse(task.Id),
              task.Required,
              task.DependsOn.Select(TaskId.Parse),
              inspection?.Tasks.TryGetValue(task.Id, out var result) == true
                  ? result.Compliance
                  : ComplianceStatus.Missing));
}
