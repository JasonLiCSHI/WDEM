using Wdem.Core.Execution;

namespace Wdem.Core.Runs;

public interface IExecutionRunStore
{
  IReadOnlyList<StructuredError> Diagnostics { get; }

  Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken);
  Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken);
  Task<IReadOnlyList<ExecutionRun>> ListAsync(CancellationToken cancellationToken);
  Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(CancellationToken cancellationToken);
  Task SaveAsync(ExecutionRun run, CancellationToken cancellationToken);
  Task AppendLogAsync(
      Guid runId,
      RunLogEntry entry,
      CancellationToken cancellationToken);
  Task<IReadOnlyList<RunLogEntry>> ReadLogPageAsync(
      Guid runId,
      long afterSequence,
      int take,
      CancellationToken cancellationToken);
}
