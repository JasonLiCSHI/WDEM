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
          if (string.IsNullOrWhiteSpace(value))
          {
            errors.Add(Error(
                $"Environment variable '{variableName}' is required by selected resource '{pair.Key}'.",
                $"/resources/{EscapePointer(pair.Key)}/parameters/{EscapePointer(parameter.Key)}"));
          }
          else if (string.Equals(variableName, "WDEM_COMPANY_VSIX_SHA256", StringComparison.Ordinal) &&
              (value.Length != 64 || !value.All(Uri.IsHexDigit)))
          {
            errors.Add(Error(
                $"Environment variable '{variableName}' must contain exactly 64 hexadecimal characters.",
                $"/resources/{EscapePointer(pair.Key)}/parameters/{EscapePointer(parameter.Key)}"));
          }
          else if (string.Equals(variableName, "WDEM_COMPANY_VSIX_PATH", StringComparison.Ordinal) &&
              !IsSafeCompanyVsixSource(value))
          {
            errors.Add(Error(
                $"Environment variable '{variableName}' must be an absolute local path or safe HTTPS URI.",
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

  private static bool IsSafeCompanyVsixSource(string value)
  {
    if (value.Any(char.IsControl))
    {
      return false;
    }

    if (Path.IsPathFullyQualified(value))
    {
      return string.Equals(Path.GetExtension(value), ".vsix", StringComparison.OrdinalIgnoreCase);
    }

    return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.Equals(Path.GetExtension(uri.AbsolutePath), ".vsix", StringComparison.OrdinalIgnoreCase);
  }

  internal static string EscapePointer(string segment) =>
      segment.Replace("~", "~0", StringComparison.Ordinal)
          .Replace("/", "~1", StringComparison.Ordinal);

  [GeneratedRegex(@"\A\$\{(?<name>WDEM_[A-Z0-9_]+)\}\z", RegexOptions.CultureInvariant)]
  private static partial Regex WdemTokenPattern();
}
