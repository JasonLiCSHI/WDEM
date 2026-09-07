namespace Wdem.Application.Runtime;

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
