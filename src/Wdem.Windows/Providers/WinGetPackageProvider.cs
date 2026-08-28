using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Windows.Providers;

public sealed class WinGetPackageProvider : IResourceProvider
{
  public const string PackageIdParameter = "packageId";
  public const string SourceParameter = "source";
  private const int PackageNotFoundExitCode = unchecked((int)0x8A150014);

  private readonly WinGetCommandClient _winGet;
  private readonly IComplianceEvaluator _complianceEvaluator;

  public WinGetPackageProvider(
      IProcessExecutor processExecutor,
      IComplianceEvaluator complianceEvaluator,
      string? logLocation = null)
      : this(new WinGetCommandClient(processExecutor, logLocation), complianceEvaluator)
  {
  }

  public WinGetPackageProvider(
      WinGetCommandClient winGet,
      IComplianceEvaluator complianceEvaluator)
  {
    _winGet = winGet ?? throw new ArgumentNullException(nameof(winGet));
    _complianceEvaluator =
        complianceEvaluator ?? throw new ArgumentNullException(nameof(complianceEvaluator));
  }

  public string ResourceType => "winget-package";
  public string ProviderName => "winget";
  public ProviderCapabilities Capabilities { get; } = new()
  {
    SupportsSource = true,
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
      errors.Add($"Resource type '{resource.Type}' is not supported.");
    }

    if (!string.Equals(resource.Provider, ProviderName, StringComparison.OrdinalIgnoreCase))
    {
      errors.Add($"Resource provider '{resource.Provider}' is not supported.");
    }

    if (!TryGetPackageId(resource, out _))
    {
      errors.Add($"Parameter '{PackageIdParameter}' is required.");
    }

    if (resource.Parameters.TryGetValue(SourceParameter, out var source) &&
        string.IsNullOrWhiteSpace(source))
    {
      errors.Add($"Parameter '{SourceParameter}' cannot be empty.");
    }

    foreach (var parameter in resource.Parameters.Keys.Where(parameter =>
                 !string.Equals(parameter, PackageIdParameter, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(parameter, SourceParameter, StringComparison.OrdinalIgnoreCase)))
    {
      errors.Add($"Parameter '{parameter}' is not supported.");
    }

    return ValueTask.FromResult(errors.Count == 0
        ? ProviderValidationResult.Valid
        : new ProviderValidationResult
        {
          Errors = errors,
          StructuredErrors = errors.Select(detail => new StructuredError(
              WdemErrorCode.ProviderError,
              "WinGet package validation failed.",
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
    if (!TryGetPackageId(resource, out var packageId))
    {
      return DetectionFailure(resource, "The packageId parameter is missing.");
    }

    var source = GetOptionalParameter(resource, SourceParameter);
    var result = await _winGet.ListAsync(packageId, source, cancellationToken).ConfigureAwait(false);
    var evidence = CommandEvidence("winget list", result);
    if (result.Error is not null)
    {
      return ProviderLifecycleSupport.DetectionFailure(
          resource,
          result,
          "WinGet package detection failed.",
          "The WinGet process did not complete successfully.",
          evidence);
    }

    if (!result.Started || result.ExitCode == PackageNotFoundExitCode)
    {
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false,
        Evidence = evidence
      };
    }

    if (result.ExitCode != 0)
    {
      return ProviderLifecycleSupport.DetectionFailure(
          resource,
          result,
          "WinGet package detection failed.",
          "The WinGet command returned an unexpected nonzero exit code.",
          evidence);
    }

    if (!CommandVersionParser.TryParseWinGetList(
            result.StandardOutput,
            packageId,
            out var detectedVersion,
            out var comparableVersion))
    {
      return DetectionFailure(
          resource,
          "The launched WinGet command did not return a parseable package row.",
          result.ExitCode,
          evidence);
    }

    return new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = detectedVersion,
      InstalledVersions = comparableVersion is null ? [] : [comparableVersion.Value],
      Evidence = evidence
    };
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
      return BlockedPlan(resource, ComplianceStatus.DetectionFailed, validation.StructuredErrors);
    }

    var compliance = _complianceEvaluator.Evaluate(resource, currentState);
    if (compliance.Status == ComplianceStatus.Satisfied)
    {
      return BasePlan(resource, compliance.Status, isExecutable: true);
    }

    if (compliance.Status is ComplianceStatus.DetectionFailed or ComplianceStatus.Unsupported)
    {
      return BlockedPlan(
          resource,
          compliance.Status,
          compliance.Error is null ? [] : [compliance.Error]);
    }

    var packageId = resource.Parameters[PackageIdParameter]!;
    var source = await _winGet.QueryAvailabilityAsync(
        resource.Id,
        packageId,
        resource.PreferredVersion,
        GetOptionalParameter(resource, SourceParameter),
        cancellationToken).ConfigureAwait(false);
    if (source.Error is not null)
    {
      return BlockedPlan(resource, compliance.Status, [source.Error]);
    }

    var action = compliance.Status == ComplianceStatus.Missing
        ? PlanAction.Install
        : PlanAction.Upgrade;
    return BasePlan(resource, compliance.Status, isExecutable: true) with
    {
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:install",
          Description = $"Install {resource.DisplayName ?? resource.Id} with WinGet.",
          Action = action,
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
    ArgumentNullException.ThrowIfNull(resource);
    ArgumentNullException.ThrowIfNull(plan);
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
        ProviderName);
    if (invalidPlan is not null)
    {
      return invalidPlan;
    }

    if (!plan.RequiresApply)
    {
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.NotRequired
      };
    }

    var packageId = resource.Parameters[PackageIdParameter]!;
    var step = plan.Steps[0];
    progress?.Report(new ProviderProgress("Detect", 0, "Using planned detection state.", step.Id));
    progress?.Report(new ProviderProgress("Plan", 0.25, "Confirming WinGet source availability.", step.Id));
    var source = await _winGet.QueryAvailabilityAsync(
        resource.Id,
        packageId,
        resource.PreferredVersion,
        GetOptionalParameter(resource, SourceParameter),
        cancellationToken).ConfigureAwait(false);
    if (source.Error is not null)
    {
      return Failure(resource, step, source.Error, source.Process.ExitCode, 0.25);
    }

    progress?.Report(new ProviderProgress("Apply", 0.5, $"Installing {packageId}.", step.Id));
    var command = await _winGet.InstallAsync(
        resource.Id,
        step.Id,
        packageId,
        resource.PreferredVersion,
        GetOptionalParameter(resource, SourceParameter),
        null,
        cancellationToken).ConfigureAwait(false);
    progress?.Report(new ProviderProgress("Verify", 0.75, $"Verifying {packageId}.", step.Id));
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
            packageId,
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

  private static ResourceApplyResult Failure(
      ResourceDefinition resource,
      PlanStep step,
      StructuredError error,
      int? exitCode,
      double progress) => ProviderLifecycleSupport.Failure(
          resource,
          step,
          error,
          exitCode,
          progress);

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

  private static ResourcePlan BlockedPlan(
      ResourceDefinition resource,
      ComplianceStatus compliance,
      IReadOnlyList<StructuredError> errors) => BasePlan(resource, compliance, false) with
      {
        Error = errors.FirstOrDefault()?.Detail ?? "The resource cannot be planned.",
        StructuredErrors = errors
      };

  private static DetectedState DetectionFailure(
      ResourceDefinition resource,
      string detail,
      int? exitCode = null,
      IReadOnlyDictionary<string, string>? evidence = null)
  {
    var error = new StructuredError(
        WdemErrorCode.DetectionError,
        "WinGet package detection failed.",
        detail)
    {
      ResourceId = resource.Id,
      ProcessExitCode = exitCode
    };
    return new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Failed,
      Error = error.Detail,
      StructuredError = error,
      Evidence = evidence ?? new Dictionary<string, string>()
    };
  }

  private static IReadOnlyDictionary<string, string> CommandEvidence(
      string command,
      ProcessExecutionResult result) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["command"] = command,
        ["started"] = result.Started.ToString(),
        ["exitCode"] = result.ExitCode?.ToString() ?? "unknown"
      };

  private static bool TryGetPackageId(ResourceDefinition resource, out string packageId)
  {
    if (resource.Parameters.TryGetValue(PackageIdParameter, out var value) &&
        !string.IsNullOrWhiteSpace(value))
    {
      packageId = value;
      return true;
    }

    packageId = string.Empty;
    return false;
  }

  private static string? GetOptionalParameter(ResourceDefinition resource, string name) =>
      resource.Parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
          ? value
          : null;
}
