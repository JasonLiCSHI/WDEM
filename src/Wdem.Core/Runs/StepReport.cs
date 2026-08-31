namespace Wdem.Core.Runs;

public sealed record StepReport(
    string Phase,
    int ExitCode,
    string Stdout,
    string Stderr);
