namespace Wdem.Application.Execution;

public sealed record WorkflowUpdate(
    WorkflowSnapshot Snapshot,
    WorkflowProgress? Change);
