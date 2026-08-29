using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;
using Wdem.Windows.VisualStudio;

namespace Wdem.Windows.Providers;

public sealed class ReSharperProvider : IResourceProvider
{
  public const string PackageId = "JetBrains.ReSharper";
  public const string VisualStudioResourceIdParameter = "visualStudioResourceId";
  public const string InstanceIdParameter = "instanceId";
  public const string VisualStudioInstanceIdParameter = "visualStudioInstanceId";
  public const string SourceParameter = "source";

  private readonly IVisualStudioDiscovery _discovery;
  private readonly IVsixManifestReader _manifestReader;
  private readonly WinGetCommandClient _winGet;
  private readonly IComplianceEvaluator _complianceEvaluator;

  public ReSharperProvider(
      IVisualStudioDiscovery discovery,
      IVsixManifestReader manifestReader,
      WinGetCommandClient winGet,
      IComplianceEvaluator complianceEvaluator)
  {
    _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    _manifestReader = manifestReader ?? throw new ArgumentNullException(nameof(manifestReader));
    _winGet = winGet ?? throw new ArgumentNullException(nameof(winGet));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
  }

  public string ResourceType => "resharper";
  public string ProviderName => "winget";
  public ProviderCapabilities Capabilities { get; } = new()
  {
    SupportsSource = true,
    SupportsVersionConstraints = true,
    SupportsInProgressCancellation = true,
    ConcurrencyGroup = "visual-studio-installer"
  };

  public ValueTask<ProviderValidationResult> ValidateAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    var errors = new List<(WdemErrorCode Code, string Detail)>();
    if (!Matches(resource.Type, ResourceType))
    {
      errors.Add((WdemErrorCode.ProviderError, "Resource type must be 'resharper'."));
    }

    if (!Matches(resource.Provider, ProviderName))
    {
      errors.Add((WdemErrorCode.ProviderError, "Resource provider must be 'winget'."));
    }

    var visualStudioResourceId =
        GetParameter(resource, VisualStudioResourceIdParameter) ?? "visual-studio";
    if (
        !resource.Dependencies.Contains(visualStudioResourceId, StringComparer.OrdinalIgnoreCase))
    {
      errors.Add((
          WdemErrorCode.DependencyError,
          "ReSharper requires the specified visual-studio resource in Dependencies."));
    }

    if (string.IsNullOrWhiteSpace(GetInstanceId(resource)))
    {
      errors.Add((
          WdemErrorCode.ConfigurationError,
          "Parameter 'instanceId' is required."));
    }

    var instanceId = GetParameter(resource, InstanceIdParameter);
    var legacyInstanceId = GetParameter(resource, VisualStudioInstanceIdParameter);
    if (!string.IsNullOrWhiteSpace(instanceId) &&
        !string.IsNullOrWhiteSpace(legacyInstanceId) &&
        !Matches(instanceId, legacyInstanceId))
    {
      errors.Add((
          WdemErrorCode.ConfigurationError,
          "Parameters 'instanceId' and 'visualStudioInstanceId' cannot select different instances."));
    }

    if (resource.Parameters.TryGetValue(SourceParameter, out var source) &&
        string.IsNullOrWhiteSpace(source))
    {
      errors.Add((WdemErrorCode.ConfigurationError, "Parameter 'source' cannot be empty."));
    }

    if (!string.IsNullOrWhiteSpace(resource.VersionConstraint))
    {
      try
      {
        _ = VersionConstraint.Parse(resource.VersionConstraint);
      }
      catch (FormatException)
      {
        errors.Add((WdemErrorCode.VersionError, "The ReSharper version constraint is invalid."));
      }
    }

    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      VisualStudioResourceIdParameter,
      InstanceIdParameter,
      VisualStudioInstanceIdParameter,
      SourceParameter
    };
    foreach (var parameter in resource.Parameters.Keys.Where(key => !supported.Contains(key)))
    {
      errors.Add((WdemErrorCode.ProviderError, $"Parameter '{parameter}' is not supported."));
    }

    return ValueTask.FromResult(errors.Count == 0
        ? ProviderValidationResult.Valid
        : new ProviderValidationResult
        {
          Errors = errors.Select(error => error.Detail).ToArray(),
          StructuredErrors = errors.Select(error => new StructuredError(
              error.Code,
              "ReSharper resource validation failed.",
              error.Detail)
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
    if (!validation.IsValid)
    {
      return Failure(resource, validation.StructuredErrors[0]);
    }

    VisualStudioInstance? instance;
    try
    {
      var instances = await _discovery.DiscoverAsync([], [], cancellationToken).ConfigureAwait(false);
      var requestedId = GetInstanceId(resource)!;
      var matches = instances.Where(candidate =>
          candidate.IsComplete && Matches(candidate.InstanceId, requestedId)).ToArray();
      if (matches.Length > 1)
      {
        return Failure(resource, Error(
            resource,
            WdemErrorCode.DetectionError,
            "ReSharper integration detection is ambiguous.",
            "More than one Visual Studio instance has the selected instance ID."));
      }

      instance = matches.SingleOrDefault();
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return Failure(resource, Error(
          resource,
          WdemErrorCode.DetectionError,
          "Visual Studio discovery failed.",
          "The target Visual Studio instance could not be discovered.",
          exception));
    }

    if (instance is null)
    {
      return CreateMissingState(resource);
    }

    try
    {
      var manifests = await _manifestReader.ReadInstalledAsync(instance, cancellationToken)
          .ConfigureAwait(false);
      var matches = manifests.Where(manifest =>
          Matches(manifest.Id, PackageId) &&
          Matches(manifest.VisualStudioInstanceId, instance.InstanceId)).ToArray();
      if (matches.Length == 0)
      {
        return CreateMissingState(resource, instance.InstanceId);
      }

      if (matches.Length > 1)
      {
        return Failure(resource, Error(
            resource,
            WdemErrorCode.DetectionError,
            "ReSharper integration detection is ambiguous.",
            "More than one ReSharper manifest is integrated with the target instance."));
      }

      var manifest = matches[0];
      IReadOnlyList<SemanticVersion> versions =
          SemanticVersion.TryParse(manifest.Version, out var version) ? [version] : [];
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = true,
        Version = manifest.Version,
        InstalledVersions = versions,
        Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
          ["extensionId"] = manifest.Id,
          ["version"] = manifest.Version,
          ["manifestPath"] = manifest.ManifestPath,
          ["visualStudioInstanceId"] = instance.InstanceId
        }
      };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
        InvalidDataException)
    {
      return Failure(resource, Error(
          resource,
          WdemErrorCode.DetectionError,
          "ReSharper integration detection failed.",
          "Installed ReSharper manifests could not be safely read.",
          exception));
    }
  }

  public async ValueTask<ResourcePlan> PlanAsync(
      ResourceDefinition resource,
      DetectedState currentState,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(currentState);
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return CreatePlan(resource, ComplianceStatus.DetectionFailed, false) with
      {
        Error = validation.StructuredErrors[0].Detail,
        StructuredErrors = validation.StructuredErrors
      };
    }

    var compliance = _complianceEvaluator.Evaluate(resource, currentState);
    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return CreatePlan(resource, compliance.Status, true);
    }

    if (compliance.Status is ComplianceStatus.DetectionFailed or ComplianceStatus.Unsupported)
    {
      return CreatePlan(resource, compliance.Status, false) with
      {
        Error = compliance.Error?.Detail,
        StructuredErrors = compliance.Error is null ? [] : [compliance.Error]
      };
    }

    var source = await _winGet.QueryAvailabilityAsync(
        resource.Id,
        PackageId,
        resource.PreferredVersion,
        GetParameter(resource, SourceParameter),
        cancellationToken).ConfigureAwait(false);
    if (source.Error is not null)
    {
      return CreatePlan(resource, compliance.Status, false) with
      {
        Error = source.Error.Detail,
        StructuredErrors = [source.Error]
      };
    }

    return CreatePlan(resource, compliance.Status, true) with
    {
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:install",
          Description = "Install JetBrains ReSharper with WinGet.",
          Action = compliance.Status == ComplianceStatus.Missing
              ? PlanAction.Install
              : PlanAction.Upgrade,
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
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    var invalidResource = ProviderLifecycleSupport.RejectInvalidResource(resource, validation);
    if (invalidResource is not null)
    {
      return invalidResource;
    }

    var invalidPlan = ProviderLifecycleSupport.RejectInvalidPlan(
        resource,
        plan,
        ResourceType,
        ProviderName);
    if (invalidPlan is not null)
    {
      return invalidPlan;
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult { ResourceId = resource.Id, Outcome = ApplyOutcome.NotRequired };
    }

    var step = plan.Steps[0];
    progress?.Report(new ProviderProgress("Plan", 0.25, "Confirming the ReSharper package source.", step.Id));
    var source = await _winGet.QueryAvailabilityAsync(
        resource.Id,
        PackageId,
        resource.PreferredVersion,
        GetParameter(resource, SourceParameter),
        cancellationToken).ConfigureAwait(false);
    if (source.Error is not null)
    {
      return ProviderLifecycleSupport.Failure(
          resource,
          step,
          source.Error,
          source.Process.ExitCode,
          0.25);
    }

    progress?.Report(new ProviderProgress("Apply", 0.5, "Installing ReSharper with WinGet.", step.Id));
    var command = await _winGet.InstallAsync(
        resource.Id,
        step.Id,
        PackageId,
        resource.PreferredVersion,
        GetParameter(resource, SourceParameter),
        null,
        cancellationToken).ConfigureAwait(false);
    progress?.Report(new ProviderProgress("Verify", 0.75, "Verifying ReSharper integration.", step.Id));
    var verification = await VerifyAsync(resource, cancellationToken).ConfigureAwait(false);
    return ProviderLifecycleSupport.CompleteAfterVerification(
        resource,
        step,
        command,
        verification,
        _complianceEvaluator,
        _winGet.CreateInstallationError(
            resource.Id,
            step.Id,
            PackageId,
            command.Process.ExitCode));
  }

  public async ValueTask<VerificationResult> VerifyAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var state = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    var compliance = _complianceEvaluator.Evaluate(resource, state);
    return new VerificationResult
    {
      ResourceId = resource.Id,
      Compliance = compliance.Status,
      DetectedState = state,
      Message = compliance.Status == ComplianceStatus.Satisfied ? null : compliance.Summary
    };
  }

  private static ResourcePlan CreatePlan(
      ResourceDefinition resource,
      ComplianceStatus compliance,
      bool executable) => new()
      {
        ResourceId = resource.Id,
        ResourceType = resource.Type,
        ProviderName = resource.Provider,
        DesiredStateFingerprint = ResourceDefinitionFingerprint.Create(resource),
        Compliance = compliance,
        IsExecutable = executable
      };

  private static DetectedState CreateMissingState(
      ResourceDefinition resource,
      string? instanceId = null) => new()
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false,
        Evidence = instanceId is null
        ? new Dictionary<string, string>()
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
          ["visualStudioInstanceId"] = instanceId
        }
      };

  private static DetectedState Failure(ResourceDefinition resource, StructuredError error) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Failed,
    Error = error.Detail,
    StructuredError = error.ResourceId is null ? error with { ResourceId = resource.Id } : error
  };

  private static StructuredError Error(
      ResourceDefinition resource,
      WdemErrorCode code,
      string summary,
      string detail,
      Exception? exception = null) => new(code, summary, detail)
      {
        ResourceId = resource.Id,
        UnderlyingException = exception
      };

  private static string? GetParameter(ResourceDefinition resource, string parameter) =>
      resource.Parameters.TryGetValue(parameter, out var value) ? value : null;

  private static string? GetInstanceId(ResourceDefinition resource) =>
      GetParameter(resource, InstanceIdParameter) ??
      GetParameter(resource, VisualStudioInstanceIdParameter);

  private static bool Matches(string? left, string? right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
