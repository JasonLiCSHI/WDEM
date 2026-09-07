namespace Wdem.Application.Runtime;

public sealed record CommandOutput(
    WorkflowOutputStream Stream,
    string Message);
