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
      CancellationToken cancellationToken) => ApplyAsync(
          runId,
          provider,
          resource,
          plan,
          progress,
          cancellationToken,
          null);

  public async Task<ResourceApplyResult> ApplyAsync(
      Guid runId,
      IResourceProvider provider,
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken,
      CancellationDrainDeadline? cancellationDeadline)
  {
    ArgumentNullException.ThrowIfNull(provider);
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);
    if (!plan.Steps.Any(step =>
            step.Action != PlanAction.None &&
            step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
    {
      return await _direct.ApplyAsync(
          runId,
          provider,
          resource,
          plan,
          progress,
          cancellationToken,
          cancellationDeadline).ConfigureAwait(false);
    }

    var segments = PrivilegePlanSegments.Split(plan);
    if (segments.Count == 1)
    {
      return await ApplyAdministratorAsync(
          runId,
          resource,
          plan,
          progress,
          cancellationToken,
          cancellationDeadline).ConfigureAwait(false);
    }

    var stepResults = new List<ProviderStepResult>();
    var diagnostics = new List<StructuredError>();
    RestartPolicy? restartRequirement = null;
    var finalizeAfterCancellation = false;
    foreach (var segment in segments)
    {
      cancellationToken.ThrowIfCancellationRequested();
      if (!segment.RequiresApply)
      {
        continue;
      }

      var administrator = segment.Steps.Any(step =>
          step.Action != PlanAction.None &&
          step.PrivilegeRequirement == PrivilegeRequirement.Administrator);
      var result = administrator
          ? await ApplyAdministratorAsync(
              runId,
              resource,
              segment,
              progress,
              cancellationToken,
              cancellationDeadline).ConfigureAwait(false)
          : await _direct.ApplyAsync(
              runId,
              provider,
              resource,
              segment,
              progress,
              cancellationToken,
              cancellationDeadline).ConfigureAwait(false);
      stepResults.AddRange(result.StepResults);
      diagnostics.AddRange(result.Diagnostics);
      finalizeAfterCancellation |= result.FinalizeAfterCancellation;
      if (result.RestartRequirement is { } actualRestart)
      {
        restartRequirement = restartRequirement is { } accumulatedRestart
            ? (RestartPolicy)Math.Max((int)accumulatedRestart, (int)actualRestart)
            : actualRestart;
      }
      if (result.Outcome != ApplyOutcome.Succeeded)
      {
        return result with
        {
          RestartRequirement = restartRequirement,
          FinalizeAfterCancellation = finalizeAfterCancellation,
          StepResults = stepResults,
          Diagnostics = diagnostics
        };
      }
    }

    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Succeeded,
      FinalizeAfterCancellation = finalizeAfterCancellation,
      RestartRequirement = restartRequirement,
      StepResults = stepResults,
      Diagnostics = diagnostics
    };
  }

  public Task CompleteRunAsync(Guid runId, CancellationToken cancellationToken) =>
      _privilegeBroker is IPrivilegeBrokerRunLifecycle lifecycle
          ? lifecycle.CompleteRunAsync(runId, cancellationToken)
          : Task.CompletedTask;

  private Task<ResourceApplyResult> ApplyAdministratorAsync(
      Guid runId,
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken,
      CancellationDrainDeadline? cancellationDeadline) => _privilegeBroker.ApplyAsync(
          new ElevatedResourceRequest(
              runId,
              resource.Id,
              ApprovedResourceFingerprint.Create(resource, plan)),
          progress,
          cancellationToken,
          cancellationDeadline);
}
