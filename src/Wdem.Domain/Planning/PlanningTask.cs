using System.Collections.ObjectModel;
using Wdem.Domain.Tasks;

namespace Wdem.Domain.Planning;

public sealed class PlanningTask
{
  public PlanningTask(
      TaskId id,
      bool isRequired,
      IEnumerable<TaskId> dependencies)
  {
    ArgumentNullException.ThrowIfNull(id);
    ArgumentNullException.ThrowIfNull(dependencies);
    Id = id;
    IsRequired = isRequired;
    Dependencies = new ReadOnlyCollection<TaskId>(dependencies.ToArray());
  }

  public TaskId Id { get; }

  public bool IsRequired { get; }

  public IReadOnlyList<TaskId> Dependencies { get; }
}
