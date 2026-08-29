using Wdem.Core.Providers;

namespace Wdem.Windows.Execution;

internal static class PrivilegePlanSegments
{
  public static IReadOnlyList<ResourcePlan> Split(ResourcePlan plan)
  {
    ArgumentNullException.ThrowIfNull(plan);
    if (plan.Steps.Count == 0)
    {
      return [plan];
    }

    var segments = new List<ResourcePlan>();
    var start = 0;
    for (var index = 1; index <= plan.Steps.Count; index++)
    {
      if (index < plan.Steps.Count &&
          plan.Steps[index].PrivilegeRequirement ==
              plan.Steps[start].PrivilegeRequirement)
      {
        continue;
      }

      segments.Add(plan with
      {
        Steps = plan.Steps.Skip(start).Take(index - start).ToArray()
      });
      start = index;
    }

    return segments;
  }
}
