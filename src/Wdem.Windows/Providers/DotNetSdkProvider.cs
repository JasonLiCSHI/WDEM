using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Windows.Providers;

public sealed class DotNetSdkProvider : IResourceProvider
{
  public const string PackageId = "Microsoft.DotNet.SDK.10";

  private readonly IProcessExecutor _processExecutor;
  private readonly IComplianceEvaluator _complianceEvaluator;
  private readonly WinGetCommandClient _winGet;

  public DotNetSdkProvider(
      IProcessExecutor processExecutor,
      IComplianceEvaluator complianceEvaluator)
      : this(processExecutor, new WinGetCommandClient(processExecutor), complianceEvaluator)
  {
  }

  public DotNetSdkProvider(
      IProcessExecutor processExecutor,
      WinGetCommandClient winGet,
      IComplianceEvaluator complianceEvaluator)
  {
    _processExecutor = processExecutor ?? throw new ArgumentNullException(nameof(processExecutor));
    _winGet = winGet ?? throw new ArgumentNullException(nameof(winGet));
    _complianceEvaluator =
        complianceEvaluator ?? throw new ArgumentNullException(nameof(complianceEvaluator));
  }

  public string ResourceType => "dotnet-sdk";
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
      errors.Add("Resource type must be 'dotnet-sdk'.");
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
        new ProcessExecutionRequest("dotnet", ["--list-sdks"]),
        null,
        cancellationToken).ConfigureAwait(false);
    if (result.Error is not null)
    {
      return ProviderLifecycleSupport.DetectionFailure(
          resource,
          result,
          ".NET SDK detection failed.",
          "The dotnet process did not complete successfully.",
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

    if (result.ExitCode == 0 && result.StandardOutput.All(string.IsNullOrWhiteSpace))
    {
      return new DetectedState
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Succeeded,
        Exists = false,
        Evidence = Evidence(result)
      };
    }

    if (result.ExitCode != 0 ||
        !CommandVersionParser.TryParseDotNetSdks(
            result.StandardOutput,
            out var detectedVersions,
            out var versions))
    {
      var error = new StructuredError(
          WdemErrorCode.DetectionError,
          ".NET SDK detection failed.",
          "The launched dotnet command did not return a parseable SDK list.")
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
      Version = detectedVersions[^1],
      InstalledVersions = versions,
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
      Steps =
      [
        new PlanStep
        {
          Id = $"{resource.Id}:install",
          Description = "Install .NET SDK with WinGet.",
          Action = compliance.Status == ComplianceStatus.Missing
              ? PlanAction.Install
              : PlanAction.Upgrade,
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
    progress?.Report(new ProviderProgress("Detect", 0, "Using planned .NET SDK detection.", step.Id));
    progress?.Report(new ProviderProgress("Plan", 0.25, "Confirming the .NET SDK package source.", step.Id));
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

    progress?.Report(new ProviderProgress("Apply", 0.5, "Installing .NET SDK.", step.Id));
    var command = await _winGet.InstallAsync(
        resource.Id,
        step.Id,
        PackageId,
        resource.PreferredVersion,
        null,
        null,
        cancellationToken).ConfigureAwait(false);
    progress?.Report(new ProviderProgress("Verify", 0.75, "Verifying .NET SDK.", step.Id));
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
    var detected = await DetectAsync(resource, cancellationToken).ConfigureAwait(false);
    var compliance = _complianceEvaluator.Evaluate(resource, detected);
    return new VerificationResult
    {
      ResourceId = resource.Id,
      Compliance = compliance.Status,
      DetectedState = detected,
      Message = compliance.Status == ComplianceStatus.Satisfied ? null : compliance.Summary
    };
  }

  private static IReadOnlyDictionary<string, string> Evidence(ProcessExecutionResult result) =>
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
        ["command"] = "dotnet --list-sdks",
        ["started"] = result.Started.ToString(),
        ["exitCode"] = result.ExitCode?.ToString() ?? "unknown"
      };

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
                ".NET SDK resource validation failed.",
                detail)
            {
              ResourceId = resource.Id
            }).ToArray()
          };
}
