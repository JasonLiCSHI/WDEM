namespace Wdem.Core.Runs;

public sealed record InspectReport(IReadOnlyDictionary<string, TaskInspection> Tasks);
