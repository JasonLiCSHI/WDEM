using System.Text;

namespace Wdem.Infrastructure.Profiles;

internal sealed class ProfileDocumentCache(
    string cacheDirectory,
    string sourceId,
    int maxDocumentBytes)
{
  private readonly string _directory = Path.Combine(
      Path.GetFullPath(Required(cacheDirectory)),
      sourceId);

  public async Task<string> ReadAsync(
      string fileName,
      string sourceDisplayName,
      CancellationToken cancellationToken)
  {
    var path = PathFor(fileName);
    if (!File.Exists(path))
    {
      throw new InvalidOperationException(
          $"Profile Source '{sourceDisplayName}' is unavailable and no local cache exists.");
    }

    await using var stream = File.OpenRead(path);
    return await ProfileDocumentReader.ReadAsync(
        stream,
        Path.GetFileName(path),
        maxDocumentBytes,
        cancellationToken);
  }

  public async Task TryWriteAsync(
      string fileName,
      string content,
      CancellationToken cancellationToken)
  {
    var destination = PathFor(fileName);
    var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
    try
    {
      Directory.CreateDirectory(_directory);
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

  public string PathFor(string fileName) => Path.Combine(_directory, fileName);

  private static string Required(string value)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(value);
    return value;
  }
}
