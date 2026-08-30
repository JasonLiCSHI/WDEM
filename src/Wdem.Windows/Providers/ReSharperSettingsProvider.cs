using System.Security;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Configuration;
using static Wdem.Windows.Configuration.ConfigurationProviderSupport;

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
    var precondition = ConfigurationExecutionPrecondition.FromDetectedState(
        currentState,
        "destinationPath");
    if (precondition is null)
    {
      return Plan(resource, ComplianceStatus.DetectionFailed, false) with
      {
        Error = "The detected ReSharper settings destination state cannot be bound to the plan.",
        StructuredErrors =
        [
          Error(resource, WdemErrorCode.DetectionError,
              "The detected ReSharper settings destination state cannot be bound to the plan.")
        ]
      };
    }

    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return Plan(resource, compliance.Status, true) with
      {
        ExecutionPreconditionFingerprint = precondition
      };
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
      ExecutionPreconditionFingerprint = precondition,
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

    var currentState = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!ConfigurationExecutionPrecondition.Matches(plan, currentState, "destinationPath"))
    {
      return Failed(resource, Error(
          resource,
          WdemErrorCode.ConfigurationError,
          "The ReSharper settings destination changed after planning; the approved plan is stale."));
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
    var destination = ResolveDestination(Get(resource, DestinationPathParameter)!);
    var staged = await _importer.StageAsync(
        source.Source!, destination, cancellationToken).ConfigureAwait(false);
    if (!staged.Succeeded)
    {
      return Failed(resource, staged.Error! with { ResourceId = resource.Id });
    }

    try
    {
      var destinationBeforeCommit = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
      if (!ConfigurationExecutionPrecondition.Matches(
              plan,
              destinationBeforeCommit,
              "destinationPath"))
      {
        return Failed(resource, Error(
            resource,
            WdemErrorCode.ConfigurationError,
            "The ReSharper settings destination changed after planning; the approved plan is stale."));
      }

      // The directory hierarchy is leased through commit and verification. A non-cooperating
      // writer can still race the file-level comparison in the final nanoseconds before replace.
      var imported = await _importer.CommitStagedAsync(
          staged.Snapshot!, destination, cancellationToken).ConfigureAwait(false);
      if (!imported.Succeeded)
      {
        return Failed(resource, imported.Error! with { ResourceId = resource.Id });
      }

      var verification = await VerifyAsync(resource, CancellationToken.None).ConfigureAwait(false);
      if (verification.Compliance != ComplianceStatus.Satisfied)
      {
        return Failed(resource, verification.DetectedState.StructuredError ?? Error(
            resource,
            WdemErrorCode.VerificationError,
            "The imported ReSharper settings did not verify."));
      }

      return Succeeded(resource, plan.Steps[0], finalizeAfterCancellation: true);
    }
    finally
    {
      ConfigurationImporter.DeleteStagingSnapshot(staged.Snapshot!.Path);
      staged.Snapshot.Dispose();
    }
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
