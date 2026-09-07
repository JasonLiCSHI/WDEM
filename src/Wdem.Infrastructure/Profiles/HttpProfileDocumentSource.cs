using Wdem.Domain.Profiles;

namespace Wdem.Infrastructure.Profiles;

internal sealed class HttpProfileDocumentSource(
    ProfileSourceDefinition source,
    HttpClient httpClient,
    int maxDocumentBytes)
{
  public async Task<string> ReadAsync(
      string relativePath,
      CancellationToken cancellationToken)
  {
    var uri = new Uri(source.BaseUri, relativePath);
    using var response = await httpClient.GetAsync(
        uri,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    EnsureHttps(response.RequestMessage?.RequestUri ?? uri);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength > maxDocumentBytes)
    {
      throw ProfileDocumentReader.TooLarge(relativePath, maxDocumentBytes);
    }

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    return await ProfileDocumentReader.ReadAsync(
        stream,
        relativePath,
        maxDocumentBytes,
        cancellationToken);
  }

  private static void EnsureHttps(Uri? uri)
  {
    if (uri is null ||
        !uri.IsAbsoluteUri ||
        !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
      throw new NotSupportedException("Profile Source requests and redirects must use HTTPS.");
    }
  }
}
