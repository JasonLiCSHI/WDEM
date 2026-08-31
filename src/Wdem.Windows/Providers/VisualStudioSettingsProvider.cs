using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;
using Wdem.Windows.Configuration;
using Wdem.Windows.VisualStudio;

namespace Wdem.Windows.Providers;

public sealed class VisualStudioSettingsProvider : IResourceProvider
{
  private const long MaxSettingsStoreBytes = 64L * 1024 * 1024;
  private static readonly TimeSpan ProcessOutputDrainAllowance = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan PostLaunchFinalizationTimeout = TimeSpan.FromMinutes(2);
  private static readonly TimeSpan FinalizationReservationMargin = TimeSpan.FromSeconds(55);
  private static readonly string[] InstancePreconditionEvidenceKeys =
  [
    "visualStudioInstanceId",
    "visualStudioProductId",
    "visualStudioProductDisplayVersion",
    "visualStudioInstallationVersion",
    "visualStudioEdition",
    "visualStudioChannelId",
    "visualStudioInstallationPath",
    "visualStudioProductPath",
    "visualStudioIsComplete",
    "visualStudioIsLaunchable"
  ];

  public const string SourcePathParameter = "sourcePath";
  public const string ExpectedSha256Parameter = "expectedSha256";
  public const string SettingsStorePathParameter = "settingsStorePath";
  public const string InstanceIdParameter = "instanceId";
  public const string ProductIdParameter = "productId";
  public const string EditionParameter = "edition";
  public const string ChannelIdParameter = "channelId";
  public const string VisualStudioResourceIdParameter = "visualStudioResourceId";
  public const string SettingsStoreSha256Parameter = "settingsStoreSha256";

  private readonly ConfigurationSourceResolver _sourceResolver;
  private readonly ConfigurationImporter _importer;
  private readonly IVisualStudioDiscovery _discovery;
  private readonly IProcessExecutor _processExecutor;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly Func<VisualStudioInstance, string> _settingsDirectoryResolver;

  public VisualStudioSettingsProvider(
      ConfigurationSourceResolver sourceResolver,
      ConfigurationImporter importer,
      IVisualStudioDiscovery discovery,
      IProcessExecutor processExecutor,
      IComplianceEvaluator complianceEvaluator,
      Func<VisualStudioInstance, string>? settingsDirectoryResolver = null)
  {
    _sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
    _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _complianceEvaluator = complianceEvaluator ??
        throw new ArgumentNullException(nameof(complianceEvaluator));
    _settingsDirectoryResolver = settingsDirectoryResolver ?? DefaultSettingsDirectory;
  }

  public string ResourceType => "visual-studio-settings";
  public string ProviderName => "visual-studio-settings";
  public ProviderCapabilities Capabilities { get; } = new()
  {
    SupportsSource = true,
    SupportsInProgressCancellation = true,
    CancellationFinalizationTimeout = ProcessExecutionRequest.DefaultTimeout +
        ProcessOutputDrainAllowance + PostLaunchFinalizationTimeout +
        FinalizationReservationMargin,
    ConcurrencyGroup = "visual-studio-installer"
  };

  public ValueTask<ProviderValidationResult> ValidateAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    var errors = new List<(WdemErrorCode Code, string Detail)>();
    if (!ConfigurationProviderSupport.Matches(resource.Type, ResourceType))
    {
      errors.Add((WdemErrorCode.ProviderError,
          "Resource type must be 'visual-studio-settings'."));
    }

    if (!ConfigurationProviderSupport.Matches(resource.Provider, ProviderName))
    {
      errors.Add((WdemErrorCode.ProviderError,
          "Resource provider must be 'visual-studio-settings'."));
    }

    var dependency = Get(resource, VisualStudioResourceIdParameter) ?? "visual-studio";
    if (!resource.Dependencies.Contains(dependency, StringComparer.OrdinalIgnoreCase))
    {
      errors.Add((WdemErrorCode.DependencyError,
          "The selected Visual Studio resource must be listed in Dependencies."));
    }

    ConfigurationProviderSupport.ValidateFileParameter(
        resource, SourcePathParameter, ".vssettings", requireAbsolute: false, errors);
    ConfigurationProviderSupport.ValidateFileParameter(
        resource, SettingsStorePathParameter, ".vssettings", requireAbsolute: false, errors);
    if (Get(resource, SourcePathParameter) is { } source &&
        ConfigurationSourceResolver.HasAlternateDataStream(source))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          "Parameter 'sourcePath' must not identify an NTFS alternate data stream."));
    }

    if (Get(resource, SettingsStorePathParameter) is { } settingsStorePath &&
        ConfigurationSourceResolver.HasAlternateDataStream(settingsStorePath))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          "Parameter 'settingsStorePath' must not identify an NTFS alternate data stream."));
    }

    if (!ConfigurationSourceResolver.IsSha256(Get(resource, ExpectedSha256Parameter)))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          "Parameter 'expectedSha256' must contain exactly 64 hexadecimal characters."));
    }

    if (string.IsNullOrWhiteSpace(Get(resource, InstanceIdParameter)))
    {
      RequireText(resource, ProductIdParameter, errors);
    }
    else
    {
      ValidateOptionalText(resource, InstanceIdParameter, errors);
      ValidateOptionalText(resource, ProductIdParameter, errors);
    }
    RequireText(resource, EditionParameter, errors);
    RequireText(resource, ChannelIdParameter, errors);
    ConfigurationProviderSupport.AddUnsupportedParameters(
        resource,
        errors,
        SourcePathParameter,
        ExpectedSha256Parameter,
        SettingsStorePathParameter,
        InstanceIdParameter,
        ProductIdParameter,
        EditionParameter,
        ChannelIdParameter,
        VisualStudioResourceIdParameter,
        SettingsStoreSha256Parameter);
    if (Get(resource, SettingsStoreSha256Parameter) is { } settingsStoreSha256 &&
        !ConfigurationSourceResolver.IsSha256(settingsStoreSha256))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          "Parameter 'settingsStoreSha256' must contain exactly 64 hexadecimal characters."));
    }
    return ValueTask.FromResult(ConfigurationProviderSupport.ToValidation(
        resource,
        "Visual Studio settings",
        errors));
  }

  public async ValueTask<DetectedState> DetectAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return ConfigurationProviderSupport.DetectionFailure(resource, validation.StructuredErrors[0]);
    }

    var source = await _sourceResolver.ResolveAsync(
        Get(resource, SourcePathParameter)!,
        Get(resource, ExpectedSha256Parameter)!,
        resource.ProfileSourcePath,
        cancellationToken).ConfigureAwait(false);
    if (!source.IsValid)
    {
      return ConfigurationProviderSupport.DetectionFailure(
          resource,
          source.Error! with { ResourceId = resource.Id });
    }

    var selection = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (selection.Error is not null)
    {
      return ConfigurationProviderSupport.DetectionFailure(resource, selection.Error);
    }

    if (selection.Instance is null)
    {
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false
      };
    }

    var pathError = ValidateSettingsStorePath(resource, selection.Instance, out var settingsStorePath);
    if (pathError is not null)
    {
      return ConfigurationProviderSupport.DetectionFailure(resource, pathError);
    }

    return await DetectBoundInstanceAsync(
        resource,
        selection.Instance,
        settingsStorePath,
        source.Source!.Sha256,
        cancellationToken).ConfigureAwait(false);
  }

  public async ValueTask<ResourcePlan> PlanAsync(
      ResourceDefinition resource,
      DetectedState currentState,
      CancellationToken cancellationToken)
  {
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return ConfigurationProviderSupport.Plan(resource, ComplianceStatus.DetectionFailed, false) with
      {
        Error = validation.StructuredErrors[0].Detail,
        StructuredErrors = validation.StructuredErrors
      };
    }

    var compliance = Evaluate(resource, currentState);
    var precondition = CreateExecutionPrecondition(currentState);
    if (precondition is null)
    {
      return ConfigurationProviderSupport.Plan(resource, ComplianceStatus.DetectionFailed, false) with
      {
        Error = "The detected Visual Studio settings destination state cannot be bound to the plan.",
        StructuredErrors =
        [
          ConfigurationProviderSupport.Error(resource, WdemErrorCode.DetectionError,
              "The detected Visual Studio settings destination state cannot be bound to the plan.")
        ]
      };
    }

    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return ConfigurationProviderSupport.Plan(resource, compliance.Status, true) with
      {
        ExecutionPreconditionFingerprint = precondition
      };
    }

    if (compliance.Status is ComplianceStatus.DetectionFailed or ComplianceStatus.Unsupported)
    {
      return ConfigurationProviderSupport.Plan(resource, compliance.Status, false) with
      {
        Error = compliance.Error?.Detail,
        StructuredErrors = compliance.Error is null ? [] : [compliance.Error]
      };
    }

    var selection = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (selection.Error is not null || selection.Instance is null)
    {
      var error = selection.Error ?? ConfigurationProviderSupport.Error(
          resource,
          WdemErrorCode.DependencyError,
          "The selected Visual Studio instance is unavailable.");
      return ConfigurationProviderSupport.Plan(resource, compliance.Status, false) with
      {
        Error = error.Detail,
        StructuredErrors = [error]
      };
    }

    var selectedInstance = selection.Instance;

    return ConfigurationProviderSupport.Plan(resource, compliance.Status, true) with
    {
      ExecutionPreconditionFingerprint = precondition,
      Steps =
      [
        new PlanStep
        {
          Id = CreateStepId(resource, selectedInstance),
          Description = "Import verified Visual Studio settings.",
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
      return ConfigurationProviderSupport.Failed(resource, validation.StructuredErrors[0]);
    }

    var planError = ConfigurationProviderSupport.ValidatePlan(
        resource,
        plan,
        step => HasValidStepId(resource, step.Id));
    if (planError is not null)
    {
      return ConfigurationProviderSupport.Failed(resource, planError);
    }

    var currentState = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!MatchesExecutionPrecondition(plan, currentState))
    {
      return ConfigurationProviderSupport.Failed(resource,
          ConfigurationProviderSupport.Error(
              resource,
              WdemErrorCode.ConfigurationError,
              "The Visual Studio settings destination changed after planning; the approved plan is stale."));
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult { ResourceId = resource.Id, Outcome = ApplyOutcome.NotRequired };
    }

    var destinationPrecondition = ConfigurationDestinationPrecondition.FromDetectedState(
        currentState,
        "settingsStorePath");
    if (destinationPrecondition is null)
    {
      return ConfigurationProviderSupport.Failed(resource,
          ConfigurationProviderSupport.Error(
              resource,
              WdemErrorCode.ConfigurationError,
              "The Visual Studio settings destination state cannot be committed safely."));
    }

    var selection = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (selection.Error is not null || selection.Instance is null)
    {
      return ConfigurationProviderSupport.Failed(resource, selection.Error ??
          ConfigurationProviderSupport.Error(resource, WdemErrorCode.DependencyError,
              "The selected Visual Studio instance is unavailable."));
    }

    if (!string.Equals(
            plan.Steps[0].Id,
            CreateStepId(resource, selection.Instance),
            StringComparison.Ordinal))
    {
      return ConfigurationProviderSupport.Failed(resource,
          ConfigurationProviderSupport.Error(resource, WdemErrorCode.DependencyError,
              "The selected Visual Studio instance changed after planning."));
    }

    var pathError = ValidateSettingsStorePath(resource, selection.Instance, out var settingsStorePath);
    if (pathError is not null)
    {
      return ConfigurationProviderSupport.Failed(resource, pathError);
    }

    progress?.Report(new ProviderProgress("Resolve", 0.2,
        "Verifying the Visual Studio settings source.", plan.Steps[0].Id));
    var source = await _sourceResolver.ResolveAsync(
        Get(resource, SourcePathParameter)!,
        Get(resource, ExpectedSha256Parameter)!,
        resource.ProfileSourcePath,
        cancellationToken).ConfigureAwait(false);
    if (!source.IsValid)
    {
      return ConfigurationProviderSupport.Failed(
          resource,
          source.Error! with { ResourceId = resource.Id });
    }

    var staged = await _importer.StageAsync(
        source.Source!,
        settingsStorePath,
        destinationPrecondition,
        cancellationToken).ConfigureAwait(false);
    if (!staged.Succeeded)
    {
      return ConfigurationProviderSupport.Failed(
          resource,
          staged.Error! with { ResourceId = resource.Id });
    }

    var snapshot = staged.Snapshot!;
    try
    {
      progress?.Report(new ProviderProgress("Apply", 0.7,
          "Importing Visual Studio settings.", plan.Steps[0].Id));
      var destinationBeforeLaunch = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
      if (!MatchesExecutionPrecondition(plan, destinationBeforeLaunch))
      {
        return ConfigurationProviderSupport.Failed(resource,
            ConfigurationProviderSupport.Error(
                resource,
                WdemErrorCode.ConfigurationError,
                "The Visual Studio settings destination changed after planning; the approved plan is stale."));
      }

      cancellationToken.ThrowIfCancellationRequested();
      var process = await _processExecutor.ExecuteAsync(
          new ProcessExecutionRequest(
              selection.Instance.ProductPath,
              ["/ResetSettings", snapshot.Path, "/Command", "Exit"],
              Timeout: ProcessExecutionRequest.DefaultTimeout)
          {
            CancellationMode = ProcessCancellationMode.LaunchOnly,
            OnStarted = () => progress?.Report(new ProviderProgress(
                "Apply",
                0.75,
                "Visual Studio settings import started.",
                plan.Steps[0].Id)
            {
              BeginsCancellationFinalization = true
            })
          },
          null,
          cancellationToken).ConfigureAwait(false);
      if (!process.Started || process.ExitCode != 0 || process.Error is not null)
      {
        var processError = (process.Error ??
            ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
                "devenv.exe did not import the Visual Studio settings successfully.")) with
        {
          ResourceId = resource.Id,
          ProcessExitCode = process.ExitCode
        };
        return ConfigurationProviderSupport.Failed(
            resource,
            processError,
            plan.Steps[0],
            process.ExitCode,
            finalizeAfterCancellation: process.Started);
      }

      using var finalization = new CancellationTokenSource(PostLaunchFinalizationTimeout);
      try
      {
        var destinationBeforeCommit = await DetectBoundInstanceAsync(
            resource,
            selection.Instance,
            settingsStorePath,
            source.Source!.Sha256,
            finalization.Token).ConfigureAwait(false);
        if (!MatchesExecutionPrecondition(plan, destinationBeforeCommit))
        {
          var error = ConfigurationProviderSupport.Error(
                  resource,
                  WdemErrorCode.ConfigurationError,
                  "The Visual Studio settings destination changed after planning; the approved plan is stale.");
          return ConfigurationProviderSupport.Failed(
              resource,
              error,
              plan.Steps[0],
              process.ExitCode,
              finalizeAfterCancellation: true);
        }

        // The hierarchy lease closes the directory redirection window. File-level CAS cannot
        // eliminate a final nanosecond race from a non-cooperating writer on Windows.
        var imported = await _importer.CommitStagedAsync(
            snapshot,
            settingsStorePath,
            finalization.Token).ConfigureAwait(false);
        if (!imported.Succeeded)
        {
          return ConfigurationProviderSupport.Failed(
              resource,
              imported.Error! with { ResourceId = resource.Id },
              plan.Steps[0],
              process.ExitCode,
              finalizeAfterCancellation: true);
        }

        var finalState = await DetectBoundInstanceAsync(
            resource,
            selection.Instance,
            settingsStorePath,
            source.Source!.Sha256,
            finalization.Token).ConfigureAwait(false);
        var verification = CreateVerification(resource, finalState);
        if (verification.Compliance != ComplianceStatus.Satisfied)
        {
          var error = verification.DetectedState.StructuredError ?? ConfigurationProviderSupport.Error(
                  resource,
                  WdemErrorCode.ConfigurationError,
                  $"The imported Visual Studio settings final verification returned {verification.Compliance}.");
          return ConfigurationProviderSupport.Failed(
              resource,
              error,
              plan.Steps[0],
              process.ExitCode,
              finalizeAfterCancellation: true,
              finalVerification: verification);
        }

        return ConfigurationProviderSupport.Succeeded(
            resource,
            plan.Steps[0],
            processExitCode: process.ExitCode,
            finalizeAfterCancellation: true) with
        {
          FinalVerification = verification
        };
      }
      catch (OperationCanceledException) when (finalization.IsCancellationRequested)
      {
        var error = ConfigurationProviderSupport.Error(
            resource,
            WdemErrorCode.VerificationError,
            "Visual Studio settings finalization exceeded its time limit.");
        var verification = CreateVerification(
            resource,
            ConfigurationProviderSupport.DetectionFailure(resource, error));
        return ConfigurationProviderSupport.Failed(
            resource,
            error,
            plan.Steps[0],
            process.ExitCode,
            finalizeAfterCancellation: true,
            finalVerification: verification);
      }
    }
    finally
    {
      snapshot.DeleteStagingFile();
      snapshot.Dispose();
    }
  }

  public async ValueTask<VerificationResult> VerifyAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    var state = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    return CreateVerification(resource, state);
  }

  private VerificationResult CreateVerification(
      ResourceDefinition resource,
      DetectedState state)
  {
    var compliance = Evaluate(resource, state);
    if (compliance.Error is { } complianceError && state.StructuredError is null)
    {
      state = state with
      {
        Error = complianceError.Detail,
        StructuredError = complianceError
      };
    }

    return new VerificationResult
    {
      ResourceId = resource.Id,
      Compliance = compliance.Status,
      DetectedState = state,
      Message = compliance.Status == ComplianceStatus.Satisfied ? null : compliance.Summary
    };
  }

  private static async Task<DetectedState> DetectBoundInstanceAsync(
      ResourceDefinition resource,
      VisualStudioInstance instance,
      string settingsStorePath,
      string sourceHash,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (ConfigurationImporter.ContainsReparsePoint(Path.GetDirectoryName(settingsStorePath)!))
    {
      return ConfigurationProviderSupport.DetectionFailure(resource,
          ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
              "The Visual Studio settings snapshot path contains an unsafe reparse point."));
    }

    if (!File.Exists(settingsStorePath))
    {
      return Missing(resource, instance, settingsStorePath, sourceHash);
    }

    try
    {
      var attributes = File.GetAttributes(settingsStorePath);
      if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
      {
        return ConfigurationProviderSupport.DetectionFailure(resource,
            ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
                "The Visual Studio settings snapshot is not a regular file."));
      }

      if (new FileInfo(settingsStorePath).Length > MaxSettingsStoreBytes)
      {
        return ConfigurationProviderSupport.DetectionFailure(resource,
            ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
                "The Visual Studio settings snapshot exceeds the 64 MiB size limit."));
      }

      var hash = await ConfigurationImporter.HashFileAsync(settingsStorePath, cancellationToken)
          .ConfigureAwait(false);
      return State(resource, instance, settingsStorePath, hash, sourceHash);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
    {
      return ConfigurationProviderSupport.DetectionFailure(resource,
          ConfigurationProviderSupport.Error(resource, WdemErrorCode.DetectionError,
              "The Visual Studio settings snapshot could not be safely read.", exception));
    }
  }

  private async Task<InstanceSelection> SelectInstanceAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    try
    {
      var instances = await _discovery.DiscoverAsync([], [], cancellationToken).ConfigureAwait(false);
      var selected = VisualStudioInstanceSelector.Select(
          instances,
          new VisualStudioInstanceCriteria(
              Get(resource, InstanceIdParameter),
              Get(resource, ProductIdParameter),
              Get(resource, EditionParameter),
              Get(resource, ChannelIdParameter)));
      var implicitSelectionIsIncompatible = Get(resource, InstanceIdParameter) is null &&
          selected.Instance is null &&
          !selected.IsAmbiguous &&
          selected.HasEligibleInstances;
      if (selected.IsIncompatible || implicitSelectionIsIncompatible)
      {
        return new InstanceSelection(null, ConfigurationProviderSupport.Error(
            resource,
            WdemErrorCode.ConfigurationError,
            "The selected Visual Studio instance does not match the configured product, edition, or channel."));
      }

      if (selected.IsAmbiguous)
      {
        return new InstanceSelection(null, ConfigurationProviderSupport.Error(
            resource, WdemErrorCode.ConfigurationError,
            $"Set parameter 'instanceId' to one of: {string.Join(", ", selected.CandidateInstanceIds)}."));
      }

      var instance = selected.Instance;
      if (instance is null)
      {
        return new InstanceSelection(null, null);
      }

      if (!IsExpectedDevenvPath(instance))
      {
        return new InstanceSelection(null, ConfigurationProviderSupport.Error(
            resource, WdemErrorCode.ConfigurationError,
            "The selected Visual Studio instance does not identify its expected devenv.exe executable."));
      }

      return new InstanceSelection(instance, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception)
    {
      return new InstanceSelection(null, ConfigurationProviderSupport.Error(
          resource, WdemErrorCode.DetectionError,
          "Visual Studio discovery failed while resolving the settings target.", exception));
    }
  }

  private Wdem.Core.Compliance.ComplianceResult Evaluate(
      ResourceDefinition resource,
      DetectedState state)
  {
    var expected = Get(resource, SettingsStoreSha256Parameter) ??
        Get(resource, ExpectedSha256Parameter);
    var parameters = resource.Parameters.ToDictionary(
        pair => pair.Key,
        pair => pair.Value,
        StringComparer.OrdinalIgnoreCase);
    parameters[ExpectedSha256Parameter] = expected;
    return _complianceEvaluator.Evaluate(resource with { Parameters = parameters }, state);
  }

  private StructuredError? ValidateSettingsStorePath(
      ResourceDefinition resource,
      VisualStudioInstance instance,
      out string settingsStorePath)
  {
    settingsStorePath = string.Empty;
    if (!IsSafeInstanceId(instance.InstanceId) ||
        !Version.TryParse(instance.InstallationVersion, out var installationVersion) ||
        installationVersion.Major <= 0)
    {
      return ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
          "The selected Visual Studio instance cannot identify a safe user settings directory.");
    }

    string root;
    try
    {
      root = Path.GetFullPath(_settingsDirectoryResolver(instance));
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      return ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
          "The selected Visual Studio user settings directory is invalid.", exception);
    }

    if (!Path.IsPathFullyQualified(root) || ConfigurationSourceResolver.HasAlternateDataStream(root))
    {
      return ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
          "The selected Visual Studio user settings directory is invalid.");
    }

    try
    {
      settingsStorePath = Path.GetFullPath(Path.IsPathFullyQualified(
          Get(resource, SettingsStorePathParameter)!)
              ? Get(resource, SettingsStorePathParameter)!
              : Path.Combine(root, Get(resource, SettingsStorePathParameter)!));
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      return ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
          "The selected Visual Studio settings snapshot path is invalid.", exception);
    }

    return ConfigurationSourceResolver.IsWithin(settingsStorePath, root)
        ? null
        : ConfigurationProviderSupport.Error(resource, WdemErrorCode.ConfigurationError,
            "Parameter 'settingsStorePath' must remain within the selected Visual Studio user settings directory.");
  }

  private static string DefaultSettingsDirectory(VisualStudioInstance instance)
  {
    _ = Version.TryParse(instance.InstallationVersion, out var version);
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "VisualStudio",
        $"{version?.Major ?? 0}.0_{instance.InstanceId}",
        "Settings");
  }

  private static bool IsSafeInstanceId(string? instanceId) =>
      !string.IsNullOrWhiteSpace(instanceId) &&
      !instanceId.Any(char.IsControl) &&
      !instanceId.Contains(Path.DirectorySeparatorChar) &&
      !instanceId.Contains(Path.AltDirectorySeparatorChar) &&
      instanceId is not "." and not ".." &&
      !ConfigurationSourceResolver.HasAlternateDataStream(instanceId);

  private static bool IsExpectedDevenvPath(VisualStudioInstance instance)
  {
    try
    {
      if (string.IsNullOrWhiteSpace(instance.InstallationPath) ||
          string.IsNullOrWhiteSpace(instance.ProductPath) ||
          instance.ProductPath.Any(char.IsControl) ||
          !Path.IsPathFullyQualified(instance.InstallationPath) ||
          !Path.IsPathFullyQualified(instance.ProductPath) ||
          ConfigurationSourceResolver.HasAlternateDataStream(instance.ProductPath))
      {
        return false;
      }

      var expected = Path.GetFullPath(Path.Combine(
          instance.InstallationPath,
          "Common7",
          "IDE",
          "devenv.exe"));
      return string.Equals(
          Path.GetFullPath(instance.ProductPath),
          expected,
          StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      return false;
    }
  }

  private static void RequireText(
      ResourceDefinition resource,
      string parameter,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    var value = Get(resource, parameter);
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
    {
      errors.Add((WdemErrorCode.ConfigurationError, $"Parameter '{parameter}' is required."));
    }
  }

  private static void ValidateOptionalText(
      ResourceDefinition resource,
      string parameter,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    if (resource.Parameters.ContainsKey(parameter) &&
        (Get(resource, parameter) is not { } value ||
         string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          $"Parameter '{parameter}' cannot be empty or contain control characters."));
    }
  }

  private static string? Get(ResourceDefinition resource, string parameter) =>
      ConfigurationProviderSupport.Get(resource, parameter);

  private static DetectedState Missing(
      ResourceDefinition resource,
      VisualStudioInstance instance,
      string path,
      string sourceHash) => new()
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false,
        Evidence = Evidence(instance, path, sourceHash, null)
      };

  private static DetectedState State(
      ResourceDefinition resource,
      VisualStudioInstance instance,
      string path,
      string hash,
      string sourceHash) => new()
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = true,
        ConfigurationHash = hash,
        Evidence = Evidence(instance, path, sourceHash, hash)
      };

  private static IReadOnlyDictionary<string, string> Evidence(
      VisualStudioInstance instance,
      string path,
      string sourceHash,
      string? destinationHash)
  {
    var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["sourceSha256"] = sourceHash,
      ["settingsStorePath"] = path,
      ["visualStudioInstanceId"] = instance.InstanceId,
      ["visualStudioProductId"] = instance.ProductId,
      ["visualStudioProductDisplayVersion"] = instance.ProductDisplayVersion,
      ["visualStudioInstallationVersion"] = instance.InstallationVersion,
      ["visualStudioInstallationPath"] = instance.InstallationPath,
      ["visualStudioProductPath"] = instance.ProductPath,
      ["visualStudioEdition"] = instance.Edition,
      ["visualStudioChannelId"] = instance.ChannelId,
      ["visualStudioIsComplete"] = instance.IsComplete.ToString(),
      ["visualStudioIsLaunchable"] = instance.IsLaunchable.ToString()
    };
    if (destinationHash is not null)
    {
      evidence["settingsStoreSha256"] = destinationHash;
    }

    return evidence;
  }

  private sealed record InstanceSelection(VisualStudioInstance? Instance, StructuredError? Error);

  private static string? CreateExecutionPrecondition(DetectedState state) =>
      ConfigurationExecutionPrecondition.FromDetectedState(
          state,
          "settingsStorePath",
          InstancePreconditionEvidenceKeys);

  private static bool MatchesExecutionPrecondition(
      ResourcePlan plan,
      DetectedState state) => ConfigurationExecutionPrecondition.Matches(
          plan,
          state,
          "settingsStorePath",
          InstancePreconditionEvidenceKeys);

  private static string CreateStepId(
      ResourceDefinition resource,
      VisualStudioInstance instance)
  {
    var identity = JsonSerializer.SerializeToUtf8Bytes(new VisualStudioSettingsPlanIdentity(
        instance.InstanceId,
        instance.ProductId,
        instance.InstallationPath,
        instance.ProductPath,
        instance.InstallationVersion));
    return $"{resource.Id}:configure:{Convert.ToHexString(SHA256.HashData(identity))}";
  }

  private static bool HasValidStepId(ResourceDefinition resource, string stepId)
  {
    var prefix = $"{resource.Id}:configure:";
    return stepId.StartsWith(prefix, StringComparison.Ordinal) &&
        stepId.Length == prefix.Length + 64 &&
        stepId[prefix.Length..].All(Uri.IsHexDigit);
  }

  private sealed record VisualStudioSettingsPlanIdentity(
      string InstanceId,
      string ProductId,
      string InstallationPath,
      string ProductPath,
      string InstallationVersion);
}
