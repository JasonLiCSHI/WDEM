using System.Security.Cryptography;
using Wdem.Core.Execution;
using Wdem.Core.Planning;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Runs;
using Wdem.Windows.Execution;

namespace Wdem.Windows.Security;

public sealed class ElevatedResourceWorker
{
  private readonly IExecutionRunStore _runStore;
  private readonly IApprovedResourceStore _approvedResources;
  private readonly IResourceProviderRegistry _providers;
  private readonly LogRedactor _redactor;
  private readonly object _claimsGate = new();
  private readonly HashSet<string> _claimedResources = new(StringComparer.OrdinalIgnoreCase);

  public ElevatedResourceWorker(
      IExecutionRunStore runStore,
      IResourceProviderRegistry providers,
      LogRedactor redactor)
      : this(
          runStore,
          runStore as IApprovedResourceStore ?? throw new ArgumentException(
              "The execution run store must provide protected approved resources.",
              nameof(runStore)),
          providers,
          redactor)
  {
  }

  public ElevatedResourceWorker(
      IExecutionRunStore runStore,
      IApprovedResourceStore approvedResources,
      IResourceProviderRegistry providers,
      LogRedactor redactor)
  {
    _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    _approvedResources = approvedResources ??
        throw new ArgumentNullException(nameof(approvedResources));
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
    if (!run.ResourceResults.TryGetValue(request.ResourceId, out var persistedResult) ||
        persistedResult.State != ExecutionState.Running ||
        persistedResult.Outcome is not null)
    {
      return Refused(request.ResourceId, "The resource is not running in the persisted run state.");
    }

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

    ApprovedResource? approved;
    try
    {
      approved = await _approvedResources.GetApprovedResourceAsync(
          request.RunId,
          request.ResourceId,
          cancellationToken).ConfigureAwait(false);
    }
    catch (ApprovedResourceAccessException exception)
    {
      return new ResourceApplyResult
      {
        ResourceId = request.ResourceId,
        Outcome = ApplyOutcome.Failed,
        Error = _redactor.Redact(exception.Error with
        {
          ResourceId = request.ResourceId
        })
      };
    }

    if (approved is null ||
        !FixedEquals(
            approved.Fingerprint,
            ApprovedResourceFingerprint.Create(approved.Definition, approved.Plan)) ||
        !FixedEquals(
            approved.Fingerprint,
            ApprovedResourceFingerprint.Create(approved.Definition, planned.ResourcePlan)))
    {
      return Refused(request.ResourceId, "The approved resource fingerprint does not match.");
    }

    var approvedSegments = PrivilegePlanSegments.Split(approved.Plan)
        .Where(segment => segment.Steps.Any(step =>
            step.Action != PlanAction.None &&
            step.PrivilegeRequirement == PrivilegeRequirement.Administrator))
        .Where(segment => FixedEquals(
            request.PlanFingerprint,
            ApprovedResourceFingerprint.Create(approved.Definition, segment)))
        .ToArray();
    if (approvedSegments.Length != 1)
    {
      return Refused(request.ResourceId, "The approved administrator segment does not match.");
    }

    var approvedSegment = approvedSegments[0];

    if (!DependenciesEqual(planned.Dependencies, approved.Definition.Dependencies))
    {
      return Refused(
          request.ResourceId,
          "The persisted resource dependencies do not match the protected approval.");
    }

    if (!approved.Definition.Dependencies.All(dependency =>
            run.ResourceResults.TryGetValue(dependency, out var dependencyResult) &&
            dependencyResult.State == ExecutionState.Completed &&
            dependencyResult.Outcome is ExecutionOutcome.Succeeded or
                ExecutionOutcome.NotRequired))
    {
      return Refused(request.ResourceId, "The resource dependencies have not succeeded.");
    }

    if (!string.Equals(
            approved.Definition.Id,
            request.ResourceId,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            planned.Definition.Id,
            approved.Definition.Id,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            planned.Definition.Type,
            approved.Definition.Type,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            planned.Definition.Provider,
            approved.Definition.Provider,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            approved.Plan.ResourceId,
            approved.Definition.Id,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            approved.Plan.ResourceType,
            approved.Definition.Type,
            StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            approved.Plan.ProviderName,
            approved.Definition.Provider,
            StringComparison.OrdinalIgnoreCase) ||
        !_providers.TryGet(
            approved.Definition.Type,
            approved.Definition.Provider,
            out var provider) ||
        provider is null)
    {
      return Refused(request.ResourceId, "The approved provider identity is unavailable.");
    }

    var claim = $"{request.RunId:N}\0{approved.Definition.Id}\0{request.PlanFingerprint}";
    lock (_claimsGate)
    {
      if (!_claimedResources.Add(claim))
      {
        return Refused(request.ResourceId, "The approved resource request has already been used.");
      }
    }

    _redactor.RegisterSensitiveParameters(approved.Definition.Parameters);
    var redactingProgress = progress is null
        ? null
        : new RedactingProgress(progress, _redactor);
    try
    {
      var result = await provider.ApplyAsync(
          approved.Definition,
          approvedSegment,
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

  private static bool DependenciesEqual(
      IReadOnlyList<string> persisted,
      IReadOnlyList<string> approved) =>
      persisted.Count == approved.Count &&
      persisted.Zip(approved).All(pair => string.Equals(
          pair.First,
          pair.Second,
          StringComparison.OrdinalIgnoreCase));

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
