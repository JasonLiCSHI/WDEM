using Wdem.Core.Tasks;

namespace Wdem.Core.Workflows;

public interface ITaskWorkflowProvider
{
  TaskWorkflowDefinition Create(TaskDefinition task);
}
