namespace Wdem.Domain.Workflows;

public sealed class FailureWorkflowActivity : WorkflowActivity
{
  public FailureWorkflowActivity(string id, string error, string? displayName = null)
      : base(id, displayName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(error);
    Error = error;
  }

  public string Error { get; }
}
