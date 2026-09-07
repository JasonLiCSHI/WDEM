using Wdem.Domain.Profiles;

namespace Wdem.Infrastructure.Profiles;

internal static class ProfileValidator
{
  public static void Validate(EnvironmentProfile profile)
  {
    ArgumentNullException.ThrowIfNull(profile);

    foreach (var task in profile.Tasks.Values)
    {
      foreach (var dependencyId in task.DependsOn)
      {
        if (!profile.Tasks.ContainsKey(dependencyId))
        {
          throw new FormatException(
              $"Task '{task.Id}' depends on undeclared task '{dependencyId}'.");
        }
      }
    }
  }
}
