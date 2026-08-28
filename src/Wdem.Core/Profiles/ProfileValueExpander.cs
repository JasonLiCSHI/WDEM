using System.Text.RegularExpressions;
using Wdem.Core.Execution;
using Wdem.Core.Resources;

namespace Wdem.Core.Profiles;

public static partial class ProfileValueExpander
{
  public static ProfileExpansionResult ExpandSelected(
      DeveloperProfile profile,
      IEnumerable<string> selectedResourceIds,
      Func<string, string?>? environmentVariableReader = null)
  {
    ArgumentNullException.ThrowIfNull(profile);
    ArgumentNullException.ThrowIfNull(selectedResourceIds);
    environmentVariableReader ??= Environment.GetEnvironmentVariable;

    var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var errors = new List<StructuredError>();
    foreach (var required in profile.RequiredResources)
    {
      AddClosure(required.Id, profile, selected, errors);
    }

    foreach (var id in selectedResourceIds)
    {
      AddClosure(id, profile, selected, errors);
    }

    var resources = new Dictionary<string, ResourceDefinition>(StringComparer.OrdinalIgnoreCase);
    foreach (var pair in profile.Resources)
    {
      if (!selected.Contains(pair.Key))
      {
        resources[pair.Key] = pair.Value;
        continue;
      }

      var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
      foreach (var parameter in pair.Value.Parameters)
      {
        var value = parameter.Value;
        var match = value is null ? null : WdemTokenPattern().Match(value);
        if (match is not null && match.Success)
        {
          var variableName = match.Groups["name"].Value;
          value = environmentVariableReader(variableName);
          if (value is null)
          {
            errors.Add(Error(
                $"Environment variable '{variableName}' is required by selected resource '{pair.Key}'.",
                $"/resources/{EscapePointer(pair.Key)}/parameters/{EscapePointer(parameter.Key)}"));
          }
        }

        parameters[parameter.Key] = value ?? parameter.Value;
      }

      resources[pair.Key] = pair.Value with { Parameters = parameters };
    }

    return new ProfileExpansionResult
    {
      Profile = profile with { Resources = resources },
      Errors = errors
    };
  }

  private static void AddClosure(
      string id,
      DeveloperProfile profile,
      HashSet<string> selected,
      List<StructuredError> errors)
  {
    var pending = new Stack<string>();
    pending.Push(id);
    while (pending.Count > 0)
    {
      var currentId = pending.Pop();
      if (!selected.Add(currentId))
      {
        continue;
      }

      if (!profile.Resources.TryGetValue(currentId, out var resource))
      {
        errors.Add(Error(
            $"Selected resource '{currentId}' does not exist in the profile.",
            $"/resources/{EscapePointer(currentId)}"));
        continue;
      }

      for (var index = resource.Dependencies.Count - 1; index >= 0; index--)
      {
        pending.Push(resource.Dependencies[index]);
      }
    }
  }

  private static StructuredError Error(string summary, string pointer) => new(
      WdemErrorCode.ProfileError,
      summary,
      $"Profile value expansion failed at '{pointer}': {summary}");

  internal static string EscapePointer(string segment) =>
      segment.Replace("~", "~0", StringComparison.Ordinal)
          .Replace("/", "~1", StringComparison.Ordinal);

  [GeneratedRegex(@"\A\$\{(?<name>WDEM_[A-Z0-9_]+)\}\z", RegexOptions.CultureInvariant)]
  private static partial Regex WdemTokenPattern();
}
