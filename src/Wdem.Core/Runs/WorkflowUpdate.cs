namespace Wdem.Core.Runs;

public sealed record WorkflowUpdate(
    WorkflowSnapshot Snapshot,
    WorkflowProgress? Change);
