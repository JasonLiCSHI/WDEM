namespace Wdem.Windows.VisualStudio;

internal sealed record VisualStudioInstanceCriteria(
    string? InstanceId,
    string? ProductId,
    string? Edition,
    string? ChannelId);

internal sealed record VisualStudioInstanceSelection(
    VisualStudioInstance? Instance,
    IReadOnlyList<string> CandidateInstanceIds,
    bool IsIncompatible = false)
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
    var completeInstances = instances
        .Where(instance => instance.IsComplete && instance.IsLaunchable)
        .ToArray();
    if (criteria.InstanceId is not null)
    {
      var idMatches = completeInstances
          .Where(instance => string.Equals(
              instance.InstanceId,
              criteria.InstanceId,
              StringComparison.OrdinalIgnoreCase))
          .ToArray();
      if (idMatches.Length == 0)
      {
        return new VisualStudioInstanceSelection(null, []);
      }

      if (idMatches.Length > 1)
      {
        return new VisualStudioInstanceSelection(
            null,
            idMatches
                .Select(instance => instance.InstanceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray());
      }

      var explicitInstance = idMatches[0];
      return MatchesCriteria(explicitInstance, criteria)
          ? new VisualStudioInstanceSelection(explicitInstance, [])
          : new VisualStudioInstanceSelection(null, [], IsIncompatible: true);
    }

    var candidates = completeInstances
        .Where(instance => MatchesOptional(instance.ProductId, criteria.ProductId))
        .Where(instance => MatchesOptional(instance.Edition, criteria.Edition))
        .Where(instance => MatchesOptional(instance.ChannelId, criteria.ChannelId))
        .ToArray();
    if (candidates.Length == 1)
    {
      return new VisualStudioInstanceSelection(candidates[0], []);
    }

    return new VisualStudioInstanceSelection(
        null,
        candidates.Length > 1
            ? candidates
                .Select(instance => instance.InstanceId)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : []);
  }

  private static bool MatchesCriteria(
      VisualStudioInstance instance,
      VisualStudioInstanceCriteria criteria) =>
      MatchesOptional(instance.ProductId, criteria.ProductId) &&
      MatchesOptional(instance.Edition, criteria.Edition) &&
      MatchesOptional(instance.ChannelId, criteria.ChannelId);

  private static bool MatchesOptional(string actual, string? expected) =>
      expected is null || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
