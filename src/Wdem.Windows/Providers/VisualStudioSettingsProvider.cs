using System.Security;
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
  public const string SourcePathParameter = "sourcePath";
  public const string ExpectedSha256Parameter = "expectedSha256";
  public const string SettingsStorePathParameter = "settingsStorePath";
  public const string InstanceIdParameter = "instanceId";
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
    ConcurrencyGroup = "visual-studio"
  };

  public ValueTask<ProviderValidationResult> ValidateAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(resource);
    var errors = new List<(WdemErrorCode Code, string Detail)>();
    if (!ReSharperSettingsProvider.Matches(resource.Type, ResourceType))
    {
      errors.Add((WdemErrorCode.ProviderError,
          "Resource type must be 'visual-studio-settings'."));
    }

    if (!ReSharperSettingsProvider.Matches(resource.Provider, ProviderName))
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

    ReSharperSettingsProvider.ValidateFileParameter(
        resource, SourcePathParameter, ".vssettings", requireAbsolute: false, errors);
    ReSharperSettingsProvider.ValidateFileParameter(
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

    RequireText(resource, InstanceIdParameter, errors);
    RequireText(resource, EditionParameter, errors);
    RequireText(resource, ChannelIdParameter, errors);
    ReSharperSettingsProvider.AddUnsupportedParameters(
        resource,
        errors,
        SourcePathParameter,
        ExpectedSha256Parameter,
        SettingsStorePathParameter,
        InstanceIdParameter,
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
    return ValueTask.FromResult(ReSharperSettingsProvider.ToValidation(
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
      return ReSharperSettingsProvider.DetectionFailure(resource, validation.StructuredErrors[0]);
    }

    var selection = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (selection.Error is not null)
    {
      return ReSharperSettingsProvider.DetectionFailure(resource, selection.Error);
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
      return ReSharperSettingsProvider.DetectionFailure(resource, pathError);
    }

    if (ConfigurationImporter.ContainsReparsePoint(Path.GetDirectoryName(settingsStorePath)!))
    {
      return ReSharperSettingsProvider.DetectionFailure(resource,
          ReSharperSettingsProvider.Error(resource, WdemErrorCode.ConfigurationError,
              "The Visual Studio settings snapshot path contains an unsafe reparse point."));
    }

    if (!File.Exists(settingsStorePath))
    {
      return Missing(resource, selection.Instance, settingsStorePath);
    }

    try
    {
      var attributes = File.GetAttributes(settingsStorePath);
      if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
      {
        return ReSharperSettingsProvider.DetectionFailure(resource,
            ReSharperSettingsProvider.Error(resource, WdemErrorCode.ConfigurationError,
                "The Visual Studio settings snapshot is not a regular file."));
      }

      var hash = await ConfigurationImporter.HashFileAsync(settingsStorePath, cancellationToken)
          .ConfigureAwait(false);
      return State(resource, selection.Instance, settingsStorePath, hash);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
    {
      return ReSharperSettingsProvider.DetectionFailure(resource,
          ReSharperSettingsProvider.Error(resource, WdemErrorCode.DetectionError,
              "The Visual Studio settings snapshot could not be safely read.", exception));
    }
  }

  public async ValueTask<ResourcePlan> PlanAsync(
      ResourceDefinition resource,
      DetectedState currentState,
      CancellationToken cancellationToken)
  {
    var validation = await ValidateAsync(resource, cancellationToken).ConfigureAwait(false);
    if (!validation.IsValid)
    {
      return ReSharperSettingsProvider.Plan(resource, ComplianceStatus.DetectionFailed, false) with
      {
        Error = validation.StructuredErrors[0].Detail,
        StructuredErrors = validation.StructuredErrors
      };
    }

    var compliance = Evaluate(resource, currentState);
    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return ReSharperSettingsProvider.Plan(resource, compliance.Status, true);
    }

    if (compliance.Status is ComplianceStatus.DetectionFailed or ComplianceStatus.Unsupported)
    {
      return ReSharperSettingsProvider.Plan(resource, compliance.Status, false) with
      {
        Error = compliance.Error?.Detail,
        StructuredErrors = compliance.Error is null ? [] : [compliance.Error]
      };
    }

    return ReSharperSettingsProvider.Plan(resource, compliance.Status, true) with
    {
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:configure",
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
      return ReSharperSettingsProvider.Failed(resource, validation.StructuredErrors[0]);
    }

    var planError = ReSharperSettingsProvider.ValidatePlan(resource, plan);
    if (planError is not null)
    {
      return ReSharperSettingsProvider.Failed(resource, planError);
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult { ResourceId = resource.Id, Outcome = ApplyOutcome.NotRequired };
    }

    var selection = await SelectInstanceAsync(resource, cancellationToken).ConfigureAwait(false);
    if (selection.Error is not null || selection.Instance is null)
    {
      return ReSharperSettingsProvider.Failed(resource, selection.Error ??
          ReSharperSettingsProvider.Error(resource, WdemErrorCode.DependencyError,
              "The selected Visual Studio instance is unavailable."));
    }

    var pathError = ValidateSettingsStorePath(resource, selection.Instance, out var settingsStorePath);
    if (pathError is not null)
    {
      return ReSharperSettingsProvider.Failed(resource, pathError);
    }

    progress?.Report(new ProviderProgress("Resolve", 0.2,
        "Verifying the Visual Studio settings source.", plan.Steps[0].Id));
    var source = await _sourceResolver.ResolveAsync(
        Get(resource, SourcePathParameter)!,
        Get(resource, ExpectedSha256Parameter)!,
        cancellationToken).ConfigureAwait(false);
    if (!source.IsValid)
    {
      return ReSharperSettingsProvider.Failed(
          resource,
          source.Error! with { ResourceId = resource.Id });
    }

    var imported = await _importer.CopyAtomicallyAsync(
        source.Source!, settingsStorePath, cancellationToken).ConfigureAwait(false);
    if (!imported.Succeeded)
    {
      return ReSharperSettingsProvider.Failed(
          resource,
          imported.Error! with { ResourceId = resource.Id });
    }

    progress?.Report(new ProviderProgress("Apply", 0.7,
        "Importing Visual Studio settings.", plan.Steps[0].Id));
    var process = await _processExecutor.ExecuteAsync(
        new ProcessExecutionRequest(
            selection.Instance.ProductPath,
            ["/Command", "File.ImportSettings", settingsStorePath]),
        null,
        cancellationToken).ConfigureAwait(false);
    if (!process.Started || process.ExitCode != 0 || process.Error is not null)
    {
      return ReSharperSettingsProvider.Failed(resource, (process.Error ??
          ReSharperSettingsProvider.Error(resource, WdemErrorCode.ConfigurationError,
              "devenv.exe did not import the Visual Studio settings successfully.")) with
      {
        ResourceId = resource.Id,
        ProcessExitCode = process.ExitCode
      });
    }

    var verification = await VerifyAsync(resource, cancellationToken).ConfigureAwait(false);
    if (verification.Compliance != ComplianceStatus.Satisfied)
    {
      return ReSharperSettingsProvider.Failed(resource,
          verification.DetectedState.StructuredError ??
          ReSharperSettingsProvider.Error(resource, WdemErrorCode.VerificationError,
              "The Visual Studio settings snapshot did not verify."));
    }

    return ReSharperSettingsProvider.Succeeded(resource, plan.Steps[0]);
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
      var instanceId = Get(resource, InstanceIdParameter)!;
      var idMatches = instances.Where(instance =>
          instance.IsComplete && ReSharperSettingsProvider.Matches(instance.InstanceId, instanceId)).ToArray();
      if (idMatches.Length > 1)
      {
        return new InstanceSelection(null, ReSharperSettingsProvider.Error(
            resource, WdemErrorCode.ConfigurationError,
            "More than one Visual Studio instance has the selected instance ID."));
      }

      var instance = idMatches.SingleOrDefault();
      if (instance is null)
      {
        return new InstanceSelection(null, null);
      }

      if (!ReSharperSettingsProvider.Matches(instance.Edition, Get(resource, EditionParameter)) ||
          !ReSharperSettingsProvider.Matches(instance.ChannelId, Get(resource, ChannelIdParameter)))
      {
        return new InstanceSelection(null, ReSharperSettingsProvider.Error(
            resource, WdemErrorCode.ConfigurationError,
            "The selected Visual Studio instance has an incompatible edition or channel."));
      }

      if (!IsExpectedDevenvPath(instance))
      {
        return new InstanceSelection(null, ReSharperSettingsProvider.Error(
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
      return new InstanceSelection(null, ReSharperSettingsProvider.Error(
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
    string root;
    try
    {
      root = Path.GetFullPath(_settingsDirectoryResolver(instance));
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
    {
      return ReSharperSettingsProvider.Error(resource, WdemErrorCode.ConfigurationError,
          "The selected Visual Studio user settings directory is invalid.", exception);
    }

    settingsStorePath = Path.GetFullPath(Path.IsPathFullyQualified(
        Get(resource, SettingsStorePathParameter)!)
            ? Get(resource, SettingsStorePathParameter)!
            : Path.Combine(root, Get(resource, SettingsStorePathParameter)!));

    return ConfigurationSourceResolver.IsWithin(settingsStorePath, root)
        ? null
        : ReSharperSettingsProvider.Error(resource, WdemErrorCode.ConfigurationError,
            "Parameter 'settingsStorePath' must remain within the selected Visual Studio user settings directory.");
  }

  private static string DefaultSettingsDirectory(VisualStudioInstance instance)
  {
    var major = instance.InstallationVersion.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        $"Visual Studio {major ?? instance.ProductDisplayVersion}");
  }

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

  private static string? Get(ResourceDefinition resource, string parameter) =>
      ReSharperSettingsProvider.Get(resource, parameter);

  private static DetectedState Missing(
      ResourceDefinition resource,
      VisualStudioInstance instance,
      string path) => new()
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false,
        Evidence = Evidence(resource, instance, path, null)
      };

  private static DetectedState State(
      ResourceDefinition resource,
      VisualStudioInstance instance,
      string path,
      string hash) => new()
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = true,
        ConfigurationHash = hash,
        Evidence = Evidence(resource, instance, path, hash)
      };

  private static IReadOnlyDictionary<string, string> Evidence(
      ResourceDefinition resource,
      VisualStudioInstance instance,
      string path,
      string? destinationHash)
  {
    var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
      ["sourceSha256"] = Get(resource, ExpectedSha256Parameter)!.ToUpperInvariant(),
      ["settingsStorePath"] = path,
      ["visualStudioInstanceId"] = instance.InstanceId,
      ["visualStudioEdition"] = instance.Edition,
      ["visualStudioChannelId"] = instance.ChannelId
    };
    if (destinationHash is not null)
    {
      evidence["settingsStoreSha256"] = destinationHash;
    }

    return evidence;
  }

  private sealed record InstanceSelection(VisualStudioInstance? Instance, StructuredError? Error);
}
