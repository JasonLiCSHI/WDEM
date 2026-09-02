using System.Text.RegularExpressions;

namespace Wdem.Core.Profiles;

public sealed record ProfileSourceDefinition
{
  private static readonly Regex IdPattern = new(
      "^[A-Za-z0-9][A-Za-z0-9._-]*$",
      RegexOptions.CultureInvariant);

  public ProfileSourceDefinition(string id, string displayName, string baseUrl)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(id);
    ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
    ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

    if (!IdPattern.IsMatch(id))
    {
      throw new ArgumentException(
          "Profile Source id may contain only letters, numbers, '.', '_' and '-'.",
          nameof(id));
    }

    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
        !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException(
          "Profile Source baseUrl must be an absolute HTTPS URL.",
          nameof(baseUrl));
    }

    Id = id;
    DisplayName = displayName;
    BaseUrl = uri.AbsoluteUri.EndsWith('/')
        ? uri.AbsoluteUri
        : uri.AbsoluteUri + '/';
  }

  public string Id { get; }

  public string DisplayName { get; }

  public string BaseUrl { get; }

  public Uri BaseUri => new(BaseUrl, UriKind.Absolute);
}
