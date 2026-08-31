namespace Wdem.Core.Runs;

public sealed record RunReport(IReadOnlyDictionary<string, TaskReport> Tasks);
