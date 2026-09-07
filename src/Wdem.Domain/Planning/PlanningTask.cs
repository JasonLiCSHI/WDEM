using System.Collections.ObjectModel;
using Wdem.Domain.Tasks;
using Wdem.Domain.Versions;

namespace Wdem.Domain.Planning;

public sealed class PlanningTask
{
  public PlanningTask(
      TaskId id,
      bool isRequired,
      IEnumerable<TaskId> dependencies,
      ComplianceStatus compliance = ComplianceStatus.Missing)
  {
    ArgumentNullException.ThrowIfNull(id);
    ArgumentNullException.ThrowIfNull(dependencies);
    Id = id;
    IsRequired = isRequired;
    Dependencies = new ReadOnlyCollection<TaskId>(dependencies.ToArray());
    Compliance = compliance;
  }

  public TaskId Id { get; }

  public bool IsRequired { get; }

  public IReadOnlyList<TaskId> Dependencies { get; }

  public ComplianceStatus Compliance { get; }
}
