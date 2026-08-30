using System.Security;
using System.Security.Cryptography;
using Wdem.Core.Execution;

namespace Wdem.Windows.Configuration;

public sealed record ConfigurationImportResult(
    bool Succeeded,
    string? DestinationPath,
    string? Sha256,
    StructuredError? Error);

internal sealed class StagedConfigurationSnapshot(
    string path,
    string sha256,
    ConfigurationDirectoryLease directoryLease,
    FileStream contentLease,
    ConfigurationDestinationPrecondition destinationPrecondition) : IDisposable
{
  private FileStream? _contentLease = contentLease;

  internal string Path { get; } = path;
  internal string Sha256 { get; } = sha256;
  internal ConfigurationDirectoryLease DirectoryLease { get; } = directoryLease;
  internal ConfigurationDestinationPrecondition DestinationPrecondition { get; } =
      destinationPrecondition;
  internal bool ContentMoved { get; private set; }

  internal async Task<string> HashLeasedContentAsync(CancellationToken cancellationToken)
  {
    var stream = _contentLease ?? throw new ObjectDisposedException(nameof(StagedConfigurationSnapshot));
    stream.Position = 0;
    var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    stream.Position = 0;
    return Convert.ToHexString(hash);
  }

  internal void ReleaseContentLease()
  {
    _contentLease?.Dispose();
    _contentLease = null;
  }

  internal void MarkContentMoved() => ContentMoved = true;

  internal void DeleteStagingFile()
  {
    ReleaseContentLease();
    if (!ContentMoved)
    {
      ConfigurationImporter.DeleteStagingSnapshot(Path);
    }
  }

  public void Dispose()
  {
    ReleaseContentLease();
    DirectoryLease.Dispose();
  }
}

internal sealed record ConfigurationStagingResult(
    StagedConfigurationSnapshot? Snapshot,
    StructuredError? Error)
{
  public bool Succeeded => Snapshot is not null && Error is null;
}

public sealed class ConfigurationImporter
{
  private readonly Action<string>? _afterDestinationMove;
  private readonly Action<string>? _afterDestinationDirectoryLeased;
  private readonly Action<string>? _beforeDestinationPreconditionCheck;

  public ConfigurationImporter()
  {
  }

  internal ConfigurationImporter(Action<string> afterDestinationMove)
      : this(
          afterDestinationMove,
          afterDestinationDirectoryLeased: null,
          beforeDestinationPreconditionCheck: null)
  {
  }

  internal ConfigurationImporter(
      Action<string>? afterDestinationMove,
      Action<string>? afterDestinationDirectoryLeased)
      : this(
          afterDestinationMove,
          afterDestinationDirectoryLeased,
          beforeDestinationPreconditionCheck: null)
  {
  }

  internal ConfigurationImporter(
      Action<string>? afterDestinationMove,
      Action<string>? afterDestinationDirectoryLeased,
      Action<string>? beforeDestinationPreconditionCheck)
  {
    _afterDestinationMove = afterDestinationMove;
    _afterDestinationDirectoryLeased = afterDestinationDirectoryLeased;
    _beforeDestinationPreconditionCheck = beforeDestinationPreconditionCheck;
  }

  public async Task<ConfigurationImportResult> CopyAtomicallyAsync(
      ResolvedConfigurationSource source,
      string destinationPath,
      CancellationToken cancellationToken)
  {
    var staged = await StageAsync(source, destinationPath, cancellationToken).ConfigureAwait(false);
    if (!staged.Succeeded)
    {
      return new ConfigurationImportResult(false, null, null, staged.Error);
    }

    var snapshot = staged.Snapshot!;
    try
    {
      return await CommitStagedAsync(
          snapshot,
          destinationPath,
          cancellationToken).ConfigureAwait(false);
    }
    finally
    {
      snapshot.DeleteStagingFile();
      snapshot.Dispose();
    }
  }

  internal async Task<ConfigurationStagingResult> StageAsync(
      ResolvedConfigurationSource source,
      string destinationPath,
      CancellationToken cancellationToken) => await StageAsync(
          source,
          destinationPath,
          destinationPrecondition: null,
          cancellationToken).ConfigureAwait(false);

  internal async Task<ConfigurationStagingResult> StageAsync(
      ResolvedConfigurationSource source,
      string destinationPath,
      ConfigurationDestinationPrecondition? destinationPrecondition,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(source);
    cancellationToken.ThrowIfCancellationRequested();
    string? stagingPath = null;
    ConfigurationDirectoryLease? directoryLease = null;
    FileStream? contentLease = null;
    try
    {
      var validationError = NormalizeDestination(
          destinationPath,
          out var fullDestination,
          out var directory);
      if (validationError is not null)
      {
        return StagingFailure(validationError);
      }

      destinationPrecondition ??= await CaptureDestinationPreconditionAsync(
          fullDestination,
          cancellationToken).ConfigureAwait(false);
      if (!string.Equals(
              destinationPrecondition.Path,
              fullDestination,
              StringComparison.OrdinalIgnoreCase))
      {
        return StagingFailure("The configuration destination precondition identifies a different path.");
      }

      directoryLease = ConfigurationDirectoryLease.Acquire(directory);
      _afterDestinationDirectoryLeased?.Invoke(directory);
      validationError = ValidateDestinationFile(destinationPath);
      if (validationError is not null)
      {
        return StagingFailure(validationError);
      }

      var extension = Path.GetExtension(destinationPath);
      var baseName = Path.GetFileNameWithoutExtension(destinationPath);
      stagingPath = Path.Combine(
          directory,
          $".{baseName}.wdem-{Guid.NewGuid():N}.staging{extension}");
      await using (var stagingWriter = new FileStream(
                       stagingPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 81920,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
      {
        await stagingWriter.WriteAsync(source.Contents, cancellationToken).ConfigureAwait(false);
        await stagingWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        stagingWriter.Flush(flushToDisk: true);
      }

      contentLease = new FileStream(
          directoryLease.OpenReadOnlyFile(stagingPath),
          FileAccess.Read,
          bufferSize: 81920,
          isAsync: false);
      var stagingHash = Convert.ToHexString(
          await SHA256.HashDataAsync(contentLease, cancellationToken).ConfigureAwait(false));
      contentLease.Position = 0;
      if (!string.Equals(stagingHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
      {
        return StagingFailure("The staged snapshot does not match the verified source SHA-256.");
      }

      var snapshot = new StagedConfigurationSnapshot(
          stagingPath,
          stagingHash,
          directoryLease,
          contentLease,
          destinationPrecondition);
      stagingPath = null;
      directoryLease = null;
      contentLease = null;
      return new ConfigurationStagingResult(snapshot, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (UnsafeConfigurationDirectoryException exception)
    {
      return StagingFailure(exception.Message, exception);
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        NotSupportedException or UnauthorizedAccessException or SecurityException)
    {
      return StagingFailure("The configuration staging snapshot could not be created.", exception);
    }
    finally
    {
      if (stagingPath is not null)
      {
        contentLease?.Dispose();
        DeleteStagingSnapshot(stagingPath);
      }

      directoryLease?.Dispose();
    }
  }

  internal async Task<ConfigurationImportResult> CommitStagedAsync(
      StagedConfigurationSnapshot snapshot,
      string destinationPath,
      CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(snapshot);
    cancellationToken.ThrowIfCancellationRequested();
    string? backupPath = null;
    var destinationCommitted = false;
    try
    {
      var validationError = NormalizeDestination(
          destinationPath,
          out var fullDestination,
          out var directory);
      if (validationError is not null)
      {
        return Failure(validationError);
      }

      if (!string.Equals(
              snapshot.DirectoryLease.DirectoryPath,
              directory,
              StringComparison.OrdinalIgnoreCase))
      {
        return Failure("The configuration staging snapshot is not leased for this destination.");
      }

      validationError = ValidateDestinationFile(fullDestination);
      if (validationError is not null)
      {
        return Failure(validationError);
      }

      var fullStagingPath = Path.GetFullPath(snapshot.Path);
      if (!string.Equals(
              Path.GetDirectoryName(fullStagingPath),
              directory,
              StringComparison.OrdinalIgnoreCase) ||
          ConfigurationSourceResolver.HasAlternateDataStream(fullStagingPath) ||
          !File.Exists(fullStagingPath) ||
          (File.GetAttributes(fullStagingPath) &
              (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
      {
        return Failure("The configuration staging snapshot is invalid.");
      }

      var stagingHash = await snapshot.HashLeasedContentAsync(cancellationToken).ConfigureAwait(false);
      if (!string.Equals(stagingHash, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
      {
        return Failure("The configuration staging snapshot changed before commit.");
      }

      if (File.Exists(fullDestination))
      {
        backupPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.backup");
        File.Copy(fullDestination, backupPath, overwrite: false);
      }

      _beforeDestinationPreconditionCheck?.Invoke(fullDestination);
      if (!await MatchesDestinationPreconditionAsync(
              fullDestination,
              snapshot.DestinationPrecondition,
              CancellationToken.None).ConfigureAwait(false))
      {
        return Failure("The configuration destination changed after planning; the approved plan is stale.");
      }

      cancellationToken.ThrowIfCancellationRequested();
      snapshot.ReleaseContentLease();
      File.Move(fullStagingPath, fullDestination, overwrite: true);
      snapshot.MarkContentMoved();
      destinationCommitted = true;
      _afterDestinationMove?.Invoke(fullDestination);

      var finalHash = await HashFileAsync(fullDestination, CancellationToken.None).ConfigureAwait(false);
      if (!string.Equals(finalHash, snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
      {
        var restoreError = RestoreDestination(fullDestination, backupPath);
        destinationCommitted = false;
        if (restoreError is not null)
        {
          backupPath = null;
        }

        return Failure(
            restoreError is null
                ? "The final destination does not match the verified source SHA-256."
                : "The final destination hash verification failed and the prior destination could not be restored.",
            restoreError);
      }

      destinationCommitted = false;
      return new ConfigurationImportResult(true, fullDestination, finalHash, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        NotSupportedException or UnauthorizedAccessException or SecurityException)
    {
      Exception failure = exception;
      if (destinationCommitted)
      {
        var restoreError = RestoreDestination(Path.GetFullPath(destinationPath), backupPath);
        destinationCommitted = false;
        if (restoreError is not null)
        {
          backupPath = null;
          failure = new AggregateException(exception, restoreError);
        }
      }

      return Failure("The configuration staging snapshot could not be atomically committed.", failure);
    }
    finally
    {
      if (backupPath is not null)
      {
        DeleteStagingSnapshot(backupPath);
      }
    }
  }

  private static Exception? RestoreDestination(string destinationPath, string? backupPath)
  {
    try
    {
      if (backupPath is not null && File.Exists(backupPath))
      {
        File.Move(backupPath, destinationPath, overwrite: true);
      }
      else
      {
        File.Delete(destinationPath);
      }

      return null;
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or
        NotSupportedException or UnauthorizedAccessException or SecurityException)
    {
      return exception;
    }
  }

  internal static void DeleteStagingSnapshot(string path)
  {
    try
    {
      File.Delete(path);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      // Best-effort cleanup. The formal destination remains untouched.
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

  private static async Task<ConfigurationDestinationPrecondition> CaptureDestinationPreconditionAsync(
      string destinationPath,
      CancellationToken cancellationToken)
  {
    if (!File.Exists(destinationPath))
    {
      return new ConfigurationDestinationPrecondition(destinationPath, false, null);
    }

    return new ConfigurationDestinationPrecondition(
        destinationPath,
        true,
        await HashFileAsync(destinationPath, cancellationToken).ConfigureAwait(false));
  }

  private static async Task<bool> MatchesDestinationPreconditionAsync(
      string destinationPath,
      ConfigurationDestinationPrecondition expected,
      CancellationToken cancellationToken)
  {
    if (!string.Equals(destinationPath, expected.Path, StringComparison.OrdinalIgnoreCase) ||
        File.Exists(destinationPath) != expected.Exists)
    {
      return false;
    }

    return !expected.Exists || string.Equals(
        await HashFileAsync(destinationPath, cancellationToken).ConfigureAwait(false),
        expected.Sha256,
        StringComparison.OrdinalIgnoreCase);
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

  private static string? NormalizeDestination(
      string destinationPath,
      out string fullDestination,
      out string directory)
  {
    fullDestination = string.Empty;
    directory = string.Empty;
    if (!Path.IsPathFullyQualified(destinationPath) || destinationPath.Any(char.IsControl))
    {
      return "The configuration destination must be an absolute local path.";
    }

    fullDestination = Path.GetFullPath(destinationPath);
    if (ConfigurationSourceResolver.HasAlternateDataStream(fullDestination))
    {
      return "NTFS alternate data stream destinations are not supported.";
    }

    directory = Path.GetDirectoryName(fullDestination) ?? string.Empty;
    if (string.IsNullOrWhiteSpace(directory))
    {
      return "The configuration destination directory is invalid.";
    }

    return null;
  }

  private static string? ValidateDestinationFile(string fullDestination)
  {
    if (Path.Exists(fullDestination))
    {
      var attributes = File.GetAttributes(fullDestination);
      if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
      {
        return "The configuration destination must be a regular non-reparse file.";
      }
    }

    return null;
  }

  private static ConfigurationStagingResult StagingFailure(
      string detail,
      Exception? exception = null) => new(null, CreateError(detail, exception));

  private static ConfigurationImportResult Failure(
      string detail,
      Exception? exception = null) => new(
          false,
          null,
          null,
          CreateError(detail, exception));

  private static StructuredError CreateError(
      string detail,
      Exception? exception) => new(
          WdemErrorCode.ConfigurationError,
          "Configuration import failed.",
          detail)
      {
        UnderlyingException = exception
      };
}
