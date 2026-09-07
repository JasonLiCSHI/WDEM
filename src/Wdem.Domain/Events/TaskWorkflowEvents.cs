using Wdem.Domain.Execution;
using Wdem.Domain.Workflows;

namespace Wdem.Domain.Events;

public sealed record TaskWorkflowStarted(
    string ProfileId,
    string TaskId,
    string InitialStateId) : IDomainEvent;

public sealed record TaskWorkflowStateEntered(
    string ProfileId,
    string TaskId,
    string StateId,
    TaskExecutionState TaskState) : IDomainEvent;

public sealed record TaskWorkflowActivityStarted(
    string ProfileId,
    string TaskId,
    string StateId,
    string ActivityId,
    WorkflowActivityLocation Location,
    int ActivityIndex) : IDomainEvent;

public sealed record TaskWorkflowActivityCompleted(
    string ProfileId,
    string TaskId,
    string StateId,
    string ActivityId,
    WorkflowActivityLocation Location,
    int ActivityIndex,
    bool Succeeded,
    bool? IsTaskSatisfied,
    string? Error) : IDomainEvent;

public sealed record TaskWorkflowTransitioned(
    string ProfileId,
    string TaskId,
    string FromStateId,
    string ToStateId,
    string TransitionName) : IDomainEvent;

public sealed record TaskWorkflowFinished(
    string ProfileId,
    string TaskId,
    string? StateId,
    TaskOutcome Outcome,
    string? Error) : IDomainEvent;
