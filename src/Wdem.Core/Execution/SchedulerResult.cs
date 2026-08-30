using System.Collections.Frozen;
using Wdem.Core.Runs;

namespace Wdem.Core.Execution;

public sealed record SchedulerResult
{
  private IReadOnlyDictionary<string, ResourceResult> _results =
      new Dictionary<string, ResourceResult>(StringComparer.OrdinalIgnoreCase)
          .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

  public required IReadOnlyDictionary<string, ResourceResult> Results
  {
    get => _results;
    init
    {
      ArgumentNullException.ThrowIfNull(value);
      _results = value.ToFrozenDictionary(
          pair => pair.Key,
          pair => pair.Value,
          StringComparer.OrdinalIgnoreCase);
    }
  }

  public Task UndrainedCompletion { get; init; } = Task.CompletedTask;
}
