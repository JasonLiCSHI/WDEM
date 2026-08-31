namespace Wdem.Core.Runtime;

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr);
