namespace Wdem.Core.Runs;

using Wdem.Core.Workflows;

public sealed record StepReport(
    string Phase,
    int ExitCode,
    string Stdout,
    string Stderr)
{
  public string? RuntimeStateId { get; init; }

  public string? ActivityId { get; init; }

  public WorkflowActivityLocation? ActivityLocation { get; init; }
}
