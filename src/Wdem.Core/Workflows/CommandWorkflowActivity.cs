using Wdem.Core.Runs;
using Wdem.Core.Tasks;

namespace Wdem.Core.Workflows;

public sealed class CommandWorkflowActivity : WorkflowActivity
{
  public CommandWorkflowActivity(
      string id,
      string phase,
      CommandDefinition command,
      string? displayName = null)
      : base(id, displayName ?? command.DisplayName ?? phase)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(phase);
    ArgumentNullException.ThrowIfNull(command);
    Phase = phase;
    Command = command;
  }

  public string Phase { get; }

  public CommandDefinition Command { get; }

  public override async Task<WorkflowActivityResult> ExecuteAsync(
      WorkflowActivityContext context,
      CancellationToken cancellationToken)
  {
    var result = await context.RunCommandAsync(Phase, Command, cancellationToken);
    var step = new StepReport(Phase, result.ExitCode, result.Stdout, result.Stderr)
    {
      ActivityId = Id,
      RuntimeStateId = context.StateId,
      ActivityLocation = context.Location
    };
    var isTaskSatisfied = TaskComplianceEvaluator.Evaluate(context.Task, Command, step).State ==
        TaskComplianceState.Satisfied;
    var activityResult = result.ExitCode == 0
        ? WorkflowActivityResult.Success(step)
        : WorkflowActivityResult.Failure(
            $"Activity '{Id}' failed with exit code {result.ExitCode}.",
            step);
    return activityResult with { IsTaskSatisfied = isTaskSatisfied };
  }
}
