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
