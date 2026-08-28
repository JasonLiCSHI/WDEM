using Wdem.Core.Execution;
using Wdem.Core.Graph;

namespace Wdem.Core.Planning;

public sealed record ExecutionPlan
{
  public required Guid PlanId { get; init; }
  public required string Fingerprint { get; init; }
  public required string ProfileId { get; init; }
  public required string ProfileVersion { get; init; }
  public required IReadOnlyList<ResourceGraphLayer> Layers { get; init; }
  public required IReadOnlyList<PlannedResource> Resources { get; init; }
  public required bool IsExecutable { get; init; }
  public IReadOnlyList<StructuredError> Errors { get; init; } = [];
}
