using Wdem.Domain.Profiles;
using Wdem.Domain.Tasks;
using Xunit;

namespace Wdem.Domain.Tests;

public sealed class ProfileImmutabilityTests
{
  [Fact]
  public void ProfileCopiesTheTaskDictionaryAtItsBoundary()
  {
    var tasks = new Dictionary<string, TaskDefinition>(StringComparer.Ordinal)
    {
      ["git"] = Task("git")
    };
    var profile = new EnvironmentProfile("dev", "1.0.0", "Developer", null, tasks);

    tasks.Clear();

    Assert.Single(profile.Tasks);
    Assert.Throws<NotSupportedException>(() =>
        ((IDictionary<string, TaskDefinition>)profile.Tasks).Clear());
  }

  [Fact]
  public void TaskCopiesCommandAndDependencyCollectionsAtItsBoundary()
  {
    var dependencies = new List<string> { "git" };
    var pre = new List<CommandDefinition> { Command("pre") };
    var post = new List<CommandDefinition> { Command("post") };
    var task = new TaskDefinition(
        "ide",
        "IDE",
        true,
        dependencies,
        null,
        null,
        null,
        Command("detect"),
        pre,
        Command("apply"),
        post);

    dependencies.Clear();
    pre.Clear();
    post.Clear();

    Assert.Equal(["git"], task.DependsOn);
    Assert.Single(task.Pre);
    Assert.Single(task.Post);
    Assert.Throws<NotSupportedException>(() =>
        ((IList<string>)task.DependsOn).Clear());
  }

  private static TaskDefinition Task(string id) => new(
      id,
      id,
      true,
      [],
      null,
      null,
      null,
      Command("detect"),
      [],
      Command("apply"),
      []);

  private static CommandDefinition Command(string executable) => new(executable, []);
}
