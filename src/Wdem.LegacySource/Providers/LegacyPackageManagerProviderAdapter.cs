using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.LegacySource.Interfaces;
using Wdem.LegacySource.Models;

namespace Wdem.LegacySource.Providers;

public sealed class LegacyPackageManagerProviderAdapter : IResourceProvider
{
  public const string PackageIdParameter = "packageId";
  public const string SourceParameter = "source";
  public const string InstallerParametersParameter = "installerParameters";

  private readonly ICancellablePackageManager _packageManager;

  public LegacyPackageManagerProviderAdapter(
      string providerName,
      ICancellablePackageManager packageManager,
      bool supportsSource = false)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
    ArgumentNullException.ThrowIfNull(packageManager);

    ProviderName = providerName;
    _packageManager = packageManager;
    Capabilities = new ProviderCapabilities
    {
      SupportsSource = supportsSource,
      SupportsInProgressCancellation = true
    };
  }

  public string ResourceType => "package";

  public string ProviderName { get; }

  public ProviderCapabilities Capabilities { get; }

  public ValueTask<ProviderValidationResult> ValidateAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);

    var errors = new List<string>();

    if (!string.Equals(resource.Type, ResourceType, StringComparison.OrdinalIgnoreCase))
    {
      errors.Add($"Resource type '{resource.Type}' is not supported by the package provider.");
    }

    if (!string.Equals(resource.Provider, ProviderName, StringComparison.OrdinalIgnoreCase))
    {
      errors.Add($"Resource provider '{resource.Provider}' does not match '{ProviderName}'.");
    }

    if (!TryGetRequiredParameter(resource, PackageIdParameter, out _))
    {
      errors.Add($"Parameter '{PackageIdParameter}' is required.");
    }

    if (!string.IsNullOrWhiteSpace(resource.VersionConstraint) ||
        !string.IsNullOrWhiteSpace(resource.PreferredVersion))
    {
      errors.Add(
          $"Legacy package provider '{ProviderName}' cannot reliably detect or enforce package versions.");
    }

    if (!Capabilities.SupportsSource &&
        !string.IsNullOrWhiteSpace(GetOptionalParameter(resource, SourceParameter)))
    {
      errors.Add($"Legacy package provider '{ProviderName}' does not support a package source.");
    }

    if (!string.IsNullOrWhiteSpace(GetOptionalParameter(resource, InstallerParametersParameter)))
    {
      errors.Add(
          $"Legacy package provider '{ProviderName}' does not safely support custom installer parameters.");
    }

    var knownParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      PackageIdParameter,
      SourceParameter,
      InstallerParametersParameter
    };
    var unknownParameters = resource.Parameters.Keys
        .Where(parameter => !knownParameters.Contains(parameter))
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (unknownParameters.Length > 0)
    {
      errors.Add(
          $"Legacy package provider '{ProviderName}' does not support parameters: " +
          $"{string.Join(", ", unknownParameters)}.");
    }

    if (errors.Count == 0)
    {
      return ValueTask.FromResult(ProviderValidationResult.Valid);
    }

    return ValueTask.FromResult(new ProviderValidationResult
    {
      Errors = errors.ToArray(),
      StructuredErrors = errors.Select(error => new StructuredError(
          WdemErrorCode.ProviderError,
          "Legacy provider validation failed.",
          error)
      {
        ResourceId = resource.Id
      }).ToArray()
    });
  }

  public async ValueTask<DetectedState> DetectAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var validation = await ValidateAsync(resource, cancellationToken);
    if (!validation.IsValid)
    {
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Failed,
        Error = string.Join(" ", validation.Errors),
        StructuredError = validation.StructuredErrors.FirstOrDefault()
      };
    }

    try
    {
      if (!_packageManager.IsAvailable())
      {
        var error = new StructuredError(
            WdemErrorCode.DetectionError,
            "Package manager is unavailable.",
            $"Package manager '{ProviderName}' is not available.")
        {
          ResourceId = resource.Id,
          IsRetryable = true
        };
        return new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Failed,
          Error = error.Detail,
          StructuredError = error
        };
      }

      var packageId = resource.Parameters[PackageIdParameter]!;
      var installed = _packageManager.IsInstalled(packageId);

      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = installed,
        Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
          ["provider"] = ProviderName,
          [PackageIdParameter] = packageId
        }
      };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      var error = new StructuredError(
          WdemErrorCode.DetectionError,
          "Package detection failed.",
          exception.Message)
      {
        ResourceId = resource.Id,
        UnderlyingException = exception,
        IsRetryable = true
      };
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Failed,
        Error = error.Detail,
        StructuredError = error
      };
    }
  }

  public async ValueTask<ResourcePlan> PlanAsync(
      ResourceDefinition resource,
      DetectedState currentState,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(currentState);

    var validation = await ValidateAsync(resource, cancellationToken);
    if (!validation.IsValid)
    {
      return CreateBlockedPlan(
          resource,
          string.Join(" ", validation.Errors),
          validation.StructuredErrors);
    }

    if (!string.Equals(currentState.ResourceId, resource.Id, StringComparison.OrdinalIgnoreCase))
    {
      return CreateBlockedPlan(
          resource,
          $"Detected state for '{currentState.ResourceId}' cannot be used to plan resource '{resource.Id}'.");
    }

    if (currentState.Outcome != DetectionOutcome.Succeeded)
    {
      var compliance = currentState.Outcome == DetectionOutcome.Unsupported
          ? ComplianceStatus.Unsupported
          : ComplianceStatus.DetectionFailed;
      var fallbackCode = currentState.Outcome == DetectionOutcome.Cancelled
          ? WdemErrorCode.CancellationError
          : currentState.Outcome == DetectionOutcome.Unsupported
              ? WdemErrorCode.ProviderError
              : WdemErrorCode.DetectionError;
      var structuredError = currentState.StructuredError ?? new StructuredError(
          fallbackCode,
          "Package detection did not succeed.",
          currentState.Error ?? "The resource could not be detected.")
      {
        ResourceId = resource.Id
      };
      return new ResourcePlan
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = compliance,
        IsExecutable = false,
        Error = currentState.Error ?? "The resource could not be detected.",
        StructuredErrors = [structuredError]
      };
    }

    if (currentState.Exists)
    {
      return new ResourcePlan
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = ComplianceStatus.Satisfied,
        IsExecutable = true
      };
    }

    var displayName = resource.DisplayName ?? resource.Id;
    return new ResourcePlan
    {
      ResourceId = resource.Id,
      ResourceType = resource.Type,
      ProviderName = resource.Provider,
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
      Compliance = ComplianceStatus.Missing,
      IsExecutable = true,
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:install",
          Description = $"Install {displayName} with {ProviderName}.",
          Action = PlanAction.Install,
          PrivilegeRequirement = resource.PrivilegeRequirement,
          RestartPolicy = resource.RestartPolicy
        }
      ]
    };
  }

  public async ValueTask<ResourceApplyResult> ApplyAsync(
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);

    var validation = await ValidateAsync(resource, cancellationToken);
    if (!validation.IsValid)
    {
      throw new InvalidOperationException(
          $"Resource '{resource.Id}' is invalid: {string.Join(" ", validation.Errors)}");
    }

    if (!string.Equals(plan.ResourceId, resource.Id, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(plan.ResourceType, resource.Type, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(plan.ProviderName, resource.Provider, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(
            plan.DesiredStateFingerprint,
            ResourceDefinitionFingerprint.Create(resource),
            StringComparison.Ordinal))
    {
      throw new InvalidOperationException(
          $"The execution plan does not match resource '{resource.Id}' and provider '{resource.Provider}'.");
    }

    if (!plan.IsExecutable)
    {
      throw new InvalidOperationException(
          $"The execution plan for resource '{resource.Id}' is not executable: {plan.Error}");
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.NotRequired
      };
    }

    if (plan.Steps.Count != 1 ||
        plan.Steps[0].Action != PlanAction.Install ||
        !string.Equals(plan.Steps[0].Id, $"{resource.Id}:install", StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException(
          $"The execution plan for resource '{resource.Id}' does not contain the expected install step.");
    }

    var packageId = resource.Parameters[PackageIdParameter]!;
    var step = plan.Steps[0];
    var safeProgress = new SafeProgressReporter(progress, resource.Id, step.Id);
    safeProgress.Report(0, $"Installing {packageId} with {ProviderName}.");

    var package = new AppConfig
    {
      Id = packageId,
      Manager = ProviderName,
      Source = GetOptionalParameter(resource, SourceParameter),
      ResourceId = resource.Id,
      DependsOn = resource.Dependencies.ToList()
    };
    var packageProgress = new ForwardingProgress<string>(message =>
        safeProgress.Report(0.5, message));

    try
    {
      await _packageManager.InstallAsync(package, packageProgress, cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      var error = new StructuredError(
          WdemErrorCode.CancellationError,
          "Package installation was cancelled.",
          $"Installation of resource '{resource.Id}' was cancelled.")
      {
        ResourceId = resource.Id,
        StepId = step.Id
      };
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Cancelled,
        Error = error,
        Diagnostics = safeProgress.Diagnostics,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = step.Id,
            Action = step.Action,
            Progress = safeProgress.LastProgress,
            Message = "Installation was cancelled.",
            Error = error
          }
        ]
      };
    }
    catch (Exception exception)
    {
      var error = new StructuredError(
          WdemErrorCode.InstallationError,
          "Package installation failed.",
          exception.Message)
      {
        ResourceId = resource.Id,
        StepId = step.Id,
        UnderlyingException = exception,
        IsRetryable = true
      };
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Failed,
        Error = error,
        Diagnostics = safeProgress.Diagnostics,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = step.Id,
            Action = step.Action,
            Progress = safeProgress.LastProgress,
            Message = "Installation failed.",
            Error = error
          }
        ]
      };
    }

    safeProgress.Report(1, $"Installed {packageId} with {ProviderName}.");

    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Succeeded,
      Diagnostics = safeProgress.Diagnostics,
      StepResults =
      [
        new ProviderStepResult
        {
          StepId = step.Id,
          Action = step.Action,
          Progress = 1,
          Message = "Installation completed."
        }
      ]
    };
  }

  public async ValueTask<VerificationResult> VerifyAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var detectedState = await DetectAsync(resource, cancellationToken);
    var compliance = detectedState.Outcome switch
    {
      DetectionOutcome.Unsupported => ComplianceStatus.Unsupported,
      DetectionOutcome.Failed => ComplianceStatus.DetectionFailed,
      DetectionOutcome.Cancelled => ComplianceStatus.DetectionFailed,
      _ when detectedState.Exists => ComplianceStatus.Satisfied,
      _ => ComplianceStatus.Missing
    };

    return new VerificationResult
    {
      ResourceId = resource.Id,
      Compliance = compliance,
      DetectedState = detectedState,
      Message = compliance == ComplianceStatus.Satisfied
          ? null
          : $"Resource '{resource.Id}' did not reach its desired state."
    };
  }

  private static bool TryGetRequiredParameter(
      ResourceDefinition resource,
      string name,
      out string value)
  {
    if (resource.Parameters.TryGetValue(name, out var candidate) &&
        !string.IsNullOrWhiteSpace(candidate))
    {
      value = candidate;
      return true;
    }

    value = string.Empty;
    return false;
  }

  private static string? GetOptionalParameter(ResourceDefinition resource, string name) =>
      resource.Parameters.TryGetValue(name, out var value) ? value : null;

  private static ResourcePlan CreateBlockedPlan(
      ResourceDefinition resource,
      string error,
      IReadOnlyList<StructuredError>? structuredErrors = null)
  {
    var structuredError = new StructuredError(
        WdemErrorCode.ProviderError,
        "Legacy provider cannot plan this resource.",
        error)
    {
      ResourceId = resource.Id
    };
    return new ResourcePlan
    {
      ResourceId = resource.Id,
      ResourceType = resource.Type,
      ProviderName = resource.Provider,
      DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
      Compliance = ComplianceStatus.DetectionFailed,
      IsExecutable = false,
      Error = error,
      StructuredErrors = structuredErrors is { Count: > 0 }
          ? structuredErrors
          : [structuredError]
    };
  }

  private sealed class SafeProgressReporter(
      IProgress<ProviderProgress>? observer,
      string resourceId,
      string stepId)
  {
    private readonly object _gate = new();
    private readonly List<StructuredError> _diagnostics = [];
    private double _lastProgress;

    public double LastProgress
    {
      get
      {
        lock (_gate)
        {
          return _lastProgress;
        }
      }
    }

    public IReadOnlyList<StructuredError> Diagnostics
    {
      get
      {
        lock (_gate)
        {
          return Array.AsReadOnly(_diagnostics.ToArray());
        }
      }
    }

    public void Report(double percent, string? message)
    {
      var report = new ProviderProgress(
          "Apply",
          percent,
          message ?? string.Empty,
          stepId);
      lock (_gate)
      {
        _lastProgress = report.Percent;
      }

      if (observer is null)
      {
        return;
      }

      try
      {
        observer.Report(report);
      }
      catch (Exception exception)
      {
        var diagnostic = new StructuredError(
            WdemErrorCode.ProviderError,
            "Progress observer failed.",
            exception.Message)
        {
          ResourceId = resourceId,
          StepId = stepId,
          UnderlyingException = exception,
          IsRetryable = false
        };
        lock (_gate)
        {
          _diagnostics.Add(diagnostic);
        }
      }
    }
  }

  private sealed class ForwardingProgress<T>(Action<T> report) : IProgress<T>
  {
    public void Report(T value) => report(value);
  }
}
