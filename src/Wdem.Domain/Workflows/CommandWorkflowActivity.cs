using Wdem.Domain.Tasks;

namespace Wdem.Domain.Workflows;

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
}
