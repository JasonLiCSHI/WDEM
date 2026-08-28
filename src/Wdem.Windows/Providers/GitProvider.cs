using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Windows.Providers;

public sealed class GitProvider : IResourceProvider
{
  public const string PackageId = "Git.Git";

  private readonly IProcessExecutor _processExecutor;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly WinGetCommandClient _winGet;

  public GitProvider(IProcessExecutor processExecutor, IComplianceEvaluator complianceEvaluator)
      : this(processExecutor, new WinGetCommandClient(processExecutor), complianceEvaluator)
  {
  }

  public GitProvider(
      IProcessExecutor processExecutor,
      WinGetCommandClient winGet,
      IComplianceEvaluator complianceEvaluator)
  {
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _winGet = winGet ?? throw new ArgumentNullException(nameof(winGet));
    _complianceEvaluator =
        complianceEvaluator ?? throw new ArgumentNullException(nameof(complianceEvaluator));
  }

  public string ResourceType => "git";
  public string ProviderName => "winget";
  public ProviderCapabilities Capabilities { get; } = new()
  {
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
      errors.Add("Resource type must be 'git'.");
    }

    if (!string.Equals(resource.Provider, ProviderName, StringComparison.OrdinalIgnoreCase))
    {
      errors.Add("Resource provider must be 'winget'.");
    }

    errors.AddRange(resource.Parameters.Keys.Select(
        parameter => $"Parameter '{parameter}' is not supported."));
    return ValueTask.FromResult(Validation(resource, errors));
  }

  public async ValueTask<DetectedState> DetectAsync(
      ResourceDefinition resource,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(resource);
    var result = await _processExecutor.ExecuteAsync(
        new ProcessExecutionRequest("git", ["--version"]),
        null,
        cancellationToken).ConfigureAwait(false);

    if (result.Error is not null)
    {
      return ProviderLifecycleSupport.DetectionFailure(
          resource,
          result,
          "Git version detection failed.",
          "The Git process did not complete successfully.",
          Evidence(result));
    }

    if (!result.Started)
    {
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false,
        Evidence = Evidence(result)
      };
    }

    var output = string.Join(Environment.NewLine, result.StandardOutput);
    if (result.ExitCode != 0 ||
        !CommandVersionParser.TryParseGit(output, out var detectedVersion, out var version))
    {
      var error = new StructuredError(
          WdemErrorCode.DetectionError,
          "Git version detection failed.",
          "The launched Git command did not return a parseable version.")
      {
        ResourceId = resource.Id,
        ProcessExitCode = result.ExitCode
      };
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Failed,
        Error = error.Detail,
        StructuredError = error,
        Evidence = Evidence(result)
      };
    }

    return new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = detectedVersion,
      InstalledVersions = version is null ? [] : [version.Value],
      Evidence = Evidence(result)
    };
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
      return Plan(resource, ComplianceStatus.DetectionFailed, false) with
      {
        Error = validation.StructuredErrors.FirstOrDefault()?.Detail,
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

    var source = await _winGet.QueryAvailabilityAsync(
        resource.Id,
        PackageId,
        resource.PreferredVersion,
        null,
        cancellationToken).ConfigureAwait(false);
    if (source.Error is not null)
    {
      return Plan(resource, compliance.Status, false) with
      {
        Error = source.Error.Detail,
        StructuredErrors = [source.Error]
      };
    }

    return Plan(resource, compliance.Status, true) with
    {
      Steps = [InstallStep(resource, compliance.Status)]
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
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.NotRequired
      };
    }

    var step = plan.Steps[0];
    progress?.Report(new ProviderProgress("Detect", 0, "Using planned Git detection.", step.Id));
    progress?.Report(new ProviderProgress("Plan", 0.25, "Confirming the Git package source.", step.Id));
    var source = await _winGet.QueryAvailabilityAsync(
        resource.Id,
        PackageId,
        resource.PreferredVersion,
        null,
        cancellationToken).ConfigureAwait(false);
    if (source.Error is not null)
    {
      return ApplyFailure(resource, step, source.Error, source.Process.ExitCode, 0.25);
    }

    progress?.Report(new ProviderProgress("Apply", 0.5, "Installing Git.", step.Id));
    var command = await _winGet.InstallAsync(
        resource.Id,
        step.Id,
        PackageId,
        resource.PreferredVersion,
        null,
        null,
        cancellationToken).ConfigureAwait(false);
    progress?.Report(new ProviderProgress("Verify", 0.75, "Verifying Git.", step.Id));
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

  private static ResourcePlan Plan(
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

  private static PlanStep InstallStep(
      ResourceDefinition resource,
      ComplianceStatus compliance) => new()
      {
        Id = $"{resource.Id}:install",
        Description = "Install Git with WinGet.",
        Action = compliance == ComplianceStatus.Missing ? PlanAction.Install : PlanAction.Upgrade,
        PrivilegeRequirement = resource.PrivilegeRequirement,
        RestartPolicy = resource.RestartPolicy
      };

  private static ResourceApplyResult ApplyFailure(
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

  private static ProviderValidationResult Validation(
      ResourceDefinition resource,
      IReadOnlyList<string> errors) => errors.Count == 0
          ? ProviderValidationResult.Valid
          : new ProviderValidationResult
          {
            Errors = errors,
            StructuredErrors = errors.Select(detail => new StructuredError(
                WdemErrorCode.ProviderError,
                "Git resource validation failed.",
                detail)
            {
              ResourceId = resource.Id
            }).ToArray()
          };

  private static IReadOnlyDictionary<string, string> Evidence(ProcessExecutionResult result) =>
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["command"] = "git --version",
        ["started"] = result.Started.ToString(),
        ["exitCode"] = result.ExitCode?.ToString() ?? "unknown"
      };
}
