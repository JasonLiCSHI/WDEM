using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public sealed partial class ExecutionPlanner
{
  private const string DeferredDynamicPlanNotice =
      "This resource will be re-detected after its declared dependencies succeed; " +
      "the resulting plan must remain within this displayed authorization.";

  internal static PlannedResource CreateDeferredPlaceholder(
      PlannedResource resource,
      DeferredPlanAuthorization authorization,
      ComplianceStatus approvedCompliance)
  {
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(authorization);
    if (authorization.AllowedActions.Count != 1)
    {
      throw new ArgumentException(
          "A deferred placeholder requires exactly one authorized action.",
          nameof(authorization));
    }

    var canonicalAuthorization = authorization with
    {
      DynamicPlanNotice = DeferredDynamicPlanNotice
    };
    var expectedAction = authorization.AllowedActions[0];
    return resource with
    {
      Status = PlannedResourceStatus.Deferred,
      Risk = canonicalAuthorization.MaximumRisk,
      RequiresElevation = canonicalAuthorization.MaximumPrivilege ==
          PrivilegeRequirement.Administrator,
      IsDestructive = canonicalAuthorization.AllowDestructive,
      RestartPolicy = canonicalAuthorization.MaximumRestartPolicy,
      Reason = canonicalAuthorization.DynamicPlanNotice,
      DeferredAuthorization = canonicalAuthorization,
      BlockedBy = [],
      Diagnostics = [],
      ResourcePlan = resource.ResourcePlan with
      {
        Compliance = approvedCompliance,
        IsExecutable = false,
        Error = null,
        StructuredErrors = [],
        Steps =
        [
          new PlanStep
          {
            Id = "deferred-refinement",
            Description =
                $"Authorize deferred {expectedAction.ToString().ToLowerInvariant()} after dependency re-detection.",
            Action = expectedAction,
            PrivilegeRequirement = canonicalAuthorization.MaximumPrivilege,
            RestartPolicy = canonicalAuthorization.MaximumRestartPolicy,
            IsDestructive = canonicalAuthorization.AllowDestructive,
            Reason = canonicalAuthorization.DynamicPlanNotice
          }
        ]
      }
    };
  }

  private static DeferredPlanAuthorization CreateDeferredAuthorization(PlannedResource resource)
  {
    var action = resource.ResourcePlan.Compliance switch
    {
      ComplianceStatus.Missing when resource.Definition.Type.EndsWith(
          "settings",
          StringComparison.OrdinalIgnoreCase) => PlanAction.Configure,
      ComplianceStatus.Missing => PlanAction.Install,
      ComplianceStatus.VersionMismatch => PlanAction.Upgrade,
      _ => PlanAction.Configure
    };
    var maximumPrivilege = resource.Definition.PrivilegeRequirement;
    return CreateDeferredAuthorization(
        [action],
        maximumPrivilege,
        resource.Definition.RestartPolicy,
        maximumPrivilege == PrivilegeRequirement.Administrator
            ? PlanRisk.Elevated
            : PlanRisk.Standard,
        allowDestructive: false);
  }

  internal static DeferredPlanAuthorization CreateDeferredAuthorization(
      IReadOnlyList<PlanAction> allowedActions,
      PrivilegeRequirement maximumPrivilege,
      RestartPolicy maximumRestartPolicy,
      PlanRisk maximumRisk,
      bool allowDestructive) => new()
      {
        AllowedActions = allowedActions,
        MaximumPrivilege = maximumPrivilege,
        MaximumRestartPolicy = maximumRestartPolicy,
        MaximumRisk = maximumRisk,
        AllowDestructive = allowDestructive,
        DynamicPlanNotice = DeferredDynamicPlanNotice
      };
}
