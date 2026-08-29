using System.Security.Cryptography;
using Wdem.Core.Execution;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;

namespace Wdem.Windows.Security;

public sealed class ElevatedResourceWorker
{
  private readonly IExecutionRunStore _runStore;
  private readonly IResourceProviderRegistry _providers;
  private readonly LogRedactor _redactor;

  public ElevatedResourceWorker(
      IExecutionRunStore runStore,
      IResourceProviderRegistry providers,
      LogRedactor redactor)
  {
    _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
  }

  public async Task<ResourceApplyResult> ApplyAsync(
      ElevatedResourceRequest request,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(request);
    cancellationToken.ThrowIfCancellationRequested();
    var run = await _runStore.GetAsync(request.RunId, cancellationToken).ConfigureAwait(false);
    if (run is null || run.Mode != RunMode.Apply || run.State != ExecutionState.Running ||
        run.Plan is null || !run.Plan.IsExecutable)
    {
      return Refused(request.ResourceId, "The execution run is not approved for elevated work.");
    }

    var matches = run.Plan.Resources
        .Where(resource => string.Equals(
            resource.Definition.Id,
            request.ResourceId,
            StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (matches.Length != 1)
    {
      return Refused(request.ResourceId, "The resource is not uniquely approved by the run plan.");
    }

    var planned = matches[0];
    if (planned.Status != PlannedResourceStatus.Ready ||
        !planned.RequiresElevation ||
        !planned.ResourcePlan.IsExecutable ||
        !planned.ResourcePlan.RequiresApply ||
        !planned.ResourcePlan.Steps.Any(step =>
            step.Action != PlanAction.None &&
            step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
    {
      return Refused(request.ResourceId, "The resource has no approved administrator action.");
    }

    var recomputedFingerprint = ResourceDefinitionFingerprint.Create(planned.Definition);
    if (!FixedEquals(request.PlanFingerprint, planned.ResourcePlan.DesiredStateFingerprint) ||
        !FixedEquals(request.PlanFingerprint, recomputedFingerprint))
    {
      return Refused(request.ResourceId, "The approved resource fingerprint does not match.");
    }

    if (!string.Equals(
            planned.ResourcePlan.ResourceId,
            planned.Definition.Id,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            planned.ResourcePlan.ResourceType,
            planned.Definition.Type,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            planned.ResourcePlan.ProviderName,
            planned.Definition.Provider,
            StringComparison.OrdinalIgnoreCase) ||
        !_providers.TryGet(
            planned.Definition.Type,
            planned.Definition.Provider,
            out var provider) ||
        provider is null)
    {
      return Refused(request.ResourceId, "The approved provider identity is unavailable.");
    }

    _redactor.RegisterSensitiveParameters(planned.Definition.Parameters);
    var redactingProgress = progress is null
        ? null
        : new RedactingProgress(progress, _redactor);
    try
    {
      var result = await provider.ApplyAsync(
          planned.Definition,
          planned.ResourcePlan,
          redactingProgress,
          cancellationToken).ConfigureAwait(false);
      return Redact(result, request.ResourceId);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return new ResourceApplyResult
      {
        ResourceId = request.ResourceId,
        Outcome = ApplyOutcome.Failed,
        Error = _redactor.Redact(new StructuredError(
            WdemErrorCode.ProviderError,
            "Elevated provider execution failed.",
            "The approved provider failed while applying the elevated resource.")
        {
          ResourceId = request.ResourceId,
          UnderlyingException = exception
        })
      };
    }
  }

  private ResourceApplyResult Redact(ResourceApplyResult result, string resourceId)
  {
    if (result is null)
    {
      return new ResourceApplyResult
      {
        ResourceId = resourceId,
        Outcome = ApplyOutcome.Failed,
        Error = new StructuredError(
            WdemErrorCode.ProviderError,
            "Elevated provider returned no result.",
            "The approved provider did not return an apply result.")
        {
          ResourceId = resourceId
        }
      };
    }

    return new ResourceApplyResult
    {
      ResourceId = _redactor.Redact(resourceId),
      Outcome = result.Outcome,
      Error = result.Error is null ? null : _redactor.Redact(result.Error),
      StepResults = result.StepResults.Select(step => new ProviderStepResult
      {
        StepId = _redactor.Redact(step.StepId),
        Action = step.Action,
        Progress = step.Progress,
        ProcessExitCode = step.ProcessExitCode,
        Message = step.Message is null ? null : _redactor.Redact(step.Message),
        Error = step.Error is null ? null : _redactor.Redact(step.Error)
      }).ToArray(),
      Diagnostics = result.Diagnostics.Select(_redactor.Redact).ToArray()
    };
  }

  private static bool FixedEquals(string? left, string? right)
  {
    if (left is null || right is null || left.Length != 64 || right.Length != 64)
    {
      return false;
    }

    try
    {
      return CryptographicOperations.FixedTimeEquals(
          Convert.FromHexString(left),
          Convert.FromHexString(right));
    }
    catch (FormatException)
    {
      return false;
    }
  }

  private static ResourceApplyResult Refused(string resourceId, string detail) => new()
  {
    ResourceId = resourceId,
    Outcome = ApplyOutcome.Failed,
    Error = new StructuredError(
        WdemErrorCode.PermissionError,
        "Elevated resource request was refused.",
        detail)
    {
      ResourceId = resourceId,
      IsRetryable = false
    }
  };

  private sealed class RedactingProgress(
      IProgress<ProviderProgress> inner,
      LogRedactor redactor) : IProgress<ProviderProgress>
  {
    public void Report(ProviderProgress value) => inner.Report(new ProviderProgress(
        redactor.Redact(value.Stage),
        value.Percent,
        redactor.Redact(value.Message),
        value.StepId is null ? null : redactor.Redact(value.StepId),
        value.LogLevel));
  }
}
