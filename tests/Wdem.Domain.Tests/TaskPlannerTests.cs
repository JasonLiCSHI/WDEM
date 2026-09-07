using Wdem.Domain.Planning;
using Wdem.Domain.Tasks;
using Wdem.Domain.Versions;
using Xunit;

namespace Wdem.Domain.Tests;

public sealed class TaskPlannerTests
{
  [Fact]
  public void CreateForSelection_WhenTasksAreSelected_ThenIncludesRequiredAndTransitiveDependencies()
  {
    var tasks = Tasks(
        Required("dotnet"),
        Required("visual-studio", "dotnet"),
        Optional("resharper", "visual-studio"));

    var plan = TaskPlanner.CreateForSelection(tasks, [TaskId.Parse("resharper")]);

    Assert.Equal(["dotnet", "visual-studio", "resharper"], Values(plan));
  }

  [Fact]
  public void CreateForTasks_WhenTaskIsUnknown_ThenRejectsIt()
  {
    var tasks = Tasks(Required("dotnet"));

    var exception = Assert.Throws<FormatException>(() =>
        TaskPlanner.CreateForTasks(tasks, [TaskId.Parse("missing")]));

    Assert.Contains("missing", exception.Message);
  }

  [Fact]
  public void CreateForTasks_WhenGraphContainsCycle_ThenReportsCyclePath()
  {
    var tasks = Tasks(
        Required("a", "b"),
        Required("b", "c"),
        Required("c", "a"));

    var exception = Assert.Throws<InvalidOperationException>(() =>
        TaskPlanner.CreateForTasks(tasks, [TaskId.Parse("a")]));

    Assert.Contains("a", exception.Message);
    Assert.Contains("b", exception.Message);
    Assert.Contains("c", exception.Message);
  }

  [Fact]
  public void CreateForTasks_WhenTasksAreIndependent_ThenProducesDeterministicOrder()
  {
    var tasks = Tasks(Required("z"), Required("a"));

    var plan = TaskPlanner.CreateForTasks(
        tasks,
        [TaskId.Parse("z"), TaskId.Parse("a")]);

    Assert.Equal(["a", "z"], Values(plan));
  }

  [Fact]
  public void CreateForTasks_WhenComplianceIsKnown_ThenProjectsExecutionActions()
  {
    var tasks = Tasks(
        RequiredWithCompliance("installed", ComplianceStatus.Satisfied),
        RequiredWithCompliance("missing", ComplianceStatus.Missing),
        RequiredWithCompliance("old", ComplianceStatus.UpgradeRequired));

    var plan = TaskPlanner.CreateForTasks(tasks, tasks.Keys.ToArray());

    Assert.Equal(PlannedTaskAction.NoOp, Find(plan, "installed").Action);
    Assert.Equal(PlannedTaskAction.Install, Find(plan, "missing").Action);
    Assert.Equal(PlannedTaskAction.Upgrade, Find(plan, "old").Action);
  }

  [Fact]
  public void CreateForTasks_WhenDetectionFails_ThenBlocksTaskAndDependents()
  {
    var tasks = Tasks(
        RequiredWithCompliance("broken", ComplianceStatus.DetectionFailed),
        RequiredWithCompliance("dependent", ComplianceStatus.Missing, "broken"),
        RequiredWithCompliance("independent", ComplianceStatus.Missing));

    var plan = TaskPlanner.CreateForTasks(tasks, tasks.Keys.ToArray());

    Assert.Equal(PlannedTaskAction.Blocked, Find(plan, "broken").Action);
    Assert.Equal(PlannedTaskAction.Blocked, Find(plan, "dependent").Action);
    Assert.Equal(PlannedTaskAction.Install, Find(plan, "independent").Action);
  }

  private static IReadOnlyDictionary<TaskId, PlanningTask> Tasks(params PlanningTask[] tasks) =>
      tasks.ToDictionary(task => task.Id);

  private static PlanningTask Required(string id, params string[] dependencies) =>
      new(
          TaskId.Parse(id),
          isRequired: true,
          dependencies.Select(TaskId.Parse).ToArray());

  private static PlanningTask RequiredWithCompliance(
      string id,
      ComplianceStatus compliance,
      params string[] dependencies) =>
      new(
          TaskId.Parse(id),
          isRequired: true,
          dependencies.Select(TaskId.Parse).ToArray(),
          compliance);

  private static PlanningTask Optional(string id, params string[] dependencies) =>
      new(
          TaskId.Parse(id),
          isRequired: false,
          dependencies.Select(TaskId.Parse).ToArray());

  private static string[] Values(Plan plan) =>
      plan.Tasks.Select(task => task.Id.Value).ToArray();

  private static PlannedTask Find(Plan plan, string id) =>
      Assert.Single(plan.Tasks, task => task.Id.Value == id);
}
