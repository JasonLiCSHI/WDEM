using System.Security.Cryptography;
using System.Text.Json;
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
  public const string ProductIdParameter = "productId";
  public const string EditionParameter = "edition";
  public const string ChannelIdParameter = "channelId";

  private const long MaxVsixBytes = 512L * 1024 * 1024;
  private readonly IVisualStudioDiscovery _discovery;
  private readonly IVsixManifestReader _manifestReader;
  private readonly IProcessExecutor _processExecutor;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly ISecureArtifactStager _artifactStager;
  private readonly HttpClient _httpClient;

  public VisualStudioExtensionProvider(
      IVisualStudioDiscovery discovery,
      IVsixManifestReader manifestReader,
      IProcessExecutor processExecutor,
      IComplianceEvaluator complianceEvaluator,
      ISecureArtifactStager? artifactStager = null,
      HttpClient? httpClient = null,
      ITrustedFileVerifier? trustedFileVerifier = null)
  {
    _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    _manifestReader = manifestReader ?? throw new ArgumentNullException(nameof(manifestReader));
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    _httpClient = httpClient ?? new HttpClient();
    var verifier = trustedFileVerifier ?? new TrustedFileVerifier();
    _artifactStager = artifactStager ?? new SecureArtifactStager(verifier: verifier);
  }

  public string ResourceType => "visual-studio-extension";
  public string ProviderName => "vsix";
  public ProviderCapabilities Capabilities { get; } = new()
  {
    SupportsSource = true,
    SupportsVersionConstraints = true,
    SupportsInProgressCancellation = true,
    ConcurrencyGroup = "visual-studio-installer",
    AcquisitionOnlyParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      ExpectedSha256Parameter
    }
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
      VisualStudioInstanceIdParameter,
      ProductIdParameter,
      EditionParameter,
      ChannelIdParameter
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
      var error = instance.Error ?? new StructuredError(
          WdemErrorCode.DependencyError,
          "Visual Studio instance is unavailable.",
          "The selected Visual Studio instance was not found.")
      {
        ResourceId = resource.Id
      };
      return CreatePlan(resource, compliance.Status, false) with
      {
        Error = error.Detail,
        StructuredErrors = [error]
      };
    }

    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return CreateSatisfiedPlan(resource, instance.Instance);
    }

    return CreateExecutablePlan(resource, compliance.Status, instance.Instance);
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
        static (_, step) => HasValidStepId(step.Id));
    if (invalidPlan is not null)
    {
      return invalidPlan;
    }

    var currentState = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    var expectedCurrentPlan = await PlanAsync(
        resource,
        currentState,
        cancellationToken).ConfigureAwait(false);
    if (!MatchesExpectedPlan(plan, expectedCurrentPlan))
    {
      return PlanMismatchFailure(resource, plan.Steps.FirstOrDefault());
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
              "Visual Studio instance changed after planning.",
              "The selected Visual Studio instance is missing or no longer matches the approved plan.")
          {
            ResourceId = resource.Id,
            StepId = step.Id
          },
          null,
          0);
    }

    var expectedPlan = CreateExecutablePlan(resource, plan.Compliance, instance.Instance);
    if (!MatchesExpectedPlan(plan, expectedPlan))
    {
      return PlanMismatchFailure(resource, step);
    }

    progress?.Report(new ProviderProgress("Apply", 0.25, "Acquiring and validating the VSIX artifact.", step.Id));
    var staged = await StageApplySourceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (staged.Artifact is null)
    {
      return ProviderLifecycleSupport.Failure(
          resource,
          step,
          (staged.Error ?? ConfigurationError(resource, "The VSIX source could not be staged.")) with
          {
            ResourceId = resource.Id,
            StepId = step.Id
          },
          null,
          0.25);
    }

    await using var approvedArtifact = staged.Artifact;
    VsixManifestReadResult sourceManifest;
    try
    {
      sourceManifest = await _manifestReader.ReadSourceAsync(
          approvedArtifact.Path,
          instance.Instance.InstanceId,
          cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
        InvalidDataException)
    {
      var sourceReadError = new StructuredError(
          WdemErrorCode.ConfigurationError,
          "VSIX source manifest is invalid.",
          "The verified staged VSIX manifest could not be safely read.")
      {
        ResourceId = resource.Id,
        StepId = step.Id,
        UnderlyingException = exception
      };
      return ProviderLifecycleSupport.Failure(resource, step, sourceReadError, null, 0.25);
    }

    if (sourceManifest.Manifest is null || sourceManifest.Error is not null)
    {
      return ProviderLifecycleSupport.Failure(
          resource,
          step,
          (sourceManifest.Error ?? ConfigurationError(resource, "The staged VSIX manifest is invalid.")) with
          {
            ResourceId = resource.Id,
            StepId = step.Id
          },
          null,
          0.25);
    }

    if (!SourceManifestMatches(resource, sourceManifest.Manifest, instance.Instance))
    {
      return ProviderLifecycleSupport.Failure(
          resource,
          step,
          new StructuredError(
              WdemErrorCode.VersionError,
              "VSIX source does not match the requested extension.",
              "The verified VSIX manifest identity, version, or installation target does not match the approved resource.")
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
      var selection = SelectInstance(resource, instances);
      if (selection.Instance is not null)
      {
        return new InstanceSelection(selection.Instance, null);
      }

      if (selection.IsIncompatible)
      {
        return new InstanceSelection(null, new StructuredError(
            WdemErrorCode.ConfigurationError,
            "The selected Visual Studio instance is incompatible.",
            "The instance does not match the configured product, edition, or channel.")
        {
          ResourceId = resource.Id
        });
      }

      return !selection.IsAmbiguous
          ? new InstanceSelection(null, null)
          : new InstanceSelection(null, new StructuredError(
              WdemErrorCode.DetectionError,
              "Visual Studio instance selection is ambiguous.",
              $"Set parameter 'instanceId' to one of: {string.Join(", ", selection.CandidateInstanceIds)}.")
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

  private async Task<SecureArtifactStageResult> StageApplySourceAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var source = GetParameter(resource, SourcePathParameter)!;
    var expectedSha256 = GetParameter(resource, ExpectedSha256Parameter)!;
    if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile)
    {
      try
      {
        return await _artifactStager.StageVerifiedAsync(
            Path.GetFullPath(source),
            expectedSha256,
            SecureArtifactKind.VisualStudioExtension,
            cancellationToken).ConfigureAwait(false);
      }
      catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
      {
        return new SecureArtifactStageResult(
            null,
            ConfigurationError(resource, "The VSIX source path is invalid.", exception));
      }
    }

    try
    {
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
      return await _artifactStager.StageVerifiedAsync(
          sourceStream,
          expectedSha256,
          SecureArtifactKind.VisualStudioExtension,
          cancellationToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (OperationCanceledException exception)
    {
      return DownloadFailure(exception);
    }
    catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
        IOException or UnauthorizedAccessException or OverflowException)
    {
      return DownloadFailure(exception);
    }
  }

  private static SecureArtifactStageResult DownloadFailure(Exception exception) => new(
      null,
      new StructuredError(
          WdemErrorCode.DownloadError,
          "VSIX download failed.",
          "The VSIX artifact could not be safely downloaded.")
      {
        UnderlyingException = exception
      });

  private Wdem.Core.Compliance.ComplianceResult Evaluate(
      ResourceDefinition resource,
      DetectedState state) => _complianceEvaluator.Evaluate(
          ProviderResourceProjection.ForCompliance(resource, Capabilities),
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

  private static ResourcePlan CreateExecutablePlan(
      ResourceDefinition resource,
      ComplianceStatus compliance,
      VisualStudioInstance instance)
  {
    var action = compliance == ComplianceStatus.Missing
        ? PlanAction.Install
        : PlanAction.Upgrade;
    var description = $"Install {resource.DisplayName ?? resource.Id} with VSIXInstaller.";
    var reason = compliance == ComplianceStatus.Missing
        ? "The extension is not installed in the selected Visual Studio instance."
        : "The installed extension version does not satisfy the requested version.";
    var privilegeRequirement = resource.PrivilegeRequirement;
    var restartPolicy = resource.RestartPolicy;
    const bool isDestructive = false;
    var definitionFingerprint = ResourceDefinitionFingerprint.Create(resource);
    var evidence = CreateCanonicalPlanEvidence(
        resource,
        definitionFingerprint,
        compliance,
        true,
        1,
        description,
        action,
        privilegeRequirement,
        restartPolicy,
        isDestructive,
        reason,
        instance);
    var evidenceHash = CreateCanonicalEvidenceHash(evidence);
    var stepId = "vsix:install:" + evidenceHash;
    return CreatePlan(resource, compliance, true) with
    {
      ExecutionPreconditionFingerprint = evidenceHash,
      Steps =
      [
        new PlanStep
        {
          Id = stepId,
          Description = description,
          Action = action,
          PrivilegeRequirement = privilegeRequirement,
          RestartPolicy = restartPolicy,
          IsDestructive = isDestructive,
          Reason = reason
        }
      ]
    };
  }

  private static ResourcePlan CreateSatisfiedPlan(
      ResourceDefinition resource,
      VisualStudioInstance instance)
  {
    var definitionFingerprint = ResourceDefinitionFingerprint.Create(resource);
    var evidence = CreateCanonicalPlanEvidence(
        resource,
        definitionFingerprint,
        ComplianceStatus.Satisfied,
        true,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        instance);
    return CreatePlan(resource, ComplianceStatus.Satisfied, true) with
    {
      ExecutionPreconditionFingerprint = CreateCanonicalEvidenceHash(evidence)
    };
  }

  private static CanonicalPlanEvidence CreateCanonicalPlanEvidence(
      ResourceDefinition resource,
      string definitionFingerprint,
      ComplianceStatus compliance,
      bool isExecutable,
      int stepCount,
      string? description,
      PlanAction? action,
      PrivilegeRequirement? privilegeRequirement,
      RestartPolicy? restartPolicy,
      bool? isDestructive,
      string? reason,
      VisualStudioInstance instance) => new(
        resource.Id,
        resource.Type,
        resource.Provider,
        definitionFingerprint,
        definitionFingerprint,
        null,
        compliance,
        isExecutable,
        stepCount,
        description,
        action,
        privilegeRequirement,
        restartPolicy,
        isDestructive,
        reason,
        new InstanceEvidence(
            instance.InstanceId,
            instance.ProductId,
            instance.InstallationPath,
            instance.ProductPath,
            instance.InstallationVersion));

  private static string CreateCanonicalEvidenceHash(CanonicalPlanEvidence evidence) =>
      Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidence)));

  private static bool MatchesExpectedPlan(ResourcePlan actual, ResourcePlan expected)
  {
    if (!string.Equals(actual.ResourceId, expected.ResourceId, StringComparison.Ordinal) ||
        !string.Equals(actual.ResourceType, expected.ResourceType, StringComparison.Ordinal) ||
        !string.Equals(actual.ProviderName, expected.ProviderName, StringComparison.Ordinal) ||
        !string.Equals(
            actual.DesiredStateFingerprint,
            expected.DesiredStateFingerprint,
            StringComparison.Ordinal) ||
        !string.Equals(
            actual.ExecutionPreconditionFingerprint,
            expected.ExecutionPreconditionFingerprint,
            StringComparison.Ordinal) ||
        actual.Compliance != expected.Compliance ||
        actual.IsExecutable != expected.IsExecutable ||
        !string.Equals(actual.Error, expected.Error, StringComparison.Ordinal) ||
        actual.StructuredErrors.Count != expected.StructuredErrors.Count ||
        actual.Steps.Count != expected.Steps.Count)
    {
      return false;
    }

    if (actual.Steps.Count == 0)
    {
      return true;
    }

    var actualStep = actual.Steps[0];
    var expectedStep = expected.Steps[0];
    return string.Equals(actualStep.Id, expectedStep.Id, StringComparison.Ordinal) &&
        string.Equals(actualStep.Description, expectedStep.Description, StringComparison.Ordinal) &&
        actualStep.Action == expectedStep.Action &&
        actualStep.PrivilegeRequirement == expectedStep.PrivilegeRequirement &&
        actualStep.RestartPolicy == expectedStep.RestartPolicy &&
        actualStep.IsDestructive == expectedStep.IsDestructive &&
        string.Equals(actualStep.Reason, expectedStep.Reason, StringComparison.Ordinal);
  }

  private static ResourceApplyResult PlanMismatchFailure(
      ResourceDefinition resource,
      PlanStep? step)
  {
    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Resource plan cannot be applied.",
        "The approved Visual Studio extension plan was modified or is stale.")
    {
      ResourceId = resource.Id,
      StepId = step?.Id
    };
    if (step is not null)
    {
      return ProviderLifecycleSupport.Failure(resource, step, error, null, 0);
    }

    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Failed,
      Error = error,
      Diagnostics = [error]
    };
  }

  private static bool HasValidStepId(string stepId)
  {
    const string prefix = "vsix:install:";
    return stepId.Length == prefix.Length + 64 &&
        stepId.StartsWith(prefix, StringComparison.Ordinal) &&
        stepId[prefix.Length..].All(Uri.IsHexDigit);
  }

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
    if (string.Equals(source, "${WDEM_COMPANY_VSIX_PATH}", StringComparison.Ordinal))
    {
      return true;
    }

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

  private static bool IsSha256(string? value) =>
      string.Equals(value, "${WDEM_COMPANY_VSIX_SHA256}", StringComparison.Ordinal) ||
      value is { Length: 64 } && value.All(Uri.IsHexDigit);

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

  private sealed record InstanceSelection(VisualStudioInstance? Instance, StructuredError? Error);
  private sealed record CanonicalPlanEvidence(
      string ResourceId,
      string ResourceType,
      string ProviderName,
      string DesiredStateFingerprint,
      string DefinitionFingerprint,
      string? ExecutionPreconditionFingerprint,
      ComplianceStatus Compliance,
      bool IsExecutable,
      int StepCount,
      string? Description,
      PlanAction? Action,
      PrivilegeRequirement? PrivilegeRequirement,
      RestartPolicy? RestartPolicy,
      bool? IsDestructive,
      string? Reason,
      InstanceEvidence VisualStudioIdentity);

  private sealed record InstanceEvidence(
      string InstanceId,
      string ProductId,
      string InstallationPath,
      string ProductPath,
      string InstallationVersion);
}
