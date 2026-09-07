using Wdem.Domain.Workflows;

namespace Wdem.Application.Execution;

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
