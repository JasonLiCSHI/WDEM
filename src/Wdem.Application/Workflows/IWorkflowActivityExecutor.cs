using Wdem.Domain.Workflows;

namespace Wdem.Application.Workflows;

public interface IWorkflowActivityExecutor
{
  Task<WorkflowActivityResult> ExecuteAsync(
      WorkflowActivity activity,
      WorkflowActivityContext context,
      CancellationToken cancellationToken);
}
