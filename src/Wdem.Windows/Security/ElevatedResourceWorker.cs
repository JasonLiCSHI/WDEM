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
  private readonly IApprovedResourceStore _approvedResources;
  private readonly IResourceProviderRegistry _providers;
  private readonly LogRedactor _redactor;

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
    ArgumentNullException.ThrowIfNull(runStore);
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

    ApprovedResourceClaim? approved;
    try
    {
      approved = await _approvedResources.ClaimApprovedResourceAsync(
          request.RunId,
          request.ResourceId,
          request.PlanFingerprint,
          cancellationToken).ConfigureAwait(false);
    }
    catch (ApprovedResourceStoreException exception)
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
            ApprovedResourceFingerprint.Create(approved.Definition, approved.Plan)))
    {
      return Refused(request.ResourceId, "The approved resource fingerprint does not match.");
    }

    if (!FixedEquals(
            request.PlanFingerprint,
            ApprovedResourceFingerprint.Create(approved.Definition, approved.Segment)))
    {
      return Refused(request.ResourceId, "The approved administrator segment does not match.");
    }

    var approvedSegment = approved.Segment;

    if (!string.Equals(
            approved.Definition.Id,
            request.ResourceId,
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
      FinalizeAfterCancellation = result.FinalizeAfterCancellation,
      RestartRequirement = result.RestartRequirement,
      Error = result.Error is null ? null : _redactor.Redact(result.Error),
      FinalVerification = result.FinalVerification is null
          ? null
          : Redact(result.FinalVerification),
      StepResults = result.StepResults.Select(step => new ProviderStepResult
      {
        StepId = _redactor.Redact(step.StepId),
        Action = step.Action,
        Progress = step.Progress,
        ProcessExitCode = step.ProcessExitCode,
        Succeeded = step.Succeeded,
        Message = step.Message is null ? null : _redactor.Redact(step.Message),
        Error = step.Error is null ? null : _redactor.Redact(step.Error)
      }).ToArray(),
      Diagnostics = result.Diagnostics.Select(_redactor.Redact).ToArray()
    };
  }

  private VerificationResult Redact(VerificationResult result) => result with
  {
    ResourceId = _redactor.Redact(result.ResourceId),
    DetectedState = Redact(result.DetectedState),
    Message = result.Message is null ? null : _redactor.Redact(result.Message)
  };

  private DetectedState Redact(DetectedState state) => state with
  {
    ResourceId = _redactor.Redact(state.ResourceId),
    Version = state.Version is null ? null : _redactor.Redact(state.Version),
    ConfigurationHash = state.ConfigurationHash is null
        ? null
        : _redactor.Redact(state.ConfigurationHash),
    Evidence = state.Evidence.ToDictionary(
        pair => _redactor.Redact(pair.Key),
        pair => _redactor.Redact(pair.Value),
        StringComparer.OrdinalIgnoreCase),
    Error = state.Error is null ? null : _redactor.Redact(state.Error),
    StructuredError = state.StructuredError is null
        ? null
        : _redactor.Redact(state.StructuredError)
  };

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
        value.LogLevel)
    {
      BeginsCancellationFinalization = value.BeginsCancellationFinalization
    });
  }
}
