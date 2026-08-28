using Wdem.Core.Execution;

namespace Wdem.Core.Processes;

public sealed record ProcessExecutionResult(
    bool Started,
    int? ExitCode,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError,
    StructuredError? Error = null);
