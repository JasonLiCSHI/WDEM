using System.Diagnostics;

namespace Wdem.Windows.Security;

internal interface IArtifactCleanupFileSystem
{
  void DeleteFile(string path);
  void DeleteDirectory(string path);
}

internal sealed class ArtifactCleanupQueue
{
  private readonly object _gate = new();
  private readonly IArtifactCleanupFileSystem _fileSystem;
  private readonly int _maxAttempts;
  private readonly List<PendingCleanup> _pending = [];

  public ArtifactCleanupQueue(
      IArtifactCleanupFileSystem? fileSystem = null,
      int maxAttempts = 3)
  {
    if (maxAttempts <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(maxAttempts));
    }

    _fileSystem = fileSystem ?? new SystemArtifactCleanupFileSystem();
    _maxAttempts = maxAttempts;
  }

  public static ArtifactCleanupQueue Shared { get; } = new();

  public int PendingCount
  {
    get
    {
      lock (_gate)
      {
        return _pending.Count;
      }
    }
  }

  public void DeleteFile(string path) => TryExecute(new PendingCleanup(path, false));

  public void DeleteDirectory(string path) => TryExecute(new PendingCleanup(path, true));

  public void RetryPending()
  {
    PendingCleanup[] pending;
    lock (_gate)
    {
      pending = [.. _pending];
      _pending.Clear();
    }

    foreach (var cleanup in pending)
    {
      TryExecute(cleanup);
    }
  }

  private void TryExecute(PendingCleanup cleanup)
  {
    Exception? failure = null;
    for (var attempt = 0; attempt < _maxAttempts; attempt++)
    {
      try
      {
        if (cleanup.IsDirectory)
        {
          _fileSystem.DeleteDirectory(cleanup.Path);
        }
        else
        {
          _fileSystem.DeleteFile(cleanup.Path);
        }

        return;
      }
      catch (Exception exception)
      {
        failure = exception;
      }
    }

    Trace.WriteLine(
        $"[ArtifactCleanup] {(cleanup.IsDirectory ? "Directory" : "File")} cleanup deferred after {_maxAttempts} attempts: {failure!.GetType().Name}.");
    lock (_gate)
    {
      if (!_pending.Contains(cleanup))
      {
        _pending.Add(cleanup);
      }
    }
  }

  private sealed record PendingCleanup(string Path, bool IsDirectory);

  private sealed class SystemArtifactCleanupFileSystem : IArtifactCleanupFileSystem
  {
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
  }
}
