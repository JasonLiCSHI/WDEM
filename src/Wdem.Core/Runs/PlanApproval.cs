using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Runs;

public enum PlanApprovalSource
{
  ExplicitApplyRequest,
  CommandLine,
  DesktopReviewedPlan,
  Retry
}

public sealed record PlanApproval
{
  private IReadOnlyList<DeferredAuthorizationProof> _deferredAuthorizations = [];

  public required string InitialPlanFingerprint { get; init; }
  public required DateTimeOffset ConfirmedAtUtc { get; init; }
  public required PlanApprovalSource Source { get; init; }
  public IReadOnlyList<DeferredAuthorizationProof> DeferredAuthorizations
  {
    get => _deferredAuthorizations;
    init => _deferredAuthorizations = Array.AsReadOnly(
        (value ?? throw new ArgumentNullException(nameof(value))).ToArray());
  }
}

public sealed record DeferredAuthorizationProof
{
  private IReadOnlyList<string> _dependencies = [];
  private IReadOnlyList<PlanAction> _allowedActions = [];

  public required string ResourceId { get; init; }
  public required string ResourceType { get; init; }
  public required string ProviderName { get; init; }
  public required string DefinitionFingerprint { get; init; }
  public required ResourceOrigin Origin { get; init; }
  public IReadOnlyList<string> Dependencies
  {
    get => _dependencies;
    init => _dependencies = Array.AsReadOnly(
        (value ?? throw new ArgumentNullException(nameof(value))).ToArray());
  }

  public IReadOnlyList<PlanAction> AllowedActions
  {
    get => _allowedActions;
    init => _allowedActions = Array.AsReadOnly(
        (value ?? throw new ArgumentNullException(nameof(value))).ToArray());
  }

  public required PrivilegeRequirement MaximumPrivilege { get; init; }
  public required RestartPolicy MaximumRestartPolicy { get; init; }
  public required PlanRisk MaximumRisk { get; init; }
  public required bool AllowDestructive { get; init; }
}
