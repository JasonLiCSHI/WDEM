using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Wdem.Windows.Security;
using Wdem.Windows.VisualStudio;
using Xunit;

namespace Wdem.Windows.Tests.Security;

public sealed class ArtifactCleanupQueueTests
{
  [WindowsFact]
  public void ArtifactLease_AcquireRejectsLeaseReplacedBySymlinkBeforeOpen()
  {
    var root = Path.Combine(Path.GetTempPath(), $"wdem-lease-race-{Guid.NewGuid():N}");
    var directory = Path.Combine(root, "artifact");
    var outsidePath = Path.Combine(root, "outside.txt");
    Directory.CreateDirectory(directory);
    File.WriteAllText(outsidePath, "outside");
    using (ArtifactLease.Create(directory))
    {
    }

    var leasePath = Path.Combine(directory, ArtifactLease.LeaseFileName);
    try
    {
      Assert.ThrowsAny<IOException>(() => ArtifactLease.Acquire(
          directory,
          () =>
          {
            File.Delete(leasePath);
            File.CreateSymbolicLink(leasePath, outsidePath);
          }));
      Assert.Equal("outside", File.ReadAllText(outsidePath));
      using var outside = new FileStream(
          outsidePath,
          FileMode.Open,
          FileAccess.ReadWrite,
          FileShare.None);
    }
    finally
    {
      if (File.Exists(leasePath))
      {
        File.Delete(leasePath);
      }

      if (Directory.Exists(root))
      {
        Directory.Delete(root, recursive: true);
      }
    }
  }

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

  [WindowsFact]
  public void StartupSweep_RetainsExpiredPublishedArtifactWithoutRevocationAuthority()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-plan-sweep-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(root);
    var future = CreateVsixPlanArtifact(root, DateTimeOffset.UtcNow.AddHours(12));
    var expired = CreateVsixPlanArtifact(root, DateTimeOffset.UtcNow.AddHours(-1));
    var invalid = CreateVsixPlanArtifact(root, DateTimeOffset.UtcNow.AddHours(-1));
    var terminal = GetTerminalStatePath(expired);
    File.WriteAllText(Path.Combine(invalid, ".wdem-vsix-owner"), "{\"schemaVersion\":1}");
    foreach (var directory in new[] { future, expired, invalid })
    {
      MakeTreeStale(directory);
    }

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.True(Directory.Exists(future));
      Assert.True(Directory.Exists(expired));
      Assert.True(Directory.Exists(invalid));
      Assert.False(File.Exists(terminal));
      Assert.False(Directory.Exists(terminal));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public void StartupSweep_UsesCurrentSealedExpiryAfterCreatorExtendsMarker()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-plan-issued-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(root);
    var originalExpiry = DateTimeOffset.UtcNow.AddHours(-1);
    var directory = CreateVsixPlanArtifact(root, originalExpiry);
    var terminal = GetTerminalStatePath(directory);
    var extended = File.ReadAllText(Path.Combine(directory, ".wdem-vsix-owner"))
        .Replace(
            $"\"expiresAtUtc\":\"{originalExpiry:O}\"",
            $"\"expiresAtUtc\":\"{DateTimeOffset.UtcNow.AddHours(12):O}\"",
            StringComparison.Ordinal);
    File.WriteAllText(Path.Combine(directory, ".wdem-vsix-owner"), extended);
    MakeTreeStale(directory);

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.True(Directory.Exists(directory));
      Assert.False(File.Exists(terminal));
      Assert.False(Directory.Exists(terminal));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public void StartupSweep_RetainsExpiredArtifactsWithUntrustedTerminalFilesOrDirectories()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-plan-revoke-fail-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(root);
    var terminalFileArtifact = CreateVsixPlanArtifact(
        root,
        DateTimeOffset.UtcNow.AddHours(-1));
    var terminalDirectoryArtifact = CreateVsixPlanArtifact(
        root,
        DateTimeOffset.UtcNow.AddHours(-1));
    var terminalFile = GetTerminalStatePath(terminalFileArtifact);
    var terminalDirectory = GetTerminalStatePath(terminalDirectoryArtifact);
    MakeTreeStale(terminalFileArtifact);
    MakeTreeStale(terminalDirectoryArtifact);
    File.WriteAllText(terminalFile, "claimed");
    Directory.CreateDirectory(terminalDirectory);

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.True(Directory.Exists(terminalFileArtifact));
      Assert.True(Directory.Exists(terminalDirectoryArtifact));
      Assert.True(File.Exists(terminalFile));
      Assert.True(Directory.Exists(terminalDirectory));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public void StartupSweep_PlanArtifactsReclaimsOnlyRecognizedStaleUninitializedLayouts()
  {
    var basePath = Path.Combine(Path.GetTempPath(), $"wdem-plan-uninitialized-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(root);
    var stale = CreateUninitializedPlanArtifact(root, stale: true);
    var active = CreateUninitializedPlanArtifact(root, stale: true);
    var recent = CreateUninitializedPlanArtifact(root, stale: false);
    var unknown = CreateUninitializedPlanArtifact(root, stale: true);
    File.WriteAllText(Path.Combine(unknown, "unexpected"), "unknown");
    Directory.SetLastWriteTimeUtc(unknown, DateTime.UtcNow - TimeSpan.FromHours(2));
    var lease = new FileStream(
        Path.Combine(active, ".wdem-lease"),
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

      Assert.False(Directory.Exists(stale));
      Assert.True(Directory.Exists(active));
      Assert.True(Directory.Exists(recent));
      Assert.True(Directory.Exists(unknown));
    }
    finally
    {
      lease.Dispose();
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [Fact]
  public void StartupSweep_ReclaimsEveryRecognizedStalePlanArtifactCrashShape()
  {
    var basePath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-plan-crash-shapes-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(root);
    var empty = CreatePlanArtifactCrashShape(root);
    var markerOnly = CreatePlanArtifactCrashShape(root, ".wdem-artifact");
    var markerAndLease = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease");
    var partial = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease",
        $".{Guid.NewGuid():N}.partial");
    var completed = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease",
        "extension.vsix");

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.False(Directory.Exists(empty));
      Assert.False(Directory.Exists(markerOnly));
      Assert.False(Directory.Exists(markerAndLease));
      Assert.False(Directory.Exists(partial));
      Assert.False(Directory.Exists(completed));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public void StartupSweep_RetainsUnsafePlanArtifactCrashShapes()
  {
    var basePath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-plan-unsafe-shapes-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    var outside = Path.Combine(basePath, "outside");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(outside);
    var foreign = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease",
        "payload.bin");
    var extra = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease",
        $".{Guid.NewGuid():N}.partial",
        "unexpected");
    var malformedPartial = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease",
        ".not-a-guid.partial");
    var recent = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease",
        $".{Guid.NewGuid():N}.partial");
    Directory.SetLastWriteTimeUtc(recent, DateTime.UtcNow);
    var active = CreatePlanArtifactCrashShape(
        root,
        ".wdem-artifact",
        ".wdem-lease",
        $".{Guid.NewGuid():N}.partial");
    using var activeLease = new FileStream(
        Path.Combine(active, ".wdem-lease"),
        FileMode.Open,
        FileAccess.ReadWrite,
        FileShare.None);
    var redirected = Path.Combine(root, Guid.NewGuid().ToString("N"));
    CreateJunction(redirected, outside);

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.True(Directory.Exists(foreign));
      Assert.True(Directory.Exists(extra));
      Assert.True(Directory.Exists(malformedPartial));
      Assert.True(Directory.Exists(recent));
      Assert.True(Directory.Exists(active));
      Assert.True(Directory.Exists(redirected));
      Assert.True(Directory.Exists(outside));
    }
    finally
    {
      activeLease.Dispose();
      if (Directory.Exists(redirected))
      {
        Directory.Delete(redirected);
      }

      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
      }
    }
  }

  [WindowsFact]
  public void StartupSweep_RetainsUnsafeExpiredPublishedPlanArtifacts()
  {
    var basePath = Path.Combine(
        Path.GetTempPath(),
        $"wdem-plan-unsafe-published-{Guid.NewGuid():N}");
    var root = Path.Combine(basePath, "Wdem", "PlanArtifacts");
    Directory.CreateDirectory(root);
    var expiry = DateTimeOffset.UtcNow.AddHours(-1);
    var recent = CreateVsixPlanArtifact(root, expiry);
    var uppercase = CreateVsixPlanArtifact(
        root,
        expiry,
        Guid.NewGuid().ToString("N").ToUpperInvariant());
    var extra = CreateVsixPlanArtifact(root, expiry);
    File.WriteAllText(Path.Combine(extra, "unexpected"), "foreign");
    MakeTreeStale(uppercase);
    MakeTreeStale(extra);

    try
    {
      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      Assert.True(Directory.Exists(recent));
      Assert.True(Directory.Exists(uppercase));
      Assert.True(Directory.Exists(extra));
    }
    finally
    {
      if (Directory.Exists(basePath))
      {
        Directory.Delete(basePath, recursive: true);
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
  public void StartupSweep_OversizedMarkerIsIgnoredWithoutUnboundedAllocationAndContinues()
  {
    var root = Path.Combine(
        Path.GetTempPath(),
        $"wdem-cleanup-oversized-marker-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var oversized = Path.Combine(root, Guid.NewGuid().ToString("N"));
    var safeCandidate = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(oversized);
    Directory.CreateDirectory(safeCandidate);
    var markerPath = Path.Combine(oversized, ".wdem-artifact");
    using (var marker = new FileStream(
               markerPath,
               FileMode.CreateNew,
               FileAccess.Write,
               FileShare.None))
    {
      marker.Write("wdem-artifact-v1\n"u8);
      marker.SetLength(16L * 1024 * 1024);
    }

    var stale = DateTime.UtcNow - TimeSpan.FromHours(2);
    File.SetLastWriteTimeUtc(markerPath, stale);
    Directory.SetLastWriteTimeUtc(oversized, stale);
    Directory.SetLastWriteTimeUtc(safeCandidate, stale);

    try
    {
      var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

      _ = new ArtifactCleanupQueue(
          maxAttempts: 1,
          retryDelay: TimeSpan.FromSeconds(30),
          maxDelayedRetryRounds: 1,
          knownStagingRoots: [root]);

      var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
      Assert.True(
          allocated < 4L * 1024 * 1024,
          $"Startup sweep allocated {allocated} bytes for an oversized marker.");
      Assert.True(Directory.Exists(oversized));
      Assert.False(Directory.Exists(safeCandidate));
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

  private static string CreateVsixPlanArtifact(
      string root,
      DateTimeOffset expiresAtUtc,
      string? directoryName = null)
  {
    using var identity = WindowsIdentity.GetCurrent();
    var creator = identity.User!;
    var directory = Path.Combine(root, directoryName ?? Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    new DirectoryInfo(directory).SetAccessControl(
        WindowsPlanArtifactDirectoryPolicy.CreateSecurity(
            creator,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
    using (ArtifactLease.Create(directory))
    {
    }

    var artifactPath = Path.Combine(directory, "extension.vsix");
    File.WriteAllText(artifactPath, "staged");
    var marker = new
    {
      schemaVersion = 1,
      resourceId = "extension",
      artifactPath,
      sha256 = new string('A', 64),
      manifestId = "Contoso.Extension",
      manifestVersion = "1.0.0",
      manifestPath = "source!/extension.vsixmanifest",
      visualStudioInstanceId = "17.0_a",
      visualStudioProductId = "Microsoft.VisualStudio.Product.Community",
      visualStudioInstallationVersion = "17.0.0",
      installationTargets = Array.Empty<object>(),
      creatorSid = creator.Value,
      ownershipDirectory = directory,
      ownershipToken = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
      expiresAtUtc,
      bootIdentifier = WindowsVsixPlanArtifactClock.GetBootIdentifier(),
      expiresAtUptimeMilliseconds = Environment.TickCount64 + (long)TimeSpan.FromHours(12).TotalMilliseconds,
      revoked = false,
      consumed = false
    };
    File.WriteAllBytes(
        Path.Combine(directory, ".wdem-vsix-owner"),
        JsonSerializer.SerializeToUtf8Bytes(marker));
    return directory;
  }

  private static string CreateUninitializedPlanArtifact(string root, bool stale)
  {
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, ".wdem-artifact"), "wdem-artifact-v1\n");
    File.WriteAllText(Path.Combine(directory, ".wdem-lease"), string.Empty);
    File.WriteAllText(Path.Combine(directory, "extension.vsix"), "staged");
    if (stale)
    {
      var staleTime = DateTime.UtcNow - TimeSpan.FromHours(2);
      foreach (var path in Directory.EnumerateFiles(directory))
      {
        File.SetLastWriteTimeUtc(path, staleTime);
      }

      Directory.SetLastWriteTimeUtc(directory, staleTime);
    }

    return directory;
  }

  private static string CreatePlanArtifactCrashShape(
      string root,
      params string[] entries)
  {
    var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    foreach (var entry in entries)
    {
      File.WriteAllText(
          Path.Combine(directory, entry),
          string.Equals(entry, ".wdem-artifact", StringComparison.Ordinal)
              ? "wdem-artifact-v1\n"
              : "staged");
    }

    MakeTreeStale(directory);
    return directory;
  }

  private static void MakeTreeStale(string directory)
  {
    var stale = DateTime.UtcNow - TimeSpan.FromHours(2);
    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
    {
      File.SetLastWriteTimeUtc(path, stale);
    }

    Directory.SetLastWriteTimeUtc(directory, stale);
  }

  private static string GetTerminalStatePath(string directory)
  {
    using var marker = JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(directory, ".wdem-vsix-owner")));
    return Path.Combine(
        Path.GetDirectoryName(directory)!,
        $".{Path.GetFileName(directory)}." +
        $"{marker.RootElement.GetProperty("ownershipToken").GetString()}.wdem-vsix-terminal");
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
