using System.Text.RegularExpressions;
using Wdem.Core.Tasks;
using Wdem.Core.Versions;

namespace Wdem.Core.Runs;

internal static class TaskComplianceEvaluator
{
  public static TaskComplianceEvaluation Evaluate(TaskDefinition task, StepReport detectStep)
      => Evaluate(task, task.Detect, detectStep);

  public static TaskComplianceEvaluation Evaluate(
      TaskDefinition task,
      CommandDefinition detectionCommand,
      StepReport detectStep)
  {
    ArgumentNullException.ThrowIfNull(task);
    ArgumentNullException.ThrowIfNull(detectionCommand);
    ArgumentNullException.ThrowIfNull(detectStep);

    var detectedVersion = ExtractVersion(detectionCommand.VersionPattern, detectStep.Stdout);
    if (detectStep.ExitCode != 0)
    {
      return new TaskComplianceEvaluation(TaskComplianceState.Missing, detectedVersion);
    }

    if (string.IsNullOrWhiteSpace(task.VersionConstraint))
    {
      return new TaskComplianceEvaluation(TaskComplianceState.Satisfied, detectedVersion);
    }

    var constraint = VersionConstraint.Parse(task.VersionConstraint);
    var candidate = string.IsNullOrWhiteSpace(detectionCommand.VersionPattern)
        ? detectStep.Stdout
        : detectedVersion;

    if (!string.IsNullOrWhiteSpace(candidate) && constraint.IsSatisfiedBy(candidate))
    {
      return new TaskComplianceEvaluation(TaskComplianceState.Satisfied, detectedVersion);
    }

    var state = !string.IsNullOrWhiteSpace(candidate) && constraint.IsBelowMinimum(candidate)
        ? TaskComplianceState.UpgradeRequired
        : TaskComplianceState.VersionMismatch;
    return new TaskComplianceEvaluation(state, detectedVersion);
  }

  private static string? ExtractVersion(string? versionPattern, string stdout)
  {
    if (string.IsNullOrWhiteSpace(versionPattern))
    {
      return null;
    }

    var match = Regex.Match(stdout ?? string.Empty, versionPattern, RegexOptions.CultureInvariant);
    return match.Success ? match.Groups["version"].Value : null;
  }
}

internal readonly record struct TaskComplianceEvaluation(
    TaskComplianceState State,
    string? DetectedVersion);
