using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public interface IEnvironmentRunService
{
  Task<ExecutionRun> InspectAsync(RunRequest request, CancellationToken cancellationToken);
  Task<ExecutionRun> RetryAsync(
      Guid priorRunId,
      IReadOnlySet<string> resourceIds,
      CancellationToken cancellationToken);
  Task<IReadOnlyList<RecoveryCandidate>> FindRecoveryCandidatesAsync(
      CancellationToken cancellationToken);
  Task<ExecutionRun> RecoverAsync(Guid priorRunId, CancellationToken cancellationToken);
  Task AbandonAsync(Guid priorRunId, CancellationToken cancellationToken);
}

public interface IEnvironmentRunFinalizationService
{
  /// <summary>
  /// Waits for a tracked run finalization when that registration is still discoverable.
  /// </summary>
  /// <remarks>
  /// Finalization tracking is bounded and best-effort. If the registration has been evicted,
  /// this method returns the latest durable run snapshot. That snapshot can be provisional and
  /// does not prove that detached finalization has completed.
  /// </remarks>
  Task<ExecutionRun> WaitForRunFinalizationAsync(
      Guid runId,
      CancellationToken cancellationToken);
}

public interface ICommandLineEnvironmentRunService : IEnvironmentRunService
{
  Task<ExecutionRun> ApplyAsync(RunRequest request, CancellationToken cancellationToken);
}

public interface IReviewedPlanEnvironmentRunService : IEnvironmentRunService
{
  Task<ExecutionRun> ApplyAsync(
      RunRequest request,
      string reviewedPlanFingerprint,
      CancellationToken cancellationToken);
}
