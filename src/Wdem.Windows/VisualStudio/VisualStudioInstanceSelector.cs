namespace Wdem.Windows.VisualStudio;

internal sealed record VisualStudioInstanceCriteria(
    string? InstanceId,
    string? ProductId,
    string? Edition,
    string? ChannelId);

internal sealed record VisualStudioInstanceSelection(
    VisualStudioInstance? Instance,
    IReadOnlyList<string> CandidateInstanceIds)
{
  public bool IsAmbiguous => CandidateInstanceIds.Count > 0;
}

internal static class VisualStudioInstanceSelector
{
  public static VisualStudioInstanceSelection Select(
      IReadOnlyList<VisualStudioInstance> instances,
      VisualStudioInstanceCriteria criteria)
  {
    ArgumentNullException.ThrowIfNull(instances);
    ArgumentNullException.ThrowIfNull(criteria);
    var candidates = instances
        .Where(instance => instance.IsComplete)
        .Where(instance => MatchesOptional(instance.ProductId, criteria.ProductId))
        .Where(instance => MatchesOptional(instance.Edition, criteria.Edition))
        .Where(instance => MatchesOptional(instance.ChannelId, criteria.ChannelId))
        .ToArray();
    var selected = criteria.InstanceId is null
        ? candidates
        : candidates.Where(instance => string.Equals(
            instance.InstanceId,
            criteria.InstanceId,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    if (selected.Length == 1)
    {
      return new VisualStudioInstanceSelection(selected[0], []);
    }

    var ambiguous = selected.Length > 1 ||
        criteria.InstanceId is null && candidates.Length > 1;
    return new VisualStudioInstanceSelection(
        null,
        ambiguous
            ? candidates
                .Select(instance => instance.InstanceId)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : []);
  }

  private static bool MatchesOptional(string actual, string? expected) =>
      expected is null || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
