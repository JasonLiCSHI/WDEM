using Wdem.Domain.Tasks;
using Wdem.Domain.Workflows;

namespace Wdem.Core.Workflows;

public interface ITaskWorkflowProvider
{
  TaskWorkflowDefinition Create(TaskDefinition task);
}
