using Wdem.Application.Execution;

namespace Wdem.Application.Workflows;

public sealed record WorkflowActivityResult(
    bool Succeeded,
    StepReport? Step = null,
    string? Error = null)
{
  public bool? IsTaskSatisfied { get; init; }

  public static WorkflowActivityResult Success(StepReport? step = null) =>
      new(true, step);

  public static WorkflowActivityResult Failure(string error, StepReport? step = null) =>
      new(false, step, error);
}
