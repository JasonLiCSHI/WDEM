using Wdem.Domain.Tasks;

namespace Wdem.Core.Workflows;

public interface ITaskWorkflowProvider
{
  TaskWorkflowDefinition Create(
      TaskDefinition task,
      TaskWorkflowDefinition? declaredWorkflow = null);
}
