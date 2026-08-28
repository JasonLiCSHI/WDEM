using System.Collections.Frozen;

namespace Wdem.Core.Runs;

public sealed record RecoveryCandidate
{
  private IReadOnlySet<string> _pendingResourceIds =
      Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

  public required Guid RunId { get; init; }
  public required string ProfileSourcePath { get; init; }
  public required DateTimeOffset StartedAtUtc { get; init; }
  public required IReadOnlySet<string> PendingResourceIds
  {
    get => _pendingResourceIds;
    init => _pendingResourceIds = (value ?? throw new ArgumentNullException(nameof(value)))
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
  }
}
