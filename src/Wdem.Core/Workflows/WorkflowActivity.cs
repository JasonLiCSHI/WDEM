namespace Wdem.Core.Workflows;

/// <summary>
/// Base class for work executed while a task workflow enters, resides in, or exits a state.
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

  public abstract Task<WorkflowActivityResult> ExecuteAsync(
      WorkflowActivityContext context,
      CancellationToken cancellationToken);
}
