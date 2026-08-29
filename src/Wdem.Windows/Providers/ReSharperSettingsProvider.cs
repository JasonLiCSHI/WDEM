using System.Security;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Configuration;

namespace Wdem.Windows.Providers;

public sealed class ReSharperSettingsProvider : IResourceProvider
{
  public const string SourcePathParameter = "sourcePath";
  public const string ExpectedSha256Parameter = "expectedSha256";
  public const string DestinationPathParameter = "destinationPath";
  public const string ReSharperResourceIdParameter = "resharperResourceId";

  private readonly ConfigurationSourceResolver _sourceResolver;
  private readonly ConfigurationImporter _importer;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly string _destinationRoot;

  public ReSharperSettingsProvider(
      ConfigurationSourceResolver sourceResolver,
      ConfigurationImporter importer,
      IComplianceEvaluator complianceEvaluator,
      string? destinationRoot = null)
  {
    _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
    _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    _destinationRoot = Path.GetFullPath(destinationRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JetBrains"));
  }

  public string ResourceType => "resharper-settings";
  public string ProviderName => "file";
  public ProviderCapabilities Capabilities { get; } = new()
  {
    SupportsSource = true,
    SupportsInProgressCancellation = true
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
      errors.Add((WdemErrorCode.ProviderError, "Resource type must be 'resharper-settings'."));
    }

    if (!Matches(resource.Provider, ProviderName))
    {
      errors.Add((WdemErrorCode.ProviderError, "Resource provider must be 'file'."));
    }

    var dependency = Get(resource, ReSharperResourceIdParameter) ?? "resharper";
    if (!resource.Dependencies.Contains(dependency, StringComparer.OrdinalIgnoreCase))
    {
      errors.Add((WdemErrorCode.DependencyError,
          "The selected ReSharper resource must be listed in Dependencies."));
    }

    ValidateFileParameter(resource, SourcePathParameter, ".DotSettings", requireAbsolute: false, errors);
    ValidateFileParameter(resource, DestinationPathParameter, ".DotSettings", requireAbsolute: false, errors);
    if (Get(resource, SourcePathParameter) is { } source &&
        ConfigurationSourceResolver.HasAlternateDataStream(source))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          "Parameter 'sourcePath' must not identify an NTFS alternate data stream."));
    }

    if (Get(resource, DestinationPathParameter) is { } destination)
    {
      if (!TryResolveDestination(destination, out var resolvedDestination))
      {
        errors.Add((WdemErrorCode.ConfigurationError,
            "Parameter 'destinationPath' must remain within the current user's ReSharper settings directory."));
      }
      else if (ConfigurationSourceResolver.HasAlternateDataStream(resolvedDestination))
      {
        errors.Add((WdemErrorCode.ConfigurationError,
            "Parameter 'destinationPath' must not identify an NTFS alternate data stream."));
      }
    }
    if (!ConfigurationSourceResolver.IsSha256(Get(resource, ExpectedSha256Parameter)))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          "Parameter 'expectedSha256' must contain exactly 64 hexadecimal characters."));
    }

    AddUnsupportedParameters(resource, errors,
        SourcePathParameter,
        ExpectedSha256Parameter,
        DestinationPathParameter,
        ReSharperResourceIdParameter);
    return ValueTask.FromResult(ToValidation(resource, "ReSharper settings", errors));
  }

  public async ValueTask<DetectedState> DetectAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return DetectionFailure(resource, validation.StructuredErrors[0]);
    }

    _ = TryResolveDestination(Get(resource, DestinationPathParameter)!, out var destination);
    if (ConfigurationImporter.ContainsReparsePoint(Path.GetDirectoryName(destination)!))
    {
      return DetectionFailure(resource, Error(
          resource,
          WdemErrorCode.ConfigurationError,
          "The ReSharper settings destination path contains an unsafe reparse point."));
    }

    if (!File.Exists(destination))
    {
      return Missing(resource, destination);
    }

    try
    {
      var attributes = File.GetAttributes(destination);
      if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
      {
        return DetectionFailure(resource, Error(
            resource,
            WdemErrorCode.ConfigurationError,
            "The ReSharper settings destination is not a regular file."));
      }

      var hash = await ConfigurationImporter.HashFileAsync(destination, cancellationToken)
          .ConfigureAwait(false);
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = true,
        ConfigurationHash = hash,
        Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
          ["sourceSha256"] = Get(resource, ExpectedSha256Parameter)!.ToUpperInvariant(),
          ["destinationPath"] = destination,
          ["destinationSha256"] = hash
        }
      };
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
    {
      return DetectionFailure(resource, Error(
          resource,
          WdemErrorCode.DetectionError,
          "The ReSharper settings destination could not be safely read.",
          exception));
    }
  }

  public async ValueTask<ResourcePlan> PlanAsync(
      ResourceDefinition resource,
      DetectedState currentState,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return Plan(resource, ComplianceStatus.DetectionFailed, false) with
      {
        Error = validation.StructuredErrors[0].Detail,
        StructuredErrors = validation.StructuredErrors
      };
    }

    var compliance = _complianceEvaluator.Evaluate(resource, currentState);
    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return Plan(resource, compliance.Status, true);
    }

    if (compliance.Status is ComplianceStatus.DetectionFailed or ComplianceStatus.Unsupported)
    {
      return Plan(resource, compliance.Status, false) with
      {
        Error = compliance.Error?.Detail,
        StructuredErrors = compliance.Error is null ? [] : [compliance.Error]
      };
    }

    return Plan(resource, compliance.Status, true) with
    {
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:configure",
          Description = "Import verified ReSharper settings.",
          Action = PlanAction.Configure,
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
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return Failed(resource, validation.StructuredErrors[0]);
    }

    var planError = ValidatePlan(resource, plan);
    if (planError is not null)
    {
      return Failed(resource, planError);
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult { ResourceId = resource.Id, Outcome = ApplyOutcome.NotRequired };
    }

    progress?.Report(new ProviderProgress("Resolve", 0.25, "Verifying the ReSharper settings source.", plan.Steps[0].Id));
    var source = await _sourceResolver.ResolveAsync(
        Get(resource, SourcePathParameter)!,
        Get(resource, ExpectedSha256Parameter)!,
        cancellationToken).ConfigureAwait(false);
    if (!source.IsValid)
    {
      return Failed(resource, source.Error! with { ResourceId = resource.Id });
    }

    progress?.Report(new ProviderProgress("Apply", 0.6, "Importing ReSharper settings.", plan.Steps[0].Id));
    var imported = await _importer.CopyAtomicallyAsync(
        source.Source!,
        ResolveDestination(Get(resource, DestinationPathParameter)!),
        cancellationToken).ConfigureAwait(false);
    if (!imported.Succeeded)
    {
      return Failed(resource, imported.Error! with { ResourceId = resource.Id });
    }

    var verification = await VerifyAsync(resource, cancellationToken).ConfigureAwait(false);
    if (verification.Compliance != ComplianceStatus.Satisfied)
    {
      return Failed(resource, verification.DetectedState.StructuredError ?? Error(
          resource,
          WdemErrorCode.VerificationError,
          "The imported ReSharper settings did not verify."));
    }

    return Succeeded(resource, plan.Steps[0]);
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

  internal static ProviderValidationResult ToValidation(
      ResourceDefinition resource,
      string displayName,
      IReadOnlyList<(WdemErrorCode Code, string Detail)> errors) => errors.Count == 0
          ? ProviderValidationResult.Valid
          : new ProviderValidationResult
          {
            Errors = errors.Select(error => error.Detail).ToArray(),
            StructuredErrors = errors.Select(error => new StructuredError(
                error.Code,
                $"{displayName} resource validation failed.",
                error.Detail)
            {
              ResourceId = resource.Id
            }).ToArray()
          };

  internal static void ValidateFileParameter(
      ResourceDefinition resource,
      string parameter,
      string extension,
      bool requireAbsolute,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    var value = Get(resource, parameter);
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
        !Path.GetExtension(value).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
        (requireAbsolute && !Path.IsPathFullyQualified(value)) ||
        (!Path.IsPathFullyQualified(value) && Uri.TryCreate(value, UriKind.Absolute, out _)))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          $"Parameter '{parameter}' must identify a{(requireAbsolute ? "n absolute" : string.Empty)} '{extension}' file path."));
    }
  }

  internal static void AddUnsupportedParameters(
      ResourceDefinition resource,
      ICollection<(WdemErrorCode Code, string Detail)> errors,
      params string[] supported)
  {
    var supportedSet = supported.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var parameter in resource.Parameters.Keys.Where(key => !supportedSet.Contains(key)))
    {
      errors.Add((WdemErrorCode.ProviderError, $"Parameter '{parameter}' is not supported."));
    }
  }

  internal static string? Get(ResourceDefinition resource, string parameter) =>
      resource.Parameters.TryGetValue(parameter, out var value) ? value : null;

  internal static bool Matches(string? left, string? right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

  internal static StructuredError Error(
      ResourceDefinition resource,
      WdemErrorCode code,
      string detail,
      Exception? exception = null) => new(code, "Configuration operation failed.", detail)
      {
        ResourceId = resource.Id,
        UnderlyingException = exception
      };

  internal static DetectedState DetectionFailure(ResourceDefinition resource, StructuredError error) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Failed,
    Error = error.Detail,
    StructuredError = error.ResourceId is null ? error with { ResourceId = resource.Id } : error
  };

  private static DetectedState Missing(ResourceDefinition resource, string destination) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Succeeded,
    Exists = false,
    Evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["destinationPath"] = destination
    }
  };

  internal static ResourcePlan Plan(
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

  internal static StructuredError? ValidatePlan(
      ResourceDefinition resource,
      ResourcePlan plan,
      Func<PlanStep, bool>? validateStep = null)
  {
    if (!plan.IsExecutable || !Matches(plan.ResourceId, resource.Id) ||
        !Matches(plan.ResourceType, resource.Type) || !Matches(plan.ProviderName, resource.Provider) ||
        !string.Equals(plan.DesiredStateFingerprint, ResourceDefinitionFingerprint.Create(resource), StringComparison.Ordinal))
    {
      return Error(resource, WdemErrorCode.ProviderError, "The approved configuration plan is invalid or stale.");
    }

    if (!plan.RequiresApply)
    {
      return plan.Compliance == ComplianceStatus.Satisfied && plan.Steps.Count == 0
          ? null
          : Error(resource, WdemErrorCode.ProviderError, "The configuration plan has no valid action.");
    }

    return plan.Steps.Count == 1 &&
        (validateStep?.Invoke(plan.Steps[0]) ?? plan.Steps[0].Id == $"{resource.Id}:configure") &&
        plan.Steps[0].Action == PlanAction.Configure &&
        plan.Steps[0].PrivilegeRequirement == resource.PrivilegeRequirement &&
        plan.Steps[0].RestartPolicy == resource.RestartPolicy && !plan.Steps[0].IsDestructive
          ? null
          : Error(resource, WdemErrorCode.ProviderError, "The configuration plan step is invalid.");
  }

  internal static ResourceApplyResult Failed(ResourceDefinition resource, StructuredError error) => new()
  {
    ResourceId = resource.Id,
    Outcome = ApplyOutcome.Failed,
    Error = error,
    Diagnostics = [error]
  };

  internal static ResourceApplyResult Succeeded(ResourceDefinition resource, PlanStep step) => new()
  {
    ResourceId = resource.Id,
    Outcome = ApplyOutcome.Succeeded,
    StepResults =
    [
      new ProviderStepResult
      {
        StepId = step.Id,
        Action = step.Action,
        Progress = 1,
        Succeeded = true
      }
    ]
  };

  private bool TryResolveDestination(string destination, out string resolved)
  {
    try
    {
      resolved = ResolveDestination(destination);
      return ConfigurationSourceResolver.IsWithin(resolved, _destinationRoot);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      resolved = string.Empty;
      return false;
    }
  }

  private string ResolveDestination(string destination) => Path.GetFullPath(
      Path.IsPathFullyQualified(destination)
          ? destination
          : Path.Combine(_destinationRoot, destination));
}
