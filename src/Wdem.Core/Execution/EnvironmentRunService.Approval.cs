using Wdem.Core.Graph;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

internal sealed record ResourceApprovalBoundary
{
  private ResourceApprovalBoundary(
      string resourceType,
      string providerName,
      string definitionFingerprint,
      ResourceOrigin origin,
      IEnumerable<string> dependencies,
      IEnumerable<PlanAction> allowedActions,
      PrivilegeRequirement maximumPrivilege,
      RestartPolicy maximumRestartPolicy,
      PlanRisk maximumRisk,
      bool allowDestructive,
      bool allowDeferredStatus)
  {
    ResourceType = resourceType;
    ProviderName = providerName;
    DefinitionFingerprint = definitionFingerprint;
    Origin = origin;
    Dependencies = Array.AsReadOnly(dependencies.ToArray());
    AllowedActions = Array.AsReadOnly(allowedActions.ToArray());
    MaximumPrivilege = maximumPrivilege;
    MaximumRestartPolicy = maximumRestartPolicy;
    MaximumRisk = maximumRisk;
    AllowDestructive = allowDestructive;
    AllowDeferredStatus = allowDeferredStatus;
  }

  public string ResourceType { get; }
  public string ProviderName { get; }
  public string DefinitionFingerprint { get; }
  public ResourceOrigin Origin { get; }
  public IReadOnlyList<string> Dependencies { get; }
  public IReadOnlyList<PlanAction> AllowedActions { get; }
  public PrivilegeRequirement MaximumPrivilege { get; }
  public RestartPolicy MaximumRestartPolicy { get; }
  public PlanRisk MaximumRisk { get; }
  public bool AllowDestructive { get; }
  public bool AllowDeferredStatus { get; }

  public DeferredPlanAuthorization CreateDeferredAuthorization() =>
      ExecutionPlanner.CreateDeferredAuthorization(
          AllowedActions,
          MaximumPrivilege,
          MaximumRestartPolicy,
          MaximumRisk,
          AllowDestructive);

  public static ResourceApprovalBoundary From(DeferredAuthorizationProof proof) => new(
      proof.ResourceType,
      proof.ProviderName,
      proof.DefinitionFingerprint,
      proof.Origin,
      proof.Dependencies,
      proof.AllowedActions,
      proof.MaximumPrivilege,
      proof.MaximumRestartPolicy,
      proof.MaximumRisk,
      proof.AllowDestructive,
      allowDeferredStatus: true);

  public static ResourceApprovalBoundary From(PlannedResource resource) => new(
      resource.Definition.Type,
      resource.Definition.Provider,
      resource.ResourcePlan.DesiredStateFingerprint,
      resource.Origin,
      resource.Dependencies,
      resource.ResourcePlan.Steps
          .Where(step => step.Action != PlanAction.None)
          .Select(step => step.Action)
          .Distinct(),
      resource.RequiresElevation
          ? PrivilegeRequirement.Administrator
          : PrivilegeRequirement.CurrentUser,
      resource.RestartPolicy,
      resource.Risk,
      resource.IsDestructive,
      allowDeferredStatus: false);
}
