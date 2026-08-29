using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Processes;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Windows.Providers;

internal static class ProviderLifecycleSupport
{
  public static ResourceApplyResult? RejectInvalidResource(
      ResourceDefinition resource,
      ProviderValidationResult validation)
  {
    if (validation.IsValid)
    {
      return null;
    }

    var error = validation.StructuredErrors.FirstOrDefault() ?? new StructuredError(
        WdemErrorCode.ProviderError,
        "Resource validation failed.",
        validation.Errors.FirstOrDefault() ?? "The resource is invalid for this provider.")
    {
      ResourceId = resource.Id
    };
    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Failed,
      Error = error,
      Diagnostics = validation.StructuredErrors.Count == 0
          ? [error]
          : validation.StructuredErrors
    };
  }

  public static ResourceApplyResult? RejectInvalidPlan(
      ResourceDefinition resource,
      ResourcePlan plan,
      string resourceType,
      string providerName,
      Func<ResourceDefinition, PlanStep, bool>? stepIdentityValidator = null)
  {
    var detail = GetPlanMismatch(
        resource,
        plan,
        resourceType,
        providerName,
        stepIdentityValidator);
    if (detail is null)
    {
      return null;
    }

    var error = new StructuredError(
        WdemErrorCode.ProviderError,
        "Resource plan cannot be applied.",
        detail)
    {
      ResourceId = resource.Id
    };
    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Failed,
      Error = error,
      Diagnostics = [error]
    };
  }

  public static ResourceApplyResult Failure(
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

  public static ResourceApplyResult CompleteAfterVerification(
      ResourceDefinition resource,
      PlanStep step,
      WinGetCommandResult command,
      VerificationResult verification,
      IComplianceEvaluator complianceEvaluator,
      StructuredError fallbackInstallationError)
  {
    var succeeded = verification.Compliance == ComplianceStatus.Satisfied;
    if (succeeded)
    {
      return new ResourceApplyResult
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded,
        Diagnostics = command.Error is null ? [] : [command.Error],
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = step.Id,
            Action = step.Action,
            Progress = 1,
            ProcessExitCode = command.Process.ExitCode
          }
        ]
      };
    }

    var compliance = complianceEvaluator.Evaluate(resource, verification.DetectedState);
    var error = compliance.Error ?? command.Error ?? fallbackInstallationError;
    var diagnostics = new List<StructuredError> { error };
    if (command.Error is not null && !Equals(command.Error, error))
    {
      diagnostics.Add(command.Error);
    }

    return new ResourceApplyResult
    {
      ResourceId = resource.Id,
      Outcome = ApplyOutcome.Failed,
      Error = error,
      Diagnostics = diagnostics,
      StepResults =
      [
        new ProviderStepResult
        {
          StepId = step.Id,
          Action = step.Action,
          Progress = 0.75,
          ProcessExitCode = command.Process.ExitCode,
          Error = error
        }
      ]
    };
  }

  public static DetectedState DetectionFailure(
      ResourceDefinition resource,
      ProcessExecutionResult result,
      string summary,
      string detail,
      IReadOnlyDictionary<string, string> evidence)
  {
    var error = result.Error is null
        ? new StructuredError(
            WdemErrorCode.DetectionError,
            summary,
            detail)
        {
          ResourceId = resource.Id,
          ProcessExitCode = result.ExitCode
        }
        : result.Error with
        {
          ResourceId = result.Error.ResourceId ?? resource.Id,
          ProcessExitCode = result.Error.ProcessExitCode ?? result.ExitCode
        };
    return new DetectedState
    {
      ResourceId = resource.Id,
      Outcome = DetectionOutcome.Failed,
      Error = error.Detail,
      StructuredError = error,
      Evidence = evidence
    };
  }

  private static string? GetPlanMismatch(
      ResourceDefinition resource,
      ResourcePlan plan,
      string resourceType,
      string providerName,
      Func<ResourceDefinition, PlanStep, bool>? stepIdentityValidator)
  {
    if (!plan.IsExecutable)
    {
      return "The plan is not executable.";
    }

    if (!Matches(resource.Type, resourceType) || !Matches(resource.Provider, providerName))
    {
      return "The resource type or provider does not match this provider.";
    }

    if (!Matches(plan.ResourceId, resource.Id) ||
        !Matches(plan.ResourceType, resourceType) ||
        !Matches(plan.ProviderName, providerName))
    {
      return "The plan belongs to a different resource, type, or provider.";
    }

    if (!string.Equals(
            plan.DesiredStateFingerprint,
            ResourceDefinitionFingerprint.Create(resource),
            StringComparison.Ordinal))
    {
      return "The plan is stale because the desired resource changed.";
    }

    if (!plan.RequiresApply)
    {
      return plan.Compliance == ComplianceStatus.Satisfied && plan.Steps.Count == 0
          ? null
          : "The plan has no applicable step for its compliance state.";
    }

    if (plan.Steps.Count != 1)
    {
      return "The plan must contain exactly one installation step.";
    }

    var expectedAction = plan.Compliance switch
    {
      ComplianceStatus.Missing => PlanAction.Install,
      ComplianceStatus.VersionMismatch => PlanAction.Upgrade,
      _ => PlanAction.None
    };
    var step = plan.Steps[0];
    var validStepIdentity = stepIdentityValidator?.Invoke(resource, step) ??
        string.Equals(step.Id, $"{resource.Id}:install", StringComparison.Ordinal);
    if (expectedAction == PlanAction.None ||
        !validStepIdentity ||
        step.Action != expectedAction ||
        step.PrivilegeRequirement != resource.PrivilegeRequirement ||
        step.RestartPolicy != resource.RestartPolicy ||
        step.IsDestructive)
    {
      return "The installation step does not match the current resource or compliance state.";
    }

    return null;
  }

  private static bool Matches(string left, string right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
