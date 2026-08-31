namespace Wdem.Core.Runs;

public sealed record WorkflowProgress(
    string TaskId,
    TaskExecutionState State,
    string? Stage,
    int Percent,
    TaskOutcome? Outcome = null,
    string? Message = null,
    WorkflowOutputStream? OutputStream = null);
