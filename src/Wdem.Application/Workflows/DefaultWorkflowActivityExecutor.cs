using Wdem.Application.Execution;
using Wdem.Application.Inspection;
using Wdem.Domain.Versions;
using Wdem.Domain.Workflows;

namespace Wdem.Application.Workflows;

public sealed class DefaultWorkflowActivityExecutor : IWorkflowActivityExecutor
{
  public static DefaultWorkflowActivityExecutor Instance { get; } = new();

  public async Task<WorkflowActivityResult> ExecuteAsync(
      WorkflowActivity activity,
      WorkflowActivityContext context,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(activity);
    ArgumentNullException.ThrowIfNull(context);

    return activity switch
    {
      CommandWorkflowActivity command =>
          await ExecuteCommandAsync(command, context, cancellationToken),
      FailureWorkflowActivity failure => WorkflowActivityResult.Failure(failure.Error),
      _ => WorkflowActivityResult.Failure(
          $"No Activity executor is registered for '{activity.GetType().FullName}'.")
    };
  }

  private static async Task<WorkflowActivityResult> ExecuteCommandAsync(
      CommandWorkflowActivity activity,
      WorkflowActivityContext context,
      CancellationToken cancellationToken)
  {
    var result = await context.RunCommandAsync(
        activity.Phase,
        activity.Command,
        cancellationToken);
    var step = new StepReport(
        activity.Phase,
        result.ExitCode,
        result.Stdout,
        result.Stderr)
    {
      ActivityId = activity.Id,
      RuntimeStateId = context.StateId,
      ActivityLocation = context.Location
    };
    var compliance = TaskComplianceEvaluator.Evaluate(
        context.Task,
        activity.Command,
        step).State;
    var isDetection =
        activity.Phase.Equals("detect", StringComparison.OrdinalIgnoreCase) ||
        activity.Phase.Equals("verify", StringComparison.OrdinalIgnoreCase);
    var activitySucceeded = result.ExitCode == 0 ||
        (isDetection && compliance == ComplianceStatus.Missing);
    var activityResult = activitySucceeded
        ? WorkflowActivityResult.Success(step)
        : WorkflowActivityResult.Failure(
            $"Activity '{activity.Id}' failed with exit code {result.ExitCode}.",
            step);
    return activityResult with { IsTaskSatisfied = compliance == ComplianceStatus.Satisfied };
  }
}
