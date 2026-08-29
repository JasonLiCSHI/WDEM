using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wdem.Windows.VisualStudio;

namespace Wdem.Windows.Security;

internal interface IArtifactCleanupFileSystem
{
  void DeleteFile(string path);
  void DeleteDirectory(string path);
}

internal sealed class ArtifactCleanupQueue
{
  private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(250);
  private static readonly TimeSpan DefaultMinimumSweepAge = TimeSpan.FromHours(1);
  private readonly object _gate = new();
  private readonly IArtifactCleanupFileSystem _fileSystem;
  private readonly int _maxAttempts;
  private readonly TimeSpan _retryDelay;
  private readonly int _maxDelayedRetryRounds;
  private readonly IReadOnlyList<string> _knownStagingRoots;
  private readonly TimeSpan _minimumSweepAge;
  private readonly Action? _beforeRetryScheduledReset;
  private readonly List<PendingCleanup> _pending = [];
  private bool _retryScheduled;
  private long _enqueueVersion;

  public ArtifactCleanupQueue(
      IArtifactCleanupFileSystem? fileSystem = null,
      int maxAttempts = 3,
      TimeSpan? retryDelay = null,
      int maxDelayedRetryRounds = 4,
      IReadOnlyList<string>? knownStagingRoots = null,
      TimeSpan? minimumSweepAge = null,
      Action? beforeRetryScheduledReset = null)
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

    if (minimumSweepAge is { } sweepAge && sweepAge < TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(minimumSweepAge));
    }

    _fileSystem = fileSystem ?? new SystemArtifactCleanupFileSystem();
    _maxAttempts = maxAttempts;
    _retryDelay = retryDelay ?? DefaultRetryDelay;
    _maxDelayedRetryRounds = maxDelayedRetryRounds;
    _knownStagingRoots = knownStagingRoots ??
        (fileSystem is null ? GetKnownStagingRoots() : []);
    _minimumSweepAge = minimumSweepAge ?? DefaultMinimumSweepAge;
    _beforeRetryScheduledReset = beforeRetryScheduledReset;
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
      if (scheduleOnFailure)
      {
        _enqueueVersion++;
      }

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
    long enqueueVersionAtStart;
    lock (_gate)
    {
      enqueueVersionAtStart = _enqueueVersion;
    }

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
      _beforeRetryScheduledReset?.Invoke();
      var retryAgain = false;
      lock (_gate)
      {
        _retryScheduled = false;
        if (_pending.Count > 0 && _enqueueVersion != enqueueVersionAtStart)
        {
          _retryScheduled = true;
          retryAgain = true;
        }
      }

      if (retryAgain)
      {
        _ = RetryPendingAfterDelayAsync();
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
          if (!attributes.HasFlag(FileAttributes.Directory) ||
              attributes.HasFlag(FileAttributes.ReparsePoint))
          {
            Trace.WriteLine("[ArtifactCleanup] Skipped redirected staging artifact.");
            continue;
          }

          if (IsPlanArtifactRoot(root))
          {
            var planStaleBeforeUtc = DateTime.UtcNow - _minimumSweepAge;
            var canReclaimExpired = VsixPlanArtifactStore.CanReclaimExpiredDirectory(
                    fullPath,
                    DateTimeOffset.UtcNow) &&
                ArtifactLease.CanAcquireForCleanup(fullPath, DateTime.MaxValue);
            if (!canReclaimExpired &&
                !ArtifactLease.CanReclaimUninitializedPlanArtifact(
                    fullPath,
                    planStaleBeforeUtc))
            {
              continue;
            }

            TryExecute(new PendingCleanup(fullPath, true));
            continue;
          }

          var staleBeforeUtc = DateTime.UtcNow - _minimumSweepAge;
          if (!ArtifactLease.CanAcquireForCleanup(fullPath, staleBeforeUtc) &&
              !ArtifactLease.CanReclaimUninitialized(fullPath, staleBeforeUtc))
          {
            continue;
          }

          TryExecute(new PendingCleanup(fullPath, true));
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

  private static bool IsPlanArtifactRoot(string root) =>
      string.Equals(Path.GetFileName(root), "PlanArtifacts", StringComparison.OrdinalIgnoreCase) &&
      string.Equals(
          Path.GetFileName(Path.GetDirectoryName(root)),
          "Wdem",
          StringComparison.OrdinalIgnoreCase);

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
      roots.Add(Path.Combine(commonData, "Wdem", "PlanArtifacts"));
    }

    var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (!string.IsNullOrWhiteSpace(localData))
    {
      roots.Add(Path.Combine(localData, "Wdem", "PlanArtifacts"));
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

internal sealed class ArtifactLease : IDisposable
{
  internal const string OwnershipMarkerFileName = ".wdem-artifact";
  internal const string LeaseFileName = ".wdem-lease";
  private FileStream? _lease;
  private static ReadOnlySpan<byte> OwnershipMarkerContent => "wdem-artifact-v1\n"u8;

  private ArtifactLease(FileStream lease)
  {
    _lease = lease;
  }

  public static ArtifactLease Create(string directoryPath)
  {
    var markerPath = Path.Combine(directoryPath, OwnershipMarkerFileName);
    var leasePath = Path.Combine(directoryPath, LeaseFileName);
    using (var marker = new FileStream(
                   markerPath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
    {
      marker.Write(OwnershipMarkerContent);
      marker.Flush(flushToDisk: true);
    }

    try
    {
      return new ArtifactLease(new FileStream(
          leasePath,
          FileMode.CreateNew,
          FileAccess.ReadWrite,
          FileShare.None,
          bufferSize: 1,
          FileOptions.WriteThrough));
    }
    catch
    {
      File.Delete(markerPath);
      throw;
    }
  }

  public static ArtifactLease Acquire(string directoryPath)
  {
    return Acquire(directoryPath, beforeOpen: null);
  }

  internal static ArtifactLease Acquire(string directoryPath, Action? beforeOpen)
  {
    var markerPath = Path.Combine(directoryPath, OwnershipMarkerFileName);
    var leasePath = Path.Combine(directoryPath, LeaseFileName);
    if (!File.Exists(markerPath) || !File.Exists(leasePath) ||
        File.GetAttributes(leasePath).HasFlag(FileAttributes.ReparsePoint) ||
        !HasValidOwnershipMarker(markerPath, DateTime.MaxValue))
    {
      throw new InvalidDataException("The staged artifact ownership marker is invalid.");
    }

    beforeOpen?.Invoke();
    using var marker = OpenExistingWithoutFollowingLinks(
        markerPath,
        FileAccess.Read,
        FileShare.Read,
        FileOptions.SequentialScan);
    if (!HasValidOwnershipMarker(marker, DateTime.MaxValue))
    {
      throw new InvalidDataException("The staged artifact ownership marker is invalid.");
    }

    var lease = OpenExistingWithoutFollowingLinks(
        leasePath,
        FileAccess.ReadWrite,
        FileShare.None,
        FileOptions.WriteThrough);
    if (File.GetAttributes(lease.SafeFileHandle).HasFlag(FileAttributes.ReparsePoint))
    {
      lease.Dispose();
      throw new InvalidDataException("The staged artifact lease is invalid.");
    }

    return new ArtifactLease(lease);
  }

  public void Dispose()
  {
    Interlocked.Exchange(ref _lease, null)?.Dispose();
  }

  internal static bool CanAcquireForCleanup(
      string directoryPath,
      DateTime staleBeforeUtc)
  {
    var markerPath = Path.Combine(directoryPath, OwnershipMarkerFileName);
    var leasePath = Path.Combine(directoryPath, LeaseFileName);
    try
    {
      if (!File.Exists(markerPath) || !File.Exists(leasePath) ||
          File.GetAttributes(leasePath).HasFlag(FileAttributes.ReparsePoint) ||
          !HasValidOwnershipMarker(markerPath, staleBeforeUtc))
      {
        return false;
      }

      using var marker = OpenExistingWithoutFollowingLinks(
          markerPath,
          FileAccess.Read,
          FileShare.Read,
          FileOptions.SequentialScan);
      if (!HasValidOwnershipMarker(marker, staleBeforeUtc))
      {
        return false;
      }

      using var lease = OpenExistingWithoutFollowingLinks(
          leasePath,
          FileAccess.ReadWrite,
          FileShare.None,
          FileOptions.None);
      return !File.GetAttributes(lease.SafeFileHandle).HasFlag(FileAttributes.ReparsePoint);
    }
    catch (Exception exception) when (exception is IOException or
        UnauthorizedAccessException or System.Security.SecurityException)
    {
      return false;
    }
  }

  internal static bool CanReclaimUninitialized(
      string directoryPath,
      DateTime staleBeforeUtc)
  {
    var name = Path.GetFileName(directoryPath);
    if (name.Length != 32 || name.Any(character =>
            character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
    {
      return false;
    }

    try
    {
      if (File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint) ||
          Directory.GetLastWriteTimeUtc(directoryPath) >= staleBeforeUtc)
      {
        return false;
      }

      var entries = Directory.EnumerateFileSystemEntries(directoryPath).Take(2).ToArray();
      if (entries.Length == 0)
      {
        return true;
      }

      var markerPath = Path.Combine(directoryPath, OwnershipMarkerFileName);
      return entries.Length == 1 &&
          string.Equals(entries[0], markerPath, StringComparison.OrdinalIgnoreCase) &&
          HasValidOwnershipMarker(markerPath, staleBeforeUtc);
    }
    catch (Exception exception) when (exception is IOException or
        UnauthorizedAccessException or System.Security.SecurityException)
    {
      return false;
    }
  }

  internal static bool CanReclaimUninitializedPlanArtifact(
      string directoryPath,
      DateTime staleBeforeUtc)
  {
    var name = Path.GetFileName(directoryPath);
    if (name.Length != 32 || name.Any(character =>
            character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
    {
      return false;
    }

    try
    {
      if (File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint) ||
          Directory.GetLastWriteTimeUtc(directoryPath) >= staleBeforeUtc)
      {
        return false;
      }

      var expectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
      {
        OwnershipMarkerFileName,
        LeaseFileName,
        "extension.vsix"
      };
      var entries = Directory.EnumerateFileSystemEntries(directoryPath).Take(4).ToArray();
      if (entries.Length != expectedNames.Count ||
          entries.Any(entry => !expectedNames.Remove(Path.GetFileName(entry))))
      {
        return false;
      }

      foreach (var entry in entries)
      {
        var attributes = File.GetAttributes(entry);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint) ||
            File.GetLastWriteTimeUtc(entry) >= staleBeforeUtc)
        {
          return false;
        }
      }

      return CanAcquireForCleanup(directoryPath, staleBeforeUtc);
    }
    catch (Exception exception) when (exception is IOException or
        UnauthorizedAccessException or System.Security.SecurityException)
    {
      return false;
    }
  }

  private static bool HasValidOwnershipMarker(
      string markerPath,
      DateTime staleBeforeUtc)
  {
    var pathAttributes = File.GetAttributes(markerPath);
    if (pathAttributes.HasFlag(FileAttributes.Directory) ||
        pathAttributes.HasFlag(FileAttributes.ReparsePoint))
    {
      return false;
    }

    using var marker = OpenExistingWithoutFollowingLinks(
        markerPath,
        FileAccess.Read,
        FileShare.Read,
        FileOptions.SequentialScan);
    return HasValidOwnershipMarker(marker, staleBeforeUtc);
  }

  private static bool HasValidOwnershipMarker(
      FileStream marker,
      DateTime staleBeforeUtc)
  {
    var attributes = File.GetAttributes(marker.SafeFileHandle);
    if (attributes.HasFlag(FileAttributes.Directory) ||
        attributes.HasFlag(FileAttributes.ReparsePoint) ||
        File.GetLastWriteTimeUtc(marker.SafeFileHandle) >= staleBeforeUtc)
    {
      return false;
    }

    var expected = OwnershipMarkerContent;
    if (marker.Length != expected.Length)
    {
      return false;
    }

    Span<byte> actual = stackalloc byte[expected.Length];
    marker.ReadExactly(actual);
    return actual.SequenceEqual(expected);
  }

  private static FileStream OpenExistingWithoutFollowingLinks(
      string path,
      FileAccess access,
      FileShare share,
      FileOptions options)
  {
    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint OpenExisting = 3;
    const uint FileFlagOpenReparsePoint = 0x00200000;
    var desiredAccess = access switch
    {
      FileAccess.Read => GenericRead,
      FileAccess.Write => GenericWrite,
      _ => GenericRead | GenericWrite
    };
    var handle = NativeMethods.CreateFile(
        path,
        desiredAccess,
        share,
        IntPtr.Zero,
        OpenExisting,
        (uint)options | FileFlagOpenReparsePoint,
        IntPtr.Zero);
    if (handle.IsInvalid)
    {
      var error = Marshal.GetLastWin32Error();
      handle.Dispose();
      throw new IOException(
          $"Could not open the staged artifact metadata '{path}'.",
          new Win32Exception(error));
    }

    return new FileStream(handle, access, bufferSize: 1, isAsync: false);
  }

  private static class NativeMethods
  {
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
  }
}
