using System.Security;
using System.Security.Cryptography;
using Wdem.Core.Execution;

namespace Wdem.Windows.Configuration;

public sealed record ConfigurationImportResult(
    bool Succeeded,
    string? DestinationPath,
    string? Sha256,
    StructuredError? Error);

public sealed class ConfigurationImporter
{
  public async Task<ConfigurationImportResult> CopyAtomicallyAsync(
      ResolvedConfigurationSource source,
      string destinationPath,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(source);
    cancellationToken.ThrowIfCancellationRequested();
    string? temporaryPath = null;
    try
    {
      if (!Path.IsPathFullyQualified(destinationPath) || destinationPath.Any(char.IsControl))
      {
        return Failure("The configuration destination must be an absolute local path.");
      }

      var fullDestination = Path.GetFullPath(destinationPath);
      if (ConfigurationSourceResolver.HasAlternateDataStream(fullDestination))
      {
        return Failure("NTFS alternate data stream destinations are not supported.");
      }

      var directory = Path.GetDirectoryName(fullDestination);
      if (string.IsNullOrWhiteSpace(directory))
      {
        return Failure("The configuration destination directory is invalid.");
      }

      Directory.CreateDirectory(directory);
      if (ContainsReparsePoint(directory))
      {
        return Failure("The configuration destination directory contains an unsafe reparse point.");
      }

      if (Path.Exists(fullDestination))
      {
        var attributes = File.GetAttributes(fullDestination);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
          return Failure("The configuration destination must be a regular non-reparse file.");
        }
      }

      temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
      await using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
      {
        await stream.WriteAsync(source.Contents, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
      }

      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temporaryPath, fullDestination, overwrite: true);
      temporaryPath = null;

      var actualHash = await HashFileAsync(fullDestination, CancellationToken.None).ConfigureAwait(false);
      if (!string.Equals(actualHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
      {
        return Failure("The imported destination does not match the verified source SHA-256.");
      }

      return new ConfigurationImportResult(true, fullDestination, actualHash, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        NotSupportedException or UnauthorizedAccessException or SecurityException)
    {
      return Failure("The configuration could not be atomically imported.", exception);
    }
    finally
    {
      if (temporaryPath is not null)
      {
        try
        {
          File.Delete(temporaryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
          // Best-effort cleanup. The original destination remains untouched.
        }
      }
    }
  }

  internal static async Task<string> HashFileAsync(
      string path,
      CancellationToken cancellationToken)
  {
    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
  }

  internal static bool ContainsReparsePoint(string directory)
  {
    var current = new DirectoryInfo(Path.GetFullPath(directory));
    while (current is not null)
    {
      if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
      {
        return true;
      }

      current = current.Parent;
    }

    return false;
  }

  private static ConfigurationImportResult Failure(
      string detail,
      Exception? exception = null) => new(
          false,
          null,
          null,
          new StructuredError(
              WdemErrorCode.ConfigurationError,
              "Configuration import failed.",
              detail)
          {
            UnderlyingException = exception
          });
}
