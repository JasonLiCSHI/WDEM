namespace Wdem.Core.Execution;

public sealed record RunRequest(
    string ProfilePath,
    IReadOnlySet<string> SelectedOptionalResourceIds,
    int MaximumConcurrency = 4,
    Guid? RetriedFromRunId = null,
    string? ApprovedPlanFingerprint = null);
