using Wdem.Core.Graph;
using Wdem.Core.Providers;

namespace Wdem.Core.Planning;

public interface IExecutionPlanner
{
  Task<ExecutionPlan> CreateAsync(
      ResourceGraph graph,
      IReadOnlyDictionary<string, DetectedState> detectedStates,
      string profileId,
      string profileVersion,
      CancellationToken cancellationToken);
}
