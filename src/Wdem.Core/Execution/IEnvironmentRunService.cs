using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public interface IEnvironmentRunService
{
  Task<ExecutionRun> InspectAsync(RunRequest request, CancellationToken cancellationToken);
  Task<ExecutionRun> ApplyAsync(RunRequest request, CancellationToken cancellationToken);
  Task<ExecutionRun> RetryAsync(
      Guid priorRunId,
      IReadOnlySet<string> resourceIds,
      CancellationToken cancellationToken);
  Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
      CancellationToken cancellationToken);
  Task<ExecutionRun> RecoverAsync(Guid priorRunId, CancellationToken cancellationToken);
  Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken);
}
