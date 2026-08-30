using Wdem.Core.Compliance;
using Wdem.Core.Execution;
using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Windows.Configuration;

internal static class ConfigurationProviderSupport
{
  internal static ProviderValidationResult ToValidation(
      ResourceDefinition resource,
      string displayName,
      IReadOnlyList<(WdemErrorCode Code, string Detail)> errors) => errors.Count == 0
          ? ProviderValidationResult.Valid
          : new ProviderValidationResult
          {
            Errors = errors.Select(error => error.Detail).ToArray(),
            StructuredErrors = errors.Select(error => new StructuredError(
                error.Code,
                $"{displayName} resource validation failed.",
                error.Detail)
            {
              ResourceId = resource.Id
            }).ToArray()
          };

  internal static void ValidateFileParameter(
      ResourceDefinition resource,
      string parameter,
      string extension,
      bool requireAbsolute,
      ICollection<(WdemErrorCode Code, string Detail)> errors)
  {
    var value = Get(resource, parameter);
    var isAbsolutePath = !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value);
    Uri? uri = null;
    var isAbsoluteUri = !isAbsolutePath && Uri.TryCreate(value, UriKind.Absolute, out uri);
    var isFileUri = isAbsoluteUri && uri!.IsFile;
    var path = isFileUri ? uri!.LocalPath : value ?? string.Empty;
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ||
        (isAbsoluteUri && !isFileUri) ||
        !Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) ||
        (requireAbsolute && !Path.IsPathFullyQualified(path)))
    {
      errors.Add((WdemErrorCode.ConfigurationError,
          $"Parameter '{parameter}' must identify a{(requireAbsolute ? "n absolute" : string.Empty)} '{extension}' file path."));
    }
  }

  internal static void AddUnsupportedParameters(
      ResourceDefinition resource,
      ICollection<(WdemErrorCode Code, string Detail)> errors,
      params string[] supported)
  {
    var supportedSet = supported.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var parameter in resource.Parameters.Keys.Where(key => !supportedSet.Contains(key)))
    {
      errors.Add((WdemErrorCode.ProviderError, $"Parameter '{parameter}' is not supported."));
    }
  }

  internal static string? Get(ResourceDefinition resource, string parameter) =>
      resource.Parameters.TryGetValue(parameter, out var value) ? value : null;

  internal static bool Matches(string? left, string? right) =>
      string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

  internal static StructuredError Error(
      ResourceDefinition resource,
      WdemErrorCode code,
      string detail,
      Exception? exception = null) => new(code, "Configuration operation failed.", detail)
      {
        ResourceId = resource.Id,
        UnderlyingException = exception
      };

  internal static DetectedState DetectionFailure(
      ResourceDefinition resource,
      StructuredError error) => new()
      {
        ResourceId = resource.Id,
        Outcome = DetectionOutcome.Failed,
        Error = error.Detail,
        StructuredError = error.ResourceId is null ? error with { ResourceId = resource.Id } : error
      };

  internal static ResourcePlan Plan(
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

  internal static StructuredError? ValidatePlan(
      ResourceDefinition resource,
      ResourcePlan plan,
      Func<PlanStep, bool>? validateStep = null)
  {
    if (!plan.IsExecutable || !Matches(plan.ResourceId, resource.Id) ||
        !Matches(plan.ResourceType, resource.Type) || !Matches(plan.ProviderName, resource.Provider) ||
        !string.Equals(plan.DesiredStateFingerprint, ResourceDefinitionFingerprint.Create(resource), StringComparison.Ordinal))
    {
      return Error(resource, WdemErrorCode.ProviderError,
          "The approved configuration plan is invalid or stale.");
    }

    if (!plan.RequiresApply)
    {
      return plan.Compliance == ComplianceStatus.Satisfied && plan.Steps.Count == 0
          ? null
          : Error(resource, WdemErrorCode.ProviderError,
              "The configuration plan has no valid action.");
    }

    return plan.Steps.Count == 1 &&
        (validateStep?.Invoke(plan.Steps[0]) ?? plan.Steps[0].Id == $"{resource.Id}:configure") &&
        plan.Steps[0].Action == PlanAction.Configure &&
        plan.Steps[0].PrivilegeRequirement == resource.PrivilegeRequirement &&
        plan.Steps[0].RestartPolicy == resource.RestartPolicy && !plan.Steps[0].IsDestructive
          ? null
          : Error(resource, WdemErrorCode.ProviderError,
              "The configuration plan step is invalid.");
  }

  internal static ResourceApplyResult Failed(
      ResourceDefinition resource,
      StructuredError error,
      PlanStep? step = null,
      int? processExitCode = null,
      bool finalizeAfterCancellation = false) => new()
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Failed,
        FinalizeAfterCancellation = finalizeAfterCancellation,
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
                Progress = 0.75,
                ProcessExitCode = processExitCode,
                Succeeded = false,
                Error = error
              }
            ]
      };

  internal static ResourceApplyResult Succeeded(
      ResourceDefinition resource,
      PlanStep step,
      int? processExitCode = null,
      bool finalizeAfterCancellation = false) => new()
      {
        ResourceId = resource.Id,
        Outcome = ApplyOutcome.Succeeded,
        FinalizeAfterCancellation = finalizeAfterCancellation,
        StepResults =
        [
          new ProviderStepResult
          {
            StepId = step.Id,
            Action = step.Action,
            Progress = 1,
            ProcessExitCode = processExitCode,
            Succeeded = true
          }
        ]
      };
}
