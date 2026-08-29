namespace Wdem.Core.Execution;

public sealed record ElevatedResourceRequest(
    Guid RunId,
    string ResourceId,
    string PlanFingerprint);
