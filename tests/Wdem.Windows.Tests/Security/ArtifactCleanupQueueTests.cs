using System.Diagnostics;
using Wdem.Windows.Security;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class ArtifactCleanupQueueTests
{
  [Fact]
  public void DeleteFile_BoundsAttemptsAndDefersWithoutThrowing()
  {
    var fileSystem = new RecordingCleanupFileSystem { FailuresRemaining = int.MaxValue };
    var cleanup = new ArtifactCleanupQueue(fileSystem, maxAttempts: 3);

    cleanup.DeleteFile(@"C:\secret-token\installer.exe");

    Assert.Equal(3, fileSystem.FileDeleteAttempts);
    Assert.Equal(1, cleanup.PendingCount);
  }

  [Fact]
  public void RetryPending_RemovesDeferredCleanupAfterTransientFailureClears()
  {
    var fileSystem = new RecordingCleanupFileSystem { FailuresRemaining = 3 };
    var cleanup = new ArtifactCleanupQueue(fileSystem, maxAttempts: 3);
    cleanup.DeleteDirectory(@"C:\staging\artifact");
    fileSystem.FailuresRemaining = 0;

    cleanup.RetryPending();

    Assert.Equal(0, cleanup.PendingCount);
    Assert.Equal(4, fileSystem.DirectoryDeleteAttempts);
  }

  [Fact]
  public async Task StartupSweep_RetriesTransientlyLockedArtifactWithoutManualPump()
  {
    var root = Path.Combine(
        Path.GetTempPath(),
        $"wdem-cleanup-sweep-{Guid.NewGuid():N}");
    var artifactDirectory = CreateOwnedArtifactDirectory(root, "stale-artifact");
    var path = Path.Combine(artifactDirectory, "stale-installer.exe");
    await File.WriteAllTextAsync(path, "stale");
    File.SetLastWriteTimeUtc(
        Path.Combine(artifactDirectory, ".wdem-artifact"),
        DateTime.UtcNow - TimeSpan.FromHours(2));

    try
    {
      using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
      {
        _ = new ArtifactCleanupQueue(
            maxAttempts: 1,
            retryDelay: TimeSpan.FromMilliseconds(25),
            maxDelayedRetryRounds: 3,
            knownStagingRoots: [root]);
      }

      var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
      while (Directory.Exists(artifactDirectory) && DateTime.UtcNow < deadline)
      {
        await Task.Delay(25);
      }

      Assert.False(Directory.Exists(artifactDirectory));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public void StartupSweep_DeletesOnlyStaleOwnedArtifactsWithoutAnActiveLease()
  {
    var root = Path.Combine(
        Path.GetTempPath(),
        $"wdem-cleanup-ownership-{Guid.NewGuid():N}");
    var staleLeased = CreateOwnedArtifactDirectory(root, "stale-leased");
    var recentUnleased = CreateOwnedArtifactDirectory(root, "recent-unleased");
    var unowned = Path.Combine(root, "unowned");
    Directory.CreateDirectory(unowned);
    File.SetLastWriteTimeUtc(
        Path.Combine(staleLeased, ".wdem-artifact"),
        DateTime.UtcNow - TimeSpan.FromHours(2));
    using var lease = new FileStream(
        Path.Combine(staleLeased, ".wdem-lease"),
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None);

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.True(Directory.Exists(staleLeased));
      Assert.True(Directory.Exists(recentUnleased));
      Assert.True(Directory.Exists(unowned));

      lease.Dispose();
      File.SetLastWriteTimeUtc(
          Path.Combine(recentUnleased, ".wdem-artifact"),
          DateTime.UtcNow - TimeSpan.FromHours(2));
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.False(Directory.Exists(staleLeased));
      Assert.False(Directory.Exists(recentUnleased));
      Assert.True(Directory.Exists(unowned));
    }
    finally
    {
      lease.Dispose();
      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public void StartupSweep_ReclaimsOnlyStaleEmptyStrictlyNamedPremarkerDirectory()
  {
    var root = Path.Combine(
        Path.GetTempPath(),
        $"wdem-cleanup-premarker-{Guid.NewGuid():N}");
    var outsideRoot = Path.Combine(
        Path.GetTempPath(),
        $"wdem-cleanup-premarker-target-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(outsideRoot);
    var staleEmpty = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var recentEmpty = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var staleNonempty = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var unowned = Path.Combine(root, "unowned-directory");
    var nonmatching = Path.Combine(root, Guid.NewGuid().ToString("N").ToUpperInvariant());
    var reparse = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(staleEmpty);
    Directory.CreateDirectory(recentEmpty);
    Directory.CreateDirectory(staleNonempty);
    File.WriteAllText(Path.Combine(staleNonempty, "partial.bin"), "partial");
    Directory.CreateDirectory(unowned);
    Directory.CreateDirectory(nonmatching);
    CreateJunction(reparse, outsideRoot);
    var stale = DateTime.UtcNow - TimeSpan.FromHours(2);
    Directory.SetLastWriteTimeUtc(staleEmpty, stale);
    Directory.SetLastWriteTimeUtc(staleNonempty, stale);
    Directory.SetLastWriteTimeUtc(unowned, stale);
    Directory.SetLastWriteTimeUtc(nonmatching, stale);

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.False(Directory.Exists(staleEmpty));
      Assert.True(Directory.Exists(recentEmpty));
      Assert.True(Directory.Exists(staleNonempty));
      Assert.True(Directory.Exists(unowned));
      Assert.True(Directory.Exists(nonmatching));
      Assert.True(Directory.Exists(reparse));
      Assert.True(Directory.Exists(outsideRoot));
    }
    finally
    {
      if (Directory.Exists(reparse))
      {
        Directory.Delete(reparse);
      }

      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }

      if (Directory.Exists(outsideRoot))
      {
        Directory.Delete(outsideRoot, recursive: true);
      }
    }
  }

  [Fact]
  public void StartupSweep_ReclaimsOnlyStaleMarkerOnlyStrictlyNamedDirectory()
  {
    var root = Path.Combine(
        Path.GetTempPath(),
        $"wdem-cleanup-marker-only-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var staleMarkerOnly = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var recentMarker = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var invalidMarker = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var markerWithPayload = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var stale = DateTime.UtcNow - TimeSpan.FromHours(2);
    foreach (var directory in new[]
             {
               staleMarkerOnly,
               recentMarker,
               invalidMarker,
               markerWithPayload
             })
    {
      Directory.CreateDirectory(directory);
      File.WriteAllText(
          Path.Combine(directory, ".wdem-artifact"),
          "wdem-artifact-v1\n");
    }

    File.WriteAllText(Path.Combine(invalidMarker, ".wdem-artifact"), "invalid\n");
    File.WriteAllText(Path.Combine(markerWithPayload, "partial.bin"), "partial");
    foreach (var directory in new[]
             {
               staleMarkerOnly,
               recentMarker,
               invalidMarker,
               markerWithPayload
             })
    {
      Directory.SetLastWriteTimeUtc(directory, stale);
    }

    foreach (var directory in new[]
             {
               staleMarkerOnly,
               invalidMarker,
               markerWithPayload
             })
    {
      File.SetLastWriteTimeUtc(Path.Combine(directory, ".wdem-artifact"), stale);
    }

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.False(Directory.Exists(staleMarkerOnly));
      Assert.True(Directory.Exists(recentMarker));
      Assert.True(Directory.Exists(invalidMarker));
      Assert.True(Directory.Exists(markerWithPayload));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

  [Fact]
  public async Task DelayedRetry_ReschedulesCleanupEnqueuedDuringScheduledReset()
  {
    var fileSystem = new RecordingCleanupFileSystem
    {
      FailuresRemaining = 1
    };
    var resetReached = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    ArtifactCleanupQueue? cleanup = null;
    var hookCalls = 0;
    cleanup = new ArtifactCleanupQueue(
        fileSystem,
        maxAttempts: 1,
        retryDelay: TimeSpan.FromMilliseconds(25),
        maxDelayedRetryRounds: 1,
        beforeRetryScheduledReset: () =>
        {
          if (Interlocked.Increment(ref hookCalls) != 1)
          {
            return;
          }

          fileSystem.FailuresRemaining = 1;
          cleanup!.DeleteFile(@"C:\staging\arrived-during-reset.exe");
          fileSystem.FailuresRemaining = 0;
          resetReached.SetResult();
        });
    cleanup.DeleteFile(@"C:\staging\initial.exe");

    await resetReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
    while (cleanup.PendingCount != 0 && DateTime.UtcNow < deadline)
    {
      await Task.Delay(25);
    }

    Assert.Equal(0, cleanup.PendingCount);
  }

  [Fact]
  public async Task DelayedRetry_ReschedulesSamePathEnqueuedDuringScheduledReset()
  {
    const string path = @"C:\staging\same-artifact.exe";
    var fileSystem = new RecordingCleanupFileSystem
    {
      FailuresRemaining = 3
    };
    var resetReached = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    ArtifactCleanupQueue? cleanup = null;
    var hookCalls = 0;
    cleanup = new ArtifactCleanupQueue(
        fileSystem,
        maxAttempts: 1,
        retryDelay: TimeSpan.FromMilliseconds(25),
        maxDelayedRetryRounds: 1,
        beforeRetryScheduledReset: () =>
        {
          if (Interlocked.Increment(ref hookCalls) != 1)
          {
            return;
          }

          cleanup!.DeleteFile(path);
          resetReached.SetResult();
        });
    cleanup.DeleteFile(path);

    await resetReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
    while (cleanup.PendingCount != 0 && DateTime.UtcNow < deadline)
    {
      await Task.Delay(25);
    }

    Assert.Equal(0, cleanup.PendingCount);
  }

  private static string CreateOwnedArtifactDirectory(string root, string name)
  {
    var directory = Path.Combine(root, name);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, ".wdem-artifact"), "wdem-artifact-v1\n");
    File.WriteAllText(Path.Combine(directory, ".wdem-lease"), string.Empty);
    return directory;
  }

  private static void CreateJunction(string path, string target)
  {
    var startInfo = new ProcessStartInfo("cmd.exe")
    {
      RedirectStandardError = true,
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("/d");
    startInfo.ArgumentList.Add("/c");
    startInfo.ArgumentList.Add("mklink");
    startInfo.ArgumentList.Add("/J");
    startInfo.ArgumentList.Add(path);
    startInfo.ArgumentList.Add(target);
    using var process = Process.Start(startInfo)!;
    process.WaitForExit();
    Assert.Equal(0, process.ExitCode);
  }

  private sealed class RecordingCleanupFileSystem : IArtifactCleanupFileSystem
  {
    public int FailuresRemaining { get; set; }
    public int FileDeleteAttempts { get; private set; }
    public int DirectoryDeleteAttempts { get; private set; }

    public void DeleteFile(string path)
    {
      FileDeleteAttempts++;
      FailIfNeeded();
    }

    public void DeleteDirectory(string path)
    {
      DirectoryDeleteAttempts++;
      FailIfNeeded();
    }

    private void FailIfNeeded()
    {
      if (FailuresRemaining > 0)
      {
        FailuresRemaining--;
        throw new IOException("secret-token cleanup failure");
      }
    }
  }
}
