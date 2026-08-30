using Wdem.Core.Providers;
using Wdem.Core.Resources;

namespace Wdem.Core.Planning;

public enum PlanStepAuthorizationViolation
{
  None,
  DeclarationRequiresElevation,
  DeclarationRequiresRestart,
  DeclarationIsDestructive,
  ActionNotAllowed,
  PrivilegeExceeded,
  RestartExceeded,
  DestructiveActionNotAllowed
}

public static class PlanStepAuthorizationPolicy
{
  public static bool IsSafeDeclaration(PlanStep step)
    => GetDeclarationViolation(step) == PlanStepAuthorizationViolation.None;

  public static PlanStepAuthorizationViolation GetDeclarationViolation(PlanStep step)
  {
    ArgumentNullException.ThrowIfNull(step);
    if (step.Action != PlanAction.None)
    {
      return PlanStepAuthorizationViolation.None;
    }

    if (step.PrivilegeRequirement != PrivilegeRequirement.CurrentUser)
    {
      return PlanStepAuthorizationViolation.DeclarationRequiresElevation;
    }

    if (step.RestartPolicy != RestartPolicy.NoRestart)
    {
      return PlanStepAuthorizationViolation.DeclarationRequiresRestart;
    }

    return step.IsDestructive
        ? PlanStepAuthorizationViolation.DeclarationIsDestructive
        : PlanStepAuthorizationViolation.None;
  }

  public static bool IsWithinBoundary(
      PlanStep step,
      IReadOnlyList<PlanAction> allowedActions,
      PrivilegeRequirement maximumPrivilege,
      RestartPolicy maximumRestartPolicy,
      bool allowDestructive)
    => GetBoundaryViolation(
        step,
        allowedActions,
        maximumPrivilege,
        maximumRestartPolicy,
        allowDestructive) == PlanStepAuthorizationViolation.None;

  public static PlanStepAuthorizationViolation GetBoundaryViolation(
      PlanStep step,
      IReadOnlyList<PlanAction> allowedActions,
      PrivilegeRequirement maximumPrivilege,
      RestartPolicy maximumRestartPolicy,
      bool allowDestructive)
  {
    ArgumentNullException.ThrowIfNull(step);
    ArgumentNullException.ThrowIfNull(allowedActions);
    if (step.Action == PlanAction.None)
    {
      return GetDeclarationViolation(step);
    }

    if (!allowedActions.Contains(step.Action))
    {
      return PlanStepAuthorizationViolation.ActionNotAllowed;
    }

    if (step.PrivilegeRequirement > maximumPrivilege)
    {
      return PlanStepAuthorizationViolation.PrivilegeExceeded;
    }

    if (step.RestartPolicy > maximumRestartPolicy)
    {
      return PlanStepAuthorizationViolation.RestartExceeded;
    }

    return step.IsDestructive && !allowDestructive
        ? PlanStepAuthorizationViolation.DestructiveActionNotAllowed
        : PlanStepAuthorizationViolation.None;
  }

  internal static PlanStepExecutionSummary Summarize(IReadOnlyList<PlanStep> steps)
  {
    ArgumentNullException.ThrowIfNull(steps);
    if (steps.Any(step => !IsSafeDeclaration(step)))
    {
      throw new ArgumentException(
          "Declaration steps cannot carry privilege, restart, or destructive requirements.",
          nameof(steps));
    }

    var modifyingSteps = steps.Where(step => step.Action != PlanAction.None).ToArray();
    var requiresElevation = modifyingSteps.Any(
        step => step.PrivilegeRequirement == PrivilegeRequirement.Administrator);
    var isDestructive = modifyingSteps.Any(step => step.IsDestructive);
    var restartPolicy = modifyingSteps
        .Select(step => step.RestartPolicy)
        .Append(RestartPolicy.NoRestart)
        .Max();
    var reason = modifyingSteps
        .Select(step => step.Reason)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    var risk = isDestructive
        ? PlanRisk.Destructive
        : requiresElevation
            ? PlanRisk.Elevated
            : modifyingSteps.Length > 0 ? PlanRisk.Standard : PlanRisk.None;

    return new PlanStepExecutionSummary(
        modifyingSteps.Length > 0,
        requiresElevation,
        isDestructive,
        restartPolicy,
        risk,
        reason);
  }
}

internal readonly record struct PlanStepExecutionSummary(
    bool RequiresApply,
    bool RequiresElevation,
    bool IsDestructive,
    RestartPolicy RestartPolicy,
    PlanRisk Risk,
    string? Reason);
