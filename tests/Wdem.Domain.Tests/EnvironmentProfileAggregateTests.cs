using Wdem.Domain.Model;
using Wdem.Domain.Profiles;
using Wdem.Domain.Tasks;
using Wdem.Domain.Execution;
using Wdem.Domain.Workflows;
using Xunit;

namespace Wdem.Domain.Tests;

public sealed class EnvironmentProfileAggregateTests
{
  [Fact]
  public void EnvironmentProfile_WhenInspected_ThenIsTheDomainAggregateRoot()
  {
    var aggregateRoots = typeof(IAggregateRoot).Assembly
        .GetTypes()
        .Where(type => type is { IsAbstract: false, IsInterface: false } &&
            typeof(IAggregateRoot).IsAssignableFrom(type))
        .ToArray();

    Assert.Equal([typeof(EnvironmentProfile)], aggregateRoots);
  }

  [Fact]
  public void Create_WhenTaskKeyDiffersFromEntityId_ThenRejectsAggregate()
  {
    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal)
    {
      ["declared-id"] = Task("different-id")
    };

    var exception = Assert.Throws<ArgumentException>(() => Profile(tasks));

    Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_WhenDependencyIsOutsideAggregate_ThenRejectsAggregate()
  {
    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal)
    {
      ["ide"] = Task("ide", "sdk")
    };

    var exception = Assert.Throws<ArgumentException>(() => Profile(tasks));

    Assert.Contains("undeclared Task 'sdk'", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_WhenTaskGraphContainsCycle_ThenRejectsAggregateWithCyclePath()
  {
    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal)
    {
      ["ide"] = Task("ide", "sdk"),
      ["sdk"] = Task("sdk", "ide")
    };

    var exception = Assert.Throws<ArgumentException>(() => Profile(tasks));

    Assert.Contains("ide -> sdk -> ide", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_WhenDependencyIsDeclaredTwice_ThenRejectsAggregate()
  {
    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal)
    {
      ["sdk"] = Task("sdk"),
      ["ide"] = Task("ide", "sdk", "sdk")
    };

    var exception = Assert.Throws<ArgumentException>(() => Profile(tasks));

    Assert.Contains("more than once", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Create_WhenSchemaOneTaskDeclaresWorkflow_ThenRejectsAggregate()
  {
    var task = new TaskDefinition(
        "tool",
        "tool",
        true,
        [],
        null,
        null,
        null,
        new CommandDefinition("detect", []),
        [],
        new CommandDefinition("apply", []),
        [],
        workflow: new TaskWorkflowDefinition(
          "done",
          [
            new TaskWorkflowState(
                "done",
                TaskExecutionState.Succeeded,
                terminalOutcome: TaskOutcome.Succeeded)
          ]));
    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal)
    {
      [task.Id] = task
    };

    var exception = Assert.Throws<ArgumentException>(() => Profile(tasks));

    Assert.Contains("schemaVersion 2", exception.Message, StringComparison.Ordinal);
  }

  private static EnvironmentProfile Profile(
      IReadOnlyDictionary<string, TaskDefinition> tasks) =>
      new("developer", "1.0.0", "Developer", null, tasks);

  private static TaskDefinition Task(string id, params string[] dependencies) => new(
      id,
      id,
      true,
      dependencies,
      null,
      null,
      null,
      new CommandDefinition("detect", []),
      [],
      new CommandDefinition("apply", []),
      []);
}
