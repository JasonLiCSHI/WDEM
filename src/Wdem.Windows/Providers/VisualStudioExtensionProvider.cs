using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;
using Wdem.Windows.Security;
using Wdem.Windows.VisualStudio;

namespace Wdem.Windows.Providers;

public sealed class VisualStudioExtensionProvider : IResourceProvider
{
  public const string ExtensionIdParameter = "extensionId";
  public const string SourcePathParameter = "sourcePath";
  public const string ExpectedSha256Parameter = "expectedSha256";
  public const string VisualStudioResourceIdParameter = "visualStudioResourceId";
  public const string InstanceIdParameter = "instanceId";
  public const string VisualStudioInstanceIdParameter = "visualStudioInstanceId";

  private const long MaxVsixBytes = 512L * 1024 * 1024;
  private readonly IVisualStudioDiscovery _discovery;
  private readonly IVsixManifestReader _manifestReader;
  private readonly IProcessExecutor _processExecutor;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly IVsixPlanArtifactStore _planArtifactStore;
  private readonly HttpClient _httpClient;
  private readonly ITrustedFileVerifier _trustedFileVerifier;

  public VisualStudioExtensionProvider(
      IVisualStudioDiscovery discovery,
      IVsixManifestReader manifestReader,
      IProcessExecutor processExecutor,
      IComplianceEvaluator complianceEvaluator,
      ISecureArtifactStager? artifactStager = null,
      HttpClient? httpClient = null,
      ITrustedFileVerifier? trustedFileVerifier = null)
      : this(
          discovery,
          manifestReader,
          processExecutor,
          complianceEvaluator,
          artifactStager,
          httpClient,
          trustedFileVerifier,
          planArtifactStore: null)
  {
  }

  internal VisualStudioExtensionProvider(
      IVisualStudioDiscovery discovery,
      IVsixManifestReader manifestReader,
      IProcessExecutor processExecutor,
      IComplianceEvaluator complianceEvaluator,
      ISecureArtifactStager? artifactStager,
      HttpClient? httpClient,
      ITrustedFileVerifier? trustedFileVerifier,
      IVsixPlanArtifactStore? planArtifactStore)
  {
    _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    _manifestReader = manifestReader ?? throw new ArgumentNullException(nameof(manifestReader));
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    _httpClient = httpClient ?? new HttpClient();
    _trustedFileVerifier = trustedFileVerifier ?? new TrustedFileVerifier();
    var stager = artifactStager ?? new SecureArtifactStager(
        new WindowsPlanArtifactDirectoryPolicy(),
        _trustedFileVerifier);
    _planArtifactStore = planArtifactStore ?? new VsixPlanArtifactStore(
        stager,
        _trustedFileVerifier,
        _manifestReader);
  }

  public string ResourceType => "visual-studio-extension";
  public string ProviderName => "vsix";
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
      errors.Add((WdemErrorCode.ProviderError, "Resource type must be 'visual-studio-extension'."));
    }

    if (!Matches(resource.Provider, ProviderName))
    {
      errors.Add((WdemErrorCode.ProviderError, "Resource provider must be 'vsix'."));
    }

    RequireId(resource, ExtensionIdParameter, errors);
    if (string.IsNullOrWhiteSpace(GetInstanceId(resource)))
    {
      errors.Add((WdemErrorCode.ConfigurationError, "Parameter 'instanceId' is required."));
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

    var visualStudioResourceId =
        GetParameter(resource, VisualStudioResourceIdParameter) ?? "visual-studio";
    if (
        !resource.Dependencies.Contains(visualStudioResourceId, StringComparer.OrdinalIgnoreCase))
    {
      errors.Add((
          WdemErrorCode.DependencyError,
          "The specified visual-studio resource must be listed in Dependencies."));
    }

    var source = GetParameter(resource, SourcePathParameter);
    if (!IsValidSource(source))
    {
      errors.Add((
          WdemErrorCode.ConfigurationError,
          "Parameter 'sourcePath' must be an absolute local path or a safe HTTPS URI."));
    }

    var expectedHash = GetParameter(resource, ExpectedSha256Parameter);
    if (!IsSha256(expectedHash))
    {
      errors.Add((
          WdemErrorCode.ConfigurationError,
          "Parameter 'expectedSha256' must contain exactly 64 hexadecimal characters."));
    }

    if (resource.PrivilegeRequirement != PrivilegeRequirement.Administrator)
    {
      errors.Add((
          WdemErrorCode.PermissionError,
          "VSIX installation must declare Administrator privilege."));
    }

    ValidateVersion(resource, errors);
    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      ExtensionIdParameter,
      SourcePathParameter,
      ExpectedSha256Parameter,
      VisualStudioResourceIdParameter,
      InstanceIdParameter,
      VisualStudioInstanceIdParameter
    };
    foreach (var parameter in resource.Parameters.Keys.Where(key => !supported.Contains(key)))
    {
      errors.Add((WdemErrorCode.ProviderError, $"Parameter '{parameter}' is not supported."));
    }

    return ValueTask.FromResult(Validation(resource, errors));
  }

  public async ValueTask<DetectedState> DetectAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resource);
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return DetectionFailure(resource, validation.StructuredErrors[0]);
    }

    var instance = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (instance.Error is not null)
    {
      return DetectionFailure(resource, instance.Error);
    }

    if (instance.Instance is null)
    {
      return CreateMissingState(resource);
    }

    try
    {
      var installed = await _manifestReader.ReadInstalledWithDiagnosticsAsync(
          instance.Instance,
          cancellationToken).ConfigureAwait(false);
      var requestedExtensionId = GetParameter(resource, ExtensionIdParameter)!;
      var relevantError = installed.Errors.FirstOrDefault(error =>
          error.ClaimedIds.Any(candidate => Matches(candidate, requestedExtensionId)));
      if (relevantError is not null)
      {
        return DetectionFailure(resource, new StructuredError(
            WdemErrorCode.DetectionError,
            "Visual Studio extension manifest is invalid.",
            "An installed manifest claiming the requested extension identity is invalid.")
        {
          ResourceId = resource.Id,
          UnderlyingException = relevantError.Error.UnderlyingException
        });
      }

      var matches = installed.Manifests.Where(manifest =>
          Matches(manifest.Id, requestedExtensionId) &&
          Matches(manifest.VisualStudioInstanceId, instance.Instance.InstanceId)).ToArray();
      if (matches.Length == 0)
      {
        return CreateMissingState(resource, instance.Instance.InstanceId);
      }

      if (matches.Length > 1)
      {
        return DetectionFailure(resource, new StructuredError(
            WdemErrorCode.DetectionError,
            "Visual Studio extension detection is ambiguous.",
            "More than one installed manifest has the requested extension identity.")
        {
          ResourceId = resource.Id
        });
      }

      if (!VsixInstallationTargetCompatibility.IsCompatible(matches[0].Targets, instance.Instance))
      {
        return DetectionFailure(resource, new StructuredError(
            WdemErrorCode.DetectionError,
            "Visual Studio extension target is incompatible.",
            "The installed VSIX manifest does not target the selected Visual Studio instance.")
        {
          ResourceId = resource.Id
        });
      }

      return CreateDetectedState(resource, matches[0]);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
        InvalidDataException)
    {
      return DetectionFailure(resource, new StructuredError(
          WdemErrorCode.DetectionError,
          "Visual Studio extension detection failed.",
          "Installed VSIX manifests could not be safely enumerated.")
      {
        ResourceId = resource.Id,
        UnderlyingException = exception
      });
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

    var compliance = Evaluate(resource, currentState);
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

    var prepared = await PreparePlanArtifactAsync(resource, cancellationToken).ConfigureAwait(false);
    if (prepared.Error is not null || prepared.StepEvidence is null)
    {
      return CreatePlan(resource, compliance.Status, false) with
      {
        Error = prepared.Error?.Detail,
        StructuredErrors = prepared.Error is null ? [] : [prepared.Error]
      };
    }

    return CreatePlan(resource, compliance.Status, true) with
    {
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:install:{prepared.StepEvidence}",
          Description = $"Install {resource.DisplayName ?? resource.Id} with VSIXInstaller.",
          Action = compliance.Status == ComplianceStatus.Missing
              ? PlanAction.Install
              : PlanAction.Upgrade,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
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
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);
    var artifactsToAbandon = GetPlanArtifactsToAbandon(resource, plan);
    ClaimedVsixPlanArtifact? approvedArtifact = null;
    try
    {
      cancellationToken.ThrowIfCancellationRequested();
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
          static (definition, step) => VsixPlanArtifactStore.HasValidStepEvidence(
              definition.Id,
              step.Id));
      if (invalidPlan is not null)
      {
        return invalidPlan;
      }

      if (!plan.RequiresApply)
      {
        return new ResourceApplyResult { ResourceId = resource.Id, Outcome = ApplyOutcome.NotRequired };
      }

      var step = plan.Steps[0];
      var instance = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
      if (instance.Instance is null)
      {
        return ProviderLifecycleSupport.Failure(
            resource,
            step,
            instance.Error ?? new StructuredError(
                WdemErrorCode.DependencyError,
                "Visual Studio instance is unavailable.",
                "The selected Visual Studio instance was not found."),
            null,
            0);
      }

      progress?.Report(new ProviderProgress("Apply", 0.25, "Validating the approved VSIX artifact.", step.Id));
      var claim = await _planArtifactStore.ClaimAsync(
          resource.Id,
          step.Id,
          GetParameter(resource, ExpectedSha256Parameter)!,
          instance.Instance.InstanceId,
          cancellationToken).ConfigureAwait(false);
      approvedArtifact = claim.Artifact;
      if (approvedArtifact is null)
      {
        return ProviderLifecycleSupport.Failure(
            resource,
            step,
            (claim.Error ?? ConfigurationError(resource, "The approved VSIX artifact is invalid.")) with
            {
              ResourceId = resource.Id
            },
            null,
            0.25);
      }

      if (!SourceManifestMatches(resource, approvedArtifact.Manifest, instance.Instance))
      {
        return ProviderLifecycleSupport.Failure(
            resource,
            step,
            new StructuredError(
                WdemErrorCode.VersionError,
                "VSIX source does not match the requested extension.",
                "The verified VSIX manifest identity or version does not match the resource.")
            {
              ResourceId = resource.Id,
              StepId = step.Id
            },
            null,
            0.25);
      }

      var installerPath = Path.GetFullPath(Path.Combine(
          instance.Instance.InstallationPath,
          "Common7",
          "IDE",
          "VSIXInstaller.exe"));
      progress?.Report(new ProviderProgress("Apply", 0.5, "Installing the verified VSIX artifact.", step.Id));
      var process = await _processExecutor.ExecuteAsync(
          new ProcessExecutionRequest(
              installerPath,
              ["/quiet", "/admin", approvedArtifact.Path]),
          null,
          cancellationToken).ConfigureAwait(false);
      progress?.Report(new ProviderProgress("Verify", 0.75, "Verifying the installed VSIX manifest.", step.Id));
      var verification = await VerifyAsync(resource, cancellationToken).ConfigureAwait(false);
      var processError = ProcessError(resource, step, process);
      if (processError is not null)
      {
        return ProviderLifecycleSupport.Failure(
            resource,
            step,
            processError,
            process.ExitCode,
            0.75);
      }

      if (verification.Compliance == ComplianceStatus.Satisfied)
      {
        return new ResourceApplyResult
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
              ProcessExitCode = process.ExitCode
            }
          ]
        };
      }

      var error = Evaluate(resource, verification.DetectedState).Error ??
          new StructuredError(
              WdemErrorCode.VerificationError,
              "VSIX installation verification failed.",
              "The extension did not reach the requested state.")
          {
            ResourceId = resource.Id,
            StepId = step.Id
          };
      return ProviderLifecycleSupport.Failure(resource, step, error, process.ExitCode, 0.75);
    }
    finally
    {
      if (approvedArtifact is not null)
      {
        await approvedArtifact.DisposeAsync().ConfigureAwait(false);
      }
      else
      {
        foreach (var artifact in artifactsToAbandon)
        {
          await _planArtifactStore.AbandonAsync(
              artifact.ResourceId,
              artifact.StepId,
              CancellationToken.None).ConfigureAwait(false);
        }
      }
    }
  }

  public async ValueTask<VerificationResult> VerifyAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var state = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    var compliance = Evaluate(resource, state);
    return new VerificationResult
    {
      ResourceId = resource.Id,
      Compliance = compliance.Status,
      DetectedState = state,
      Message = compliance.Status == ComplianceStatus.Satisfied ? null : compliance.Summary
    };
  }

  private async Task<InstanceSelection> SelectInstanceAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    try
    {
      var instances = await _discovery.DiscoverAsync([], [], cancellationToken).ConfigureAwait(false);
      var requestedId = GetInstanceId(resource)!;
      var matches = instances.Where(instance =>
          instance.IsComplete &&
          Matches(instance.InstanceId, requestedId)).ToArray();
      if (matches.Length == 1)
      {
        return new InstanceSelection(matches[0], null);
      }

      return matches.Length == 0
          ? new InstanceSelection(null, null)
          : new InstanceSelection(null, new StructuredError(
              WdemErrorCode.DetectionError,
              "Visual Studio instance selection is ambiguous.",
              "More than one Visual Studio instance has the selected instance ID.")
          {
            ResourceId = resource.Id
          });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return new InstanceSelection(null, new StructuredError(
          WdemErrorCode.DetectionError,
          "Visual Studio discovery failed.",
          "The installed Visual Studio instances could not be discovered.")
      {
        ResourceId = resource.Id,
        UnderlyingException = exception
      });
    }
  }

  private async Task<AcquiredSource> AcquireSourceAsync(
      string source,
      CancellationToken cancellationToken)
  {
    if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile)
    {
      try
      {
        var localPath = Path.GetFullPath(source);
        return File.Exists(localPath)
            ? new AcquiredSource(localPath, false, null)
            : new AcquiredSource(null, false, new StructuredError(
                WdemErrorCode.DownloadError,
                "VSIX source is unavailable.",
                "The configured local VSIX source does not exist."));
      }
      catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
      {
        return new AcquiredSource(null, false, ConfigurationError(null, "The VSIX source path is invalid.", exception));
      }
    }

    var directory = Path.Combine(Path.GetTempPath(), "wdem", "vsix", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "extension.vsix");
    try
    {
      Directory.CreateDirectory(directory);
      using var response = await _httpClient.GetAsync(
          uri,
          HttpCompletionOption.ResponseHeadersRead,
          cancellationToken).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      if (response.RequestMessage?.RequestUri is not { } finalUri ||
          !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
          !string.IsNullOrEmpty(finalUri.UserInfo) ||
          finalUri.OriginalString.Any(char.IsControl))
      {
        throw new HttpRequestException("The VSIX download redirected to an unsafe URI.");
      }
      if (response.Content.Headers.ContentLength is > MaxVsixBytes)
      {
        throw new InvalidDataException("The VSIX artifact is too large.");
      }

      await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken)
          .ConfigureAwait(false);
      await using var destination = new FileStream(
          path,
          FileMode.CreateNew,
          FileAccess.Write,
          FileShare.None,
          81920,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
      var buffer = new byte[81920];
      long copied = 0;
      while (true)
      {
        var count = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
          break;
        }

        copied = checked(copied + count);
        if (copied > MaxVsixBytes)
        {
          throw new InvalidDataException("The VSIX artifact is too large.");
        }

        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
            .ConfigureAwait(false);
      }

      await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
      return new AcquiredSource(path, true, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      TryDeleteDownloaded(path);
      throw;
    }
    catch (Exception exception) when (exception is HttpRequestException or IOException or
        UnauthorizedAccessException or OverflowException)
    {
      TryDeleteDownloaded(path);
      return new AcquiredSource(null, false, new StructuredError(
          WdemErrorCode.DownloadError,
          "VSIX download failed.",
          "The VSIX artifact could not be safely downloaded.")
      {
        UnderlyingException = exception
      });
    }
  }

  private async Task<PreparedPlanArtifact> PreparePlanArtifactAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var instance = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (instance.Error is not null)
    {
      return new PreparedPlanArtifact(null, instance.Error);
    }

    if (instance.Instance is null)
    {
      return new PreparedPlanArtifact(null, new StructuredError(
          WdemErrorCode.DependencyError,
          "Visual Studio instance is unavailable.",
          "The selected Visual Studio instance was not found.")
      {
        ResourceId = resource.Id
      });
    }

    string? downloadedPath = null;
    try
    {
      var acquired = await AcquireSourceAsync(
          GetParameter(resource, SourcePathParameter)!,
          cancellationToken).ConfigureAwait(false);
      if (acquired.Error is not null)
      {
        return new PreparedPlanArtifact(
            null,
            acquired.Error with { ResourceId = resource.Id });
      }

      downloadedPath = acquired.Temporary ? acquired.Path : null;
      var staged = await _planArtifactStore.StageAsync(
          resource.Id,
          acquired.Path!,
          GetParameter(resource, ExpectedSha256Parameter)!,
          instance.Instance.InstanceId,
          cancellationToken).ConfigureAwait(false);
      if (staged.Error is not null || staged.Manifest is null || staged.StepEvidence is null)
      {
        return new PreparedPlanArtifact(
            null,
            (staged.Error ?? ConfigurationError(resource, "The VSIX source could not be staged.")) with
            {
              ResourceId = resource.Id
            });
      }

      if (SourceManifestMatches(resource, staged.Manifest, instance.Instance))
      {
        return new PreparedPlanArtifact(staged.StepEvidence, null);
      }

      await DiscardPreparedArtifactAsync(
          resource,
          instance.Instance.InstanceId,
          staged.StepEvidence,
          cancellationToken)
          .ConfigureAwait(false);
      return new PreparedPlanArtifact(null, new StructuredError(
              WdemErrorCode.ConfigurationError,
              "VSIX source is incompatible.",
              "The verified VSIX identity, version, or Visual Studio installation target does not match the resource.")
      {
        ResourceId = resource.Id
      });
    }
    finally
    {
      if (downloadedPath is not null)
      {
        TryDeleteDownloaded(downloadedPath);
      }
    }
  }

  private async Task DiscardPreparedArtifactAsync(
      ResourceDefinition resource,
      string instanceId,
      string stepEvidence,
      CancellationToken cancellationToken)
  {
    var claim = await _planArtifactStore.ClaimAsync(
        resource.Id,
        $"{resource.Id}:install:{stepEvidence}",
        GetParameter(resource, ExpectedSha256Parameter)!,
        instanceId,
        cancellationToken).ConfigureAwait(false);
    if (claim.Artifact is not null)
    {
      await claim.Artifact.DisposeAsync().ConfigureAwait(false);
    }
  }

  private Wdem.Core.Compliance.ComplianceResult Evaluate(
      ResourceDefinition resource,
      DetectedState state) => _complianceEvaluator.Evaluate(
          resource with
          {
            Parameters = resource.Parameters
                .Where(pair => !Matches(pair.Key, ExpectedSha256Parameter))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
          },
          state);

  private static bool VersionMatches(ResourceDefinition resource, string version)
  {
    if (!string.IsNullOrWhiteSpace(resource.PreferredVersion) &&
        !Matches(resource.PreferredVersion, version))
    {
      return false;
    }

    if (string.IsNullOrWhiteSpace(resource.VersionConstraint))
    {
      return true;
    }

    return SemanticVersion.TryParse(version, out var semantic) &&
        VersionConstraint.Parse(resource.VersionConstraint).IsSatisfiedBy(semantic);
  }

  private static bool SourceManifestMatches(
      ResourceDefinition resource,
      VsixManifest manifest,
      VisualStudioInstance instance) =>
      Matches(manifest.Id, GetParameter(resource, ExtensionIdParameter)!) &&
      VersionMatches(resource, manifest.Version) &&
      VsixInstallationTargetCompatibility.IsCompatible(manifest.Targets, instance);

  private static DetectedState CreateDetectedState(
      ResourceDefinition resource,
      VsixManifest manifest)
  {
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
        ["sourceManifestPath"] = manifest.ManifestPath,
        ["visualStudioInstanceId"] = manifest.VisualStudioInstanceId
      }
    };
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

  private static DetectedState DetectionFailure(ResourceDefinition resource, StructuredError error) => new()
  {
    ResourceId = resource.Id,
    Outcome = DetectionOutcome.Failed,
    Error = error.Detail,
    StructuredError = error.ResourceId is null ? error with { ResourceId = resource.Id } : error
  };

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

  private static ProviderValidationResult Validation(
      ResourceDefinition resource,
      IReadOnlyList<(WdemErrorCode Code, string Detail)> errors) => errors.Count == 0
          ? ProviderValidationResult.Valid
          : new ProviderValidationResult
          {
            Errors = errors.Select(error => error.Detail).ToArray(),
            StructuredErrors = errors.Select(error => new StructuredError(
                error.Code,
                "Visual Studio extension validation failed.",
                error.Detail)
            {
              ResourceId = resource.Id
            }).ToArray()
          };

  private static void RequireId(
      ResourceDefinition resource,
      string parameter,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    var value = GetParameter(resource, parameter);
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
    {
      errors.Add((WdemErrorCode.ConfigurationError, $"Parameter '{parameter}' is required."));
    }
  }

  private static void ValidateVersion(
      ResourceDefinition resource,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    if (string.IsNullOrWhiteSpace(resource.VersionConstraint))
    {
      return;
    }

    try
    {
      _ = VersionConstraint.Parse(resource.VersionConstraint);
    }
    catch (FormatException)
    {
      errors.Add((WdemErrorCode.VersionError, "The version constraint is invalid."));
    }
  }

  private static bool IsValidSource(string? source)
  {
    if (string.IsNullOrWhiteSpace(source) || source.Any(char.IsControl))
    {
      return false;
    }

    if (Path.IsPathFullyQualified(source))
    {
      return string.Equals(Path.GetExtension(source), ".vsix", StringComparison.OrdinalIgnoreCase);
    }

    return Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(Path.GetExtension(uri.AbsolutePath), ".vsix", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

  private static string? GetParameter(ResourceDefinition resource, string parameter) =>
      resource.Parameters.TryGetValue(parameter, out var value) ? value : null;

  private static string? GetInstanceId(ResourceDefinition resource) =>
      GetParameter(resource, InstanceIdParameter) ??
      GetParameter(resource, VisualStudioInstanceIdParameter);

  private static bool Matches(string? left, string? right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

  private static IReadOnlyList<PlanArtifactReference> GetPlanArtifactsToAbandon(
      ResourceDefinition resource,
      ResourcePlan plan)
  {
    if (!string.Equals(resource.Id, plan.ResourceId, StringComparison.Ordinal))
    {
      return [];
    }

    return plan.Steps
        .Where(step => VsixPlanArtifactStore.HasValidStepEvidence(resource.Id, step.Id))
        .Select(step => new PlanArtifactReference(resource.Id, step.Id))
        .Distinct()
        .ToArray();
  }

  private static StructuredError ConfigurationError(
      ResourceDefinition? resource,
      string detail,
      Exception? exception = null) => new(
          WdemErrorCode.ConfigurationError,
          "VSIX artifact is invalid.",
          detail)
      {
        ResourceId = resource?.Id,
        UnderlyingException = exception
      };

  private static StructuredError? ProcessError(
      ResourceDefinition resource,
      PlanStep step,
      ProcessExecutionResult process)
  {
    if (process.Started && process.ExitCode == 0 && process.Error is null)
    {
      return null;
    }

    return (process.Error ?? new StructuredError(
        WdemErrorCode.InstallationError,
        "VSIX installation failed.",
        "VSIXInstaller did not complete successfully.")) with
    {
      ResourceId = resource.Id,
      StepId = step.Id,
      ProcessExitCode = process.ExitCode
    };
  }

  private static void TryDeleteDownloaded(string path)
  {
    try
    {
      var directory = Path.GetDirectoryName(path);
      if (File.Exists(path))
      {
        File.Delete(path);
      }

      if (directory is not null && Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      // Cleanup is best effort; the artifact has already been closed.
    }
  }

  private sealed record InstanceSelection(VisualStudioInstance? Instance, StructuredError? Error);
  private sealed record AcquiredSource(string? Path, bool Temporary, StructuredError? Error);
  private sealed record PreparedPlanArtifact(string? StepEvidence, StructuredError? Error);
  private sealed record PlanArtifactReference(string ResourceId, string StepId);
}
