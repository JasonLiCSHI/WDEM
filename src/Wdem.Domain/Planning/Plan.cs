using System.Collections.ObjectModel;
using Wdem.Domain.Tasks;

namespace Wdem.Domain.Planning;

public sealed record PlannedTask(TaskId Id);

public sealed class Plan
{
  internal Plan(IEnumerable<PlannedTask> tasks)
  {
    Tasks = new ReadOnlyCollection<PlannedTask>(tasks.ToArray());
  }

  public IReadOnlyList<PlannedTask> Tasks { get; }
}
