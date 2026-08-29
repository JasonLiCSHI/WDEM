using System.Diagnostics;

namespace Wdem.Windows.Security;

internal interface IArtifactCleanupFileSystem
{
  void DeleteFile(string path);
  void DeleteDirectory(string path);
}

internal sealed class ArtifactCleanupQueue
{
  private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);
  private readonly object _gate = new();
  private readonly IArtifactCleanupFileSystem _fileSystem;
  private readonly int _maxAttempts;
  private readonly TimeSpan _retryDelay;
  private readonly int _maxDelayedRetryRounds;
  private readonly IReadOnlyList<string> _knownStagingRoots;
  private readonly List<PendingCleanup> _pending = [];
  private bool _retryScheduled;

  public ArtifactCleanupQueue(
      IArtifactCleanupFileSystem? fileSystem = null,
      int maxAttempts = 3,
      TimeSpan? retryDelay = null,
      int maxDelayedRetryRounds = 4,
      IReadOnlyList<string>? knownStagingRoots = null)
  {
    if (maxAttempts <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(maxAttempts));
    }

    if (retryDelay is { } delay && delay <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(retryDelay));
    }

    if (maxDelayedRetryRounds <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(maxDelayedRetryRounds));
    }

    _fileSystem = fileSystem ?? new SystemArtifactCleanupFileSystem();
    _maxAttempts = maxAttempts;
    _retryDelay = retryDelay ?? DefaultRetryDelay;
    _maxDelayedRetryRounds = maxDelayedRetryRounds;
    _knownStagingRoots = knownStagingRoots ??
        (fileSystem is null ? GetKnownStagingRoots() : []);
    SweepKnownStagingArtifacts();
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

  public void RetryPending() => RetryPending(scheduleOnFailure: true);

  private void RetryPending(bool scheduleOnFailure)
  {
    PendingCleanup[] pending;
    lock (_gate)
    {
      pending = [.. _pending];
      _pending.Clear();
    }

    foreach (var cleanup in pending)
    {
      TryExecute(cleanup, scheduleOnFailure);
    }
  }

  private void TryExecute(PendingCleanup cleanup, bool scheduleOnFailure = true)
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

    if (scheduleOnFailure)
    {
      ScheduleDelayedRetries();
    }
  }

  private void ScheduleDelayedRetries()
  {
    lock (_gate)
    {
      if (_retryScheduled)
      {
        return;
      }

      _retryScheduled = true;
    }

    _ = RetryPendingAfterDelayAsync();
  }

  private async Task RetryPendingAfterDelayAsync()
  {
    try
    {
      for (var round = 0; round < _maxDelayedRetryRounds; round++)
      {
        await Task.Delay(_retryDelay).ConfigureAwait(false);
        RetryPending(scheduleOnFailure: false);
        if (PendingCount == 0)
        {
          return;
        }
      }
    }
    catch (Exception exception)
    {
      Trace.WriteLine(
          $"[ArtifactCleanup] Delayed cleanup stopped: {exception.GetType().Name}.");
    }
    finally
    {
      lock (_gate)
      {
        _retryScheduled = false;
      }
    }
  }

  private void SweepKnownStagingArtifacts()
  {
    foreach (var configuredRoot in _knownStagingRoots)
    {
      try
      {
        if (string.IsNullOrWhiteSpace(configuredRoot) ||
            !Path.IsPathFullyQualified(configuredRoot))
        {
          continue;
        }

        var root = Path.GetFullPath(configuredRoot);
        if (!Directory.Exists(root) ||
            File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
        {
          continue;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
          var fullPath = Path.GetFullPath(entry);
          if (!string.Equals(
                  Path.GetDirectoryName(fullPath),
                  root,
                  StringComparison.OrdinalIgnoreCase))
          {
            continue;
          }

          var attributes = File.GetAttributes(fullPath);
          if (attributes.HasFlag(FileAttributes.ReparsePoint))
          {
            Trace.WriteLine("[ArtifactCleanup] Skipped redirected staging artifact.");
            continue;
          }

          TryExecute(new PendingCleanup(
              fullPath,
              attributes.HasFlag(FileAttributes.Directory)));
        }
      }
      catch (Exception exception) when (exception is ArgumentException or IOException or
          UnauthorizedAccessException or NotSupportedException)
      {
        Trace.WriteLine(
            $"[ArtifactCleanup] Staging sweep deferred: {exception.GetType().Name}.");
      }
    }
  }

  private static IReadOnlyList<string> GetKnownStagingRoots()
  {
    var roots = new List<string>
    {
      Path.Combine(Path.GetTempPath(), "wdem", "visual-studio")
    };
    var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    if (!string.IsNullOrWhiteSpace(commonData))
    {
      roots.Add(Path.Combine(commonData, "Wdem", "SecureArtifacts"));
    }

    return roots;
  }

  private sealed record PendingCleanup(string Path, bool IsDirectory);

  private sealed class SystemArtifactCleanupFileSystem : IArtifactCleanupFileSystem
  {
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
  }
}
