namespace Wdem.LegacySource.Models;

public sealed record ProcessRunResult(
    bool Started,
    int? ExitCode,
    IReadOnlyList<string> StandardOutput,
    IReadOnlyList<string> StandardError);
