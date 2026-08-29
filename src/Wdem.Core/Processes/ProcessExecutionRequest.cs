namespace Wdem.Core.Processes;

public sealed record ProcessExecutionRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    TimeSpan? Timeout = null);
