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
    var valid = string.Equals(resource.Type, ResourceType, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(resource.Provider, ProviderName, StringComparison.OrdinalIgnoreCase);
    return ValueTask.FromResult(valid
        ? ProviderValidationResult.Valid
        : ProviderValidationResult.Invalid("Resource type and provider must be 'git' and 'winget'."));
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
    if (result.ExitCode != 0 || !CommandVersionParser.TryParseGit(output, out var version))
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

    var normalized = $"{version.Major}.{version.Minor}.{version.Patch}";
    return new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Succeeded,
      Exists = true,
      Version = normalized,
      InstalledVersions = [version],
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
        cancellationToken).ConfigureAwait(false);
    progress?.Report(new ProviderProgress("Verify", 0.75, "Verifying Git.", step.Id));
    var verification = await VerifyAsync(resource, cancellationToken).ConfigureAwait(false);
    var succeeded = verification.Compliance == ComplianceStatus.Satisfied;
    var error = succeeded
        ? null
        : command.Error ?? _winGet.CreateInstallationError(
            resource.Id,
            step.Id,
            PackageId,
            command.Process.ExitCode);

    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = succeeded ? ApplyOutcome.Succeeded : ApplyOutcome.Failed,
      Error = error,
      Diagnostics = command.Error is null ? [] : [command.Error],
      StepResults =
      [
        new ProviderStepResult
        {
          StepId = step.Id,
          Action = step.Action,
          Progress = succeeded ? 1 : 0.75,
          ProcessExitCode = command.Process.ExitCode,
          Error = error
        }
      ]
    };
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
      double progress) => new()
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Failed,
        Error = error,
        Diagnostics = [error],
        StepResults =
    [
      new ProviderStepResult
      {
        StepId = step.Id,
        Action = step.Action,
        Progress = progress,
        ProcessExitCode = exitCode,
        Error = error
      }
    ]
      };

  private static IReadOnlyDictionary<string, string> Evidence(ProcessExecutionResult result) =>
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["command"] = "git --version",
        ["started"] = result.Started.ToString(),
        ["exitCode"] = result.ExitCode?.ToString() ?? "unknown"
      };
}
