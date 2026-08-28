using Wdem.Core.Execution;

namespace Wdem.Core.Runs;

public interface IExecutionRunStore
{
  IReadOnlyList<StructuredError> Diagnostics { get; }

  Task CreateAsync(ExecutionRun run, CancellationToken cancellationToken);
  Task<ExecutionRun?> GetAsync(Guid runId, CancellationToken cancellationToken);
  Task<IReadOnlyList<ExecutionRun>> ListAsync(CancellationToken cancellationToken);
  Task<IReadOnlyList<ExecutionRun>> ListIncompleteAsync(CancellationToken cancellationToken);
  Task<IAsyncDisposable?> TryAcquireRecoveryOperationAsync(
      Guid runId,
      CancellationToken cancellationToken);
  Task<ExecutionRun> SaveAsync(ExecutionRun run, CancellationToken cancellationToken);
  Task<bool> TrySaveAsync(
      ExecutionRun run,
      long expectedRevision,
      Guid? expectedRecoveryClaimId,
      CancellationToken cancellationToken);
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
