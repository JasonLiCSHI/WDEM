namespace Wdem.Domain.Workflows;

public readonly record struct TaskWorkflowTransitionContext(
    bool ActivitiesSucceeded,
    bool IsTaskSatisfied);
