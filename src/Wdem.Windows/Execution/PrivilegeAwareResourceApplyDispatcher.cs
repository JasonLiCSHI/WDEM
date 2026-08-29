using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Security;

namespace Wdem.Windows.Execution;

public sealed class PrivilegeAwareResourceApplyDispatcher(
    IResourceApplyDispatcher direct,
    IPrivilegeBroker privilegeBroker) : IResourceApplyDispatcher
{
  private readonly IResourceApplyDispatcher _direct =
      direct ?? throw new ArgumentNullException(nameof(direct));
  private readonly IPrivilegeBroker _privilegeBroker =
      privilegeBroker ?? throw new ArgumentNullException(nameof(privilegeBroker));

  public Task<ResourceApplyResult> ApplyAsync(
      Guid runId,
      IResourceProvider provider,
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);
    if (!plan.Steps.Any(step =>
            step.Action != PlanAction.None &&
            step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
    {
      return _direct.ApplyAsync(
          runId,
          provider,
          resource,
          plan,
          progress,
          cancellationToken);
    }

    return _privilegeBroker.ApplyAsync(
        new ElevatedResourceRequest(
            runId,
            resource.Id,
            plan.DesiredStateFingerprint,
            string.Empty),
        progress,
        cancellationToken);
  }

  public Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken) =>
      _privilegeBroker is IPrivilegeBrokerRunLifecycle lifecycle
          ? lifecycle.CompleteRunAsync(runId, cancellationToken)
          : Task.CompletedTask;
}
