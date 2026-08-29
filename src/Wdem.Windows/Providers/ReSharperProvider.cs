using System.Security.Cryptography;
using System.Text.Json;
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
  public const string ProductIdParameter = "productId";
  public const string EditionParameter = "edition";
  public const string ChannelIdParameter = "channelId";
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
      RequireSelector(resource, ProductIdParameter, errors);
      RequireSelector(resource, EditionParameter, errors);
      RequireSelector(resource, ChannelIdParameter, errors);
    }

    var instanceId = GetParameter(resource, InstanceIdParameter);
    var legacyInstanceId = GetParameter(resource, VisualStudioInstanceIdParameter);
    ValidateOptionalSelector(resource, InstanceIdParameter, errors);
    ValidateOptionalSelector(resource, VisualStudioInstanceIdParameter, errors);
    ValidateOptionalSelector(resource, ProductIdParameter, errors);
    ValidateOptionalSelector(resource, EditionParameter, errors);
    ValidateOptionalSelector(resource, ChannelIdParameter, errors);
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
      ProductIdParameter,
      EditionParameter,
      ChannelIdParameter,
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
      var selection = SelectInstance(resource, instances);
      if (selection.IsIncompatible)
      {
        return Failure(resource, Error(
            resource,
            WdemErrorCode.ConfigurationError,
            "The selected Visual Studio instance is incompatible.",
            "The instance does not match the configured product, edition, or channel."));
      }

      if (selection.IsAmbiguous)
      {
        return Failure(resource, Error(
            resource,
            WdemErrorCode.DetectionError,
            "ReSharper integration detection is ambiguous.",
            $"Set parameter 'instanceId' to one of: {string.Join(", ", selection.CandidateInstanceIds)}."));
      }

      instance = selection.Instance;
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
      var installed = await _manifestReader.ReadInstalledWithDiagnosticsAsync(
          instance,
          cancellationToken)
          .ConfigureAwait(false);
      var relevantError = installed.Errors.FirstOrDefault(error =>
          error.ClaimedIds.Any(candidate => Matches(candidate, PackageId)));
      if (relevantError is not null)
      {
        return Failure(resource, Error(
            resource,
            WdemErrorCode.DetectionError,
            "ReSharper integration manifest is invalid.",
            "An installed manifest claiming ReSharper is invalid.",
            relevantError.Error.UnderlyingException));
      }

      var matches = installed.Manifests.Where(manifest =>
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
      if (!VsixInstallationTargetCompatibility.IsCompatible(manifest.Targets, instance))
      {
        return Failure(resource, Error(
            resource,
            WdemErrorCode.DetectionError,
            "ReSharper integration target is incompatible.",
            "The installed ReSharper manifest does not target the selected Visual Studio instance."));
      }

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
          ["visualStudioInstanceId"] = instance.InstanceId,
          ["visualStudioInstallationPath"] = instance.InstallationPath,
          ["visualStudioProductPath"] = instance.ProductPath
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

    var instance = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (instance.Instance is null)
    {
      var error = instance.Error ?? Error(
          resource,
          WdemErrorCode.DependencyError,
          "Visual Studio instance is unavailable.",
          "The selected Visual Studio instance was not found.");
      return CreatePlan(resource, compliance.Status, false) with
      {
        Error = error.Detail,
        StructuredErrors = [error]
      };
    }

    var source = await _winGet.QueryVersionsAsync(
        resource.Id,
        PackageId,
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

    var exactVersion = ResolveExactVersion(resource, source.Versions);
    if (exactVersion is null)
    {
      var error = Error(
          resource,
          WdemErrorCode.VersionError,
          "No compatible ReSharper version is available.",
          $"No available version of '{PackageId}' satisfies the requested version constraint.");
      return CreatePlan(resource, compliance.Status, false) with
      {
        Error = error.Detail,
        StructuredErrors = [error]
      };
    }

    return CreatePlan(resource, compliance.Status, true) with
    {
      Steps =
      [
        new PlanStep
        {
          Id = CreateStepId(instance.Instance, exactVersion),
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
        ProviderName,
        static (_, step) => TryParseStepId(step.Id, out string _));
    if (invalidPlan is not null)
    {
      return invalidPlan;
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult { ResourceId = resource.Id, Outcome = ApplyOutcome.NotRequired };
    }

    var step = plan.Steps[0];
    _ = TryParseStepId(step.Id, out var exactVersion);
    var instance = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (instance.Instance is null ||
        !string.Equals(
            step.Id,
            CreateStepId(instance.Instance, exactVersion),
            StringComparison.Ordinal))
    {
      return ProviderLifecycleSupport.Failure(
          resource,
          step,
          instance.Error ?? Error(
              resource,
              WdemErrorCode.DependencyError,
              "Visual Studio instance changed after planning.",
              "The selected Visual Studio instance is missing or no longer matches the approved plan."),
          null,
          0);
    }

    progress?.Report(new ProviderProgress("Plan", 0.25, "Confirming the ReSharper package source.", step.Id));
    var source = await _winGet.QueryAvailabilityAsync(
        resource.Id,
        PackageId,
        exactVersion,
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
        exactVersion,
        GetParameter(resource, SourceParameter),
        null,
        cancellationToken).ConfigureAwait(false);
    progress?.Report(new ProviderProgress("Verify", 0.75, "Verifying ReSharper integration.", step.Id));
    var verification = await VerifyAsync(resource, cancellationToken).ConfigureAwait(false);
    if (command.Error is not null || !command.Process.Started || command.Process.ExitCode != 0)
    {
      return ProviderLifecycleSupport.Failure(
          resource,
          step,
          command.Error ?? _winGet.CreateInstallationError(
              resource.Id,
              step.Id,
              PackageId,
              command.Process.ExitCode),
          command.Process.ExitCode,
          0.75);
    }

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

  private async Task<InstanceSelection> SelectInstanceAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    try
    {
      var instances = await _discovery.DiscoverAsync([], [], cancellationToken).ConfigureAwait(false);
      var selection = SelectInstance(resource, instances);
      if (selection.Instance is not null)
      {
        return new InstanceSelection(selection.Instance, null);
      }

      if (selection.IsIncompatible)
      {
        return new InstanceSelection(null, Error(
            resource,
            WdemErrorCode.ConfigurationError,
            "The selected Visual Studio instance is incompatible.",
            "The instance does not match the configured product, edition, or channel."));
      }

      return !selection.IsAmbiguous
          ? new InstanceSelection(null, null)
          : new InstanceSelection(null, Error(
              resource,
              WdemErrorCode.DetectionError,
              "Visual Studio instance selection is ambiguous.",
              $"Set parameter 'instanceId' to one of: {string.Join(", ", selection.CandidateInstanceIds)}."));
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return new InstanceSelection(null, Error(
          resource,
          WdemErrorCode.DetectionError,
          "Visual Studio discovery failed.",
          "The target Visual Studio instance could not be discovered.",
          exception));
    }
  }

  private static string CreateStepId(
      VisualStudioInstance instance,
      string exactVersion)
  {
    var evidence = JsonSerializer.SerializeToUtf8Bytes(new InstanceEvidence(
        instance.InstanceId,
        instance.ProductId,
        instance.InstallationPath,
        instance.ProductPath,
        instance.InstallationVersion,
        exactVersion));
    return $"resharper:install:{exactVersion}:" +
        Convert.ToHexString(SHA256.HashData(evidence));
  }

  private static bool TryParseStepId(string stepId, out string exactVersion)
  {
    const string prefix = "resharper:install:";
    exactVersion = string.Empty;
    if (!stepId.StartsWith(prefix, StringComparison.Ordinal))
    {
      return false;
    }

    var hashSeparator = stepId.LastIndexOf(':');
    if (hashSeparator <= prefix.Length || stepId.Length != hashSeparator + 1 + 64)
    {
      return false;
    }

    var candidate = stepId[prefix.Length..hashSeparator];
    if (!SemanticVersion.TryParse(candidate, out _) ||
        !candidate.All(character => char.IsAsciiDigit(character) || character == '.') ||
        !stepId[(hashSeparator + 1)..].All(Uri.IsHexDigit))
    {
      return false;
    }

    exactVersion = candidate;
    return true;
  }

  private static string? ResolveExactVersion(
      ResourceDefinition resource,
      IReadOnlyList<string> availableVersions)
  {
    VersionConstraint? constraint = string.IsNullOrWhiteSpace(resource.VersionConstraint)
        ? null
        : VersionConstraint.Parse(resource.VersionConstraint);
    var candidates = availableVersions
        .Select(text => (Text: text, Parsed: SemanticVersion.TryParse(text, out var parsed)
            ? parsed
            : (SemanticVersion?)null))
        .Where(candidate => candidate.Parsed is { } parsed &&
            (constraint is null || constraint.IsSatisfiedBy(parsed)));
    if (!string.IsNullOrWhiteSpace(resource.PreferredVersion))
    {
      var preferred = resource.PreferredVersion.Trim();
      return candidates.Any(candidate => string.Equals(
          candidate.Text,
          preferred,
          StringComparison.Ordinal))
          ? preferred
          : null;
    }

    return candidates
        .OrderByDescending(candidate => candidate.Parsed)
        .ThenByDescending(candidate => candidate.Text, StringComparer.Ordinal)
        .Select(candidate => candidate.Text)
        .FirstOrDefault();
  }

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

  private static VisualStudioInstanceSelection SelectInstance(
      ResourceDefinition resource,
      IReadOnlyList<VisualStudioInstance> instances) => VisualStudioInstanceSelector.Select(
          instances,
          new VisualStudioInstanceCriteria(
              GetInstanceId(resource),
              GetParameter(resource, ProductIdParameter),
              GetParameter(resource, EditionParameter),
              GetParameter(resource, ChannelIdParameter)));

  private static void RequireSelector(
      ResourceDefinition resource,
      string parameter,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    var value = GetParameter(resource, parameter);
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          $"Parameter '{parameter}' is required when 'instanceId' is omitted."));
    }
  }

  private static void ValidateOptionalSelector(
      ResourceDefinition resource,
      string parameter,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    if (resource.Parameters.ContainsKey(parameter) &&
        (GetParameter(resource, parameter) is not { } value ||
         string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          $"Parameter '{parameter}' cannot be empty or contain control characters."));
    }
  }

  private static bool Matches(string? left, string? right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

  private sealed record InstanceEvidence(
      string InstanceId,
      string ProductId,
      string InstallationPath,
      string ProductPath,
      string InstallationVersion,
      string ExactVersion);

  private sealed record InstanceSelection(
      VisualStudioInstance? Instance,
      StructuredError? Error);
}
