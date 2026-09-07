namespace Wdem.Application.Execution;

public sealed record RunReport(IReadOnlyDictionary<string, TaskReport> Tasks);
