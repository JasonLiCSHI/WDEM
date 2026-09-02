using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Wdem.Core.Profiles;

/// <summary>
/// Presents one remote Profile Source as a catalog and transparently maintains
/// a last-known-good local cache for offline use.
/// </summary>
public sealed class ProfileCatalog
{
  public const int DefaultMaxDocumentBytes = 1024 * 1024;

  private static readonly HttpClient SharedHttpClient = new();
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    AllowTrailingCommas = true
  };
  private static readonly Regex ProfileIdPattern = new(
      "^[A-Za-z0-9][A-Za-z0-9._-]*$",
      RegexOptions.CultureInvariant);

  private readonly ProfileSourceDefinition _source;
  private readonly string _cacheDirectory;
  private readonly HttpClient _httpClient;
  private readonly int _maxDocumentBytes;

  public ProfileCatalog(
      ProfileSourceDefinition source,
      string cacheDirectory,
      HttpClient? httpClient = null,
      int maxDocumentBytes = DefaultMaxDocumentBytes)
  {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocumentBytes);

    _source = source;
    _cacheDirectory = Path.Combine(Path.GetFullPath(cacheDirectory), source.Id);
    _httpClient = httpClient ?? SharedHttpClient;
    _maxDocumentBytes = maxDocumentBytes;
  }

  public ProfileSourceDefinition Source => _source;

  public async Task<IReadOnlyList<ProfileCatalogEntry>> ListAsync(
      CancellationToken cancellationToken = default)
  {
    string json;
    ProfileOrigin origin;
    try
    {
      json = await DownloadAsync("index.json", cancellationToken);
      var parsed = ParseIndex(json);
      await TryWriteCacheAsync(CachePath("index.json"), json, cancellationToken);
      return CreateEntries(parsed, ProfileOrigin.Remote);
    }
    catch (Exception exception) when (IsOfflineFailure(exception, cancellationToken))
    {
      json = await ReadCacheAsync(CachePath("index.json"), cancellationToken);
      origin = ProfileOrigin.Cache;
    }

    return CreateEntries(ParseIndex(json), origin);
  }

  public async Task<LoadedProfile> LoadAsync(
      string profileId,
      CancellationToken cancellationToken = default)
  {
    ValidateProfileId(profileId);
    var fileName = profileId + ".json";
    var remoteUri = new Uri(_source.BaseUri, fileName);

    string json;
    ProfileOrigin origin;
    string location;
    try
    {
      json = await DownloadAsync(fileName, cancellationToken);
      var parsed = ProfileParser.Parse(json);
      ValidateLoadedId(profileId, parsed.Id);
      await TryWriteCacheAsync(CachePath(fileName), json, cancellationToken);
      return new LoadedProfile(
          parsed,
          ProfileOrigin.Remote,
          remoteUri.AbsoluteUri,
          ComputeHash(json),
          _source.Id);
    }
    catch (Exception exception) when (IsOfflineFailure(exception, cancellationToken))
    {
      var cachePath = CachePath(fileName);
      json = await ReadCacheAsync(cachePath, cancellationToken);
      origin = ProfileOrigin.Cache;
      location = cachePath;
    }

    var profile = ProfileParser.Parse(json);
    ValidateLoadedId(profileId, profile.Id);
    return new LoadedProfile(
        profile,
        origin,
        location,
        ComputeHash(json),
        _source.Id);
  }

  private ProfileCatalogEntry[] CreateEntries(
      IReadOnlyList<CatalogItem> items,
      ProfileOrigin origin) =>
      items
          .Select(item => new ProfileCatalogEntry(
              item.Id,
              item.Version,
              item.DisplayName,
              item.Description,
              origin,
              new Uri(_source.BaseUri, item.Id + ".json").AbsoluteUri,
              _source.Id,
              _source.DisplayName))
          .OrderBy(entry => entry.Id, StringComparer.Ordinal)
          .ToArray();

  private async Task<string> DownloadAsync(
      string relativePath,
      CancellationToken cancellationToken)
  {
    var uri = new Uri(_source.BaseUri, relativePath);
    using var response = await _httpClient.GetAsync(
        uri,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);
    EnsureHttps(response.RequestMessage?.RequestUri ?? uri);
    response.EnsureSuccessStatusCode();
    if (response.Content.Headers.ContentLength > _maxDocumentBytes)
    {
      throw TooLarge(relativePath);
    }

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    return await ReadLimitedUtf8Async(stream, relativePath, cancellationToken);
  }

  private async Task<string> ReadCacheAsync(
      string path,
      CancellationToken cancellationToken)
  {
    if (!File.Exists(path))
    {
      throw new InvalidOperationException(
          $"Profile Source '{_source.DisplayName}' is unavailable and no local cache exists.");
    }

    await using var stream = File.OpenRead(path);
    return await ReadLimitedUtf8Async(stream, Path.GetFileName(path), cancellationToken);
  }

  private async Task<string> ReadLimitedUtf8Async(
      Stream stream,
      string documentName,
      CancellationToken cancellationToken)
  {
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    while (true)
    {
      var read = await stream.ReadAsync(chunk, cancellationToken);
      if (read == 0)
      {
        break;
      }

      await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
      if (buffer.Length > _maxDocumentBytes)
      {
        throw TooLarge(documentName);
      }
    }

    return new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
  }

  private static async Task TryWriteCacheAsync(
      string destination,
      string content,
      CancellationToken cancellationToken)
  {
    var directory = Path.GetDirectoryName(destination)!;
    var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
      Directory.CreateDirectory(directory);
      await File.WriteAllTextAsync(
          temporary,
          content,
          new UTF8Encoding(false),
          cancellationToken);
      File.Move(temporary, destination, overwrite: true);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
    {
      // A cache failure must not hide a valid remote response.
    }
    finally
    {
      try
      {
        File.Delete(temporary);
      }
      catch
      {
        // Best-effort cleanup of WDEM's own temporary cache file.
      }
    }
  }

  private string CachePath(string fileName) => Path.Combine(_cacheDirectory, fileName);

  private static bool IsOfflineFailure(Exception exception, CancellationToken cancellationToken) =>
      exception is HttpRequestException { StatusCode: null } ||
      exception is TaskCanceledException && !cancellationToken.IsCancellationRequested;

  private static string ComputeHash(string json) =>
      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

  private static void EnsureHttps(Uri? uri)
  {
    if (uri is null ||
        !uri.IsAbsoluteUri ||
        !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
      throw new NotSupportedException("Profile Source requests and redirects must use HTTPS.");
    }
  }

  private static void ValidateProfileId(string profileId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
    if (!ProfileIdPattern.IsMatch(profileId))
    {
      throw new ArgumentException(
          "Profile id may contain only letters, numbers, '.', '_' and '-'.",
          nameof(profileId));
    }
  }

  private static void ValidateLoadedId(string requestedId, string actualId)
  {
    if (!string.Equals(requestedId, actualId, StringComparison.Ordinal))
    {
      throw new FormatException(
          $"Requested Profile '{requestedId}' but the document declares '{actualId}'.");
    }
  }

  private static CatalogItem[] ParseIndex(string json)
  {
    CatalogDto dto;
    try
    {
      dto = JsonSerializer.Deserialize<CatalogDto>(json, JsonOptions)
          ?? throw new FormatException("Profile catalog index must contain an object.");
    }
    catch (JsonException exception)
    {
      throw new FormatException("Profile catalog index JSON is invalid.", exception);
    }

    if (dto.Profiles is null)
    {
      throw new FormatException("Profile catalog index must contain a profiles array.");
    }

    var ids = new HashSet<string>(StringComparer.Ordinal);
    return dto.Profiles.Select((item, index) =>
    {
      if (item is null)
      {
        throw new FormatException($"Profile catalog entry {index} must contain an object.");
      }

      var id = Required(item.Id, $"Profile catalog entry {index} id");
      ValidateProfileId(id);
      if (!ids.Add(id))
      {
        throw new FormatException($"Profile catalog contains duplicate id '{id}'.");
      }

      return new CatalogItem(
          id,
          Required(item.Version, $"Profile catalog entry '{id}' version"),
          Required(item.DisplayName, $"Profile catalog entry '{id}' displayName"),
          string.IsNullOrWhiteSpace(item.Description) ? null : item.Description);
    }).ToArray();
  }

  private InvalidDataException TooLarge(string documentName) =>
      new($"Profile Source document '{documentName}' exceeds the {_maxDocumentBytes} byte size limit.");

  private static string Required(string? value, string field) =>
      string.IsNullOrWhiteSpace(value)
          ? throw new FormatException($"{field} is required.")
          : value;

  private sealed class CatalogDto
  {
    public List<CatalogItemDto?>? Profiles { get; init; }
  }

  private sealed class CatalogItemDto
  {
    public string? Id { get; init; }

    public string? Version { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }
  }

  private sealed record CatalogItem(
      string Id,
      string Version,
      string DisplayName,
      string? Description);
}
