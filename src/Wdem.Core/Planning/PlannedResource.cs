using Wdem.Core.Execution;
using Wdem.Core.Graph;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public enum PlannedResourceStatus
{
  Ready,
  Deferred,
  AlreadySatisfied,
  Blocked,
  Unsupported,
  DetectionFailed,
  Invalid
}

public enum PlanRisk
{
  None,
  Standard,
  Elevated,
  Destructive
}

public sealed record DeferredPlanAuthorization
{
  private IReadOnlyList<PlanAction> _allowedActions = [];

  public required IReadOnlyList<PlanAction> AllowedActions
  {
    get => _allowedActions;
    init => _allowedActions = Array.AsReadOnly(
        (value ?? throw new ArgumentNullException(nameof(value))).ToArray());
  }

  public required PrivilegeRequirement MaximumPrivilege { get; init; }
  public required RestartPolicy MaximumRestartPolicy { get; init; }
  public required PlanRisk MaximumRisk { get; init; }
  public required bool AllowDestructive { get; init; }
  public required string DynamicPlanNotice { get; init; }
}

public sealed record PlannedResource
{
  public required ResourceDefinition Definition { get; init; }
  public required ResourceOrigin Origin { get; init; }
  public required IReadOnlyList<string> Dependencies { get; init; }
  public required ResourcePlan ResourcePlan { get; init; }
  public required PlannedResourceStatus Status { get; init; }
  public required PlanRisk Risk { get; init; }
  public required bool RequiresElevation { get; init; }
  public required bool IsDestructive { get; init; }
  public required RestartPolicy RestartPolicy { get; init; }
  public DeferredPlanAuthorization? DeferredAuthorization { get; init; }
  public string? Reason { get; init; }
  public IReadOnlyList<string> BlockedBy { get; init; } = [];
  public IReadOnlyList<StructuredError> Diagnostics { get; init; } = [];
}
