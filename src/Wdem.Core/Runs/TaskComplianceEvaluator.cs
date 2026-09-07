using System.Text.RegularExpressions;
using Wdem.Core.Tasks;
using Wdem.Domain.Versions;

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
      return new TaskComplianceEvaluation(ComplianceStatus.Missing, detectedVersion);
    }

    if (task.VersionRequirement is null)
    {
      return new TaskComplianceEvaluation(ComplianceStatus.Satisfied, detectedVersion);
    }

    var candidate = string.IsNullOrWhiteSpace(detectionCommand.VersionPattern)
        ? detectStep.Stdout
        : detectedVersion;

    var compliance = task.VersionRequirement.Evaluate(candidate ?? string.Empty);
    return new TaskComplianceEvaluation(compliance.Status, detectedVersion);
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
    ComplianceStatus State,
    string? DetectedVersion);
