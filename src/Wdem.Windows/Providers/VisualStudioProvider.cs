using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;
using Wdem.Windows.VisualStudio;

namespace Wdem.Windows.Providers;

public sealed class VisualStudioProvider : IResourceProvider
{
  private readonly IVisualStudioDiscovery _discovery;
  private readonly IComplianceEvaluator _complianceEvaluator;

  public VisualStudioProvider(
      IVisualStudioDiscovery discovery,
      IComplianceEvaluator complianceEvaluator)
  {
    _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
  }

  public string ResourceType => "visual-studio";
  public string ProviderName => "visual-studio";
  public ProviderCapabilities Capabilities { get; } = new()
  {
    SupportsVersionConstraints = true,
    SupportsInProgressCancellation = true
  };

  public ValueTask<ProviderValidationResult> ValidateAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    var errors = new List<string>();
    if (!string.Equals(resource.Type, ResourceType, StringComparison.OrdinalIgnoreCase))
    {
      errors.Add("Resource type must be 'visual-studio'.");
    }

    if (!string.Equals(resource.Provider, ProviderName, StringComparison.OrdinalIgnoreCase))
    {
      errors.Add("Resource provider must be 'visual-studio'.");
    }

    if (!VisualStudioResourceOptions.TryParse(resource, out _, out var optionErrors))
    {
      errors.AddRange(optionErrors);
    }

    if (!string.IsNullOrWhiteSpace(resource.VersionConstraint))
    {
      try
      {
        _ = VersionConstraint.Parse(resource.VersionConstraint);
      }
      catch (FormatException)
      {
        errors.Add($"Version constraint '{resource.VersionConstraint}' is invalid.");
      }
    }

    return ValueTask.FromResult(errors.Count == 0
        ? ProviderValidationResult.Valid
        : new ProviderValidationResult
        {
          Errors = errors,
          StructuredErrors = errors.Select(detail => new StructuredError(
              WdemErrorCode.ProviderError,
              "Visual Studio resource validation failed.",
              detail)
          {
            ResourceId = resource.Id
          }).ToArray()
        });
  }

  public async ValueTask<DetectedState> DetectAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resource);
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid ||
        !VisualStudioResourceOptions.TryParse(resource, out var options, out _))
    {
      var error = validation.StructuredErrors.First();
      return Failure(resource, error);
    }

    IReadOnlyList<VisualStudioInstance> instances;
    try
    {
      instances = await _discovery.DiscoverAsync(
          options!.Workloads,
          options.Components,
          cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      var error = new StructuredError(
          WdemErrorCode.DetectionError,
          "Visual Studio detection failed.",
          $"vswhere discovery failed: {exception.Message}")
      {
        ResourceId = resource.Id,
        UnderlyingException = exception
      };
      return Failure(resource, error);
    }

    var candidates = instances
        .Where(instance => string.Equals(
            instance.ProductId,
            options.ProductId,
            StringComparison.OrdinalIgnoreCase))
        .Where(instance => string.Equals(
            instance.Edition,
            options.Edition,
            StringComparison.OrdinalIgnoreCase))
        .Where(instance => string.Equals(
            instance.ChannelId,
            options.ChannelId,
            StringComparison.OrdinalIgnoreCase))
        .Where(instance => MatchesVersion(instance, resource.VersionConstraint))
        .ToArray();
    if (candidates.Length > 1)
    {
      var candidateIds = candidates
          .Select(instance => instance.InstanceId)
          .Order(StringComparer.OrdinalIgnoreCase)
          .ToArray();
      var selectedCandidates = options.InstanceId is null
          ? []
          : candidates.Where(instance => string.Equals(
              instance.InstanceId,
              options.InstanceId,
              StringComparison.OrdinalIgnoreCase)).ToArray();
      if (selectedCandidates.Length != 1)
      {
        var error = new StructuredError(
            WdemErrorCode.DetectionError,
            "Multiple Visual Studio instances match.",
            $"Set parameter 'instanceId' to one of: {string.Join(", ", candidateIds)}.")
        {
          ResourceId = resource.Id
        };
        return new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Failed,
          Error = error.Detail,
          StructuredError = error,
          Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
          {
            ["candidateInstanceIds"] = string.Join(';', candidateIds)
          }
        };
      }

      candidates = selectedCandidates;
    }
    else if (options.InstanceId is not null)
    {
      candidates = candidates.Where(instance => string.Equals(
          instance.InstanceId,
          options.InstanceId,
          StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    if (candidates.Length == 0)
    {
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false
      };
    }

    var selected = candidates[0];
    var installedVersions = SemanticVersion.TryParse(
        selected.ProductDisplayVersion,
        out var displayVersion)
        ? new[] { displayVersion }
        : SemanticVersion.TryParse(selected.InstallationVersion, out var installationVersion)
            ? [installationVersion]
            : [];
    return new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = selected.ProductDisplayVersion,
      InstalledVersions = installedVersions,
      Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["instanceId"] = selected.InstanceId,
        ["installationPath"] = selected.InstallationPath,
        ["productId"] = selected.ProductId,
        ["productPath"] = selected.ProductPath,
        ["productDisplayVersion"] = selected.ProductDisplayVersion,
        ["installationVersion"] = selected.InstallationVersion,
        ["edition"] = selected.Edition,
        ["channel"] = selected.ChannelId,
        ["isComplete"] = selected.IsComplete.ToString().ToLowerInvariant(),
        ["isLaunchable"] = selected.IsLaunchable.ToString().ToLowerInvariant(),
        ["workloads"] = JoinIds(selected.Workloads),
        ["components"] = JoinIds(selected.Components)
      }
    };
  }

  public async ValueTask<ResourcePlan> PlanAsync(
      ResourceDefinition resource,
      DetectedState currentState,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(currentState);
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return BasePlan(resource, ComplianceStatus.DetectionFailed, isExecutable: false) with
      {
        Error = validation.StructuredErrors[0].Detail,
        StructuredErrors = validation.StructuredErrors
      };
    }

    var compliance = _complianceEvaluator.Evaluate(resource, currentState);
    if (compliance.Status is ComplianceStatus.DetectionFailed or ComplianceStatus.Unsupported)
    {
      return BasePlan(resource, compliance.Status, isExecutable: false) with
      {
        Error = compliance.Error?.Detail,
        StructuredErrors = compliance.Error is null ? [] : [compliance.Error]
      };
    }

    var plan = BasePlan(resource, compliance.Status, isExecutable: true);
    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return plan;
    }

    var action = compliance.Status switch
    {
      ComplianceStatus.Missing => PlanAction.Install,
      ComplianceStatus.VersionMismatch => PlanAction.Upgrade,
      _ => PlanAction.Configure
    };
    return plan with
    {
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:{action.ToString().ToLowerInvariant()}",
          Description = $"{action} Visual Studio.",
          Action = action,
          PrivilegeRequirement = resource.PrivilegeRequirement,
          RestartPolicy = resource.RestartPolicy
        }
      ]
    };
  }

  public ValueTask<ResourceApplyResult> ApplyAsync(
      ResourceDefinition resource,
      ResourcePlan plan,
      IProgress<ProviderProgress>? progress,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);
    return ValueTask.FromResult(new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Failed,
      Error = new StructuredError(
          WdemErrorCode.ProviderError,
          "Visual Studio changes are not available.",
          "Visual Studio installation and modification are not implemented by this provider.")
      {
        ResourceId = resource.Id
      }
    });
  }

  public async ValueTask<VerificationResult> VerifyAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var detected = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    var compliance = _complianceEvaluator.Evaluate(resource, detected);
    return new VerificationResult
    {
      ResourceId = resource.Id,
      Compliance = compliance.Status,
      DetectedState = detected,
      Message = compliance.Status == ComplianceStatus.Satisfied ? null : compliance.Summary
    };
  }

  private static bool MatchesVersion(
      VisualStudioInstance instance,
      string? versionConstraint)
  {
    if (string.IsNullOrWhiteSpace(versionConstraint))
    {
      return true;
    }

    var constraint = VersionConstraint.Parse(versionConstraint);
    return (SemanticVersion.TryParse(instance.ProductDisplayVersion, out var displayVersion) &&
            constraint.IsSatisfiedBy(displayVersion)) ||
        (SemanticVersion.TryParse(instance.InstallationVersion, out var installationVersion) &&
         constraint.IsSatisfiedBy(installationVersion));
  }

  private static string JoinIds(IEnumerable<string> ids) => string.Join(
      ';',
      ids.Order(StringComparer.OrdinalIgnoreCase));

  private static ResourcePlan BasePlan(
      ResourceDefinition resource,
      ComplianceStatus compliance,
      bool isExecutable) => new()
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = compliance,
        IsExecutable = isExecutable
      };

  private static DetectedState Failure(
      ResourceDefinition resource,
      StructuredError error) => new()
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Failed,
        Error = error.Detail,
        StructuredError = error
      };
}
