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
    var path = Path.Combine(root, "stale-installer.exe");
    Directory.CreateDirectory(root);
    await File.WriteAllTextAsync(path, "stale");

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
      while (File.Exists(path) && DateTime.UtcNow < deadline)
      {
        await Task.Delay(25);
      }

      Assert.False(File.Exists(path));
    }
    finally
    {
      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }
    }
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
