using Wdem.Application.Runtime;
using Wdem.Domain.Execution;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

public sealed record WorkflowProgress(
    string TaskId,
    TaskExecutionState State,
    string? Stage,
    int Percent,
    TaskOutcome? Outcome = null,
    string? Message = null,
    WorkflowOutputStream? OutputStream = null)
{
  public string? RuntimeStateId { get; init; }

  public string? ActivityId { get; init; }

  public WorkflowActivityLocation? ActivityLocation { get; init; }
}
