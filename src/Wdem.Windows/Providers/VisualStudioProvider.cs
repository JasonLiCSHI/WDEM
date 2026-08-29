using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Core.Versions;
using Wdem.Windows.Security;
using Wdem.Windows.VisualStudio;

namespace Wdem.Windows.Providers;

public sealed class VisualStudioProvider : IResourceProvider
{
  private readonly IVisualStudioDiscovery _discovery;
  private readonly IVisualStudioInstallerClient? _installer;
  private readonly ITrustedFileVerifier _trustedFileVerifier;
  private readonly ISecureArtifactStager _secureArtifactStager;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly VisualStudioConfigurationResolver _configurationResolver = new();

  public VisualStudioProvider(
      IVisualStudioDiscovery discovery,
      IComplianceEvaluator complianceEvaluator)
  {
    _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    _trustedFileVerifier = new TrustedFileVerifier();
    _secureArtifactStager = new SecureArtifactStager(verifier: _trustedFileVerifier);
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    Capabilities = CreateCapabilities(supportsInstallerParameters: false);
  }

  public VisualStudioProvider(
      IVisualStudioDiscovery discovery,
      IVisualStudioInstallerClient installer,
      ITrustedFileVerifier trustedFileVerifier,
      IComplianceEvaluator complianceEvaluator,
      ISecureArtifactStager? secureArtifactStager = null)
  {
    _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    _trustedFileVerifier = trustedFileVerifier ??
        throw new ArgumentNullException(nameof(trustedFileVerifier));
    _secureArtifactStager = secureArtifactStager ?? new SecureArtifactStager(
        verifier: _trustedFileVerifier);
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    Capabilities = CreateCapabilities(supportsInstallerParameters: true);
  }

  public string ResourceType => "visual-studio";
  public string ProviderName => "visual-studio";
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
      errors.Add("Resource type must be 'visual-studio'.");
    }

    if (!string.Equals(resource.Provider, ProviderName, StringComparison.OrdinalIgnoreCase))
    {
      errors.Add("Resource provider must be 'visual-studio'.");
    }

    if (!TryParseOptions(resource, out var options, out var optionErrors))
    {
      errors.AddRange(optionErrors);
    }

    if (options?.VsConfigPath is not null && GetExpectedSha256(resource) is null)
    {
      errors.Add("Parameter 'expectedSha256' is required when 'vsconfigPath' is specified.");
    }

    if (GetExpectedSha256(resource) is { } expectedHash &&
        (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)))
    {
      errors.Add("Parameter 'expectedSha256' must contain exactly 64 hexadecimal characters.");
    }

    if ((options?.BootstrapperUri is null) != (options?.BootstrapperSha256 is null))
    {
      errors.Add("Parameters 'bootstrapperUri' and 'bootstrapperSha256' must be specified together.");
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
        !TryParseOptions(resource, out var options, out _))
    {
      var error = validation.StructuredErrors.First();
      return Failure(resource, error);
    }

    var resolved = await _configurationResolver.ResolveAsync(
        options!,
        GetExpectedSha256(resource) ?? string.Empty,
        cancellationToken).ConfigureAwait(false);
    if (resolved.Error is not null)
    {
      return Failure(resource, resolved.Error with { ResourceId = resource.Id });
    }

    options = resolved.Options;

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

    return VisualStudioStateMapper.Create(
        resource.Id,
        candidates[0],
        resolved.VerifiedPath,
        resolved.Sha256);
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

    _ = TryParseOptions(resource, out var options, out _);
    var resolved = await _configurationResolver.ResolveAsync(
        options!,
        GetExpectedSha256(resource) ?? string.Empty,
        cancellationToken).ConfigureAwait(false);
    if (resolved.Error is not null)
    {
      var error = resolved.Error with { ResourceId = resource.Id };
      return BasePlan(resource, ComplianceStatus.ConfigurationMismatch, isExecutable: false) with
      {
        Error = error.Detail,
        StructuredErrors = [error]
      };
    }

    options = resolved.Options;

    var compliance = Evaluate(resource, currentState, options);
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
    var operation = action switch
    {
      PlanAction.Install => "install",
      PlanAction.Upgrade => "update",
      _ => "modify"
    };
    return plan with
    {
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:{operation}",
          Description = action switch
          {
            PlanAction.Install => "Install Visual Studio.",
            PlanAction.Upgrade => "Update Visual Studio.",
            _ => "Modify Visual Studio workloads and components."
          },
          Action = action,
          PrivilegeRequirement = PrivilegeRequirement.Administrator,
          RestartPolicy = RestartPolicy.NoRestart
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
    if (!validation.IsValid)
    {
      return ProviderLifecycleSupport.RejectInvalidResource(resource, validation)!;
    }

    if (!IsApplicablePlan(resource, plan, out var planError))
    {
      return ApplyFailure(resource, null, planError!, null, 0);
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.NotRequired
      };
    }

    if (_installer is null)
    {
      var error = new StructuredError(
          WdemErrorCode.ProviderError,
          "Visual Studio changes are not available.",
          "Visual Studio installation is not implemented by this provider instance because its installer client is not configured.")
      {
        ResourceId = resource.Id
      };
      return ApplyFailure(resource, plan.Steps[0], error, null, 0);
    }

    _ = TryParseOptions(resource, out var options, out _);
    var step = plan.Steps[0];
    progress?.Report(new ProviderProgress(
        "BootstrapperVerification", 0.1, "Verifying Visual Studio installer inputs.", step.Id));
    SecureStagedArtifact? stagedConfiguration = null;
    if (options!.VsConfigPath is not null)
    {
      var staged = await _secureArtifactStager.StageVerifiedAsync(
          options.VsConfigPath,
          GetExpectedSha256(resource)!,
          SecureArtifactKind.VisualStudioConfiguration,
          cancellationToken).ConfigureAwait(false);
      if (staged.Artifact is null)
      {
        return ApplyFailure(
            resource,
            step,
            staged.Error! with { ResourceId = resource.Id, StepId = step.Id },
            null,
            0.1);
      }

      stagedConfiguration = staged.Artifact;
    }

    await using var configurationLease = stagedConfiguration;
    if (stagedConfiguration is not null)
    {
      var parsed = await VisualStudioConfigurationParser.ParseAsync(
          stagedConfiguration.Path,
          cancellationToken).ConfigureAwait(false);
      if (parsed.Error is not null)
      {
        return ApplyFailure(
            resource,
            step,
            parsed.Error with { ResourceId = resource.Id, StepId = step.Id },
            null,
            0.1);
      }

      options = options with
      {
        Workloads = MergeIds(options.Workloads, parsed.Configuration!.Workloads),
        Components = MergeIds(options.Components, parsed.Configuration.Components)
      };
    }

    var verifiedVsConfig = stagedConfiguration?.Path;

    await using var bootstrapper = options.BootstrapperUri is not null
        ? await _installer.AcquireBootstrapperAsync(
            options.BootstrapperUri,
            options.BootstrapperSha256!,
            cancellationToken).ConfigureAwait(false)
        : null;
    var setupPath = DefaultSetupPath();
    if (bootstrapper is not null)
    {
      if (!bootstrapper.IsTrusted)
      {
        return ApplyFailure(
            resource,
            step,
            bootstrapper.Error! with { ResourceId = resource.Id, StepId = step.Id },
            null,
            0.1);
      }

      setupPath = bootstrapper.VerifiedPath!;
    }

    VisualStudioInstallerResult command;
    if (step.Action == PlanAction.Install)
    {
      progress?.Report(new ProviderProgress("Install", 0.35, "Installing Visual Studio.", step.Id));
      command = await _installer.InstallAsync(
          setupPath,
          options.ProductId,
          null,
          options.InstallPath ?? DefaultInstallPath(options),
          options.Workloads,
          options.Components,
          verifiedVsConfig,
          cancellationToken).ConfigureAwait(false);
    }
    else
    {
      var operation = step.Action == PlanAction.Upgrade ? "Update" : "Modify";
      progress?.Report(new ProviderProgress(
          operation,
          0.35,
          $"{operation} Visual Studio.",
          step.Id));
      var instances = await _discovery.DiscoverAsync(
          options.Workloads,
          options.Components,
          cancellationToken).ConfigureAwait(false);
      var instance = SelectInstance(instances, options);
      if (instance is null)
      {
        var error = new StructuredError(
            WdemErrorCode.VerificationError,
            "Visual Studio instance could not be selected.",
            "The planned Visual Studio instance was not found before modification.")
        {
          ResourceId = resource.Id,
          StepId = step.Id
        };
        return ApplyFailure(resource, step, error, null, 0.35);
      }

      command = step.Action == PlanAction.Upgrade
          ? await _installer.UpdateAsync(
              setupPath,
              instance.InstallationPath,
              cancellationToken).ConfigureAwait(false)
          : await _installer.ModifyAsync(
              setupPath,
              instance.InstallationPath,
              options.Workloads,
              options.Components,
              verifiedVsConfig,
              cancellationToken).ConfigureAwait(false);
    }

    if (command.Process.Error is not null ||
        !command.Process.Started ||
        command.Process.ExitCode is not (0 or 1641 or 3010))
    {
      var error = command.Process.Error ?? new StructuredError(
          WdemErrorCode.InstallationError,
          "Visual Studio installer failed.",
          "The Visual Studio installer did not complete successfully.")
      {
        ResourceId = resource.Id,
        StepId = step.Id,
        ProcessExitCode = command.Process.ExitCode
      };
      return ApplyFailure(
          resource,
          step,
          error,
          command.Process.ExitCode,
          0.5,
          command.Evidence,
          command.RestartRequirement);
    }

    progress?.Report(new ProviderProgress(
        "Configuration", 0.65, "Applying Visual Studio workloads and components.", step.Id));
    progress?.Report(new ProviderProgress(
        "Verification", 0.85, "Verifying Visual Studio configuration.", step.Id));
    var finalVerification = await VerifyAppliedConfigurationAsync(
        resource,
        options,
        stagedConfiguration?.Sha256,
        cancellationToken).ConfigureAwait(false);
    if (finalVerification.Compliance != ComplianceStatus.Satisfied)
    {
      var compliance = Evaluate(resource, finalVerification.DetectedState, options);
      var error = compliance.Error ?? new StructuredError(
          WdemErrorCode.VerificationError,
          "Visual Studio verification failed.",
          finalVerification.Message ?? "Visual Studio did not reach the requested state.")
      {
        ResourceId = resource.Id,
        StepId = step.Id,
        ProcessExitCode = command.Process.ExitCode
      };
      return ApplyFailure(
          resource,
          step,
          error,
          command.Process.ExitCode,
          0.85,
          command.Evidence,
          command.RestartRequirement);
    }

    var evidence = string.Join(
        "; ",
        command.Evidence.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Succeeded,
      RestartRequirement = command.RestartRequirement,
      StepResults =
      [
        new ProviderStepResult
        {
          StepId = step.Id,
          Action = step.Action,
          Progress = 1,
          ProcessExitCode = command.Process.ExitCode,
          Message = evidence
        }
      ]
    };
  }

  public async ValueTask<VerificationResult> VerifyAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var detected = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    _ = TryParseOptions(resource, out var options, out _);
    if (options is not null)
    {
      var resolved = await _configurationResolver.ResolveAsync(
          options,
          GetExpectedSha256(resource) ?? string.Empty,
          cancellationToken).ConfigureAwait(false);
      options = resolved.Error is null ? resolved.Options : null;
    }

    var compliance = Evaluate(resource, detected, options);
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

  private ComplianceResult Evaluate(
      ResourceDefinition resource,
      DetectedState state,
      VisualStudioResourceOptions? options)
  {
    var baseCompliance = _complianceEvaluator.Evaluate(resource, state);
    if (baseCompliance.Status != ComplianceStatus.Satisfied || options is null)
    {
      return baseCompliance;
    }

    if (!EvidenceEquals(state, "edition", options.Edition) ||
        !EvidenceEquals(state, "channel", options.ChannelId))
    {
      return ConfigurationMismatch(resource, "The installed edition or channel does not match.");
    }

    if (!ContainsEvery(state, "workloads", options.Workloads))
    {
      return ConfigurationMismatch(resource, "One or more requested workloads are missing.");
    }

    if (!ContainsEvery(state, "components", options.Components))
    {
      return ConfigurationMismatch(resource, "One or more requested components are missing.");
    }

    if (options.VsConfigPath is not null)
    {
      var expectedHash = GetExpectedSha256(resource);
      if (!EvidenceEquals(state, "vsconfigPath", Path.GetFullPath(options.VsConfigPath)) ||
          !EvidenceEquals(state, "vsconfigSource", Path.GetFullPath(options.VsConfigPath)) ||
          !EvidenceEquals(state, "vsconfigSha256", expectedHash))
      {
        return ConfigurationMismatch(resource, "The .vsconfig source or hash was not verified.");
      }
    }

    return baseCompliance;
  }

  private static ComplianceResult ConfigurationMismatch(
      ResourceDefinition resource,
      string detail) => new(
          ComplianceStatus.ConfigurationMismatch,
          $"Resource '{resource.Id}' has a different Visual Studio configuration.",
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "Visual Studio configuration does not match.",
              detail)
          {
            ResourceId = resource.Id
          });

  private static bool EvidenceEquals(
      DetectedState state,
      string key,
      string? expected) => expected is not null &&
      state.Evidence.TryGetValue(key, out var actual) &&
      string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

  private static bool ContainsEvery(
      DetectedState state,
      string key,
      IReadOnlyList<string> expected)
  {
    if (!state.Evidence.TryGetValue(key, out var joined))
    {
      return expected.Count == 0;
    }

    var actual = joined.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    return expected.All(actual.Contains);
  }

  private static bool TryParseOptions(
      ResourceDefinition resource,
      out VisualStudioResourceOptions? options,
      out IReadOnlyList<string> errors)
  {
    if (!resource.Parameters.ContainsKey("expectedSha256"))
    {
      return VisualStudioResourceOptions.TryParse(resource, out options, out errors);
    }

    var parameters = resource.Parameters
        .Where(pair => !string.Equals(
            pair.Key,
            "expectedSha256",
            StringComparison.OrdinalIgnoreCase))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    return VisualStudioResourceOptions.TryParse(
        resource with { Parameters = parameters },
        out options,
        out errors);
  }

  private static string? GetExpectedSha256(ResourceDefinition resource) =>
      resource.Parameters.TryGetValue("expectedSha256", out var value) &&
      !string.IsNullOrWhiteSpace(value)
          ? value.Trim()
          : null;

  private static VisualStudioInstance? SelectInstance(
      IReadOnlyList<VisualStudioInstance> instances,
      VisualStudioResourceOptions options)
  {
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
            StringComparison.OrdinalIgnoreCase));
    if (options.InstanceId is not null)
    {
      candidates = candidates.Where(instance => string.Equals(
          instance.InstanceId,
          options.InstanceId,
          StringComparison.OrdinalIgnoreCase));
    }

    var selected = candidates.Take(2).ToArray();
    return selected.Length == 1 ? selected[0] : null;
  }

  private static bool IsApplicablePlan(
      ResourceDefinition resource,
      ResourcePlan plan,
      out StructuredError? error)
  {
    var validIdentity = plan.IsExecutable &&
        string.Equals(plan.ResourceId, resource.Id, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(plan.ResourceType, "visual-studio", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(plan.ProviderName, "visual-studio", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            plan.DesiredStateFingerprint,
            ResourceDefinitionFingerprint.Create(resource),
            StringComparison.Ordinal);
    var validNoOp = !plan.RequiresApply &&
        plan.Compliance == ComplianceStatus.Satisfied &&
        plan.Steps.Count == 0;
    var validStep = plan.Steps.Count == 1 &&
        plan.Steps[0].Action is PlanAction.Install or PlanAction.Configure or PlanAction.Upgrade &&
        ((plan.Compliance == ComplianceStatus.Missing &&
          plan.Steps[0].Action == PlanAction.Install) ||
         (plan.Compliance == ComplianceStatus.VersionMismatch &&
          plan.Steps[0].Action == PlanAction.Upgrade) ||
         (plan.Compliance == ComplianceStatus.ConfigurationMismatch &&
          plan.Steps[0].Action == PlanAction.Configure)) &&
        string.Equals(
            plan.Steps[0].Id,
            $"{resource.Id}:{plan.Steps[0].Action switch
            {
              PlanAction.Install => "install",
              PlanAction.Upgrade => "update",
              _ => "modify"
            }}",
            StringComparison.Ordinal) &&
        plan.Steps[0].PrivilegeRequirement == PrivilegeRequirement.Administrator &&
        plan.Steps[0].RestartPolicy == RestartPolicy.NoRestart &&
        !plan.Steps[0].IsDestructive;
    if (validIdentity && (validNoOp || validStep))
    {
      error = null;
      return true;
    }

    error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Resource plan cannot be applied.",
        "The Visual Studio plan is stale or does not match the requested operation.")
    {
      ResourceId = resource.Id
    };
    return false;
  }

  private static ResourceApplyResult ApplyFailure(
      ResourceDefinition resource,
      PlanStep? step,
      StructuredError error,
      int? exitCode,
      double progress,
      IReadOnlyDictionary<string, string>? evidence = null,
      RestartPolicy? restartRequirement = null) => new()
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Failed,
        RestartRequirement = restartRequirement,
        Error = error,
        Diagnostics = [error],
        StepResults = step is null
            ? []
            :
            [
              new ProviderStepResult
              {
                StepId = step.Id,
                Action = step.Action,
                Progress = progress,
                ProcessExitCode = exitCode,
                Message = FormatEvidence(evidence),
                Error = error
              }
            ]
      };

  private async Task<VerificationResult> VerifyAppliedConfigurationAsync(
      ResourceDefinition resource,
      VisualStudioResourceOptions options,
      string? configurationSha256,
      CancellationToken cancellationToken)
  {
    var instances = await _discovery.DiscoverAsync(
        options.Workloads,
        options.Components,
        cancellationToken).ConfigureAwait(false);
    var selected = SelectInstance(instances, options);
    if (selected is null || !MatchesVersion(selected, resource.VersionConstraint))
    {
      return new VerificationResult
      {
        ResourceId = resource.Id,
        Compliance = ComplianceStatus.ConfigurationMismatch,
        DetectedState = new DetectedState
        {
          ResourceId = resource.Id,
          Outcome = DetectionOutcome.Succeeded,
          Exists = selected is not null
        },
        Message = "Visual Studio did not reach the requested state."
      };
    }

    var detected = VisualStudioStateMapper.Create(
        resource.Id,
        selected,
        options.VsConfigPath,
        configurationSha256);
    var compliance = Evaluate(resource, detected, options);
    return new VerificationResult
    {
      ResourceId = resource.Id,
      Compliance = compliance.Status,
      DetectedState = detected,
      Message = compliance.Status == ComplianceStatus.Satisfied ? null : compliance.Summary
    };
  }

  private static IReadOnlyList<string> MergeIds(
      IReadOnlyList<string> declared,
      IReadOnlyList<string> configured) => declared
          .Concat(configured)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToArray();

  private static string? FormatEvidence(IReadOnlyDictionary<string, string>? evidence)
  {
    if (evidence is null || evidence.Count == 0)
    {
      return null;
    }

    var safeEvidence = evidence.Where(pair => pair.Key is
        "installerPath" or "installerSha256" or "restartRequirement");
    var formatted = string.Join(
        "; ",
        safeEvidence.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key}={pair.Value}"));
    return formatted.Length == 0 ? null : formatted;
  }

  private static string DefaultSetupPath()
  {
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    if (string.IsNullOrWhiteSpace(programFiles))
    {
      programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
    }

    return Path.Combine(
        programFiles,
        "Microsoft Visual Studio",
        "Installer",
        "setup.exe");
  }

  private static string DefaultInstallPath(VisualStudioResourceOptions options)
  {
    var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    if (string.IsNullOrWhiteSpace(programFiles))
    {
      programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
    }

    var channelVersion = options.ChannelId.Split('.').ElementAtOrDefault(1) ?? "Current";
    return Path.Combine(
        programFiles,
        "Microsoft Visual Studio",
        channelVersion,
        options.Edition);
  }

  private static ProviderCapabilities CreateCapabilities(bool supportsInstallerParameters) => new()
  {
    SupportsVersionConstraints = true,
    SupportsInstallerParameters = supportsInstallerParameters,
    SupportsInProgressCancellation = true
  };

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
