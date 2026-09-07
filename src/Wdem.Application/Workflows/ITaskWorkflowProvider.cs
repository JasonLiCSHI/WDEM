using Wdem.Domain.Tasks;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Workflows;

public interface ITaskWorkflowProvider
{
  TaskWorkflowDefinition Create(TaskDefinition task);
}
