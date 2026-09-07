namespace Wdem.Application.Inspection;

public sealed record InspectReport(IReadOnlyDictionary<string, TaskInspection> Tasks);
