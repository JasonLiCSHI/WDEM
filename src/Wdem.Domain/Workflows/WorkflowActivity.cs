namespace Wdem.Domain.Workflows;

/// <summary>
/// Describes work attached to a workflow state. Execution belongs to an
/// Application-layer <c>IWorkflowActivityExecutor</c>.
/// </summary>
public abstract class WorkflowActivity
{
  protected WorkflowActivity(string id, string? displayName = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    Id = id;
    DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName;
  }

  public string Id { get; }

  public string DisplayName { get; }
}
